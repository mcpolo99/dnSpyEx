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

using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.NRefactory.CSharp;

namespace dnSpy.Decompiler.ILSpy.Core.CSharp {
	sealed class WithEventsTransform : IAstTransform {
		readonly HashSet<PropertyDef> withEventsProperties;

		public WithEventsTransform(HashSet<PropertyDef> withEventsProperties) {
			this.withEventsProperties = withEventsProperties;
		}

		public void Run(AstNode compilationUnit) {
			// Phase 1: Collect event subscription info from WithEvents property setters
			var propertyEvents = new Dictionary<string, List<EventSubscription>>();
			var propertiesToReplace = new List<(PropertyDeclaration decl, PropertyDef propDef)>();

			// Build name-based lookup for fallback matching
			var withEventsByName = new Dictionary<string, PropertyDef>();
			foreach (var p in withEventsProperties)
				withEventsByName[p.Name] = p;

			foreach (var propDecl in compilationUnit.Descendants.OfType<PropertyDeclaration>()) {
				PropertyDef? propDef = null;

				// Try annotation first (fastest, works without extra decompiler flags)
				var def = propDecl.Annotation<IMemberDef>() as PropertyDef;
				if (def is not null && withEventsProperties.Contains(def))
					propDef = def;

				// Fallback: match by name
				if (propDef is null)
					withEventsByName.TryGetValue(propDecl.Name, out propDef);

				if (propDef is null)
					continue;

				var events = ExtractEventSubscriptions(propDecl);
				propertyEvents[propDef.Name] = events;
				propertiesToReplace.Add((propDecl, propDef));
			}

			if (propertiesToReplace.Count == 0)
				return;

			// Phase 2: Replace PropertyDeclarations with FieldDeclarations
			// Also collect backing field names to remove their declarations
			var backingFieldNames = new HashSet<string>();
			foreach (var (propDecl, propDef) in propertiesToReplace) {
				// Create field declaration: private <Type> <PropertyName>;
				var fieldDecl = new FieldDeclaration();
				fieldDecl.Modifiers = Modifiers.Private;
				fieldDecl.ReturnType = propDecl.ReturnType.Clone();
				fieldDecl.Variables.Add(new VariableInitializer(null, propDef.Name));

				// Replace property with field
				propDecl.ReplaceWith(fieldDecl);

				// Track backing field name for removal
				backingFieldNames.Add("_" + propDef.Name);
			}

			// Phase 3: Remove backing field declarations (_PropertyName)
			foreach (var fieldDecl in compilationUnit.Descendants.OfType<FieldDeclaration>().ToArray()) {
				foreach (var variable in fieldDecl.Variables) {
					if (backingFieldNames.Contains(variable.Name)) {
						fieldDecl.Remove();
						break;
					}
				}
			}

			// Phase 4: Inject event subscriptions into InitializeComponent
			var initMethod = compilationUnit.Descendants.OfType<MethodDeclaration>()
				.FirstOrDefault(m => m.Name == "InitializeComponent");
			if (initMethod is null || initMethod.Body.IsNull)
				return;

			var statements = initMethod.Body.Statements.ToArray();
			for (int i = statements.Length - 1; i >= 0; i--) {
				var stmt = statements[i];
				// Find assignment: this.PropertyName = expr;
				if (stmt is ExpressionStatement exprStmt &&
					exprStmt.Expression is AssignmentExpression assignment &&
					assignment.Left is MemberReferenceExpression memberRef) {

					var propName = memberRef.MemberName;
					if (!propertyEvents.TryGetValue(propName, out var events) || events.Count == 0)
						continue;

					// Insert event subscriptions AFTER this assignment
					// Insert in reverse order so they end up in correct order
					for (int j = events.Count - 1; j >= 0; j--) {
						var ev = events[j];
						// Build: this.PropertyName.EventName += HandlerMethodName;
						var thisExpr = new ThisReferenceExpression();
						var propRef = new MemberReferenceExpression(thisExpr, propName);
						var eventRef = new MemberReferenceExpression(propRef, ev.EventName);
						var handlerRef = new IdentifierExpression(ev.HandlerMethodName);
						var addAssign = new AssignmentExpression(eventRef, AssignmentOperatorType.Add, handlerRef);
						var newStmt = new ExpressionStatement(addAssign);

						stmt.Parent.InsertChildAfter(stmt, newStmt, BlockStatement.StatementRole);
					}
				}
			}
		}

		List<EventSubscription> ExtractEventSubscriptions(PropertyDeclaration propDecl) {
			var result = new List<EventSubscription>();

			// Find the setter accessor
			var setter = propDecl.Setter;
			if (setter.IsNull || setter.Body.IsNull)
				return result;

			// Scan setter body for += patterns (event subscriptions on the new value)
			// Pattern: backingField.EventName += handler
			// We look for AssignmentExpression with Add operator
			foreach (var assignment in setter.Body.Descendants.OfType<AssignmentExpression>()) {
				if (assignment.Operator != AssignmentOperatorType.Add)
					continue;

				// Left side should be: something.EventName
				if (assignment.Left is not MemberReferenceExpression eventAccess)
					continue;

				var eventName = eventAccess.MemberName;

				// Right side should reference the handler
				// Could be: new EventHandler(this.MethodName) or just a delegate variable
				// We need to trace back to find the method name
				string? handlerMethodName = FindHandlerMethodName(setter.Body, assignment.Right);
				if (handlerMethodName is null)
					continue;

				result.Add(new EventSubscription(eventName, handlerMethodName));
			}

			return result;
		}

		string? FindHandlerMethodName(BlockStatement body, Expression handlerExpr) {
			// The handler expression in += can be:
			// 1. A local variable referencing a delegate creation
			// 2. A direct delegate creation: new EventHandler(this.Method)
			// 3. A method group: this.Method (with implicit conversion)
			if (handlerExpr is IdentifierExpression ident) {
				// Find the variable declaration and resolve its initializer
				foreach (var varDecl in body.Descendants.OfType<VariableDeclarationStatement>()) {
					foreach (var variable in varDecl.Variables) {
						if (variable.Name != ident.Identifier)
							continue;
						// Explicit delegate creation: new EventHandler(this.MethodName)
						if (variable.Initializer is ObjectCreateExpression objCreate) {
							var arg = objCreate.Arguments.FirstOrDefault();
							if (arg is MemberReferenceExpression methodRef)
								return methodRef.MemberName;
						}
						// Method group conversion: EventHandler handler = this.MethodName;
						else if (variable.Initializer is MemberReferenceExpression directRef)
							return directRef.MemberName;
					}
				}
			}
			// Direct delegate creation: new EventHandler(this.MethodName)
			else if (handlerExpr is ObjectCreateExpression directCreate) {
				var arg = directCreate.Arguments.FirstOrDefault();
				if (arg is MemberReferenceExpression methodRef)
					return methodRef.MemberName;
			}
			// Method group conversion: event += this.MethodName
			else if (handlerExpr is MemberReferenceExpression memberRef)
				return memberRef.MemberName;

			return null;
		}

		struct EventSubscription {
			public string EventName;
			public string HandlerMethodName;

			public EventSubscription(string eventName, string handlerMethodName) {
				EventName = eventName;
				HandlerMethodName = handlerMethodName;
			}
		}
	}

	sealed class CompositeTransform : IAstTransform {
		readonly IAstTransform[] transforms;
		public CompositeTransform(params IAstTransform[] transforms) => this.transforms = transforms;
		public void Run(AstNode node) {
			foreach (var t in transforms)
				t.Run(node);
		}
	}
}
