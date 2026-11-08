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
using System.Collections.Generic;
using dnSpy.Contracts.Decompiler;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// Persisted C# (ILSpy 3.2) settings. One MEF singleton; the decompiler creators hand
	/// this instance to the C# decompiler so option edits reach the output immediately.
	/// </summary>
	[Export]
	sealed class DecompilerSettings32Impl : DecompilerSettings32 {
		static readonly Guid SETTINGS_GUID = new Guid("7E19E9D1-4B7A-4E12-9A03-6D7D9F52C331");

		readonly ISettingsService settingsService;
		bool disableSave;

		[ImportingConstructor]
		DecompilerSettings32Impl(ISettingsService settingsService) : base() {
			this.settingsService = settingsService;
			settingsService_Migrate = settingsService;
			disableSave = true;
			LoadSettings(SETTINGS_GUID);
			// One-time carry-over of the option values the user customized under the 2.3
			// decompilers (same GUIDs / same meaning where the 3.2 engine honors them).
			MigrateFrom23(SETTINGS_GUID);
			disableSave = false;
			VersionChanged += (s, e) => SaveSettings();
		}

		void LoadSettings(Guid sectionGuid) {
			var sect = settingsService.GetOrCreateSection(sectionGuid);
			foreach (var entry in PersistedBooleanEntries) {
				var value = sect.Attribute<bool?>(entry.key);
				if (value.HasValue)
					entry.set(value.Value);
			}
		}

		void SaveSettings() {
			if (disableSave)
				return;
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			foreach (var entry in PersistedBooleanEntries)
				sect.Attribute(entry.key, entry.value);
		}

		internal override IEnumerable<(string key, bool? value, Action<bool> set)> PersistedBooleanEntries =>
			new (string, bool?, Action<bool>)[] {
				("AnonymousMethods", EngineSettings.AnonymousMethods, v => { EngineSettings.AnonymousMethods = v; RaiseVersionChanged(); }),
				("ExpressionTrees", EngineSettings.ExpressionTrees, v => { EngineSettings.ExpressionTrees = v; RaiseVersionChanged(); }),
				("YieldReturn", EngineSettings.YieldReturn, v => { EngineSettings.YieldReturn = v; RaiseVersionChanged(); }),
				("AsyncAwait", EngineSettings.AsyncAwait, v => { EngineSettings.AsyncAwait = v; RaiseVersionChanged(); }),
				("QueryExpressions", EngineSettings.QueryExpressions, v => { EngineSettings.QueryExpressions = v; RaiseVersionChanged(); }),
				("FullyQualifyAmbiguousTypeNames", EngineSettings.FullyQualifyAmbiguousTypeNames, v => { EngineSettings.FullyQualifyAmbiguousTypeNames = v; RaiseVersionChanged(); }),
				("UseDebugSymbols", EngineSettings.UseDebugSymbols, v => { EngineSettings.UseDebugSymbols = v; RaiseVersionChanged(); }),
				("ShowXmlDocumentation", EngineSettings.ShowXmlDocumentation, v => { EngineSettings.ShowXmlDocumentation = v; RaiseVersionChanged(); }),
				("ShowTokenAndRvaComments", ShowTokenAndRvaComments, v => ShowTokenAndRvaComments = v),
				("AlwaysGenerateExceptionVariableForCatchBlocks", EngineSettings.AlwaysGenerateExceptionVariableForCatchBlocks, v => { EngineSettings.AlwaysGenerateExceptionVariableForCatchBlocks = v; RaiseVersionChanged(); }),
			};
	}

	/// <summary>
	/// Persisted Visual Basic (ILSpy 3.2) settings.
	/// </summary>
	[Export]
	sealed class VbDecompilerSettings32Impl : DecompilerSettings32 {
		static readonly Guid SETTINGS_GUID = new Guid("C02D7A64-9E5C-4B3D-8C41-2F1A9E0B7D55");
		readonly ISettingsService settingsService;
		bool disableSave;

		[ImportingConstructor]
		VbDecompilerSettings32Impl(ISettingsService settingsService) : base(vbCompatibility: true) {
			this.settingsService = settingsService;
			settingsService_Migrate = settingsService;
			disableSave = true;
			LoadSettings(SETTINGS_GUID);
			MigrateFrom23(SETTINGS_GUID);
			disableSave = false;
			VersionChanged += (s, e) => SaveSettings();
		}

		void LoadSettings(Guid sectionGuid) {
			var sect = settingsService.GetOrCreateSection(sectionGuid);
			foreach (var entry in PersistedBooleanEntries) {
				var value = sect.Attribute<bool?>(entry.key);
				if (value.HasValue)
					entry.set(value.Value);
			}
		}

		void SaveSettings() {
			if (disableSave)
				return;
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			foreach (var entry in PersistedBooleanEntries)
				sect.Attribute(entry.key, entry.value);
		}

		// QueryExpressions is pinned false by the VB-compatibility constructor (the VB
		// converter cannot map query continuation clauses) and is not persisted for VB.
		internal override IEnumerable<(string key, bool? value, Action<bool> set)> PersistedBooleanEntries =>
			new (string, bool?, Action<bool>)[] {
				("AnonymousMethods", EngineSettings.AnonymousMethods, v => { EngineSettings.AnonymousMethods = v; RaiseVersionChanged(); }),
				("ExpressionTrees", EngineSettings.ExpressionTrees, v => { EngineSettings.ExpressionTrees = v; RaiseVersionChanged(); }),
				("YieldReturn", EngineSettings.YieldReturn, v => { EngineSettings.YieldReturn = v; RaiseVersionChanged(); }),
				("AsyncAwait", EngineSettings.AsyncAwait, v => { EngineSettings.AsyncAwait = v; RaiseVersionChanged(); }),
				("FullyQualifyAmbiguousTypeNames", EngineSettings.FullyQualifyAmbiguousTypeNames, v => { EngineSettings.FullyQualifyAmbiguousTypeNames = v; RaiseVersionChanged(); }),
				("UseDebugSymbols", EngineSettings.UseDebugSymbols, v => { EngineSettings.UseDebugSymbols = v; RaiseVersionChanged(); }),
				("ShowXmlDocumentation", EngineSettings.ShowXmlDocumentation, v => { EngineSettings.ShowXmlDocumentation = v; RaiseVersionChanged(); }),
				("ShowTokenAndRvaComments", ShowTokenAndRvaComments, v => ShowTokenAndRvaComments = v),
				("AlwaysGenerateExceptionVariableForCatchBlocks", EngineSettings.AlwaysGenerateExceptionVariableForCatchBlocks, v => { EngineSettings.AlwaysGenerateExceptionVariableForCatchBlocks = v; RaiseVersionChanged(); }),
			};
	}
}
