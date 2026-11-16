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
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// Registers the 3.2 decompiler settings pages. Ports the 2.3
	/// <c>DecompilerSettingsPageProvider</c>; the C#/IL page GUIDs are the same as 2.3's so the
	/// pages replace them when the 2.3 extension is removed (M4). The VB page is new (3.2 has a
	/// separate VB settings singleton).
	/// </summary>
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class DecompilerSettingsPageProvider32 : IAppSettingsPageProvider {
		readonly DecompilerSettings32Impl csharpSettings;
		readonly VbDecompilerSettings32Impl vbSettings;
		readonly ILDecompilerSettings32Impl ilSettings;

		[ImportingConstructor]
		DecompilerSettingsPageProvider32(DecompilerSettings32Impl csharpSettings, VbDecompilerSettings32Impl vbSettings, ILDecompilerSettings32Impl ilSettings) {
			this.csharpSettings = csharpSettings;
			this.vbSettings = vbSettings;
			this.ilSettings = ilSettings;
		}

		public IEnumerable<AppSettingsPage> Create() {
			yield return new CSharpDecompilerSettingsPage32(csharpSettings);
			yield return new VbDecompilerSettingsPage32(vbSettings);
			yield return new ILDecompilerSettingsPage32(ilSettings);
		}
	}
}
