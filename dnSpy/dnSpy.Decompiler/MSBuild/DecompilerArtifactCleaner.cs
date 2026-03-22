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
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace dnSpy.Decompiler.MSBuild {
	static class DecompilerArtifactCleaner {
		static readonly Regex TokenComment = new Regex(
			@"^[ \t]*// (?:\([^)]+\) )?Token: 0x[0-9A-Fa-f]+ RID: \d+.*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		static readonly Regex SimpleAttribute = new Regex(
			@"^[ \t]*\[(CompilerGenerated|DebuggerNonUserCode|DebuggerStepThrough|DesignerGenerated|DebuggerHidden)\]\s*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		static readonly Regex ParameterizedAttribute = new Regex(
			@"^[ \t]*\[(MethodImpl\(32\)|EditorBrowsable\([^)]+\)|GeneratedCode\([^]]*\))\]\s*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		const string CompareStringPrefix = "Operators.CompareString(";

		static readonly Dictionary<string, string> ConversionsMap = new Dictionary<string, string> {
			{ "Conversions.ToBoolean(", "Convert.ToBoolean(" },
			{ "Conversions.ToInteger(", "Convert.ToInt32(" },
			{ "Conversions.ToLong(", "Convert.ToInt64(" },
			{ "Conversions.ToDecimal(", "Convert.ToDecimal(" },
			{ "Conversions.ToDouble(", "Convert.ToDouble(" },
			{ "Conversions.ToSingle(", "Convert.ToSingle(" },
			{ "Conversions.ToByte(", "Convert.ToByte(" },
			{ "Conversions.ToShort(", "Convert.ToInt16(" },
			{ "Conversions.ToDate(", "Convert.ToDateTime(" },
			{ "Conversions.ToChar(", "Convert.ToChar(" },
			{ "Conversions.ToUInteger(", "Convert.ToUInt32(" },
			{ "Conversions.ToUShort(", "Convert.ToUInt16(" },
			{ "Conversions.ToULong(", "Convert.ToUInt64(" },
		};

		public static void CleanFile(string filePath) {
			// Delete compiler-generated <PrivateImplementationDetails> files entirely
			string fileName = Path.GetFileName(filePath);
			if (fileName.IndexOf("PrivateImplementationDetails", StringComparison.OrdinalIgnoreCase) >= 0) {
				File.Delete(filePath);
				return;
			}

			var original = File.ReadAllText(filePath, Encoding.UTF8);
			var result = original;

			result = TokenComment.Replace(result, "");
			result = SimpleAttribute.Replace(result, "");
			result = ParameterizedAttribute.Replace(result, "");
			result = ReplaceCompareString(result);
			result = ReplaceConversions(result);
			result = ClosureInliner.InlineAll(result);
			result = EnumeratorDisposalCleaner.CleanAll(result);

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

		static string ReplaceConversions(string text) {
			if (text.IndexOf("Conversions.To", StringComparison.Ordinal) < 0)
				return text;

			var sb = new StringBuilder(text.Length);
			int pos = 0;
			while (pos < text.Length) {
				int bestIdx = -1;
				string? bestFrom = null;
				string? bestTo = null;

				foreach (var kv in ConversionsMap) {
					int idx = text.IndexOf(kv.Key, pos, StringComparison.Ordinal);
					if (idx >= 0 && (bestIdx < 0 || idx < bestIdx)) {
						bestIdx = idx;
						bestFrom = kv.Key;
						bestTo = kv.Value;
					}
				}

				if (bestIdx < 0) {
					sb.Append(text, pos, text.Length - pos);
					break;
				}

				sb.Append(text, pos, bestIdx - pos);
				sb.Append(bestTo);
				pos = bestIdx + bestFrom!.Length;
			}
			return sb.ToString();
		}

		static bool TryParseCompareString(string text, int start, out string arg1, out string arg2, out bool textCompare, out int closePos) {
			arg1 = arg2 = "";
			textCompare = false;
			closePos = 0;

			if (!TryReadArg(text, start, out arg1, out int afterArg1))
				return false;

			if (afterArg1 >= text.Length || text[afterArg1] != ',')
				return false;
			int next = afterArg1 + 1;
			while (next < text.Length && text[next] == ' ')
				next++;

			if (!TryReadArg(text, next, out arg2, out int afterArg2))
				return false;

			if (afterArg2 >= text.Length || text[afterArg2] != ',')
				return false;
			next = afterArg2 + 1;
			while (next < text.Length && text[next] == ' ')
				next++;

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
