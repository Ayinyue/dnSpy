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

		// CSharpDecompiler is not thread-safe and building its type system is costly, so
		// instances are cached per bridged module and per engine settings. The C# and VB
		// decompilers configure the engine differently (VB disables null-propagation), and
		// the engine reads its settings when it is constructed, so they cannot share one.
		static readonly ConditionalWeakTable<ModuleDefinition, Dictionary<DecompilerSettings32, ICSharpCode.Decompiler.CSharp.CSharpDecompiler>> engineCache = new ConditionalWeakTable<ModuleDefinition, Dictionary<DecompilerSettings32, ICSharpCode.Decompiler.CSharp.CSharpDecompiler>>();
		internal static readonly object engineLock = new object();

		internal static ICSharpCode.Decompiler.CSharp.CSharpDecompiler GetOrCreateEngine(ModuleDefinition cecilModule, DecompilerSettings32 settings) {
			var engines = engineCache.GetOrCreateValue(cecilModule);
			if (!engines.TryGetValue(settings, out var engine)) {
				engine = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(cecilModule, settings.EngineSettings);
				engines.Add(settings, engine);
			}
			return engine;
		}

		/// <summary>
		/// Formats an exception for the failure note written into the output.
		/// Set <c>DNSPY_ILSPY32_DIAG=1</c> to include the stack trace, which is the only
		/// way to locate failures in the VB converter: it catches everything internally.
		/// Off by default, so normal notes stay one line.
		/// </summary>
		internal static string DescribeException(Exception ex) {
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1")
				return ex.ToString().Replace("\r", "").Replace("\n", " | ");
			return ex.GetType().Name + ": " + ex.Message;
		}

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
			DecompileCore(type.Module, output, ctx,
				d => d.DecompileType(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(Common.TypeNameHelper.ToReflectionName(cecilType.FullName))),
				d => d.Decompile(cecilType));
		}

		void DecompileMember(IMDTokenProvider? member, IDecompilerOutput output, DecompilationContext ctx) {
			if (member is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var module = (member as IMemberDef)?.Module;
			if (module is null) {
				WriteNote(output, "// ILSpy 3.2: member has no module.");
				return;
			}
			// Like the 2.3 engine: single-member decompiles start with the declaring type as a
			// comment line; the reference lets dnSpy jump to that type.
			if (member is IMemberDef memberDef)
				WriteCommentLineDeclaringType(output, memberDef);
			var cecilMember = TryGetCecil(module, member.MDToken.Raw) as IMemberDefinition;
			if (cecilMember is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(module, output, ctx, d => d.Decompile(cecilMember));
		}

		static IMemberDefinition? TryGetCecil(ModuleDef module, uint rawToken) {
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null)
				return null;
			return DnlibCecilBridge.TryGetMember(cecilModule, rawToken);
		}

		void DecompileCore(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx,
				Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree> decompile,
				Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree>? fallbackDecompile = null) {
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module.");
				return;
			}
			try {
				lock (engineLock) {
					var engine = GetOrCreateEngine(cecilModule, settings);
					var tree = decompile(engine);
					// The engine hides compiler-generated implementation-detail types entirely
					// (<PrivateImplementationDetails> renders empty via MemberIsHidden); the 2.3
					// engine showed their members. Re-decompile through the member list, which
					// has no such filter.
					if (fallbackDecompile != null && tree.Members.Count == 0)
						tree = fallbackDecompile(engine);
					StripCommentsInsideFieldDeclarations(tree);
					// Definitions on the tree (the resolver reads them for colors/references),
					// then the uncompilable compiler-generated attributes, then render. A binder
					// failure must not kill the decompilation: degrade to unannotated output.
					try {
						AstAnnotationBinder.Attach(module, tree);
					}
					catch {
						// annotations stay absent; rendering continues
					}
					RemoveUncompilableCompilerAttributes(tree);
					tree.AcceptVisitor(new ICSharpCode.Decompiler.CSharp.OutputVisitor.CSharpOutputVisitor(
						new DnSpyTokenWriter(output, engine, module as ModuleDefMD, VisualBasicMetadataTextColorProvider.Instance),
						settings.EngineSettings.CSharpFormattingOptions));
				}
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 decompilation failed: " + DescribeException(ex));
				return;
			}
		}

		/// <summary>
		/// Drops the IteratorStateMachine/AsyncStateMachine attributes. Their typeof(&lt;X&gt;d__N)
		/// argument names an identifier C# cannot express, so they render as syntax errors; they
		/// only name the compiler's own state-machine type, which is re-derived when the decompiled
		/// source compiles, and the 2.3 engine never emitted them.
		/// </summary>
		static void RemoveUncompilableCompilerAttributes(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree) {
			foreach (var section in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.AttributeSection>().ToList()) {
				var removed = false;
				foreach (var attribute in section.Attributes.ToList()) {
					var name = attribute.Type switch {
						ICSharpCode.Decompiler.CSharp.Syntax.SimpleType simple => simple.Identifier,
						ICSharpCode.Decompiler.CSharp.Syntax.MemberType member => member.MemberName,
						_ => null,
					};
					// C# lets attribute names drop the "Attribute" suffix, and the engine does.
					if (name == "IteratorStateMachineAttribute" || name == "AsyncStateMachineAttribute" ||
							name == "IteratorStateMachine" || name == "AsyncStateMachine") {
						attribute.Remove();
						removed = true;
					}
				}
				if (removed && !section.Attributes.Any())
					section.Remove();
			}
		}

		/// <summary>
		/// Removes comment nodes that the engine placed INSIDE field declarations (its
		/// "not supported: data(...)" notes on compiler-generated data fields). Rendered
		/// inline they split the declaration in half in both C# and VB.
		/// </summary>
		static void StripCommentsInsideFieldDeclarations(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree) {
			foreach (var field in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration>().ToList()) {
				foreach (var comment in field.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.Comment>().ToList())
					comment.Remove();
			}
		}

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
	}
}
