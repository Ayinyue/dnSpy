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
	/// Persisted IL (ILSpy 3.2) settings. One MEF singleton shared by the IL view and the Mixed
	/// view - the 2.3 side did the same with its <c>ILSettings</c> singleton. Also carries the
	/// four 2.3 IL options that the 3.2 engine honours over from the 2.3 section.
	/// </summary>
	[Export]
	sealed class ILDecompilerSettings32Impl : ILDecompilerSettings32 {
		static readonly Guid SETTINGS_GUID = new Guid("3B7E2A94-6D1C-4F58-9A42-1C8E5D7B60F1");
		// The 2.3 IL settings section (Extensions/ILSpy.Decompiler/.../Settings/ILSettingsImpl.cs)
		internal static readonly Guid Legacy23ILSettingsGuid = new Guid("DD6752B1-5336-4601-A9B2-0879E18AE9F3");

		readonly ISettingsService settingsService;
		bool disableSave;

		[ImportingConstructor]
		ILDecompilerSettings32Impl(ISettingsService settingsService) {
			this.settingsService = settingsService;
			disableSave = true;
			LoadSettings(SETTINGS_GUID);
			MigrateFrom23();
			disableSave = false;
			VersionChanged += (s, e) => SaveSettings();
		}

		void LoadSettings(Guid sectionGuid) {
			var sect = settingsService.GetOrCreateSection(sectionGuid);
			ShowXmlDocumentation = sect.Attribute<bool?>(nameof(ShowXmlDocumentation)) ?? ShowXmlDocumentation;
			ShowTokenAndRvaComments = sect.Attribute<bool?>(nameof(ShowTokenAndRvaComments)) ?? ShowTokenAndRvaComments;
			ShowILBytes = sect.Attribute<bool?>(nameof(ShowILBytes)) ?? ShowILBytes;
			ShowPdbInfo = sect.Attribute<bool?>(nameof(ShowPdbInfo)) ?? ShowPdbInfo;
		}

		/// <summary>
		/// One-time carry-over of the 2.3 IL options. The 3.2 engine has no equivalent for the
		/// other four (IL comments, member sorting, max string length, hexadecimal numbers).
		/// </summary>
		void MigrateFrom23() {
			var target = settingsService.GetOrCreateSection(SETTINGS_GUID);
			if (target.Attributes.Length != 0)
				return; // the user already has 3.2-era values; migration is done
			var legacy = settingsService.GetOrCreateSection(Legacy23ILSettingsGuid);
			foreach (var (key, value) in legacy.Attributes) {
				if (!bool.TryParse(value, out var b))
					continue;
				switch (key) {
					case nameof(ShowXmlDocumentation): ShowXmlDocumentation = b; break;
					case nameof(ShowTokenAndRvaComments): ShowTokenAndRvaComments = b; break;
					case nameof(ShowILBytes): ShowILBytes = b; break;
					case nameof(ShowPdbInfo): ShowPdbInfo = b; break;
				}
			}
		}

		void SaveSettings() {
			if (disableSave)
				return;
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(ShowXmlDocumentation), ShowXmlDocumentation);
			sect.Attribute(nameof(ShowTokenAndRvaComments), ShowTokenAndRvaComments);
			sect.Attribute(nameof(ShowILBytes), ShowILBytes);
			sect.Attribute(nameof(ShowPdbInfo), ShowPdbInfo);
		}
	}
}
