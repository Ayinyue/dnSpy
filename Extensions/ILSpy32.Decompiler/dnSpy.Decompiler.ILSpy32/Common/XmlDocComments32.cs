using System;
using System.Collections.Generic;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler.XmlDoc;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// XML documentation lines for the IL / Mixed views, ported from the 2.3 engine's
	/// <c>ILDecompiler.GetXmlDocComments</c>.
	///
	/// The 3.2 <c>ReflectionDisassembler</c> exposes the hook as
	/// <c>Func&lt;IMemberDefinition, IEnumerable&lt;string&gt;&gt;</c>, so the cecil member is first
	/// resolved back to its dnlib counterpart (the bridge preserves rids, so the metadata token
	/// is authoritative) before the documentation is looked up.
	/// </summary>
	static class XmlDocComments32 {
		/// <summary>
		/// Returns one entry per documentation line, each prefixed with a space (the disassembler
		/// writes the <c>///</c> delimiter itself). Blank lines are preserved so the rendered
		/// comment keeps its paragraph structure.
		/// </summary>
		internal static IEnumerable<string> GetXmlDocComments(ModuleDef module, IMemberDefinition member, StringBuilder sb) {
			if (module is null || member is null)
				yield break;

			IMemberRef? dnlibMember;
			try {
				dnlibMember = module.ResolveToken((uint)member.MetadataToken.ToInt32()) as IMemberRef;
			}
			catch {
				dnlibMember = null;
			}
			if (dnlibMember is null || dnlibMember.Module is null)
				yield break;

			var xmldoc = XmlDocLoader.LoadDocumentation(dnlibMember.Module);
			if (xmldoc is null)
				yield break;
			var doc = xmldoc.GetDocumentation(XmlDocKeyProvider.GetKey(dnlibMember, sb)) ??
						xmldoc.GetDocumentation(XmlDocKeyProvider.GetKey(dnlibMember, sb, XmlDocCompiler.MSVC));
			if (string.IsNullOrEmpty(doc))
				yield break;

			foreach (var line in SplitLines(doc!)) {
				sb.Clear();
				if (line.Length != 0) {
					sb.Append(' ');
					sb.Append(line);
				}
				yield return sb.ToString();
			}
		}

		/// <summary>
		/// Splits the documentation into lines, dropping leading and trailing blank lines and
		/// stripping the indentation of the first content line from every line (the 2.3
		/// XmlDocLine behavior). Interior blank lines yield an empty string so the paragraph
		/// structure survives.
		/// </summary>
		static IEnumerable<string> SplitLines(string doc) {
			var lines = doc.Split('\n');
			var start = 0;
			while (start < lines.Length && lines[start].Trim().Length == 0)
				start++;
			var end = lines.Length;
			while (end > start && lines[end - 1].Trim().Length == 0)
				end--;
			if (start >= end)
				yield break;

			var first = RemoveCR(lines[start]);
			var indent = first.Substring(0, first.Length - first.TrimStart().Length);
			for (var i = start; i < end; i++) {
				var line = RemoveCR(lines[i]);
				if (line.Trim().Length == 0) {
					yield return string.Empty;
					continue;
				}
				if (indent.Length != 0 && line.StartsWith(indent, StringComparison.Ordinal))
					line = line.Substring(indent.Length);
				yield return line;
			}
		}

		static string RemoveCR(string line) => line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line;
	}
}
