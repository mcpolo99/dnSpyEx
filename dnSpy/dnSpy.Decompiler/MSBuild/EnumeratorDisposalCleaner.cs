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
	static class EnumeratorDisposalCleaner {
		const string FinallyKeyword = "finally";
		const string ForeachObjectPrefix = "foreach (object ";

		// Matches enumerator variable declaration: any type ending with Enumerator or IEnumerator variants
		static readonly Regex EnumeratorDecl = new Regex(
			@"^\s*(?:[\w<>,.\[\]\s]+(?:Enumerator|IEnumerator)(?:<[^>]+>)?(?:\.[\w]+)*)\s+(\w+)\s*;\s*$",
			RegexOptions.Compiled);

		// Multiple blank lines cleanup
		static readonly Regex MultipleBlankLines = new Regex(
			@"(\r?\n){3,}",
			RegexOptions.Compiled);

		public static string CleanAll(string text) {
			if (text.IndexOf(FinallyKeyword, StringComparison.Ordinal) < 0)
				return text;

			text = RemoveDeadFinallyBlocks(text);
			text = InlineForeachCast(text);
			return text;
		}

		/// <summary>
		/// Remove finally blocks that contain only unassigned enumerator disposal patterns.
		/// Then unwrap orphaned try blocks.
		/// </summary>
		static string RemoveDeadFinallyBlocks(string text) {
			bool changed = true;
			while (changed) {
				changed = false;
				int searchFrom = 0;

				while (searchFrom < text.Length) {
					int finallyIdx = text.IndexOf(FinallyKeyword, searchFrom, StringComparison.Ordinal);
					if (finallyIdx < 0)
						break;

					// Verify it's the keyword "finally" (not part of another word)
					if (finallyIdx > 0 && char.IsLetterOrDigit(text[finallyIdx - 1])) {
						searchFrom = finallyIdx + FinallyKeyword.Length;
						continue;
					}
					int afterKeyword = finallyIdx + FinallyKeyword.Length;
					if (afterKeyword < text.Length && char.IsLetterOrDigit(text[afterKeyword])) {
						searchFrom = afterKeyword;
						continue;
					}

					// Find the { after "finally"
					int openBrace = -1;
					for (int i = afterKeyword; i < text.Length; i++) {
						if (text[i] == '{') { openBrace = i; break; }
						if (!char.IsWhiteSpace(text[i])) break;
					}
					if (openBrace < 0) {
						searchFrom = afterKeyword;
						continue;
					}

					int closeBrace = FindMatchingBrace(text, openBrace);
					if (closeBrace < 0) {
						searchFrom = afterKeyword;
						continue;
					}

					// Extract the body between { and }
					string body = text.Substring(openBrace + 1, closeBrace - openBrace - 1);

					// Check if body is a dead enumerator disposal pattern
					if (IsDeadEnumeratorDisposal(body, out string? varName)) {
						// Safety: check that the variable is not assigned in the preceding try block
						if (varName != null && IsEnumeratorAssignedInTryBlock(text, finallyIdx, varName)) {
							searchFrom = closeBrace + 1;
							continue;
						}

						// Remove the finally block: from "finally" line start to after closing brace
						int finallyLineStart = FindLineStart(text, finallyIdx);
						int blockEnd = closeBrace + 1;
						while (blockEnd < text.Length && (text[blockEnd] == '\r' || text[blockEnd] == '\n'))
							blockEnd++;

						text = text.Substring(0, finallyLineStart) +
							(blockEnd < text.Length ? text.Substring(blockEnd) : "");

						// Try to unwrap orphaned try block
						text = TryUnwrapOrphanedTry(text, finallyLineStart);

						changed = true;
						break; // Restart scanning since positions changed
					}

					searchFrom = closeBrace + 1;
				}
			}

			text = MultipleBlankLines.Replace(text, "\n\n");
			return text;
		}

		/// <summary>
		/// Check if the finally body is a dead enumerator disposal pattern.
		/// Returns true if it matches one of the three variants, with the variable name extracted.
		/// </summary>
		static bool IsDeadEnumeratorDisposal(string body, out string? varName) {
			varName = null;
			var lines = body.Split('\n');

			// Collect non-empty trimmed lines
			var nonEmpty = new System.Collections.Generic.List<string>();
			foreach (var line in lines) {
				string trimmed = line.Trim();
				if (trimmed.Length > 0)
					nonEmpty.Add(trimmed);
			}

			if (nonEmpty.Count < 2)
				return false;

			// First line must be an enumerator variable declaration
			string declLine = nonEmpty[0];
			if (!declLine.EndsWith(";"))
				return false;

			// Extract variable name from declaration (last word before ;)
			string withoutSemicolon = declLine.Substring(0, declLine.Length - 1).TrimEnd();
			int lastSpace = withoutSemicolon.LastIndexOf(' ');
			if (lastSpace < 0)
				return false;

			string typePart = withoutSemicolon.Substring(0, lastSpace).Trim();
			varName = withoutSemicolon.Substring(lastSpace + 1).Trim();

			// Type must contain "Enumerator" or "IEnumerator"
			if (!typePart.Contains("Enumerator") && !typePart.Contains("IEnumerator"))
				return false;

			// Variable name must be a simple identifier
			if (varName.Length == 0 || !char.IsLetter(varName[0]))
				return false;

			// Check remaining lines form a disposal pattern
			// Variant A: if (VAR is IDisposable) { (VAR as IDisposable).Dispose(); }
			// Variant B: if (VAR != null) { VAR.Dispose(); }
			// Variant C: ((IDisposable)VAR).Dispose();
			string remaining = string.Join(" ", nonEmpty.GetRange(1, nonEmpty.Count - 1));

			if (remaining.Contains($"{varName} is IDisposable") &&
				remaining.Contains(".Dispose()"))
				return true;

			if (remaining.Contains($"{varName} != null") &&
				remaining.Contains($"{varName}.Dispose()"))
				return true;

			if (remaining.Contains($"((IDisposable){varName}).Dispose()"))
				return true;

			if (remaining.Contains($"(IDisposable){varName}") &&
				remaining.Contains(".Dispose()"))
				return true;

			return false;
		}

		/// <summary>
		/// Check if the enumerator variable is actually assigned in the preceding try block.
		/// If it is, the finally block is legitimate and should not be removed.
		/// </summary>
		static bool IsEnumeratorAssignedInTryBlock(string text, int finallyIdx, string varName) {
			// Walk backwards from finallyIdx to find the closing } of the try block
			int tryCloseIdx = -1;
			for (int i = finallyIdx - 1; i >= 0; i--) {
				if (text[i] == '}') { tryCloseIdx = i; break; }
				if (!char.IsWhiteSpace(text[i])) return true; // Unexpected, be safe
			}
			if (tryCloseIdx < 0)
				return true;

			// Find the matching opening { of the try block
			int tryOpenIdx = FindMatchingBraceReverse(text, tryCloseIdx);
			if (tryOpenIdx < 0)
				return true;

			// Extract try block body
			string tryBody = text.Substring(tryOpenIdx + 1, tryCloseIdx - tryOpenIdx - 1);

			// Check for assignment: "varName =" (but not "varName ==" or "varName !=")
			string assignPattern = varName + " =";
			int idx = 0;
			while ((idx = tryBody.IndexOf(assignPattern, idx, StringComparison.Ordinal)) >= 0) {
				int afterEq = idx + assignPattern.Length;
				if (afterEq < tryBody.Length && tryBody[afterEq] != '=') // Not == or !=
					return true; // Found an assignment, this is legitimate
				idx = afterEq;
			}

			// Also check for var = GetEnumerator() etc.
			if (tryBody.Contains(varName + ".MoveNext"))
				return true;

			return false;
		}

		/// <summary>
		/// After removing a finally block, check if the preceding try has no catch/finally left.
		/// If so, unwrap the try block (remove try { and } wrapper, dedent body by one tab).
		/// </summary>
		static string TryUnwrapOrphanedTry(string text, int whereFinallywas) {
			// Walk backwards from whereFinallywas to find the } that ended the try body
			int tryCloseIdx = -1;
			for (int i = whereFinallywas - 1; i >= 0; i--) {
				if (text[i] == '}') { tryCloseIdx = i; break; }
				if (!char.IsWhiteSpace(text[i])) return text;
			}
			if (tryCloseIdx < 0)
				return text;

			// Find the matching { of the try block
			int tryOpenIdx = FindMatchingBraceReverse(text, tryCloseIdx);
			if (tryOpenIdx < 0)
				return text;

			// Find the "try" keyword before the {
			// The { might be on the same line as "try" or on the next line
			int braceLineStart = FindLineStart(text, tryOpenIdx);
			string braceLine = text.Substring(braceLineStart, tryOpenIdx - braceLineStart).Trim();

			int tryKeywordLine;
			if (braceLine == "") {
				// { is on its own line — try is on the previous line
				tryKeywordLine = FindPrevLineStart(text, braceLineStart);
				if (tryKeywordLine < 0) return text;
				string prevLine = text.Substring(tryKeywordLine, braceLineStart - tryKeywordLine).Trim();
				if (prevLine != "try") return text;
			}
			else if (braceLine == "try") {
				// "try {" on same line
				tryKeywordLine = braceLineStart;
			}
			else {
				return text;
			}

			// Check nothing follows the } (no catch, no finally)
			int afterClose = tryCloseIdx + 1;
			while (afterClose < text.Length && (text[afterClose] == '\r' || text[afterClose] == '\n'))
				afterClose++;

			// If there's more text, check it's not a catch/finally
			if (afterClose < text.Length) {
				int nextNonWs = afterClose;
				while (nextNonWs < text.Length && (text[nextNonWs] == ' ' || text[nextNonWs] == '\t'))
					nextNonWs++;
				if (nextNonWs + 5 <= text.Length && text.Substring(nextNonWs, 5) == "catch")
					return text; // Has catch block, keep try
				if (nextNonWs + 7 <= text.Length && text.Substring(nextNonWs, 7) == "finally")
					return text; // Has another finally, keep try
			}

			// Remove "try" line and opening brace line, remove closing brace line
			// Keep the body content as-is (with original indentation)

			// Find the end of the "try\n{" block (the line after the opening brace)
			int afterOpenBrace = tryOpenIdx + 1;
			while (afterOpenBrace < text.Length && (text[afterOpenBrace] == '\r' || text[afterOpenBrace] == '\n'))
				afterOpenBrace++;

			// Find the start of the closing brace line
			int closeLineStart = FindLineStart(text, tryCloseIdx);

			// Include line ending after closing brace
			int afterCloseBrace = tryCloseIdx + 1;
			while (afterCloseBrace < text.Length && (text[afterCloseBrace] == '\r' || text[afterCloseBrace] == '\n'))
				afterCloseBrace++;

			// Build result: before try + body (between { and }) + after }
			string before = text.Substring(0, tryKeywordLine);
			string body = text.Substring(afterOpenBrace, closeLineStart - afterOpenBrace);
			string after = afterCloseBrace <= text.Length ? text.Substring(afterCloseBrace) : "";

			return before + body + after;
		}

		/// <summary>
		/// Inline foreach cast: foreach (object obj in X) { T var = (T)obj; ... }
		/// becomes: foreach (T var in X) { ... }
		/// </summary>
		static string InlineForeachCast(string text) {
			if (text.IndexOf(ForeachObjectPrefix, StringComparison.Ordinal) < 0)
				return text;

			var sb = new StringBuilder(text.Length);
			int pos = 0;

			while (pos < text.Length) {
				int foreachIdx = text.IndexOf(ForeachObjectPrefix, pos, StringComparison.Ordinal);
				if (foreachIdx < 0) {
					sb.Append(text, pos, text.Length - pos);
					break;
				}

				// Extract: foreach (object VARNAME in COLLECTION)
				int varStart = foreachIdx + ForeachObjectPrefix.Length;
				int inIdx = text.IndexOf(" in ", varStart, StringComparison.Ordinal);
				if (inIdx < 0) {
					sb.Append(text, pos, varStart - pos);
					pos = varStart;
					continue;
				}

				string objVar = text.Substring(varStart, inIdx - varStart).Trim();

				// Find the closing ) of the foreach
				int closeParenIdx = -1;
				int depth = 1; // We're inside the ( of foreach (
				for (int i = inIdx + 4; i < text.Length; i++) {
					if (text[i] == '(') depth++;
					else if (text[i] == ')') {
						depth--;
						if (depth == 0) { closeParenIdx = i; break; }
					}
				}
				if (closeParenIdx < 0) {
					sb.Append(text, pos, varStart - pos);
					pos = varStart;
					continue;
				}

				string collection = text.Substring(inIdx + 4, closeParenIdx - (inIdx + 4)).Trim();

				// Find the { of the foreach body
				int bodyOpen = -1;
				for (int i = closeParenIdx + 1; i < text.Length; i++) {
					if (text[i] == '{') { bodyOpen = i; break; }
					if (!char.IsWhiteSpace(text[i])) break;
				}
				if (bodyOpen < 0) {
					sb.Append(text, pos, closeParenIdx + 1 - pos);
					pos = closeParenIdx + 1;
					continue;
				}

				int bodyClose = FindMatchingBrace(text, bodyOpen);
				if (bodyClose < 0) {
					sb.Append(text, pos, bodyOpen + 1 - pos);
					pos = bodyOpen + 1;
					continue;
				}

				string body = text.Substring(bodyOpen + 1, bodyClose - bodyOpen - 1);

				// Find the first non-blank line in the body
				if (!TryFindCastLine(body, objVar, out string? castType, out string? castVar, out int castLineStart, out int castLineEnd)) {
					sb.Append(text, pos, bodyClose + 1 - pos);
					pos = bodyClose + 1;
					continue;
				}

				// Verify objVar is not used elsewhere in the body (after the cast line)
				string bodyAfterCast = body.Substring(castLineEnd);
				if (bodyAfterCast.Contains(objVar)) {
					sb.Append(text, pos, bodyClose + 1 - pos);
					pos = bodyClose + 1;
					continue;
				}

				// Replace: foreach (object objVar in collection)\n{\n  TYPE castVar = (TYPE)objVar;\n  ...
				// With:    foreach (TYPE castVar in collection)\n{\n  ...
				sb.Append(text, pos, foreachIdx - pos);
				sb.Append($"foreach ({castType} {castVar} in {collection})");

				// Preserve whitespace between ) and { from original text
				sb.Append(text, closeParenIdx + 1, bodyOpen - closeParenIdx - 1);

				// Append the body: opening brace, skip the cast line, keep the rest
				string bodyBeforeCast = body.Substring(0, castLineStart);
				string bodyRest = body.Substring(castLineEnd);

				sb.Append('{');
				sb.Append(bodyBeforeCast);
				sb.Append(bodyRest);
				sb.Append('}');

				pos = bodyClose + 1;
			}

			return sb.ToString();
		}

		/// <summary>
		/// Find a cast line like "TYPE VAR = (TYPE)objVar;" as the first non-blank line in body.
		/// </summary>
		static bool TryFindCastLine(string body, string objVar, out string? castType, out string? castVar, out int lineStart, out int lineEnd) {
			castType = null;
			castVar = null;
			lineStart = 0;
			lineEnd = 0;

			// Find first non-blank line
			int i = 0;
			while (i < body.Length && (body[i] == ' ' || body[i] == '\t' || body[i] == '\r' || body[i] == '\n'))
				i++;
			if (i >= body.Length)
				return false;

			lineStart = i;

			// Find end of this line
			int eol = body.IndexOf('\n', i);
			if (eol < 0) eol = body.Length;
			lineEnd = eol + 1;
			if (lineEnd > body.Length) lineEnd = body.Length;

			string firstLine = body.Substring(i, eol - i).Trim();

			// Match pattern: TYPE VAR = (TYPE)objVar;
			string castSuffix = $"({objVar})";
			if (!firstLine.EndsWith(";"))
				return false;

			// Remove trailing ;
			string withoutSemi = firstLine.Substring(0, firstLine.Length - 1).TrimEnd();

			// Find the cast pattern: = (TYPE)objVar
			string castPattern = $")({objVar})";
			// Actually the pattern is: TYPE VAR = (TYPE)objVar
			// Let's find "= (" which starts the cast
			int eqIdx = withoutSemi.IndexOf(" = (", StringComparison.Ordinal);
			if (eqIdx < 0)
				return false;

			// Extract the cast expression: (TYPE)objVar
			string castExpr = withoutSemi.Substring(eqIdx + 3).Trim(); // after "= "
			if (!castExpr.EndsWith(objVar))
				return false;

			// Extract TYPE from (TYPE)objVar
			// castExpr is: (TYPE)objVar
			if (!castExpr.StartsWith("("))
				return false;
			int closeParenCast = castExpr.IndexOf(')');
			if (closeParenCast < 0)
				return false;

			castType = castExpr.Substring(1, closeParenCast - 1).Trim();

			// Verify the rest after ) is exactly objVar
			string afterParen = castExpr.Substring(closeParenCast + 1).Trim();
			if (afterParen != objVar)
				return false;

			// Extract TYPE VAR from before the =
			string declPart = withoutSemi.Substring(0, eqIdx).Trim();
			int lastSpaceDecl = declPart.LastIndexOf(' ');
			if (lastSpaceDecl < 0)
				return false;

			string declType = declPart.Substring(0, lastSpaceDecl).Trim();
			castVar = declPart.Substring(lastSpaceDecl + 1).Trim();

			// Verify the cast type matches the declared type
			if (declType != castType)
				return false;

			return true;
		}

		static int FindLineStart(string text, int pos) {
			int i = pos - 1;
			while (i >= 0 && text[i] != '\n')
				i--;
			return i + 1;
		}

		static int FindPrevLineStart(string text, int lineStart) {
			if (lineStart <= 0) return -1;
			int i = lineStart - 1;
			if (i >= 0 && text[i] == '\n') i--;
			if (i >= 0 && text[i] == '\r') i--;
			while (i >= 0 && text[i] != '\n') i--;
			return i + 1;
		}

		static int FindMatchingBrace(string text, int openBracePos) {
			int depth = 0;
			bool inString = false;
			bool inChar = false;
			bool escaped = false;

			for (int i = openBracePos; i < text.Length; i++) {
				char c = text[i];
				if (escaped) { escaped = false; continue; }
				if (c == '\\' && (inString || inChar)) { escaped = true; continue; }
				if (c == '"' && !inChar) inString = !inString;
				else if (c == '\'' && !inString) inChar = !inChar;
				else if (!inString && !inChar) {
					if (c == '{') depth++;
					else if (c == '}') { depth--; if (depth == 0) return i; }
				}
			}
			return -1;
		}

		/// <summary>
		/// Find the matching opening { for a closing } by scanning backwards.
		/// </summary>
		static int FindMatchingBraceReverse(string text, int closeBracePos) {
			int depth = 0;
			for (int i = closeBracePos; i >= 0; i--) {
				char c = text[i];
				if (c == '}') depth++;
				else if (c == '{') {
					depth--;
					if (depth == 0) return i;
				}
			}
			return -1;
		}
	}
}
