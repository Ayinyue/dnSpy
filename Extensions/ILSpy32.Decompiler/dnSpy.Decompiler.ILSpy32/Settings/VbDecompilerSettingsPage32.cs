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
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// Visual Basic (ILSpy 3.2) settings page. New in 3.2: the 2.3 extension had a single
	/// "C# / Visual Basic" page because both languages shared one settings instance, but 3.2 has
	/// a dedicated <see cref="VbDecompilerSettings32Impl"/> singleton.
	/// </summary>
	sealed class VbDecompilerSettingsPage32 : DecompilerSettingsPage32Base {
		readonly VbDecompilerSettings32Impl globalSettings;

		/// <summary>Edited clone; the page's DataTemplate binds <c>Settings.Options</c>.</summary>
		public DecompilerSettings32 Settings { get; }

		public override double Order => AppSettingsConstants.ORDER_DECOMPILER_SETTINGS_ILSPY_CSHARP + 500;
		public override Guid Guid => SettingsConstants32.PAGE_VISUALBASIC_ILSPY;
		public override string Title => "Visual Basic (ILSpy 3.2)";
		protected override DecompilerSettingsBase GlobalSettings => globalSettings;
		protected override DecompilerSettingsBase EditedSettings => Settings;
		protected override Guid RefreshGuid => SettingsConstants32.REDECOMPILE_VB_ILSPY_CODE;

		public VbDecompilerSettingsPage32(VbDecompilerSettings32Impl settings) {
			globalSettings = settings;
			Settings = (DecompilerSettings32)settings.Clone();
		}
	}
}
