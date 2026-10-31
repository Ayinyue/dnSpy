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
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.Common;
using dnSpy.Decompiler.ILSpy32.Settings;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.CSharp {
	/// <summary>
	/// C# decompiler backed by the ILSpy 3.2 engine (cecil-based), bridged from dnlib modules.
	/// </summary>
	sealed class CSharpDecompiler32 : DecompilerBase {
		static readonly Guid THE_GUID = new Guid("8C1F4A62-9D3B-4E77-8A51-2F6B0C9D4E13");

		readonly DecompilerSettings32 settings;
		readonly string uniqueNameUI;

		// CSharpDecompiler is not thread-safe and building its type system is costly, so one
		// instance is cached per bridged module and all decompilation is serialized.
		internal static readonly ConditionalWeakTable<ModuleDefinition, ICSharpCode.Decompiler.CSharp.CSharpDecompiler> engineCache = new ConditionalWeakTable<ModuleDefinition, ICSharpCode.Decompiler.CSharp.CSharpDecompiler>();
		internal static readonly object engineLock = new object();

		public CSharpDecompiler32(DecompilerSettings32 settings, string uniqueNameUI) {
			this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
			this.uniqueNameUI = uniqueNameUI ?? throw new ArgumentNullException(nameof(uniqueNameUI));
		}

		public override DecompilerSettingsBase Settings => settings;
		public override string ContentTypeString => ContentTypeDefinitions.CSharpILSpy;
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_CSHARP;
		public override string UniqueNameUI => uniqueNameUI;
		public override double OrderUI => DecompilerConstants.CSHARP_ILSPY_ORDERUI + 0.5;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_CSHARP;
		public override Guid UniqueGuid => THE_GUID;
		public override string FileExtension => ".cs";
		public override string? ProjectFileExtension => null;

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(method, output, ctx);

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(property, output, ctx);

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(field, output, ctx);

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(ev, output, ctx);

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			if (type is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var cecilType = TryGetCecil(type.Module, type.MDToken.Raw) as TypeDefinition;
			if (cecilType is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(type.Module, output, ctx, d => d.DecompileTypeAsString(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(Common.TypeNameHelper.ToReflectionName(cecilType.FullName))));
		}

		void DecompileMember(IMDTokenProvider? member, IDecompilerOutput output, DecompilationContext ctx) {
			if (member is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var module = (member as IMemberDef)?.Module;
			if (module is null) {
				WriteNote(output, "// ILSpy 3.2: member has no module.");
				return;
			}
			var cecilMember = TryGetCecil(module, member.MDToken.Raw) as IMemberDefinition;
			if (cecilMember is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(module, output, ctx, d => d.DecompileAsString(cecilMember));
		}

		static IMemberDefinition? TryGetCecil(ModuleDef module, uint rawToken) {
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null)
				return null;
			return DnlibCecilBridge.TryGetMember(cecilModule, rawToken);
		}

		void DecompileCore(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, string> decompile) {
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module.");
				return;
			}
			string text;
			try {
				lock (engineLock) {
					if (!engineCache.TryGetValue(cecilModule, out var engine)) {
						engine = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(cecilModule, settings.EngineSettings);
						engineCache.Add(cecilModule, engine);
					}
					text = decompile(engine);
				}
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 decompilation failed: " + ex.GetType().Name + ": " + ex.Message);
				return;
			}
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
		}

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
	}
}
