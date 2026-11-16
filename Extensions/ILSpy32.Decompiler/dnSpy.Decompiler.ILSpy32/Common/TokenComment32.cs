using System;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// The <c>// Token: 0x... RID: ... RVA: ... File Offset: ...</c> comment, ported from the 2.3
	/// engine's <c>ReflectionDisassembler.AddComment</c>. The 3.2 disassembler only exposes a
	/// string provider, so the cecil member is resolved back to dnlib (the bridge preserves rids)
	/// and the RVA / file offset are read from dnlib.
	/// </summary>
	static class TokenComment32 {
		internal static string GetComment(ModuleDef module, IMemberDefinition member) {
			if (module is null || member is null)
				return null;

			IMemberDef dnlibMember;
			try {
				dnlibMember = module.ResolveToken((uint)member.MetadataToken.ToInt32()) as IMemberDef;
			}
			catch {
				return null;
			}
			if (dnlibMember is null)
				return null;

			var sb = new StringBuilder();
			sb.Append(" Token: ").AppendFormat("0x{0:X8}", dnlibMember.MDToken.Raw);
			sb.Append(" RID: ").Append(dnlibMember.MDToken.Rid);

			uint rva;
			long fileOffset;
			dnlibMember.GetRVA(out rva, out fileOffset);
			if (rva != 0) {
				sb.Append(" RVA: ").AppendFormat("0x{0:X8}", rva);
				sb.Append(" File Offset: ").AppendFormat("0x{0:X8}", fileOffset);
			}
			return sb.ToString();
		}
	}
}
