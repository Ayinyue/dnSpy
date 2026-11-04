// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// Copyright (c) 2014-2019 de4dot@gmail.com
//
// Ported from the ILSpy 2.3 VB output formatter. The IL-debug mapping parts
// (MethodDebugInfoBuilder / ILSpan / DecompilerContext) were removed because the
// ILSpy 3.2 engine has no ILAst; the remaining logic (identifier/reference
// coloring, comments, braces) is unchanged.

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using ICSharpCode.NRefactory.VB;
using ICSharpCode.NRefactory.VB.Ast;
using CSharp2 = ICSharpCode.NRefactory.CSharp;

namespace dnSpy.Decompiler.ILSpy32.VisualBasic {
	sealed class VBTextOutputFormatter32 : IOutputFormatter {
		readonly IDecompilerOutput output;
		readonly Stack<AstNode> nodeStack = new Stack<AstNode>();

		public VBTextOutputFormatter32(IDecompilerOutput output) {
			this.output = output ?? throw new ArgumentNullException(nameof(output));
		}

		public void StartNode(AstNode node) => nodeStack.Push(node);

		public void EndNode(AstNode node) {
			if (nodeStack.Pop() != node)
				throw new InvalidOperationException();
		}

		public void WriteIdentifier(string identifier, object data, object extraData) {
			var definition = GetCurrentDefinition();
			if (definition is not null) {
				output.Write(IdentifierEscaper.Escape(identifier), definition, DecompilerReferenceFlags.Definition, data);
				return;
			}

			var memberRef = GetCurrentMemberReference() ?? (object?)(extraData as NamespaceReference);
			if (memberRef is not null) {
				output.Write(IdentifierEscaper.Escape(identifier), memberRef, DecompilerReferenceFlags.None, data);
				return;
			}

			var localDef = GetCurrentLocalDefinition();
			if (localDef is not null) {
				output.Write(IdentifierEscaper.Escape(identifier), localDef, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, data);
				return;
			}

			var localRef = GetCurrentLocalReference();
			if (localRef is not null) {
				output.Write(IdentifierEscaper.Escape(identifier), localRef, DecompilerReferenceFlags.Local, data);
				return;
			}

			output.Write(IdentifierEscaper.Escape(identifier), data);
		}

		object? GetCurrentMemberReference() {
			var node = nodeStack.Peek();
			if (node.Role == AstNode.Roles.Type && node.Parent is ObjectCreationExpression)
				node = node.Parent;
			var memberRef = (object?)node.Annotation<IMemberRef>() ?? node.Annotation<IMemberDef>();
			if (memberRef is null && node is Identifier) {
				node = node.Parent ?? node;
				memberRef = (object?)node.Annotation<IMemberRef>() ?? node.Annotation<IMemberDef>();
			}
			if (memberRef is null && node.Role == AstNode.Roles.TargetExpression && (node.Parent is InvocationExpression || node.Parent is ObjectCreationExpression)) {
				memberRef = node.Parent.Annotation<IMemberRef>();
			}
			return memberRef;
		}

		object? GetCurrentLocalReference() {
			// Label references would need an IMethod annotation to build a qualified
			// name; the 3.2 converter does not annotate labels with one, so skip it.
			return null;
		}

		object? GetCurrentLocalDefinition() {
			var node = nodeStack.Peek();
			if (node is Identifier && node.Parent is CatchBlock)
				node = node.Parent;

			var parameterDef = node.Annotation<Parameter>();
			if (parameterDef is not null)
				return parameterDef;
			if (node is ParameterDeclaration) {
				node = ((ParameterDeclaration)node).Name;
				parameterDef = node.Annotation<Parameter>();
				if (parameterDef is not null)
					return parameterDef;
			}

			return null;
		}

		object? GetCurrentDefinition() {
			if (nodeStack.Count == 0)
				return null;

			var node = nodeStack.Peek();
			if (node is ParameterDeclaration)
				return null;
			if (node is VariableIdentifier)
				return ((VariableIdentifier)node).Name.Annotation<IMemberDef>();
			if (IsDefinition(node))
				return node.Annotation<IMemberRef>();

			if (node is Identifier) {
				node = node.Parent;
				if (IsDefinition(node))
					return node.Annotation<IMemberRef>();
			}

			return null;
		}

		public void WriteKeyword(string keyword) {
			var memberRef = GetCurrentMemberReference();
			var node = nodeStack.Peek();
			if (memberRef is not null && (node is PrimitiveType || node is InstanceExpression))
				output.Write(keyword, memberRef, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
			else if (memberRef is not null && node is ConstructorDeclaration && keyword == "New")
				output.Write(keyword, memberRef, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, BoxedTextColor.Keyword);
			else if (memberRef is not null && node is Accessor && (keyword == "Get" || keyword == "Set" || keyword == "AddHandler" || keyword == "RemoveHandler" || keyword == "RaiseEvent")) {
				if (canPrintAccessor)
					output.Write(keyword, memberRef, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, BoxedTextColor.Keyword);
				else
					output.Write(keyword, BoxedTextColor.Keyword);
				canPrintAccessor = !canPrintAccessor;
			}
			else if (memberRef is not null && node is OperatorDeclaration && keyword == "Operator")
				output.Write(keyword, memberRef, DecompilerReferenceFlags.Definition, BoxedTextColor.Keyword);
			else
				output.Write(keyword, BoxedTextColor.Keyword);
		}
		bool canPrintAccessor = true;

		public void WriteToken(string token, object data, object reference) {
			var memberRef = GetCurrentMemberReference();
			var node = nodeStack.Peek();

			bool addRef = memberRef is not null &&
					(node is BinaryOperatorExpression ||
					node is UnaryOperatorExpression ||
					node is AssignmentExpression);

			// Add a ref to the method if it's a delegate call
			if (!addRef && node is InvocationExpression && memberRef is IMethod) {
				var md = Resolve(memberRef as IMethod);
				if (md is not null && md.DeclaringType is not null && md.DeclaringType.IsDelegate)
					addRef = true;
			}

			if (addRef)
				output.Write(token, memberRef, DecompilerReferenceFlags.None, data);
			else if (reference is not null)
				output.Write(token, reference, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Hidden | DecompilerReferenceFlags.NoFollow, data);
			else
				output.Write(token, data);
		}

		static MethodDef? Resolve(IMethod? method) {
			if (method is MethodSpec)
				method = ((MethodSpec)method).Method;
			if (method is MemberRef)
				return ((MemberRef)method).ResolveMethod();
			else
				return (MethodDef?)method;
		}

		public void Space() => output.Write(" ", BoxedTextColor.Text);
		public void Indent() => output.IncreaseIndent();
		public void Unindent() => output.DecreaseIndent();
		public void NewLine() => output.WriteLine();

		public void WriteComment(bool isDocumentation, string content, CSharp2.CommentReference[] refs) {
			if (isDocumentation) {
				output.Write("'''", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				output.WriteLine();
			}
			else {
				output.Write("'", BoxedTextColor.Comment);
				Write(content, refs);
				output.WriteLine();
			}
		}

		void Write(string content, CSharp2.CommentReference[] refs) {
			if (refs is null) {
				output.Write(content, BoxedTextColor.Comment);
				return;
			}

			int offs = 0;
			for (int i = 0; i < refs.Length; i++) {
				var @ref = refs[i];
				var s = content.Substring(offs, @ref.Length);
				offs += @ref.Length;
				if (@ref.Reference is null)
					output.Write(s, BoxedTextColor.Comment);
				else
					output.Write(s, @ref.Reference, @ref.IsLocal ? DecompilerReferenceFlags.Local : DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			}
			System.Diagnostics.Debug.Assert(offs == content.Length);
		}

		static bool IsDefinition(AstNode node) =>
			node is FieldDeclaration ||
			node is ConstructorDeclaration ||
			node is EventDeclaration ||
			node is DelegateDeclaration ||
			node is OperatorDeclaration ||
			node is MemberDeclaration ||
			node is TypeDeclaration ||
			node is EnumDeclaration ||
			node is EnumMemberDeclaration ||
			node is TypeParameterDeclaration;

		public void DebugStart(AstNode node) { }
		public void DebugHidden(object hiddenILSpans) { }
		public void DebugExpression(AstNode node) { }
		public void DebugEnd(AstNode node) { }

		public int NextPosition => output.NextPosition;

		public void AddHighlightedKeywordReference(object reference, int start, int end) {
			if (reference is not null)
				output.AddSpanReference(reference, start, end, PredefinedSpanReferenceIds.HighlightRelatedKeywords);
		}

		public void AddBracePair(int leftStart, int leftEnd, int rightStart, int rightEnd, CodeBracesRangeFlags flags) =>
			output.AddBracePair(TextSpan.FromBounds(leftStart, leftEnd), TextSpan.FromBounds(rightStart, rightEnd), flags);

		public void AddBlock(int start, int end, CodeBracesRangeFlags flags) =>
			output.AddBracePair(new TextSpan(start, 0), new TextSpan(end, 0), flags);

		public void AddLineSeparator(int position) => output.AddLineSeparator(position);
	}
}