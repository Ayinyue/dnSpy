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
		/// <summary>
		/// Per-module name index. Building it walks the module three times, so it is cached with the
		/// module's lifetime; decompiling a type's members repeatedly used to rebuild it every time.
		/// </summary>
		sealed class ModuleIndex {
			public readonly Dictionary<string, TypeDef> Types;
			public readonly Dictionary<string, TypeDef> BySimpleName;
			public readonly HashSet<string> AmbiguousNames;

			public ModuleIndex(Dictionary<string, TypeDef> types, Dictionary<string, TypeDef> bySimpleName, HashSet<string> ambiguousNames) {
				Types = types;
				BySimpleName = bySimpleName;
				AmbiguousNames = ambiguousNames;
			}
		}

		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModuleDef, ModuleIndex> moduleIndexes = new System.Runtime.CompilerServices.ConditionalWeakTable<ModuleDef, ModuleIndex>();

		static ModuleIndex GetIndex(ModuleDef module) => moduleIndexes.GetValue(module, m => BuildIndex(m));

		static ModuleIndex BuildIndex(ModuleDef module) {
			var types = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes())
				types[type.FullName] = type;
			// The syntax tree names a generic type without the arity suffix the metadata carries
			// ("BulkObservableCollection" vs "BulkObservableCollection`1"), so a generic type used
			// to bind nothing at all - no MethodDef, no EventDef, which silently disabled every
			// annotation-based feature for it. Register the tree spelling too, but only where it
			// cannot shadow a real type of that name.
			foreach (var type in module.GetTypes()) {
				var stripped = StripTypeArity(type.FullName);
				if (stripped != type.FullName && !types.ContainsKey(stripped))
					types[stripped] = type;
			}

			// A nested type is dumped on its own, so its declaring types are not in the tree and the
			// computed full name is just the short name. Fall back to a unique simple-name match.
			var bySimpleName = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
			var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes()) {
				var simpleName = type.Name?.String;
				if (string.IsNullOrEmpty(simpleName))
					continue;
				if (bySimpleName.ContainsKey(simpleName))
					ambiguousNames.Add(simpleName);
				else
					bySimpleName[simpleName] = type;
			}
			return new ModuleIndex(types, bySimpleName, ambiguousNames);
		}

		public static void Attach(ModuleDef module, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree) =>
			Attach(module, tree, null);

		public static void Attach(ModuleDef module, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree, TypeDef? knownType) {
			if (module is null || tree is null)
				return;
			var index = GetIndex(module);
			var types = index.Types;
			var bySimpleName = index.BySimpleName;
			var ambiguousNames = index.AmbiguousNames;

			// The caller knows exactly which type is being decompiled (dnSpy passes the TypeDef);
			// bind it and its nested types directly by name, bypassing the simple-name ambiguity
			// fallback that fails for nested types with common names (two ScrollDirection enums).
			Dictionary<string, TypeDef>? knownTypes = null;
			if (knownType is not null) {
				knownTypes = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
				void AddKnown(TypeDef type) {
					if (type.Name is not null)
						knownTypes[type.Name.String] = type;
					foreach (var nested in type.NestedTypes)
						AddKnown(nested);
				}
				AddKnown(knownType);
			}

			var renames = new Dictionary<string, string>(StringComparer.Ordinal);
			var methodNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			foreach (var typeDeclaration in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>()) {
				TypeDef? typeDef = null;
				if (knownTypes is not null && typeDeclaration.Name is not null) {
					// The syntax tree omits the arity; the metadata TypeDef.Name carries it
					// ("MenuItemBase" vs "MenuItemBase`1"). The bare-name lookup made a generic
					// type bind to a non-generic sibling of the same name.
					var key = typeDeclaration.Name;
					if (typeDeclaration.TypeParameters.Count > 0)
						key = key + "`" + typeDeclaration.TypeParameters.Count;
					knownTypes.TryGetValue(key, out typeDef);
				}
				if (typeDef is null) {
					var fullName = GetFullTypeName(typeDeclaration);
					if (fullName is null || !types.TryGetValue(fullName, out typeDef)) {
						if (!bySimpleName.TryGetValue(typeDeclaration.Name, out typeDef) || ambiguousNames.Contains(typeDeclaration.Name))
							continue;
					}
				}
				typeDeclaration.AddAnnotation(typeDef);
				AttachMembers(typeDef, typeDeclaration);
				CollectIdentifierRenames(typeDef, typeDeclaration, renames);
				RegisterMethodNames(typeDef, typeDeclaration, methodNames);
			}

			// Single-member decompilations (DecompileMember) have a top-level
			// MethodDeclaration/PropertyDeclaration/EventDeclaration instead of a type
			// declaration - the known type IS their declaring type. Without this the declared
			// name never got its MethodDef and the member-level debug info was never built
			// (member-level views render zero debug infos; the debugger's single-method
			// decompilation path consumes exactly this).
			if (knownType is not null && tree.Members.Count > 0 &&
					!tree.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>().Any()) {
				foreach (var member in tree.Members) {
					switch (member) {
					case ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration method:
						var methodDef = MatchMethod(knownType, method);
						if (methodDef is not null)
							method.AddAnnotation(methodDef);
						break;
					case ICSharpCode.Decompiler.CSharp.Syntax.ConstructorDeclaration constructor:
						var ctorDef = MatchConstructor(knownType, constructor);
						if (ctorDef is not null)
							constructor.AddAnnotation(ctorDef);
						break;
					case ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration property:
						var propertyDef = MatchProperty(knownType, property.Name, 0, property.PrivateImplementationType);
						if (propertyDef is not null)
							property.AddAnnotation(propertyDef);
						break;
					case ICSharpCode.Decompiler.CSharp.Syntax.EventDeclaration eventDecl:
						var eventDef = MatchEvent(knownType, eventDecl.Name, wantsQualified: false);
						if (eventDef is not null)
							eventDecl.AddAnnotation(eventDef);
						break;
					}
				}
			}

			if (renames.Count > 0)
				tree.AddAnnotation(renames);
			if (methodNames.Count > 0)
				tree.AddAnnotation(methodNames);
		}

		/// <summary>
		/// Records the method names per type. The converter needs them to decide whether a bare
		/// member reference is a method group (VB requires AddressOf) or a field/property.
		/// </summary>
		static void RegisterMethodNames(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration declaration,
				Dictionary<string, HashSet<string>> methodNames) {
			var names = new HashSet<string>(declaration.Members
				.OfType<ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration>()
				.Select(m => m.Name), StringComparer.Ordinal);
			if (names.Count == 0)
				return;
			foreach (var key in new string[] { typeDef.Name?.String, typeDef.FullName }) {
				if (string.IsNullOrEmpty(key))
					continue;
				if (!methodNames.TryGetValue(key, out var existing))
					methodNames[key] = existing = new HashSet<string>(StringComparer.Ordinal);
				existing.UnionWith(names);
			}
		}

		/// <summary>
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

		/// <summary>Removes the metadata arity suffix from every segment of a type full name.</summary>
		static string StripTypeArity(string fullName) {
			if (fullName.IndexOf('`') < 0)
				return fullName;
			var builder = new System.Text.StringBuilder(fullName.Length);
			for (int i = 0; i < fullName.Length; i++) {
				if (fullName[i] == '`') {
					// The arity is already expressed by the type parameters of the declaration.
					while (i + 1 < fullName.Length && char.IsDigit(fullName[i + 1]))
						i++;
					continue;
				}
				builder.Append(fullName[i]);
			}
			return builder.ToString();
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
			// The metadata full name carries the arity and the type parameter names
			// ("MenuItemBase`1[TContext]"); the syntax tree only carries "MenuItemBase".
			// Without the arity a generic type collides with a non-generic sibling of the
			// same name (MenuItemBase / MenuItemBase<TContext>) and binds to the wrong TypeDef.
			if (declaration.TypeParameters.Count > 0)
				typeName += "`" + declaration.TypeParameters.Count + "[" + string.Join(",", declaration.TypeParameters.Select(tp => tp.Name)) + "]";
			return string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
		}

		static void AttachMembers(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration declaration) {
			foreach (var method in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration>()) {
				// 由 .override 指令合成的转发方法没有自己的 MethodDef：按名字匹配会绑到被实现的
				// 方法上，把那个方法的 token 注释印两遍（audit 8.79）。
				if (method.Annotations.Any(a => ReferenceEquals(a, ICSharpCode.Decompiler.CSharp.CSharpDecompiler.SyntheticExplicitImplementationMarker)))
					continue;
				var methodDef = MatchMethod(typeDef, method);
				if (methodDef != null)
					method.AddAnnotation(methodDef);
			}

			// Constructors, destructors and operators are member bodies too: without a MethodDef
			// annotation the converter ran their bodies with CurrentType=null, which silently
			// disabled every resolver-based decision inside them (event subscriptions stayed
			// "+=" instead of AddHandler, reference comparisons fell back to '='). The metadata
			// names are ".ctor"/".cctor"/"op_*", which MatchMethod's qualified-name rule skips,
			// so each gets its own matcher.
			foreach (var constructor in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.ConstructorDeclaration>()) {
				var methodDef = MatchConstructor(typeDef, constructor);
				if (methodDef != null)
					constructor.AddAnnotation(methodDef);
			}

			foreach (var destructor in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.DestructorDeclaration>()) {
				var methodDef = MatchDestructor(typeDef, destructor);
				if (methodDef != null)
					destructor.AddAnnotation(methodDef);
			}

			foreach (var operatorDeclaration in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.OperatorDeclaration>()) {
				var methodDef = MatchOperator(typeDef, operatorDeclaration);
				if (methodDef != null)
					operatorDeclaration.AddAnnotation(methodDef);
			}

			foreach (var property in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration>()) {
				var propertyDef = MatchProperty(typeDef, property.Name, 0, property.PrivateImplementationType);
				if (propertyDef != null)
					property.AddAnnotation(propertyDef);
			}

			foreach (var indexer in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.IndexerDeclaration>()) {
				var paramNames = indexer.Parameters.Select(p => NormalizeParameterTypeName(GetAstTypeName(p.Type) ?? p.Type.ToString())).ToList();
				var propertyDef = MatchProperty(typeDef, "Item", indexer.Parameters.Count, indexer.PrivateImplementationType, paramNames);
				if (propertyDef != null)
					indexer.AddAnnotation(propertyDef);
			}

			foreach (var eventDeclaration in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.CustomEventDeclaration>()) {
				var eventDef = MatchEvent(typeDef, eventDeclaration.Name,
					eventDeclaration.PrivateImplementationType is { IsNull: false });
				if (eventDef != null)
					eventDeclaration.AddAnnotation(eventDef);
			}

			// Field declaration names: the variable initializer reads the FieldDeclaration's
			// FieldDef for the definition reference on the declared name (2.3 emitted
			// FieldDefMD there). Multi-declarator statements match per name in the converter.
			foreach (var field in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration>()) {
				foreach (var variable in field.Variables) {
					var fieldDef = typeDef.Fields.FirstOrDefault(f => f.Name == variable.Name);
					if (fieldDef is not null)
						field.AddAnnotation(fieldDef);
				}
			}

			// Field-like events ("public event EventHandler Changed;") are EventDeclaration nodes,
			// and the Implements clause for them needs the EventDef just as much. They have no
			// private-implementation syntax (explicit interface events always carry accessors,
			// i.e. CustomEventDeclaration), so they only bind the plain name.
			foreach (var eventDeclaration in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.EventDeclaration>()) {
				foreach (var variable in eventDeclaration.Variables) {
					var eventDef = MatchEvent(typeDef, variable.Name, wantsQualified: false);
					if (eventDef != null)
						eventDeclaration.AddAnnotation(eventDef);
				}
			}

			// Accessor bodies run with the owning member's state: ResetMethodState reads the
			// accessor's MethodDef for CurrentType, and the synthesized setter's "value"
			// parameter claims its dnlib parameter through the same annotation. The accessors of
			// an already-matched property/event follow from its definition, so no separate
			// matching pass is needed (explicit implementations too - their accessors live on
			// the PropertyDef/EventDef itself).
			foreach (var property in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration>()) {
				var propertyDef = property.Annotation<PropertyDef>();
				if (propertyDef is null)
					continue;
				AttachAccessor(propertyDef.GetMethod, property.Getter);
				AttachAccessor(propertyDef.SetMethod, property.Setter);
			}

			foreach (var indexer in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.IndexerDeclaration>()) {
				var propertyDef = indexer.Annotation<PropertyDef>();
				if (propertyDef is null)
					continue;
				AttachAccessor(propertyDef.GetMethod, indexer.Getter);
				AttachAccessor(propertyDef.SetMethod, indexer.Setter);
			}

			foreach (var customEvent in declaration.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.CustomEventDeclaration>()) {
				var eventDef = customEvent.Annotation<EventDef>();
				if (eventDef is null)
					continue;
				AttachAccessor(eventDef.AddMethod, customEvent.AddAccessor);
				AttachAccessor(eventDef.RemoveMethod, customEvent.RemoveAccessor);
			}
		}

		static void AttachAccessor(MethodDef? accessor, ICSharpCode.Decompiler.CSharp.Syntax.Accessor node) {
			if (accessor is null || node is null || node.IsNull)
				return;
			node.AddAnnotation(accessor);
		}

		static MethodDef? MatchConstructor(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.ConstructorDeclaration declaration) {
			bool wantsStatic = declaration.HasModifier(ICSharpCode.Decompiler.CSharp.Syntax.Modifiers.Static);
			MethodDef? fallback = null;
			foreach (var method in typeDef.Methods) {
				bool staticConstructor = method.Name == ".cctor";
				if (staticConstructor != wantsStatic)
					continue;
				if (!staticConstructor && method.Name != ".ctor")
					continue;
				var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
				if (parameters.Count != declaration.Parameters.Count)
					continue;
				if (ParameterTypesMatch(parameters, declaration.Parameters))
					return method;
				// A same-arity constructor with different parameter types; keep the first
				// candidate as the fallback, same as MatchMethod.
				fallback ??= method;
			}
			return fallback;
		}

		static MethodDef? MatchDestructor(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.DestructorDeclaration declaration) {
			// A C# finalizer compiles to the single virtual parameterless Finalize override; a
			// non-virtual Finalize helper must not claim it. The node is a member of the very
			// typeDef that was matched for the enclosing declaration, so no name comparison is
			// needed (it would miss generic types, whose tree name lacks the "`1" arity), and
			// the destructor syntax has no parameter list to compare against.
			foreach (var method in typeDef.Methods) {
				if (method.IsStatic || !method.IsVirtual)
					continue;
				if (method.Name != "Finalize")
					continue;
				var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
				if (parameters.Count == 0)
					return method;
			}
			return null;
		}

		static MethodDef? MatchOperator(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.OperatorDeclaration declaration) {
			MethodDef? parameterMatch = null;
			foreach (var method in typeDef.Methods) {
				var name = method.Name?.String;
				if (name is null || !name.StartsWith("op_", StringComparison.Ordinal))
					continue;
				// op_Implicit/op_Explicit are shared by every conversion operator, so the
				// parameter and return types decide; unresolvable spellings keep the candidate.
				if (CSOperatorType(method.Name) != declaration.OperatorType)
					continue;
				if (!method.IsStatic)
					continue;
				var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
				if (parameters.Count != declaration.Parameters.Count)
					continue;
				if (ParameterTypesMatch(parameters, declaration.Parameters) && ReturnTypeMatches(method, declaration))
					return method;
				parameterMatch ??= method;
			}
			return parameterMatch;
		}

		/// <summary>Maps an "op_*" metadata name to its operator type, null when unknown.</summary>
		static ICSharpCode.Decompiler.CSharp.Syntax.OperatorType? CSOperatorType(UTF8String? name) =>
			ICSharpCode.Decompiler.CSharp.Syntax.OperatorDeclaration.GetOperatorType(name?.String);

		/// <summary>
		/// Exact name wins over the qualified form ("IFoo.Bar" ends with ".Bar"): an override
		/// property and an explicit interface implementation can share the short name, and giving
		/// the qualified definition to both syntax nodes made the override claim the interface
		/// member too (BC30401). Explicit implementations are only matched against the qualified
		/// name, their plain-named twin must not be picked for them.
		/// </summary>
		static PropertyDef MatchProperty(TypeDef typeDef, string name, int parameterCount,
				ICSharpCode.Decompiler.CSharp.Syntax.AstType privateImplementationType,
				List<string> parameterTypeNames = null) {
			bool wantsQualified = privateImplementationType is { IsNull: false };
			// 元数据里显式实现的属性名是「接口全名.成员名」，可能同时存在
			// IEnumerator<IDsDocument>.Current 与 IEnumerator.Current 这种只差泛型/非泛型的
			// 同名成员。只按 ".成员名" 匹配会把两者绑到同一个 PropertyDef 上（token 重复）。
			var ifaceName = wantsQualified ? GetSimpleName(privateImplementationType) : null;
			bool ifaceIsGeneric = wantsQualified && IsGeneric(privateImplementationType);
			PropertyDef exact = null, suffix = null;
			foreach (var property in typeDef.Properties) {
				if (property.PropertySig.Params.Count != parameterCount)
					continue;
				// 索引器（Item）必须连参数类型一起比：this[int] 与 this[long] 个数相同，
				// 只看个数会把两者绑到同一个 PropertyDef（audit 8.87）。
				if (parameterTypeNames != null && !PropertyParameterTypesMatch(property, parameterTypeNames))
					continue;
				if (property.Name == name)
					exact ??= property;
				else if (property.Name.EndsWith("." + name, StringComparison.Ordinal)) {
					if (ifaceName != null && !InterfaceMatches(property.Name, name, ifaceName, ifaceIsGeneric))
						continue;
					suffix ??= property;
				}
			}
			var result = wantsQualified ? suffix ?? exact : exact ?? suffix;
			if (result == null && parameterTypeNames != null) {
				// 没有类型相符的候选时退回只按名字+个数匹配（例如泛型参数 T 对 !T 拼不上）。
				return MatchProperty(typeDef, name, parameterCount, privateImplementationType);
			}
			return result;
		}

		/// <summary>索引器的参数类型是否与语法声明一致（用 <see cref="NormalizeParameterTypeName"/> 归一）。</summary>
		static bool PropertyParameterTypesMatch(PropertyDef property, List<string> syntaxNames) {
			for (int i = 0; i < syntaxNames.Count; i++) {
				var meta = property.PropertySig.Params[i]?.ToString();
				if (!string.Equals(NormalizeParameterTypeName(meta), syntaxNames[i], StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		/// <summary>
		/// 取语法类型（如 <c>IEnumerator&lt;IDsDocument&gt;</c>）的接口简单名与是否泛型。
		/// 用 <see cref="object.ToString"/> 解析，避开语法树里各种 AstType 子类的名字解析差异。
		/// </summary>
		static string GetSimpleName(ICSharpCode.Decompiler.CSharp.Syntax.AstType type) {
			var s = type.ToString();
			var lt = s.IndexOf('<');
			var dot = s.LastIndexOf('.', lt >= 0 ? lt : s.Length - 1);
			return lt >= 0 ? s.Substring(dot + 1, lt - dot - 1) : s.Substring(dot + 1);
		}

		static bool IsGeneric(ICSharpCode.Decompiler.CSharp.Syntax.AstType type) {
			return type.ToString().IndexOf('<') >= 0;
		}

		/// <summary>
		/// 元数据名形如 <c>Ns.IEnumerator&lt;Args&gt;.Current</c>：接口段的简单名与泛型性必须与
		/// 语法类型一致，否则会把 <c>IEnumerator.Current</c> 错配给
		/// <c>IEnumerator&lt;T&gt;.Current</c>。
		/// </summary>
		static bool InterfaceMatches(string metadataName, string name, string ifaceName, bool ifaceIsGeneric) {
			var iface = metadataName.Substring(0, metadataName.Length - name.Length - 1);
			// 泛型实参里可能含 '.'（如 IEnumerator<dnSpy.Contracts.Documents.IDsDocument>），
			// 所以要在 '<' 之前找最后一个 '.'，不能直接 LastIndexOf('.')。
			var lt = iface.IndexOf('<');
			var end = lt >= 0 ? lt : iface.Length;
			var dot = iface.LastIndexOf('.', end - 1);
			var simple = iface.Substring(dot + 1, end - dot - 1);
			return simple == ifaceName && (lt >= 0) == ifaceIsGeneric;
		}

		/// <summary>
		/// Exact name wins over the qualified form ("IWpfTextView.BackgroundBrushChanged" ends
		/// with ".BackgroundBrushChanged"): an explicit interface event implementation's EventDef
		/// is named after the interface in metadata, and without accepting the qualified spelling
		/// the event bound nothing - no EventDef, no RaiseEvent synthesis, no Implements clause.
		/// </summary>
		static EventDef MatchEvent(TypeDef typeDef, string name, bool wantsQualified) {
			EventDef? exact = null, suffix = null;
			foreach (var ev in typeDef.Events) {
				if (ev.Name == name)
					exact ??= ev;
				else if (ev.Name.EndsWith("." + name, StringComparison.Ordinal))
					suffix ??= ev;
			}
			return wantsQualified ? suffix ?? exact : exact ?? suffix;
		}

		/// <summary>
		/// Matches by name and parameter count, preferring the full signature. Explicit interface
		/// implementations are usually qualified in metadata ("IFoo.Bar") while the syntax tree
		/// keeps "Bar", but the .override can also live on a plain-named method — so an explicit
		/// node accepts both spellings and the full signature decides. A non-explicit node never
		/// binds a qualified method: two methods can share name and parameters and differ only by
		/// return type (a private helper next to an explicit implementation), and binding both
		/// syntax nodes to one definition made both claim the interface member (BC30401).
		/// </summary>
		static MethodDef MatchMethod(TypeDef typeDef, ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration declaration) {
			bool wantsQualified = declaration.PrivateImplementationType is { IsNull: false };
			// 同 MatchProperty：元数据里 IEnumerable<IDsDocument>.GetEnumerator 与
			// IEnumerable.GetEnumerator 都以 ".GetEnumerator" 结尾，只按后缀匹配会把两者
			// 绑到同一个 MethodDef 上（token 重复）。接口的简单名与泛型性必须一致。
			var ifaceName = wantsQualified ? GetSimpleName(declaration.PrivateImplementationType) : null;
			bool ifaceIsGeneric = wantsQualified && IsGeneric(declaration.PrivateImplementationType);
			MethodDef qualifiedParamOnly = null, qualifiedFallback = null;
			MethodDef plainMatch = null, plainParamOnly = null, plainFallback = null;
			foreach (var method in typeDef.Methods) {
				bool qualified = method.Name.IndexOf('.') >= 0;
				if (qualified) {
					if (!wantsQualified || !method.Name.EndsWith("." + declaration.Name, StringComparison.Ordinal))
						continue;
					if (ifaceName != null && !InterfaceMatches(method.Name, declaration.Name, ifaceName, ifaceIsGeneric))
						continue;
				}
				else if (method.Name != declaration.Name) {
					continue;
				}
				var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
				if (parameters.Count != declaration.Parameters.Count)
					continue;
				if (method.IsStatic != declaration.HasModifier(ICSharpCode.Decompiler.CSharp.Syntax.Modifiers.Static))
					continue;
				if (ParameterTypesMatch(parameters, declaration.Parameters)) {
					// Full signature match; a same-params candidate with a different return type
					// (Equals(object) vs Equals(T), explicit impl vs helper) must not win.
					if (ReturnTypeMatches(method, declaration)) {
						// 显式实现的声明优先采用「带接口名的」候选：plain 候选的返回类型
						// (IEnumerator<T>) 与 IEnumerator 归一后相同，若先命中就会抢走
						// IEnumerable.GetEnumerator 的 token（audit 8.84）。
						if (qualified)
							return method;
						plainMatch ??= method;
						continue;
					}
					if (qualified)
						qualifiedParamOnly ??= method;
					else
						plainParamOnly ??= method;
					continue;
				}
				// Same name and arity: remember the first candidate, but keep looking. Without
				// this, Equals(object) inherits the definition of Equals(T) and then claims to
				// implement an interface method it does not implement.
				if (qualified)
					qualifiedFallback ??= method;
				else
					plainFallback ??= method;
			}
			if (wantsQualified)
				return qualifiedParamOnly ?? qualifiedFallback
					?? plainMatch ?? plainParamOnly ?? plainFallback;
			return plainMatch ?? plainParamOnly ?? plainFallback;
		}

		/// <summary>
		/// True when the metadata method matches the declaration in the syntax tree: parameters
		/// AND return type. <see cref="MatchMethod"/> can fall back to a same-name/arity overload,
		/// and overloads that differ only by their return type must not claim interface members
		/// (BC30401) — VB compares the whole signature when validating Implements clauses.
		/// </summary>
		public static bool ParametersMatch(MethodDef method, ICSharpCode.Decompiler.CSharp.Syntax.MethodDeclaration declaration) {
			var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
			return parameters.Count == declaration.Parameters.Count &&
				ParameterTypesMatch(parameters, declaration.Parameters) &&
				ReturnTypeMatches(method, declaration);
		}

		/// <summary>
		/// Return type comparison as a tie-breaker between candidates that share name, arity and
		/// parameters. Unresolvable names on either side keep the candidate alive (return true).
		/// </summary>
		static bool ReturnTypeMatches(MethodDef method, ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration declaration) {
			var retType = method.MethodSig?.RetType?.RemovePinnedAndModifiers();
			// The syntax tree spells Nullable<T> as "T?", which normalizes to the underlying name;
			// compare against the same name from the metadata side.
			string expected;
			if (retType is GenericInstSig nullable && nullable.GenericType?.TypeDefOrRef?.FullName == "System.Nullable`1" &&
					nullable.GenericArguments.Count == 1)
				expected = NormalizeParameterTypeName(nullable.GenericArguments[0]?.TypeName ?? "");
			else
				expected = NormalizeParameterTypeName(retType?.TypeName ?? "");
			var actual = NormalizeParameterTypeName(GetAstTypeName(declaration.ReturnType) ?? "");
			if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
				return true;
			// Unresolved generic parameters ("!!TResult" and friends) cannot be compared against
			// the syntax tree spelling; keep the candidate alive instead of vetoing it.
			if (expected.StartsWith("!") || actual.StartsWith("!"))
				return true;
			return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
		}

		static bool ParameterTypesMatch(List<Parameter> parameters, IEnumerable<ICSharpCode.Decompiler.CSharp.Syntax.ParameterDeclaration> declarationParameters) {
			for (int i = 0; i < parameters.Count; i++) {
				var expected = parameters[i].Type?.TypeName;
				var actual = GetAstTypeName(declarationParameters.ElementAt(i).Type);
				if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
					continue;
				// 元数据侧可能只给 'Nullable`1'（不带实参），而语法侧给元素类型
				// ('HexBufferSpan') —— 此时名字无从比较，交给 GenericArgumentsMatch 决定。
				bool metaIsBareNullable = expected.StartsWith("Nullable`1", StringComparison.Ordinal);
				bool astIsBareNullable = actual.StartsWith("Nullable`1", StringComparison.Ordinal);
				if (metaIsBareNullable != astIsBareNullable) {
					if (!GenericArgumentsMatch(parameters[i].Type, declarationParameters.ElementAt(i).Type))
						return false;
					continue;
				}
				// 'ref/out' shows up as a trailing '&' in the metadata name but as a modifier on
				// the syntax node, so it must not decide the match.
				if (!string.Equals(NormalizeParameterTypeName(expected), NormalizeParameterTypeName(actual), StringComparison.OrdinalIgnoreCase))
					return false;
				// 名字比较会把泛型实参切掉，所以 (Func<string>) 与 (Func<EditableValueTextInfo>)
				// 会撞车；泛型实参必须单独比一遍（audit 8.82）。
				if (!GenericArgumentsMatch(parameters[i].Type, declarationParameters.ElementAt(i).Type))
					return false;
			}
			return true;
		}

		/// <summary>
		/// 泛型实参也要一致：否则 <c>(Func&lt;string&gt;)</c> 与 <c>(Func&lt;EditableValueTextInfo&gt;)</c>
		/// 的简单名都归一成 "Func"，两个重载绑到同一个 MethodDef（token 重复）。
		/// 只加不改：名字比较保持原样，这条是额外的必要条件。
		/// </summary>
		static bool GenericArgumentsMatch(TypeSig metadataType, ICSharpCode.Decompiler.CSharp.Syntax.AstType syntaxType) {
			var metaArgs = (metadataType as GenericInstSig)?.GenericArguments;
			var text = syntaxType.ToString();
			// 语法侧的 'T?' 对应元数据侧的 Nullable<T>：去掉 '?' 再比。
			var lt = text.IndexOf('<');
			var bare = text.TrimEnd('?');
			var synArgs = lt >= 0 ? SplitGenericArguments(text.Substring(lt + 1, text.LastIndexOf('>') - lt - 1)) : null;
			if (metaArgs != null && metaArgs.Count == 1 && synArgs == null) {
				return string.Equals(SimpleTypeName(metaArgs[0].ToString()), SimpleTypeName(bare), StringComparison.OrdinalIgnoreCase);
			}
			if (metaArgs == null || synArgs == null)
				return (metaArgs == null) == (synArgs == null);
			if (metaArgs.Count != synArgs.Count)
				return false;
			for (int i = 0; i < metaArgs.Count; i++) {
				var m = metaArgs[i].ToString();
				var s = synArgs[i];
				if (!string.Equals(SimpleTypeName(m), SimpleTypeName(s), StringComparison.OrdinalIgnoreCase))
					return false;
				if (!GenericArgumentsTextMatch(m, s))
					return false;
			}
			return true;
		}

		static bool GenericArgumentsTextMatch(string meta, string syntax) {
			var ml = meta.IndexOf('<');
			var sl = syntax.IndexOf('<');
			var mArgs = ml >= 0 ? SplitGenericArguments(meta.Substring(ml + 1, meta.LastIndexOf('>') - ml - 1)) : null;
			var sArgs = sl >= 0 ? SplitGenericArguments(syntax.Substring(sl + 1, syntax.LastIndexOf('>') - sl - 1)) : null;
			if (mArgs == null || sArgs == null)
				return (mArgs == null) == (sArgs == null);
			if (mArgs.Count != sArgs.Count)
				return false;
			for (int i = 0; i < mArgs.Count; i++) {
				if (!string.Equals(SimpleTypeName(mArgs[i]), SimpleTypeName(sArgs[i]), StringComparison.OrdinalIgnoreCase))
					return false;
				if (!GenericArgumentsTextMatch(mArgs[i], sArgs[i]))
					return false;
			}
			return true;
		}

		/// <summary>取类型名的简单名（去掉命名空间、泛型实参与 arity 后缀）。</summary>
		static string SimpleTypeName(string name) {
			var lt = name.IndexOf('<');
			var end = lt >= 0 ? lt : name.Length;
			var tick = name.IndexOf('`');
			if (tick >= 0 && tick < end)
				end = tick;
			var dot = name.LastIndexOf('.', end - 1);
			return (dot < 0 ? name.Substring(0, end) : name.Substring(dot + 1, end - dot - 1)).Trim();
		}

		/// <summary>把顶层逗号分开（尊重 <&gt; 嵌套），用于拆分泛型实参。</summary>
		static List<string> SplitGenericArguments(string text) {
			var result = new List<string>();
			int depth = 0;
			int start = 0;
			for (int i = 0; i < text.Length; i++) {
				switch (text[i]) {
					case '<': depth++; break;
					case '>': depth--; break;
					case ',':
						if (depth == 0) {
							result.Add(text.Substring(start, i - start));
							start = i + 1;
						}
						break;
				}
			}
			result.Add(text.Substring(start));
			return result;
		}

		/// <summary>VB keyword spellings of the BCL primitives, for name-only comparisons.</summary>
		static readonly Dictionary<string, string> VbPrimitiveNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["Integer"] = "Int32",
			["UInteger"] = "UInt32",
			["Long"] = "Int64",
			["ULong"] = "UInt64",
			["Short"] = "Int16",
			["UShort"] = "UInt16",
			["Date"] = "DateTime",
		};

		internal static string NormalizeParameterTypeName(string name) {
			// 统一 VB 的数组拼写（Byte()）与元数据/C# 的 Byte[]，但不能把 [] 也剥掉 ——
			// 否则 (string) 与 (string[]) 会被当成同一个签名，重载绑定到同一个 MethodDef，
			// 于是两个构造函数印同一个 token（audit 8.81）。
			var result = name.Replace("()", "[]");
			result = result.TrimEnd('&', '*', '?');
			// Arrays ("Byte[]" in metadata, "Byte()" in VB) and Nullable ("T?" / Nullable`1")
			// normalize to their element type; the trim above already handled the spellings.
			// A generic argument contains dots and an arity suffix of its own, so cut the arguments
			// off first: "System.ValueTuple`3<A.B, C.D>" must compare as "ValueTuple". The VB
			// spelling "Lazy(Of T)" cuts the same way.
			var generic = result.IndexOf('<');
			if (generic >= 0)
				result = result.Substring(0, generic);
			var of = result.IndexOf("(Of ", StringComparison.Ordinal);
			if (of >= 0)
				result = result.Substring(0, of);
			int tick = result.LastIndexOf('`');
			if (tick > 0)
				result = result.Substring(0, tick);
			int index = result.LastIndexOf('.');
			result = index < 0 ? result.Trim() : result.Substring(index + 1).Trim();
			return VbPrimitiveNames.TryGetValue(result, out var mapped) ? mapped : result;
		}

		static string GetAstTypeName(ICSharpCode.Decompiler.CSharp.Syntax.AstType type) {
			switch (type) {
			case ICSharpCode.Decompiler.CSharp.Syntax.SimpleType simple:
				return simple.Identifier;
			case ICSharpCode.Decompiler.CSharp.Syntax.MemberType member:
				return member.MemberName;
			case ICSharpCode.Decompiler.CSharp.Syntax.ComposedType composed:
				var baseName = GetAstTypeName(composed.BaseType);
				return composed.ArraySpecifiers.Count > 0 ? baseName + "[]" : baseName;
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
