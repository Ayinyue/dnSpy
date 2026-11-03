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
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.Decompiler {
	interface IDecompilerServiceSettings : INotifyPropertyChanged {
		Guid LanguageGuid { get; }
	}

	class DecompilerServiceSettings : ViewModelBase, IDecompilerServiceSettings {
		// The 3.2 engine's language GUIDs. The extension assemblies register them through MEF and
		// the main program has no assembly reference, so the values are spelled out here; they
		// come from Extensions/ILSpy32.Decompiler/dnSpy.Decompiler.ILSpy32/
		// {CSharp/CSharpDecompiler32.cs, VisualBasic/VBDecompiler32.cs}.
		internal static readonly Guid LANGUAGE_CSHARP_ILSPY32 = new Guid("8C1F4A62-9D3B-4E77-8A51-2F6B0C9D4E13");
		internal static readonly Guid LANGUAGE_VISUALBASIC_ILSPY32 = new Guid("9C5E4C7A-1F2B-4E6D-9A30-8B7D2E5F6A41");

		public Guid LanguageGuid {
			get => languageGuid;
			set {
				if (languageGuid != value) {
					languageGuid = value;
					OnPropertyChanged(nameof(LanguageGuid));
				}
			}
		}
		// Fresh installs get the 3.2 decompiler (M3). A saved setting always overrides this:
		// the UI stores UniqueGuids, so the generic LANGUAGE_CSHARP never reaches the file.
		Guid languageGuid = LANGUAGE_CSHARP_ILSPY32;

		public bool LanguageGuidMigratedTo32 {
			get => languageGuidMigratedTo32;
			set {
				if (languageGuidMigratedTo32 != value) {
					languageGuidMigratedTo32 = value;
					OnPropertyChanged(nameof(LanguageGuidMigratedTo32));
				}
			}
		}
		bool languageGuidMigratedTo32;
	}

	[Export, Export(typeof(IDecompilerServiceSettings))]
	sealed class DecompilerServiceSettingsImpl : DecompilerServiceSettings {
		static readonly Guid SETTINGS_GUID = new Guid("6A7E565D-DC09-4AAE-A7C8-E86A835FCBFC");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		DecompilerServiceSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			LanguageGuid = sect.Attribute<Guid?>(nameof(LanguageGuid)) ?? LanguageGuid;
			LanguageGuidMigratedTo32 = sect.Attribute<bool?>(nameof(LanguageGuidMigratedTo32)) ?? false;
			PropertyChanged += DecompilerServiceSettingsImpl_PropertyChanged;
			if (!LanguageGuidMigratedTo32) {
				// One-time migration (M3, docs/Migration-3.2-Plan.md, scheme B): point a saved 2.3
				// language at its 3.2 counterpart. The 2.3 registrations stay until M4, so a user
				// who deliberately switches back keeps that choice — the flag stops the rewrite.
				var migratedGuid = MigrateTo32(LanguageGuid);
				if (migratedGuid != LanguageGuid)
					LanguageGuid = migratedGuid;
				LanguageGuidMigratedTo32 = true;
			}
		}

		internal static Guid MigrateTo32(Guid guid) {
			if (guid == DecompilerConstants.LANGUAGE_VISUALBASIC_ILSPY)
				return LANGUAGE_VISUALBASIC_ILSPY32;
			if (guid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
				return LANGUAGE_CSHARP_ILSPY32;
			return guid;
		}

		void DecompilerServiceSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(LanguageGuid), LanguageGuid);
			sect.Attribute(nameof(LanguageGuidMigratedTo32), LanguageGuidMigratedTo32);
		}
	}
}
