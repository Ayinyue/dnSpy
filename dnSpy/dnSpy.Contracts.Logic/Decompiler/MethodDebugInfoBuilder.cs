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
using dnlib.DotNet;

namespace dnSpy.Contracts.Decompiler {
	/// <summary>
	/// Builds <see cref="MethodDebugInfo"/> instances
	/// </summary>
	public sealed class MethodDebugInfoBuilder {
		readonly MethodDef method;
		readonly MethodDef? kickoffMethod;
		readonly StateMachineKind stateMachineKind;
		readonly List<SourceStatement> statements;

		/// <summary>
		/// Compiler name (<see cref="PredefinedCompilerNames"/>) or null
		/// </summary>
		public string? CompilerName { get; set; }

		/// <summary>
		/// Gets the scope builder
		/// </summary>
		public MethodDebugScopeBuilder Scope { get; }

		/// <summary>
		/// Gets/sets the parameters
		/// </summary>
		public SourceParameter[]? Parameters { get; set; }

		/// <summary>
		/// Async method debug info or null
		/// </summary>
		public AsyncMethodDebugInfo? AsyncInfo { get; set; }

		/// <summary>
		/// Start of method (eg. position of the first character of the modifier or return type)
		/// </summary>
		public int? StartPosition { get; set; }

		/// <summary>
		/// End of method (eg. after the last brace)
		/// </summary>
		public int? EndPosition { get; set; }

		readonly int decompilerSettingsVersion;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="decompilerSettingsVersion">Decompiler settings version number. This version number should get incremented when the settings change.</param>
		/// <param name="stateMachineKind">State machine kind</param>
		/// <param name="method">Method</param>
		/// <param name="kickoffMethod">Kickoff method or null</param>
		public MethodDebugInfoBuilder(int decompilerSettingsVersion, StateMachineKind stateMachineKind, MethodDef method, MethodDef? kickoffMethod) {
			this.decompilerSettingsVersion = decompilerSettingsVersion;
			this.stateMachineKind = stateMachineKind;
			this.method = method ?? throw new ArgumentNullException(nameof(method));
			this.kickoffMethod = kickoffMethod;
			statements = new List<SourceStatement>();
			Scope = new MethodDebugScopeBuilder();
			Scope.Span = ILSpan.FromBounds(0, (uint)method.Body.GetCodeSize());
			if (method == kickoffMethod)
				throw new ArgumentException("Method and kickoffMethod must be different");
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="decompilerSettingsVersion">Decompiler settings version number. This version number should get incremented when the settings change.</param>
		/// <param name="stateMachineKind">State machine kind</param>
		/// <param name="method">Method</param>
		/// <param name="kickoffMethod">Kickoff method or null</param>
		/// <param name="locals">Locals</param>
		/// <param name="parameters">Parameters or null</param>
		/// <param name="asyncInfo">Async method info or null</param>
		public MethodDebugInfoBuilder(int decompilerSettingsVersion, StateMachineKind stateMachineKind, MethodDef method, MethodDef? kickoffMethod, SourceLocal[] locals, SourceParameter[]? parameters, AsyncMethodDebugInfo? asyncInfo)
			: this(decompilerSettingsVersion, stateMachineKind, method, kickoffMethod) {
			Scope.Locals.AddRange(locals);
			Parameters = parameters;
			AsyncInfo = asyncInfo;
		}

		/// <summary>
		/// Adds a <see cref="SourceStatement"/>
		/// </summary>
		/// <param name="statement">Statement</param>
		public void Add(SourceStatement statement) => statements.Add(statement);

		/// <summary>
		/// Extends the statements' IL spans so they cover the whole method body: sort by IL
		/// start, pull the first start back to 0, fill every gap by extending each end to the
		/// next start, and extend the last end to <paramref name="codeSize"/>. The 2.3 engine's
		/// sequence points covered the whole body; without this the debugger has no highlight
		/// for instructions that sit between statements (branches, spills, prolog code).
		/// Text spans are left untouched.
		/// </summary>
		/// <param name="codeSize">Size of the method body in bytes</param>
		public void CompactILSpans(uint codeSize) {
			if (statements.Count == 0)
				return;
			statements.Sort(SourceStatement.SpanStartComparer);
			for (var i = 0; i < statements.Count - 1; i++) {
				var cur = statements[i];
				var next = statements[i + 1];
				if (cur.ILSpan.End < next.ILSpan.Start)
					statements[i] = new SourceStatement(ILSpan.FromBounds(cur.ILSpan.Start, next.ILSpan.Start), cur.TextSpan);
			}
			var last = statements[statements.Count - 1];
			if (last.ILSpan.End < codeSize)
				statements[statements.Count - 1] = new SourceStatement(ILSpan.FromBounds(last.ILSpan.Start, codeSize), last.TextSpan);
			var first = statements[0];
			if (first.ILSpan.Start > 0)
				statements[0] = new SourceStatement(new ILSpan(0, first.ILSpan.End), first.TextSpan);
		}

		/// <summary>
		/// Creates a <see cref="MethodDebugInfo"/>
		/// </summary>
		/// <returns></returns>
		public MethodDebugInfo Create() {
			TextSpan? methodSpan;
			if (StartPosition is not null && EndPosition is not null && StartPosition.Value <= EndPosition.Value)
				methodSpan = TextSpan.FromBounds(StartPosition.Value, EndPosition.Value);
			else
				methodSpan = null;
			return new MethodDebugInfo(CompilerName, decompilerSettingsVersion, stateMachineKind, method, kickoffMethod, Parameters, statements.ToArray(), Scope.ToScope(), methodSpan, AsyncInfo);
		}
	}
}
