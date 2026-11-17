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
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.CSharp.Syntax;
using CS = ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy32.CSharp {
	/// <summary>
	/// Writes the 3.2 engine's C# syntax tree into dnSpy's decompiler output with per-token
	/// colors and metadata references (click navigation), mirroring the 2.3 engine's
	/// TextTokenWriter. Text behavior mirrors the engine's TextWriterTokenWriter so the
	/// rendered code stays identical to the previous text-only rendering.
	/// </summary>
	sealed class DnSpyTokenWriter : TokenWriter, ILocatable {
		readonly IDecompilerOutput output;
		readonly ModuleDefMD module;
		readonly DecompilerTypeSystem? typeSystem;
		readonly MetadataTextColorProvider metadataTextColorProvider;
		readonly Stack<AstNode> nodeStack = new Stack<AstNode>();
		readonly Stack<(MethodDebugInfoBuilder builder, MethodDef method)> methodBuilders = new Stack<(MethodDebugInfoBuilder, MethodDef)>();
		readonly List<MethodDebugInfo> debugInfos = new List<MethodDebugInfo>();
		public readonly List<(MethodDef method, int start, int end)> MethodSpans = new List<(MethodDef, int, int)>();
		readonly List<(int line, int column, int offset)> positionMap = new List<(int, int, int)>();
		/// <summary>line -> indices into <see cref="positionMap"/>; rebuilt lazily after appends.</summary>
		Dictionary<int, List<int>>? lineIndex;
		readonly int settingsVersion;
		bool isAtStartOfLine = true;
		int line = 1;
		int column = 1;

		/// <summary>
		/// The module's corlib reference, used for primitive keyword references ('string' ->
		/// System.String, matching the 2.3 engine); null when the module has no corlib ref.
		/// </summary>
		readonly IResolutionScope? corlibAssemblyRef;

		/// <summary>
		/// Diagnostic: reports which code path colored the token being written. The color audit
		/// tool sets this to capture a path tag per token; production runs leave it null.
		/// </summary>
		internal static Action<string>? ColorPathSink;
		string lastColorPath = "";

		/// <summary>
		/// The engine's CreateSequencePoints needs line/column information on the tree nodes;
		/// the wrapping decorator copies our position into the nodes as tokens are written.
		/// </summary>
		public TextLocation Location => new TextLocation(line, column);

		/// <summary>
		/// Debug info produced during rendering (one whole-body span per method with a body -
		/// the 2.3 engine's own degraded shape for bodies it could not track statement-level).
		/// Callers drain these and pass them to output.AddDebugInfo after rendering.
		/// </summary>
		public IEnumerable<MethodDebugInfo> DebugInfos => debugInfos;

		public DnSpyTokenWriter(IDecompilerOutput output, ICSharpCode.Decompiler.CSharp.CSharpDecompiler engine,
				ModuleDefMD module, MetadataTextColorProvider metadataTextColorProvider, int settingsVersion) {
			this.output = output;
			this.module = module;
			this.metadataTextColorProvider = metadataTextColorProvider;
			this.settingsVersion = settingsVersion;
			corlibAssemblyRef = module.CorLibTypes?.AssemblyRef;
			try {
				typeSystem = (DecompilerTypeSystem?)typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler)
					.GetField("typeSystem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
					.GetValue(engine);
			}
			catch {
				typeSystem = null;
			}
		}

		/// <summary>Writes text and records its dnSpy output position for the line/column map.</summary>
		void EmitText(string text, object color) {
			ColorPathSink?.Invoke(lastColorPath);
			positionMap.Add((line, column, output.NextPosition));
			lineIndex = null;
			output.Write(text, color);
			foreach (var ch in text) {
				if (ch == '\n') {
					line++;
					column = 1;
				} else {
					column++;
				}
			}
		}

		void EmitText(string text, object reference, DecompilerReferenceFlags flags, object color) {
			ColorPathSink?.Invoke(lastColorPath);
			positionMap.Add((line, column, output.NextPosition));
			lineIndex = null;
			output.Write(text, reference, flags, color);
			foreach (var ch in text) {
				if (ch == '\n') {
					line++;
					column = 1;
				} else {
					column++;
				}
			}
		}

		/// <summary>
		/// Resolves a line/column position to the dnSpy output offset recorded when that exact
		/// position was written; falls back to the closest position on the same line.
		/// </summary>
		public int OffsetOf(int line, int column) {
			// CreateSequencePoints asks twice per sequence point, so scanning the whole map was
			// quadratic; the per-line index keeps the same result with a much smaller scan.
			if (lineIndex is null) {
				var index = new Dictionary<int, List<int>>();
				for (int i = 0; i < positionMap.Count; i++) {
					var l = positionMap[i].line;
					if (!index.TryGetValue(l, out var list))
						index[l] = list = new List<int>();
					list.Add(i);
				}
				lineIndex = index;
			}
			if (lineIndex is null || !lineIndex.TryGetValue(line, out var indices))
				return -1;
			int best = -1;
			foreach (var i in indices) {
				var (l, c, o) = positionMap[i];
				if (c == column)
					return o;
				if (c < column)
					best = o;
			}
			return best;
		}


		public override void StartNode(AstNode node) {
			nodeStack.Push(node);
			// Method-level debug spans (v1): one SourceStatement covers the whole rendered
			// method and maps to the method's whole IL range - the same shape the 2.3 engine
			// used for bodies it could not track statement-level (CreateBadMethod).
			if (node is MethodDeclaration method) {
				var methodDef = node.Annotation<MethodDef>();
				if (methodDef?.Body is not null) {
					var smInfo = Common.StateMachineDebugInfoDetector.TryDetect(methodDef);
					var bodyMethod = smInfo?.BodyMethod ?? methodDef;
					// The builder's subject is the state machine's MoveNext body (the AsyncStepInfo
					// and sequence-point IL offsets address it), while the push pairing key stays
					// the node's own annotation (the kickoff) - EndNode matches on that.
					methodBuilders.Push((new MethodDebugInfoBuilder(settingsVersion, smInfo?.Kind ?? StateMachineKind.None,
						bodyMethod, smInfo?.KickoffMethod,
						Array.Empty<SourceLocal>(), null, smInfo?.AsyncInfo), methodDef));
					// MethodSpans keeps the pairing key (the node's own annotation, matched in
					// EndNode); consumers re-run TryDetect on it to get the MoveNext body.
					MethodSpans.Add((methodDef, output.NextPosition, -1));
				}
			}
		}

		public override void EndNode(AstNode node) {
			if (node is MethodDeclaration && methodBuilders.Count > 0) {
				var (builder, method) = methodBuilders.Peek();
				if (method == node.Annotation<MethodDef>()) {
					methodBuilders.Pop();
					builder.EndPosition = output.NextPosition;
					for (var i = MethodSpans.Count - 1; i >= 0; i--) {
						if (MethodSpans[i].method == method && MethodSpans[i].end == -1) {
							MethodSpans[i] = (method, MethodSpans[i].start, output.NextPosition);
							break;
						}
					}
				}
			}
			if (nodeStack.Count > 0)
				nodeStack.Pop();
		}

		public override void WriteIdentifier(Identifier identifier) {
			var text = IdentifierEscaper.Escape(identifier.Name);
			if (identifier.IsVerbatim || CSharpOutputVisitor.IsKeyword(identifier.Name, identifier))
				text = "@" + text;

			// The identifier names the declared member: write it as a definition.
			if (identifier.Parent is EntityDeclaration entity && entity.Name == identifier.Name) {
				lastColorPath = "def-binder";
				var definition = ResolveEntity(entity);
				if (definition != null) {
					EmitText(text, definition, DecompilerReferenceFlags.Definition, metadataTextColorProvider.GetColor(definition));
					return;
				}
				lastColorPath = "def-fallback";
				EmitText(text, SyntacticFallbackColor(identifier, BoxedTextColor.Type), DecompilerReferenceFlags.Definition, BoxedTextColor.Type);
				return;
			}

			// A member or type reference: 'foo.Bar', a base type, an attribute type...
			// NOTE: only the identifier's own parent is probed - outer expressions carry their
			// own resolve results, which would miscolor plain parameter/local usages.
			var reference = ResolveReference(identifier.Parent ?? (AstNode)identifier) ?? ResolveReference(identifier);
			if (reference != null) {
				// lastColorPath was set inside ResolveReference (which sub-path resolved it)
				EmitText(text, reference, DecompilerReferenceFlags.None, metadataTextColorProvider.GetColor(reference));
				return;
			}

			// Type-name usage the engine left unannotated (attribute types, generic arguments,
			// base types): resolve by name in the module's own TypeRef table. Those references
			// carry the correct assembly scope, so navigation matches the 2.3 engine, which
			// colors external type names plain Type.
			if (IsTypeNameIdentifier(identifier)) {
				// Attribute type names drop the 'Attribute' suffix in C# output; fall back to the
				// full metadata name for those.
				var typeRef = ResolveTypeRefByName(identifier.Name, ArityOfTypeName(identifier))
					?? (IsAttributeTypeName(identifier) ? ResolveTypeRefByName(identifier.Name + "Attribute", -1) : null);
				if (typeRef is not null) {
					lastColorPath = "ref-typeref-byname";
					EmitText(text, typeRef, DecompilerReferenceFlags.None, BoxedTextColor.Type);
					return;
				}
			}

			// Namespace segments of qualified type names ('dnSpy.Bookmarks.X'): the 2.3 engine
			// emitted a NamespaceReference on every segment - including the module's own
			// namespaces, which have no TypeRef entries (their types are TypeDefs) and fall
			// back to the module's own assembly as the scope.
			if (identifier.Parent is CS.SimpleType nsSimple && nsSimple.Role == CS.MemberType.TargetRole) {
				var nsRef1 = NamespaceReferenceForSegment(nsSimple.ToString());
				if (nsRef1 is not null) {
					lastColorPath = "ref-ns-segment";
					EmitText(text, nsRef1, DecompilerReferenceFlags.None, BoxedTextColor.Namespace);
					return;
				}
			}
			if (identifier.Parent is CS.MemberType nsMemberType && nsMemberType.MemberNameToken != identifier) {
				var nsRef2 = NamespaceReferenceForSegment(nsMemberType.ToString());
				if (nsRef2 is not null) {
					lastColorPath = "ref-ns-segment";
					EmitText(text, nsRef2, DecompilerReferenceFlags.None, BoxedTextColor.Namespace);
					return;
				}
			}

			// Preserve the ref-* diagnostic tag ResolveReference may have left (which sub-path
			// failed); only plain identifiers get the generic fallback tag.
			if (!lastColorPath.StartsWith("ref-", StringComparison.Ordinal))
				lastColorPath = "fallback";
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1" && identifier.Name == "Length")
				Console.Error.WriteLine("LEN-IDENT parent=" + identifier.Parent?.GetType().Name + " anns=[" + string.Join(",", (identifier.Parent ?? (AstNode)identifier).Annotations.Select(a => a.GetType().Name)) + "]");
			var color = SyntacticColor(identifier);
			// Parameters, locals, generic parameters and using-namespace segments: the 2.3
			// engine attached references to all of these (dnlib Param, SourceLocal,
			// GenericParamDef, NamespaceReference). dnSpy treats referenced identifiers
			// differently (highlight-all-occurrences, definitions in bold), so the missing
			// references showed as display differences.
			// IL labels of unreconstructable control flow: the 2.3 engine matched a goto with
			// its target label via a shared string reference (method + label name), so dnSpy
			// highlights the pair together; both carry the Label color.
			if (identifier.Parent is CS.LabelStatement labelStatement && FindEnclosingMethod() is { } labelMethod) {
				lastColorPath = "ref-label-def";
				EmitText(text, labelMethod.ToString() + labelStatement.Label,
					DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, BoxedTextColor.Label);
				return;
			}
			if (identifier.Parent is CS.GotoStatement gotoStatement && FindEnclosingMethod() is { } gotoMethod) {
				lastColorPath = "ref-label-ref";
				EmitText(text, gotoMethod.ToString() + gotoStatement.Label,
					DecompilerReferenceFlags.Local, BoxedTextColor.Label);
				return;
			}
			if (TryGetVariableReference(identifier, color) is { } variableRef) {
				lastColorPath = "ref-variable";
				EmitText(text, variableRef.reference, variableRef.flags, color);
				return;
			}
			if (TryGetGenericParameterReference(identifier, color) is { } genericRef) {
				lastColorPath = "ref-genericparam";
				EmitText(text, genericRef, DecompilerReferenceFlags.None, color);
				return;
			}
			if (TryGetUsingNamespaceReference(identifier, color) is { } namespaceRef) {
				lastColorPath = "ref-nsusing";
				EmitText(text, namespaceRef, DecompilerReferenceFlags.None, color);
				return;
			}
			if (TryGetNamespaceDeclarationReference(identifier, color) is { } declarationNamespaceRef) {
				lastColorPath = "ref-nsdecl";
				EmitText(text, declarationNamespaceRef, DecompilerReferenceFlags.None, color);
				return;
			}
			// Member usages of same-type members: attach the definition as a navigation
			// reference, matching what the 2.3 engine emitted for these sites.
			if (identifier.Parent is MemberReferenceExpression usageMemberRef && usageMemberRef.MemberNameToken == identifier &&
					FindEnclosingTypeMember(identifier.Name) is { } memberDef) {
				lastColorPath = "ref-sametype-member";
				EmitText(text, memberDef, DecompilerReferenceFlags.None, color);
				return;
			}
			EmitText(text, color);
		}

		/// <summary>
		/// Syntactic fallback color when metadata resolution fails: class names, attribute
		/// types, base types and method names get reasonable colors from their context.
		/// </summary>
		object SyntacticFallbackColor(Identifier identifier, object defaultColor) {
			var parent = identifier.Parent;
			if (parent is null)
				return defaultColor;
			if (parent is TypeDeclaration)
				return BoxedTextColor.Type;
			if (parent is CS.Attribute)
				return BoxedTextColor.Type;
			if (parent is CS.SimpleType || parent is CS.MemberType)
				return BoxedTextColor.Type;
			if (parent is MethodDeclaration || parent is CS.ConstructorDeclaration || parent is CS.OperatorDeclaration)
				return BoxedTextColor.InstanceMethod;
			if (parent is PropertyDeclaration)
				return BoxedTextColor.InstanceProperty;
			if (parent is EventDeclaration)
				return BoxedTextColor.InstanceEvent;
			if (parent is FieldDeclaration)
				return BoxedTextColor.InstanceField;
			return defaultColor;
		}

		/// <summary>
		/// Full syntactic fallback for identifiers whose metadata resolution failed. Mirrors the
		/// 2.3 engine's colors: namespace segments, generic parameters, parameters, locals, and
		/// member usages colored from their syntactic context.
		/// </summary>
		object SyntacticColor(Identifier identifier) {
			var parent = identifier.Parent;
			if (parent is null)
				return BoxedTextColor.Text;

			// Declaration sites that carry no binder annotation.
			if (parent is TypeParameterDeclaration) {
				var genericColor = GenericParameterColorByName(identifier.Name);
				return genericColor ?? BoxedTextColor.TypeGenericParameter;
			}
			if (parent is ParameterDeclaration)
				return BoxedTextColor.Parameter;
			if (parent is VariableInitializer || parent is ForeachStatement || parent is CatchClause)
				return BoxedTextColor.Local;

			// Attribute named argument: '[AttributeUsage(AllowMultiple = true)]' - always a
			// property or field of the attribute type. The name token's parent is the
			// NamedArgumentExpression itself.
			if (parent is NamedArgumentExpression)
				return BoxedTextColor.InstanceProperty;

			// Attribute named argument: '[AttributeUsage(AllowMultiple = true)]' renders as an
			// assignment inside the attribute; the left side is always a property or field of
			// the attribute type.
			if (parent is IdentifierExpression attributeArg && attributeArg.Parent is AssignmentExpression assignment &&
					assignment.Left == attributeArg && assignment.Parent is CS.Attribute)
				return BoxedTextColor.InstanceProperty;

			// Usage sites: the engine writes these through an IdentifierExpression token.
			if (parent is IdentifierExpression usage) {
				if (usage.Parent is NamespaceDeclaration)
					return BoxedTextColor.Namespace;
				// 'value' inside a property setter accessor: the 2.3 engine shows it as a keyword.
				if (identifier.Name == "value" && IsInsideSetter(identifier))
					return BoxedTextColor.Keyword;
				if (usage.Parent is InvocationExpression invocation && invocation.Target == usage)
					return MethodColorByName(identifier.Name, false);
				// Method group passed as an argument: 'new Thread(Method)', 'UI(handler)'. A
				// variable used as an argument is indistinguishable syntactically, so parameter
				// names are checked first and win.
				if (usage.Role == Roles.Argument &&
						(usage.Parent is ObjectCreateExpression ||
						 (usage.Parent is InvocationExpression invocationArgument && invocationArgument.Target != usage)) &&
						!IsParameterName(identifier.Name) &&
						GenericParameterColorByName(identifier.Name) is null)
					return BoxedTextColor.InstanceMethod;
				var genericColor = GenericParameterColorByName(identifier.Name);
				if (genericColor != null)
					return genericColor;
				if (IsParameterName(identifier.Name))
					return BoxedTextColor.Parameter;
				return BoxedTextColor.Local;
			}

			// Qualified type name segments: only the last segment is a type name; the
			// intermediate namespace segments match the 2.3 engine's Namespace color. A
			// SimpleType in the Target role is a middle segment ('dnSpy' in dnSpy.Contracts.X);
			// one in the type-argument role is a real type ('Action' in List<Action>).
			if (parent is CS.SimpleType simple) {
				if (simple.Role == CS.MemberType.TargetRole ||
						simple.Parent is NamespaceDeclaration || simple.Parent is UsingDeclaration)
					return BoxedTextColor.Namespace;
				return TypeUsageColor(identifier.Name, simple);
			}
			if (parent is CS.MemberType memberType && memberType.MemberNameToken == identifier) {
				if (memberType.Parent is MemberType || memberType.Parent is NamespaceDeclaration || memberType.Parent is UsingDeclaration)
					return BoxedTextColor.Namespace;
				return TypeUsageColor(identifier.Name, memberType);
			}

			// Member usage: 'expr.Name', 'Console.WriteLine', 'MyEnum.Value'.
			if (parent is MemberReferenceExpression memberRef && memberRef.MemberNameToken == identifier) {
				bool invoked = memberRef.Parent is InvocationExpression inv && inv.Target == memberRef;
				// A type-name target is wrapped in a TypeReferenceExpression ('Console.WriteLine').
				bool targetIsTypeName = memberRef.Target is TypeReferenceExpression;
				if (invoked)
					return targetIsTypeName ? BoxedTextColor.StaticMethod : MethodColorByName(identifier.Name, false);
				return targetIsTypeName ? BoxedTextColor.InstanceField : BoxedTextColor.InstanceProperty;
			}

			return SyntacticFallbackColor(identifier, BoxedTextColor.Text);
		}

		/// <summary>Type-name usage color: a type parameter in scope, else Type.</summary>
		object TypeUsageColor(string name, CS.AstNode typeNode) {
			var genericColor = GenericParameterColorByName(name);
			return genericColor ?? BoxedTextColor.Type;
		}

		/// <summary>
		/// Colors a generic parameter name by scanning the enclosing declarations currently on
		/// the node stack; innermost (method) parameters win over type parameters.
		/// </summary>
		object? GenericParameterColorByName(string name) {
			if (string.IsNullOrEmpty(name))
				return null;
			foreach (var node in nodeStack) {
				if (node is MethodDeclaration md && md.TypeParameters.Any(tp => tp.Name == name))
					return BoxedTextColor.MethodGenericParameter;
				if (node is CS.TypeDeclaration td && td.TypeParameters.Any(tp => tp.Name == name))
					return BoxedTextColor.TypeGenericParameter;
			}
			return null;
		}

		/// <summary>
		/// Instance/static color for a method usage: a same-named method of the enclosing type
		/// (the binder annotates it) settles it; else fall back to the type-name heuristic.
		/// </summary>
		/// <summary>
		/// The enclosing type's member with this name (method, property, field or event) -
		/// used to attach references for member usages whose engine resolve results are
		/// missing, matching the references the 2.3 engine emitted.
		/// </summary>
		/// <summary>The enclosing type as the binder annotated it (TypeDeclaration nodes).</summary>
		TypeDef? FindEnclosingType() {
			foreach (var node in nodeStack) {
				if (node is CS.TypeDeclaration && node.Annotation<TypeDef>() is { } typeDef)
					return typeDef;
			}
			return null;
		}

		/// <summary>
		/// The enclosing method as the binder annotated it (MethodDeclaration, accessor,
		/// constructor, operator all carry MethodDef annotations).
		/// </summary>
		MethodDef? FindEnclosingMethod() {
			foreach (var node in nodeStack) {
				if (node.Annotation<MethodDef>() is { } methodDef)
					return methodDef;
			}
			return null;
		}

		/// <summary>
		/// Reference for a parameter or local identifier: the 2.3 engine emitted the dnlib
		/// <see cref="Param"/> for parameters (all occurrences, declaration and usages) and a
		/// cached <see cref="SourceLocal"/> for locals, so dnSpy highlights every occurrence
		/// and shows definitions in bold. Parameters map by IL slot (the engine's
		/// ILVariable.Index matches dnlib MethodDef.Parameters including the implicit this);
		/// locals map by IL slot into the method body's variable table.
		/// </summary>
		/// <summary>
		/// The dnlib Parameter of the compiled lambda method for a lambda's parameter
		// declaration - via the engine's ILVariableResolveResult (MakeParameters), then the
		// LambdaExpression's ILFunction for lambdas in field initializers. dnlib Parameters
		// include the hidden this; the engine's index counts only the explicit ones.
		/// </summary>
		dnlib.DotNet.Parameter? ResolveLambdaParameterReference(CS.ParameterDeclaration parameterDeclaration) {
			var ilv = parameterDeclaration.Annotation<ILVariable>()
				?? (parameterDeclaration.Annotation<ResolveResult>() as ICSharpCode.Decompiler.CSharp.ILVariableResolveResult)?.Variable;
			if (ilv is { Kind: VariableKind.Parameter, Index: >= 0 } && ilv.Function?.CecilMethod is { } cecilLambda &&
					module.ResolveToken(new MDToken(cecilLambda.MetadataToken.ToInt32())) is MethodDef lambdaDef) {
				var paramIndex = ilv.Index + (lambdaDef.HasThis ? 1 : 0);
				if (paramIndex < lambdaDef.Parameters.Count)
					return lambdaDef.Parameters[paramIndex];
			}
			if (parameterDeclaration.Parent is CS.LambdaExpression lambdaExpr &&
					lambdaExpr.Annotation<ILFunction>() is { } lambdaFn && lambdaFn.CecilMethod is { } cecilLambda2 &&
					module.ResolveToken(new MDToken(cecilLambda2.MetadataToken.ToInt32())) is MethodDef lambdaDef2) {
				var position = 0;
				foreach (var pd in lambdaExpr.Parameters) {
					if (pd == parameterDeclaration)
						break;
					position++;
				}
				position += lambdaDef2.HasThis ? 1 : 0;
				if (position < lambdaDef2.Parameters.Count)
					return lambdaDef2.Parameters[position];
			}
			return null;
		}

		(object reference, DecompilerReferenceFlags flags)? TryGetVariableReference(Identifier identifier, object color) {
			var parent = identifier.Parent;
			if (parent is null)
				return null;

			ILVariable? variable = null;
			var isDefinition = false;
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1" && identifier.Name == "line2") {
				var v2 = parent is IdentifierExpression ie2 ? ie2.GetILVariable() : (parent is VariableInitializer vi2 ? vi2.GetILVariable() : null);
				if (v2 is null)
					Console.Error.WriteLine("LINE2 NO-VAR parent=" + parent?.GetType().Name);
				else
					Console.Error.WriteLine("LINE2 var kind=" + v2.Kind + " idx=" + v2.Index + " bodyVars=" + (FindEnclosingMethod()?.Body?.Variables.Count ?? -1) + " fnKind=" + v2.Function?.Method?.Name);
			}
			if (parent is ParameterDeclaration parameterDeclaration) {
				// Lambda parameters: no enclosing method match - they belong to the compiled
				// lambda method (<M>b__N), the same reference the 2.3 engine attached.
				if (FindEnclosingMethod() is null) {
					var lambdaParam = ResolveLambdaParameterReference(parameterDeclaration);
					if (lambdaParam is not null) {
						lastColorPath = "ref-lambdaparam";
						return (lambdaParam, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition);
					}
					return null;
				}
				// Declaration name: resolve the enclosing method's parameter by name.
				var methodDef = FindEnclosingMethod();
				var parameter = methodDef.Parameters.FirstOrDefault(p => p.Name == parameterDeclaration.Name);
				if (parameter is null && parameterDeclaration.Parent is MethodDeclaration md2) {
					// Parameter names can be missing or synthesized; fall back to position
					// (instance methods carry the implicit this at index 0 in dnlib).
					var position = 0;
					foreach (var pd in md2.Parameters) {
						if (pd == parameterDeclaration)
							break;
						position++;
					}
					if (position < md2.Parameters.Count)
						parameter = methodDef.Parameters.Skip(1).Skip(position).FirstOrDefault();
				}
				if (parameter is null) {
					var lambdaParam = ResolveLambdaParameterReference(parameterDeclaration);
					if (lambdaParam is not null) {
						lastColorPath = "ref-lambdaparam";
						return (lambdaParam, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition);
					}
					return null;
				}
				lastColorPath = "ref-paramdecl";
				return (parameter, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition);
			}
			if (parent is VariableInitializer initializer) {
				variable = initializer.GetILVariable();
				isDefinition = true;
			}
			else if (parent is ForeachStatement foreachStatement) {
				variable = foreachStatement.GetILVariable();
				isDefinition = true;
			}
			else if (parent is IdentifierExpression usage) {
				variable = usage.GetILVariable();
			}

			if (variable is null)
				return null;
			var method = FindEnclosingMethod();
			if (method is null)
				return null;
			if (variable.Kind == VariableKind.Parameter) {
				// The engine's parameter index includes the implicit this, matching
				// dnlib MethodDef.Parameters.
				if (variable.Index < 0 || variable.Index >= method.Parameters.Count)
					return null;
				lastColorPath = "ref-param";
				return (method.Parameters[variable.Index], isDefinition ? DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition : DecompilerReferenceFlags.Local);
			}
			// Captured/hoisted variables (Local without a slot, or lambda-body StackSlot
			// references to hoisted fields) have no IL slot in this method. Both shapes render
			// with the original variable name; the 2.3 engine still emitted a SourceLocal (with
			// the hoisted field set), so synthesize one cached per (method, name) - dnSpy
			// highlights locals by reference identity.
			var flags = isDefinition ? DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition : DecompilerReferenceFlags.Local;
			var hoistedKey = (method, variable.Name ?? string.Empty);
			if (variable.Kind is VariableKind.Local or VariableKind.UsingLocal or VariableKind.ForeachLocal) {
				if (method.Body is not null && variable.Index >= 0 && variable.Index < method.Body.Variables.Count) {
					var dnlibLocal = method.Body.Variables[variable.Index];
					var key = (method, variable.Index);
					if (!sourceLocalCache.TryGetValue(key, out var sourceLocal)) {
						sourceLocal = new SourceLocal(dnlibLocal, variable.Name ?? dnlibLocal.Name ?? string.Empty,
							dnlibLocal.Type, SourceVariableFlags.None);
						sourceLocalCache[key] = sourceLocal;
					}
					lastColorPath = "ref-local";
					return (sourceLocal, flags);
				}
			}
			if (hoistedNameCache.TryGetValue(hoistedKey, out var hoistedLocal))
				return (hoistedLocal, flags);
			var synthesizedLocal = new SourceLocal(null, variable.Name ?? string.Empty,
				module.CorLibTypes.Object, SourceVariableFlags.None);
			hoistedNameCache[hoistedKey] = synthesizedLocal;
			lastColorPath = "ref-local-hoisted";
			return (synthesizedLocal, flags);
		}

		readonly Dictionary<(MethodDef, int), SourceLocal> sourceLocalCache = new Dictionary<(MethodDef, int), SourceLocal>();
		readonly Dictionary<(MethodDef, string), SourceLocal> hoistedNameCache = new Dictionary<(MethodDef, string), SourceLocal>();

		/// <summary>
		/// Reference for a generic parameter identifier (declaration or usage): the 2.3 engine
		/// emitted the dnlib GenericParamDef for both type and method type parameters.
		/// </summary>
		IMemberDef? TryGetGenericParameterReference(Identifier identifier, object color) {
			var parent = identifier.Parent;
			// Scan the node stack outward: innermost (method) parameters win over type
			// parameters, mirroring GenericParameterColorByName's color precedence.
			foreach (var node in nodeStack) {
				if (node is MethodDeclaration methodDeclaration && methodDeclaration.Annotation<MethodDef>() is { } methodDef) {
					var gp = methodDef.GenericParameters.FirstOrDefault(g => g.Name == identifier.Name)
						?? (methodDeclaration.TypeParameters.Any(tp => tp.Name == identifier.Name) ? methodDef.GenericParameters.FirstOrDefault() : null);
					if (gp is not null)
						return gp;
				}
				if (node is CS.TypeDeclaration typeDeclaration && typeDeclaration.Annotation<TypeDef>() is { } typeDef) {
					var gp = typeDef.GenericParameters.FirstOrDefault(g => g.Name == identifier.Name)
						?? (typeDeclaration.TypeParameters.Any(tp => tp.Name == identifier.Name) ? typeDef.GenericParameters.FirstOrDefault() : null);
					if (gp is not null)
						return gp;
				}
			}
			return null;
		}

		/// <summary>
		/// Namespace reference for the segments of a using declaration: the 2.3 engine
		/// emitted NamespaceReference(assembly, segment) on every segment of the import.
		/// The assembly is resolved by finding any TypeRef of that namespace in the module's
		/// reference table (its scope is the assembly the namespace is imported from).
		/// </summary>
		/// <summary>
		/// NamespaceReference for a dotted namespace prefix: the assembly scope comes from any
		/// TypeRef of that namespace in the module's reference table (any deeper namespace of
		/// the same root resolves to the same owning assembly); the module's own namespaces
		// have no TypeRef entries and use the module's own assembly.
		/// </summary>
		object? NamespaceReferenceForSegment(string fullNamespace) {
			if (string.IsNullOrEmpty(fullNamespace))
				return null;
			var typeRef = module.GetTypeRefs().FirstOrDefault(tr => tr.Namespace == fullNamespace)
				?? module.GetTypeRefs().FirstOrDefault(tr => tr.Namespace.StartsWith(fullNamespace + ".", StringComparison.Ordinal));
			if (typeRef?.ResolutionScope is dnlib.DotNet.IAssembly scope)
				return new NamespaceReference(scope, fullNamespace);
			if (module.Types.Any(t => t.Namespace == fullNamespace || t.Namespace.StartsWith(fullNamespace + ".", StringComparison.Ordinal)))
				return new NamespaceReference(module.Assembly, fullNamespace);
			return null;
		}

		object? TryGetUsingNamespaceReference(Identifier identifier, object color) {
			if (identifier.Parent is not SimpleType && identifier.Parent is not MemberType)
				return null;
			// The full namespace of the import: walk the MemberType chain.
			// Reconstruct the full namespace of the import by walking the MemberType chain:
			// 'using System.Runtime.InteropServices;' - the identifier 'Interoperability...'
			// segment chain builds 'System.Runtime.InteropServices'.
			string BuildNamespace(AstType type) {
				switch (type) {
				case SimpleType simple:
					return simple.Identifier;
				case MemberType member:
					var prefix = BuildNamespace(member.Target);
					return prefix is null ? null : prefix + "." + member.MemberName;
				default:
					return null;
				}
			}
			string? fullNamespace = null;
			for (var node = identifier.Parent as AstNode; node is not null; node = node.Parent) {
				if (node is UsingDeclaration usingDeclaration) {
					fullNamespace = BuildNamespace(usingDeclaration.Import);
					break;
				}
			}
			if (fullNamespace is null)
				return null;
			var typeRef = module.GetTypeRefs().FirstOrDefault(tr => tr.Namespace == fullNamespace);
			var assembly = typeRef?.ResolutionScope as AssemblyRef ?? module.CorLibTypes.AssemblyRef;
			return new NamespaceReference(assembly, identifier.Name);
		}

		/// <summary>
		/// Namespace reference for the segments of a namespace declaration
		/// ('namespace SpTestNamespace;'): the 2.3 engine emitted NamespaceReference on
		/// every declaration segment too, with the assembly declaring the namespace.
		/// </summary>
		object? TryGetNamespaceDeclarationReference(Identifier identifier, object color) {
			if (identifier.Parent is not NamespaceDeclaration && !(identifier.Parent is MemberType && identifier.Parent.Parent is NamespaceDeclaration) && !(identifier.Parent is SimpleType && identifier.Parent.Parent is NamespaceDeclaration))
				return null;
			// Reconstruct the full declared namespace.
			string BuildNamespace(AstNode node) {
				switch (node) {
				case NamespaceDeclaration nsDecl:
					return string.Join(".", nsDecl.Identifiers);
				case MemberType member:
					return member.MemberName;
				default:
					return null;
				}
			}
			for (var node = identifier.Parent as AstNode; node is not null; node = node.Parent) {
				if (node is NamespaceDeclaration) {
					var fullNamespace = BuildNamespace(node);
					if (fullNamespace is null)
						return null;
					var typeRef = module.GetTypeRefs().FirstOrDefault(tr => tr.Namespace == fullNamespace)
						?? module.GetTypeRefs().FirstOrDefault(tr => fullNamespace.StartsWith(tr.Namespace + ".", StringComparison.Ordinal) || tr.Namespace == string.Empty);
					var assembly = (typeRef?.ResolutionScope as AssemblyRef) ?? module.CorLibTypes.AssemblyRef;
					return new NamespaceReference(assembly, identifier.Name);
				}
			}
			return null;
		}

		Dictionary<TypeDef, Dictionary<string, IMemberDef?>>? memberLookupCache;

		/// <summary>
		/// Per-type member lookup cache: large types (100+ members) render hundreds of tokens
		/// that each resolve through this, and a linear scan per token turns the whole render
		/// into O(tokens x members) - the profiled hot spot (WpfHexViewImpl: 9.8x the 2.3 time).
		/// </summary>
		IMemberDef? FindEnclosingTypeMember(string name) {
			foreach (var node in nodeStack) {
				if (node is CS.TypeDeclaration typeDecl && typeDecl.Annotation<TypeDef>() is { } typeDef) {
					if (memberLookupCache is null)
						memberLookupCache = new Dictionary<TypeDef, Dictionary<string, IMemberDef?>>();
					if (!memberLookupCache.TryGetValue(typeDef, out var nameMap))
						memberLookupCache[typeDef] = nameMap = new Dictionary<string, IMemberDef?>(StringComparer.Ordinal);
					if (nameMap.TryGetValue(name, out var cached))
						return cached;
					IMemberDef? found = typeDef.Methods.FirstOrDefault(m => m.Name == name);
					if (found is null)
						found = typeDef.Properties.FirstOrDefault(p => p.Name == name);
					if (found is null)
						found = typeDef.Events.FirstOrDefault(ev => ev.Name == name);
					if (found is null) {
						foreach (var field in typeDef.Fields) {
							if (field.Name == name) {
								found = field;
								break;
							}
						}
					}
					nameMap[name] = found;
					return found;
				}
			}
			return null;
		}

		object MethodColorByName(string name, bool targetIsTypeName) {
			foreach (var node in nodeStack) {
				if (node is CS.TypeDeclaration typeDecl && typeDecl.Annotation<TypeDef>() is { } typeDef) {
					var methods = typeDef.Methods.Where(m => m.Name == name).ToList();
					if (methods.Count > 0) {
						if (methods.All(m => m.IsStatic))
							return BoxedTextColor.StaticMethod;
						if (methods.All(m => !m.IsStatic))
							return BoxedTextColor.InstanceMethod;
					}
					break;
				}
			}
			return targetIsTypeName ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
		}

		/// <summary>True when the node sits inside a property or indexer setter accessor.</summary>
		static bool IsInsideSetter(AstNode node) {
			for (var n = node; n is not null; n = n.Parent) {
				if (n.Parent is CS.PropertyDeclaration property && property.Setter == n)
					return true;
				if (n.Parent is CS.IndexerDeclaration indexer && indexer.Setter == n)
					return true;
			}
			return false;
		}

		/// <summary>True when 'name' is a parameter of an enclosing method or lambda.</summary>
		bool IsParameterName(string name) {
			foreach (var node in nodeStack) {
				IEnumerable<ParameterDeclaration>? parameters = node switch {
					MethodDeclaration md => md.Parameters,
					CS.ConstructorDeclaration cd => cd.Parameters,
					CS.OperatorDeclaration od => od.Parameters,
					LambdaExpression lambda => lambda.Parameters,
					_ => null,
				};
				if (parameters != null && parameters.Any(p => p.Name == name))
					return true;
			}
			return false;
		}

		public override void WriteKeyword(Role role, string keyword) {
			// The 'this' keyword carries the declaring type reference, and accessor keywords
			// ('add'/'remove'/'get'/'set') carry their accessor method - both mirror the 2.3
			// engine's WriteKeyword on ThisReferenceExpression/Accessor nodes.
			if (nodeStack.Count > 0 && (keyword == "this" || keyword == "base") && nodeStack.Peek() is ThisReferenceExpression) {
				var declaringType = FindEnclosingType();
				if (declaringType is not null) {
					lastColorPath = "keyword-this";
					EmitText(keyword, declaringType, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
					isAtStartOfLine = false;
					return;
				}
			}
			if (nodeStack.Count > 0 && (keyword == "add" || keyword == "remove" || keyword == "get" || keyword == "set") && nodeStack.Peek() is Accessor) {
				var accessorMethod = FindEnclosingMethod();
				if (accessorMethod is not null) {
					lastColorPath = "keyword-accessor";
					EmitText(keyword, accessorMethod, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
					isAtStartOfLine = false;
					return;
				}
			}
			// 'operator' on OperatorDeclaration nodes carries the operator method.
			if (keyword == "operator" && nodeStack.Count > 0 && nodeStack.Peek() is CS.OperatorDeclaration operatorDeclaration &&
					operatorDeclaration.Annotation<MethodDef>() is { } operatorMethod) {
				lastColorPath = "keyword-operator";
				EmitText(keyword, operatorMethod, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
				isAtStartOfLine = false;
				return;
			}
			// 'base' on BaseReferenceExpression nodes carries the base type reference; on
			// ConstructorInitializer nodes (': base(...)') it carries the base ctor - the
			// 2.3 engine attached refs to both.
			if (keyword == "base" && nodeStack.Count > 0) {
				if (nodeStack.Peek() is BaseReferenceExpression) {
					var baseType = FindEnclosingType()?.BaseType;
					if (baseType is not null) {
						lastColorPath = "keyword-base";
						EmitText(keyword, baseType, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
						isAtStartOfLine = false;
						return;
					}
				}
				else if (nodeStack.Peek() is ConstructorInitializer) {
					var baseCtor = ResolveReference(nodeStack.Peek());
					if (baseCtor is not null) {
						lastColorPath = "keyword-base-ctor";
						EmitText(keyword, baseCtor, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
						isAtStartOfLine = false;
						return;
					}
				}
			}
			// The 'delegate' keyword of anonymous methods carries the compiler-generated
			// method the body was reconstructed from (the node's ILFunction annotation);
			// the 2.3 engine attached the same MethodDef so dnSpy navigates to the hidden
			// method.
			if (keyword == "delegate" && nodeStack.Count > 0 && nodeStack.Peek() is CS.AnonymousMethodExpression anonMethod &&
					anonMethod.Annotation<ILFunction>() is { } anonFunction && anonFunction.CecilMethod is not null) {
				var anonRef = ToDnlib(anonFunction.CecilMethod);
				if (anonRef is not null) {
					lastColorPath = "keyword-delegate";
					EmitText(keyword, anonRef, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
					isAtStartOfLine = false;
					return;
				}
			}
			// The 'new' keyword carries the constructor reference, mirroring the 2.3 engine's
			// WriteKeyword on ObjectCreateExpression nodes.
			if (keyword == "new" && nodeStack.Count > 0 && nodeStack.Peek() is ObjectCreateExpression) {
				var ctorRef = ResolveReference(nodeStack.Peek()) ?? FindMemberRefForTypeSystemMemberFromCtor(nodeStack.Peek());
				if (ctorRef is not null) {
					lastColorPath = "keyword-new";
					EmitText(keyword, ctorRef, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
					isAtStartOfLine = false;
					return;
				}
			}
			// Primitive type keywords carry a corlib reference like the 2.3 engine, so
			// 'string'/'int'/'void' navigate to their System.* definitions.
			if (primitiveTypeFullNames.TryGetValue(keyword, out var fullName) && corlibAssemblyRef is not null) {
				lastColorPath = "keyword-primitive";
				EmitText(keyword, GetPrimitiveTypeRef(fullName), DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
				isAtStartOfLine = false;
				return;
			}
			lastColorPath = "keyword";
			EmitText(keyword, BoxedTextColor.Keyword);
			isAtStartOfLine = false;
		}

		static readonly Dictionary<string, string> primitiveTypeFullNames = new Dictionary<string, string> {
			["string"] = "System.String",
			["object"] = "System.Object",
			["bool"] = "System.Boolean",
			["byte"] = "System.Byte",
			["sbyte"] = "System.SByte",
			["short"] = "System.Int16",
			["ushort"] = "System.UInt16",
			["int"] = "System.Int32",
			["uint"] = "System.UInt32",
			["long"] = "System.Int64",
			["ulong"] = "System.UInt64",
			["float"] = "System.Single",
			["double"] = "System.Double",
			["decimal"] = "System.Decimal",
			["char"] = "System.Char",
			["void"] = "System.Void",
		};
		readonly Dictionary<string, TypeRefUser> primitiveTypeRefs = new Dictionary<string, TypeRefUser>();

		TypeRefUser GetPrimitiveTypeRef(string fullName) {
			if (!primitiveTypeRefs.TryGetValue(fullName, out var typeRef)) {
				var slash = fullName.LastIndexOf('.');
				typeRef = new TypeRefUser(module, fullName.Substring(0, slash), fullName.Substring(slash + 1), corlibAssemblyRef!);
				primitiveTypeRefs[fullName] = typeRef;
			}
			return typeRef;
		}

		public override void WriteToken(Role role, string token) {
			lastColorPath = "token";
			// The 2.3 engine attaches the member reference to operator/punctuation tokens of
			// binary/unary/assignment/indexer expressions and delegate invokes, so clicking
			// '+' navigates to op_Addition and the indexer brackets to the accessor. Replicate
			// that using the current node's resolve results.
			var tokenRef = GetCurrentTokenReference();
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1" && tokenRef is dnlib.DotNet.TypeRefUser)
				Console.Error.WriteLine("TOK-TYPEREF token=" + token + " node=" + (nodeStack.Count > 0 ? nodeStack.Peek().GetType().Name : "?") + " anns=[" + string.Join(",", (nodeStack.Count > 0 ? nodeStack.Peek() : (AstNode?)null)?.Annotations.Select(a => a.GetType().Name) ?? Array.Empty<string>()) + "]");
			if (tokenRef is dnlib.DotNet.IMemberRef && tokenRef is not dnlib.DotNet.TypeRefUser) {
				lastColorPath = "token-ref";
				EmitText(token, tokenRef, DecompilerReferenceFlags.None, ColorOfToken(token, role));
				isAtStartOfLine = false;
				return;
			}
			EmitText(token, ColorOfToken(token, role));
			isAtStartOfLine = false;
		}

		/// <summary>
		/// Member reference for the current punctuation/operator token, mirroring the 2.3
		/// TextTokenWriter.WriteToken: expressions that map to a member (operators bind to
		/// op_Addition etc., indexer brackets to the accessor, delegate invokes to Invoke).
		/// </summary>
		object? GetCurrentTokenReference() {
			if (nodeStack.Count == 0)
				return null;
			var node = nodeStack.Peek();
			var isOperatorLike = node is BinaryOperatorExpression || node is UnaryOperatorExpression ||
				node is AssignmentExpression || node is IndexerExpression;
			var isDelegateInvoke = node is InvocationExpression &&
				node.Annotation<ResolveResult>() is InvocationResolveResult irr && irr.Member is ICSharpCode.Decompiler.TypeSystem.IMethod m &&
				m.DeclaringType?.Kind == TypeKind.Delegate;
			if (!isOperatorLike && !isDelegateInvoke)
				return null;
			// User-defined operators carry their operator method (op_Addition etc.); indexer
			// brackets and delegate invokes carry a MemberResolveResult, which ResolveReference
			// handles. String concatenation binds to a synthetic operator with no metadata
			// method - the 2.3 engine showed String.Concat here (its ReplaceMethodCallsWithOperators
			// folds Concat into '+' and copies the invocation annotation onto the folded node),
			// so synthesize the same reference from the corlib TypeRef table.
			var orr = node.Annotation<OperatorResolveResult>();
			if (orr is not null) {
				if (orr.UserDefinedOperatorMethod is not null)
					return ToDnlib(TryGetCecil(orr.UserDefinedOperatorMethod));
				if (orr.OperatorType == System.Linq.Expressions.ExpressionType.Add && orr.Operands.Any(o => o.Type?.FullName == "System.String"))
					return SynthesizeStringConcatRef();
				return null;
			}
			// NOT the general ResolveReference: assignment nodes can carry a TypeResolveResult
			// (field store through an object), which would wrongly put a type reference on the
			// operator token. Only member resolve results are valid here.
			if (node.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>() is { } tmrr && tmrr.Member is not null)
				if (FindMemberRefForTypeSystemMember(tmrr.Member, node) is { } found)
					return found;
			// External accessors (e.g. List<T>.get_Item behind an indexer expression) can fail
			// GetCecil; the module's own MemberRef table has the authoritative entry.
			if (node.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>() is { } mrr &&
					mrr.Member is ICSharpCode.Decompiler.TypeSystem.IMember tm) {
				// The module's MemberRef table keys on the declaring type as the IL spelled it:
				// generic instantiations carry their arguments (List`1<System.String>) and the
				// member name is the accessor's (get_Item), not the property's (Item).
				var declaringNames = new List<string>();
				if (mrr.TargetResult?.Type is not null)
					declaringNames.Add(mrr.TargetResult.Type.FullName);
				if (tm.DeclaringType is not null)
					declaringNames.Add(tm.DeclaringType.FullName);
				var memberNames = new List<string> { tm.Name };
				if (tm is ICSharpCode.Decompiler.TypeSystem.IProperty property) {
					if (property.Getter is not null)
						memberNames.Add(property.Getter.Name);
					if (property.Setter is not null)
						memberNames.Add(property.Setter.Name);
				}
				foreach (var declaringName in declaringNames)
					foreach (var memberName in memberNames)
						if (Common.DnlibReferenceMapper.FindMemberRefInTable(module, declaringName, memberName) is { } found)
							return found;
			}
			return null;
		}

		object? SynthesizeStringConcatRef() {
			var stringTypeRef = Common.DnlibReferenceMapper.ResolveTypeRefByName(module, "String", 0);
			if (stringTypeRef is null)
				return null;
			return new MemberRefUser(module, "Concat",
				MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.Object),
				stringTypeRef);
		}

		/// <summary>
		/// Token colors matching the 2.3 engine: member-access dots and the other operator
		/// tokens are Operator, while brackets and the generic angle brackets and colon are
		/// Punctuation. Comparison operators use their own token roles, so they stay Operator
		/// like in the 2.3 engine.
		/// </summary>
		static object ColorOfToken(string token, Role role) {
			switch (token) {
			case ".":
				return BoxedTextColor.Operator;
			case "<" when role == Roles.LChevron:
			case ">" when role == Roles.RChevron:
			case ":":
				return BoxedTextColor.Punctuation;
			}
			return IsPunctuation(token) ? BoxedTextColor.Punctuation : BoxedTextColor.Operator;
		}

		static bool IsPunctuation(string token) {
			foreach (var c in token) {
				if (c != '(' && c != ')' && c != '[' && c != ']' && c != '{' && c != '}' && c != ',' && c != ';')
					return false;
			}
			return token.Length > 0;
		}

		public override void Space() => EmitText(" ", BoxedTextColor.Text);

		public override void Indent() => output.IncreaseIndent();

		public override void Unindent() => output.DecreaseIndent();

		public override void NewLine() {
			lastColorPath = "";
			output.WriteLine();
			line++;
			column = 1;
			isAtStartOfLine = true;
		}

		public override void WriteComment(CommentType commentType, string content) {
			switch (commentType) {
			case CommentType.SingleLine:
				EmitText("//", BoxedTextColor.Comment);
				EmitText(content, BoxedTextColor.Comment);
				NewLine();
				break;
			case CommentType.Documentation:
				output.Write("///", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				NewLine();
				break;
			case CommentType.MultiLine:
				EmitText("/*", BoxedTextColor.Comment);
				EmitText(content, BoxedTextColor.Comment);
				EmitText("*/", BoxedTextColor.Comment);
				isAtStartOfLine = false;
				break;
			case CommentType.MultiLineDocumentation:
				output.Write("/**", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				output.Write("*/", BoxedTextColor.XmlDocCommentDelimiter);
				isAtStartOfLine = false;
				break;
			default:
				EmitText(content, BoxedTextColor.Comment);
				isAtStartOfLine = false;
				break;
			}
		}

		public override void WritePreProcessorDirective(PreProcessorDirectiveType type, string argument) {
			if (!isAtStartOfLine)
				NewLine();
			var directive = type.ToString().ToLowerInvariant();
			EmitText("#" + directive, BoxedTextColor.Keyword);
			if (!string.IsNullOrEmpty(argument))
				EmitText(" " + argument, BoxedTextColor.Text);
			NewLine();
		}

		public override void WritePrimitiveValue(object value, string literalValue = null) {
			if (literalValue != null) {
				EmitText(literalValue, BoxedTextColor.Number);
				return;
			}
			// Numeric literals carry their primitive type reference (Hidden|Local|NoFollow),
			// matching the 2.3 engine's MakePrimitive annotations - hovering a number shows
			// its type. Strings/chars/bools never had references in 2.3.
			if (nodeStack.Count > 0 && nodeStack.Peek().Annotation<ResolveResult>() is { } primitiveRR &&
					primitiveRR.Type?.FullName is string primitiveFullName && value is IFormattable &&
					value is not string && value is not char && !(value is bool) &&
					GetPrimitiveTypeRef(primitiveFullName) is { } primitiveTypeRef) {
				lastColorPath = "primitive-ref";
				EmitText(FormatPrimitiveLiteral(value), primitiveTypeRef,
					DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Hidden | DecompilerReferenceFlags.NoFollow,
					BoxedTextColor.Number);
				return;
			}

			if (value is null) {
				EmitText("null", BoxedTextColor.Keyword);
				return;
			}

			if (value is bool b) {
				EmitText(b ? "true" : "false", BoxedTextColor.Keyword);
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
				var specialFloatName = GetSpecialFloatConstantName(f);
				if (specialFloatName != null) {
					EmitText("float", BoxedTextColor.Keyword);
					EmitText(".", BoxedTextColor.Punctuation);
					output.Write(specialFloatName, BoxedTextColor.Keyword);
					return;
				}
				if (f == 0 && 1 / f == float.NegativeInfinity)
					EmitText("-", BoxedTextColor.Operator);
				output.Write(f.ToString("R", NumberFormatInfo.InvariantInfo) + "f", BoxedTextColor.Number);
				return;
			}

			if (value is double dbl) {
				var specialDoubleName = GetSpecialDoubleConstantName(dbl);
				if (specialDoubleName != null) {
					EmitText("double", BoxedTextColor.Keyword);
					EmitText(".", BoxedTextColor.Punctuation);
					output.Write(specialDoubleName, BoxedTextColor.Keyword);
					return;
				}
				if (dbl == 0 && 1 / dbl == double.NegativeInfinity)
					EmitText("-", BoxedTextColor.Operator);
				var number = dbl.ToString("R", NumberFormatInfo.InvariantInfo);
				if (number.IndexOf('.') < 0 && number.IndexOf('E') < 0)
					number += ".0";
				EmitText(number, BoxedTextColor.Number);
				return;
			}

			if (value is IFormattable formattable) {
				var sb = new StringBuilder();
				sb.Append(formattable.ToString(null, NumberFormatInfo.InvariantInfo));
				if (value is uint || value is ulong)
					sb.Append('u');
				if (value is long || value is ulong)
					sb.Append('L');
				EmitText(sb.ToString(), BoxedTextColor.Number);
				return;
			}

			EmitText(value.ToString(), BoxedTextColor.Number);
		}

		/// <summary>
		/// Constructor reference for the 'new' keyword: the ObjectCreateExpression carries a
		/// CSharpInvocationResolveResult whose Member is the ctor; external ctors fall back to
		/// the module's MemberRef table (constructor name is always .ctor).
		/// </summary>
		object? FindMemberRefForTypeSystemMemberFromCtor(AstNode node) {
			var rr = node.Annotation<ResolveResult>();
			ICSharpCode.Decompiler.TypeSystem.IMethod? ctor = rr switch {
				ICSharpCode.Decompiler.CSharp.Resolver.CSharpInvocationResolveResult csInv => (ICSharpCode.Decompiler.TypeSystem.IMethod?)csInv.Member,
				InvocationResolveResult inv => (ICSharpCode.Decompiler.TypeSystem.IMethod?)inv.Member,
				_ => null,
			};
			if (ctor is null)
				return null;
			var resolved = ToDnlib(TryGetCecil(ctor));
			if (resolved is not null)
				return resolved;
			if (ctor.DeclaringType is not null)
				return Common.DnlibReferenceMapper.FindMemberRefInTable(module, ctor.DeclaringType.FullName, ".ctor")
					?? Common.DnlibReferenceMapper.FindMemberRefInTable(module, ctor.DeclaringType.FullName, ctor.Name);
			return null;
		}

		/// <summary>
		/// dnSpy 2.3 rewrites ldc.r4/r8 holding these values into ldsfld float/double::&lt;name&gt;
		/// (ILAstOptimizer.cs:595-649), so they print as 'float.MaxValue' instead of the numeric
		/// literal. Returns null for ordinary values.
		/// </summary>
		static string GetSpecialFloatConstantName(float f)
		{
			if (float.IsNaN(f))
				return "NaN";
			if (float.IsPositiveInfinity(f))
				return "PositiveInfinity";
			if (float.IsNegativeInfinity(f))
				return "NegativeInfinity";
			if (f == float.Epsilon)
				return "Epsilon";
			if (f == float.MinValue)
				return "MinValue";
			if (f == float.MaxValue)
				return "MaxValue";
			return null;
		}

		static string GetSpecialDoubleConstantName(double d)
		{
			if (double.IsNaN(d))
				return "NaN";
			if (double.IsPositiveInfinity(d))
				return "PositiveInfinity";
			if (double.IsNegativeInfinity(d))
				return "NegativeInfinity";
			if (d == double.Epsilon)
				return "Epsilon";
			if (d == double.MinValue)
				return "MinValue";
			if (d == double.MaxValue)
				return "MaxValue";
			return null;
		}

		static string FormatPrimitiveLiteral(object value) {
			// The primitive-ref path above routes every numeric literal through here, so the
			// special-value names have to be produced in this helper (dnSpy 2.3 prints
			// 'double.MaxValue' where a numeric literal would otherwise appear).
			if (value is float f) {
				var name = GetSpecialFloatConstantName(f);
				if (name != null)
					return "float." + name;
			}
			if (value is double dbl) {
				var name = GetSpecialDoubleConstantName(dbl);
				if (name != null)
					return "double." + name;
			}
			var sb = new StringBuilder();
			sb.Append(((IFormattable)value).ToString(null, NumberFormatInfo.InvariantInfo));
			// dnSpy 2.3 writes the unsigned suffix in upper case ('0U', 'UL'), not 'u'/'uL'.
			if (value is uint || value is ulong)
				sb.Append('U');
			if (value is long || value is ulong)
				sb.Append('L');
			return sb.ToString();
		}

		public override void WritePrimitiveType(string type) {
			// Primitive type keywords carry a corlib reference like the 2.3 engine ('void' ->
			// System.Void) so they navigate to the corlib definitions.
			lastColorPath = "primitive-type";
			if (primitiveTypeFullNames.TryGetValue(type, out var fullName) && corlibAssemblyRef is not null)
				EmitText(type, GetPrimitiveTypeRef(fullName), DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
			else
				EmitText(type, BoxedTextColor.Keyword);
			if (type == "new")
				EmitText("()", BoxedTextColor.Punctuation);
			isAtStartOfLine = false;
		}

		/// <summary>
		/// True when the identifier is the last segment of a type name (a base type, an
		/// attribute type, a generic argument) - not a namespace segment.
		/// </summary>
		static bool IsTypeNameIdentifier(Identifier identifier) {
			if (identifier.Parent is CS.Attribute)
				return true;
			if (identifier.Parent is CS.SimpleType simple)
				return simple.Role != CS.MemberType.TargetRole;
			if (identifier.Parent is CS.MemberType memberType)
				return memberType.MemberNameToken == identifier;
			return false;
		}

		/// <summary>True when the identifier names an attribute's type in an attribute section.</summary>
		static bool IsAttributeTypeName(Identifier identifier) =>
			identifier.Parent is CS.Attribute || identifier.Parent?.Parent is CS.Attribute;

		/// <summary>Number of type arguments on the type node, or -1 when unknown.</summary>
		static int ArityOfTypeName(Identifier identifier) {
			if (identifier.Parent is CS.SimpleType simple)
				return simple.TypeArguments.Count;
			if (identifier.Parent is CS.MemberType memberType)
				return memberType.TypeArguments.Count;
			return -1;
		}

		/// <summary>
		/// Finds a TypeRef in this module's reference tables by simple name; the scope of those
		/// refs is authoritative, so the resulting reference navigates to the right assembly.
		/// </summary>
		TypeRef? ResolveTypeRefByName(string name, int arity) =>
			Common.DnlibReferenceMapper.ResolveTypeRefByName(module, name, arity);

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
		internal static long ProfAnnTicks, ProfTrrTicks, ProfITypeTicks, ProfInvocTicks, ProfSymbolTicks, ProfFallbackTicks;
		internal static readonly bool ProfEnabled = Environment.GetEnvironmentVariable("DNSPY_ILSPY32_PROF") == "1";

		static long ProfAcc(System.Diagnostics.Stopwatch? sw) => sw?.ElapsedTicks ?? 0;

		object? ResolveReference(AstNode node) {
			if (node is null || typeSystem is null)
				return null;
			var rr = node.Annotation<MemberResolveResult>();
			if (rr?.Member != null) {
				lastColorPath = "ref-rr";
				var sw1 = ProfEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
				var memberResolved = ToDnlib(TryGetCecil(rr.Member));
				ProfAnnTicks += ProfAcc(sw1);
				if (memberResolved is not null)
					return memberResolved;
				// External members (x.Length, x.Count on BCL types, external accessors) can
				// fail GetCecil; the module's own MemberRef table carries the authoritative
				// entry - the same ref the 2.3 engine emitted for these sites. The lookup is
				// cached per member identity: large types resolve the same external members
				// from hundreds of tokens, and the table scan dominates the render otherwise.
				var sw2 = ProfEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
				if (externalMemberRefCache is null)
					externalMemberRefCache = new Dictionary<ICSharpCode.Decompiler.TypeSystem.IMember, object?>();
				if (!externalMemberRefCache.TryGetValue(rr.Member, out var extCached)) {
					extCached = FindMemberRefForTypeSystemMember(rr.Member, node);
					externalMemberRefCache[rr.Member] = extCached;
				}
				ProfFallbackTicks += ProfAcc(sw2);
				return extCached;
			}
			var trr = node.Annotation<TypeResolveResult>();
			if (trr?.Type?.GetDefinition() != null) {
				lastColorPath = "ref-trr";
				var sw3 = ProfEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
				var trrResolved = ToDnlib(TryGetCecil(trr.Type.GetDefinition()));
				ProfTrrTicks += ProfAcc(sw3);
				return trrResolved;
			}
			var type = node.Annotation<ICSharpCode.Decompiler.TypeSystem.IType>();
			if (type != null) {
				var definition = type.GetDefinition();
				if (definition != null) {
					lastColorPath = "ref-itype";
					var sw4 = ProfEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
					var itypeResolved = ToDnlib(TryGetCecil(definition));
					ProfITypeTicks += ProfAcc(sw4);
					return itypeResolved;
				}
			}
			// 'items.Add(...)': the engine attaches the resolved method to the surrounding
			// InvocationExpression, not to the MemberReferenceExpression that forms its
			// target (CallBuilder creates the mre bare and annotates only the invocation).
			// Probe the invocation from here so invoked member names carry references like
			// the 2.3 engine's output. Only for bare call targets: a node that already
			// resolved on its own (the 'items' in 'items.Add' carries its field's
			// MemberResolveResult; a local carries ILVariableResolveResult) must not steal
			// the invocation's method.
			// 'items.Add(...)': the engine attaches the resolved method to the surrounding
			// InvocationExpression, not to the MemberReferenceExpression that forms its
			// target (CallBuilder creates the mre bare and annotates only the invocation).
			// Probe the invocation from here so invoked member names carry references like
			// the 2.3 engine's output. Only for bare call targets: a node that already
			// resolved on its own (the 'items' in 'items.Add' carries its field's
			// MemberResolveResult; a local carries ILVariableResolveResult) must not steal
			// the invocation's method.
			if (node.Parent is InvocationExpression inv && inv.Target == node &&
					node.Annotation<ResolveResult>() is null &&
					inv.Annotation<ResolveResult>() is InvocationResolveResult irr && irr.Member != null) {
				lastColorPath = "ref-invocation";
				var sw5 = ProfEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
				var invocationRef = ToDnlib(TryGetCecil(irr.Member));
				ProfInvocTicks += ProfAcc(sw5);
				if (invocationRef != null)
					return invocationRef;
			}
			var sw6 = ProfEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
			var symbol = node.GetSymbol();
			if (symbol is null) {
				lastColorPath = "ref-no-symbol";
				ProfSymbolTicks += ProfAcc(sw6);
			}
			else {
				lastColorPath = "ref-symbol";
				var symbolResult = SymbolToDnlib(symbol);
				ProfSymbolTicks += ProfAcc(sw6);
				return symbolResult;
			}
			return null;
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
		Dictionary<ICSharpCode.Decompiler.TypeSystem.IMember, Mono.Cecil.MemberReference?>? cecilCache;
		Dictionary<ICSharpCode.Decompiler.TypeSystem.IMember, object?>? externalMemberRefCache;

		/// <summary>
		/// Cached GetCecil: the engine's entityDict lookup takes a lock every call, and the
		/// same members are re-resolved for every token that references them. The result is
		/// deterministic per (typeSystem, member), so a per-writer cache is safe.
		/// </summary>
		Mono.Cecil.MemberReference? TryGetCecil(ICSharpCode.Decompiler.TypeSystem.IMember? member) {
			if (member is null || typeSystem is null)
				return null;
			if (cecilCache is null)
				cecilCache = new Dictionary<ICSharpCode.Decompiler.TypeSystem.IMember, Mono.Cecil.MemberReference?>();
			if (cecilCache.TryGetValue(member, out var cached))
				return cached;
			Mono.Cecil.MemberReference? resolved = null;
			try {
				resolved = typeSystem.GetCecil(member);
			}
			catch {
			}
			cecilCache[member] = resolved;
			return resolved;
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

		/// <summary>
		/// Resolves a type-system member to a dnlib reference when the cecil bridge fails
		/// (external members): looks up the module's own MemberRef table by declaring type and
		/// member name (with accessor-name variants for properties and prefix matching for
		/// generic instantiations). The table entry carries the authoritative assembly scope,
		/// matching the reference the 2.3 engine emitted for the same site.
		/// </summary>
		object? FindMemberRefForTypeSystemMember(ICSharpCode.Decompiler.TypeSystem.IMember member, AstNode node) {
			var declaringNames = new List<string>();
			if (node is MemberReferenceExpression usageMre && usageMre.Target is not null) {
				var targetType = GetTypeNameTextForTarget(usageMre.Target);
				if (targetType is not null)
					declaringNames.Add(targetType);
			}
			else if (node is InvocationExpression usageInv && usageInv.Target is MemberReferenceExpression invMre && invMre.Target is not null) {
				var targetType = GetTypeNameTextForTarget(invMre.Target);
				if (targetType is not null)
					declaringNames.Add(targetType);
			}
			else if (node is IndexerExpression indexExpr) {
				var targetType = GetTypeNameTextForTarget(indexExpr.Target);
				if (targetType is not null)
					declaringNames.Add(targetType);
			}
			if (member.DeclaringType is not null)
				declaringNames.Add(member.DeclaringType.FullName);
			if (declaringNames.Count == 0)
				return null;
			var memberNames = new List<string> { member.Name };
			if (member is ICSharpCode.Decompiler.TypeSystem.IProperty property) {
				if (property.Getter is not null)
					memberNames.Add(property.Getter.Name);
				if (property.Setter is not null)
					memberNames.Add(property.Setter.Name);
			}
			if (member is ICSharpCode.Decompiler.TypeSystem.IEvent ev2) {
				if (ev2.AddAccessor is not null)
					memberNames.Add(ev2.AddAccessor.Name);
				if (ev2.RemoveAccessor is not null)
					memberNames.Add(ev2.RemoveAccessor.Name);
			}
			foreach (var declaringName in declaringNames)
				foreach (var memberName in memberNames)
					if (Common.DnlibReferenceMapper.FindMemberRefInTable(module, declaringName, memberName) is { } found)
						return found;
			// Array.Length/LongLength compiles to the raw ldlen instruction (no MemberRef in
			// the table); the 2.3 engine synthesized the get_Length MemberRefUser, so match it.
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1")
				Console.Error.WriteLine("ARR-TRY decl=" + member.DeclaringType?.FullName + " name=" + member.Name + " isProp=" + (member is ICSharpCode.Decompiler.TypeSystem.IProperty));
			if (member is ICSharpCode.Decompiler.TypeSystem.IProperty lengthProperty &&
					member.DeclaringType?.FullName == "System.Array" &&
					(lengthProperty.Name == "Length" || lengthProperty.Name == "LongLength")) {
				var arrayTypeRef = new TypeRefUser(module, "System", "Array", module.CorLibTypes.AssemblyRef);
				var sig = MethodSig.CreateInstance(lengthProperty.Name == "Length"
					? module.CorLibTypes.Int32
					: module.CorLibTypes.Int64);
				return new MemberRefUser(module, "get_" + lengthProperty.Name, sig, arrayTypeRef);
			}
			return null;
		}

		/// <summary>The type name an expression target refers to (member-access chains only).</summary>
		static string? GetTypeNameTextForTarget(AstNode target) {
			// 'x.Member' where x is a local/param/field: the target expression has no type
			// annotation we can use for the table lookup; declaring type name is tried instead.
			if (target is MemberReferenceExpression inner)
				return inner.MemberName;
			if (target is IdentifierExpression ident)
				return ident.Identifier;
			return null;
		}

		Dictionary<Mono.Cecil.MemberReference, object?>? toDnlibCache;

		object? ToDnlib(Mono.Cecil.MemberReference? cecilMember) {
			if (cecilMember is null)
				return null;
			// cecil hands out the same instance for a given metadata token, so reference
			// equality is a valid cache key. ResolveToken + SameIdentity dominate the render
			// of large types (the same member is resolved from hundreds of tokens).
			var cache = toDnlibCache ??= new Dictionary<Mono.Cecil.MemberReference, object?>();
			if (cache.TryGetValue(cecilMember, out var cached))
				return cached;
			var mapped = Common.DnlibReferenceMapper.ToDnlib(module, cecilMember);
			cache[cecilMember] = mapped;
			if (mapped is null)
				lastColorPath = "ref-external-unmapped";
			return mapped;
		}
	}
}
