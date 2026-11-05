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
using System.Globalization;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy32.CSharp {
	/// <summary>
	/// Writes the 3.2 engine's C# syntax tree into dnSpy's decompiler output with per-token
	/// colors and metadata references (click navigation), mirroring the 2.3 engine's
	/// TextTokenWriter. Text behavior mirrors the engine's TextWriterTokenWriter so the
	/// rendered code stays identical to the previous text-only rendering.
	/// </summary>
	sealed class DnSpyTokenWriter : TokenWriter {
		readonly IDecompilerOutput output;
		readonly ModuleDefMD module;
		readonly DecompilerTypeSystem? typeSystem;
		readonly MetadataTextColorProvider metadataTextColorProvider;
		readonly Stack<AstNode> nodeStack = new Stack<AstNode>();
		bool isAtStartOfLine = true;

		public DnSpyTokenWriter(IDecompilerOutput output, ICSharpCode.Decompiler.CSharp.CSharpDecompiler engine,
				ModuleDefMD module, MetadataTextColorProvider metadataTextColorProvider) {
			this.output = output;
			this.module = module;
			this.metadataTextColorProvider = metadataTextColorProvider;
			try {
				typeSystem = (DecompilerTypeSystem?)typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler)
					.GetField("typeSystem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
					.GetValue(engine);
			}
			catch {
				typeSystem = null;
			}
		}

		public override void StartNode(AstNode node) => nodeStack.Push(node);

		public override void EndNode(AstNode node) {
			if (nodeStack.Count > 0)
				nodeStack.Pop();
		}

		public override void WriteIdentifier(Identifier identifier) {
			var text = IdentifierEscaper.Escape(identifier.Name);
			if (identifier.IsVerbatim || CSharpOutputVisitor.IsKeyword(identifier.Name, identifier))
				text = "@" + text;

			// The identifier names the declared member: write it as a definition.
			if (identifier.Parent is EntityDeclaration entity && entity.Name == identifier.Name) {
				var definition = ResolveEntity(entity);
				if (definition != null) {
					output.Write(text, definition, DecompilerReferenceFlags.Definition, metadataTextColorProvider.GetColor(definition));
					return;
				}
			}

			// A member or type reference: 'foo.Bar', a base type, an attribute type...
			var reference = ResolveReference(identifier.Parent ?? (AstNode)identifier) ?? ResolveReference(identifier);
			if (reference != null) {
				output.Write(text, reference, DecompilerReferenceFlags.None, metadataTextColorProvider.GetColor(reference));
				return;
			}

			output.Write(text, BoxedTextColor.Text);
		}

		public override void WriteKeyword(Role role, string keyword) {
			output.Write(keyword, BoxedTextColor.Keyword);
			isAtStartOfLine = false;
		}

		public override void WriteToken(Role role, string token) {
			output.Write(token, IsPunctuation(token) ? BoxedTextColor.Punctuation : BoxedTextColor.Operator);
			isAtStartOfLine = false;
		}

		static bool IsPunctuation(string token) {
			foreach (var c in token) {
				if (c != '(' && c != ')' && c != '[' && c != ']' && c != '{' && c != '}' && c != ',' && c != ';' && c != '.')
					return false;
			}
			return token.Length > 0;
		}

		public override void Space() => output.Write(" ", BoxedTextColor.Text);

		public override void Indent() => output.IncreaseIndent();

		public override void Unindent() => output.DecreaseIndent();

		public override void NewLine() {
			output.WriteLine();
			isAtStartOfLine = true;
		}

		public override void WriteComment(CommentType commentType, string content) {
			switch (commentType) {
			case CommentType.SingleLine:
				output.Write("//", BoxedTextColor.Comment);
				output.Write(content, BoxedTextColor.Comment);
				NewLine();
				break;
			case CommentType.Documentation:
				output.Write("///", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				NewLine();
				break;
			case CommentType.MultiLine:
				output.Write("/*", BoxedTextColor.Comment);
				output.Write(content, BoxedTextColor.Comment);
				output.Write("*/", BoxedTextColor.Comment);
				isAtStartOfLine = false;
				break;
			case CommentType.MultiLineDocumentation:
				output.Write("/**", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				output.Write("*/", BoxedTextColor.XmlDocCommentDelimiter);
				isAtStartOfLine = false;
				break;
			default:
				output.Write(content, BoxedTextColor.Comment);
				isAtStartOfLine = false;
				break;
			}
		}

		public override void WritePreProcessorDirective(PreProcessorDirectiveType type, string argument) {
			if (!isAtStartOfLine)
				NewLine();
			var directive = type.ToString().ToLowerInvariant();
			output.Write("#" + directive, BoxedTextColor.Keyword);
			if (!string.IsNullOrEmpty(argument))
				output.Write(" " + argument, BoxedTextColor.Text);
			NewLine();
		}

		public override void WritePrimitiveValue(object value, string literalValue = null) {
			if (literalValue != null) {
				output.Write(literalValue, BoxedTextColor.Number);
				return;
			}

			if (value is null) {
				output.Write("null", BoxedTextColor.Keyword);
				return;
			}

			if (value is bool b) {
				output.Write(b ? "true" : "false", BoxedTextColor.Keyword);
				return;
			}

			if (value is string s) {
				output.Write("\"" + TextWriterTokenWriter.ConvertString(s) + "\"", BoxedTextColor.String);
				return;
			}

			if (value is char c) {
				output.Write("'" + TextWriterTokenWriter.ConvertCharLiteral(c) + "'", BoxedTextColor.Char);
				return;
			}

			if (value is decimal d) {
				output.Write(d.ToString(NumberFormatInfo.InvariantInfo) + "m", BoxedTextColor.Number);
				return;
			}

			if (value is float f) {
				if (float.IsInfinity(f) || float.IsNaN(f)) {
					output.Write("float", BoxedTextColor.Keyword);
					output.Write(".", BoxedTextColor.Punctuation);
					output.Write(float.IsPositiveInfinity(f) ? "PositiveInfinity" : float.IsNegativeInfinity(f) ? "NegativeInfinity" : "NaN", BoxedTextColor.Keyword);
					return;
				}
				if (f == 0 && 1 / f == float.NegativeInfinity)
					output.Write("-", BoxedTextColor.Operator);
				output.Write(f.ToString("R", NumberFormatInfo.InvariantInfo) + "f", BoxedTextColor.Number);
				return;
			}

			if (value is double dbl) {
				if (double.IsInfinity(dbl) || double.IsNaN(dbl)) {
					output.Write("double", BoxedTextColor.Keyword);
					output.Write(".", BoxedTextColor.Punctuation);
					output.Write(double.IsPositiveInfinity(dbl) ? "PositiveInfinity" : double.IsNegativeInfinity(dbl) ? "NegativeInfinity" : "NaN", BoxedTextColor.Keyword);
					return;
				}
				if (dbl == 0 && 1 / dbl == double.NegativeInfinity)
					output.Write("-", BoxedTextColor.Operator);
				var number = dbl.ToString("R", NumberFormatInfo.InvariantInfo);
				if (number.IndexOf('.') < 0 && number.IndexOf('E') < 0)
					number += ".0";
				output.Write(number, BoxedTextColor.Number);
				return;
			}

			if (value is IFormattable formattable) {
				var sb = new StringBuilder();
				sb.Append(formattable.ToString(null, NumberFormatInfo.InvariantInfo));
				if (value is uint || value is ulong)
					sb.Append('u');
				if (value is long || value is ulong)
					sb.Append('L');
				output.Write(sb.ToString(), BoxedTextColor.Number);
				return;
			}

			output.Write(value.ToString(), BoxedTextColor.Number);
		}

		public override void WritePrimitiveType(string type) {
			output.Write(type, BoxedTextColor.Keyword);
			if (type == "new")
				output.Write("()", BoxedTextColor.Punctuation);
			isAtStartOfLine = false;
		}

		/// <summary>
		/// dnlib metadata reference for a declaration node (the entity the binder annotated or
		/// the type-system symbol resolved to cecil and back through the shared token table).
		/// </summary>
		object? ResolveEntity(AstNode node) {
			var definition = node.Annotation<MethodDef>() ?? node.Annotation<FieldDef>() ??
				node.Annotation<PropertyDef>() ?? node.Annotation<EventDef>() ??
				(object?)node.Annotation<TypeDef>();
			if (definition != null)
				return definition;
			if (typeSystem is null)
				return null;
			var symbol = node.GetSymbol();
			return SymbolToDnlib(symbol);
		}

		/// <summary>
		/// dnlib metadata reference for a reference expression or type name, resolved through the
		/// tree's resolve results and the shared dnlib/cecil token table (the bridge writes the
		/// snapshot with PreserveRids, so tokens are identical on both sides).
		/// </summary>
		object? ResolveReference(AstNode node) {
			if (node is null || typeSystem is null)
				return null;
			var rr = node.Annotation<MemberResolveResult>();
			if (rr?.Member != null)
				return ToDnlib(TryGetCecil(rr.Member));
			var trr = node.Annotation<TypeResolveResult>();
			if (trr?.Type?.GetDefinition() != null)
				return ToDnlib(TryGetCecil(trr.Type.GetDefinition()));
			var type = node.Annotation<ICSharpCode.Decompiler.TypeSystem.IType>();
			if (type != null) {
				var definition = type.GetDefinition();
				if (definition != null)
					return ToDnlib(TryGetCecil(definition));
			}
			var symbol = node.GetSymbol();
			return SymbolToDnlib(symbol);
		}

		object? SymbolToDnlib(ISymbol? symbol) {
			if (typeSystem is null || symbol is null)
				return null;
			switch (symbol) {
			case IMember member:
				return ToDnlib(TryGetCecil(member));
			case ICSharpCode.Decompiler.TypeSystem.IType type:
				var definition = type.GetDefinition();
				return definition != null ? ToDnlib(TryGetCecil(definition)) : null;
			}
			return null;
		}

		/// <summary>
		/// GetCecil can throw for members outside the decompiled module (e.g. references into
		/// assemblies that failed to resolve); a token writer must never kill a decompilation.
		/// </summary>
		Mono.Cecil.MemberReference? TryGetCecil(ICSharpCode.Decompiler.TypeSystem.IMember? member) {
			if (member is null || typeSystem is null)
				return null;
			try {
				return typeSystem.GetCecil(member);
			}
			catch {
				return null;
			}
		}

		Mono.Cecil.MemberReference? TryGetCecil(ICSharpCode.Decompiler.TypeSystem.ITypeDefinition? definition) {
			if (definition is null || typeSystem is null)
				return null;
			try {
				return typeSystem.GetCecil(definition);
			}
			catch {
				return null;
			}
		}

		object? ToDnlib(Mono.Cecil.MemberReference? cecilMember) {
			if (cecilMember is null)
				return null;
			try {
				return module.ResolveToken(new MDToken(cecilMember.MetadataToken.ToInt32())) as IMemberRef;
			}
			catch {
				return null;
			}
		}
	}
}
