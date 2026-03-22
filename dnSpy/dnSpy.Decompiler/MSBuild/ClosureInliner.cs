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
using System.Text;
using System.Text.RegularExpressions;

namespace dnSpy.Decompiler.MSBuild {
	static class ClosureInliner {
		const string ClosureMarker = "_Closure$__";
		const string VBLocalPrefix = "$VB$Local_";
		const string CSLocalsPrefix = "CS$<>8__locals";

		// Pattern 1: Cached delegate null-check ternary
		// Matches: (FIELD == null) ? (FIELD =
		// Where FIELD is either ClassName._Closure$__.$I[N]-[N] or CS$<>8__locals[N].$I[N]
		static readonly Regex CachedDelegateTernary = new Regex(
			@"\((" +
				@"(?:\w+(?:\.\w+)*\._Closure\$__\.\$I[\w-]+)" +  // ClassName._Closure$__.$I[N]-[N]
				@"|" +
				@"(?:CS\$<>8__locals\d+\.\$I[\w-]+)" +            // CS$<>8__locals[N].$I[N]
			@")\s*==\s*null\)\s*\?\s*\(\1\s*=\s*",
			RegexOptions.Compiled);

		// Pattern 2: Captured variable references (order matters — nested first)
		static readonly Regex CapturedVarNested = new Regex(
			@"CS\$<>8__locals\d+\.\$VB\$NonLocal_\$VB\$Closure_\d+\.\$VB\$Local_(\w+)",
			RegexOptions.Compiled);

		static readonly Regex CapturedVarDirect = new Regex(
			@"CS\$<>8__locals\d+\.\$VB\$Local_(\w+)",
			RegexOptions.Compiled);

		static readonly Regex CapturedVarThisNested = new Regex(
			@"this\.\$VB\$NonLocal_\$VB\$Closure_\d+\.\$VB\$Local_(\w+)",
			RegexOptions.Compiled);

		static readonly Regex CapturedVarThis = new Regex(
			@"this\.\$VB\$Local_(\w+)",
			RegexOptions.Compiled);

		// Bare $VB$Local_ as a variable/field name (not prefixed by this. or CS$<>8__locals)
		static readonly Regex BareVBLocal = new Regex(
			@"\$VB\$Local_(\w+)",
			RegexOptions.Compiled);

		// Lines to remove entirely
		static readonly Regex ClosureInstantiation = new Regex(
			@"^[ \t]*\S+\._Closure\$__\S*\s+CS\$<>8__locals\d+\s*=\s*new\s+\S+\._Closure\$__\S*\(CS\$<>8__locals\d+\);\s*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		static readonly Regex CrossRefAssignment = new Regex(
			@"^[ \t]*CS\$<>8__locals\d+\.\$VB\$NonLocal_\$VB\$Closure_\d+\s*=\s*CS\$<>8__locals\d+;\s*\r?\n",
			RegexOptions.Multiline | RegexOptions.Compiled);

		// Closure class declaration
		static readonly Regex ClosureClassDecl = new Regex(
			@"internal\s+sealed\s+class\s+(_Closure\$__\S*)",
			RegexOptions.Compiled);

		// Multiple blank lines cleanup
		static readonly Regex MultipleBlankLines = new Regex(
			@"(\r?\n){3,}",
			RegexOptions.Compiled);

		public static string InlineAll(string text) {
			bool hasClosure = text.IndexOf(ClosureMarker, StringComparison.Ordinal) >= 0;
			bool hasVBLocal = text.IndexOf(VBLocalPrefix, StringComparison.Ordinal) >= 0;
			bool hasCSLocals = text.IndexOf(CSLocalsPrefix, StringComparison.Ordinal) >= 0;

			if (!hasClosure && !hasVBLocal && !hasCSLocals)
				return text;

			text = InlineCachedDelegates(text);
			text = InlineCapturedVariables(text);
			text = RemoveClosureClasses(text);
			return text;
		}

		/// <summary>
		/// Pattern 1: Inline cached delegate ternary patterns.
		/// Handles both stateless (_Closure$__.$I) and instance (CS$&lt;&gt;8__locals.$I) forms.
		/// </summary>
		static string InlineCachedDelegates(string text) {
			var sb = new StringBuilder(text.Length);
			int pos = 0;

			while (pos < text.Length) {
				var match = CachedDelegateTernary.Match(text, pos);
				if (!match.Success) {
					sb.Append(text, pos, text.Length - pos);
					break;
				}

				string fieldName = match.Groups[1].Value;
				int patternStart = match.Index;
				int lambdaStart = match.Index + match.Length;

				// Skip optional [SpecialName] attribute before the lambda
				if (lambdaStart + 15 <= text.Length &&
					text.Substring(lambdaStart, 15) == "[SpecialName] (")
					lambdaStart += 14;

				// Extract the lambda body using paren-balanced reading
				// We're inside (FIELD = LAMBDA), so find the unmatched ) that closes the assignment
				if (!TryReadBalancedContent(text, lambdaStart, out string lambda, out int afterLambda)) {
					sb.Append(text, pos, match.Index + match.Length - pos);
					pos = match.Index + match.Length;
					continue;
				}

				// After the assignment close ), expect whitespace then ": FIELD"
				int restPos = afterLambda;
				while (restPos < text.Length && (text[restPos] == ' ' || text[restPos] == '\t'
					|| text[restPos] == '\r' || text[restPos] == '\n'))
					restPos++;

				string colonField = $": {fieldName}";
				if (restPos + colonField.Length <= text.Length &&
					text.Substring(restPos, colonField.Length) == colonField) {
					int ternaryEnd = restPos + colonField.Length;

					// Copy everything before the ternary, then insert just the lambda
					sb.Append(text, pos, patternStart - pos);
					sb.Append(lambda);
					pos = ternaryEnd;
				}
				else {
					// Pattern didn't fully match — skip past this match
					sb.Append(text, pos, match.Index + match.Length - pos);
					pos = match.Index + match.Length;
				}
			}

			return sb.ToString();
		}

		/// <summary>
		/// Read balanced content starting at position, tracking parens.
		/// Reads until the unmatched ) at depth 0 that closes the enclosing expression.
		/// </summary>
		static bool TryReadBalancedContent(string text, int start, out string content, out int afterClose) {
			content = "";
			afterClose = start;

			int depth = 0;
			int i = start;
			bool inString = false;
			bool inChar = false;
			bool escaped = false;

			while (i < text.Length) {
				char c = text[i];

				if (escaped) {
					escaped = false;
					i++;
					continue;
				}

				if (c == '\\' && (inString || inChar)) {
					escaped = true;
					i++;
					continue;
				}

				if (c == '"' && !inChar)
					inString = !inString;
				else if (c == '\'' && !inString)
					inChar = !inChar;
				else if (!inString && !inChar) {
					if (c == '(')
						depth++;
					else if (c == ')') {
						if (depth == 0) {
							content = text.Substring(start, i - start).Trim();
							afterClose = i + 1;
							return true;
						}
						depth--;
					}
				}

				i++;
			}
			return false;
		}

		/// <summary>
		/// Pattern 2: Replace captured variable references with clean local variable names.
		/// Also removes closure instantiation and cross-reference assignment lines.
		/// </summary>
		static string InlineCapturedVariables(string text) {
			if (text.IndexOf(VBLocalPrefix, StringComparison.Ordinal) < 0 &&
				text.IndexOf(CSLocalsPrefix, StringComparison.Ordinal) < 0)
				return text;

			// Order matters: nested references first, then direct
			text = CapturedVarNested.Replace(text, "$1");
			text = CapturedVarDirect.Replace(text, "$1");
			text = CapturedVarThisNested.Replace(text, "$1");
			text = CapturedVarThis.Replace(text, "$1");

			// Replace any remaining bare $VB$Local_ prefixes (used as variable/field names)
			text = BareVBLocal.Replace(text, "$1");

			// Remove closure instantiation lines
			text = ClosureInstantiation.Replace(text, "");

			// Remove cross-reference assignment lines
			text = CrossRefAssignment.Replace(text, "");

			return text;
		}

		/// <summary>
		/// Pattern 3: Remove unreferenced _Closure$__ class definitions.
		/// First collects all closure class block ranges, then checks references
		/// in non-closure text only (so multiple _Closure$__ classes in the same
		/// file don't prevent each other from being removed).
		/// </summary>
		static string RemoveClosureClasses(string text) {
			if (text.IndexOf(ClosureMarker, StringComparison.Ordinal) < 0)
				return text;

			// Collect all closure class block ranges
			var blocks = new System.Collections.Generic.List<(int start, int end, string className)>();
			var match = ClosureClassDecl.Match(text);
			while (match.Success) {
				string className = match.Groups[1].Value;
				int classLineStart = FindLineStart(text, match.Index);

				int blockStart = classLineStart;
				int prevLineStart = FindPrevLineStart(text, classLineStart);
				if (prevLineStart >= 0) {
					string prevLine = text.Substring(prevLineStart, classLineStart - prevLineStart).Trim();
					if (prevLine == "[Serializable]")
						blockStart = prevLineStart;
				}

				int openBrace = text.IndexOf('{', match.Index + match.Length);
				if (openBrace >= 0) {
					int closeBrace = FindMatchingBrace(text, openBrace);
					if (closeBrace >= 0) {
						int blockEnd = closeBrace + 1;
						while (blockEnd < text.Length && (text[blockEnd] == '\r' || text[blockEnd] == '\n'))
							blockEnd++;
						blocks.Add((blockStart, blockEnd, className));
					}
				}

				match = match.NextMatch();
			}

			if (blocks.Count == 0)
				return text;

			// Build text excluding ALL closure class blocks
			var nonClosureText = new StringBuilder(text.Length);
			int pos = 0;
			foreach (var block in blocks) {
				if (block.start > pos)
					nonClosureText.Append(text, pos, block.start - pos);
				pos = block.end;
			}
			if (pos < text.Length)
				nonClosureText.Append(text, pos, text.Length - pos);
			string outsideText = nonClosureText.ToString();

			// Remove blocks in reverse order (so positions stay valid)
			for (int i = blocks.Count - 1; i >= 0; i--) {
				var block = blocks[i];
				if (outsideText.IndexOf(block.className, StringComparison.Ordinal) < 0) {
					text = text.Substring(0, block.start) +
						(block.end < text.Length ? text.Substring(block.end) : "");
				}
			}

			// Clean up multiple blank lines
			text = MultipleBlankLines.Replace(text, "\n\n");

			return text;
		}

		static int FindLineStart(string text, int pos) {
			int i = pos - 1;
			while (i >= 0 && text[i] != '\n')
				i--;
			return i + 1;
		}

		static int FindPrevLineStart(string text, int lineStart) {
			if (lineStart <= 0)
				return -1;
			int i = lineStart - 1;
			if (i >= 0 && text[i] == '\n')
				i--;
			if (i >= 0 && text[i] == '\r')
				i--;
			while (i >= 0 && text[i] != '\n')
				i--;
			return i + 1;
		}

		static int FindMatchingBrace(string text, int openBracePos) {
			int depth = 0;
			bool inString = false;
			bool inChar = false;
			bool escaped = false;

			for (int i = openBracePos; i < text.Length; i++) {
				char c = text[i];

				if (escaped) {
					escaped = false;
					continue;
				}

				if (c == '\\' && (inString || inChar)) {
					escaped = true;
					continue;
				}

				if (c == '"' && !inChar)
					inString = !inString;
				else if (c == '\'' && !inString)
					inChar = !inChar;
				else if (!inString && !inChar) {
					if (c == '{')
						depth++;
					else if (c == '}') {
						depth--;
						if (depth == 0)
							return i;
					}
				}
			}
			return -1;
		}
	}
}
