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
using dnSpy.Decompiler.ILSpy32.IL;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// IL (ILSpy 3.2) settings page. Ports the 2.3 <c>ILDecompilerSettingsPage</c>; the
	/// DataTemplate binds <see cref="Settings"/> to <see cref="ILDecompilerSettings32Impl"/>
	/// and renders <see cref="ILDecompilerSettings32.Options"/>.
	/// </summary>
	sealed class ILDecompilerSettingsPage32 : DecompilerSettingsPage32Base {
		readonly ILDecompilerSettings32Impl globalSettings;

		/// <summary>Edited clone; the page's DataTemplate binds <c>Settings.Options</c>.</summary>
		public ILDecompilerSettings32 Settings { get; }

		public override double Order => AppSettingsConstants.ORDER_DECOMPILER_SETTINGS_ILSPY_IL;
		public override Guid Guid => SettingsConstants32.PAGE_IL_ILSPY;
		public override string Title => "IL (ILSpy 3.2)";
		protected override DecompilerSettingsBase GlobalSettings => globalSettings;
		protected override DecompilerSettingsBase EditedSettings => Settings;
		protected override Guid RefreshGuid => SettingsConstants32.REDISASSEMBLE_IL_ILSPY_CODE;

		public ILDecompilerSettingsPage32(ILDecompilerSettings32Impl settings) {
			globalSettings = settings;
			Settings = (ILDecompilerSettings32)settings.Clone();
		}
	}
}
