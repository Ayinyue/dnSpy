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

using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy32.IL;
using dnSpy.Decompiler.ILSpy32.ILAst;
using dnSpy.Decompiler.ILSpy32.Mixed;
using dnSpy.Decompiler.ILSpy32.Settings;
using dnSpy.Decompiler.ILSpy32.VisualBasic;

namespace dnSpy.Decompiler.ILSpy32.CSharp {
	[Export(typeof(IDecompilerCreator))]
	sealed class DecompilerCreator : IDecompilerCreator {
		readonly DecompilerSettings32? csharpSettings;
		readonly VbDecompilerSettings32Impl? vbSettings;
		readonly ILDecompilerSettings32Impl? ilSettings;
		readonly ILAstDecompilerSettings32Impl? ilastSettings;

		/// <summary>Reflection-based loaders (dnSpy.Console, baseline) have no MEF
		/// composition; they get default settings with no persistence.</summary>
		public DecompilerCreator() {
		}

		[ImportingConstructor]
		public DecompilerCreator(DecompilerSettings32Impl csharpSettings, VbDecompilerSettings32Impl vbSettings,
				ILDecompilerSettings32Impl ilSettings, ILAstDecompilerSettings32Impl ilastSettings) {
			this.csharpSettings = csharpSettings;
			this.vbSettings = vbSettings;
			this.ilSettings = ilSettings;
			this.ilastSettings = ilastSettings;
		}

		public IEnumerable<IDecompiler> Create() {
			yield return new CSharpDecompiler32(csharpSettings ?? new DecompilerSettings32(), "C# (ILSpy 3.2)");
			yield return new VBDecompiler32(vbSettings ?? new DecompilerSettings32(vbCompatibility: true), "Visual Basic (ILSpy 3.2)");
			// The IL/ILAst settings are persisted singletons; the reflection-based loaders fall
			// back to default instances.
			yield return new ILDecompiler32(ilSettings ?? new ILDecompilerSettings32(), "IL (ILSpy 3.2)");
			// Unlike the 2.3 ILAst language (a #if DEBUG dev tool with one entry per optimizer
			// step), this one is always registered; its options live in its settings page.
			yield return new ILAstDecompiler32(ilastSettings ?? new ILAstDecompilerSettings32(), "ILAst (ILSpy 3.2)");
			// Mixed's C# overlay uses the persisted C# settings (same instance as the C# view);
			// its decorations use the persisted IL settings (shared with the IL view, as in 2.3).
			yield return new ILWithCSharpDecompiler32(csharpSettings ?? new DecompilerSettings32(), ilSettings ?? new ILDecompilerSettings32(), "IL with C# (ILSpy 3.2)");
		}
	}
}
