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
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.CSharp;
using dnSpy.Decompiler.ILSpy32.Common;
using dnSpy.Decompiler.ILSpy32.IL;
using dnSpy.Decompiler.ILSpy32.Settings;
using dnSpy.Decompiler.Utils;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace dnSpy.Decompiler.ILSpy32.Mixed {
	/// <summary>
	/// "IL with C#" language: the 3.2 engine's IL disassembly with the C# source line each
	/// instruction came from written above it, ported from the 2.3 Mixed view.
	///
	/// The 2.3 view used the 2.3 engine's <c>MethodBodyDisassembler</c> with a
	/// <c>MethodDebugInfoBuilder</c> and the 2.3 C# <c>AstBuilder</c>. The 3.2 engine has no
	/// debug-info hook on its disassembler, so this view decompiles the method once with the
	/// 3.2 C# decompiler (capturing the text and its statements), then lets a custom
	/// <see cref="MethodBodyDisassembler"/> look up the statement for each instruction offset.
	/// </summary>
	sealed class ILWithCSharpDecompiler32 : DecompilerBase {
		static readonly Guid THE_GUID = new Guid("5D2C8E47-9A31-4B68-BE05-2F7C1A9D63B8");

		readonly ILDecompilerSettings32 ilLangSettings = new ILDecompilerSettings32();
		readonly DecompilerSettings32 csharpLangSettings = new DecompilerSettings32();
		readonly string uniqueNameUI;

		public ILWithCSharpDecompiler32(string uniqueNameUI) {
			this.uniqueNameUI = uniqueNameUI ?? throw new ArgumentNullException(nameof(uniqueNameUI));
		}

		public override DecompilerSettingsBase Settings => ilLangSettings;
		public override string ContentTypeString => "IL with C# ILSpy";
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_IL_WITH_CSHARP;
		public override string UniqueNameUI => uniqueNameUI;
		public override double OrderUI => DecompilerConstants.IL_WITH_CSHARP_ILSPY_ORDERUI + 0.5;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_IL;
		public override Guid UniqueGuid => THE_GUID;
		public override string FileExtension => ".il";

		const string NoBridgeNote = "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).";
		const string NoModuleNote = "// ILSpy 3.2: member has no module.";

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			if (method is null || output is null || ctx is null)
				throw new ArgumentNullException();
			if (method.Module is null) {
				WriteNote(output, NoModuleNote);
				return;
			}
			ctx.CancellationToken.ThrowIfCancellationRequested();
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
				var dis = CreateReflectionDisassembler(output, ctx, method.Module, m => GetSourceStatementProvider(m, ctx), out var bridge);
				dis.DisassembleMethod(cecilMethod);
				AddDebugInfo(method, bridge, output, ctx);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 mixed disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			if (field is null || output is null || ctx is null)
				throw new ArgumentNullException();
			if (field.Module is null) {
				WriteNote(output, NoModuleNote);
				return;
			}
			var cecilModule = DnlibCecilBridge.TryGetModule(field.Module);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			var cecilField = DnlibCecilBridge.TryGetMember(cecilModule, field.MDToken.Raw) as FieldDefinition;
			if (cecilField is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			try {
				CreateReflectionDisassembler(output, ctx, field.Module, null, out _).DisassembleField(cecilField);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 mixed disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
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

		void DecompileAccessors(System.Collections.Generic.IEnumerable<MethodDef?> accessors, IDecompilerOutput output, DecompilationContext ctx) {
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

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			if (type is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var cecilModule = DnlibCecilBridge.TryGetModule(type.Module);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			var cecilType = DnlibCecilBridge.TryGetMember(cecilModule, type.MDToken.Raw) as TypeDefinition;
			if (cecilType is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			try {
				CreateReflectionDisassembler(output, ctx, type.Module, null, out _).DisassembleType(cecilType);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 mixed disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			if (asm is null || output is null || ctx is null)
				throw new ArgumentNullException();
			output.WriteLine("// " + asm.ManifestModule.Location, BoxedTextColor.Comment);
			PrintEntryPoint(asm.ManifestModule, output);
			output.WriteLine();
			var cecilModule = DnlibCecilBridge.TryGetModule(asm.ManifestModule);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			try {
				var cecilAssembly = cecilModule.Assembly;
				if (cecilAssembly is null) {
					WriteNote(output, "// ILSpy 3.2: the bridged module has no assembly.");
					return;
				}
				CreateReflectionDisassembler(output, ctx, asm.ManifestModule, null, out _).WriteAssemblyHeader(cecilAssembly);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 mixed disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			if (mod is null || output is null || ctx is null)
				throw new ArgumentNullException();
			output.WriteLine("// " + mod.Location, BoxedTextColor.Comment);
			PrintEntryPoint(mod, output);
			output.WriteLine();
			var cecilModule = DnlibCecilBridge.TryGetModule(mod);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			try {
				CreateReflectionDisassembler(output, ctx, mod, null, out _).WriteModuleHeader(cecilModule);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 mixed disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		ReflectionDisassembler CreateReflectionDisassembler(IDecompilerOutput output, DecompilationContext ctx, ModuleDef module,
				Func<MethodDef, SourceStatementProvider32>? createProvider, out DecompilerOutputTextOutput bridge) {
			bridge = new DecompilerOutputTextOutput(output, module);
			var bodyDisassembler = new MixedMethodBodyDisassembler(bridge, ctx.CancellationToken, module, createProvider, ctx.IsBodyModified);
			bridge.BodyTracker = bodyDisassembler;
			var disassembler = new ReflectionDisassembler(bridge, bodyDisassembler, ctx.CancellationToken) {
				DetectControlStructure = true,
			};
			// XML documentation above each member header, like the 2.3 Mixed view (its
			// ShowXmlDocumentation option defaulted to on).
			var sb = new StringBuilder();
			disassembler.GetXmlDocComments = m => XmlDocComments32.GetXmlDocComments(module, m, sb);
			// '// Token: 0x... RID: ... RVA: ... File Offset: ...' above the header, like the
			// 2.3 view's ShowTokenAndRvaComments (also on by default there).
			disassembler.GetTokenAndRvaComment = m => TokenComment32.GetComment(module, m);
			// Body header block, local names, IL bytes and PDB info: the 2.3 disassembler
			// options that the Mixed view showed by default. These are the inherited engine fields
			// (declaring same-named members here would hide them).
			bodyDisassembler.GetBodyHeaderComments = m => ToDnlib(module, m) is MethodDef dm ? MixedDisassemblyComments32.GetHeaderComments(dm) : null;
			bodyDisassembler.GetLocalNames = m => ToDnlib(module, m) is MethodDef dm ? MixedDisassemblyComments32.GetLocalNames(dm) : null;
			return disassembler;
		}

		/// <summary>Bridges a cecil method back to its dnlib counterpart (rids are preserved).</summary>
		static MethodDef? ToDnlib(ModuleDef module, MethodDefinition method) {
			try {
				return module.ResolveToken((uint)method.MetadataToken.ToInt32()) as MethodDef;
			}
			catch {
				return null;
			}
		}

		/// <summary>
		/// Same per-instruction debug info as the IL view: one statement per instruction, so the
		/// debugger highlights the executing instruction in the Mixed view too.
		/// </summary>
		void AddDebugInfo(MethodDef method, DecompilerOutputTextOutput bridge, IDecompilerOutput output, DecompilationContext ctx) {
			if (!ctx.CalculateILSpans || method.Body is null || bridge.InstructionRanges.Count == 0)
				return;
			var locals = new System.Collections.Generic.List<SourceLocal>();
			foreach (var l in method.Body.Variables)
				locals.Add(new SourceLocal(l, l.Name ?? string.Empty, l.Type, SourceVariableFlags.None));
			var parameters = new System.Collections.Generic.List<SourceParameter>();
			foreach (var p in method.Parameters)
				parameters.Add(new SourceParameter(p, p.Name ?? string.Empty, p.Type, SourceVariableFlags.None));
			var builder = new MethodDebugInfoBuilder(1, StateMachineKind.None, method, null, locals.ToArray(), parameters.ToArray(), null);
			builder.Scope.Span = ILSpan.FromBounds(0, (uint)method.Body.GetCodeSize());
			var ranges = bridge.InstructionRanges;
			for (var i = 0; i < ranges.Count; i++) {
				var nextIl = i + 1 < ranges.Count ? ranges[i + 1].IlOffset : method.Body.GetCodeSize();
				builder.Add(new SourceStatement(
					ILSpan.FromBounds((uint)ranges[i].IlOffset, (uint)nextIl),
					TextSpan.FromBounds(ranges[i].Start, ranges[i].End)));
			}
			builder.CompactILSpans((uint)method.Body.GetCodeSize());
			output.AddDebugInfo(builder.Create());
		}

		/// <summary>
		/// Decompiles the method once with the 3.2 C# decompiler and captures the source text
		/// plus its statement table. State-machine methods decompile through their kickoff
		/// method first (same order of attempts as the 2.3 view), falling back to the body
		/// method with async/yield decompilation disabled.
		/// </summary>
		SourceStatementProvider32 GetSourceStatementProvider(MethodDef method, DecompilationContext ctx) {
			var output = new DecompilerOutputImpl32(new StringBuilder());

			MethodDebugInfo debugInfo;
			if (StateMachineHelpers.TryGetKickoffMethod(method, out var containingMethod)) {
				var info = TryDecompileCode(containingMethod, ctx, output, false);
				if (info.stateMachineDebugInfo is null || info.stateMachineDebugInfo.Method != method)
					info = TryDecompileCode(method, ctx, output, true);
				debugInfo = info.stateMachineDebugInfo ?? info.debugInfo;
			}
			else
				debugInfo = TryDecompileCode(method, ctx, output, true).debugInfo;

			if (debugInfo is null)
				return default;
			return new SourceStatementProvider32(output.ToString(), debugInfo);
		}

		(MethodDebugInfo debugInfo, MethodDebugInfo? stateMachineDebugInfo) TryDecompileCode(MethodDef method, DecompilationContext ctx, DecompilerOutputImpl32 output, bool disableStateMachineDecompilation) {
			output.Initialize(method);
			var settings = (DecompilerSettings32)csharpLangSettings.Clone();
			settings.EngineSettings.UsingDeclarations = false;
			if (disableStateMachineDecompilation) {
				settings.EngineSettings.AsyncAwait = false;
				settings.EngineSettings.YieldReturn = false;
			}
			var csharp = new CSharpDecompiler32(settings, "C# (ILSpy 3.2)");
			// The statement table is the whole point here, so force CalculateILSpans on a copy
			// of the caller's context instead of flipping the shared instance.
			var innerCtx = new DecompilationContext {
				CancellationToken = ctx.CancellationToken,
				IsBodyModified = ctx.IsBodyModified,
				GetDisableAssemblyLoad = ctx.GetDisableAssemblyLoad,
				CalculateILSpans = true,
				AsyncMethodBodyDecompilation = ctx.AsyncMethodBodyDecompilation,
			};
			csharp.Decompile(method, output, innerCtx);
			return output.TryGetMethodDebugInfo();
		}

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);

		/// <summary>
		/// Writes the C# source line a instruction belongs to above the instruction, and tracks
		/// the instruction's output range for the debug info (inherited from
		/// <see cref="BodyTrackingMethodBodyDisassembler"/>).
		/// </summary>
		sealed class MixedMethodBodyDisassembler : BodyTrackingMethodBodyDisassembler {
			readonly ModuleDef module;
			readonly Func<MethodDef, SourceStatementProvider32>? createProvider;
			readonly Func<MethodDef, bool>? isBodyModified;
			SourceStatementProvider32 provider;
			IInstructionBytesReader? byteReader;
			long? baseFileOffset;

			public MixedMethodBodyDisassembler(ITextOutput output, CancellationToken cancellationToken, ModuleDef module,
					Func<MethodDef, SourceStatementProvider32>? createProvider, Func<MethodDef, bool>? isBodyModified)
				: base(output, cancellationToken, module) {
				this.module = module ?? throw new ArgumentNullException(nameof(module));
				this.createProvider = createProvider;
				this.isBodyModified = isBodyModified;
				// dnSpy's 2.3 IL layout: padded operand column and upper-case IL labels.
				UseDnSpyLayout = true;
			}

			public override void Disassemble(MethodBody body) {
				provider = default;
				byteReader = null;
				baseFileOffset = null;
				MethodDef? method = null;
				if (body?.Method is not null &&
					module.ResolveToken((uint)body.Method.MetadataToken.ToInt32()) is MethodDef resolved)
					method = resolved;
				if (method is not null) {
					if (createProvider is not null)
						provider = createProvider(method);
					byteReader = MixedDisassemblyComments32.CreateByteReader(method, isBodyModified?.Invoke(method) == true);
					baseFileOffset = MixedDisassemblyComments32.GetBaseFileOffset(method);
				}
				base.Disassemble(body);
			}

			protected override void WriteInstruction(ITextOutput output, Instruction instruction) {
				var statement = provider.GetStatement((uint)instruction.Offset);
				if (statement.line is not null)
					WriteStatement(output, statement.line, statement.span);

				var method = CurrentMethod;
				var dnlibInstruction = method is null ? null : FindInstruction(instruction.Offset);
				if (method is not null && dnlibInstruction is not null) {
					var sequencePointComment = MixedDisassemblyComments32.GetSequencePointComment(dnlibInstruction);
					if (sequencePointComment is not null)
						output.WriteLine(sequencePointComment);
					foreach (var asyncComment in MixedDisassemblyComments32.GetAsyncComments(method, dnlibInstruction))
						output.WriteLine(asyncComment);
				}

				var byteComment = dnlibInstruction is null ? null :
					MixedDisassemblyComments32.GetByteComment(method!, byteReader, dnlibInstruction, baseFileOffset);
				if (byteComment is not null)
					output.Write(byteComment);

				base.WriteInstruction(output, instruction);
			}

			/// <summary>
			/// The 2.3 view wrote the source line as a <c>// </c> comment followed by a
			/// <c>^</c> underline marking the exact statement columns when the statement does
			/// not start at column 0.
			/// </summary>
			static void WriteStatement(ITextOutput output, string line, TextSpan span) {
				output.WriteLine();

				int pos = 0;
				while (pos < line.Length) {
					int eol = line.IndexOf('\n', pos);
					int nextLineOffset;
					if (eol < 0) {
						eol = line.Length;
						nextLineOffset = eol;
					}
					else {
						nextLineOffset = eol + 1;
						if (eol > 0 && line[eol - 1] == '\r')
							eol--;
					}

					output.Write("// ");
					output.Write(line.Substring(pos, eol - pos));
					output.WriteLine();

					if (pos == 0) {
						int nonSpace = FindNonSpace(line, pos, eol);
						int stmtEnd = Math.Min(eol, span.End);
						if (!(nonSpace >= span.Start && stmtEnd == eol)) {
							output.Write("// ");
							if (span.Start > pos)
								output.Write(new string(' ', span.Start - pos));
							output.Write(new string('^', Math.Max(0, stmtEnd - span.Start)));
							output.WriteLine();
						}
					}

					pos = nextLineOffset;
				}

				output.WriteLine();
			}

			static int FindNonSpace(string lines, int pos, int end) {
				while (pos < end) {
					if (!char.IsWhiteSpace(lines[pos]))
						return pos;
					pos++;
				}
				return -1;
			}
		}
	}
}
