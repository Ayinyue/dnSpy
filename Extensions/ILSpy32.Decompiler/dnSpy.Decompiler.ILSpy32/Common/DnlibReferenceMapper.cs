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
			return MapExternal(module, cecilMember);
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

		/// <summary>Maps a cecil reference into another assembly to a dnlib reference by name.</summary>
		public static IMemberRef? MapExternal(ModuleDefMD module, Mono.Cecil.MemberReference cecilMember) {
			Mono.Cecil.TypeReference? typeReference = cecilMember switch {
				Mono.Cecil.TypeSpecification spec => spec.ElementType,
				Mono.Cecil.TypeReference tr => tr,
				_ => null,
			};
			if (typeReference is null)
				return null;
			if (typeReference.Scope is null || typeReference.Scope is Mono.Cecil.ModuleDefinition)
				return null;
			var scope = FindAssemblyRef(module, typeReference.Scope.Name);
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
