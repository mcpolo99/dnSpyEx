/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace dnSpy.Decompiler.MSBuild {
	static class DecompilerArtifactCleaner {
		static readonly Regex TokenComment = new Regex(
			@"^[ \t]*// (?:\([^)]+\) )?Token: 0x[0-9A-Fa-f]+ RID: \d+.*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		static readonly Regex DecompilerAttribute = new Regex(
			@"^[ \t]*\[(CompilerGenerated|DebuggerNonUserCode|DebuggerStepThrough)\]\s*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		const string CompareStringPrefix = "Operators.CompareString(";

		public static void CleanFile(string filePath) {
			var original = File.ReadAllText(filePath, Encoding.UTF8);
			var result = original;

			result = TokenComment.Replace(result, "");
			result = DecompilerAttribute.Replace(result, "");
			result = ReplaceCompareString(result);

			if (!string.Equals(original, result, StringComparison.Ordinal))
				File.WriteAllText(filePath, result, new UTF8Encoding(true));
		}

		static string ReplaceCompareString(string text) {
			var sb = new StringBuilder(text.Length);
			int pos = 0;
			while (pos < text.Length) {
				int idx = text.IndexOf(CompareStringPrefix, pos, StringComparison.Ordinal);
				if (idx < 0) {
					sb.Append(text, pos, text.Length - pos);
					break;
				}

				sb.Append(text, pos, idx - pos);

				int argsStart = idx + CompareStringPrefix.Length;
				if (!TryParseCompareString(text, argsStart, out var arg1, out var arg2, out var textCompare, out int closePos)) {
					sb.Append(CompareStringPrefix);
					pos = argsStart;
					continue;
				}

				// Check what follows the closing paren: == 0, != 0
				int afterClose = closePos + 1;
				int ws = afterClose;
				while (ws < text.Length && (text[ws] == ' ' || text[ws] == '\t'))
					ws++;

				string? op = null;
				int endPos = ws;
				if (ws + 4 <= text.Length && text.Substring(ws, 4) == "== 0")
					{ op = "=="; endPos = ws + 4; }
				else if (ws + 4 <= text.Length && text.Substring(ws, 4) == "!= 0")
					{ op = "!="; endPos = ws + 4; }

				if (op is null) {
					sb.Append(CompareStringPrefix);
					pos = argsStart;
					continue;
				}

				string comparison = textCompare
					? "StringComparison.OrdinalIgnoreCase"
					: "StringComparison.Ordinal";

				if (op == "==")
					sb.Append($"string.Equals({arg1}, {arg2}, {comparison})");
				else
					sb.Append($"!string.Equals({arg1}, {arg2}, {comparison})");

				pos = endPos;
			}
			return sb.ToString();
		}

		static bool TryParseCompareString(string text, int start, out string arg1, out string arg2, out bool textCompare, out int closePos) {
			arg1 = arg2 = "";
			textCompare = false;
			closePos = 0;

			// Parse first argument (balanced parens until comma at depth 0)
			if (!TryReadArg(text, start, out arg1, out int afterArg1))
				return false;

			// Skip ", "
			if (afterArg1 >= text.Length || text[afterArg1] != ',')
				return false;
			int next = afterArg1 + 1;
			while (next < text.Length && text[next] == ' ')
				next++;

			// Parse second argument
			if (!TryReadArg(text, next, out arg2, out int afterArg2))
				return false;

			// Skip ", "
			if (afterArg2 >= text.Length || text[afterArg2] != ',')
				return false;
			next = afterArg2 + 1;
			while (next < text.Length && text[next] == ' ')
				next++;

			// Parse third argument: "false" or "true"
			if (next + 5 <= text.Length && text.Substring(next, 5) == "false") {
				textCompare = false;
				next += 5;
			}
			else if (next + 4 <= text.Length && text.Substring(next, 4) == "true") {
				textCompare = true;
				next += 4;
			}
			else
				return false;

			// Expect closing paren
			if (next >= text.Length || text[next] != ')')
				return false;

			closePos = next;
			return true;
		}

		static bool TryReadArg(string text, int start, out string arg, out int endPos) {
			arg = "";
			endPos = start;
			int depth = 0;
			int i = start;
			while (i < text.Length) {
				char c = text[i];
				if (c == '(') {
					depth++;
				}
				else if (c == ')') {
					if (depth == 0)
						break;
					depth--;
				}
				else if (c == ',' && depth == 0) {
					break;
				}
				i++;
			}
			if (i == start)
				return false;
			arg = text.Substring(start, i - start).Trim();
			endPos = i;
			return arg.Length > 0;
		}
	}
}
