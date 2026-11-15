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
using dnlib.DotNet;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// Maps cecil references from the 3.2 engine's type system back to dnlib references for the
	/// module being decompiled. A cecil reference into ANOTHER assembly carries that assembly's
	/// metadata token, which is meaningless inside this module: the raw token resolve hits an
	/// unrelated local definition (wrong kind color AND wrong navigation target). Token resolves
	/// are validated, and external references are mapped by name to TypeRefs from the module's
	/// own reference tables, whose assembly scope is authoritative.
	/// </summary>
	static class DnlibReferenceMapper {
		/// <summary>Validated dnlib reference for a cecil reference, or null when unmappable.</summary>
		public static IMemberRef? ToDnlib(ModuleDefMD module, Mono.Cecil.MemberReference? cecilMember) {
			if (cecilMember is null)
				return null;
			try {
				var resolved = module.ResolveToken(new MDToken(cecilMember.MetadataToken.ToInt32())) as IMemberRef;
				if (resolved is not null && SameIdentity(resolved, cecilMember))
					return resolved;
			}
			catch {
			}
			// The cecil reference belongs to another module: its token is meaningless here.
			if (cecilMember is Mono.Cecil.TypeReference)
				return MapExternal(module, cecilMember);
			return MapExternalMember(module, cecilMember);
		}

		/// <summary>
		/// Maps a cecil member of another assembly (a method or field) to a MemberRef whose
		/// class TypeRef carries the authoritative assembly scope and whose signature is
		/// converted from cecil, so dnSpy resolves it against the target assembly - the same
		/// shape the 2.3 engine emitted (MemberRefMD) for these sites. An existing MemberRef
		/// from the module's own reference table wins when one matches (exact metadata
		/// identity); otherwise one is synthesized.
		/// </summary>
		public static IMemberRef? MapExternalMember(ModuleDefMD module, Mono.Cecil.MemberReference cecilMember) {
			try {
				string name;
				Mono.Cecil.MethodReference? elementMethod = null;
				Mono.Cecil.FieldReference? fieldRef = null;
				if (cecilMember is Mono.Cecil.MethodSpecification spec) {
					name = cecilMember.Name;
					elementMethod = spec.ElementMethod;
				} else if (cecilMember is Mono.Cecil.MethodReference mr) {
					name = mr.Name;
					elementMethod = mr;
				} else if (cecilMember is Mono.Cecil.FieldReference fr) {
					name = fr.Name;
					fieldRef = fr;
				} else {
					return null;
				}
				var declaringRaw = StripSpecs((elementMethod ?? (Mono.Cecil.MemberReference)fieldRef!).DeclaringType!);
				if (declaringRaw is null || MapExternal(module, declaringRaw) is not TypeRefUser declaring)
					return null;
				// A matching entry from the module's own MemberRef table is the most faithful
				// reference; the cache keeps repeated call sites cheap.
				var key = declaring.FullName + "::" + name;
				if (memberRefTable is null || !ReferenceEquals(memberRefTableModule, module)) {
					memberRefTable = new Dictionary<string, MemberRef>(StringComparer.Ordinal);
					memberRefTableModule = module;
					foreach (var existing in module.GetMemberRefs()) {
						if (existing.Class?.FullName is string cls)
							memberRefTable[cls + "::" + existing.Name] = existing;
					}
				}
				if (memberRefTable.TryGetValue(key, out var matched))
					return matched;
				if (elementMethod is not null) {
					var sig = ConvertMethodSig(module, elementMethod);
					return sig is null ? null : new MemberRefUser(module, name, sig, declaring);
				}
				var fieldType = ConvertType(module, fieldRef!.FieldType);
				return fieldType is null ? null : new MemberRefUser(module, name, new FieldSig(fieldType), declaring);
			}
			catch {
				return null;
			}
		}

		static Dictionary<string, MemberRef>? memberRefTable;
		static ModuleDefMD? memberRefTableModule;

		/// <summary>Strips a generic instance down to its element type (List&lt;int&gt; -> List).</summary>
		static Mono.Cecil.TypeReference? StripSpecs(Mono.Cecil.TypeReference type) {
			if (type is Mono.Cecil.TypeSpecification spec) {
				if (spec is Mono.Cecil.GenericInstanceType genericInstance)
					return genericInstance.ElementType;
				return null;
			}
			return type;
		}

		/// <summary>Converts a cecil type to its dnlib signature shape; null when unmappable.</summary>
		static TypeSig? ConvertType(ModuleDefMD module, Mono.Cecil.TypeReference? type) {
			if (type is null)
				return null;
			try {
				switch (type) {
				case Mono.Cecil.ByReferenceType byRef:
					return ConvertType(module, byRef.ElementType) is { } byRefElement ? new ByRefSig(byRefElement) : null;
				case Mono.Cecil.PointerType pointer:
					return ConvertType(module, pointer.ElementType) is { } ptrElement ? new PtrSig(ptrElement) : null;
				case Mono.Cecil.SentinelType sentinel:
					return ConvertType(module, sentinel.ElementType);
				case Mono.Cecil.RequiredModifierType reqMod:
					return ConvertType(module, reqMod.ElementType);
				case Mono.Cecil.OptionalModifierType optMod:
					return ConvertType(module, optMod.ElementType);
				case Mono.Cecil.PinnedType pinned:
					return ConvertType(module, pinned.ElementType);
				case Mono.Cecil.ArrayType array:
					return ConvertType(module, array.ElementType) is { } arrayElement ? new ArraySig(arrayElement, (uint)array.Rank) : null;
				case Mono.Cecil.GenericParameter genericParameter:
					return genericParameter.Type == Mono.Cecil.GenericParameterType.Method
						? (TypeSig)new GenericMVar((uint)genericParameter.Position)
						: new GenericVar((uint)genericParameter.Position);
				case Mono.Cecil.GenericInstanceType genericInstance: {
					if (ConvertType(module, genericInstance.ElementType) is not ClassOrValueTypeSig element)
						return null;
					var args = new TypeSig[genericInstance.GenericArguments.Count];
					for (var i = 0; i < args.Length; i++) {
						args[i] = ConvertType(module, genericInstance.GenericArguments[i]);
						if (args[i] is null)
							return null;
					}
					return new GenericInstSig(element, args);
				}
				default: {
					var corlibSig = CorLibTypeSig(module, type);
					if (corlibSig is not null)
						return corlibSig;
					var scopeName = ScopeAssemblyName(type);
					if (scopeName is null)
						return null;
					var scope = FindAssemblyRef(module, scopeName);
					if (scope is null)
						return null;
					if (type.DeclaringType is not null) {
						if (ConvertType(module, type.DeclaringType) is not ITypeDefOrRef nestedOuter)
							return null;
						var nestedRef = new TypeRefUser(module, string.Empty, type.Name, (IResolutionScope)nestedOuter);
						return type.IsValueType ? (TypeSig)new ValueTypeSig(nestedRef) : new ClassSig(nestedRef);
					}
					var typeRef = new TypeRefUser(module, type.Namespace, type.Name, scope);
					return type.IsValueType ? (TypeSig)new ValueTypeSig(typeRef) : new ClassSig(typeRef);
				}
				}
			}
			catch {
				return null;
			}
		}

		static TypeSig? CorLibTypeSig(ModuleDefMD module, Mono.Cecil.TypeReference type) {
			if (ScopeAssemblyName(type) != module.CorLibTypes.AssemblyRef.Name)
				return null;
			var corlib = module.CorLibTypes;
			return type.FullName switch {
				"System.String" => corlib.String,
				"System.Object" => corlib.Object,
				"System.Boolean" => corlib.Boolean,
				"System.Char" => corlib.Char,
				"System.Byte" => corlib.Byte,
				"System.SByte" => corlib.SByte,
				"System.Int16" => corlib.Int16,
				"System.UInt16" => corlib.UInt16,
				"System.Int32" => corlib.Int32,
				"System.UInt32" => corlib.UInt32,
				"System.Int64" => corlib.Int64,
				"System.UInt64" => corlib.UInt64,
				"System.Single" => corlib.Single,
				"System.Double" => corlib.Double,
				"System.IntPtr" => corlib.IntPtr,
				"System.UIntPtr" => corlib.UIntPtr,
				"System.Void" => corlib.Void,
				_ => null,
			};
		}

		static MethodSig? ConvertMethodSig(ModuleDefMD module, Mono.Cecil.MethodReference method) {
			try {
				var returnType = ConvertType(module, method.ReturnType);
				if (returnType is null)
					return null;
				var parameterTypes = new TypeSig[method.Parameters.Count];
				for (var i = 0; i < parameterTypes.Length; i++) {
					parameterTypes[i] = ConvertType(module, method.Parameters[i].ParameterType);
					if (parameterTypes[i] is null)
						return null;
				}
				var genericParamCount = (uint)method.GenericParameters.Count;
				// The metadata calling convention is a flag set: convention plus HasThis /
				// ExplicitThis / Generic bits, exactly what MemberRef signatures encode.
				var callConvention = CallingConvention.Default;
				if (method.HasThis)
					callConvention |= CallingConvention.HasThis;
				if (method.ExplicitThis)
					callConvention |= CallingConvention.ExplicitThis;
				if (genericParamCount > 0)
					callConvention |= CallingConvention.Generic;
				return new MethodSig(callConvention, genericParamCount, returnType, parameterTypes);
			}
			catch {
				return null;
			}
		}

		/// <summary>
		/// Loose identity check that catches cross-module rid collisions: a token resolved in
		/// this module must match the cecil reference's kind, name and declaring type - methods
		/// like ToString or get_X share names across assemblies, so the declaring type decides.
		/// </summary>
		static bool SameIdentity(IMemberRef resolved, Mono.Cecil.MemberReference cecilMember) {
			switch (cecilMember) {
			case Mono.Cecil.TypeDefinition cecilType:
				return resolved is TypeDef td && td.Name == cecilType.Name && td.Namespace == cecilType.Namespace;
			case Mono.Cecil.MethodDefinition cecilMethod:
				return resolved is MethodDef md && md.Name == cecilMethod.Name &&
					SameDeclaringType(md.DeclaringType, cecilMethod.DeclaringType);
			case Mono.Cecil.FieldDefinition cecilField:
				return resolved is FieldDef fd && fd.Name == cecilField.Name &&
					SameDeclaringType(fd.DeclaringType, cecilField.DeclaringType);
			case Mono.Cecil.PropertyDefinition cecilProperty:
				return resolved is PropertyDef pd && pd.Name == cecilProperty.Name &&
					SameDeclaringType(pd.DeclaringType, cecilProperty.DeclaringType);
			case Mono.Cecil.EventDefinition cecilEvent:
				return resolved is EventDef ed && ed.Name == cecilEvent.Name &&
					SameDeclaringType(ed.DeclaringType, cecilEvent.DeclaringType);
			default:
				return false;
			}
		}

		static bool SameDeclaringType(TypeDef? declaring, Mono.Cecil.TypeDefinition? cecilDeclaring) {
			if (declaring is null || cecilDeclaring is null)
				return declaring is null && cecilDeclaring is null;
			return declaring.Name == cecilDeclaring.Name && declaring.Namespace == cecilDeclaring.Namespace;
		}

		/// <summary>
		/// The referenced assembly's plain name for a cecil type reference. The bridge resolves
		/// external references to loaded modules, whose Scope is a ModuleDefinition carrying a
		/// file name ('System.Private.CoreLib.dll') instead of an AssemblyNameReference - both
		/// shapes must yield the bare assembly name.
		/// </summary>
		static string? ScopeAssemblyName(Mono.Cecil.TypeReference type) {
			switch (type.Scope) {
			case Mono.Cecil.ModuleDefinition moduleDefinition: {
				var assemblyName = moduleDefinition.Assembly?.Name?.Name;
				if (!string.IsNullOrEmpty(assemblyName))
					return assemblyName;
				var fileName = moduleDefinition.Name;
				if (string.IsNullOrEmpty(fileName))
					return null;
				return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? fileName.Substring(0, fileName.Length - 4) : fileName;
			}
			case Mono.Cecil.AssemblyNameReference assemblyNameReference:
				return assemblyNameReference.Name;
			default:
				return null;
			}
		}

		/// <summary>Maps a cecil reference into another assembly to a dnlib reference by name.</summary>
		public static IMemberRef? MapExternal(ModuleDefMD module, Mono.Cecil.MemberReference cecilMember) {
			Mono.Cecil.TypeReference? typeReference = cecilMember switch {
				Mono.Cecil.TypeSpecification spec => spec.ElementType,
				Mono.Cecil.TypeReference tr => tr,
				_ => null,
			};
			if (typeReference is null)
				return null;
			var scopeName = ScopeAssemblyName(typeReference);
			if (scopeName is null)
				return null;
			var scope = FindAssemblyRef(module, scopeName);
			if (scope is null)
				return null;
			// Nested types chain through declaring TypeRefs so dnSpy can resolve the whole path.
			if (typeReference.DeclaringType is not null) {
				if (MapExternal(module, typeReference.DeclaringType) is not TypeRefUser declaring)
					return null;
				return new TypeRefUser(module, string.Empty, typeReference.Name, (IResolutionScope)declaring);
			}
			return new TypeRefUser(module, typeReference.Namespace, typeReference.Name, scope);
		}

		static IResolutionScope? FindAssemblyRef(ModuleDefMD module, string? scopeName) {
			if (string.IsNullOrEmpty(scopeName))
				return null;
			foreach (var ar in module.GetAssemblyRefs()) {
				if (ar.Name == scopeName)
					return ar;
			}
			// Not among the module's refs yet (for example the corlib alias): create one by name.
			return new AssemblyRefUser(new UTF8String(scopeName));
		}

		/// <summary>
		/// Finds a TypeRef in this module's reference tables by simple name; the scope of those
		/// refs is authoritative, so the resulting reference navigates to the right assembly.
		/// The generic arity comes from the metadata name suffix (List`1).
		/// </summary>
		/// <summary>
		/// Looks up a MemberRef in the module's own reference table by declaring-type full name
		/// and member name (the exact call the IL made). Used when the type-system member
		/// cannot be bridged through cecil (external accessors, external operator methods) and
		/// the table entry carries the authoritative assembly scope.
		/// </summary>
		public static MemberRef? FindMemberRefInTable(ModuleDefMD module, string declaringTypeFullName, string memberName) {
			if (memberRefTable is null || !ReferenceEquals(memberRefTableModule, module)) {
				memberRefTable = new Dictionary<string, MemberRef>(StringComparer.Ordinal);
				memberRefTableModule = module;
				foreach (var existing in module.GetMemberRefs()) {
					if (existing.Class?.FullName is string cls)
						memberRefTable[cls + "::" + existing.Name] = existing;
				}
			}
			if (memberRefTable.TryGetValue(declaringTypeFullName + "::" + memberName, out var found))
				return found;
			// The type-system full name of a generic type omits the arity suffix (List vs
			// List`1) and generic instances (List`1<System.String>), while dnlib keys carry
			// them. Match by prefix: the declaring name followed by the arity tick or an
			// instantiation angle bracket.
			MemberRef? prefixMatch = null;
			foreach (var kv in memberRefTable) {
				var colon = kv.Key.IndexOf("::", StringComparison.Ordinal);
				if (colon < 0 || kv.Key.Substring(colon + 2) != memberName)
					continue;
				var cls = kv.Key.Substring(0, colon);
				if (cls == declaringTypeFullName ||
					(cls.Length > declaringTypeFullName.Length && cls.StartsWith(declaringTypeFullName, StringComparison.Ordinal) &&
					 (cls[declaringTypeFullName.Length] == '`' || cls[declaringTypeFullName.Length] == '<')))
					prefixMatch ??= kv.Value;
			}
			return prefixMatch;
		}

		public static TypeRef? ResolveTypeRefByName(ModuleDefMD module, string name, int arity) {
			var table = GetOrCreateTypeRefTable(module);
			if (!table.TryGetValue(name, out var candidates))
				return null;
			foreach (var (typeRef, refArity) in candidates) {
				if (typeRef.DeclaringType is null && (arity < 0 || arity == refArity))
					return typeRef;
			}
			foreach (var (typeRef, refArity) in candidates) {
				if (arity < 0 || arity == refArity)
					return typeRef;
			}
			return null;
		}

		static Dictionary<string, List<(TypeRef typeRef, int arity)>>? typeRefTable;
		static ModuleDefMD? typeRefTableModule;

		static Dictionary<string, List<(TypeRef, int)>> GetOrCreateTypeRefTable(ModuleDefMD module) {
			if (typeRefTable is null || !ReferenceEquals(typeRefTableModule, module)) {
				typeRefTable = new Dictionary<string, List<(TypeRef, int)>>(StringComparer.Ordinal);
				typeRefTableModule = module;
				foreach (var tr in module.GetTypeRefs()) {
					var (bare, refArity) = SplitGenericArity(tr.Name);
					if (!typeRefTable.TryGetValue(bare, out var list))
						typeRefTable[bare] = list = new List<(TypeRef, int)>();
					list.Add((tr, refArity));
				}
			}
			return typeRefTable;
		}

		public static (string bare, int arity) SplitGenericArity(string metadataName) {
			var tick = metadataName.IndexOf('`');
			if (tick < 0)
				return (metadataName, 0);
			return (metadataName.Substring(0, tick), int.TryParse(metadataName.Substring(tick + 1), out var n) ? n : 0);
		}
	}
}
