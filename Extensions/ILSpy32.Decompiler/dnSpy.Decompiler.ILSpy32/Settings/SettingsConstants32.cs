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

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// GUIDs used by the 3.2 settings pages and the settings-modified listener. The page and
	/// refresh GUIDs are the same values the 2.3 extension uses so that, when the 2.3 extension
	/// is removed (M4), the 3.2 pages take over the exact same slots. Every field notes its
	/// 2.3 source.
	/// </summary>
	static class SettingsConstants32 {
		/// <summary>
		/// C# / Visual Basic (ILSpy) settings page. Source: 2.3
		/// <c>Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/Settings/CSharpDecompilerSettingsPage.cs:36</c>.
		/// </summary>
		public static readonly Guid PAGE_CSHARP_ILSPY = new Guid("8929CE8E-7E2C-4701-A8BA-42F70363872C");

		/// <summary>
		/// IL (ILSpy) settings page. Source: 2.3
		/// <c>Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/Settings/ILDecompilerSettingsPage.cs:32</c>.
		/// </summary>
		public static readonly Guid PAGE_IL_ILSPY = new Guid("0F8FBD3F-01DA-4AF0-9316-B7B5C8901A74");

		/// <summary>
		/// Visual Basic (ILSpy 3.2) settings page. New in 3.2: the 2.3 page was shared by C# and
		/// VB because both languages used one <c>DecompilerSettings</c> instance, but 3.2 has a
		/// separate <see cref="VbDecompilerSettings32Impl"/> singleton, so it needs its own page.
		/// </summary>
		public static readonly Guid PAGE_VISUALBASIC_ILSPY = new Guid("3F8A1C2E-7B4D-4E6A-9C51-2D8E0F6A4B73");

		/// <summary>
		/// Redisassemble IL (ILSpy) code. Source: 2.3
		/// <c>Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/Settings/SettingsConstants.cs:27</c>.
		/// </summary>
		public static readonly Guid REDISASSEMBLE_IL_ILSPY_CODE = new Guid("9F975119-FB7A-461D-BA17-09B82C7AE258");

		/// <summary>
		/// Redecompile ILAst (ILSpy) code. Source: 2.3
		/// <c>Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/Settings/SettingsConstants.cs:32</c>.
		/// </summary>
		public static readonly Guid REDECOMPILE_ILAST_ILSPY_CODE = new Guid("20D97DB7-EA2D-489D-820F-DCDCE3AB01D3");

		/// <summary>
		/// Redecompile C# (ILSpy) code. Source: 2.3
		/// <c>Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/Settings/SettingsConstants.cs:37</c>.
		/// </summary>
		public static readonly Guid REDECOMPILE_CSHARP_ILSPY_CODE = new Guid("00C8E462-E6E7-4D1F-8CE0-3385B34EA1FB");

		/// <summary>
		/// Redecompile VB (ILSpy) code. Source: 2.3
		/// <c>Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/Settings/SettingsConstants.cs:42</c>.
		/// </summary>
		public static readonly Guid REDECOMPILE_VB_ILSPY_CODE = new Guid("818B8011-851F-4868-9674-3AC3A7B0AEC6");
	}
}
