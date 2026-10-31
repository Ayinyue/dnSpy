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

namespace dnSpy.Decompiler.ILSpy32.Common {
	static class TypeNameHelper {
		/// <summary>
		/// Converts a cecil type name to the reflection-style name expected by
		/// <c>ICSharpCode.Decompiler.TypeSystem.FullTypeName(string)</c>.
		///
		/// cecil separates a declaring type from its nested types with <c>/</c>
		/// (<c>Ns.Outer/Inner</c>), while <c>FullTypeName</c> only splits on <c>+</c>.
		/// Passing a cecil name straight through makes the engine treat the whole
		/// string as a top-level type name, so nested types cannot be resolved and
		/// <c>DecompileType</c> throws "Could not find type definition ...".
		/// </summary>
		public static string ToReflectionName(string cecilTypeFullName) =>
			cecilTypeFullName.Replace('/', '+');
	}
}
