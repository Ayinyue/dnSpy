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

using System.ComponentModel.Composition;
using dnSpy.Contracts.Text;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Decompiler.ILSpy32 {
	/// <summary>
	/// Content types of the five 3.2 languages. The names are identical to the 2.3 extension's so
	/// the editor keeps its syntax highlighting and text-view settings; they must be exported from
	/// this assembly too, because removing the 2.3 extension (M4) would otherwise leave the 3.2
	/// languages with dangling <see cref="IDecompiler.ContentTypeString"/> values.
	/// </summary>
	static class ContentTypeDefinitions {
		public const string DecompilerILSpy = "Decompiler ILSpy";
		public const string CSharpILSpy = "C# ILSpy";
		public const string VisualBasicILSpy = "VB ILSpy";
		public const string ILILSpy = "IL ILSpy";
		public const string ILAstILSpy = "ILAst ILSpy";
		public const string ILWithCSharpILSpy = "IL with C# ILSpy";

#pragma warning disable CS0169
		[Export]
		[Name(DecompilerILSpy)]
		[BaseDefinition(ContentTypes.DecompiledCode)]
		static readonly ContentTypeDefinition? DecompilerILSpyContentTypeDefinition;

		[Export]
		[Name(CSharpILSpy)]
		[BaseDefinition(DecompilerILSpy)]
		[BaseDefinition(ContentTypes.CSharp)]
		static readonly ContentTypeDefinition? CSharpILSpyContentTypeDefinition;

		[Export]
		[Name(VisualBasicILSpy)]
		[BaseDefinition(DecompilerILSpy)]
		[BaseDefinition(ContentTypes.VisualBasic)]
		static readonly ContentTypeDefinition? VisualBasicILSpyContentTypeDefinition;

		[Export]
		[Name(ILILSpy)]
		[BaseDefinition(DecompilerILSpy)]
		[BaseDefinition(ContentTypes.IL)]
		static readonly ContentTypeDefinition? ILILSpyContentTypeDefinition;

		[Export]
		[Name(ILAstILSpy)]
		[BaseDefinition(DecompilerILSpy)]
		static readonly ContentTypeDefinition? ILAstILSpyContentTypeDefinition;

		[Export]
		[Name(ILWithCSharpILSpy)]
		[BaseDefinition(ILILSpy)]
		static readonly ContentTypeDefinition? ILWithCSharpILSpyContentTypeDefinition;
#pragma warning restore CS0169
	}
}
