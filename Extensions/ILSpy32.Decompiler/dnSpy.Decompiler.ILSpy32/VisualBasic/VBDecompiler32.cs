// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// Copyright (c) 2014-2019 de4dot@gmail.com
//
// Visual Basic decompiler backed by the ILSpy 3.2 engine. It reuses the dnlib-to-cecil
// bridge and the shared engine cache from CSharpDecompiler32, asks the 3.2 engine for a
// C# SyntaxTree, converts it to a VB AST with CSharpToVBConverter32, then renders the VB
// AST through OutputVisitor + VBTextOutputFormatter32.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.Common;
using dnSpy.Decompiler.ILSpy32.CSharp;
using dnSpy.Decompiler.ILSpy32.Settings;
using ICSharpCode.NRefactory.VB;
using ICSharpCode.NRefactory.VB.Ast;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.VisualBasic {
	sealed class VBDecompiler32 : DecompilerBase {
		static readonly Guid THE_GUID = new Guid("9C5E4C7A-1F2B-4E6D-9A30-8B7D2E5F6A41");

		readonly DecompilerSettings32 settings;
		readonly string uniqueNameUI;

		public VBDecompiler32(DecompilerSettings32 settings, string uniqueNameUI) {
			this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
			this.uniqueNameUI = uniqueNameUI ?? throw new ArgumentNullException(nameof(uniqueNameUI));
		}

		public override DecompilerSettingsBase Settings => settings;
		public override string ContentTypeString => "VB ILSpy";
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_VISUALBASIC;
		public override string UniqueNameUI => uniqueNameUI;
		public override double OrderUI => DecompilerConstants.VISUALBASIC_ILSPY_ORDERUI + 0.5;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_VISUALBASIC;
		public override Guid UniqueGuid => THE_GUID;
		public override string FileExtension => ".vb";
		public override string? ProjectFileExtension => ".vbproj";

		public override void WriteCommentBegin(IDecompilerOutput output, bool addSpace) {
			if (addSpace)
				output.Write("' ", BoxedTextColor.Comment);
			else
				output.Write("'", BoxedTextColor.Comment);
		}

		public override void WriteCommentEnd(IDecompilerOutput output, bool addSpace) { }

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
				WriteNote(output, "' ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(type.Module, output, ctx, d => d.DecompileType(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(Common.TypeNameHelper.ToReflectionName(cecilType.FullName))));
		}

		void DecompileMember(IMDTokenProvider? member, IDecompilerOutput output, DecompilationContext ctx) {
			if (member is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var module = (member as IMemberDef)?.Module;
			if (module is null) {
				WriteNote(output, "' ILSpy 3.2: member has no module.");
				return;
			}
			// Like the 2.3 engine: single-member decompiles start with the declaring type as a
			// comment line; the reference lets dnSpy jump to that type.
			if (member is IMemberDef memberDef)
				WriteCommentLineDeclaringType(output, memberDef);
			var cecilMember = TryGetCecil(module, member.MDToken.Raw) as IMemberDefinition;
			if (cecilMember is null) {
				WriteNote(output, "' ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
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

		/// <summary>
		/// Applies the renames the binder computed for VB's case-insensitive name space. Both the
		/// declarations and the references go through Identifier, so renaming the nodes keeps them
		/// consistent.
		/// </summary>
		static void ApplyIdentifierRenames(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree csharpUnit, CompilationUnit unit) {
			var renames = csharpUnit.Annotation<Dictionary<string, string>>();
			if (renames is null || renames.Count == 0)
				return;
			foreach (var identifier in unit.Descendants.OfType<Identifier>()) {
				if (renames.TryGetValue(identifier.Name, out var renamed))
					identifier.Name = renamed;
			}
		}

		void DecompileCore(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree> decompile) {
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null) {
				WriteNote(output, "' ILSpy 3.2: cannot bridge this module.");
				return;
			}
			ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree csharpUnit;
			ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem? engineTypeSystem = null;
			try {
				lock (CSharpDecompiler32.engineLock) {
					var engine = CSharpDecompiler32.GetOrCreateEngine(cecilModule, settings);
					csharpUnit = decompile(engine);
					engineTypeSystem = DnlibCecilBridge.TryGetTypeSystem(engine);
				}
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "' ILSpy 3.2 VB decompilation failed: " + CSharpDecompiler32.DescribeException(ex));
				return;
			}

			try {
				// The 3.2 tree has no dnlib annotations; the ported converter needs them.
				AstAnnotationBinder.Attach(module, csharpUnit);
				var converter = new CSharpToVBConverter32(module, new ILSpyEnvironmentProvider32(), engineTypeSystem);
				var unit = (CompilationUnit)csharpUnit.AcceptVisitor(converter, null);
				ApplyIdentifierRenames(csharpUnit, unit);
				var outputFormatter = new VBTextOutputFormatter32(output);
				var formattingPolicy = new VBFormattingOptions {
					NumberFormatter = ICSharpCode.NRefactory.NumberFormatter.GetVBInstance(hex: false, upper: true),
				};
				unit.AcceptVisitor(new OutputVisitor(outputFormatter, formattingPolicy), null);
			}
			catch (Exception ex) {
				WriteNote(output, "' ILSpy 3.2 VB conversion failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
	}
}