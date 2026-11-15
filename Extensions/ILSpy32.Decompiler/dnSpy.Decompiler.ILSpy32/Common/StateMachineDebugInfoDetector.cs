// Copyright (C) 2026 dnSpyEx
// Licensed under the same license as the dnSpyEx project.

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// Detects compiler-generated state machines and extracts the async-step mapping from the
	/// MoveNext body with plain dnlib scanning - the 2.3 engine filled the same info from its
	/// ILAst layer (AsyncDecompiler.RunStep2), and dnSpy's async stepper consumes it
	/// (DbgEngineStepperImpl.GetAsyncStepInfos: await step-over, kickoff jump into MoveNext).
	/// The detected values feed the MethodDebugInfoBuilder constructor (they are read-only
	/// there), so this returns them before the builder is created.
	/// </summary>
	static class StateMachineDebugInfoDetector {
		/// <summary>
		/// The builder constructor arguments for a state-machine member: bodyMethod is the
		/// MoveNext (whose IL offsets the AsyncStepInfos and the sequence points address) and
		/// kickoffMethod the visible async/iterator method.
		/// </summary>
		public sealed class Result {
			public StateMachineKind Kind;
			public MethodDef BodyMethod = null!;
			public MethodDef? KickoffMethod;
			public AsyncMethodDebugInfo? AsyncInfo;
		}

		/// <summary>
		/// Detects the state machine behind <paramref name="method"/> (an async/iterator kickoff
		/// or a MoveNext body) and returns the builder arguments, or null when the method is not
		/// a state machine member.
		/// </summary>
		public static Result? TryDetect(MethodDef method) {
			if (method?.Body is null)
				return null;

			TypeDef stateMachineType;
			MethodDef kickoff;
			MethodDef? moveNext;
			if (IsStateMachineType(method.DeclaringType)) {
				stateMachineType = method.DeclaringType;
				moveNext = stateMachineType.FindMethod("MoveNext");
				if (moveNext is null || moveNext.Body is null)
					return null;
				kickoff = FindKickoff(stateMachineType) ?? method;
			}
			else {
				kickoff = method;
				stateMachineType = FindNestedStateMachine(method.DeclaringType, method.Name);
				if (stateMachineType is null)
					return null;
				moveNext = stateMachineType.FindMethod("MoveNext");
				if (moveNext is null || moveNext.Body is null)
					return null;
			}

			var kind = IsAsyncStateMachine(stateMachineType) ? StateMachineKind.AsyncMethod : StateMachineKind.IteratorMethod;
			var result = new Result { Kind = kind, BodyMethod = moveNext, KickoffMethod = kickoff != moveNext ? kickoff : null };
			result.AsyncInfo = BuildAsyncInfo(stateMachineType, moveNext, kickoff, kind);
			return result;
		}

		static AsyncMethodDebugInfo? BuildAsyncInfo(TypeDef stateMachineType, MethodDef moveNext, MethodDef kickoff, StateMachineKind kind) {
			if (moveNext.Body is null)
				return null;
			var builderField = stateMachineType.Fields.FirstOrDefault(f => f.Name?.String?.Contains("t__builder") == true);
			var awaitPoints = new List<(uint YieldOffset, uint ResumeOffset)>();
			uint setResultOffset = uint.MaxValue;
			uint catchHandlerOffset = uint.MaxValue;

			var body = moveNext.Body;
			var stateField = stateMachineType.Fields.FirstOrDefault(f => f.Name == "<>1__state");
			// State dispatch map: modern Roslyn compiles the state switch as a chain of
			// conditional branches over the state value (ldloc/ldfld state; brfalse → the
			// state-0 resume point; ldc.i4.N; beq → the state-N resume point) instead of a
			// Switch instruction. Collect (state → resume offset) pairs from both shapes.
			var resumeByState = new Dictionary<int, uint>();
			var instructions = body.Instructions;
			for (var i = 0; i < instructions.Count; i++) {
				var ins = instructions[i];
				var target = (ins.Operand as Instruction)?.Offset;
				if (target is null)
					continue;
				// 'ldc.i4.N; beq' over a state load: if state == N, jump to the resume point.
				if (i >= 2 && ins.OpCode.Code is Code.Beq or Code.Beq_S &&
						instructions[i - 1].OpCode.Code == Code.Ldc_I4 &&
						instructions[i - 2].OpCode.Code == Code.Ldfld &&
						(instructions[i - 2].Operand as FieldDef)?.Name == "<>1__state") {
					var state = (int)instructions[i - 1].Operand!;
					resumeByState[state] = target.Value;
				}
				// 'brfalse' over a state load: if state == 0.
				if (i >= 1 && ins.OpCode.Code is Code.Brfalse or Code.Brfalse_S &&
						(instructions[i - 1].OpCode.Code == Code.Ldloc || instructions[i - 1].OpCode.Code == Code.Ldloc_S ||
						 instructions[i - 1].OpCode.Code == Code.Ldloc_0 || instructions[i - 1].OpCode.Code == Code.Ldloc_1 ||
						 instructions[i - 1].OpCode.Code == Code.Ldloc_2 || instructions[i - 1].OpCode.Code == Code.Ldloc_3 ||
						 instructions[i - 1].OpCode.Code == Code.Ldloc) &&
						StateLoadSourceIsField(instructions, i - 1, stateField))
					resumeByState[0] = target.Value;
			}
			var switchTargets = GetSwitchTargets(body);
			for (var i = 0; i < switchTargets.Count; i++)
				resumeByState[i] = switchTargets[i];

			foreach (var ins in instructions) {
				if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) {
					var called = ins.Operand as IMethod;
					var name = called?.Name?.String;
					if (name == "AwaitUnsafeOnCompleted" || name == "AwaitOnCompleted") {
						// The state store right before the await call carries the await's state
						// id; the dispatch map gives that state's resume point.
						var state = stateField is null ? null : TryGetConstantBefore(body, ins, stateField);
						if (state is not null && resumeByState.TryGetValue(unchecked((int)state.Value), out var resumeOffset))
							awaitPoints.Add(((uint)ins.Offset, resumeOffset));
					}
					else if (name == "SetResult" && setResultOffset == uint.MaxValue)
						setResultOffset = (uint)ins.Offset;
					else if (name == "SetException" && kickoff.MethodSig.RetType.RemovePinnedAndModifiers().GetElementType() == ElementType.Void)
						catchHandlerOffset = (uint)ins.Offset;
				}
			}
			// Non-void async methods don't jump through the catch handler.
			if (kickoff.MethodSig.RetType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
				catchHandlerOffset = uint.MaxValue;
			if (kind != StateMachineKind.AsyncMethod || awaitPoints.Count == 0)
				return null;
			return new AsyncMethodDebugInfo(
				awaitPoints.Select(p => new AsyncStepInfo(p.YieldOffset, moveNext, p.ResumeOffset)).ToArray(),
				builderField, catchHandlerOffset, setResultOffset);
		}

		static bool IsStateMachineType(TypeDef type) =>
			type is not null && type.HasCustomAttributes &&
			type.CustomAttributes.Any(a => a.AttributeType?.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute") &&
			(type.Name?.String?.StartsWith("<", StringComparison.Ordinal) == true);

		static bool IsAsyncStateMachine(TypeDef type) =>
			type.Interfaces.Any(i => i.Interface?.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine");

		static TypeDef? FindNestedStateMachine(TypeDef declaringType, string methodName) {
			if (declaringType is null)
				return null;
			var prefix = "<" + methodName + ">";
			return declaringType.NestedTypes.FirstOrDefault(t =>
				t.Name?.String?.StartsWith(prefix, StringComparison.Ordinal) == true &&
				IsStateMachineType(t));
		}

		static MethodDef? FindKickoff(TypeDef stateMachineType) {
			var name = stateMachineType.Name?.String ?? string.Empty;
			// "<M>d__N" / "<>u__N" style - the kickoff method name sits between '<' and '>'.
			if (name.Length < 3 || name[0] != '<')
				return null;
			var end = name.IndexOf('>', 1);
			if (end <= 1)
				return null;
			var kickoffName = name.Substring(1, end - 1);
			return stateMachineType.DeclaringType?.FindMethod(kickoffName);
		}

		/// <summary>True when the local loaded at instructions[index] was filled from &lt;&gt;1__state.</summary>
		/// <summary>True when the operand stores/loads the state field (name match, since the
		/// module's resolution may hand out a different FieldDef instance than the Fields list).</summary>
		static bool IsStateField(object? operand, FieldDef? stateField) =>
			operand is IField field && field.Name == stateField?.Name;

		static bool StateLoadSourceIsField(IList<Instruction> instructions, int index, FieldDef? stateField) {
			if (stateField is null)
				return false;
			// The load reads a local whose last write came from the <>1__state field (the
			// method prologue does 'ldfld <>1__state; stloc V'). Walk back past the stloc and
			// look for the field load feeding it; a brfalse anywhere marks a different branch.
			for (var i = index - 1; i >= 0; i--) {
				var prev = instructions[i];
				if (prev.OpCode.Code == Code.Ldfld && IsStateField(prev.Operand, stateField))
					return true;
				if (prev.OpCode.Code == Code.Br || prev.OpCode.Code == Code.Br_S ||
					prev.OpCode.Code == Code.Brfalse || prev.OpCode.Code == Code.Brfalse_S ||
					prev.OpCode.Code == Code.Beq || prev.OpCode.Code == Code.Beq_S ||
					prev.OpCode.Code == Code.Call || prev.OpCode.Code == Code.Callvirt ||
					prev.OpCode.Code == Code.Newobj)
					return false;
			}
			return false;
		}

		static List<uint> GetSwitchTargets(CilBody body) {
			var targets = new List<uint>();
			foreach (var ins in body.Instructions) {
				if (ins.OpCode.Code == Code.Switch && ins.Operand is Instruction[] list)
					targets.AddRange(list.Select(t => (uint)t.Offset));
			}
			return targets;
		}

		/// <summary>
		/// The state id stored before an await call: locate the 'stfld &lt;&gt;1__state' ahead of
		/// the call (the awaiter setup runs between them), then the constant feeding that store.
		/// </summary>
		static uint? TryGetConstantBefore(CilBody body, Instruction awaitCall, FieldDef stateField) {
			var instructions = body.Instructions;
			var index = instructions.IndexOf(awaitCall);
			var stfldIndex = -1;
			for (var i = index - 1; i >= 0; i--) {
				var prev = instructions[i];
				if (prev.OpCode.Code == Code.Stfld && IsStateField(prev.Operand, stateField)) {
					stfldIndex = i;
					break;
				}
				// A previous await's call marks the boundary of this await's setup block.
				if (prev.OpCode.Code == Code.Call || prev.OpCode.Code == Code.Callvirt)
					return null;
			}
			if (stfldIndex < 0)
				return null;
			for (var i = stfldIndex - 1; i >= 0; i--) {
				var prev = instructions[i];
				switch (prev.OpCode.Code) {
				case Code.Ldc_I4: return (uint)(int)prev.Operand!;
				case Code.Ldc_I4_S: return (uint)(sbyte)prev.Operand!;
				case Code.Ldc_I4_M1: return unchecked((uint)-1);
				case Code.Ldc_I4_0: return 0;
				case Code.Ldc_I4_1: return 1;
				case Code.Ldc_I4_2: return 2;
				case Code.Ldc_I4_3: return 3;
				case Code.Ldc_I4_4: return 4;
				case Code.Ldc_I4_5: return 5;
				case Code.Ldc_I4_6: return 6;
				case Code.Ldc_I4_7: return 7;
				case Code.Ldc_I4_8: return 8;
				}
				// The state slot push and the builder/awaiter address loads sit between the
				// constant and the store; Roslyn duplicates the state constant ('dup; stloc V')
				// so the dup and its local store pass through too. Anything else breaks it.
				if (prev.OpCode.Code is not (Code.Ldloca or Code.Ldloca_S or Code.Ldarg or Code.Ldarg_S or
						Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3 or
						Code.Ldflda or Code.Ldfld or Code.Nop or Code.Dup or
						Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or
						Code.Stloc_2 or Code.Stloc_3))
					return null;
			}
			return null;
		}
	}
}
