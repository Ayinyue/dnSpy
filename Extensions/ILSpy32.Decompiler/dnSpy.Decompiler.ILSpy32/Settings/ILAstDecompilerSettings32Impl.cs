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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings;
using dnSpy.Decompiler.ILSpy32.IL;
using dnSpy.Decompiler.ILSpy32.ILAst;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// Persisted ILAst (ILSpy 3.2) settings. The 2.3 ILAst language had an options class with an
	/// empty option list, so there is nothing to migrate.
	/// </summary>
	[Export]
	sealed class ILAstDecompilerSettings32Impl : ILAstDecompilerSettings32 {
		static readonly Guid SETTINGS_GUID = new Guid("9F4C1D72-2B8E-4A63-B5C7-7D2E6F1A3094");

		readonly ISettingsService settingsService;
		bool disableSave;

		[ImportingConstructor]
		ILAstDecompilerSettings32Impl(ISettingsService settingsService) {
			this.settingsService = settingsService;
			disableSave = true;
			LoadSettings(SETTINGS_GUID);
			disableSave = false;
			VersionChanged += (s, e) => SaveSettings();
		}

		void LoadSettings(Guid sectionGuid) {
			var sect = settingsService.GetOrCreateSection(sectionGuid);
			RunTransforms = sect.Attribute<bool?>(nameof(RunTransforms)) ?? RunTransforms;
			ShowILRanges = sect.Attribute<bool?>(nameof(ShowILRanges)) ?? ShowILRanges;
			UseLogicOperationSugar = sect.Attribute<bool?>(nameof(UseLogicOperationSugar)) ?? UseLogicOperationSugar;
			UseFieldSugar = sect.Attribute<bool?>(nameof(UseFieldSugar)) ?? UseFieldSugar;
		}

		void SaveSettings() {
			if (disableSave)
				return;
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(RunTransforms), RunTransforms);
			sect.Attribute(nameof(ShowILRanges), ShowILRanges);
			sect.Attribute(nameof(UseLogicOperationSugar), UseLogicOperationSugar);
			sect.Attribute(nameof(UseFieldSugar), UseFieldSugar);
		}
	}
}
