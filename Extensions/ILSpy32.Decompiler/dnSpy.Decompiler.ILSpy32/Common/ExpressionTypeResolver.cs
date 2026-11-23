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
		readonly Dictionary<string, bool> implicitStringCache = new Dictionary<string, bool>(StringComparer.Ordinal);
		readonly Dictionary<string, Mono.Cecil.TypeDefinition> externalTypes = new Dictionary<string, Mono.Cecil.TypeDefinition>(StringComparer.Ordinal);
		readonly Dictionary<string, Mono.Cecil.AssemblyDefinition> resolvedAssemblies = new Dictionary<string, Mono.Cecil.AssemblyDefinition>(StringComparer.Ordinal);

		/// <summary>Type definition of the member whose body is being converted.</summary>
		public TypeDef CurrentType { get; set; }

		public ExpressionTypeResolver(ModuleDef module, Dictionary<string, string> variables) {
			this.module = module;
			this.variables = variables;
		}

		/// <summary>
		/// True when 'left == right' can only be a reference comparison. One side being a known
		/// reference type is enough: a struct operand would need an operator the other operand
		/// cannot convert to. A side known to be a value type vetoes it.
		/// </summary>
		public bool IsReferentialComparison(ICSharpCode.Decompiler.CSharp.Syntax.Expression left,
				ICSharpCode.Decompiler.CSharp.Syntax.Expression right) {
			var l = IsReference(left);
			var r = IsReference(right);
			if (l == false || r == false)
				return false;
			// C# '==' between strings binds String.op_Equality (value comparison), and VB '=' is
			// the matching operator; 'Is' would compare references. When the other side is a
			// known object reference, C# really did bind reference equality, so 'Is' stays.
			var lString = IsStringType(GetTypeName(left));
			var rString = IsStringType(GetTypeName(right));
			if (lString && (rString || r is null))
				return false;
			if (rString && l is null)
				return false;
			return l == true || r == true;
		}

		public bool? IsReference(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			var name = GetTypeName(expression);
			if (string.IsNullOrEmpty(name))
				return null;
			return Classify(name);
		}

		/// <summary>
		/// True when the expression's type defines an implicit conversion to String. C# folds that
		/// conversion into 'a + b' (dnlib's UTF8String is the common case), VB does not chain a
		/// user-defined conversion into the built-in '+' operator and needs CStr().
		/// </summary>
		public bool HasImplicitStringConversion(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			var name = Normalize(GetTypeName(expression));
			if (string.IsNullOrEmpty(name) || IsStringType(name))
				return false;
			if (implicitStringCache.TryGetValue(name, out var cached))
				return cached;

			var bare = StripGenericArgs(name, out var arity);
			bool result;
			var local = LookupLocalTypeDef(bare, arity) ?? LookupLocalTypeDef(name);
			if (local != null)
				result = HasImplicitStringConversion(local);
			else {
				var external = LookupExternalType(bare, arity) ?? LookupExternalType(name);
				result = external != null && HasImplicitStringConversion(external);
			}
			implicitStringCache[name] = result;
			return result;
		}

		static bool HasImplicitStringConversion(TypeDef type) {
			foreach (var method in type.Methods) {
				if (!method.IsStatic || method.Name != "op_Implicit")
					continue;
				var sig = method.MethodSig;
				if (sig is null || sig.Params.Count != 1)
					continue;
				var returnType = sig.RetType?.RemovePinnedAndModifiers();
				if (returnType is ClassSig classSig && classSig.TypeDefOrRef?.FullName == "System.String")
					return true;
			}
			return false;
		}

		static bool HasImplicitStringConversion(Mono.Cecil.TypeDefinition type) {
			foreach (var method in type.Methods) {
				if (!method.IsStatic || method.Name != "op_Implicit")
					continue;
				if (method.Parameters.Count != 1)
					continue;
				if (method.ReturnType?.FullName == "System.String")
					return true;
			}
			return false;
		}

		public string GetTypeName(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			switch (expression) {
			case ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveExpression primitive:
				return PrimitiveName(primitive.Value);
			case ICSharpCode.Decompiler.CSharp.Syntax.CastExpression cast:
				return cast.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.AsExpression asCast:
				// 'x as T' and 'x is T' — the latter yields a bool (a known value type, which
				// vetoes a reference comparison on that side).
				return asCast.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.IsExpression:
				return "bool";
			case ICSharpCode.Decompiler.CSharp.Syntax.ObjectCreateExpression created:
				return created.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.ArrayCreateExpression array:
				return array.Type?.ToString();
			case ICSharpCode.Decompiler.CSharp.Syntax.IdentifierExpression identifier:
				return LookupVariable(identifier.Identifier) ?? MemberType(identifier.Identifier);
			case ICSharpCode.Decompiler.CSharp.Syntax.BaseReferenceExpression:
				// MyBase.X resolves members against the base type.
				return CurrentType?.BaseType?.FullName;
			case ICSharpCode.Decompiler.CSharp.Syntax.MemberReferenceExpression member:
				if (member.Target is ICSharpCode.Decompiler.CSharp.Syntax.ThisReferenceExpression)
					return MemberType(member.MemberName);
				var targetType = GetTypeName(member.Target);
				return targetType is null ? null : MemberTypeOf(targetType, member.MemberName);
			case ICSharpCode.Decompiler.CSharp.Syntax.InvocationExpression invocation:
				return InvocationResultType(invocation);
			case ICSharpCode.Decompiler.CSharp.Syntax.ParenthesizedExpression parenthesized:
				return GetTypeName(parenthesized.Expression);
			case ICSharpCode.Decompiler.CSharp.Syntax.UnaryOperatorExpression unary:
				switch (unary.Operator) {
				case ICSharpCode.Decompiler.CSharp.Syntax.UnaryOperatorType.Not:
					return "bool";
				case ICSharpCode.Decompiler.CSharp.Syntax.UnaryOperatorType.Minus:
				case ICSharpCode.Decompiler.CSharp.Syntax.UnaryOperatorType.Plus:
				case ICSharpCode.Decompiler.CSharp.Syntax.UnaryOperatorType.BitNot:
					return GetTypeName(unary.Expression);
				}
				return null;
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

		/// <summary>
		/// Static type of a method call. Only metadata-backed answers are produced: the return
		/// type of a method found on the target type (or of Invoke when a delegate is called).
		/// </summary>
		string InvocationResultType(ICSharpCode.Decompiler.CSharp.Syntax.InvocationExpression invocation) {
			switch (invocation.Target) {
			case ICSharpCode.Decompiler.CSharp.Syntax.MemberReferenceExpression member:
				var targetType = GetTypeName(member.Target);
				if (targetType is null)
					return null;
				return MemberReturnTypeOf(targetType, member.MemberName);
			case ICSharpCode.Decompiler.CSharp.Syntax.IdentifierExpression identifier:
				var localType = LookupVariable(identifier.Identifier);
				if (localType != null) {
					// A local or parameter of delegate type is being invoked.
					var invokeResult = MemberReturnTypeOf(localType, "Invoke");
					if (invokeResult != null)
						return invokeResult;
				}
				// A plain method on the type being converted.
				return CurrentType is null ? null : MemberReturnTypeOfOn(CurrentType, identifier.Identifier, null);
			}
			return null;
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
			// 'ReadOnlyCollection<string>', 'ReadOnlyCollection`1<string>' and
			// 'ReadOnlyCollection(Of String)' are all the metadata type 'ReadOnlyCollection`1'.
			// Without stripping the arguments no lookup can match and the type stays unknown,
			// which used to turn a reference comparison into a value comparison (BC30452).
			var bare = StripGenericArgs(name, out var arity);
			if (result is null && IsValueTypeName(bare))
				result = false;
			else if (result is null) {
				var local = LookupLocalTypeDef(bare, arity) ?? LookupLocalTypeDef(name);
				if (local != null)
					result = !local.IsValueType;
				else {
					var external = LookupExternalType(bare, arity) ?? LookupExternalType(name);
					if (external != null)
						result = !external.IsValueType;
					else {
						var runtimeType = GetRuntimeType(bare) ?? GetRuntimeType(name);
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

		/// <summary>
		/// Removes generic arguments, accepting the C#, dnlib ('Foo`1&lt;...&gt;') and VB spellings,
		/// and returns the arity so the metadata name ('Foo`1') can be looked up.
		/// </summary>
		static string StripGenericArgs(string name, out int arity) {
			arity = 0;
			var result = name;
			var open = result.IndexOf('<');
			if (open >= 0) {
				arity = CountGenericArguments(result, open + 1, '<', '>');
				result = result.Substring(0, open);
			}
			else if ((open = result.IndexOf("(Of ", StringComparison.Ordinal)) >= 0) {
				arity = CountGenericArguments(result, open + 4, '(', ')');
				result = result.Substring(0, open);
			}
			// dnlib writes the arity into the name: ReadOnlyCollection`1<System.String>.
			var tick = result.LastIndexOf('`');
			if (tick >= 0 && int.TryParse(result.Substring(tick + 1), out var declared)) {
				arity = declared;
				result = result.Substring(0, tick);
			}
			return result;
		}

		static int CountGenericArguments(string text, int start, char open, char close) {
			int depth = 1, count = 1;
			for (int i = start; i < text.Length; i++) {
				var c = text[i];
				if (c == open)
					depth++;
				else if (c == close) {
					if (--depth == 0)
						break;
				}
				else if (c == ',' && depth == 1)
					count++;
			}
			return count;
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

		/// <summary>
		/// True/false when the member is known to be an event, null when the type cannot be
		/// resolved (types from assemblies that are not referenced here).
		/// </summary>
		public bool? HasEvent(string typeName, string memberName) {
			if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(memberName))
				return null;
			var name = Normalize(typeName);
			var local = LookupLocalTypeDef(name);
			if (local != null)
				return HasEventIn(local, memberName);
			var external = LookupExternalType(name);
			if (external != null)
				return HasEventIn(external, memberName);
			var runtime = GetRuntimeType(name);
			if (runtime is null)
				return null;
			foreach (var type in SelfAndInterfaces(runtime)) {
				foreach (var ev in type.GetEvents())
					if (ev.Name == memberName)
						return true;
			}
			return false;
		}

		// Events are often declared on a base type or a base interface (ITextView.Closed), so the
		// lookup follows both hierarchies. A base that lives in another module cannot be walked
		// (ResolveTypeDef only reaches the same module); that is "unknown", not "no": treating
		// it as false made IsEventAccess reject a real event subscription (MyBase.Loaded with an
		// external ButtonBase in the chain) and emit "+=" instead of AddHandler.
		static bool? HasEventIn(TypeDef type, string memberName) {
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var queue = new Queue<TypeDef>();
			bool complete = true;
			queue.Enqueue(type);
			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current is null || !visited.Add(current.FullName))
					continue;
				foreach (var ev in current.Events) {
					if (ev.Name == memberName)
						return true;
				}
				var baseType = ResolveSameModule(current.BaseType);
				if (baseType != null)
					queue.Enqueue(baseType);
				else if (current.BaseType != null)
					complete = false;
				foreach (var iface in current.Interfaces) {
					var resolved = ResolveSameModule(iface.Interface);
					if (resolved != null)
						queue.Enqueue(resolved);
					else if (iface.Interface != null)
						complete = false;
				}
			}
			return complete ? false : (bool?)null;
		}

		/// <summary>
		/// Resolves a base/interface reference when the target is in this module. A generic
		/// instance ("Base&lt;T&gt;") points at the generic definition through its signature, so
		/// the arity is unwrapped before the resolve.
		/// </summary>
		static TypeDef? ResolveSameModule(ITypeDefOrRef? type) {
			if (type is null)
				return null;
			var resolved = type.ResolveTypeDef();
			if (resolved != null)
				return resolved;
			if (type is TypeSpec spec && spec.TypeSig?.RemovePinnedAndModifiers() is GenericInstSig generic)
				return generic.GenericType?.TypeDefOrRef?.ResolveTypeDef();
			return null;
		}

		static bool? HasEventIn(Mono.Cecil.TypeDefinition type, string memberName) {
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var queue = new Queue<Mono.Cecil.TypeDefinition>();
			bool complete = true;
			queue.Enqueue(type);
			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current is null || !visited.Add(current.FullName))
					continue;
				foreach (var ev in current.Events) {
					if (ev.Name == memberName)
						return true;
				}
				var baseType = current.BaseType?.Resolve();
				if (baseType != null)
					queue.Enqueue(baseType);
				else if (current.BaseType != null)
					complete = false;
				foreach (var iface in current.Interfaces) {
					var resolved = iface.InterfaceType?.Resolve();
					if (resolved != null)
						queue.Enqueue(resolved);
					else if (iface.InterfaceType != null)
						complete = false;
				}
			}
			return complete ? false : (bool?)null;
		}

		static IEnumerable<Type> SelfAndInterfaces(Type type) {
			for (var current = type; current != null; current = current.BaseType)
				yield return current;
			foreach (var iface in type.GetInterfaces())
				yield return iface;
		}

		/// <summary>
		/// True when the type is a delegate, i.e. "New T(AddressOf M)" can be written as just
		/// "AddressOf M". Everything else must keep its constructor call.
		/// </summary>
		public bool IsDelegateType(string typeName) {
			if (string.IsNullOrEmpty(typeName))
				return false;
			var name = Normalize(typeName);
			// The syntax tree names constructed delegates ('EventHandler<E>', 'Func<A, B>');
			// the metadata only has the generic definition ('EventHandler`1'). StripGenericArgs
			// yields both the bare name and the arity so every lookup below can match.
			var bare = StripGenericArgs(name, out var arity);
			var local = LookupLocalTypeDef(bare, arity) ?? LookupLocalTypeDef(name);
			if (local != null)
				return IsMulticastDelegate(local.BaseType?.FullName);
			var external = LookupExternalType(bare, arity) ?? LookupExternalType(name);
			if (external != null)
				return IsMulticastDelegate(external.BaseType?.FullName);
			var runtimeType = GetRuntimeType(bare) ?? GetRuntimeType(name);
			for (var type = runtimeType?.BaseType; type != null; type = type.BaseType) {
				if (type.FullName == "System.MulticastDelegate")
					return true;
			}
			return false;
		}

		static bool IsMulticastDelegate(string baseTypeName) =>
			baseTypeName == "System.MulticastDelegate" || baseTypeName == "System.Delegate";

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
				// Type.GetType resolves the compiler alias "System.Nullable" to a special
				// runtime type whose IsValueType is false. Nullable<T> itself is a struct:
				// misreading it as a reference type turned nullable comparisons into Is/IsNot
				// (BC32128). Remap to the arity name, which resolves to the real struct.
				if (name == "System.Nullable" || name == "Nullable")
					name = "System.Nullable`1";
				return Type.GetType(name, throwOnError: false);
			}
			catch {
				return null;
			}
		}

		/// <summary>Type of a member of an explicitly named type.</summary>
		string MemberTypeOf(string typeName, string memberName) {
			var name = Normalize(typeName);
			// Members live on the generic type definition, not on the constructed type.
			var bare = StripGenericArgs(name, out var arity);
			var local = LookupLocalTypeDef(bare, arity) ?? LookupLocalTypeDef(name);
			if (local != null) {
				var localMember = FindMemberType(local, memberName);
				if (localMember != null)
					return localMember;
			}

			// Types from other assemblies (dnlib.DotNet.MethodDef, ...) resolve through cecil.
			var external = LookupExternalType(bare, arity) ?? LookupExternalType(name);
			return external is null ? null : FindMemberType(external, memberName);
		}

		/// <summary>
		/// Return type of a method (or of a delegate's Invoke when a delegate field/property is
		/// invoked) on the given type. This is what 'x.M(...) == y' needs on the call side.
		/// </summary>
		string MemberReturnTypeOf(string typeName, string memberName) {
			var name = Normalize(typeName);
			var bare = StripGenericArgs(name, out var arity);
			var local = LookupLocalTypeDef(bare, arity) ?? LookupLocalTypeDef(name);
			if (local != null) {
				var result = MemberReturnTypeOfOn(local, memberName, name);
				if (result != null)
					return result;
			}
			var external = LookupExternalType(bare, arity) ?? LookupExternalType(name);
			if (external != null) {
				var result = MemberReturnTypeOfOnExternal(external, memberName, name);
				if (result != null)
					return result;
			}
			return null;
		}

		string MemberReturnTypeOfOn(TypeDef type, string memberName, string constructedName) {
			// Methods come first: invoking a name means a method group unless a delegate field
			// with the same name shadows it (the field case is handled below).
			var methodResult = FindMethodReturnType(type, memberName);
			if (methodResult != null)
				return SubstituteGenericArgument(type, methodResult, constructedName);
			var fieldResult = FindMemberType(type, memberName);
			if (fieldResult is null)
				return null;
			if (IsDelegateType(fieldResult))
				return MemberReturnTypeOf(fieldResult, "Invoke");
			return null;
		}

		string MemberReturnTypeOfOnExternal(Mono.Cecil.TypeDefinition type, string memberName, string constructedName) {
			foreach (var typeOrBase in SelfAndBases(type)) {
				foreach (var method in typeOrBase.Methods) {
					if (method.Name == memberName && method.ReturnType != null)
						return SubstituteGenericArgument(typeOrBase, method.ReturnType.FullName, constructedName);
				}
			}
			return null;
		}

		static IEnumerable<Mono.Cecil.TypeDefinition> SelfAndBases(Mono.Cecil.TypeDefinition type) {
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var queue = new Queue<Mono.Cecil.TypeDefinition>();
			queue.Enqueue(type);
			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current is null || !visited.Add(current.FullName))
					continue;
				yield return current;
				var baseType = TryResolve(current.BaseType);
				if (baseType != null)
					queue.Enqueue(baseType);
				foreach (var iface in current.Interfaces) {
					var resolved = TryResolve(iface.InterfaceType);
					if (resolved != null)
						queue.Enqueue(resolved);
				}
			}
		}

		/// <summary>Method return type on the type or any of its bases/interfaces, as a short name.</summary>
		static string FindMethodReturnType(TypeDef type, string memberName) {
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var queue = new Queue<TypeDef>();
			queue.Enqueue(type);
			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current is null || !visited.Add(current.FullName))
					continue;
				foreach (var method in current.Methods) {
					if (method.Name == memberName && method.MethodSig != null)
						return method.MethodSig.RetType?.RemovePinnedAndModifiers()?.TypeName;
				}
				var baseType = current.BaseType?.ResolveTypeDef();
				if (baseType != null)
					queue.Enqueue(baseType);
				foreach (var iface in current.Interfaces) {
					var resolved = iface.Interface?.ResolveTypeDef();
					if (resolved != null)
						queue.Enqueue(resolved);
				}
			}
			return null;
		}

		/// <summary>
		/// Replaces a generic parameter name ('T', 'TKey') from a generic definition with the
		/// corresponding argument of the constructed type name the member was accessed through.
		/// </summary>
		string SubstituteGenericArgument(TypeDef definitionType, string memberTypeName, string constructedName) {
			if (memberTypeName is null || constructedName is null)
				return memberTypeName;
			var index = IndexOfGenericParameter(definitionType.GenericParameters, memberTypeName);
			if (index < 0)
				return memberTypeName;
			var args = ParseGenericArgs(constructedName);
			return index < args.Count ? args[index] : memberTypeName;
		}

		string SubstituteGenericArgument(Mono.Cecil.TypeDefinition definitionType, string memberTypeName, string constructedName) {
			if (memberTypeName is null || constructedName is null)
				return memberTypeName;
			var index = IndexOfGenericParameter(definitionType.GenericParameters, memberTypeName);
			if (index < 0)
				return memberTypeName;
			var args = ParseGenericArgs(constructedName);
			return index < args.Count ? args[index] : memberTypeName;
		}

		static int IndexOfGenericParameter(System.Collections.Generic.IList<GenericParam> parameters, string name) {
			for (int i = 0; i < parameters.Count; i++) {
				if (parameters[i].Name == name)
					return i;
			}
			return -1;
		}

		static int IndexOfGenericParameter(System.Collections.Generic.IList<Mono.Cecil.GenericParameter> parameters, string name) {
			for (int i = 0; i < parameters.Count; i++) {
				if (parameters[i].Name == name)
					return i;
			}
			return -1;
		}

		/// <summary>
		/// Splits the arguments of a constructed type name: 'Lazy&lt;A, B&gt;',
		/// 'System.Lazy`1&lt;A&gt;' and 'Lazy(Of A)' all yield [A].
		/// </summary>
		static System.Collections.Generic.List<string> ParseGenericArgs(string name) {
			var result = new System.Collections.Generic.List<string>();
			int start = -1, end = -1;
			char open, close;
			var angle = name.IndexOf('<');
			if (angle >= 0) {
				open = '<'; close = '>'; start = angle + 1;
			} else {
				var of = name.IndexOf("(Of ", StringComparison.Ordinal);
				if (of >= 0) {
					open = '('; close = ')'; start = of + 4;
				} else {
					return result;
				}
			}
			int depth = 1;
			int argStart = start;
			for (int i = start; i < name.Length; i++) {
				var c = name[i];
				if (c == open)
					depth++;
				else if (c == close) {
					if (--depth == 0) { end = i; break; }
				}
				if (c == ',' && depth == 1) {
					result.Add(name.Substring(argStart, i - argStart).Trim());
					argStart = i + 1;
				}
			}
			if (end > argStart)
				result.Add(name.Substring(argStart, end - argStart).Trim());
			return result;
		}

		// Members are inherited: dnlib's IAssembly declares no 'Name' of its own, it comes from a
		// base interface, so a lookup that only walks the declared members finds nothing.
		static string FindMemberType(TypeDef type, string memberName) {
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var queue = new Queue<TypeDef>();
			queue.Enqueue(type);
			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current is null || !visited.Add(current.FullName))
					continue;
				foreach (var field in current.Fields) {
					if (field.Name == memberName)
						return field.FieldType?.TypeName;
				}
				foreach (var property in current.Properties) {
					if (property.Name == memberName)
						return property.PropertySig?.RetType?.TypeName;
				}
				var baseType = current.BaseType?.ResolveTypeDef();
				if (baseType != null)
					queue.Enqueue(baseType);
				foreach (var iface in current.Interfaces) {
					var resolved = iface.Interface?.ResolveTypeDef();
					if (resolved != null)
						queue.Enqueue(resolved);
				}
			}
			return null;
		}

		static string FindMemberType(Mono.Cecil.TypeDefinition type, string memberName) {
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var queue = new Queue<Mono.Cecil.TypeDefinition>();
			queue.Enqueue(type);
			while (queue.Count > 0) {
				var current = queue.Dequeue();
				if (current is null || !visited.Add(current.FullName))
					continue;
				foreach (var field in current.Fields) {
					if (field.Name == memberName)
						return field.FieldType?.FullName;
				}
				foreach (var property in current.Properties) {
					if (property.Name == memberName)
						return property.PropertyType?.FullName;
				}
				var baseType = TryResolve(current.BaseType);
				if (baseType != null)
					queue.Enqueue(baseType);
				foreach (var iface in current.Interfaces) {
					var resolved = TryResolve(iface.InterfaceType);
					if (resolved != null)
						queue.Enqueue(resolved);
				}
			}
			return null;
		}

		static Mono.Cecil.TypeDefinition TryResolve(Mono.Cecil.TypeReference reference) {
			try {
				return reference?.Resolve();
			}
			catch {
				return null;
			}
		}

		TypeDef LookupLocalTypeDef(string name, int arity = 0) {
			var genericName = arity > 0 ? name + "`" + arity : null;
			foreach (var type in module.GetTypes()) {
				if (type.FullName == name || type.Name == name)
					return type;
				// Generic types carry the arity in their metadata name.
				if (genericName != null && (type.FullName == genericName || type.Name == genericName))
					return type;
			}
			return null;
		}

		Mono.Cecil.TypeDefinition LookupExternalType(string name, int arity = 0) {
			var cacheKey = arity > 0 ? name + "`" + arity : name;
			if (externalTypes.TryGetValue(cacheKey, out var cached))
				return cached;
			var genericName = arity > 0 ? name + "`" + arity : null;
			Mono.Cecil.TypeDefinition result = null;
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule != null) {
				result = cecilModule.GetType(name);
				if (result is null && genericName != null)
					result = cecilModule.GetType(genericName);
				if (result is null) {
					foreach (var assemblyRef in cecilModule.AssemblyReferences) {
						var referenced = ResolveAssembly(assemblyRef);
						if (referenced is null)
							continue;
						result = referenced.MainModule.GetType(name);
						if (result is null && genericName != null)
							result = referenced.MainModule.GetType(genericName);
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
							if (type.Name == name || (genericName != null && type.Name == genericName)) {
								result = type;
								break;
							}
						}
						if (result != null)
							break;
					}
				}
				// BCL facades type-forward most of the framework: 'ThreadStart' and 'Action' are
				// defined in System.Private.CoreLib and show up as ExportedTypes (not Types) in
				// every facade dnSpy references, so the scans above never see them. Follow the
				// forwarder to its target; the exported full name must match exactly and a bare
				// name that reaches more than one definition stays unknown rather than guessing.
				// The qualified form is what constructed BCL delegates arrive as
				// ('System.EventHandler`1'); restricting it to 'System.*' keeps the extra scan
				// off the hot path of ordinary unknown application types.
				if (result is null && (name.IndexOf('.') < 0 || name.StartsWith("System.", StringComparison.Ordinal)))
					result = LookupForwardedType(name, genericName, cecilModule);
			}
			externalTypes[cacheKey] = result;
			return result;
		}

		Mono.Cecil.TypeDefinition LookupForwardedType(string name, string genericName, Mono.Cecil.ModuleDefinition cecilModule) {
			// An arity-bearing request ('EventHandler`1') also matches the non-generic
			// 'EventHandler' by simple name, which used to make the answer ambiguous and cost
			// the whole lookup. The arity must therefore be part of the match; either side may
			// be qualified ('System.EventHandler`1') or bare ('EventHandler`1').
			var found = new List<Mono.Cecil.TypeDefinition>();
			try {
				foreach (var assemblyRef in cecilModule.AssemblyReferences) {
					var referenced = ResolveAssembly(assemblyRef);
					var module = referenced?.MainModule;
					if (module is null)
						continue;
					foreach (var exported in module.ExportedTypes) {
						var matches = genericName != null
							? exported.FullName == genericName || exported.Name == genericName
							: exported.FullName == name || exported.Name == name;
						if (!matches)
							continue;
						var targetModule = ResolveAssembly(exported.Scope as Mono.Cecil.AssemblyNameReference)?.MainModule;
						var targetType = targetModule?.GetType(exported.FullName);
						if (targetType != null)
							found.Add(targetType);
					}
				}
			}
			catch {
				return null;
			}
			var fullNames = found.Select(t => t.FullName).Distinct().ToList();
			return fullNames.Count == 1 ? found[0] : null;
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
