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
using System.Runtime.CompilerServices;
using dnlib.DotNet;
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
				// Serializing the (already dnlib-loaded) module gives cecil well-formed bytes.
				module.Write(stream);
			}
			catch {
				stream.Dispose();
				return null;
			}
			stream.Position = 0;
			try {
				var readerParams = new ReaderParameters {
					AssemblyResolver = CreateResolver(location),
					ReadSymbols = false,
				};
				var cecilModule = ModuleDefinition.ReadModule(stream, readerParams);
				return new Entry { Module = cecilModule, Stream = stream };
			}
			catch {
				stream.Dispose();
				return null;
			}
		}

		static Mono.Cecil.IAssemblyResolver CreateResolver(string? mainModuleLocation) {
			var resolver = new DefaultAssemblyResolver();
			foreach (var dir in GetSearchDirectories(mainModuleLocation)) {
				try {
					resolver.AddSearchDirectory(dir);
				}
				catch {
					// ignore invalid directories
				}
			}
			return resolver;
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
	}
}
