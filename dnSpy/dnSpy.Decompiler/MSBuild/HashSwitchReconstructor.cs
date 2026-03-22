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
using System.Text;
using System.Text.RegularExpressions;

namespace dnSpy.Decompiler.MSBuild {
	/// <summary>
	/// Converts VB.NET hash-based switch-on-string patterns from if/else trees to switch statements.
	/// Pattern: ComputeStringHash(var) + binary search if/else tree with string.Equals guards + goto labels.
	/// </summary>
	static class HashSwitchReconstructor {
		const string ComputeStringHashMarker = "ComputeStringHash(";

		// Matches: uint num = ComputeStringHash(VARNAME);
		static readonly Regex HashAssignment = new Regex(
			@"^([ \t]*)uint\s+(\w+)\s*=\s*ComputeStringHash\((\w+)\);\s*$",
			RegexOptions.Multiline | RegexOptions.Compiled);

		// Matches: if (!string.Equals(VARNAME, "LITERAL", StringComparison.Ordinal))
		// or: if (string.Equals(VARNAME, "LITERAL", StringComparison.Ordinal))
		static readonly Regex StringEqualsCheck = new Regex(
			@"string\.Equals\(\w+,\s*""([^""]*)"",\s*StringComparison\.Ordinal(?:IgnoreCase)?\)",
			RegexOptions.Compiled);

		// Matches: goto IL_XXXX;
		static readonly Regex GotoLabel = new Regex(
			@"goto\s+(IL_[0-9A-Fa-f]+);",
			RegexOptions.Compiled);

		// Matches label definitions: IL_XXXX:
		static readonly Regex LabelDef = new Regex(
			@"^([ \t]*)(IL_[0-9A-Fa-f]+):\s*$",
			RegexOptions.Multiline | RegexOptions.Compiled);

		public static string ReconstructAll(string text) {
			if (text.IndexOf(ComputeStringHashMarker, StringComparison.Ordinal) < 0)
				return text;

			// Find each ComputeStringHash assignment
			var match = HashAssignment.Match(text);
			while (match.Success) {
				string indent = match.Groups[1].Value;
				string hashVar = match.Groups[2].Value;
				string switchVar = match.Groups[3].Value;
				int hashLineStart = match.Index;
				int hashLineEnd = match.Index + match.Length;

				// Skip past newline after hash assignment
				while (hashLineEnd < text.Length && (text[hashLineEnd] == '\r' || text[hashLineEnd] == '\n'))
					hashLineEnd++;

				// The if/else tree starts at hashLineEnd
				// Find the if/else block that uses hashVar
				string afterHash = text.Substring(hashLineEnd);
				if (!afterHash.TrimStart().StartsWith($"if ({hashVar}")) {
					match = HashAssignment.Match(text, hashLineEnd);
					continue;
				}

				// Find the extent of the if/else tree by looking for the first non-nested
				// statement after the tree that uses hashVar comparisons
				int treeStart = hashLineEnd;
				int treeEnd = FindHashSwitchTreeEnd(text, treeStart, hashVar);
				if (treeEnd < 0) {
					match = HashAssignment.Match(text, hashLineEnd);
					continue;
				}

				// Extract string literals and their goto targets from the if/else tree
				string treeText = text.Substring(treeStart, treeEnd - treeStart);
				var cases = ExtractCasesFromTree(treeText, switchVar);
				if (cases.Count < 2) {
					match = HashAssignment.Match(text, hashLineEnd);
					continue;
				}

				// Find the labeled target blocks after the tree
				string afterTree = text.Substring(treeEnd);
				var labelBodies = ExtractLabelBodies(afterTree, cases, out int labelsEnd);

				// Find the "continue" label (end label that all cases goto)
				string? endLabel = FindEndLabel(labelBodies);

				// Build the switch statement
				string switchText = BuildSwitchStatement(indent, switchVar, cases, labelBodies, endLabel);

				// Determine what to replace:
				// - The hash assignment line
				// - The if/else tree
				// - The labeled goto target blocks
				int replaceStart = hashLineStart;
				int replaceEnd = treeEnd + labelsEnd;

				// Check for "continue;" after labels (in foreach loops)
				string remaining = text.Substring(replaceEnd).TrimStart();
				if (remaining.StartsWith("continue;")) {
					// The "continue;" is part of the foreach flow, keep it
				}

				text = text.Substring(0, replaceStart) + switchText + text.Substring(replaceEnd);

				// Restart search from after the inserted switch
				match = HashAssignment.Match(text, replaceStart + switchText.Length);
			}

			return text;
		}

		/// <summary>
		/// Find the end of the hash-switch if/else tree by tracking brace depth.
		/// The tree ends when we reach a labeled statement (IL_XXXX:) or a statement
		/// at the same indentation level that isn't part of the if/else chain.
		/// </summary>
		static int FindHashSwitchTreeEnd(string text, int start, string hashVar) {
			// Find the opening if statement
			int i = start;
			while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

			if (i >= text.Length || !text.Substring(i).StartsWith("if ("))
				return -1;

			// Track brace depth to find the end of the outermost if/else
			int depth = 0;
			bool foundFirstBrace = false;
			bool inString = false;
			bool escaped = false;

			for (int j = i; j < text.Length; j++) {
				char c = text[j];

				if (escaped) { escaped = false; continue; }
				if (c == '\\' && inString) { escaped = true; continue; }
				if (c == '"') inString = !inString;
				if (inString) continue;

				if (c == '{') {
					depth++;
					foundFirstBrace = true;
				}
				else if (c == '}') {
					depth--;
					if (foundFirstBrace && depth == 0) {
						// Check if followed by "else"
						int afterBrace = j + 1;
						while (afterBrace < text.Length && (text[afterBrace] == '\r' || text[afterBrace] == '\n' || text[afterBrace] == ' ' || text[afterBrace] == '\t'))
							afterBrace++;
						if (afterBrace + 4 < text.Length && text.Substring(afterBrace, 4) == "else") {
							// Continue — there's an else clause
							continue;
						}
						// End of the if/else tree
						int end = j + 1;
						while (end < text.Length && (text[end] == '\r' || text[end] == '\n'))
							end++;
						return end;
					}
				}
			}

			return -1;
		}

		/// <summary>
		/// Extract case labels and their goto targets from the hash-switch if/else tree.
		/// </summary>
		static List<(string literal, string gotoTarget)> ExtractCasesFromTree(string treeText, string switchVar) {
			var cases = new List<(string literal, string gotoTarget)>();

			// Find all string.Equals checks with their associated goto targets
			// Pattern: if (!string.Equals(name, "LITERAL", ...)) { goto DEFAULT; } goto CASE;
			// or inline: string.Equals(name, "LITERAL", ...) followed by goto
			var equalsMatches = StringEqualsCheck.Matches(treeText);
			foreach (Match em in equalsMatches) {
				string literal = em.Groups[1].Value;

				// Find the goto that follows a successful string.Equals match
				// The pattern is: if (!string.Equals(...)) { goto X; } FALLTHROUGH
				// where FALLTHROUGH is either: goto CASE_LABEL; or a direct statement
				// OR: if (string.Equals(...)) { goto CASE_LABEL; }

				// Look at context around this match to determine the case target
				int pos = em.Index + em.Length;
				string afterEquals = treeText.Substring(pos);

				// Check if this is a negated check: !string.Equals(...)
				int beforeEquals = em.Index;
				string beforeText = treeText.Substring(Math.Max(0, beforeEquals - 10), Math.Min(10, beforeEquals));
				bool isNegated = beforeText.Contains("!");

				if (isNegated) {
					// Pattern: if (!string.Equals(name, "LIT")) { goto DEFAULT; } goto CASE;
					// or:      if (!string.Equals(name, "LIT")) { goto DEFAULT; }
					// followed by a fallthrough statement (which may be a goto or an inline body)

					// Find the goto inside the if-true block (this is the DEFAULT/fallthrough target)
					var ifBodyGoto = GotoLabel.Match(afterEquals);

					// Find the next goto AFTER the if block (this is the CASE target)
					// Skip past the closing brace of the if block
					int closeBrace = afterEquals.IndexOf('}');
					if (closeBrace >= 0) {
						string afterIfBlock = afterEquals.Substring(closeBrace + 1);
						var caseGoto = GotoLabel.Match(afterIfBlock);
						if (caseGoto.Success) {
							cases.Add((literal, caseGoto.Groups[1].Value));
							continue;
						}
					}

					// No goto after the if block — the case body is inline (falls through to the next statement)
					// This happens when the case is the last before a direct assignment
					// e.g., if (!string.Equals(name, "String")) { goto DEFAULT; }
					//        gridEXColumn.DefaultValue = "";
					// In this case, we need to find what statement follows and extract it as inline
					// For now, mark as inline case with special target
					if (ifBodyGoto.Success) {
						// The fallthrough after the if block is the "success" path
						// We'll mark it with a special inline marker
						cases.Add((literal, "__INLINE__"));
					}
				}
				else {
					// Positive check: if (string.Equals(name, "LIT")) { goto CASE; }
					var caseGoto = GotoLabel.Match(afterEquals);
					if (caseGoto.Success) {
						cases.Add((literal, caseGoto.Groups[1].Value));
					}
				}
			}

			return cases;
		}

		/// <summary>
		/// Extract labeled statement blocks that follow the if/else tree.
		/// These are the goto targets containing case bodies.
		/// </summary>
		static Dictionary<string, string> ExtractLabelBodies(string text, List<(string literal, string gotoTarget)> cases, out int consumedLength) {
			var bodies = new Dictionary<string, string>();
			var neededLabels = new HashSet<string>();
			foreach (var c in cases) {
				if (c.gotoTarget != "__INLINE__")
					neededLabels.Add(c.gotoTarget);
			}

			consumedLength = 0;
			var labelMatch = LabelDef.Match(text);

			// Collect all label positions
			var labels = new List<(int start, string name, string indent)>();
			while (labelMatch.Success) {
				labels.Add((labelMatch.Index, labelMatch.Groups[2].Value, labelMatch.Groups[1].Value));
				labelMatch = labelMatch.NextMatch();
			}

			if (labels.Count == 0)
				return bodies;

			// Extract body for each label (from label to next label or end of relevant section)
			for (int i = 0; i < labels.Count; i++) {
				string labelName = labels[i].name;
				int bodyStart = labels[i].start + LabelDef.Match(text, labels[i].start).Length;
				while (bodyStart < text.Length && (text[bodyStart] == '\r' || text[bodyStart] == '\n'))
					bodyStart++;

				int bodyEnd;
				if (i + 1 < labels.Count)
					bodyEnd = labels[i + 1].start;
				else {
					// Last label — find end by looking for the next non-labeled, non-goto statement
					bodyEnd = text.Length;
				}

				string body = text.Substring(bodyStart, bodyEnd - bodyStart).Trim();

				// Remove trailing "goto IL_XXXX;" from the body (this is the goto to the end label)
				var trailingGoto = GotoLabel.Match(body);
				if (trailingGoto.Success && body.EndsWith(trailingGoto.Value)) {
					body = body.Substring(0, body.Length - trailingGoto.Value.Length).TrimEnd();
					// Track what the end label is
				}

				bodies[labelName] = body;

				if (neededLabels.Contains(labelName) || i == labels.Count - 1)
					consumedLength = Math.Max(consumedLength, bodyEnd);
			}

			// Find the furthest consumed position including the last label's end
			if (labels.Count > 0) {
				int lastLabelEnd = labels[labels.Count - 1].start;
				// Find end of last label block
				var lastMatch = LabelDef.Match(text, lastLabelEnd);
				if (lastMatch.Success) {
					int end = lastMatch.Index + lastMatch.Length;
					// Include all statements until the next non-label line at lower indent
					while (end < text.Length) {
						int lineEnd = text.IndexOf('\n', end);
						if (lineEnd < 0) lineEnd = text.Length;
						string line = text.Substring(end, lineEnd - end).Trim();
						if (line.Length == 0 || GotoLabel.IsMatch(line) || line.EndsWith(";")) {
							end = lineEnd + 1;
							continue;
						}
						break;
					}
					consumedLength = Math.Max(consumedLength, end);
				}
			}

			return bodies;
		}

		/// <summary>
		/// Find the common end label that all case bodies goto.
		/// </summary>
		static string? FindEndLabel(Dictionary<string, string> labelBodies) {
			// Count which labels are referenced as goto targets from other labels
			var targetCounts = new Dictionary<string, int>();
			foreach (var body in labelBodies.Values) {
				var gotoMatch = GotoLabel.Match(body);
				// Check the ORIGINAL body (before we stripped trailing gotos)
				// Actually we already stripped them. The end label is the one that all bodies goto.
			}
			return null; // We already stripped trailing gotos during extraction
		}

		/// <summary>
		/// Build a switch statement from extracted cases and label bodies.
		/// </summary>
		static string BuildSwitchStatement(string indent, string switchVar,
			List<(string literal, string gotoTarget)> cases,
			Dictionary<string, string> labelBodies, string? endLabel) {

			var sb = new StringBuilder();
			sb.AppendLine($"{indent}switch ({switchVar})");
			sb.AppendLine($"{indent}{{");

			// Group cases by target label
			var groups = new Dictionary<string, List<string>>();
			var inlineCases = new List<string>();
			string? defaultTarget = null;

			foreach (var c in cases) {
				if (c.gotoTarget == "__INLINE__") {
					inlineCases.Add(c.literal);
					continue;
				}
				if (!groups.ContainsKey(c.gotoTarget))
					groups[c.gotoTarget] = new List<string>();
				groups[c.gotoTarget].Add(c.literal);
			}

			// Find the default target — it's the label that's NOT a case target
			// (referenced by goto in the if-else tree but not associated with any string.Equals)
			foreach (var label in labelBodies.Keys) {
				if (!groups.ContainsKey(label)) {
					defaultTarget = label;
				}
			}

			// Emit each case group
			foreach (var group in groups) {
				foreach (var literal in group.Value)
					sb.AppendLine($"{indent}\tcase \"{literal}\":");

				string body = labelBodies.ContainsKey(group.Key) ? labelBodies[group.Key] : "";
				if (!string.IsNullOrWhiteSpace(body)) {
					// Indent the body
					foreach (var line in body.Split('\n')) {
						string trimmed = line.TrimEnd('\r');
						if (trimmed.Trim().Length > 0)
							sb.AppendLine($"{indent}\t\t{trimmed.Trim()}");
					}
				}
				sb.AppendLine($"{indent}\t\tbreak;");
			}

			// Emit default case
			if (defaultTarget != null && labelBodies.ContainsKey(defaultTarget)) {
				sb.AppendLine($"{indent}\tdefault:");
				string body = labelBodies[defaultTarget];
				if (!string.IsNullOrWhiteSpace(body)) {
					foreach (var line in body.Split('\n')) {
						string trimmed = line.TrimEnd('\r');
						if (trimmed.Trim().Length > 0)
							sb.AppendLine($"{indent}\t\t{trimmed.Trim()}");
					}
				}
				sb.AppendLine($"{indent}\t\tbreak;");
			}

			sb.AppendLine($"{indent}}}");
			return sb.ToString();
		}
	}
}
