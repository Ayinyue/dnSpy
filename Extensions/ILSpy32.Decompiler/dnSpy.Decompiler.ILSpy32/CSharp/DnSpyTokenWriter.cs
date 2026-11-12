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
			int best = -1;
			foreach (var (l, c, o) in positionMap) {
				if (l != line)
					continue;
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
					methodBuilders.Push((new MethodDebugInfoBuilder(settingsVersion, StateMachineKind.None,
						methodDef, null, Array.Empty<SourceLocal>(), null, null), methodDef));
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

			// Preserve the ref-* diagnostic tag ResolveReference may have left (which sub-path
			// failed); only plain identifiers get the generic fallback tag.
			if (!lastColorPath.StartsWith("ref-", StringComparison.Ordinal))
				lastColorPath = "fallback";
			var color = SyntacticColor(identifier);
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
		IMemberDef? FindEnclosingTypeMember(string name) {
			foreach (var node in nodeStack) {
				if (node is CS.TypeDeclaration typeDecl && typeDecl.Annotation<TypeDef>() is { } typeDef) {
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
			var tokenRef = GetCurrentTokenReference() as dnlib.DotNet.IMemberRef;
			if (tokenRef is not null) {
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
			var resolved = ResolveReference(node);
			if (resolved is not null)
				return resolved;
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
				if (float.IsInfinity(f) || float.IsNaN(f)) {
					EmitText("float", BoxedTextColor.Keyword);
					EmitText(".", BoxedTextColor.Punctuation);
					output.Write(float.IsPositiveInfinity(f) ? "PositiveInfinity" : float.IsNegativeInfinity(f) ? "NegativeInfinity" : "NaN", BoxedTextColor.Keyword);
					return;
				}
				if (f == 0 && 1 / f == float.NegativeInfinity)
					EmitText("-", BoxedTextColor.Operator);
				output.Write(f.ToString("R", NumberFormatInfo.InvariantInfo) + "f", BoxedTextColor.Number);
				return;
			}

			if (value is double dbl) {
				if (double.IsInfinity(dbl) || double.IsNaN(dbl)) {
					EmitText("double", BoxedTextColor.Keyword);
					EmitText(".", BoxedTextColor.Punctuation);
					output.Write(double.IsPositiveInfinity(dbl) ? "PositiveInfinity" : double.IsNegativeInfinity(dbl) ? "NegativeInfinity" : "NaN", BoxedTextColor.Keyword);
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
		object? ResolveReference(AstNode node) {
			if (node is null || typeSystem is null)
				return null;
			var rr = node.Annotation<MemberResolveResult>();
			if (rr?.Member != null) {
				lastColorPath = "ref-rr";
				return ToDnlib(TryGetCecil(rr.Member));
			}
			var trr = node.Annotation<TypeResolveResult>();
			if (trr?.Type?.GetDefinition() != null) {
				lastColorPath = "ref-trr";
				return ToDnlib(TryGetCecil(trr.Type.GetDefinition()));
			}
			var type = node.Annotation<ICSharpCode.Decompiler.TypeSystem.IType>();
			if (type != null) {
				var definition = type.GetDefinition();
				if (definition != null) {
					lastColorPath = "ref-itype";
					return ToDnlib(TryGetCecil(definition));
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
				var invocationRef = ToDnlib(TryGetCecil(irr.Member));
				if (invocationRef != null)
					return invocationRef;
			}
			var symbol = node.GetSymbol();
			if (symbol is null)
				lastColorPath = "ref-no-symbol";
			else
				lastColorPath = "ref-symbol";
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
			var mapped = Common.DnlibReferenceMapper.ToDnlib(module, cecilMember);
			if (mapped is null)
				lastColorPath = "ref-external-unmapped";
			return mapped;
		}
	}
}
