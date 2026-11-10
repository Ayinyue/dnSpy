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

		public override bool CanDecompile(DecompilationType decompilationType) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
			case DecompilationType.AssemblyInfo:
			case DecompilationType.TypeMethods:
				return true;
			}
			return base.CanDecompile(decompilationType);
		}

		public override void Decompile(DecompilationType decompilationType, object data) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
				DecompilePartial((DecompilePartialType)data);
				return;
			case DecompilationType.AssemblyInfo:
				DecompileAssemblyInfo((DecompileAssemblyInfo)data);
				return;
			case DecompilationType.TypeMethods:
				DecompileTypeMethods((DecompileTypeMethods)data);
				return;
			}
			base.Decompile(decompilationType, data);
		}

		/// <summary>
		/// Export-to-project partial type: the 3.2 engine renders the whole type (the 2.3
		/// engine filtered designer-file members out of the partial class).
		/// </summary>
		void DecompilePartial(DecompilePartialType info) {
			var cecilType = TryGetCecil(info.Type.Module, info.Type.MDToken.Raw) as TypeDefinition;
			if (cecilType is null) {
				WriteNote(info.Output, "' ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(info.Type.Module, info.Output, info.Context,
				d => d.DecompileTypes(new[] { cecilType }),
				d => d.Decompile(cecilType),
				knownType: info.Type);
		}

		void DecompileAssemblyInfo(DecompileAssemblyInfo info) {
			DecompileCore(info.Module, info.Output, info.Context,
				d => d.DecompileModuleAndAssemblyAttributes());
		}

		void DecompileTypeMethods(DecompileTypeMethods info) {
			// No dnSpy consumer passes TypeMethods today; decompile the whole type.
			DecompilePartial(new DecompilePartialType(info.Output, info.Context, info.Type));
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			if (type is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var cecilType = TryGetCecil(type.Module, type.MDToken.Raw) as TypeDefinition;
			if (cecilType is null) {
				WriteNote(output, "' ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			// DecompileTypes (not DecompileType): the latter resolves the name to its TOP-LEVEL
			// type, so a nested type node rendered its whole declaring type (mirrors the C# fix).
			DecompileCore(type.Module, output, ctx,
				d => d.DecompileTypes(new[] { cecilType }),
				d => d.Decompile(cecilType),
				knownType: type);
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

		void DecompileCore(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx,
				Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree> decompile,
				Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree>? fallbackDecompile = null,
				TypeDef? knownType = null) {
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
					// The engine hides compiler-generated implementation-detail types entirely
					// (<PrivateImplementationDetails> renders empty via MemberIsHidden); the 2.3
					// engine showed their members. Re-decompile through the member list, which
					// has no such filter.
					if (fallbackDecompile != null && csharpUnit.Members.Count == 0)
						csharpUnit = fallbackDecompile(engine);
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
				AstAnnotationBinder.Attach(module, csharpUnit, knownType);
				// The 2.3 engine shows a 'Token: 0x... RID: ...' comment before every member
				// declaration when ShowTokenAndRvaComments is on; the converter turns those
				// comments into VB quotes.
				CSharpDecompiler32.InsertTokenComments(csharpUnit, module as ModuleDefMD, settings.ShowTokenAndRvaComments);
				// Debug locals come from the C# tree's engine variables (the converted VB nodes
				// carry no variable annotations), so collect them before the tree is consumed.
				var pendingLocals = CollectPendingLocals(module as ModuleDefMD, csharpUnit);
				var converter = new CSharpToVBConverter32(module, new ILSpyEnvironmentProvider32(), engineTypeSystem);
				var unit = (CompilationUnit)csharpUnit.AcceptVisitor(converter, null);
				ApplyIdentifierRenames(csharpUnit, unit);
				var outputFormatter = new VBTextOutputFormatter32(output, 1, ctx.CalculateILSpans, pendingLocals);
				var formattingPolicy = new VBFormattingOptions {
					NumberFormatter = ICSharpCode.NRefactory.NumberFormatter.GetVBInstance(hex: false, upper: true),
				};
				unit.AcceptVisitor(new OutputVisitor(outputFormatter, formattingPolicy), null);
				// Hand dnSpy the method-level debug spans collected during rendering.
				foreach (var debugInfo in outputFormatter.DebugInfos)
					output.AddDebugInfo(debugInfo);
			}
			catch (Exception ex) {
				WriteNote(output, "' ILSpy 3.2 VB conversion failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);

		/// <summary>
		/// Debug locals for the Locals window: walks the C# tree the way the engine's own
		/// sequence-point builder does (node annotations lead to IL instructions whose
		/// ancestors contain the ILFunction), then maps each function's variables - which
		/// carry the IL slot index and the decompiled name - onto the method's dnlib locals.
		/// Local/UsingLocal/ForeachLocal kinds bind by slot; parameters, stack slots and
		/// exception variables are excluded, as are engine-lifted variables whose slot index
		/// is out of range.
		/// </summary>
		static Dictionary<MethodDef, SourceLocal[]> CollectPendingLocals(ModuleDefMD? module, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree csharpUnit) {
			var result = new Dictionary<MethodDef, SourceLocal[]>();
			if (module is null || csharpUnit is null)
				return result;
			try {
				var functions = new HashSet<ICSharpCode.Decompiler.IL.ILFunction>();
				foreach (var node in csharpUnit.Descendants) {
					foreach (var ann in node.Annotations) {
						if (ann is ICSharpCode.Decompiler.IL.ILInstruction inst) {
							var function = inst.Ancestors.OfType<ICSharpCode.Decompiler.IL.ILFunction>().FirstOrDefault();
							if (function?.CecilMethod is not null)
								functions.Add(function);
						}
					}
				}
				foreach (var function in functions) {
					if (!(module.ResolveToken((uint)function.CecilMethod.MetadataToken.ToInt32()) is MethodDef method) || method.Body is null)
						continue;
					var locals = new List<SourceLocal>();
					foreach (var v in function.Variables) {
						if (v.Index < 0 || v.Index >= method.Body.Variables.Count)
							continue;
						if (v.Kind == ICSharpCode.Decompiler.IL.VariableKind.Parameter ||
								v.Kind == ICSharpCode.Decompiler.IL.VariableKind.StackSlot ||
								v.Kind == ICSharpCode.Decompiler.IL.VariableKind.Exception)
							continue;
						var dnlibLocal = method.Body.Variables[v.Index];
						locals.Add(new SourceLocal(dnlibLocal, v.Name ?? dnlibLocal.Name ?? string.Empty,
							dnlibLocal.Type, SourceVariableFlags.None));
					}
					result[method] = locals.ToArray();
				}
			}
			catch {
				// locals mapping is an enhancement; never break the decompilation
			}
			return result;
		}
	}
}