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
using dnSpy.Decompiler.ILSpy32.Settings;
using dnSpy.Decompiler.ILSpy32.VisualBasic;

namespace dnSpy.Decompiler.ILSpy32.CSharp {
	[Export(typeof(IDecompilerCreator))]
	sealed class DecompilerCreator : IDecompilerCreator {
		readonly DecompilerSettings32? csharpSettings;
		readonly VbDecompilerSettings32Impl? vbSettings;

		/// <summary>Reflection-based loaders (dnSpy.Console, baseline) have no MEF
		/// composition; they get default settings with no persistence.</summary>
		public DecompilerCreator() {
		}

		[ImportingConstructor]
		public DecompilerCreator(DecompilerSettings32Impl csharpSettings, VbDecompilerSettings32Impl vbSettings) {
			this.csharpSettings = csharpSettings;
			this.vbSettings = vbSettings;
		}

		public IEnumerable<IDecompiler> Create() {
			yield return new CSharpDecompiler32(csharpSettings ?? new DecompilerSettings32(), "C# (ILSpy 3.2)");
			yield return new VBDecompiler32(vbSettings ?? new DecompilerSettings32(vbCompatibility: true), "Visual Basic (ILSpy 3.2)");
		}
	}
}
