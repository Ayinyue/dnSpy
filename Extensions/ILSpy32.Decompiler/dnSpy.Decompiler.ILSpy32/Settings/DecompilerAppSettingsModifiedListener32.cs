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
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings.Dialog;
using dnSpy.Decompiler.ILSpy32.CSharp;
using dnSpy.Decompiler.ILSpy32.IL;
using dnSpy.Decompiler.ILSpy32.ILAst;
using dnSpy.Decompiler.ILSpy32.Mixed;
using dnSpy.Decompiler.ILSpy32.VisualBasic;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// Refreshes the visible 3.2 decompiler code windows after the settings dialog closes.
	/// Ports the 2.3 <c>DecompilerAppSettingsModifiedListener</c>; the refresh GUIDs are the
	/// same, but the refresh targets are the 3.2 language classes.
	/// </summary>
	[ExportAppSettingsModifiedListener(Order = AppSettingsConstants.ORDER_LISTENER_DECOMPILER)]
	sealed class DecompilerAppSettingsModifiedListener32 : IAppSettingsModifiedListener {
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		DecompilerAppSettingsModifiedListener32(IDocumentTabService documentTabService) => this.documentTabService = documentTabService;

		public void OnSettingsModified(IAppRefreshSettings appRefreshSettings) {
			bool refreshIL = appRefreshSettings.Has(SettingsConstants32.REDISASSEMBLE_IL_ILSPY_CODE);
			bool refreshILAst = appRefreshSettings.Has(SettingsConstants32.REDECOMPILE_ILAST_ILSPY_CODE);
			bool refreshCSharp = appRefreshSettings.Has(SettingsConstants32.REDECOMPILE_CSHARP_ILSPY_CODE);
			bool refreshVB = appRefreshSettings.Has(SettingsConstants32.REDECOMPILE_VB_ILSPY_CODE);

			if (refreshIL)
				RefreshCode<ILDecompiler32>();
			// The 3.2 ILAst language is always registered (unlike the 2.3 #if DEBUG dev tool).
			if (refreshILAst)
				RefreshCode<ILAstDecompiler32>();
			if (refreshCSharp)
				RefreshCode<CSharpDecompiler32>();
			if (refreshVB)
				RefreshCode<VBDecompiler32>();
			// The Mixed view's C# overlay uses the C# settings and its decorations the IL settings.
			if (refreshCSharp || refreshIL)
				RefreshCode<ILWithCSharpDecompiler32>();
		}

		IEnumerable<(IDocumentTab tab, IDecompiler decompiler)> DecompilerTabs {
			get {
				foreach (var tab in documentTabService.VisibleFirstTabs) {
					var decompiler = (tab.Content as IDecompilerTabContent)?.Decompiler;
					if (decompiler is not null)
						yield return (tab, decompiler);
				}
			}
		}

		void RefreshCode<T>() => documentTabService.Refresh(DecompilerTabs.Where(t => t.decompiler is T).Select(a => a.tab).ToArray());
	}
}
