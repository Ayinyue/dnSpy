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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// Bridges dnlib modules to cecil modules so the ILSpy 3.2 engine can be used.
	///
	/// The engine works on <see cref="ModuleDefinition"/> (cecil). dnSpy works on
	/// <see cref="ModuleDef"/> (dnlib). Instead of porting the engine's type system to
	/// dnlib, the dnlib module is serialized to a memory stream and re-read with cecil.
	///
	/// This deliberately bypasses cecil's weak spot: cecil never parses the original
	/// (possibly obfuscated) file, it reads bytes that dnlib already normalized.
	/// </summary>
	static class DnlibCecilBridge {
		sealed class Entry {
			public ModuleDefinition Module = null!;
			public MemoryStream Stream = null!;
			public FailSoftAssemblyResolver? Resolver;
		}

		static readonly ConditionalWeakTable<ModuleDef, Entry> cache = new ConditionalWeakTable<ModuleDef, Entry>();
		static readonly object cacheLock = new object();

		/// <summary>
		/// Returns a cecil <see cref="ModuleDefinition"/> for <paramref name="module"/>, or null
		/// if the module cannot be bridged (e.g. it has no file on disk and cannot be written).
		/// The returned instance is cached and must not be disposed by the caller.
		/// </summary>
		public static ModuleDefinition? TryGetModule(ModuleDef module) {
			if (module is null)
				return null;
			lock (cacheLock) {
				if (cache.TryGetValue(module, out var existing))
					return existing.Module;
				Entry? entry = null;
				try {
					entry = CreateEntry(module);
				}
				catch {
					entry?.Module?.Dispose();
					entry?.Stream?.Dispose();
					entry?.Resolver?.Dispose();
					return null;
				}
				if (entry is null)
					return null;
				cache.Add(module, entry);
				return entry.Module;
			}
		}

		static Entry? CreateEntry(ModuleDef module) {
			string? location = (module as ModuleDefMD)?.Location;
			var stream = new MemoryStream();
			try {
				// dnlib's writer reorders the TypeDef table by default (nested types are
				// emitted after the top-level types), which invalidates every MDToken
				// captured from the dnlib module. The engine addresses types and members
				// through those tokens (LookupToken), so a plain Write silently maps a
				// token to the wrong definition. PreserveRids keeps the original RIDs.
				var options = new ModuleWriterOptions(module);
				options.MetadataOptions.Flags |= MetadataFlags.PreserveRids;
				module.Write(stream, options);
			}
			catch {
				// Modules with pending edits can contain row counts that cannot be
				// written with preserved RIDs; fall back to a normal write.
				stream.Dispose();
				stream = new MemoryStream();
				try {
					module.Write(stream);
				}
				catch {
					stream.Dispose();
					return null;
				}
			}
			stream.Position = 0;
			FailSoftAssemblyResolver? resolver = null;
			try {
				resolver = (FailSoftAssemblyResolver)CreateResolver(location, module);
				var readerParams = new ReaderParameters {
					AssemblyResolver = resolver,
					ReadSymbols = false,
				};
				var cecilModule = ModuleDefinition.ReadModule(stream, readerParams);
				// The engine's AddXmlDocumentationTransform locates the documentation file from
				// ModuleDefinition.FileName; the snapshot came from a memory stream, so hand it
				// the original assembly's path (dnlib knows it, cecil does not).
				if (!string.IsNullOrEmpty(location))
					SetCecilFileName(cecilModule, location);
				return new Entry { Module = cecilModule, Stream = stream, Resolver = resolver };
			}
			catch {
				resolver?.Dispose();
				stream.Dispose();
				return null;
			}
		}

		static void SetCecilFileName(ModuleDefinition module, string location) {
			try {
				typeof(ModuleDefinition)
					.GetField("file_name", BindingFlags.Instance | BindingFlags.NonPublic)?
					.SetValue(module, location);
			}
			catch {
				// the documentation lookup then just stays unavailable
			}
		}

		static Mono.Cecil.IAssemblyResolver CreateResolver(string? mainModuleLocation, ModuleDef module) {
			var directories = GetSearchDirectories(mainModuleLocation).ToArray();
			return new FailSoftAssemblyResolver(module, directories);
		}

		/// <summary>
		/// Resolves assembly references without failing the whole decompilation. cecil's
		/// DefaultAssemblyResolver matches name+version+token exactly and only probes the
		/// search directories, so a .NET Framework assembly's 'System.Management, 4.0.0.0'
		/// (which lives in the legacy GAC) threw and degraded the entire type. Three tiers:
		/// exact match, the dnlib module's own resolver (dnSpy's global resolver in the GUI -
		/// GAC, loaded documents, version tolerance), then a nearest simple-name match in the
		/// search directories and the legacy GAC. Total failure returns null, which the engine
		/// renders as unresolved types instead of a failed decompilation.
		/// </summary>
		sealed class FailSoftAssemblyResolver : Mono.Cecil.IAssemblyResolver {
			readonly ModuleDef module;
			readonly DefaultAssemblyResolver exact;
			readonly string[] searchDirectories;
			readonly string[] gacRoots = {
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "assembly", "GAC_MSIL"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "assembly", "GAC_64"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "assembly", "GAC_32"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "assembly", "GAC_MSIL"),
			};
			readonly Dictionary<string, AssemblyDefinition?> cache = new Dictionary<string, AssemblyDefinition?>(StringComparer.Ordinal);
			readonly List<Mono.Cecil.ModuleDefinition> owned = new List<Mono.Cecil.ModuleDefinition>();
			readonly object lockObject = new object();

			public FailSoftAssemblyResolver(ModuleDef module, string[] searchDirectories) {
				this.module = module;
				this.searchDirectories = searchDirectories;
				exact = new DefaultAssemblyResolver();
				foreach (var dir in searchDirectories) {
					try {
						exact.AddSearchDirectory(dir);
					}
					catch {
						// ignore invalid directories
					}
				}
			}

			public AssemblyDefinition? Resolve(AssemblyNameReference reference) =>
				Resolve(reference, new ReaderParameters { AssemblyResolver = this, ReadSymbols = false });

			public AssemblyDefinition? Resolve(AssemblyNameReference reference, ReaderParameters parameters) {
				if (reference is null)
					return null;
				lock (lockObject) {
					if (cache.TryGetValue(reference.FullName, out var cached))
						return cached;
					var result = ResolveCore(reference, parameters);
					cache[reference.FullName] = result;
					return result;
				}
			}

			AssemblyDefinition? ResolveCore(AssemblyNameReference reference, ReaderParameters parameters) {
				// 1) Exact name+version+token in the search directories.
				try {
					return exact.Resolve(reference, parameters);
				}
				catch {
					// fall through
				}
				// 2) The dnlib module's own resolver: inside dnSpy this is the global resolver
				// (GAC, loaded documents, version tolerance); standalone tools get dnlib's.
				try {
					var resolver = module.Context?.AssemblyResolver;
					if (resolver != null) {
						var info = new AssemblyNameInfo(reference.FullName);
						var assemblyDef = resolver.Resolve(info, module);
						var location = assemblyDef?.ManifestModule?.Location;
						if (!string.IsNullOrEmpty(location))
							return ReadOwned(location!, parameters);
					}
				}
				catch {
					// fall through
				}
				// 3) Nearest simple-name match: the search directories and the legacy GAC.
				foreach (var path in ProbeSimpleNames(reference.Name)) {
					try {
						return ReadOwned(path, parameters);
					}
					catch {
						// try the next candidate
					}
				}
				return null;
			}

			IEnumerable<string> ProbeSimpleNames(string simpleName) {
				var fileName = simpleName + ".dll";
				foreach (var dir in searchDirectories) {
					var path = Path.Combine(dir, fileName);
					if (File.Exists(path))
						yield return path;
				}
				foreach (var root in gacRoots) {
					var dir = Path.Combine(root, simpleName);
					if (!Directory.Exists(dir))
						continue;
					foreach (var path in Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories))
						yield return path;
				}
			}

			AssemblyDefinition ReadOwned(string path, ReaderParameters parameters) {
				var ownParameters = new ReaderParameters {
					AssemblyResolver = this,
					ReadSymbols = false,
				};
				var module = ModuleDefinition.ReadModule(path, ownParameters);
				owned.Add(module);
				return module.Assembly;
			}

			public void Dispose() {
				foreach (var module in owned)
					module.Dispose();
				owned.Clear();
				exact.Dispose();
			}
		}

		static IEnumerable<string> GetSearchDirectories(string? mainModuleLocation) {
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			IEnumerable<string?> candidates = new string?[] {
				!string.IsNullOrEmpty(mainModuleLocation) ? Path.GetDirectoryName(mainModuleLocation) : null,
				AppContext.BaseDirectory,
				GetWindowsDesktopRuntimeDir(),
				GetNetCoreRuntimeDir(),
			};
			foreach (var c in candidates) {
				if (string.IsNullOrEmpty(c))
					continue;
				if (!seen.Add(c!))
					continue;
				if (Directory.Exists(c))
					yield return c!;
			}
		}

		static string? GetWindowsDesktopRuntimeDir() => GetNewestRuntimeDir("Microsoft.WindowsDesktop.App");

		static string? GetNetCoreRuntimeDir() => GetNewestRuntimeDir("Microsoft.NETCore.App");

		static string? GetNewestRuntimeDir(string name) {
			try {
				var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", name);
				if (!Directory.Exists(root))
					return null;
				return Directory.GetDirectories(root)
					.Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
					.Where(t => t.Ver is not null)
					.OrderByDescending(t => t.Ver)
					.Select(t => t.Dir)
					.FirstOrDefault();
			}
			catch {
				return null;
			}
		}

		static Version? ParseVersion(string s) => Version.TryParse(s, out var v) ? v : null;

		/// <summary>
		/// Maps a dnlib metadata token to the cecil definition, or null if not found.
		/// </summary>
		public static IMemberDefinition? TryGetMember(ModuleDefinition module, uint rawToken) {
			try {
				return module.LookupToken((int)rawToken) as IMemberDefinition;
			}
			catch {
				return null;
			}
		}

		/// <summary>
		/// Maps a dnlib <see cref="ITypeDefOrRef"/> to the equivalent cecil
		/// <see cref="TypeReference"/>, or null if it cannot be bridged.
		/// TypeDef / TypeRef / TypeSpec tokens all resolve through cecil's metadata.
		/// </summary>
		public static TypeReference? TryGetTypeReference(ModuleDef module, ITypeDefOrRef type) {
			if (module is null || type is null)
				return null;
			var cecilModule = TryGetModule(module);
			if (cecilModule is null)
				return null;
			try {
				return cecilModule.LookupToken((int)type.MDToken.Raw) as TypeReference;
			}
			catch {
				return null;
			}
		}
	}
}
