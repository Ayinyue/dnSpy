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
				var propertyDef = MatchProperty(typeDef, "Item", indexer.Parameters.Count, indexer.PrivateImplementationType);
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
				ICSharpCode.Decompiler.CSharp.Syntax.AstType privateImplementationType) {
			bool wantsQualified = privateImplementationType is { IsNull: false };
			PropertyDef exact = null, suffix = null;
			foreach (var property in typeDef.Properties) {
				if (property.PropertySig.Params.Count != parameterCount)
					continue;
				if (property.Name == name)
					exact ??= property;
				else if (property.Name.EndsWith("." + name, StringComparison.Ordinal))
					suffix ??= property;
			}
			return wantsQualified ? suffix ?? exact : exact ?? suffix;
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
			MethodDef fallback = null;
			MethodDef parameterMatch = null;
			foreach (var method in typeDef.Methods) {
				bool qualified = method.Name.IndexOf('.') >= 0;
				if (qualified) {
					if (!wantsQualified || !method.Name.EndsWith("." + declaration.Name, StringComparison.Ordinal))
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
					if (ReturnTypeMatches(method, declaration))
						return method;
					parameterMatch ??= method;
					continue;
				}
				// Same name and arity: remember the first candidate, but keep looking. Without
				// this, Equals(object) inherits the definition of Equals(T) and then claims to
				// implement an interface method it does not implement.
				fallback ??= method;
			}
			return parameterMatch ?? fallback;
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
				// 'ref/out' shows up as a trailing '&' in the metadata name but as a modifier on
				// the syntax node, so it must not decide the match.
				if (!string.Equals(NormalizeParameterTypeName(expected), NormalizeParameterTypeName(actual), StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
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
			var result = name.TrimEnd('&', '*', '(', ')', '[', ']', '?');
			// Arrays ("Byte[]" in metadata, "Byte()" in VB) and Nullable ("T?" / Nullable`1)
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
