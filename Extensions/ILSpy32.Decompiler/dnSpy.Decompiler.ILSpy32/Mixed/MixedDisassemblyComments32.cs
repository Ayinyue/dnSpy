using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.IL;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.Mixed {
	/// <summary>
	/// The per-member and per-instruction decorations the 2.3 Mixed view showed: the body header
	/// comments, local variable names, the IL byte comment, the PDB source-location comment and
	/// the async state machine comments. All of them are computed from the dnlib module, which is
	/// where dnSpy loads symbols (the cecil snapshot used by the 3.2 engine is read without them).
	/// </summary>
	static class MixedDisassemblyComments32 {
		/// <summary>
		/// The 2.3 body header block (<c>// Header Size / Code Size / LocalVarSig Token</c>).
		/// </summary>
		public static IEnumerable<string> GetHeaderComments(MethodDef method) {
			var body = method.Body;
			if (body is null)
				yield break;
			yield return $"// Header Size: {body.HeaderSize} {(body.HeaderSize == 1 ? "byte" : "bytes")}";
			var codeSize = body.GetCodeSize();
			yield return $"// Code Size: {codeSize} (0x{codeSize:X}) {(codeSize == 1 ? "byte" : "bytes")}";
			if (body.LocalVarSigTok != 0)
				yield return $"// LocalVarSig Token: 0x{body.LocalVarSigTok:X8} RID: {body.LocalVarSigTok & 0xFFFFFF}";
		}

		/// <summary>Local names indexed by slot; empty names are returned as null.</summary>
		public static string[] GetLocalNames(MethodDef method) {
			var body = method.Body;
			if (body is null || body.Variables.Count == 0)
				return null;
			var names = new string[body.Variables.Count];
			for (int i = 0; i < body.Variables.Count; i++) {
				var name = body.Variables[i].Name;
				names[i] = string.IsNullOrEmpty(name) ? null : name;
			}
			return names;
		}

		/// <summary>
		/// The file offset of the first IL byte, used as the base for the byte comment. Null when
		/// the module has no file image (in-memory modules).
		/// </summary>
		public static long? GetBaseFileOffset(MethodDef method) {
			var body = method.Body;
			if (body is null || method.RVA == 0)
				return null;
			return method.Module.ToFileOffset((uint)(method.RVA + body.HeaderSize)) ?? (uint)(method.RVA + body.HeaderSize);
		}

		/// <summary>
		/// <c>/* (line,col)-(line,col) url */</c>, or null when the instruction has no sequence
		/// point (the 2.3 <c>ShowPdbInfo</c> decoration).
		/// </summary>
		public static string GetSequencePointComment(Instruction instruction) {
			var seqPoint = instruction.SequencePoint;
			if (seqPoint is null)
				return null;
			const int HIDDEN = 0xFEEFEE;
			var start = seqPoint.StartLine == HIDDEN ? "hidden" : $"{seqPoint.StartLine},{seqPoint.StartColumn}";
			var end = seqPoint.EndLine == HIDDEN ? "hidden" : $"{seqPoint.EndLine},{seqPoint.EndColumn}";
			return $"/* ({start})-({end}) {seqPoint.Document.Url} */";
		}

		/// <summary>The async state machine decorations (<c>Catch Handler</c>, <c>Yield Instruction</c>, <c>Resume Instruction</c>).</summary>
		public static IEnumerable<string> GetAsyncComments(MethodDef method, Instruction instruction) {
			var asyncInfo = method.CustomDebugInfos?.OfType<PdbAsyncMethodCustomDebugInfo>().FirstOrDefault();
			if (asyncInfo is null)
				yield break;
			if (asyncInfo.CatchHandlerInstruction == instruction)
				yield return "/* Catch Handler */";
			foreach (var info in asyncInfo.StepInfos) {
				if (info.YieldInstruction == instruction)
					yield return "/* Yield Instruction */";
				if (info.BreakpointInstruction == instruction)
					yield return "/* Resume Instruction */";
			}
		}

		/// <summary>Creates the byte reader used for the byte comment (original bytes when available).</summary>
		public static IInstructionBytesReader CreateByteReader(MethodDef method, bool isBodyModified) =>
			InstructionBytesReader.Create(method, isBodyModified);

		/// <summary>
		/// <c>/* 0x&lt;fileOffset&gt; &lt;hex bytes&gt; */ </c>, or null when the bytes cannot be read.
		/// </summary>
		public static string GetByteComment(MethodDef method, IInstructionBytesReader byteReader, Instruction instruction, long? baseFileOffset) {
			if (byteReader is null)
				return null;
			var sb = new System.Text.StringBuilder();
			sb.Append("/* ");
			var fileOffset = (baseFileOffset ?? 0) + instruction.Offset;
			sb.Append($"0x{fileOffset:X8}");
			sb.Append(' ');
			int size = instruction.GetSize();
			for (int i = 0; i < size; i++) {
				int b = byteReader.ReadByte();
				if (b < 0) {
					sb.Append("??");
					continue;
				}
				sb.Append($"{b:X2}");
			}
			// Most instructions are at most 5 bytes, but pad to 6 to keep the operand column
			// aligned (the same MIN_BYTES the 2.3 engine used).
			for (int i = size; i < 6; i++)
				sb.Append("  ");
			sb.Append(" */ ");
			return sb.ToString();
		}
	}
}
