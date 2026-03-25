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
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace dnSpy.Decompiler.MSBuild {
	/// <summary>
	/// Roslyn-based rewriter that converts VB.NET hash-switch-on-string patterns to switch statements.
	/// Uses proper AST scoping — never consumes past method/block boundaries.
	/// </summary>
	static class RoslynHashSwitchRewriter {
		public static (string result, int transforms) Rewrite(string sourceText) {
			var tree = CSharpSyntaxTree.ParseText(sourceText);
			var root = tree.GetRoot();
			var rewriter = new HashSwitchVisitor();
			var newRoot = rewriter.Visit(root);
			if (rewriter.Count == 0)
				return (sourceText, 0);
			return (newRoot.ToFullString(), rewriter.Count);
		}
	}

	sealed class HashSwitchVisitor : CSharpSyntaxRewriter {
		public int Count { get; private set; }

		public override SyntaxNode? VisitBlock(BlockSyntax node) {
			node = (BlockSyntax)base.VisitBlock(node)!;

			var stmts = node.Statements;
			var result = new List<StatementSyntax>();
			bool changed = false;

			for (int i = 0; i < stmts.Count; i++) {
				if (i + 1 < stmts.Count &&
					IsHashAssignment(stmts[i], out var hashVar, out var switchVar) &&
					stmts[i + 1] is IfStatementSyntax ifStmt &&
					UsesVar(ifStmt.Condition, hashVar!)) {

					var cases = new List<(string literal, string target)>();
					string? defaultLabel = null;
					WalkIfTree(ifStmt, hashVar!, cases, ref defaultLabel);

					if (cases.Count >= 2) {
						var labelMap = new Dictionary<string, List<StatementSyntax>>();
						var consumed = new HashSet<int> { i, i + 1 };
						string? endLabel = null;

						CollectLabels(stmts, i + 2, cases, defaultLabel, labelMap, consumed, ref endLabel);

						var sw = BuildSwitch(switchVar!, cases, labelMap, defaultLabel, endLabel, node);
						if (sw != null) {
							for (int j = 0; j < i; j++)
								result.Add(stmts[j]);

							result.Add(sw);

							for (int j = i + 2; j < stmts.Count; j++) {
								if (!consumed.Contains(j))
									result.Add(stmts[j]);
							}
							Count++;
							changed = true;
							break;
						}
					}
				}

				if (!changed)
					result.Add(stmts[i]);
			}

			return changed ? node.WithStatements(List(result)) : node;
		}

		static bool IsHashAssignment(StatementSyntax stmt, out string? hashVar, out string? switchVar) {
			hashVar = switchVar = null;
			if (stmt is not LocalDeclarationStatementSyntax decl) return false;
			if (decl.Declaration.Variables.Count != 1) return false;
			var v = decl.Declaration.Variables[0];
			if (v.Initializer?.Value is not InvocationExpressionSyntax inv) return false;
			if (!inv.Expression.ToString().Contains("ComputeStringHash")) return false;
			if (inv.ArgumentList.Arguments.Count != 1) return false;
			hashVar = v.Identifier.Text;
			switchVar = inv.ArgumentList.Arguments[0].Expression.ToString();
			return true;
		}

		static bool UsesVar(ExpressionSyntax expr, string name) =>
			expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == name);

		static void WalkIfTree(IfStatementSyntax ifStmt, string hashVar,
			List<(string literal, string target)> cases, ref string? defaultLabel) {

			if (UsesVar(ifStmt.Condition, hashVar)) {
				WalkStatement(ifStmt.Statement, hashVar, cases, ref defaultLabel);
				if (ifStmt.Else?.Statement != null)
					WalkStatement(ifStmt.Else.Statement, hashVar, cases, ref defaultLabel);
				return;
			}

			if (TryGetStringLiteral(ifStmt.Condition, out var literal, out bool neg)) {
				if (neg) {
					var target = GotoTarget(ifStmt.Statement);
					if (target != null) defaultLabel ??= target;
				}
				else {
					var target = GotoTarget(ifStmt.Statement);
					if (target != null && literal != null)
						cases.Add((literal, target));
				}
			}
		}

		static void WalkStatement(StatementSyntax stmt, string hashVar,
			List<(string literal, string target)> cases, ref string? defaultLabel) {

			if (stmt is BlockSyntax block) {
				for (int i = 0; i < block.Statements.Count; i++) {
					if (block.Statements[i] is IfStatementSyntax inner) {
						WalkIfTree(inner, hashVar, cases, ref defaultLabel);
						// Check for fallthrough goto after the if
						if (i + 1 < block.Statements.Count &&
							block.Statements[i + 1] is GotoStatementSyntax fg) {
							var target = fg.Expression?.ToString();
							var literal = FindLiteral(inner);
							if (target != null && literal != null)
								cases.Add((literal, target));
						}
					}
					else if (block.Statements[i] is GotoStatementSyntax gs) {
						defaultLabel ??= gs.Expression?.ToString();
					}
				}
			}
			else if (stmt is IfStatementSyntax inner) {
				WalkIfTree(inner, hashVar, cases, ref defaultLabel);
			}
		}

		static bool TryGetStringLiteral(ExpressionSyntax expr, out string? literal, out bool negated) {
			literal = null; negated = false;
			if (expr is PrefixUnaryExpressionSyntax pfx && pfx.OperatorToken.IsKind(SyntaxKind.ExclamationToken)) {
				negated = true; expr = pfx.Operand;
			}
			if (expr is InvocationExpressionSyntax inv && inv.Expression.ToString().Contains("Equals")) {
				var args = inv.ArgumentList.Arguments;
				if (args.Count >= 2 && args[1].Expression is LiteralExpressionSyntax lit
					&& lit.IsKind(SyntaxKind.StringLiteralExpression)) {
					literal = lit.Token.ValueText;
					return true;
				}
			}
			return false;
		}

		static string? FindLiteral(IfStatementSyntax ifStmt) {
			if (TryGetStringLiteral(ifStmt.Condition, out var lit, out _)) return lit;
			if (ifStmt.Statement is BlockSyntax b)
				foreach (var s in b.Statements)
					if (s is IfStatementSyntax inner) {
						var r = FindLiteral(inner);
						if (r != null) return r;
					}
			return null;
		}

		static string? GotoTarget(StatementSyntax stmt) {
			if (stmt is GotoStatementSyntax gs) return gs.Expression?.ToString();
			if (stmt is BlockSyntax b && b.Statements.Count == 1) return GotoTarget(b.Statements[0]);
			return null;
		}

		/// <summary>
		/// Collect labeled statement bodies within THIS block only, starting from startIdx.
		/// This is the key safety guarantee — we never look outside the current block.
		/// </summary>
		static void CollectLabels(SyntaxList<StatementSyntax> stmts, int startIdx,
			List<(string literal, string target)> cases, string? defaultLabel,
			Dictionary<string, List<StatementSyntax>> labelMap,
			HashSet<int> consumed, ref string? endLabel) {

			var needed = new HashSet<string>();
			foreach (var c in cases) needed.Add(c.target);
			if (defaultLabel != null) needed.Add(defaultLabel);

			for (int j = startIdx; j < stmts.Count; j++) {
				if (stmts[j] is LabeledStatementSyntax labeled) {
					string label = labeled.Identifier.Text;
					if (!needed.Contains(label)) {
						// This could be the end label or an unrelated label — check if it's
						// referenced as a goto target from case bodies
						endLabel ??= label;
						consumed.Add(j);
						continue;
					}

					consumed.Add(j);
					var body = new List<StatementSyntax>();
					if (labeled.Statement is not EmptyStatementSyntax)
						body.Add(labeled.Statement);

					for (int k = j + 1; k < stmts.Count; k++) {
						if (stmts[k] is LabeledStatementSyntax) break;
						if (stmts[k] is GotoStatementSyntax gs) {
							endLabel ??= gs.Expression?.ToString();
							consumed.Add(k);
							break;
						}
						body.Add(stmts[k]);
						consumed.Add(k);
					}
					labelMap[label] = body;
				}
			}
		}

		static SwitchStatementSyntax? BuildSwitch(string switchVar,
			List<(string literal, string target)> cases,
			Dictionary<string, List<StatementSyntax>> labelMap,
			string? defaultLabel, string? endLabel, BlockSyntax origBlock) {

			var groups = new Dictionary<string, List<string>>();
			foreach (var c in cases) {
				if (!groups.ContainsKey(c.target)) groups[c.target] = new List<string>();
				groups[c.target].Add(c.literal);
			}

			var sections = new List<SwitchSectionSyntax>();
			foreach (var g in groups) {
				var labels = g.Value.Select(lit =>
					(SwitchLabelSyntax)CaseSwitchLabel(
						LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(lit)))).ToList();

				var body = new List<StatementSyntax>();
				if (labelMap.TryGetValue(g.Key, out var stmts))
					body.AddRange(stmts.Where(s =>
						!(s is GotoStatementSyntax gs && gs.Expression?.ToString() == endLabel)));
				body.Add(BreakStatement());
				sections.Add(SwitchSection(List(labels), List(body)));
			}

			if (defaultLabel != null && labelMap.TryGetValue(defaultLabel, out var defBody)) {
				var body = defBody.Where(s =>
					!(s is GotoStatementSyntax gs && gs.Expression?.ToString() == endLabel)).ToList();
				body.Add(BreakStatement());
				sections.Add(SwitchSection(
					SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel()),
					List<StatementSyntax>(body)));
			}

			if (sections.Count == 0) return null;

			return SwitchStatement(IdentifierName(switchVar), List(sections))
				.NormalizeWhitespace()
				.WithLeadingTrivia(origBlock.Statements[0].GetLeadingTrivia());
		}
	}
}
