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
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.Common;
using dnSpy.Decompiler.ILSpy32.CSharp;
using dnSpy.Decompiler.ILSpy32.IL;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.ILAst {
	/// <summary>
	/// ILAst language backed by the ILSpy 3.2 engine's IL AST (<see cref="ICSharpCode.Decompiler.IL.ILFunction"/>).
	///
	/// The 2.3 ILAst view was a <c>#if DEBUG</c> dev tool built on the 2.3 engine's
	/// <c>ICSharpCode.Decompiler.ILAst</c> pipeline (stack AST + ILAstOptimizer). The 3.2 engine
	/// has the successor of that pipeline - <see cref="ILReader"/> produces an
	/// <see cref="ILFunction"/>, <see cref="ICSharpCode.Decompiler.CSharp.CSharpDecompiler.GetILTransforms"/>
	/// runs the optimizer, and <see cref="ILFunction.WriteTo"/> renders the same block/IL style.
	/// This view ports the dnSpy-side glue: bridge dnlib to the engine's type system, run the
	/// transforms, and write the AST through the shared text-output bridge.
	///
	/// Differences from the 2.3 view: one language instead of one per optimizer step (the
	/// engine exposes the transform list, not numbered optimizer phases; use the
	/// <c>Run IL transforms</c> option to compare raw and optimized output), and debug info is
	/// built from the per-instruction text spans the writer reports.
	/// </summary>
	sealed class ILAstDecompiler32 : DecompilerBase {
		static readonly Guid THE_GUID = new Guid("3F8B2E71-6C4A-4D95-A2B7-9E1F0C5D8A34");

		readonly ILAstDecompilerSettings32 settings = new ILAstDecompilerSettings32();
		readonly string uniqueNameUI;

		public ILAstDecompiler32(string uniqueNameUI) {
			this.uniqueNameUI = uniqueNameUI ?? throw new ArgumentNullException(nameof(uniqueNameUI));
		}

		public override DecompilerSettingsBase Settings => settings;
		// Same content type as the 2.3 ILAst language so the editor reuses its text-view settings.
		public override string ContentTypeString => ContentTypeDefinitions.ILAstILSpy;
		public override string GenericNameUI => "ILAst";
		public override string UniqueNameUI => uniqueNameUI;
		public override double OrderUI => DecompilerConstants.ILAST_ILSPY_DEBUG_ORDERUI + 0.5;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_ILAST_ILSPY;
		public override Guid UniqueGuid => THE_GUID;
		public override string FileExtension => ".il";

		protected override void TypeToString(IDecompilerOutput output, ITypeDefOrRef? t, bool includeNamespace, IHasCustomAttribute? attributeProvider = null) =>
			t.WriteTo(output, new StringBuilder(), includeNamespace ? ILNameSyntax.TypeName : ILNameSyntax.ShortTypeName);

		public override void WriteToolTip(ITextColorWriter output, IMemberRef member, IHasCustomAttribute? typeAttributes) {
			if (!(member is ITypeDefOrRef) && ILDecompilerUtils32.Write(TextColorWriterToDecompilerOutput.Create(output), member))
				return;
			base.WriteToolTip(output, member, typeAttributes);
		}

		const string NoBridgeNote = "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).";
		const string NoBodyNote = "// ILSpy 3.2: the member has no body.";

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			if (method is null || output is null || ctx is null)
				throw new ArgumentNullException();
			WriteCommentBegin(output, true);
			output.Write("Method: ", BoxedTextColor.Comment);
			output.Write(method.FullName, method, DecompilerReferenceFlags.Definition, BoxedTextColor.Comment);
			WriteCommentEnd(output, true);
			output.WriteLine();

			if (!method.HasBody) {
				WriteNote(output, NoBodyNote);
				return;
			}

			var cecilModule = DnlibCecilBridge.TryGetModule(method.Module);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			var cecilMethod = DnlibCecilBridge.TryGetMember(cecilModule, method.MDToken.Raw) as MethodDefinition;
			if (cecilMethod is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}

			try {
				DecompileCore(method, cecilMethod, cecilModule, output, ctx);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 ILAst failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		void DecompileCore(MethodDef method, MethodDefinition cecilMethod, ModuleDefinition cecilModule, IDecompilerOutput output, DecompilationContext ctx) {
			ctx.CancellationToken.ThrowIfCancellationRequested();

			// The engine's ILReader needs an IDecompilerTypeSystem. The C# decompiler instance
			// (cached per module and settings) already owns one, so reuse it instead of building
			// a second type system for the same module.
			var engine = CSharpDecompiler32.GetOrCreateEngine(cecilModule, settings.DecompilerSettings);
			var typeSystem = DnlibCecilBridge.TryGetTypeSystem(engine) ?? new DecompilerTypeSystem(cecilModule);
			var reader = new ILReader(typeSystem) {
				UseDebugSymbols = settings.DecompilerSettings.EngineSettings.UseDebugSymbols,
			};
			var function = reader.ReadIL(cecilMethod.Body, ctx.CancellationToken);

			if (settings.RunTransforms) {
				var context = engine.CreateILTransformContext(function);
				context.CancellationToken = ctx.CancellationToken;
				function.RunTransforms(ICSharpCode.Decompiler.CSharp.CSharpDecompiler.GetILTransforms(), context);
			}

			foreach (var warning in reader.Warnings)
				output.WriteLine("// " + warning, BoxedTextColor.Comment);

			var bridge = new DecompilerOutputTextOutput(output, method.Module) {
				// The ILAst writer reports its own brace pairs; the bridge's delimiter-based
				// reconstruction (used by the IL view) would add duplicates.
				TrackDelimiters = false,
			};
			var builder = new MethodDebugInfoBuilder(1, StateMachineKind.None, method, null,
				CreateLocals(method), CreateParameters(method), null);
			builder.Scope.Span = ILSpan.FromBounds(0, (uint)method.Body.GetCodeSize());

			var options = new ILAstWritingOptions {
				ShowILRanges = settings.ShowILRanges,
				UseLogicOperationSugar = settings.UseLogicOperationSugar,
				UseFieldSugar = settings.UseFieldSugar,
				WriteCodeBracesRanges = true,
				DebugInfoWriter = new StatementCollector(builder),
			};
			function.WriteTo(bridge, options);

			if (ctx.CalculateILSpans) {
				builder.CompactILSpans((uint)method.Body.GetCodeSize());
				output.AddDebugInfo(builder.Create());
			}
		}

		static SourceLocal[] CreateLocals(MethodDef method) {
			var locals = new List<SourceLocal>();
			foreach (var local in method.Body.Variables)
				locals.Add(new SourceLocal(local, local.Name ?? string.Empty, local.Type, SourceVariableFlags.None));
			return locals.ToArray();
		}

		static SourceParameter[] CreateParameters(MethodDef method) {
			var parameters = new List<SourceParameter>();
			foreach (var parameter in method.Parameters)
				parameters.Add(new SourceParameter(parameter, parameter.Name ?? string.Empty, parameter.Type, SourceVariableFlags.None));
			return parameters.ToArray();
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			if (property is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DecompileAccessors(new[] { property.GetMethod, property.SetMethod }.Concat(property.OtherMethods), output, ctx);
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			if (ev is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DecompileAccessors(new[] { ev.AddMethod, ev.RemoveMethod }.Concat(ev.OtherMethods), output, ctx);
		}

		void DecompileAccessors(IEnumerable<MethodDef?> accessors, IDecompilerOutput output, DecompilationContext ctx) {
			var first = true;
			foreach (var accessor in accessors) {
				if (accessor is null)
					continue;
				if (!first)
					output.WriteLine();
				first = false;
				Decompile(accessor, output, ctx);
			}
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			if (field is null || output is null || ctx is null)
				throw new ArgumentNullException();
			output.Write(IdentifierEscaper.Escape(field.FieldType.GetFullName()), field.FieldType.ToTypeDefOrRef(),
				DecompilerReferenceFlags.None, BoxedTextColor.Text);
			output.Write(" ", BoxedTextColor.Text);
			output.Write(IdentifierEscaper.Escape(field.Name), field, DecompilerReferenceFlags.Definition, BoxedTextColor.Text);
			var c = field.Constant;
			if (c is not null) {
				output.Write(" = ", BoxedTextColor.Operator);
				output.Write(c.Value is null ? "null" : c.Value.ToString(), BoxedTextColor.Text);
			}
			output.WriteLine();
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			if (type is null || output is null || ctx is null)
				throw new ArgumentNullException();
			WriteCommentBegin(output, true);
			output.Write("Type: ", BoxedTextColor.Comment);
			output.Write(IdentifierEscaper.Escape(type.FullName), type, DecompilerReferenceFlags.Definition, BoxedTextColor.Comment);
			WriteCommentEnd(output, true);
			output.WriteLine();

			if (type.BaseType is not null) {
				WriteCommentBegin(output, true);
				output.Write("Base type: ", BoxedTextColor.Comment);
				output.Write(IdentifierEscaper.Escape(type.BaseType.FullName), type.BaseType, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				WriteCommentEnd(output, true);
				output.WriteLine();
			}

			foreach (var nested in type.NestedTypes) {
				Decompile(nested, output, ctx);
				output.WriteLine();
			}

			var lastFieldPos = -1;
			foreach (var field in type.Fields) {
				Decompile(field, output, ctx);
				lastFieldPos = output.NextPosition;
				output.WriteLine();
			}
			if (lastFieldPos >= 0) {
				output.AddLineSeparator(lastFieldPos);
				output.WriteLine();
			}

			foreach (var property in type.Properties) {
				ILDecompilerUtils32.WriteProperty(output, property, full: false);
				output.WriteLine();
			}

			foreach (var ev in type.Events) {
				ILDecompilerUtils32.WriteEvent(output, ev, full: false);
				output.WriteLine();
			}

			foreach (var method in type.Methods) {
				Decompile(method, output, ctx);
				output.WriteLine();
			}
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) =>
			Decompile(asm.ManifestModule, output, ctx);

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			if (mod is null || output is null || ctx is null)
				throw new ArgumentNullException();
			foreach (var type in mod.Types) {
				Decompile(type, output, ctx);
				output.WriteLine();
			}
		}

		internal static void WriteCommentBegin(IDecompilerOutput output, bool addSpace) {
			output.Write("// ", BoxedTextColor.Comment);
			if (addSpace)
				output.Write(" ", BoxedTextColor.Text);
		}

		internal static void WriteCommentEnd(IDecompilerOutput output, bool addLineSeparator) {
			if (addLineSeparator)
				output.AddLineSeparator(output.NextPosition);
			output.WriteLine();
		}

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);

		/// <summary>
		/// Collects the per-instruction text spans reported by the engine's ILAst writer and
		/// turns them into debug-info source statements.
		/// </summary>
		sealed class StatementCollector : ILAstDebugInfoWriter {
			readonly MethodDebugInfoBuilder builder;

			public StatementCollector(MethodDebugInfoBuilder builder) {
				this.builder = builder ?? throw new ArgumentNullException(nameof(builder));
			}

			public void WriteStatement(Interval ilRange, int textStart, int textEnd) {
				if (ilRange.IsEmpty || textEnd <= textStart)
					return;
				builder.Add(new SourceStatement(
					ILSpan.FromBounds((uint)ilRange.Start, (uint)ilRange.End),
					TextSpan.FromBounds(textStart, textEnd)));
			}
		}
	}
}
