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

namespace dnSpy.Contracts.Decompiler {
	/// <summary>
	/// The user's default decompiler language. Components outside dnSpy.exe (debugger,
	/// analyzers) that must follow the language the user selected import this instead of
	/// guessing among the decompilers that share a <see cref="IDecompiler.GenericGuid"/>.
	/// Implemented by dnSpy.exe on top of its own settings service.
	/// </summary>
	public interface IDecompilerLanguageSettings {
		/// <summary>
		/// Unique GUID (<see cref="IDecompiler.UniqueGuid"/>) of the default language
		/// </summary>
		Guid LanguageGuid { get; }
	}
}
