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
using EngineIL = ICSharpCode.Decompiler.IL;

namespace dnSpy.Decompiler.ILSpy32.VisualBasic {
	sealed class VBTextOutputFormatter32 : IOutputFormatter {
	readonly IDecompilerOutput output;
	readonly Stack<AstNode> nodeStack = new Stack<AstNode>();
	readonly Stack<(MethodDebugInfoBuilder builder, MethodDef method)> methodBuilders = new Stack<(MethodDebugInfoBuilder, MethodDef)>();
	readonly HashSet<MethodDebugInfoBuilder> withStatements = new HashSet<MethodDebugInfoBuilder>();
	readonly List<MethodDebugInfo> debugInfos = new List<MethodDebugInfo>();
	readonly int settingsVersion;
	readonly bool collectDebugInfo;
	// Live while a method builder is open: instructions already claimed by an inner
	// statement's sequence point, so outer statements don't duplicate them.
	HashSet<EngineIL.ILInstruction> mappedInstructions;

		/// <summary>
		/// Debug info produced during rendering (one whole-body span per method with a body -
		/// the same v1 shape the C# path uses). Callers drain these and pass them to
		/// output.AddDebugInfo after rendering.
		/// </summary>
		public IEnumerable<MethodDebugInfo> DebugInfos => debugInfos;

		public VBTextOutputFormatter32(IDecompilerOutput output, int settingsVersion = 1, bool collectDebugInfo = true) {
			this.output = output ?? throw new ArgumentNullException(nameof(output));
			this.settingsVersion = settingsVersion;
			this.collectDebugInfo = collectDebugInfo;
		}

		public void StartNode(AstNode node) {
			nodeStack.Push(node);
			// Method-level debug spans (v1): one SourceStatement covers the whole rendered
			// method and maps to the method's whole IL range (the 2.3 engine's degraded shape
			// for bodies it could not track statement-level). Only computed when the caller
			// asked for it (CalculateILSpans); the export and console pipelines skip it.
			if (node is MethodDeclaration method) {
				var methodDef = node.Annotation<MethodDef>();
				if (collectDebugInfo && methodDef?.Body is not null) {
					var builder = new MethodDebugInfoBuilder(settingsVersion, StateMachineKind.None,
						methodDef, null, Array.Empty<SourceLocal>(), null, null);
					builder.StartPosition = output.NextPosition;
					methodBuilders.Push((builder, methodDef));
					mappedInstructions = new HashSet<EngineIL.ILInstruction>();
				}
			}
		}

		public void EndNode(AstNode node) {
			if (node is MethodDeclaration && methodBuilders.Count > 0) {
				var (builder, method) = methodBuilders.Peek();
				if (method == node.Annotation<MethodDef>()) {
					methodBuilders.Pop();
					mappedInstructions = null;
					builder.EndPosition = output.NextPosition;
					if (!withStatements.Contains(builder)) {
						// No statement-level sequence points made it: fall back to the v1
						// whole-body span, the shape the 2.3 engine used for bodies it could
						// not track statement-level.
						var textSpan = TextSpan.FromBounds(builder.StartPosition!.Value, builder.EndPosition.Value);
						builder.Add(new SourceStatement(new ILSpan(0, (uint)method.Body!.GetCodeSize()), textSpan));
					}
					debugInfos.Add(builder.Create());
				}
			}
			if (nodeStack.Pop() != node)
				throw new InvalidOperationException();
		}

		public void WriteIdentifier(string identifier, object data, object extraData) {
			var definition = GetCurrentDefinition();
			if (definition is not null) {
				ColorPathSink?.Invoke("def");
				output.Write(IdentifierEscaper.Escape(identifier), definition, DecompilerReferenceFlags.Definition, data);
				return;
			}

			var memberRef = GetCurrentMemberReference() ?? (object?)(extraData as NamespaceReference);
			if (memberRef is not null) {
				// External type names: the 2.3 engine colors them plain Type (only types defined
				// in the decompiled module get their kind color). Namespace segments carry their
				// own color from the converter and keep it.
				if (memberRef is TypeRef { DeclaringType: null, ResolutionScope: not null } && BoxedTextColor.Text.Equals(data))
					data = BoxedTextColor.Type;
				ColorPathSink?.Invoke("ref:" + memberRef.GetType().Name);
				output.Write(IdentifierEscaper.Escape(identifier), memberRef, DecompilerReferenceFlags.None, data);
				return;
			}

			var localDef = GetCurrentLocalDefinition();
			if (localDef is not null) {
				ColorPathSink?.Invoke("local-def");
				output.Write(IdentifierEscaper.Escape(identifier), localDef, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, data);
				return;
			}

			var localRef = GetCurrentLocalReference();
			if (localRef is not null) {
				ColorPathSink?.Invoke("local-ref");
				output.Write(IdentifierEscaper.Escape(identifier), localRef, DecompilerReferenceFlags.Local, data);
				return;
			}

			ColorPathSink?.Invoke("plain:" + (data?.GetType().Name ?? "null"));
			output.Write(IdentifierEscaper.Escape(identifier), data);
		}

		/// <summary>
		/// Diagnostic: reports which formatter path wrote each identifier. The color audit tool
		/// sets this to capture a path tag per token; production runs leave it null.
		/// </summary>
		internal static Action<string>? ColorPathSink;

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

		/// <summary>One statement's debug mapping state (the 2.3 engine's DebugState shape):
		/// the nodes rendered for the statement so far, plus the text start.</summary>
		class DebugState {
			public List<AstNode> Nodes = new List<AstNode>();
			public int StartLocation;
		}
		readonly Stack<DebugState> debugStack = new Stack<DebugState>();

		/// <summary>
		/// The VB32 output visitor calls this from WriteKeyword right before the keyword is
		/// written (and for declaration nodes), so StartLocation is the statement's text start.
		/// </summary>
		public void DebugStart(AstNode node) {
			if (collectDebugInfo)
				debugStack.Push(new DebugState { StartLocation = output.NextPosition });
		}

		public void DebugHidden(object hiddenILSpans) { }

		/// <summary>Registers one rendered expression node of the current statement.</summary>
		public void DebugExpression(AstNode node) {
			if (debugStack.Count > 0)
				debugStack.Peek().Nodes.Add(node);
		}

		/// <summary>
		/// Statement-level IL<->text mapping (the 2.3 engine's DebugEnd shape): the rendered
		/// nodes' annotations - copied over from the C# tree by the converter - carry the
		/// engine's IL instructions, whose IL ranges map 1:1 onto dnSpy ILSpans. Only the
		/// nodes the visitor reported through DebugExpression are read, so a statement never
		/// claims IL from parts of its body that have not rendered yet.
		/// </summary>
		public void DebugEnd(AstNode node) {
			if (debugStack.Count == 0)
				return;
			var state = debugStack.Pop();
			if (mappedInstructions is null || methodBuilders.Count == 0)
				return;
			var spans = new List<ILSpan>();
			foreach (var rendered in state.Nodes) {
				foreach (var ann in rendered.Annotations)
					CollectILSpansFrom(ann as EngineIL.ILInstruction, spans);
			}
			if (spans.Count == 0)
				return;
			var (builder, method) = methodBuilders.Peek();
			withStatements.Add(builder);
			var textSpan = new TextSpan(state.StartLocation, output.NextPosition - state.StartLocation);
			foreach (var ilSpan in ILSpan.OrderAndCompact(spans))
				builder.Add(new SourceStatement(ilSpan, textSpan));
		}

		void CollectILSpansFrom(EngineIL.ILInstruction inst, List<ILSpan> spans) {
			if (inst is null || !mappedInstructions.Add(inst))
				return;
			// Container blocks only route control flow; the leaves carry the offsets (the
			// engine's own SequencePointBuilder skips them the same way).
			if (inst.ILRange.Start < inst.ILRange.End && !(inst is EngineIL.BlockContainer || inst is EngineIL.Block))
				spans.Add(ILSpan.FromBounds((uint)inst.ILRange.Start, (uint)inst.ILRange.End));
			// Do not descend into lambdas/local functions: their IL belongs to their own
			// (compiler-generated) method, not to this statement's mapping.
			if (inst is EngineIL.ILFunction)
				return;
			foreach (var child in inst.Children)
				CollectILSpansFrom(child, spans);
		}

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