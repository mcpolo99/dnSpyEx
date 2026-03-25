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
	/// Converts VB.NET hash-based switch-on-string patterns to switch statements.
	/// Uses brace-depth tracking to stay within the containing scope (method/foreach body).
	/// </summary>
	static class HashSwitchReconstructor {
		const string ComputeStringHashMarker = "ComputeStringHash(";

		static readonly Regex HashAssignment = new Regex(
			@"^([ \t]*)uint\s+(\w+)\s*=\s*(?:\S+\.)?ComputeStringHash\((\w+)\);\s*$",
			RegexOptions.Multiline | RegexOptions.Compiled);

		static readonly Regex StringEqualsLiteral = new Regex(
			@"string\.Equals\(\w+,\s*""([^""]*)"",\s*StringComparison\.\w+\)",
			RegexOptions.Compiled);

		static readonly Regex GotoStmt = new Regex(
			@"goto\s+(IL_[0-9A-Fa-f]+);",
			RegexOptions.Compiled);

		static readonly Regex LabelLine = new Regex(
			@"^([ \t]*)(IL_[0-9A-Fa-f]+):\s*$",
			RegexOptions.Multiline | RegexOptions.Compiled);

		public static string ReconstructAll(string text) {
			if (text.IndexOf(ComputeStringHashMarker, StringComparison.Ordinal) < 0)
				return text;

			bool changed = true;
			while (changed) {
				changed = false;
				var match = HashAssignment.Match(text);
				while (match.Success) {
					string indent = match.Groups[1].Value;
					string hashVar = match.Groups[2].Value;
					string switchVar = match.Groups[3].Value;

					int hashLineStart = FindLineStart(text, match.Index);
					int hashLineEnd = match.Index + match.Length;
					while (hashLineEnd < text.Length && (text[hashLineEnd] == '\r' || text[hashLineEnd] == '\n'))
						hashLineEnd++;

					// Find the containing scope (the { } block this hash assignment is inside)
					int scopeStart, scopeEnd;
					if (!FindContainingScope(text, match.Index, out scopeStart, out scopeEnd)) {
						match = match.NextMatch();
						continue;
					}

					// Verify next statement is an if using hashVar
					string afterHash = text.Substring(hashLineEnd, scopeEnd - hashLineEnd);
					if (!afterHash.TrimStart().StartsWith($"if ({hashVar}")) {
						match = match.NextMatch();
						continue;
					}

					// Find the if/else tree end
					int treeStart = hashLineEnd;
					int treeEnd = FindIfElseTreeEnd(text, treeStart, scopeEnd);
					if (treeEnd < 0 || treeEnd > scopeEnd) {
						match = match.NextMatch();
						continue;
					}

					// Extract cases from the if/else tree
					string treeText = text.Substring(treeStart, treeEnd - treeStart);
					var cases = ExtractCases(treeText, switchVar);
					if (cases.Count < 2) {
						match = match.NextMatch();
						continue;
					}

					// Collect all goto targets referenced by cases
					var caseTargets = new HashSet<string>();
					string? defaultTarget = null;
					foreach (var c in cases)
						caseTargets.Add(c.gotoTarget);

					// Find default target — scan the tree for gotos that aren't case targets
					foreach (Match g in GotoStmt.Matches(treeText)) {
						string label = g.Groups[1].Value;
						if (!caseTargets.Contains(label)) {
							// This is likely the default fallthrough
							// But only if it's not the end label
							defaultTarget ??= label;
						}
					}

					// Find labeled blocks WITHIN the containing scope only
					string scopeText = text.Substring(treeEnd, scopeEnd - treeEnd);
					var labelBodies = ExtractLabelBodies(scopeText, caseTargets, defaultTarget, out int labelsConsumed);

					// Find end label (the label all cases goto at the end)
					string? endLabel = FindEndLabel(labelBodies);

					// Build switch
					string switchText = BuildSwitch(indent, switchVar, cases, labelBodies, defaultTarget, endLabel);

					// Replace: hash assignment + if/else tree + label blocks
					int replaceStart = hashLineStart;
					int replaceEnd = treeEnd + labelsConsumed;

					// Safety: don't go past the scope end
					if (replaceEnd > scopeEnd)
						replaceEnd = treeEnd;

					text = text.Substring(0, replaceStart) + switchText + text.Substring(replaceEnd);
					changed = true;
					break;
				}
			}

			return text;
		}

		/// <summary>
		/// Find the enclosing { } scope for a position. Returns the positions of { and }.
		/// This ensures we never consume past method/foreach/class boundaries.
		/// </summary>
		static bool FindContainingScope(string text, int pos, out int scopeStart, out int scopeEnd) {
			scopeStart = -1;
			scopeEnd = -1;

			// Walk backwards to find the nearest unmatched {
			int depth = 0;
			for (int i = pos - 1; i >= 0; i--) {
				if (text[i] == '}') depth++;
				else if (text[i] == '{') {
					if (depth == 0) { scopeStart = i; break; }
					depth--;
				}
			}
			if (scopeStart < 0) return false;

			// Walk forward from scopeStart to find the matching }
			depth = 0;
			bool inStr = false, escaped = false;
			for (int i = scopeStart; i < text.Length; i++) {
				char c = text[i];
				if (escaped) { escaped = false; continue; }
				if (c == '\\' && inStr) { escaped = true; continue; }
				if (c == '"') inStr = !inStr;
				if (inStr) continue;
				if (c == '{') depth++;
				else if (c == '}') { depth--; if (depth == 0) { scopeEnd = i; return true; } }
			}
			return false;
		}

		/// <summary>
		/// Find the end of the outermost if/else chain starting at the given position.
		/// </summary>
		static int FindIfElseTreeEnd(string text, int start, int maxEnd) {
			int i = start;
			while (i < maxEnd && char.IsWhiteSpace(text[i])) i++;

			if (i >= maxEnd || !text.Substring(i, Math.Min(4, maxEnd - i)).StartsWith("if ("))
				return -1;

			int depth = 0;
			bool foundBrace = false;
			bool inStr = false, escaped = false;

			for (int j = i; j < maxEnd; j++) {
				char c = text[j];
				if (escaped) { escaped = false; continue; }
				if (c == '\\' && inStr) { escaped = true; continue; }
				if (c == '"') inStr = !inStr;
				if (inStr) continue;

				if (c == '{') { depth++; foundBrace = true; }
				else if (c == '}') {
					depth--;
					if (foundBrace && depth == 0) {
						int afterBrace = j + 1;
						while (afterBrace < maxEnd && (text[afterBrace] == '\r' || text[afterBrace] == '\n' || text[afterBrace] == ' ' || text[afterBrace] == '\t'))
							afterBrace++;
						// Check for "else"
						if (afterBrace + 4 < maxEnd && text.Substring(afterBrace, 4) == "else")
							continue;
						int end = j + 1;
						while (end < maxEnd && (text[end] == '\r' || text[end] == '\n'))
							end++;
						return end;
					}
				}
			}
			return -1;
		}

		/// <summary>
		/// Extract string case labels and their goto targets from the if/else tree text.
		/// </summary>
		static List<(string literal, string gotoTarget)> ExtractCases(string treeText, string switchVar) {
			var cases = new List<(string literal, string gotoTarget)>();
			var lines = treeText.Split('\n');

			for (int i = 0; i < lines.Length; i++) {
				string line = lines[i].Trim();

				// Look for: if (!string.Equals(name, "LITERAL", ...))
				var eqMatch = StringEqualsLiteral.Match(line);
				if (!eqMatch.Success) continue;

				string literal = eqMatch.Groups[1].Value;
				bool negated = line.Contains("!");

				if (negated) {
					// Pattern: if (!string.Equals(name, "LIT")) { goto DEFAULT; }
					// The case target is a goto on a subsequent line (fallthrough)
					// Look ahead for a standalone goto
					for (int j = i + 1; j < lines.Length && j <= i + 5; j++) {
						string nextLine = lines[j].Trim();
						if (nextLine.StartsWith("goto ")) {
							var gm = GotoStmt.Match(nextLine);
							if (gm.Success) {
								cases.Add((literal, gm.Groups[1].Value));
								break;
							}
						}
						// If we hit another if, this goto is embedded differently
						if (nextLine.StartsWith("if (") || nextLine.StartsWith("else"))
							break;
					}
				}
				else {
					// Pattern: if (string.Equals(name, "LIT")) { goto CASE; }
					// The goto is inside the if block
					for (int j = i; j < lines.Length && j <= i + 3; j++) {
						var gm = GotoStmt.Match(lines[j]);
						if (gm.Success) {
							cases.Add((literal, gm.Groups[1].Value));
							break;
						}
					}
				}
			}

			return cases;
		}

		/// <summary>
		/// Extract labeled statement blocks, bounded within the scope text.
		/// Only consumes labels that are goto targets of the hash-switch.
		/// </summary>
		static Dictionary<string, string> ExtractLabelBodies(string scopeText,
			HashSet<string> caseTargets, string? defaultTarget, out int consumed) {

			var bodies = new Dictionary<string, string>();
			consumed = 0;

			var allTargets = new HashSet<string>(caseTargets);
			if (defaultTarget != null)
				allTargets.Add(defaultTarget);

			// Find all labels in scopeText
			var labels = new List<(int pos, string name, int lineEnd)>();
			foreach (Match m in LabelLine.Matches(scopeText)) {
				string name = m.Groups[2].Value;
				int lineEnd = m.Index + m.Length;
				labels.Add((m.Index, name, lineEnd));
			}

			if (labels.Count == 0)
				return bodies;

			// Only process labels that are targets of our hash-switch
			for (int i = 0; i < labels.Count; i++) {
				if (!allTargets.Contains(labels[i].name))
					continue;

				int bodyStart = labels[i].lineEnd;

				// Body extends until the next label or a line starting with } at same/lower indent
				int bodyEnd = bodyStart;
				if (i + 1 < labels.Count)
					bodyEnd = labels[i + 1].pos;
				else {
					// Last label — find end by looking for end of statement(s)
					// Stop at next label, or at closing brace at depth 0, or at end
					int j = bodyStart;
					while (j < scopeText.Length) {
						if (scopeText[j] == '\n') {
							int nextLineStart = j + 1;
							string nextLine = "";
							int k = nextLineStart;
							while (k < scopeText.Length && scopeText[k] != '\n')
								k++;
							if (k > nextLineStart)
								nextLine = scopeText.Substring(nextLineStart, k - nextLineStart).Trim();

							// Stop if we hit a non-label, non-goto, non-empty line that looks like
							// regular code (not part of a label body)
							if (nextLine.Length > 0 && !nextLine.StartsWith("goto ") &&
								!nextLine.StartsWith("IL_") && !nextLine.StartsWith("//") &&
								!nextLine.EndsWith(";"))
								break;
						}
						j++;
					}
					bodyEnd = j;
				}

				string body = scopeText.Substring(bodyStart, bodyEnd - bodyStart).Trim();

				// Remove trailing goto (to end label)
				var trailingGoto = GotoStmt.Match(body);
				if (trailingGoto.Success) {
					int gotoStart = body.LastIndexOf("goto ");
					if (gotoStart >= 0)
						body = body.Substring(0, gotoStart).Trim();
				}

				bodies[labels[i].name] = body;
				consumed = Math.Max(consumed, bodyEnd);
			}

			// Also consume the end label line itself if it exists
			foreach (var label in labels) {
				if (!allTargets.Contains(label.name) && label.pos >= consumed - 10) {
					// This might be the end label — consume it too
					int lineEnd = label.lineEnd;
					while (lineEnd < scopeText.Length && (scopeText[lineEnd] == '\r' || scopeText[lineEnd] == '\n'))
						lineEnd++;
					consumed = Math.Max(consumed, lineEnd);
					break;
				}
			}

			return bodies;
		}

		/// <summary>
		/// Find the end label — the label most cases goto (after their body).
		/// </summary>
		static string? FindEndLabel(Dictionary<string, string> labelBodies) {
			// The end label is typically the one NOT in labelBodies as a key
			// but referenced by goto in the bodies
			return null;
		}

		/// <summary>
		/// Build a switch statement text from cases and label bodies.
		/// </summary>
		static string BuildSwitch(string indent, string switchVar,
			List<(string literal, string gotoTarget)> cases,
			Dictionary<string, string> labelBodies,
			string? defaultTarget, string? endLabel) {

			var sb = new StringBuilder();
			sb.AppendLine($"{indent}switch ({switchVar})");
			sb.AppendLine($"{indent}{{");

			// Group cases by goto target
			var groups = new Dictionary<string, List<string>>();
			foreach (var c in cases) {
				if (!groups.ContainsKey(c.gotoTarget))
					groups[c.gotoTarget] = new List<string>();
				groups[c.gotoTarget].Add(c.literal);
			}

			foreach (var group in groups) {
				foreach (var lit in group.Value)
					sb.AppendLine($"{indent}\tcase \"{lit}\":");

				if (labelBodies.TryGetValue(group.Key, out string? body) && !string.IsNullOrWhiteSpace(body)) {
					foreach (var line in body.Split('\n')) {
						string trimmed = line.TrimEnd('\r').Trim();
						if (trimmed.Length > 0)
							sb.AppendLine($"{indent}\t\t{trimmed}");
					}
				}
				sb.AppendLine($"{indent}\t\tbreak;");
			}

			// Default case
			if (defaultTarget != null) {
				sb.AppendLine($"{indent}\tdefault:");
				if (labelBodies.TryGetValue(defaultTarget, out string? body) && !string.IsNullOrWhiteSpace(body)) {
					foreach (var line in body.Split('\n')) {
						string trimmed = line.TrimEnd('\r').Trim();
						if (trimmed.Length > 0)
							sb.AppendLine($"{indent}\t\t{trimmed}");
					}
				}
				sb.AppendLine($"{indent}\t\tbreak;");
			}

			sb.AppendLine($"{indent}}}");
			return sb.ToString();
		}

		static int FindLineStart(string text, int pos) {
			int i = pos - 1;
			while (i >= 0 && text[i] != '\n')
				i--;
			return i + 1;
		}
	}
}
