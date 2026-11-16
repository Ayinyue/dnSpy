using System;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy32.Mixed {
	/// <summary>
	/// Capturing <see cref="IDecompilerOutput"/> used to decompile a single method to plain text
	/// plus its <see cref="MethodDebugInfo"/>, which the Mixed view overlays onto the IL
	/// disassembly. Ported from the 2.3 Mixed view; the debug info arrives through
	/// <see cref="AddCustomData{TData}"/> because the 3.2 C# view builds it the same way.
	/// </summary>
	sealed class DecompilerOutputImpl32 : IDecompilerOutput {
		readonly StringBuilder sb;
		int indentLevel;
		bool addIndent;
		MethodDef? method;
		MethodDebugInfo? methodDebugInfo;
		MethodDebugInfo? kickoffMethodDebugInfo;

		const string TAB_SPACES = "    ";

		public int Length => sb.Length;
		public int NextPosition => sb.Length + (addIndent ? indentLevel * TAB_SPACES.Length : 0);
		public bool UsesCustomData => true;

		public DecompilerOutputImpl32(StringBuilder sb) => this.sb = sb ?? throw new ArgumentNullException(nameof(sb));

		public void Initialize(MethodDef method) {
			sb.Clear();
			indentLevel = 0;
			addIndent = true;
			methodDebugInfo = null;
			kickoffMethodDebugInfo = null;
			this.method = method;
		}

		public (MethodDebugInfo debugInfo, MethodDebugInfo? stateMachineDebugInfo) TryGetMethodDebugInfo() {
			if (methodDebugInfo is not null) {
				if (kickoffMethodDebugInfo is not null)
					return (kickoffMethodDebugInfo, methodDebugInfo);
				return (methodDebugInfo, null);
			}
			return default;
		}

		public void AddCustomData<TData>(string id, TData data) {
			if (id == PredefinedCustomDataIds.DebugInfo && data is MethodDebugInfo debugInfo) {
				if (debugInfo.Method == method)
					methodDebugInfo = debugInfo;
				else if (debugInfo.KickoffMethod is { } m && m == method) {
					var body = m.Body;
					int bodySize = body?.GetCodeSize() ?? 0;
					var scope = new MethodDebugScope(new ILSpan(0, (uint)bodySize), Array.Empty<MethodDebugScope>(), Array.Empty<SourceLocal>(), Array.Empty<ImportInfo>(), Array.Empty<MethodDebugConstant>());
					kickoffMethodDebugInfo = new MethodDebugInfo(debugInfo.CompilerName, debugInfo.DecompilerSettingsVersion, StateMachineKind.None, m, null, null, Array.Empty<SourceStatement>(), scope, null, null);
					methodDebugInfo = debugInfo;
				}
			}
		}

		public void DecreaseIndent() => indentLevel--;
		public void IncreaseIndent() => indentLevel++;

		public void WriteLine() {
			addIndent = true;
			sb.Append(Environment.NewLine);
		}

		void AddIndent() {
			if (!addIndent)
				return;
			addIndent = false;
			for (int i = 0; i < indentLevel; i++)
				sb.Append(TAB_SPACES);
		}

		void AddText(string text) {
			if (addIndent)
				AddIndent();
			sb.Append(text);
		}

		void AddText(string text, int index, int length) {
			if (addIndent)
				AddIndent();
			sb.Append(text, index, length);
		}

		public void Write(string text, object color) => AddText(text);
		public void Write(string text, int index, int length, object color) => AddText(text, index, length);
		public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) => AddText(text);
		public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) => AddText(text, index, length);

		public override string ToString() => sb.ToString();
	}
}
