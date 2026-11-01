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
using dnlib.DotNet;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// The 3.2 engine builds its C# syntax tree without attaching any decompiler metadata:
	/// no <see cref="TypeDef"/>/<see cref="MethodDef"/> annotations. The VB converter was
	/// ported from the 2.3 engine and depends on them for Implements clauses, Overloads and
	/// accessor information, so those features silently did nothing.
	/// This binder walks the tree once and attaches the dnlib definitions by name.
	/// </summary>
	static class AstAnnotationBinder {
		public static void Attach(ModuleDef module, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree) {
			if (module is null || tree is null)
				return;
			var types = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes())
				types[type.FullName] = type;

			var renames = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var typeDeclaration in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>()) {
				var fullName = GetFullTypeName(typeDeclaration);
				if (fullName is null || !types.TryGetValue(fullName, out var typeDef))
					continue;
				typeDeclaration.AddAnnotation(typeDef);
				AttachMembers(typeDef, typeDeclaration);
				CollectIdentifierRenames(typeDef, typeDeclaration, renames);
			}

			if (renames.Count > 0)
				tree.AddAnnotation(renames);
		}

		/// <summary>
		/// VB is case-insensitive, so a private field "start" and a property "Start" cannot both
		/// exist (BC30260/BC31429). Rename the field: fields are private in practice, so no other
		/// file refers to them, while renaming a public member would break the other dumps.
		/// </summary>
		static void CollectIdentifierRenames(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration declaration, Dictionary<string, string> renames) {
			var fields = declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration>()
				.SelectMany(f => f.Variables.Select(v => v.Name)).ToList();
			if (fields.Count == 0)
				return;

			var names = new List<string>(fields);
			names.AddRange(declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration>().Select(p => p.Name));
			names.AddRange(declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration>().Select(m => m.Name));
			names.AddRange(declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.EventDeclaration>().Select(e => e.Name));
			names.AddRange(declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.CustomEventDeclaration>().Select(e => e.Name));
			names.AddRange(declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>().Select(t => t.Name));

			foreach (var group in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)) {
				if (group.Count() <= 1)
					continue;
				if (group.Distinct(StringComparer.Ordinal).Count() <= 1)
					continue;
				var field = fields.FirstOrDefault(f => group.Contains(f, StringComparer.Ordinal));
				if (field is null)
					continue;
				var renamed = field + "_2";
				while (names.Contains(renamed, StringComparer.OrdinalIgnoreCase))
					renamed += "_";
				renames[field] = renamed;
				names.Add(renamed);
			}
		}

		/// <summary>
		/// Rebuilds the dnlib full name ("Namespace.Outer/Inner") from the syntax tree.
		/// </summary>
		static string GetFullTypeName(ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration declaration) {
			var nested = new List<string>();
			string ns = null;
			for (ICSharpCode.Decompiler.CSharp.Syntax.AstNode node = declaration; node != null; node = node.Parent) {
				if (node is ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration type)
					nested.Insert(0, type.Name);
				else if (node is ICSharpCode.Decompiler.CSharp.Syntax.NamespaceDeclaration space) {
					ns = space.Name;
					break;
				}
				else if (node is ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree)
					break;
			}
			if (nested.Count == 0)
				return null;
			var typeName = string.Join("/", nested);
			return string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
		}

		static void AttachMembers(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration declaration) {
			foreach (var method in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration>()) {
				var methodDef = MatchMethod(typeDef, method);
				if (methodDef != null)
					method.AddAnnotation(methodDef);
			}

			foreach (var property in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration>()) {
				var propertyDef = MatchProperty(typeDef, property.Name, 0);
				if (propertyDef != null)
					property.AddAnnotation(propertyDef);
			}

			foreach (var indexer in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.IndexerDeclaration>()) {
				var propertyDef = MatchProperty(typeDef, "Item", indexer.Parameters.Count);
				if (propertyDef != null)
					indexer.AddAnnotation(propertyDef);
			}

			foreach (var eventDeclaration in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.CustomEventDeclaration>()) {
				var eventDef = MatchEvent(typeDef, eventDeclaration.Name);
				if (eventDef != null)
					eventDeclaration.AddAnnotation(eventDef);
			}
		}

		static PropertyDef MatchProperty(TypeDef typeDef, string name, int parameterCount) {
			foreach (var property in typeDef.Properties) {
				if (property.Name != name && !property.Name.EndsWith("." + name, StringComparison.Ordinal))
					continue;
				if (property.PropertySig.Params.Count != parameterCount)
					continue;
				return property;
			}
			return null;
		}

		static EventDef MatchEvent(TypeDef typeDef, string name) {
			foreach (var ev in typeDef.Events) {
				if (ev.Name == name)
					return ev;
			}
			return null;
		}

		/// <summary>
		/// Matches by name and parameter count. Overloads that differ only by parameter types
		/// resolve to the first candidate; the caller only uses the definition for interface
		/// matching and modifier heuristics, both of which re-check the signature.
		/// </summary>
		static MethodDef MatchMethod(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration declaration) {
			MethodDef fallback = null;
			foreach (var method in typeDef.Methods) {
				// Explicit interface implementations are named "IFoo.Bar" in metadata while the
				// syntax tree keeps "Bar". Parameters exclude the hidden this/return parameters.
				if (method.Name != declaration.Name && !method.Name.EndsWith("." + declaration.Name, StringComparison.Ordinal))
					continue;
				var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
				if (parameters.Count != declaration.Parameters.Count)
					continue;
				if (method.IsStatic != declaration.HasModifier(ICSharpCode.Decompiler.CSharp.Syntax.Modifiers.Static))
					continue;
				if (ParameterTypesMatch(parameters, declaration))
					return method;
				// Same name and arity: remember the first candidate, but keep looking. Without
				// this, Equals(object) inherits the definition of Equals(T) and then claims to
				// implement an interface method it does not implement.
				fallback ??= method;
			}
			return fallback;
		}

		static bool ParameterTypesMatch(List<Parameter> parameters, ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration declaration) {
			for (int i = 0; i < parameters.Count; i++) {
				var expected = parameters[i].Type?.TypeName;
				var actual = GetAstTypeName(declaration.Parameters.ElementAt(i).Type);
				if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
					continue;
				if (!string.Equals(LastSegment(expected), actual, StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		static string LastSegment(string name) {
			int index = name.LastIndexOf('.');
			return index < 0 ? name : name.Substring(index + 1);
		}

		static string GetAstTypeName(ICSharpCode.Decompiler.CSharp.Syntax.AstType type) {
			switch (type) {
			case ICSharpCode.Decompiler.CSharp.Syntax.SimpleType simple:
				return simple.Identifier;
			case ICSharpCode.Decompiler.CSharp.Syntax.MemberType member:
				return member.MemberName;
			case ICSharpCode.Decompiler.CSharp.Syntax.ComposedType composed:
				return GetAstTypeName(composed.BaseType);
			case ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType primitive:
				return PrimitiveTypeName(primitive.Keyword);
			default:
				return null;
			}
		}

		/// <summary>
		/// Maps a C# keyword to the BCL type name used by dnlib ("object" -> "Object"), so that
		/// overloads taking a primitive can be told apart from ones taking a reference type.
		/// </summary>
		static string PrimitiveTypeName(string keyword) {
			switch (keyword) {
			case "object": return "Object";
			case "string": return "String";
			case "bool": return "Boolean";
			case "byte": return "Byte";
			case "sbyte": return "SByte";
			case "short": return "Int16";
			case "ushort": return "UInt16";
			case "int": return "Int32";
			case "uint": return "UInt32";
			case "long": return "Int64";
			case "ulong": return "UInt64";
			case "float": return "Single";
			case "double": return "Double";
			case "decimal": return "Decimal";
			case "char": return "Char";
			case "void": return "Void";
			default: return null;
			}
		}
	}
}
