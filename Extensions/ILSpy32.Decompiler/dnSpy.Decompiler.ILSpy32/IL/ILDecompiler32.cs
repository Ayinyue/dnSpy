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
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.CSharp;
using dnSpy.Decompiler.ILSpy32.Common;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using Mono.Cecil;
using Mono.Cecil.Cil;
using DnlibInstruction = dnlib.DotNet.Emit.Instruction;

namespace dnSpy.Decompiler.ILSpy32.IL {
	/// <summary>
	/// IL disassembly language backed by the ILSpy 3.2 engine's
	/// <see cref="ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler"/>, ported from the
	/// 2.3 IL language (dnSpy.Decompiler.ILSpy.Core.IL.ILDecompiler). Every Decompile overload
	/// keeps the 2.3 call sequence (header comment + entry point for assembly/module, member
	/// disassembly plus accessor bodies for properties/events); dnlib definitions are bridged to
	/// the cecil snapshot via their metadata tokens (the bridge preserves rids), and the engine's
	/// <see cref="ITextOutput"/> writes are translated onto dnSpy's
	/// <see cref="IDecompilerOutput"/> with the BoxedTextColor IL palette used by the 2.3 view.
	/// 2.3 options that the 3.2 engine has no hook for (IL comments, token/RVA comments, IL bytes,
	/// member sorting, PDB info, string length/number format, original-bytes reading) are not
	/// exposed; the settings page stays empty.
	/// </summary>
	sealed class ILDecompiler32 : DecompilerBase {
		static readonly Guid THE_GUID = new Guid("7A3F1C2E-8B24-4D57-9C36-1E5A0F4B92D3");

		readonly ILDecompilerSettings32 settings = new ILDecompilerSettings32();
		readonly string uniqueNameUI;

		public ILDecompiler32(string uniqueNameUI) {
			this.uniqueNameUI = uniqueNameUI ?? throw new ArgumentNullException(nameof(uniqueNameUI));
		}

		public override DecompilerSettingsBase Settings => settings;
		// Same content type as the 2.3 IL language (ContentTypesInternal.ILILSpy), so the editor
		// reuses its IL text-view settings (the VB language uses the same literal style).
		public override string ContentTypeString => "IL ILSpy";
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_IL;
		public override string UniqueNameUI => uniqueNameUI;
		// 3.2 convention: the 2.3 sibling's OrderUI + 0.5, so the language sorts after C# and
		// Visual Basic (dnSpy orders languages by ascending OrderUI) and after the 2.3 IL entry.
		public override double OrderUI => DecompilerConstants.IL_ILSPY_ORDERUI + 0.5;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_IL;
		public override Guid UniqueGuid => THE_GUID;
		public override string FileExtension => ".il";

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			if (method is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DisassembleMemberCore<MethodDefinition>(method.Module, c => ResolveByToken<MethodDefinition>(c, method.MDToken.Raw),
				output, ctx, (dis, cecilMethod) => dis.DisassembleMethod(cecilMethod),
				afterBody: bridge => {
					// The 2.3 engine's IL view emitted one statement per instruction (its
					// MethodBodyDisassembler took a MethodDebugInfoBuilder), so dnSpy highlights
					// the executing instruction when stopped inside the IL view. The 3.2 engine's
					// disassembler has no debug-info pipeline - build the same mapping here from
					// the per-instruction output ranges the bridge collected.
					if (!ctx.CalculateILSpans || method.Body is null || bridge.InstructionRanges.Count == 0)
						return;
					var locals = new List<SourceLocal>();
					foreach (var l in method.Body.Variables)
						locals.Add(new SourceLocal(l, l.Name ?? string.Empty, l.Type, SourceVariableFlags.None));
					var parameters = new List<SourceParameter>();
					foreach (var p in method.Parameters)
						parameters.Add(new SourceParameter(p, p.Name ?? string.Empty, p.Type, SourceVariableFlags.None));
					var builder = new MethodDebugInfoBuilder(1, StateMachineKind.None, method, null,
						locals.ToArray(), parameters.ToArray(), null);
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
				});
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			if (field is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DisassembleMemberCore<FieldDefinition>(field.Module, c => ResolveByToken<FieldDefinition>(c, field.MDToken.Raw),
				output, ctx, (dis, cecilField) => dis.DisassembleField(cecilField));
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			if (property is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DisassembleMemberCore<PropertyDefinition>(property.Module, c => ResolveProperty(c, property), output, ctx, (dis, cecilProperty) => {
				// Like the 2.3 language: the property header block first, then the full body of
				// every accessor, each preceded by a blank line.
				dis.DisassembleProperty(cecilProperty);
				if (cecilProperty.GetMethod is not null) {
					output.WriteLine();
					dis.DisassembleMethod(cecilProperty.GetMethod);
				}
				if (cecilProperty.SetMethod is not null) {
					output.WriteLine();
					dis.DisassembleMethod(cecilProperty.SetMethod);
				}
				foreach (var m in cecilProperty.OtherMethods) {
					output.WriteLine();
					dis.DisassembleMethod(m);
				}
			});
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			if (ev is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DisassembleMemberCore<EventDefinition>(ev.Module, c => ResolveEvent(c, ev), output, ctx, (dis, cecilEvent) => {
				dis.DisassembleEvent(cecilEvent);
				if (cecilEvent.AddMethod is not null) {
					output.WriteLine();
					dis.DisassembleMethod(cecilEvent.AddMethod);
				}
				if (cecilEvent.RemoveMethod is not null) {
					output.WriteLine();
					dis.DisassembleMethod(cecilEvent.RemoveMethod);
				}
				foreach (var m in cecilEvent.OtherMethods) {
					output.WriteLine();
					dis.DisassembleMethod(m);
				}
			});
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			if (type is null || output is null || ctx is null)
				throw new ArgumentNullException();
			DisassembleMemberCore<TypeDefinition>(type.Module, c => ResolveByToken<TypeDefinition>(c, type.MDToken.Raw),
				output, ctx, (dis, cecilType) => dis.DisassembleType(cecilType));
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			if (asm is null || output is null || ctx is null)
				throw new ArgumentNullException();
			// Same prologue as the 2.3 language: location comment, entry point, blank line.
			output.WriteLine("// " + asm.ManifestModule.Location, BoxedTextColor.Comment);
			PrintEntryPoint(asm.ManifestModule, output);
			output.WriteLine();
			DisassembleModuleCore(asm.ManifestModule, output, ctx, (dis, cecilModule) => {
				var cecilAssembly = cecilModule.Assembly;
				if (cecilAssembly is null) {
					WriteNote(output, "// ILSpy 3.2: the bridged module has no assembly.");
					return;
				}
				dis.WriteAssemblyHeader(cecilAssembly);
			});
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			if (mod is null || output is null || ctx is null)
				throw new ArgumentNullException();
			output.WriteLine("// " + mod.Location, BoxedTextColor.Comment);
			PrintEntryPoint(mod, output);
			output.WriteLine();
			DisassembleModuleCore(mod, output, ctx, (dis, cecilModule) => {
				output.WriteLine();
				dis.WriteModuleHeader(cecilModule);
			});
		}

		const string NoBridgeNote = "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).";
		const string NoModuleNote = "// ILSpy 3.2: member has no module.";

		static void WriteNote(IDecompilerOutput output, string text) =>
			output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);

		ReflectionDisassembler CreateDisassembler(IDecompilerOutput output, DecompilationContext ctx, ModuleDef module, ModuleDefinition cecilModule) =>
			CreateDisassembler(output, ctx, module, cecilModule, out _);

		ReflectionDisassembler CreateDisassembler(IDecompilerOutput output, DecompilationContext ctx, ModuleDef module, ModuleDefinition cecilModule, out DecompilerOutputTextOutput bridge) {
			bridge = new DecompilerOutputTextOutput(output, module);
			var bodyDisassembler = new BodyTrackingMethodBodyDisassembler(bridge, ctx.CancellationToken, module);
			bridge.BodyTracker = bodyDisassembler;
			return new ReflectionDisassembler(bridge, bodyDisassembler, ctx.CancellationToken) {
				// The 2.3 language only ever instantiated the structured variant (flat IL was dead code).
				DetectControlStructure = true,
			};
		}

		/// <summary>
		/// Bridges the dnlib module to the cecil snapshot, resolves the member with
		/// <paramref name="resolve"/>, wires the disassembler onto the dnSpy output and runs
		/// <paramref name="action"/>. Failures degrade to a one-line note instead of breaking the
		/// whole view, like the C#/VB languages.
		/// </summary>
		void DisassembleMemberCore<TCecil>(ModuleDef? module, Func<ModuleDefinition, TCecil?> resolve,
				IDecompilerOutput output, DecompilationContext ctx,
				Action<ReflectionDisassembler, TCecil> action,
				Action<DecompilerOutputTextOutput>? afterBody = null) where TCecil : class, IMemberDefinition {
			if (module is null) {
				WriteNote(output, NoModuleNote);
				return;
			}
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			var cecilMember = resolve(cecilModule);
			if (cecilMember is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			var dis = CreateDisassembler(output, ctx, module, cecilModule, out var bridge);
			try {
				action(dis, cecilMember);
				afterBody?.Invoke(bridge);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}

		static T? ResolveByToken<T>(ModuleDefinition cecilModule, uint rawToken) where T : class, IMemberDefinition =>
			DnlibCecilBridge.TryGetMember(cecilModule, rawToken) as T;

		// Cecil's ModuleDefinition.LookupToken does not resolve Property (0x17) or Event (0x14)
		// definition rows (it returns null for them), so those members are found through their
		// declaring type's property/event lists by rid. dnlib tokens are preserved by the bridge.
		static PropertyDefinition? ResolveProperty(ModuleDefinition cecilModule, PropertyDef property) {
			var declaringType = property.DeclaringType is null ? null :
				DnlibCecilBridge.TryGetMember(cecilModule, property.DeclaringType.MDToken.Raw) as TypeDefinition;
			if (declaringType is null)
				return null;
			var rid = property.MDToken.Rid;
			foreach (var p in declaringType.Properties) {
				if (p.MetadataToken.RID == rid)
					return p;
			}
			return null;
		}

		static EventDefinition? ResolveEvent(ModuleDefinition cecilModule, EventDef ev) {
			var declaringType = ev.DeclaringType is null ? null :
				DnlibCecilBridge.TryGetMember(cecilModule, ev.DeclaringType.MDToken.Raw) as TypeDefinition;
			if (declaringType is null)
				return null;
			var rid = ev.MDToken.Rid;
			foreach (var e in declaringType.Events) {
				if (e.MetadataToken.RID == rid)
					return e;
			}
			return null;
		}

		/// <summary>Assembly/module level core: no member token, the whole cecil module is the subject.</summary>
		void DisassembleModuleCore(ModuleDef? module, IDecompilerOutput output, DecompilationContext ctx,
				Action<ReflectionDisassembler, ModuleDefinition> action) {
			if (module is null) {
				WriteNote(output, NoModuleNote);
				return;
			}
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null) {
				WriteNote(output, NoBridgeNote);
				return;
			}
			var dis = CreateDisassembler(output, ctx, module, cecilModule);
			try {
				action(dis, cecilModule);
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 disassembly failed: " + CSharpDecompiler32.DescribeException(ex));
			}
		}
	}

	/// <summary>
	/// IL-view settings. The 3.2 engine has no engine-side switch for any of the 2.3 IL options
	/// (IL comments, token/RVA comments, IL bytes, member sorting, PDB info, max string length,
	/// hexadecimal numbers), so the option list is empty and dnSpy's settings page shows nothing.
	/// </summary>
	sealed class ILDecompilerSettings32 : DecompilerSettingsBase {
		int version;

		public override DecompilerSettingsBase Clone() => new ILDecompilerSettings32 { version = version };
		public override int Version => version;
		// Contract member with nothing to raise it for: the settings are stateless (empty options).
#pragma warning disable CS0067
		public override event EventHandler? VersionChanged;
		public override IEnumerable<IDecompilerOption> Options {
			get { yield break; }
		}
#pragma warning restore CS0067
		public override bool Equals(object? obj) => obj is ILDecompilerSettings32;
		public override int GetHashCode() => 0x32;
	}

	/// <summary>
	/// Tracks which dnlib method a method body belongs to. The 3.2 disassembler hands
	/// <see cref="Mono.Cecil.Cil.Instruction"/> objects to the output with no back pointer to
	/// their method, but instruction labels and branch targets need a dnlib
	/// <see cref="InstructionReference"/> (method + instruction) to be clickable, so the body
	/// disassembly is wrapped to resolve the body's method and index its instructions by offset.
	/// </summary>
	class BodyTrackingMethodBodyDisassembler : MethodBodyDisassembler {
		readonly ModuleDef module;
		MethodDef? currentMethod;
		Dictionary<int, DnlibInstruction>? instructionMap;

		public BodyTrackingMethodBodyDisassembler(ITextOutput output, CancellationToken cancellationToken, ModuleDef module)
			: base(output, cancellationToken) {
			this.module = module ?? throw new ArgumentNullException(nameof(module));
		}

		/// <summary>The dnlib method whose body is currently being disassembled, if known.</summary>
		public MethodDef? CurrentMethod => currentMethod;

		/// <summary>Resolves a cecil instruction offset to the dnlib instruction of the current body.</summary>
		public DnlibInstruction? FindInstruction(int offset) =>
			instructionMap is not null && instructionMap.TryGetValue(offset, out var inst) ? inst : null;

		public override void Disassemble(MethodBody body) {
			var prevMethod = currentMethod;
			var prevMap = instructionMap;
			currentMethod = TryResolveMethod(body.Method);
			instructionMap = BuildInstructionMap(currentMethod);
			try {
				base.Disassemble(body);
			}
			finally {
				currentMethod = prevMethod;
				instructionMap = prevMap;
			}
		}

		/// <summary>
		/// Brackets each instruction's written span so the IL view's debug info can map every
		/// IL offset to the output positions it occupies (the 2.3 engine's MethodBodyDisassembler
		/// took a MethodDebugInfoBuilder and did the same).
		/// </summary>
		protected override void WriteInstruction(ITextOutput output, Instruction instruction) {
			if (output is DecompilerOutputTextOutput bridge)
				bridge.BeginInstruction(instruction.Offset);
			base.WriteInstruction(output, instruction);
			if (output is DecompilerOutputTextOutput bridge2)
				bridge2.EndInstruction();
		}

		MethodDef? TryResolveMethod(MethodDefinition method) {
			try {
				return module.ResolveToken((uint)method.MetadataToken.ToInt32()) as MethodDef;
			}
			catch {
				return null;
			}
		}

		static Dictionary<int, DnlibInstruction>? BuildInstructionMap(MethodDef? method) {
			var body = method?.Body;
			if (body is null)
				return null;
			var map = new Dictionary<int, DnlibInstruction>(body.Instructions.Count);
			foreach (var inst in body.Instructions)
				map[(int)inst.Offset] = inst;
			return map;
		}
	}

	/// <summary>
	/// Minimal bridge from the 3.2 engine's <see cref="ITextOutput"/> (plain strings,
	/// definition/reference markers and fold markers) onto dnSpy's
	/// <see cref="IDecompilerOutput"/> (colored writes with references, custom data). The engine
	/// carries no color, so each write is classified with the BoxedTextColor IL palette the 2.3
	/// IL view used: comments, IL directives, keywords, numbers, punctuation/operators, string
	/// literals, and metadata colors for names resolved back to dnlib. Cecil definition/reference
	/// objects are mapped to dnlib objects by metadata token so dnSpy navigation and tooltips
	/// work; instruction offsets become <see cref="InstructionReference"/>s. Fold markers are
	/// dropped (dnSpy folds via brace pairs, which this bridge reconstructs from the standalone
	/// delimiters the engine writes).
	/// </summary>
	sealed class DecompilerOutputTextOutput : ITextOutput {
		readonly IDecompilerOutput output;
		readonly ModuleDef module;
		readonly List<(char open, int start)> openDelimiters = new List<(char, int)>();
		bool atLineStart;
		bool lineIsComment;
		bool inStringLiteral;
		bool xmlDocLinePending;

		/// <summary>Set by the language class once the body tracker exists (circular init).</summary>
		internal BodyTrackingMethodBodyDisassembler? BodyTracker { get; set; }

		/// <summary>
		/// Whether matching delimiters at the start of a line are reported as brace pairs.
		/// The IL view relies on this (the 3.2 disassembler writes no brace metadata); the
		/// ILAst view turns it off because the engine's ILAst writer emits its own ranges.
		/// </summary>
		internal bool TrackDelimiters { get; set; } = true;

		/// <summary>
		/// Per-instruction output ranges collected during a body write, for the IL view's
		/// debug info (the 2.3 engine built one statement per instruction, so dnSpy's
		/// debugger highlights the executing instruction in the IL view).
		/// </summary>
		public List<(int IlOffset, int Start, int End)> InstructionRanges { get; } = new List<(int, int, int)>();
		int currentIlOffset = -1;
		int currentStart = -1;

		/// <summary>Marks the start of one instruction's written span.</summary>
		public void BeginInstruction(int ilOffset) {
			currentIlOffset = ilOffset;
			currentStart = output.NextPosition;
		}

		/// <summary>Marks the end of the current instruction's written span.</summary>
		public void EndInstruction() {
			if (currentIlOffset < 0 || currentStart < 0)
				return;
			var end = output.NextPosition;
			if (end >= currentStart)
				InstructionRanges.Add((currentIlOffset, currentStart, end));
			currentIlOffset = -1;
			currentStart = -1;
		}

		public DecompilerOutputTextOutput(IDecompilerOutput output, ModuleDef module) {
			this.output = output ?? throw new ArgumentNullException(nameof(output));
			this.module = module ?? throw new ArgumentNullException(nameof(module));
			atLineStart = true;
		}

		public void Indent() => output.IncreaseIndent();
		public void Unindent() => output.DecreaseIndent();

		public void Write(char ch) => WriteCore(ch.ToString(), null, DecompilerReferenceFlags.None);
		public void Write(string text) => WriteCore(text, null, DecompilerReferenceFlags.None);
		public void WriteDefinition(string text, object definition, bool isLocal = true) =>
			WriteCore(text, definition, DecompilerReferenceFlags.Definition);
		public void WriteReference(string text, object reference, bool isLocal = false) =>
			WriteCore(text, reference, DecompilerReferenceFlags.None);

		public void WriteLine() {
			output.WriteLine();
			atLineStart = true;
			lineIsComment = false;
			// Safety: an escaped newline inside a string operand would desync the literal tracking.
			inStringLiteral = false;
		}

		// dnSpy folds via brace pairs, not ITextOutput fold markers.
		public void MarkFoldStart(string collapsedText = "...", bool defaultCollapsed = false) { }
		public void MarkFoldEnd() { }

		/// <summary>Delegates to the dnSpy output so brace offsets share its position accounting.</summary>
		public int NextPosition => output.NextPosition;

		/// <summary>
		/// Forwards a brace pair written by the engine's ILAst writer. The engine enum mirrors
		/// dnSpy's numerically, so the flags can be cast directly.
		/// </summary>
		public void AddCodeBracesRange(int start, int end, ICSharpCode.Decompiler.CodeBracesRangeFlags flags) =>
			output.AddBracePair(new TextSpan(start, 1), new TextSpan(end, 1), (dnSpy.Contracts.Decompiler.CodeBracesRangeFlags)(int)flags);

		void WriteCore(string text, object? cecilReference, DecompilerReferenceFlags baseFlags) {
			if (text.Length == 0)
				return;

			// XML documentation body: the engine writes "///" followed by the doc text. dnSpy
			// has dedicated colors for the delimiter and the parsed XML, so route the text through
			// IDecompilerOutput.WriteXmlDoc, exactly like the 2.3 view did.
			if (xmlDocLinePending) {
				xmlDocLinePending = false;
				output.WriteXmlDoc(text);
				return;
			}
			if (atLineStart && text == "///") {
				xmlDocLinePending = true;
				WriteWithReference("///", null, DecompilerReferenceFlags.None, BoxedTextColor.XmlDocCommentDelimiter);
				return;
			}

			// ldstr operand content between the '"' writes keeps the string color.
			if (inStringLiteral) {
				if (text == "\"")
					inStringLiteral = false;
				WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.String);
				return;
			}

			// Rest of an already-started comment line (loop-head labels, "(head: ...)", ...).
			if (lineIsComment) {
				WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				return;
			}

			// Whitespace-only writes (alignment spaces between blob bytes, ...): nothing to
			// classify and the directive/delimiter checks would index into an empty trimmed string.
			if (text.Trim().Length == 0) {
				WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
				return;
			}

			// The engine writes string operands as three calls: '"', escaped content, '"'.
			if (text == "\"") {
				inStringLiteral = true;
				WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.String);
				return;
			}

			bool atStartOfLine = atLineStart;

			// C-style comment blocks: the Mixed view's /* 0x... bytes */ and /* (line,col) */ decorators.
			if (text.StartsWith("/*", StringComparison.Ordinal)) {
				WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				return;
			}

			// Comments can start mid-line: "} // end of method ...", ".hash ... // SHA1".
			int commentIndex = text.IndexOf("//", StringComparison.Ordinal);
			if (commentIndex >= 0) {
				if (commentIndex > 0)
					WriteCore(text.Substring(0, commentIndex), null, DecompilerReferenceFlags.None);
				lineIsComment = true;
				WriteWithReference(text.Substring(commentIndex), null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				return;
			}

			// IL directives at the start of a line: .method, .field, .class, .property, .event,
			// .namespace, .assembly, .module, .custom, .param, .permissionset, .publickey,
			// .publickeytoken, .ver, .hash, .corflags, .try, .maxstack, .locals, .entrypoint,
			// .override, .get/.set/.other/.addon/.removeon/.fire, ...
			// The reference (if any) is attached so the directive token itself is clickable.
			if (atStartOfLine && text.TrimStart()[0] == '.') {
				WriteMappedOrDirective(text, cecilReference, baseFlags);
				return;
			}

			// Standalone delimiters: reconstruct the 2.3 view's brace pairs. Only single-character
			// tokens count (local definitions "[0] " and security decls " = {" must not match),
			// openers only when they start the line (method/property parameter parens and blobs
			// are mid-line), and closers only close a matching opener.
			string trimmedText = text.Trim();
			if (trimmedText.Length == 1 &&
				(trimmedText[0] == '(' || trimmedText[0] == '{' || trimmedText[0] == '[' ||
				 trimmedText[0] == ')' || trimmedText[0] == '}' || trimmedText[0] == ']')) {
				WriteDelimiter(text, trimmedText[0], atStartOfLine);
				return;
			}

			// Definitions/references the engine marked: map them to dnlib and use the metadata
			// colors. Falls through to plain classification when the object type is unknown.
			if (cecilReference is not null && TryWriteMappedReference(text, cecilReference, baseFlags))
				return;

			WriteWithReference(text, null, DecompilerReferenceFlags.None, ClassifyText(text.Trim()));
		}

		/// <summary>Directive-colored write that still carries the mapped dnlib reference.</summary>
		void WriteMappedOrDirective(string text, object? cecilReference, DecompilerReferenceFlags baseFlags) {
			object? reference = null;
			if (cecilReference is not null) {
				var dnlibObj = TryResolveDnlib(cecilReference);
				reference = dnlibObj;
			}
			WriteWithReference(text, reference, baseFlags, BoxedTextColor.ILDirective);
		}

		void WriteDelimiter(string text, char c, bool atStartOfLine) {
			int start = output.NextPosition;
			if ((c == '(' || c == '{' || c == '[') && atStartOfLine) {
				openDelimiters.Add((c, start));
				WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Punctuation);
				return;
			}
			if ((c == ')' || c == '}' || c == ']') && atStartOfLine && openDelimiters.Count > 0) {
				var (open, openStart) = openDelimiters[openDelimiters.Count - 1];
				if ((open == '(' && c == ')') || (open == '{' && c == '}') || (open == '[' && c == ']')) {
					openDelimiters.RemoveAt(openDelimiters.Count - 1);
					WriteWithReference(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Punctuation);
					if (output.UsesCustomData && TrackDelimiters)
						output.AddBracePair(new TextSpan(openStart, 1), new TextSpan(start, 1),
						open == '(' ? dnSpy.Contracts.Decompiler.CodeBracesRangeFlags.Parentheses :
						open == '[' ? dnSpy.Contracts.Decompiler.CodeBracesRangeFlags.SquareBrackets :
						dnSpy.Contracts.Decompiler.CodeBracesRangeFlags.OtherBlockBraces);
					return;
				}
			}
			WriteWithReference(text, null, DecompilerReferenceFlags.None, ClassifyDelimiter(c));
		}

		static object ClassifyDelimiter(char c) =>
			(c == '-' || c == '=' || c == '*' || c == '&' || c == '+') ? BoxedTextColor.Operator : BoxedTextColor.Punctuation;

		bool TryWriteMappedReference(string text, object cecilReference, DecompilerReferenceFlags baseFlags) {
			object color;
			object? reference = null;
			var flags = baseFlags;
			var tracker = BodyTracker;
			switch (cecilReference) {
			case Mono.Cecil.Cil.Instruction inst:
				// Labels and branch targets: 2.3 used Label color and an InstructionReference.
				color = BoxedTextColor.Label;
				var dnlibInst = tracker?.FindInstruction(inst.Offset);
				var dnlibMethod = tracker?.CurrentMethod;
				if (dnlibInst is not null && dnlibMethod is not null)
					reference = new InstructionReference(dnlibMethod, dnlibInst);
				break;

			case Mono.Cecil.Cil.OpCode:
				color = BoxedTextColor.OpCode;
				break;

			case Mono.Cecil.Cil.VariableReference varRef:
				// Local index: the 2.3 view colored the definition "[i]" as a number and operand
				// references with the local color; both carry a SourceLocal reference so the
				// debugger's Locals window and tooltips work.
				color = (baseFlags & DecompilerReferenceFlags.Definition) != 0 ? BoxedTextColor.Number : BoxedTextColor.Local;
				flags |= DecompilerReferenceFlags.Local;
				var dnlibBody = tracker?.CurrentMethod?.Body;
				if (dnlibBody is not null && varRef.Index >= 0 && varRef.Index < dnlibBody.Variables.Count) {
					var dnlibLocal = dnlibBody.Variables[varRef.Index];
					reference = new SourceLocal(dnlibLocal, dnlibLocal.Name ?? string.Empty, dnlibLocal.Type, SourceVariableFlags.None);
				}
				break;

			case ParameterReference paramRef:
				color = BoxedTextColor.Parameter;
				flags |= DecompilerReferenceFlags.Local;
				reference = TryResolveDnlib(paramRef);
				break;

			case MemberReference:
				// Everything with a metadata token: types, methods, fields, properties, events,
				// generic parameters - definitions and references alike.
				var dnlibObj = TryResolveDnlib(cecilReference);
				color = dnlibObj is null ? BoxedTextColor.Text : CSharpMetadataTextColorProvider.Instance.GetColor(dnlibObj);
				reference = dnlibObj;
				break;

			default:
				return false;
			}
			WriteWithReference(text, reference, flags, color);
			return true;
		}

		object? TryResolveDnlib(object cecilObject) {
			try {
				// MemberReference (types/methods/fields/properties/events/generic params) and
				// ParameterReference both expose a metadata token; the cecil snapshot preserves
				// rids, so it resolves back to the dnlib object dnSpy knows how to navigate.
				if (cecilObject is IMetadataTokenProvider mtp)
					return module.ResolveToken((uint)mtp.MetadataToken.ToInt32());
			}
			catch {
				// unresolvable references degrade to an unlinked write
			}
			return null;
		}

		void WriteWithReference(string text, object? reference, DecompilerReferenceFlags flags, object color) {
			if (reference is null) {
				flags = DecompilerReferenceFlags.None;
				output.Write(text, color);
			}
			else {
				output.Write(text, reference, flags, color);
			}
			atLineStart = false;
		}

		/// <summary>
		/// Color for bare writes that carry no reference. Matches the 2.3 IL view's palette:
		/// keywords, numbers, punctuation/operators, everything else plain text.
		/// </summary>
		static object ClassifyText(string trimmed) {
			if (trimmed.Length == 0)
				return BoxedTextColor.Text;
			if (IsNumberToken(trimmed))
				return BoxedTextColor.Number;
			if (IsIlKeyword(trimmed))
				return BoxedTextColor.Keyword;
			if (IsPunctuationToken(trimmed))
				return ClassifyDelimiter(trimmed[0]);
			return BoxedTextColor.Text;
		}

		static bool IsNumberToken(string s) {
			int i = 0;
			if (s[0] == '-' || s[0] == '+')
				i = 1;
			if (i >= s.Length)
				return false;
			if (s[i] == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X')) {
				i += 2;
				if (i >= s.Length)
					return false;
				for (; i < s.Length; i++) {
					if (!Uri.IsHexDigit(s[i]))
						return false;
				}
				return true;
			}
			bool seenDot = false;
			for (; i < s.Length; i++) {
				char c = s[i];
				if (c == '.') {
					if (seenDot)
						return false;
					seenDot = true;
					continue;
				}
				if (c < '0' || c > '9')
					return false;
			}
			return true;
		}

		static bool IsPunctuationToken(string s) {
			switch (s) {
			case ":":
			case "::":
			case ",":
			case ";":
			case "/":
			case "(":
			case ")":
			case "[":
			case "]":
			case "{":
			case "}":
				return true;
			case "-":
			case "=":
			case "*":
			case "&":
			case "+":
				return true;
			default:
				return false;
			}
		}

		static bool IsIlKeyword(string s) {
			// Multi-token phrases ("instance explicit", "nested famorassem", "unsigned int8",
			// "unmanaged cdecl") count when every token is a keyword. Unescaped identifiers that
			// match a keyword cannot occur here: the engine quotes identifiers that collide with
			// ILAsm keywords before writing them.
			foreach (var part in s.Split(' ')) {
				if (part.Length == 0 || !IlKeywords.Contains(part))
					return false;
			}
			return true;
		}

		/// <summary>
		/// The ILAsm keywords the disassembler emits as bare writes: attribute/visibility flag
		/// names, primitive type names and structural words (a subset of the engine's own
		/// keyword list, which is private).
		/// </summary>
		static readonly HashSet<string> IlKeywords = new HashSet<string>(StringComparer.Ordinal) {
			// method/field/type visibility + flags (WriteEnum/WriteFlags values)
			"public", "private", "famandassem", "assembly", "family", "famorassem",
			"final", "hidebysig", "specialname", "export", "rtspecialname", "reqsecobj",
			"newslot", "strict", "abstract", "virtual", "static", "literal", "initonly",
			"notserialized", "sealed", "import", "serializable", "windowsruntime",
			"beforefieldinit", "hasdefault", "nested",
			// calling conventions / code types / impl flags
			"cdecl", "stdcall", "thiscall", "fastcall", "winapi", "vararg", "cil", "native",
			"optil", "runtime", "synchronized", "noinlining", "nooptimization", "preservesig",
			"internalcall", "forwardref", "managed", "unmanaged", "pinvokeimpl", "nomangle",
			"lasterr", "ansi", "autochar", "unicode",
			// structural words
			"instance", "explicit", "privatescope", "extends", "implements", "interface",
			"catch", "finally", "fault", "filter", "handler", "init", "at", "to", "class",
			"valuetype", "enum", "field", "method", "property", "type", "flags", "callconv",
			"in", "out", "opt", "default", "marshal", "custom", "true", "false", "null",
			"nullref", "auto", "sequential", "forwarder",
			// primitive type names
			"void", "bool", "char", "string", "object", "int8", "int16", "int32", "int64",
			"unsigned", "float32", "float64", "int", "uint", "lpstr", "lpwstr", "lptstr",
			"lpstruct", "lpvoid", "bstr", "tbstr", "ansibstr", "variantbool", "currency",
			"date", "hresult", "error", "iunknown", "idispatch", "struct", "safearray",
			"variant", "byvalstr", "typedref", "objectref", "bytearray", "syschar",
			"sysstring", "asany", "value", "record", "array", "carray", "fixed", "pinned",
			// security actions
			"request", "demand", "assert", "deny", "permitonly", "linkcheck", "inheritcheck",
			"reqmin", "reqopt", "reqrefuse", "prejitgrant", "prejitdeny", "noncasdemand",
			"noncaslinkdemand", "noncasinheritance",
		};
	}
}
