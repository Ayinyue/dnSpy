using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy32.Mixed {
	/// <summary>
	/// Maps an IL offset to the source line the 3.2 C# decompiler produced for it. Ported from
	/// the 2.3 Mixed view; the statement text is written above the instruction it belongs to.
	/// </summary>
	struct SourceStatementProvider32 {
		readonly string text;
		readonly MethodDebugInfo debugInfo;
		SourceStatement? lastStatement;

		public SourceStatementProvider32(string text, MethodDebugInfo debugInfo) {
			this.text = text ?? throw new System.ArgumentNullException(nameof(text));
			this.debugInfo = debugInfo ?? throw new System.ArgumentNullException(nameof(debugInfo));
			lastStatement = null;
		}

		public (string? line, TextSpan span) GetStatement(uint ilOffset) {
			if (text is null || debugInfo is null)
				return default;

			var lastStmt = lastStatement;
			var stmt = debugInfo.GetSourceStatementByCodeOffset(ilOffset);
			lastStatement = stmt;
			if (stmt is null)
				return default;
			if (lastStmt == stmt)
				return default;

			int startPos = stmt.Value.TextSpan.Start;
			while (startPos > 0 && text[startPos - 1] != '\n')
				startPos--;
			var endPos = stmt.Value.TextSpan.End;
			while (endPos < text.Length) {
				var c = text[endPos];
				if (c == '\r' || c == '\n')
					break;
				endPos++;
			}

			return (text.Substring(startPos, endPos - startPos), new TextSpan(stmt.Value.TextSpan.Start - startPos, stmt.Value.TextSpan.Length));
		}
	}
}
