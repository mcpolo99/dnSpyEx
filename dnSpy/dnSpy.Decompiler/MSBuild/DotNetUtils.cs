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

namespace dnSpy.Decompiler.MSBuild {
	static class DotNetUtils {
		static bool IsType(TypeDef type, string typeFullName) {
			while (type is not null) {
				var bt = type.BaseType;
				if (bt is null)
					break;
				if (bt.FullName == typeFullName)
					return true;
				var resolved = bt.ResolveTypeDef();
				if (resolved is not null) {
					type = resolved;
					continue;
				}
				// When type can't be resolved (e.g., --no-gac), check well-known inheritance chains
				return IsKnownSubType(bt.FullName, typeFullName);
			}
			return false;
		}

		static bool IsKnownSubType(string unresolvedType, string targetType) {
			// Well-known WinForms/Component inheritance chains for when assemblies can't be resolved
			if (targetType == "System.Windows.Forms.Control") {
				return unresolvedType == "System.Windows.Forms.Control" ||
					unresolvedType == "System.Windows.Forms.ScrollableControl" ||
					unresolvedType == "System.Windows.Forms.ContainerControl" ||
					unresolvedType == "System.Windows.Forms.Form" ||
					unresolvedType == "System.Windows.Forms.UserControl" ||
					unresolvedType == "System.Windows.Forms.Panel" ||
					unresolvedType == "System.Windows.Forms.Button" ||
					unresolvedType == "System.Windows.Forms.TextBox" ||
					unresolvedType == "System.Windows.Forms.Label" ||
					unresolvedType == "System.Windows.Forms.DataGridView" ||
					unresolvedType == "System.Windows.Forms.ListView" ||
					unresolvedType == "System.Windows.Forms.TreeView" ||
					unresolvedType == "System.Windows.Forms.ToolStrip" ||
					unresolvedType == "System.Windows.Forms.StatusStrip" ||
					unresolvedType == "System.Windows.Forms.MenuStrip" ||
					unresolvedType == "System.Windows.Forms.SplitContainer";
			}
			if (targetType == "System.Windows.Forms.Form") {
				return unresolvedType == "System.Windows.Forms.Form";
			}
			if (targetType == "System.Windows.Forms.UserControl") {
				return unresolvedType == "System.Windows.Forms.UserControl";
			}
			if (targetType == "System.ComponentModel.Component") {
				return unresolvedType == "System.ComponentModel.Component" ||
					unresolvedType == "System.Windows.Forms.Control" ||
					unresolvedType == "System.Windows.Forms.ScrollableControl" ||
					unresolvedType == "System.Windows.Forms.ContainerControl" ||
					unresolvedType == "System.Windows.Forms.Form" ||
					unresolvedType == "System.Windows.Forms.UserControl";
			}
			if (targetType == "System.Windows.Application") {
				return unresolvedType == "System.Windows.Application";
			}
			return false;
		}

		public static bool IsWinForm(TypeDef type) =>
			IsType(type, "System.Windows.Forms.Control") &&
			type.Methods.Any(x => x.Name == "InitializeComponent");

		public static bool IsWinFormImproved(TypeDef type) =>
			(IsType(type, "System.Windows.Forms.Control") || IsType(type, "System.ComponentModel.Component")) &&
			type.Methods.Any(x => x.Name == "InitializeComponent");

		public static string GetWinFormsSubType(TypeDef type) {
			if (IsType(type, "System.Windows.Forms.Form"))
				return "Form";
			if (IsType(type, "System.Windows.Forms.UserControl"))
				return "UserControl";
			if (IsType(type, "System.ComponentModel.Component"))
				return "Component";
			return "Form";
		}

		public static bool IsSystemWindowsApplication(TypeDef type) => IsType(type, "System.Windows.Application");
		public static bool IsStartUpClass(TypeDef type) => type.Module.EntryPoint is not null && type.Module.EntryPoint.DeclaringType == type;
		public static bool IsUnsafe(ModuleDef module) => module.CustomAttributes.IsDefined("System.Security.UnverifiableCodeAttribute");
		public static IEnumerable<FieldDef> GetFields(MethodDef method) => GetDefs(method).OfType<FieldDef>();

		public static IEnumerable<FieldDef> GetDesignerFields(TypeDef type) {
			foreach (var f in type.Fields) {
				if (f.CustomAttributes.IsDefined("System.ComponentModel.DesignerSerializationVisibilityAttribute") ||
					f.CustomAttributes.IsDefined("System.CodeDom.Compiler.GeneratedCodeAttribute"))
					yield return f;
				// IContainer components field
				if (f.FieldType.RemovePinnedAndModifiers()?.FullName == "System.ComponentModel.IContainer")
					yield return f;
			}
		}

		public static IEnumerable<IMemberDef> GetDefs(MethodDef method) {
			var body = method.Body;
			if (body is not null) {
				foreach (var instr in body.Instructions) {
					if (instr.Operand is IMemberDef def && def.DeclaringType == method.DeclaringType)
						yield return def;
				}
			}
		}

		public static IEnumerable<IMemberDef> GetDefs(PropertyDef prop) {
			foreach (var g in prop.GetMethods) {
				foreach (var d in GetDefs(g))
					yield return d;
			}
		}

		public static IEnumerable<IMemberDef> GetMethodsAndSelf(PropertyDef p) {
			yield return p;
			foreach (var m in p.GetMethods)
				yield return m;
			foreach (var m in p.SetMethods)
				yield return m;
			foreach (var m in p.OtherMethods)
				yield return m;
		}

		public static PropertyDef? GetOwningProperty(MethodDef method) {
			if (method.DeclaringType is null)
				return null;
			foreach (var p in method.DeclaringType.Properties) {
				foreach (var m in p.GetMethods)
					if (m == method) return p;
				foreach (var m in p.SetMethods)
					if (m == method) return p;
				foreach (var m in p.OtherMethods)
					if (m == method) return p;
			}
			return null;
		}

		public static bool IsWithEventsProperty(PropertyDef prop) {
			if (prop.GetMethod is null || prop.SetMethod is null)
				return false;
			if (!prop.GetMethod.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
				return false;
			if (!prop.SetMethod.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
				return false;
			return prop.SetMethod.ImplAttributes.HasFlag(MethodImplAttributes.Synchronized);
		}
	}
}
