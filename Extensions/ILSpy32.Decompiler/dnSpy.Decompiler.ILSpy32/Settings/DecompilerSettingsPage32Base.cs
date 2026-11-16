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
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// Base class for the 3.2 decompiler settings pages. Mirrors the 2.3 pages: the page edits a
	/// clone of the persisted settings and writes the option values back through their
	/// <see cref="IDecompilerOption"/> setters on apply, which raises
	/// <see cref="DecompilerSettingsBase.VersionChanged"/> and persists them. The DataTemplate
	/// binds the page's <c>Settings.Options</c> collection and renders one row per option.
	/// </summary>
	abstract class DecompilerSettingsPage32Base : AppSettingsPage, IAppSettingsPage2 {
		/// <summary>The persisted settings singleton.</summary>
		protected abstract DecompilerSettingsBase GlobalSettings { get; }
		/// <summary>The clone edited by the page.</summary>
		protected abstract DecompilerSettingsBase EditedSettings { get; }
		/// <summary>Refresh GUID to add when any option changed.</summary>
		protected abstract Guid RefreshGuid { get; }

		public override Guid ParentGuid => new Guid(AppSettingsConstants.GUID_DECOMPILER);
		public override object? UIObject => this;

		public override void OnApply() => throw new InvalidOperationException();

		public void OnApply(IAppRefreshSettings appRefreshSettings) {
			if (CopyOptions(EditedSettings, GlobalSettings))
				appRefreshSettings.Add(RefreshGuid);
		}

		public override string[]? GetSearchStrings() {
			var list = new List<string>();
			foreach (var option in EditedSettings.Options) {
				if (!string.IsNullOrEmpty(option.Name))
					list.Add(option.Name);
				if (!string.IsNullOrEmpty(option.Description))
					list.Add(option.Description);
			}
			return list.ToArray();
		}

		/// <summary>
		/// Copies the edited option values onto the persisted settings through their setters (so
		/// they raise <see cref="DecompilerSettingsBase.VersionChanged"/> and persist). Returns
		/// true if any value changed.
		/// </summary>
		protected static bool CopyOptions(DecompilerSettingsBase source, DecompilerSettingsBase target) {
			bool changed = false;
			foreach (var src in source.Options) {
				var dst = target.TryGetOption(src.Guid);
				if (dst is null || Equals(dst.Value, src.Value))
					continue;
				dst.Value = src.Value;
				changed = true;
			}
			return changed;
		}
	}
}
