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
	/// Minimal expression type inference for the VB converter. The 3.2 syntax tree carries no
	/// type annotations, but several VB constructs need the static type of an operand:
	/// reference equality must use Is instead of =, and arithmetic on Char needs AscW.
	///
	/// Only names are inferred (no full type system). Anything that cannot be resolved stays
	/// Unknown so callers keep their previous behaviour.
	/// </summary>
	sealed class ExpressionTypeResolver {
		readonly ModuleDef module;
		readonly Dictionary<string, string> variables;
		readonly Dictionary<string, bool?> referenceCache = new Dictionary<string, bool?>(StringComparer.Ordinal);
		readonly Dictionary<string, Mono.Cecil.TypeDefinition> externalTypes = new Dictionary<string, Mono.Cecil.TypeDefinition>(StringComparer.Ordinal);
		readonly Dictionary<string, Mono.Cecil.AssemblyDefinition> resolvedAssemblies = new Dictionary<string, Mono.Cecil.AssemblyDefinition>(StringComparer.Ordinal);

		/// <summary>Type definition of the member whose body is being converted.</summary>
		public TypeDef CurrentType { get; set; }

		public ExpressionTypeResolver(ModuleDef module, Dictionary<string, string> variables) {
			this.module = module;
			this.variables = variables;
		}

		/// <summary>True when both operands are known to be reference types.</summary>
		public bool AreBothReferenceTypes(ICSharpCode.Decompiler.CSharp.Syntax.Expression left,
				ICSharpCode.Decompiler.CSharp.Syntax.Expression right) =>
			IsReference(left) == true && IsReference(right) == true;

		public bool? IsReference(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			var name = GetTypeName(expression);
			if (string.IsNullOrEmpty(name))
				return null;
			return Classify(name);
		}

		public string GetTypeName(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			switch (expression) {
			case ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveExpression primitive:
				return PrimitiveName(primitive.Value);
			case ICSharpCode.Decompiler.CSharp.Syntax.CastExpression cast:
				return cast.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.ObjectCreateExpression created:
				return created.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.ArrayCreateExpression array:
				return array.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.IdentifierExpression identifier:
				return LookupVariable(identifier.Identifier) ?? MemberType(identifier.Identifier);
			case ICSharpCode.Decompiler.CSharp.Syntax.MemberReferenceExpression member:
				if (member.Target is ICSharpCode.Decompiler.CSharp.Syntax.ThisReferenceExpression)
					return MemberType(member.MemberName);
				var targetType = GetTypeName(member.Target);
				return targetType is null ? null : MemberTypeOf(targetType, member.MemberName);
			case ICSharpCode.Decompiler.CSharp.Syntax.ParenthesizedExpression parenthesized:
				return GetTypeName(parenthesized.Expression);
			case ICSharpCode.Decompiler.CSharp.Syntax.ConditionalExpression conditional:
				// "cond ? a : b" has the type the two branches share.
				var whenTrue = GetTypeName(conditional.TrueExpression);
				var whenFalse = GetTypeName(conditional.FalseExpression);
				if (whenTrue is null)
					return whenFalse;
				if (whenFalse is null)
					return whenTrue;
				return whenTrue == whenFalse ? whenTrue : Wider(whenTrue, whenFalse) ?? whenTrue;
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorExpression binary:
				return BinaryResultType(binary);
			case ICSharpCode.Decompiler.CSharp.Syntax.IndexerExpression indexer:
				var indexed = GetTypeName(indexer.Target);
				if (indexed is null)
					return null;
				if (IsStringType(indexed))
					return "char";
				if (indexed.EndsWith("[]", StringComparison.Ordinal))
					return indexed.Substring(0, indexed.Length - 2);
				// Anything else with an Item indexer (StringBuilder, IList<T>, ...) yields the
				// indexer's return type.
				return IndexerResultType(indexed);
			default:
				return null;
			}
		}

		/// <summary>true: reference type, false: value type, null: unknown.</summary>
		public bool? Classify(string typeName) {
			var name = Normalize(typeName);
			if (name is null)
				return null;
			if (referenceCache.TryGetValue(name, out var cached))
				return cached;

			bool? result = null;
			if (IsStringType(name) || name == "object" || name == "Object")
				result = true;
			else if (name.EndsWith("[]", StringComparison.Ordinal))
				result = true;
			else if (IsValueTypeName(name))
				result = false;
			else {
				var local = LookupLocalTypeDef(name);
				if (local != null)
					result = !local.IsValueType;
				else {
					var external = LookupExternalType(name);
					if (external != null)
						result = !external.IsValueType;
					else {
						var runtimeType = GetRuntimeType(name);
						if (runtimeType != null)
							result = !runtimeType.IsValueType;
					}
				}
			}

			referenceCache[name] = result;
			return result;
		}

		static string PrimitiveName(object value) {
			switch (value) {
			case null: return null;
			case bool _: return "bool";
			case char _: return "char";
			case string _: return "string";
			case sbyte _: return "sbyte";
			case byte _: return "byte";
			case short _: return "short";
			case ushort _: return "ushort";
			case int _: return "int";
			case uint _: return "uint";
			case long _: return "long";
			case ulong _: return "ulong";
			case float _: return "float";
			case double _: return "double";
			case decimal _: return "decimal";
			default: return null;
			}
		}

		static string Normalize(string typeName) {
			if (string.IsNullOrEmpty(typeName))
				return null;
			var name = typeName.Trim();
			if (name.StartsWith("global::", StringComparison.Ordinal))
				name = name.Substring("global::".Length);
			if (name.StartsWith("System.", StringComparison.Ordinal) && name.IndexOf('.', "System.".Length) < 0)
				name = name.Substring("System.".Length);
			return name;
		}

		static bool IsStringType(string name) {
			var n = Normalize(name);
			return n == "string" || n == "String";
		}

		static bool IsValueTypeName(string name) {
			switch (name) {
			case "bool": case "Boolean":
			case "byte": case "Byte":
			case "sbyte": case "SByte":
			case "short": case "Int16":
			case "ushort": case "UInt16":
			case "int": case "Int32":
			case "uint": case "UInt32":
			case "long": case "Int64":
			case "ulong": case "UInt64":
			case "float": case "Single":
			case "double": case "Double":
			case "decimal": case "Decimal":
			case "char": case "Char":
			case "DateTime": case "TimeSpan": case "Guid":
			case "IntPtr": case "UIntPtr":
				return true;
			default:
				return false;
			}
		}

		string LookupVariable(string name) =>
			variables.TryGetValue(name, out var typeName) ? typeName : null;

		/// <summary>Type of a member declared on the type currently being converted.</summary>
		string MemberType(string memberName) {
			var type = CurrentType;
			if (type is null)
				return null;
			foreach (var field in type.Fields) {
				if (field.Name == memberName)
					return field.FieldType?.FullName;
			}
			foreach (var property in type.Properties) {
				if (property.Name == memberName)
					return property.PropertySig?.RetType?.FullName;
			}
			return null;
		}

		/// <summary>Result type of a binary operation, as far as the operands allow.</summary>
		string BinaryResultType(ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorExpression binary) {
			var left = GetTypeName(binary.Left);
			var right = GetTypeName(binary.Right);
			switch (binary.Operator) {
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.Equality:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.InEquality:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.LessThan:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.LessThanOrEqual:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.GreaterThan:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.GreaterThanOrEqual:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.ConditionalAnd:
			case ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.ConditionalOr:
				return "bool";
			}
			if (binary.Operator == ICSharpCode.Decompiler.CSharp.Syntax.BinaryOperatorType.Add &&
					(IsStringType(left) || IsStringType(right)))
				return "string";
			// char promotes to int in C#.
			if (IsCharType(left))
				left = "int";
			if (IsCharType(right))
				right = "int";
			if (left is null || right is null)
				return null;
			return Wider(left, right);
		}

		static bool IsCharType(string name) =>
			name == "char" || name == "Char" || name == "System.Char";

		static string Wider(string left, string right) {
			var leftRank = NumericRank(left);
			var rightRank = NumericRank(right);
			if (leftRank < 0 || rightRank < 0)
				return null;
			return leftRank >= rightRank ? left : right;
		}

		static int NumericRank(string name) {
			switch (name) {
			case "byte": case "Byte": case "System.Byte": return 0;
			case "sbyte": case "SByte": case "System.SByte": return 1;
			case "short": case "Int16": case "System.Int16": return 2;
			case "ushort": case "UInt16": case "System.UInt16": return 3;
			case "int": case "Int32": case "System.Int32": return 4;
			case "uint": case "UInt32": case "System.UInt32": return 5;
			case "long": case "Int64": case "System.Int64": return 6;
			case "ulong": case "UInt64": case "System.UInt64": return 7;
			case "float": case "Single": case "System.Single": return 8;
			case "double": case "Double": case "System.Double": return 9;
			case "decimal": case "Decimal": case "System.Decimal": return 10;
			default: return -1;
			}
		}

		/// <summary>Return type of the Item indexer of the given type, if known.</summary>
		string IndexerResultType(string typeName) {
			var name = Normalize(typeName);
			var type = LookupLocalTypeDef(name);
			if (type != null) {
				foreach (var property in type.Properties) {
					if (property.Name == "Item")
						return property.PropertySig?.RetType?.FullName;
				}
			}
			var external = LookupExternalType(name);
			if (external != null) {
				// An indexer is called Item in C# but keeps its metadata name (StringBuilder
				// uses Chars), so accept any property that takes parameters.
				foreach (var property in external.Properties) {
					if (property.Name == "Item")
						return property.PropertyType?.FullName;
				}
				foreach (var property in external.Properties) {
					if (property.HasParameters)
						return property.PropertyType?.FullName;
				}
			}

			// Framework types often fail to resolve through cecil; reflection is reliable there.
			var runtimeType = GetRuntimeType(name);
			if (runtimeType != null) {
				foreach (var property in runtimeType.GetProperties()) {
					if (property.GetIndexParameters().Length > 0)
						return property.PropertyType.FullName;
				}
			}
			return null;
		}

		/// <summary>
		/// Adds the leaf assembly to Type.GetType. It still throws FileLoadException for a
		/// malformed name (a binary result can be "int[]" or similar), so failures are swallowed.
		/// </summary>
		static Type GetRuntimeType(string name) {
			if (string.IsNullOrEmpty(name))
				return null;
			try {
				return Type.GetType(name, throwOnError: false);
			}
			catch {
				return null;
			}
		}

		/// <summary>Type of a member of an explicitly named type.</summary>
		string MemberTypeOf(string typeName, string memberName) {
			var name = Normalize(typeName);
			var type = LookupLocalTypeDef(name);
			if (type != null) {
				foreach (var field in type.Fields) {
					if (field.Name == memberName)
						return field.FieldType?.TypeName;
				}
				foreach (var property in type.Properties) {
					if (property.Name == memberName)
						return property.PropertySig?.RetType?.TypeName;
				}
			}

			// Types from other assemblies (dnlib.DotNet.MethodDef, ...) resolve through cecil.
			var external = LookupExternalType(name);
			if (external is null)
				return null;
			foreach (var field in external.Fields) {
				if (field.Name == memberName)
					return field.FieldType?.FullName;
			}
			foreach (var property in external.Properties) {
				if (property.Name == memberName)
					return property.PropertyType?.FullName;
			}
			return null;
		}

		TypeDef LookupLocalTypeDef(string name) {
			foreach (var type in module.GetTypes()) {
				if (type.FullName == name || type.Name == name)
					return type;
			}
			return null;
		}

		Mono.Cecil.TypeDefinition LookupExternalType(string name) {
			if (externalTypes.TryGetValue(name, out var cached))
				return cached;
			Mono.Cecil.TypeDefinition result = null;
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule != null) {
				result = cecilModule.GetType(name);
				if (result is null) {
					foreach (var assemblyRef in cecilModule.AssemblyReferences) {
						var referenced = ResolveAssembly(assemblyRef);
						result = referenced?.MainModule.GetType(name);
						if (result != null)
							break;
					}
				}
				// Member types sometimes come back with a short name ("MethodDef"), which no
				// full-name lookup can match; fall back to the simple name.
				if (result is null && name.IndexOf('.') < 0) {
					foreach (var assemblyRef in cecilModule.AssemblyReferences) {
						var referenced = ResolveAssembly(assemblyRef);
						var module = referenced?.MainModule;
						if (module is null)
							continue;
						foreach (var type in module.GetTypes()) {
							if (type.Name == name) {
								result = type;
								break;
							}
						}
						if (result != null)
							break;
					}
				}
			}
			externalTypes[name] = result;
			return result;
		}

		Mono.Cecil.AssemblyDefinition ResolveAssembly(Mono.Cecil.AssemblyNameReference name) {
			if (resolvedAssemblies.TryGetValue(name.FullName, out var cached))
				return cached;
			Mono.Cecil.AssemblyDefinition result = null;
			try {
				var cecilModule = DnlibCecilBridge.TryGetModule(module);
				result = cecilModule?.AssemblyResolver?.Resolve(name);
			}
			catch {
				result = null;
			}
			resolvedAssemblies[name.FullName] = result;
			return result;
		}
	}
}
