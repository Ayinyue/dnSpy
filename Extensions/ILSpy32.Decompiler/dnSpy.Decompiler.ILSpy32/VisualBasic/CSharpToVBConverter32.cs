// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.Common;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.VB;
using ICSharpCode.NRefactory.VB.Ast;
using Mono.Cecil;
using TypeSystem = ICSharpCode.NRefactory.TypeSystem;
using CS = ICSharpCode.Decompiler.CSharp.Syntax;
using VB = ICSharpCode.NRefactory.VB;

// This file is a direct port of the ILSpy 3.2 VB converter, which predates
// nullable reference type annotations. The converter freely mixes C# AST,
// Mono.Cecil and VB AST objects whose nullability cannot be inferred, so the
// whole file runs in the oblivious (legacy) nullable context. The public
// IEnvironmentProvider contract implemented by our own code lives in a
// separate nullable-enabled file (ILSpyEnvironmentProvider32.cs).
#nullable disable

namespace dnSpy.Decompiler.ILSpy32.VisualBasic {
	public interface IEnvironmentProvider
	{
		string RootNamespace { get; }
		string GetTypeNameForAttribute(CS.Attribute attribute);
		TypeKind GetTypeKindForAstType(CS.AstType type);
		TypeCode ResolveExpression(CS.Expression expression);
		bool? IsReferenceType(CS.Expression expression);
		//ITypeResolveContext ResolveContext { get; }
		TypeSystem.IType ResolveType(AstType type, TypeDeclaration entity = null);
		bool IsMethodGroup(CS.Expression expression);
		bool HasEvent(Expression expression);
		CS.ParameterDeclaration[] GetParametersForProperty(CS.PropertyDeclaration property);
	}

	/// <summary>
	/// Description of CSharpToVBConverterVisitor.
	/// </summary>
	public class CSharpToVBConverter32 : CS.IAstVisitor<object, VB.AstNode>
	{
		IEnvironmentProvider provider;
		Stack<BlockStatement> blocks;
		Stack<TypeDeclaration> types;
		Stack<MemberInfo> members;
		readonly ModuleDef module;
		int catchVariableCount;
		int unusedObjectCounter;
		int incrementTempCounter;
		// Increments that belong to a loop condition: they are emitted at the top of the loop
		// body, so the converter must not hoist them a second time.
		readonly Dictionary<CS.UnaryOperatorExpression, bool> loopConditionUpdates =
			new Dictionary<CS.UnaryOperatorExpression, bool>();
		readonly Stack<string> namespaceStack = new Stack<string>();
		readonly Dictionary<string, string> variableTypes = new Dictionary<string, string>(StringComparer.Ordinal);
		// VB forbids a local that hides an enclosing one (BC30616) and a second declaration of
		// the same name in one block (BC30288); C# allows both. Locals are therefore tracked
		// per VB block, and a conflicting declaration is emitted under a fresh name.
		readonly Dictionary<BlockStatement, Dictionary<string, string>> blockScopes =
			new Dictionary<BlockStatement, Dictionary<string, string>>();
		readonly Dictionary<CS.VariableInitializer, string> localDeclarationNames =
			new Dictionary<CS.VariableInitializer, string>();
		// VB makes a declaration visible in its whole block, independent of the textual order,
		// so an outer "Dim i" written below an inner one still gets hidden by it. Conflicts are
		// therefore decided against every local name already used in the method.
		readonly HashSet<string> methodLocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> emittedLocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// Simple names of the types referenced by the member body. VB resolves identifiers
		// case-insensitively, so a local or parameter named "searchLocation" swallows the type
		// reference "SearchLocation" ('SearchLocation.SelectedType' then fails, BC30456).
		readonly HashSet<string> memberTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// Parameter renames forced by the type-name collision above; consulted when references
		// to the original parameter name are emitted.
		// Case-sensitive lookup: the keys are C# identifiers as spelled at their declarations,
		// and every C# reference spells them identically. A case-insensitive lookup would
		// rewrite a DIFFERENT member whose name differs only in case ('Parameter' property vs
		// 'parameter' parameter) into the renamed parameter - the assignment
		// 'Parameter = parameter' silently became 'parameter2 = parameter2' (a no-op).
		readonly Dictionary<string, string> paramNameRenames = new Dictionary<string, string>(StringComparer.Ordinal);
		// Every local name the method declares. ILSpy already disambiguates repeats by appending
		// a digit (array, array2), so a generated name must not reuse any of them.
		readonly HashSet<string> reservedLocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// C# raises events through a copy: "var handler = Changed; if (handler != null) handler(...)".
		// The copy's name maps back to the event so the call becomes RaiseEvent.
		readonly Dictionary<string, string> eventAliases = new Dictionary<string, string>(StringComparer.Ordinal);
		// Copies that only exist to null-check the event before raising it. RaiseEvent performs
		// that check itself, and VB cannot read an event, so both the copy and the guard go away.
		readonly HashSet<string> suppressedEventAliases = new HashSet<string>(StringComparer.Ordinal);
		readonly Common.ExpressionTypeResolver typeResolver;

		/// <summary>
		/// Per-switch labels used to translate "goto case" / "goto default". VB has no such
		/// statement, but it allows GoTo to a label inside a Case block.
		/// </summary>
		sealed class SwitchLabelMap {
			public readonly Dictionary<CS.SwitchSection, string> BySection = new Dictionary<CS.SwitchSection, string>();
			public readonly Dictionary<string, string> ByCaseValue = new Dictionary<string, string>(StringComparer.Ordinal);
			public string DefaultLabel;
		}
		readonly Stack<SwitchLabelMap> switchLabelMaps;

		enum ConvertedStatementKind {
			None,
			While,
			For,
		}

		class MemberInfo
		{
			public bool inIterator;
		}

		readonly ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem? engineTypeSystem;

		/// <summary>
		/// dnSpy's 2.3 VB output carried the Nullable/NullableContext attributes; the 3.2
		/// converter drops them (VB cannot apply them, BC30662). Keeping them is what matches 2.3.
		/// </summary>
		public bool EmitNullableAttributes { get; set; }

		public CSharpToVBConverter32(ModuleDef module, IEnvironmentProvider provider,
				ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem? engineTypeSystem = null)
		{
			this.module = module;
			this.provider = provider;
			this.engineTypeSystem = engineTypeSystem;
			this.blocks = new Stack<BlockStatement>();
			this.types = new Stack<TypeDeclaration>();
			this.members = new Stack<MemberInfo>();
			this.switchLabelMaps = new Stack<SwitchLabelMap>();
			this.typeResolver = new Common.ExpressionTypeResolver(module, variableTypes);
		}

		public AstNode VisitAnonymousMethodExpression(CS.AnonymousMethodExpression anonymousMethodExpression, object data)
		{
			members.Push(new MemberInfo());

			var expr = new MultiLineLambdaExpression() {
				Body = (BlockStatement)anonymousMethodExpression.Body.AcceptVisitor(this, data)
			};
			if (anonymousMethodExpression.IsAsync)
				expr.Modifiers |= LambdaExpressionModifiers.Async;
			var retStmt = anonymousMethodExpression.Body.Descendants.OfType<CS.ReturnStatement>().FirstOrDefault();
			expr.IsSub = retStmt == null || retStmt.Expression.IsNull;

			ConvertNodes(anonymousMethodExpression.Parameters, expr.Parameters);

			if (members.Pop().inIterator) {
				expr.Modifiers |= LambdaExpressionModifiers.Iterator;
			}

			return EndNode(anonymousMethodExpression, expr);
		}

		public AstNode VisitUndocumentedExpression(CS.UndocumentedExpression undocumentedExpression, object data)
		{
			var invocation = new InvocationExpression();

			switch (undocumentedExpression.UndocumentedExpressionType) {
				case CS.UndocumentedExpressionType.ArgListAccess:
				case CS.UndocumentedExpressionType.ArgList:
					invocation.Target = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__ArgList") };
					break;
				case CS.UndocumentedExpressionType.RefValue:
					invocation.Target = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__RefValue") };
					break;
				case CS.UndocumentedExpressionType.RefType:
					invocation.Target = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__RefType") };
					break;
				case CS.UndocumentedExpressionType.MakeRef:
					invocation.Target = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__MakeRef") };
					break;
				default:
					throw new Exception("Invalid value for UndocumentedExpressionType");
			}

			ConvertNodes(undocumentedExpression.Arguments, invocation.Arguments);

			return EndNode(undocumentedExpression, invocation);
		}

		public AstNode VisitArrayCreateExpression(CS.ArrayCreateExpression arrayCreateExpression, object data)
		{
			var expr = new ArrayCreateExpression() {
				Type = (AstType)arrayCreateExpression.Type.AcceptVisitor(this, data),
				Initializer = (ArrayInitializerExpression)arrayCreateExpression.Initializer.AcceptVisitor(this, data)
			};
			ConvertNodes(arrayCreateExpression.Arguments, expr.Arguments, ReduceArrayUpperBoundExpression);
			ConvertNodes(arrayCreateExpression.AdditionalArraySpecifiers, expr.AdditionalArraySpecifiers);

			return EndNode(arrayCreateExpression, expr);
		}

		Expression ReduceArrayUpperBoundExpression(Expression expression)
		{
			if (expression is PrimitiveExpression)
			{
				var numericLiteral = expression as PrimitiveExpression;
				int? upperBound = numericLiteral.Value as int?;
				if (upperBound.HasValue)
					return new PrimitiveExpression(upperBound.Value - 1);
			}
			return new BinaryOperatorExpression(expression, BinaryOperatorType.Subtract, new PrimitiveExpression(1));
		}

		public AstNode VisitArrayInitializerExpression(CS.ArrayInitializerExpression arrayInitializerExpression, object data)
		{
			var expr = new ArrayInitializerExpression();
			ConvertNodes(arrayInitializerExpression.Elements, expr.Elements);

			return EndNode(arrayInitializerExpression, expr);
		}

		public AstNode VisitAsExpression(CS.AsExpression asExpression, object data)
		{
			// TryCast only accepts reference types (BC30792). For a value type such as Boolean?
			// the CLR performs an unboxing conversion, which VB spells DirectCast.
			var typeName = asExpression.Type.ToString();
			var castType = IsValueTypeTarget(typeName) ? CastType.DirectCast : CastType.TryCast;
			return EndNode(asExpression, new CastExpression(castType, (AstType)asExpression.Type.AcceptVisitor(this, data), (Expression)asExpression.Expression.AcceptVisitor(this, data)));
		}

		bool IsValueTypeTarget(string typeName) {
			if (typeName.EndsWith("?", StringComparison.Ordinal))
				return true;
			return typeResolver.Classify(typeName) == false;
		}

		public AstNode VisitAssignmentExpression(CS.AssignmentExpression assignmentExpression, object data)
		{
			var left = (Expression)assignmentExpression.Left.AcceptVisitor(this, data);
			var op = AssignmentOperatorType.None;
			var right = (Expression)assignmentExpression.Right.AcceptVisitor(this, data);

			switch (assignmentExpression.Operator) {
				case CS.AssignmentOperatorType.Assign:
					op = AssignmentOperatorType.Assign;
					break;
				case CS.AssignmentOperatorType.Add:
					if (provider.HasEvent(left)) {
						var addHandler = new AddRemoveHandlerStatement { IsAddHandler = true };
						addHandler.EventExpression = left;
						addHandler.DelegateExpression = right;
						return EndNode(assignmentExpression, addHandler);
					}
					op = AssignmentOperatorType.Add;
					break;
				case CS.AssignmentOperatorType.Subtract:
					if (provider.HasEvent(left)) {
						var addHandler = new AddRemoveHandlerStatement { IsAddHandler = false };
						addHandler.EventExpression = left;
						addHandler.DelegateExpression = right;
						return EndNode(assignmentExpression, addHandler);
					}
					op = AssignmentOperatorType.Subtract;
					break;
				case CS.AssignmentOperatorType.Multiply:
					op = AssignmentOperatorType.Multiply;
					break;
				case CS.AssignmentOperatorType.Divide:
					op = AssignmentOperatorType.Divide;
					break;
				case CS.AssignmentOperatorType.Modulus:
					op = AssignmentOperatorType.Assign;
					right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.Modulus, right);
					break;
				case CS.AssignmentOperatorType.ShiftLeft:
					op = AssignmentOperatorType.ShiftLeft;
					break;
				case CS.AssignmentOperatorType.ShiftRight:
					op = AssignmentOperatorType.ShiftRight;
					break;
				case CS.AssignmentOperatorType.BitwiseAnd:
					op = AssignmentOperatorType.Assign;
					right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.BitwiseAnd, right);
					break;
				case CS.AssignmentOperatorType.BitwiseOr:
					op = AssignmentOperatorType.Assign;
					right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.BitwiseOr, right);
					break;
				case CS.AssignmentOperatorType.ExclusiveOr:
					op = AssignmentOperatorType.Assign;
					right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.ExclusiveOr, right);
					break;
				default:
					throw new Exception("Invalid value for AssignmentOperatorType: " + assignmentExpression.Operator);
			}

			var expr = new AssignmentExpression(left, op, right);

			// VB has no assignment expression. Emitted in expression position, "a = (b = c)"
			// would be parsed as a comparison. Hoist the assignment into the enclosing block
			// and yield the assigned value instead. Only the plain assignment operator is
			// handled: the value of a compound assignment cannot be expressed this way.
			// Statement positions must keep the normal path, including unbraced bodies such
			// as "if (c) x = y;": hoisting there would make the assignment unconditional.
			// Compound assignments ('+=', '-=', ...) fold an operator call: the engine's
			// resolve result on the assignment node carries the op_/Concat method the same
			// way a binary expression does - attach it so the 2.3 engine's operator-token
			// reference also renders on the compound token.
			if (op != AssignmentOperatorType.Assign) {
				// The operator resolve result sits first but carries no method for the folded
				// string Concat (udom=null); the invocation resolve result behind it does.
				ICSharpCode.Decompiler.TypeSystem.IMethod? compoundMethod =
					assignmentExpression.Annotation<ICSharpCode.Decompiler.Semantics.OperatorResolveResult>()?.UserDefinedOperatorMethod
					?? assignmentExpression.Annotation<ICSharpCode.Decompiler.CSharp.Resolver.CSharpInvocationResolveResult>()?.Member as ICSharpCode.Decompiler.TypeSystem.IMethod;
				if (compoundMethod is not null) {
					var compoundRef = ToDnlibRef(TryGetCecilRef(compoundMethod));
					if (compoundRef is null && module is ModuleDefMD compoundMd &&
							compoundMethod.DeclaringType is not null)
						compoundRef = Common.DnlibReferenceMapper.FindMemberRefInTable(compoundMd,
							compoundMethod.DeclaringType.FullName, compoundMethod.Name);
					if (compoundRef is not null)
						expr.AddAnnotation(compoundRef);
				}
			}

			if (op == AssignmentOperatorType.Assign
					&& !(assignmentExpression.Parent is CS.Statement)
					&& CanHoistAssignment(assignmentExpression)
					&& blocks.Count > 0) {
				blocks.Peek().Add(new ExpressionStatement(expr));
				// The value of "target = value" is the value that was stored, so re-read the
				// target. Cloning the right-hand side instead would evaluate it twice and, for
				// "x = (y = new T())", produce two different objects.
				return EndNode(assignmentExpression, (Expression)left.Clone());
			}

			return EndNode(assignmentExpression, expr);
		}

		// Hoisting is only safe when the assignment is evaluated once per execution of the
		// statement being converted. Stop at the nearest block: a lambda body belongs to
		// another scope, and a loop condition/iterator is re-evaluated on every iteration
		// (hoisting "while ((line = Read()) != null)" would read only once).
		static bool CanHoistAssignment(CS.AstNode node) {

			for (var parent = node.Parent; parent != null; parent = parent.Parent) {
				// "a ?? (a = new D(...))" must not be hoisted: the assignment only runs when the
				// left side is null, hoisting it would assign unconditionally (and kill the cache).
				if (parent is CS.BinaryOperatorExpression { Operator: CS.BinaryOperatorType.NullCoalescing })
					return false;
				if (parent is CS.LambdaExpression || parent is CS.AnonymousMethodExpression)
					return false;
				if (parent is CS.WhileStatement || parent is CS.DoWhileStatement
						|| parent is CS.ForStatement || parent is CS.ForeachStatement)
					return false;
				if (parent is CS.BlockStatement)
					return true;
			}
			return true;
		}

		/// <summary>
		/// Clears the per-member state. Constructors and operators are member bodies too, so they
		/// must reset it as well; otherwise names leak from the previously converted method.
		/// Property, indexer and custom-event accessors are member bodies in the same sense: they
		/// used to run with whatever state the last converted method had left, so the resolver's
		/// field and parameter lookups failed and reference comparisons fell back to '=' (BC30452).
		/// </summary>
		void ResetMethodState(CS.AstNode declaration) {
			variableTypes.Clear();
			blockScopes.Clear();
			localDeclarationNames.Clear();
			methodLocalNames.Clear();
			emittedLocalNames.Clear();
			reservedLocalNames.Clear();
			eventAliases.Clear();
			suppressedEventAliases.Clear();
			foreach (var variableDeclaration in declaration.Descendants.OfType<CS.VariableDeclarationStatement>()) {
				foreach (var variable in variableDeclaration.Variables)
					reservedLocalNames.Add(variable.Name);
			}
			foreach (var outVar in declaration.Descendants.OfType<CS.OutVarDeclarationExpression>())
				reservedLocalNames.Add(outVar.Variable.Name);
			foreach (var forEach in declaration.Descendants.OfType<CS.ForeachStatement>())
				reservedLocalNames.Add(forEach.VariableName);
			memberTypeNames.Clear();
			foreach (var simpleType in declaration.Descendants.OfType<CS.SimpleType>())
				memberTypeNames.Add(simpleType.Identifier);
			foreach (var memberType in declaration.Descendants.OfType<CS.MemberType>())
				memberTypeNames.Add(memberType.MemberName);
			paramNameRenames.Clear();
			typeResolver.CurrentType = ResolveCurrentType(declaration);
			CollectSuppressedEventAliases(declaration);
		}

		/// <summary>
		/// Declaring type of the member about to be converted. Methods and constructors carry the
		/// MethodDef annotation themselves; for properties, indexers and custom events it lives on
		/// the accessors.
		/// </summary>
		TypeDef ResolveCurrentType(CS.AstNode declaration) {
			var fromSelf = declaration.Annotation<MethodDef>()?.DeclaringType;
			if (fromSelf != null)
				return fromSelf;
			foreach (var accessor in declaration.Descendants.OfType<CS.Accessor>()) {
				var fromAccessor = accessor.Annotation<MethodDef>()?.DeclaringType;
				if (fromAccessor != null)
					return fromAccessor;
			}
			return null;
		}

		/// <summary>
		/// Marks an event copy as removable when it is used exactly twice: in the null check and
		/// as the target of the invocation inside that guard's body.
		/// </summary>
		void CollectSuppressedEventAliases(CS.AstNode declaration) {
			foreach (var variableDeclaration in declaration.Descendants.OfType<CS.VariableDeclarationStatement>()) {
				if (variableDeclaration.Variables.Count != 1)
					continue;
				var variable = variableDeclaration.Variables.First();
				if (GetEventName(variable.Initializer) == null)
					continue;
				var uses = declaration.Descendants.OfType<CS.IdentifierExpression>()
					.Where(u => u.Identifier == variable.Name).ToList();
				if (uses.Count != 2)
					continue;
				bool nullCheck = uses.Any(u => u.Parent is CS.BinaryOperatorExpression comparison &&
					(comparison.Operator == CS.BinaryOperatorType.InEquality || comparison.Operator == CS.BinaryOperatorType.Equality) &&
					(comparison.Right is CS.NullReferenceExpression || comparison.Left is CS.NullReferenceExpression));
				bool call = uses.Any(u => u.Parent is CS.InvocationExpression invocation && invocation.Target == u);
				if (nullCheck && call)
					suppressedEventAliases.Add(variable.Name);
			}
		}

		Dictionary<string, string> GetScope(BlockStatement block) {
			if (!blockScopes.TryGetValue(block, out var scope))
				blockScopes[block] = scope = new Dictionary<string, string>(StringComparer.Ordinal);
			return scope;
		}

		/// <summary>
		/// Registers a parameter and returns the name to emit. A parameter whose name collides
		/// case-insensitively with a type referenced by the body must be renamed (VB resolves
		/// identifiers without case, so "searchLocation As String" swallows "SearchLocation").
		/// </summary>
		/// <summary>
		/// Registers a method-header parameter and returns the name to emit. A parameter whose
		/// name collides case-insensitively with a type referenced by the body must be renamed
		/// (VB resolves identifiers without case, so "searchLocation As String" swallows
		/// "SearchLocation"). Lambda parameters are converted while their block is on the stack;
		/// they keep their original name — C# forbids shadowing there, parallel lambdas reuse the
		/// same name, and the member-level rename map cannot express per-lambda scopes.
		/// </summary>
		string RegisterParameterName(string name, string parameterTypeName = null) {
			if (blocks.Count != 0)
				return name;
			// "action As Action" is legal VB: after "As" the identifier is a type context, so a
			// parameter does not collide with its own type. Only *other* referenced types shadow it
			// (VB resolves identifiers case-insensitively) - see audit 8.98.
			string ownTypeIdentifier = null;
			if (!string.IsNullOrEmpty(parameterTypeName)) {
				int p = parameterTypeName.LastIndexOfAny(new[] { '.', '+' });
				ownTypeIdentifier = p >= 0 ? parameterTypeName.Substring(p + 1) : parameterTypeName;
			}
			bool conflicts = memberTypeNames.Any(t => !string.Equals(t, ownTypeIdentifier, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(t, name, StringComparison.OrdinalIgnoreCase))
				|| methodLocalNames.Contains(name) || variableTypes.ContainsKey(name);
			if (!conflicts) {
				methodLocalNames.Add(name);
				return name;
			}
			var emitted = MakeUniqueLocalName(name, 2);
			methodLocalNames.Add(emitted);
			paramNameRenames[name] = emitted;
			return emitted;
		}

		/// <summary>Emitted name for a reference to a local, following the enclosing blocks.</summary>
		string ResolveLocalName(string name) {
			// The block stack is the scope chain: nodes are not attached to their parents until
			// their children have been converted, so walking Parent would miss outer blocks.
			foreach (var block in blocks) {
				if (blockScopes.TryGetValue(block, out var scope) && scope.TryGetValue(name, out var mapped))
					return mapped;
			}
			if (paramNameRenames.TryGetValue(name, out var renamed))
				return renamed;
			return name;
		}

		/// <summary>
		/// Registers a local declaration and returns the name to emit, renaming it when the name
		/// is already visible in this block or an enclosing one.
		/// </summary>
		string DeclareLocal(string name) {
			if (blocks.Count == 0)
				return name;
			var block = blocks.Peek();
			var scope = GetScope(block);
			bool conflicts = methodLocalNames.Contains(name) || scope.ContainsKey(name) ||
				memberTypeNames.Contains(name);
			if (!conflicts) {
				foreach (var enclosing in blocks) {
					if (enclosing == block)
						continue;
					if (blockScopes.TryGetValue(enclosing, out var outer) && outer.ContainsKey(name)) {
						conflicts = true;
						break;
					}
				}
			}
			methodLocalNames.Add(name);
			// "_" is not a legal VB identifier and the output visitor rewrites it to "__",
			// which then collides with a real local of that name. Give discards their own names.
			if (name == "_")
				conflicts = true;
			var emitted = conflicts
				? (name == "_" ? MakeUniqueLocalName("_discard", 1) : MakeUniqueLocalName(name, 2))
				: name;
			emittedLocalNames.Add(emitted);
			scope[name] = emitted;
			return emitted;
		}

		string MakeUniqueLocalName(string name, int start = 2) {
			for (int suffix = start; ; suffix++) {
				var candidate = name + suffix;
				if (reservedLocalNames.Contains(candidate) || methodLocalNames.Contains(candidate))
					continue;
				if (emittedLocalNames.Contains(candidate))
					continue;
				if (variableTypes.ContainsKey(candidate))
					continue;
				// The rename exists to keep names apart from referenced types, so the generated
				// name must stay clear of them too.
				if (memberTypeNames.Contains(candidate))
					continue;
				if (ResolveLocalName(candidate) != candidate)
					continue;
				return candidate;
			}
		}

		// VB has no ++/--. A standalone "x++;" becomes "x += 1". In an expression the value
		// matters: prefix yields the new value (re-read the target after hoisting the update),
		// postfix yields the old one (copy to a temporary first). When hoisting is unsafe the
		// old helper call is kept, even though no such helper exists - it is at least visible.
		Expression ConvertIncrement(CS.UnaryOperatorExpression unaryOperatorExpression, object data, bool increment, bool postfix) {
			var target = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data);
			// The update already ran at the top of the loop body, so the expression is just the
			// target: prefix yields the new value, postfix the old one.
			if (loopConditionUpdates.TryGetValue(unaryOperatorExpression, out var postfixUpdate))
				return postfix
					? new BinaryOperatorExpression((Expression)target.Clone(),
						increment ? BinaryOperatorType.Add : BinaryOperatorType.Subtract, new PrimitiveExpression(1))
					: (Expression)target.Clone();
			var op = increment ? AssignmentOperatorType.Add : AssignmentOperatorType.Subtract;
			var assign = new AssignmentExpression((Expression)target.Clone(), op, new PrimitiveExpression(1));

			if (unaryOperatorExpression.Parent is CS.ExpressionStatement)
				return assign;

			if (blocks.Count == 0 || !CanHoistAssignment(unaryOperatorExpression))
				return MakeIncrementHelperCall(increment, postfix, (Expression)target.Clone());

			string tempName = null;
			if (postfix) {
				tempName = "__tmp_" + (++incrementTempCounter);
				var decl = new LocalDeclarationStatement();
				decl.Modifiers = Modifiers.Dim;
				var variable = new VariableDeclaratorWithTypeAndInitializer();
				variable.Identifiers.Add(new VariableIdentifier() {
					Name = Identifier.Create((object)null, tempName)
				});
				variable.Initializer = (Expression)target.Clone();
				decl.Variables.Add(variable);
				blocks.Peek().Add(decl);
			}
			blocks.Peek().Add(new ExpressionStatement(assign));
			return tempName is null ? (Expression)target.Clone() : MakeIdentifierExpression(tempName);
		}

		static Expression MakeIncrementHelperCall(bool increment, bool postfix, Expression argument) {
			string name = increment
				? (postfix ? "__PostIncrement" : "__Increment")
				: (postfix ? "__PostDecrement" : "__Decrement");
			var invocation = new InvocationExpression();
			invocation.Target = new IdentifierExpression() { Identifier = Identifier.Create(BoxedTextColor.Keyword, name) };
			invocation.Arguments.Add(argument);
			return invocation;
		}

		static Expression MakeIdentifierExpression(string name) {
			var identifier = new IdentifierExpression();
			identifier.Identifier = Identifier.Create((object)null, name);
			return identifier;
		}

		static bool IsComparisonOperator(CS.BinaryOperatorType op) {
			switch (op) {
			case CS.BinaryOperatorType.Equality:
			case CS.BinaryOperatorType.InEquality:
			case CS.BinaryOperatorType.GreaterThan:
			case CS.BinaryOperatorType.GreaterThanOrEqual:
			case CS.BinaryOperatorType.LessThan:
			case CS.BinaryOperatorType.LessThanOrEqual:
				return true;
			default:
				return false;
			}
		}

		static bool IsArithmetic(CS.BinaryOperatorType op) {
			switch (op) {
			case CS.BinaryOperatorType.Add:
			case CS.BinaryOperatorType.Subtract:
			case CS.BinaryOperatorType.Multiply:
			case CS.BinaryOperatorType.Divide:
			case CS.BinaryOperatorType.Modulus:
			case CS.BinaryOperatorType.BitwiseAnd:
			case CS.BinaryOperatorType.BitwiseOr:
			case CS.BinaryOperatorType.ExclusiveOr:
			case CS.BinaryOperatorType.ShiftLeft:
			case CS.BinaryOperatorType.ShiftRight:
				return true;
			default:
				return false;
			}
		}

		bool IsCharExpression(CS.Expression expression) =>
			IsCharTypeName(typeResolver.GetTypeName(expression));

		static bool IsCharTypeName(string name) =>
			name == "char" || name == "Char" || name == "System.Char";

		static bool IsNumericTypeName(string name) {
			switch (name) {
			case "byte": case "sbyte": case "short": case "ushort":
			case "int": case "uint": case "long": case "ulong":
			case "Byte": case "SByte": case "Int16": case "UInt16":
			case "Int32": case "UInt32": case "Int64": case "UInt64":
			case "System.Byte": case "System.SByte": case "System.Int16": case "System.UInt16":
			case "System.Int32": case "System.UInt32": case "System.Int64": case "System.UInt64":
				return true;
			default:
				return false;
			}
		}

		static Expression WrapChrW(Expression expression) {
			var invocation = new InvocationExpression();
			invocation.Target = new IdentifierExpression() {
				Identifier = Identifier.Create((object)null, "ChrW")
			};
			invocation.Arguments.Add(expression);
			return invocation;
		}

		bool IsStringExpression(CS.Expression expression) {
			switch (expression) {
			case CS.PrimitiveExpression primitive:
				return primitive.Value is string;
			case CS.CastExpression cast:
				return cast.Type.ToString() == "string" || cast.Type.ToString() == "String";
			case CS.IdentifierExpression identifier:
				return variableTypes.TryGetValue(identifier.Identifier, out var typeName) &&
					(typeName == "string" || typeName == "String" || typeName == "System.String");
			default:
				return false;
			}
		}

		static Expression WrapAscW(Expression expression) {
			var invocation = new InvocationExpression();
			invocation.Target = new IdentifierExpression() {
				Identifier = Identifier.Create((object)null, "AscW")
			};
			invocation.Arguments.Add(expression);
			return invocation;
		}

		// Note: an invocation of an identifier called 'CStr' would be escaped as [CStr], so the
		// conversion is expressed as the AST node the output visitor knows how to print.
		static Expression WrapCStr(Expression expression) {
			return new CastExpression() {
				CastType = CastType.CStr,
				Expression = expression
			};
		}

		public AstNode VisitBaseReferenceExpression(CS.BaseReferenceExpression baseReferenceExpression, object data)
		{
			InstanceExpression result = new InstanceExpression(InstanceExpressionType.MyBase, ToNRefactoryLocation(baseReferenceExpression.StartLocation));
			// The 2.3 engine emitted the base type's reference on the MyBase keyword (the
			// formatter's node is InstanceExpression branch is ported); supply the annotation.
			if (FindEnclosingType(baseReferenceExpression)?.BaseType is { } baseType)
				result.AddAnnotation(baseType);
			return EndNode(baseReferenceExpression, result);
		}

		public AstNode VisitBinaryOperatorExpression(CS.BinaryOperatorExpression binaryOperatorExpression, object data)
		{
			var left = (Expression)binaryOperatorExpression.Left.AcceptVisitor(this, data);
			var op = BinaryOperatorType.None;
			var right = (Expression)binaryOperatorExpression.Right.AcceptVisitor(this, data);

			// VB has no arithmetic on Char (C# does): "c - 48" must become "AscW(c) - 48".
			// Addition is left alone because C# yields Char there, which VB represents differently.
			if (IsArithmetic(binaryOperatorExpression.Operator)
					&& !(binaryOperatorExpression.Operator == CS.BinaryOperatorType.Add
						&& (IsStringExpression(binaryOperatorExpression.Left) || IsStringExpression(binaryOperatorExpression.Right)))) {
				if (IsCharExpression(binaryOperatorExpression.Left))
					left = WrapAscW(left);
				if (IsCharExpression(binaryOperatorExpression.Right))
					right = WrapAscW(right);
			}

			// VB cannot compare Char with a number (BC30452): C# promotes the Char to Int32, so
			// "i != c" has to become "i <> AscW(c)". Char to Char stays untouched.
			if (IsComparisonOperator(binaryOperatorExpression.Operator)) {
				if (IsCharExpression(binaryOperatorExpression.Left) &&
						IsNumericTypeName(typeResolver.GetTypeName(binaryOperatorExpression.Right)))
					left = WrapAscW(left);
				if (IsCharExpression(binaryOperatorExpression.Right) &&
						IsNumericTypeName(typeResolver.GetTypeName(binaryOperatorExpression.Left)))
					right = WrapAscW(right);
			}

			// C# folds an implicit conversion to String into 'a + b' (dnlib's UTF8String is the
			// common case); VB does not chain a user-defined conversion into '+', so the operand
			// needs an explicit CStr (BC30452).
			if (binaryOperatorExpression.Operator == CS.BinaryOperatorType.Add) {
				var leftIsString = IsStringExpression(binaryOperatorExpression.Left);
				var rightIsString = IsStringExpression(binaryOperatorExpression.Right);
				var leftConverts = !leftIsString && typeResolver.HasImplicitStringConversion(binaryOperatorExpression.Left);
				var rightConverts = !rightIsString && typeResolver.HasImplicitStringConversion(binaryOperatorExpression.Right);
				// Only when the other side really is text, otherwise this could be an arithmetic
				// '+' on a type that happens to define a conversion to String.
				if (leftConverts && (rightIsString || rightConverts))
					left = WrapCStr(left);
				if (rightConverts && (leftIsString || leftConverts))
					right = WrapCStr(right);
			}

			switch (binaryOperatorExpression.Operator) {
				case CS.BinaryOperatorType.BitwiseAnd:
					op = BinaryOperatorType.BitwiseAnd;
					break;
				case CS.BinaryOperatorType.BitwiseOr:
					op = BinaryOperatorType.BitwiseOr;
					break;
				case CS.BinaryOperatorType.ConditionalAnd:
					op = BinaryOperatorType.LogicalAnd;
					break;
				case CS.BinaryOperatorType.ConditionalOr:
					op = BinaryOperatorType.LogicalOr;
					break;
				case CS.BinaryOperatorType.ExclusiveOr:
					op = BinaryOperatorType.ExclusiveOr;
					break;
				case CS.BinaryOperatorType.GreaterThan:
					op = BinaryOperatorType.GreaterThan;
					break;
				case CS.BinaryOperatorType.GreaterThanOrEqual:
					op = BinaryOperatorType.GreaterThanOrEqual;
					break;
				case CS.BinaryOperatorType.Equality:
					if (IsReferentialEquality(binaryOperatorExpression))
						op = BinaryOperatorType.ReferenceEquality;
					else
						op = BinaryOperatorType.Equality;
					break;
				case CS.BinaryOperatorType.InEquality:
					if (IsReferentialEquality(binaryOperatorExpression))
						op = BinaryOperatorType.ReferenceInequality;
					else
						op = BinaryOperatorType.InEquality;
					break;
				case CS.BinaryOperatorType.LessThan:
					op = BinaryOperatorType.LessThan;
					break;
				case CS.BinaryOperatorType.LessThanOrEqual:
					op = BinaryOperatorType.LessThanOrEqual;
					break;
				case CS.BinaryOperatorType.Add:
					// TODO might be string concatenation
					op = BinaryOperatorType.Add;
					break;
				case CS.BinaryOperatorType.Subtract:
					op = BinaryOperatorType.Subtract;
					break;
				case CS.BinaryOperatorType.Multiply:
					op = BinaryOperatorType.Multiply;
					break;
				case CS.BinaryOperatorType.Divide:
					op = BinaryOperatorType.Divide;
					break;
				case CS.BinaryOperatorType.Modulus:
					op = BinaryOperatorType.Modulus;
					break;
				case CS.BinaryOperatorType.ShiftLeft:
					op = BinaryOperatorType.ShiftLeft;
					break;
				case CS.BinaryOperatorType.ShiftRight:
					op = BinaryOperatorType.ShiftRight;
					break;
				case CS.BinaryOperatorType.NullCoalescing:
					var nullCoalescing = new ConditionalExpression {
						ConditionExpression = left,
						FalseExpression = right
					};
					return EndNode(binaryOperatorExpression, nullCoalescing);
				default:
					throw new Exception("Invalid value for BinaryOperatorType: " + binaryOperatorExpression.Operator);
			}

			var binOp = new BinaryOperatorExpression(left, op, right);
			// User-defined operators (op_Inequality, op_Addition on delegates, ...): the
			// 2.3 engine attached the operator method to the expression, and its formatter
			// emitted it on the operator token - the same shape the C# token writer has.
			// String concatenation binds to a synthetic operator without metadata; leave it
			// to the formatter's delegate/string branches.
			// User-defined operator comparisons/arithmetic: the engine tree keeps the op_
			// call as an InvocationExpression (the C# output layer folds it back to the
			// operator), so the resolve result is a CSharpInvocationResolveResult; a real
			// operator expression carries OperatorResolveResult.UserDefinedOperatorMethod.
			// The 2.3 engine attached the op_ method's MemberRef to the operator token;
			// external declaring types (string, TimeSpan) fail the cecil bridge - the op_
			// method lives in the module's own MemberRef table (2.3 emitted MemberRefMD).
			ICSharpCode.Decompiler.TypeSystem.IMethod? operatorMethod =
				binaryOperatorExpression.Annotation<ICSharpCode.Decompiler.Semantics.OperatorResolveResult>()?.UserDefinedOperatorMethod
				?? binaryOperatorExpression.Annotation<ICSharpCode.Decompiler.CSharp.Resolver.CSharpInvocationResolveResult>()?.Member as ICSharpCode.Decompiler.TypeSystem.IMethod;
			if (operatorMethod is not null) {
				var operatorRef = ToDnlibRef(TryGetCecilRef(operatorMethod));
				if (operatorRef is null && module is ModuleDefMD operatorMd &&
						operatorMethod.DeclaringType is not null)
					operatorRef = Common.DnlibReferenceMapper.FindMemberRefInTable(operatorMd,
						operatorMethod.DeclaringType.FullName, operatorMethod.Name);
				if (operatorRef is not null)
					binOp.AddAnnotation(operatorRef);
			}
			return EndNode(binaryOperatorExpression, binOp);
		}

		bool IsReferentialEquality(CS.BinaryOperatorExpression binaryOperatorExpression)
		{
			var left = provider.IsReferenceType(binaryOperatorExpression.Left);
			var right = provider.IsReferenceType(binaryOperatorExpression.Right);

			var leftCode = provider.ResolveExpression(binaryOperatorExpression.Left);
			var rightCode = provider.ResolveExpression(binaryOperatorExpression.Right);

			if ((left == true || right == true) && (leftCode != TypeCode.String && rightCode != TypeCode.String))
				return true;
			// No annotations in 3.2, so both operands are usually unknown. When the resolver can
			// prove at least one side is a reference type, C# compares references and VB needs Is.
			return typeResolver.IsReferentialComparison(binaryOperatorExpression.Left, binaryOperatorExpression.Right);
		}

		public AstNode VisitCastExpression(CS.CastExpression castExpression, object data)
		{
			// VB cannot convert between Char and a number with CType: it needs ChrW/AscW.
			var targetTypeName = castExpression.Type.ToString();
			var sourceTypeName = typeResolver.GetTypeName(castExpression.Expression);
			if (IsCharTypeName(targetTypeName) && IsNumericTypeName(sourceTypeName))
				return EndNode(castExpression, WrapChrW((Expression)castExpression.Expression.AcceptVisitor(this, data)));
			if (IsNumericTypeName(targetTypeName) && IsCharTypeName(sourceTypeName))
				return EndNode(castExpression, WrapAscW((Expression)castExpression.Expression.AcceptVisitor(this, data)));

			var expr = new CastExpression();

			expr.Type = (AstType)castExpression.Type.AcceptVisitor(this, data);
			// TODO read additional type information from annotation
			// (int)x is equivalent to CInt(Math.Truncate(x))
			expr.CastType = GetCastType(expr.Type, null);
			expr.Expression = (Expression)castExpression.Expression.AcceptVisitor(this, data);

			if (expr.CastType != CastType.CType)
				expr.Type = null;

			return EndNode(castExpression, expr);
		}

		CastType GetCastType(AstType type, object typeInformation)
		{
			var primType = type as PrimitiveType;
			if (primType == null)
				return CastType.CType;

			switch (primType.Keyword) {
				case "Boolean":
					return CastType.CBool;
				case "Byte":
					return CastType.CByte;
				case "Char":
					return CastType.CChar;
				case "Date":
					return CastType.CDate;
				case "Double":
					return CastType.CDbl;
				case "Decimal":
					return CastType.CDec;
				case "Integer":
					return CastType.CInt;
				case "Long":
					return CastType.CLng;
				case "Object":
					return CastType.CObj;
				case "SByte":
					return CastType.CSByte;
				case "Short":
					return CastType.CShort;
				case "Single":
					return CastType.CSng;
				case "String":
					return CastType.CStr;
				case "UInteger":
					return CastType.CUInt;
				case "ULong":
					return CastType.CULng;
				case "UShort":
					return CastType.CUShort;
			}

			return CastType.CType;
		}

		public AstNode VisitCheckedExpression(CS.CheckedExpression checkedExpression, object data)
		{
			if (blocks.Count > 0)
				blocks.Peek().AddChild(new Comment(" The following expression was wrapped in a checked-expression", false), AstNode.Roles.Comment);
			return EndNode(checkedExpression, checkedExpression.Expression.AcceptVisitor(this, data));
		}

		public AstNode VisitConditionalExpression(CS.ConditionalExpression conditionalExpression, object data)
		{
			var cond = new ConditionalExpression() {
				ConditionExpression = (Expression)conditionalExpression.Condition.AcceptVisitor(this, data),
				TrueExpression = (Expression)conditionalExpression.TrueExpression.AcceptVisitor(this, data),
				FalseExpression = (Expression)conditionalExpression.FalseExpression.AcceptVisitor(this, data)
			};

			return EndNode(conditionalExpression, cond);
		}

		public AstNode VisitDefaultValueExpression(CS.DefaultValueExpression defaultValueExpression, object data)
		{
			// Nothing is equivalent to default(T) for reference and value types.
			return EndNode(defaultValueExpression, new PrimitiveExpression(null));
		}

		public AstNode VisitDirectionExpression(CS.DirectionExpression directionExpression, object data)
		{
			return EndNode(directionExpression, (Expression)directionExpression.Expression.AcceptVisitor(this, data));
		}

		public AstNode VisitIdentifierExpression(CS.IdentifierExpression identifierExpression, object data)
		{
			var identifier = Identifier.Create(ResolveIdentifierAnnotations(identifierExpression),
				ResolveLocalName(identifierExpression.Identifier));
			// The 2.3 engine built every implicit-this instance access as an explicit
			// ThisReferenceExpression node (ldarg.0 emitted one with the declaring type's
			// annotation), so its VB output qualified instance members with Me. The 3.2 tree
			// leaves the target implicit; recreate the same display here.
			Expression qualified;
			if (ShouldQualifyWithMe(identifierExpression)) {
				var me = new InstanceExpression(InstanceExpressionType.Me, ICSharpCode.NRefactory.TextLocation.Empty);
				if (FindEnclosingType(identifierExpression) is { } enclosingType)
					me.AddAnnotation(enclosingType);
				var memberAccess = new MemberAccessExpression { Target = me, MemberName = identifier };
				ConvertNodes(identifierExpression.TypeArguments, memberAccess.TypeArguments);
				qualified = memberAccess;
			}
			else {
				var expr = new IdentifierExpression();
				expr.Identifier = identifier;
				ConvertNodes(identifierExpression.TypeArguments, expr.TypeArguments);
				qualified = expr;
			}
			AppendExplicitTypeArgumentsIfNeeded(identifierExpression, qualified);
			if (provider.IsMethodGroup(identifierExpression)) {
				// Method group passed as an argument ('AddressOf TimerHandler...'): the 2.3
				// engine attached the method reference to the name. The resolve chain yields
				// nothing for these (no invocation yet); the enclosing type's member match is
				// the same navigation reference.
				if (!identifier.Annotations.OfType<dnlib.DotNet.IMemberDef>().Any() &&
						FindCurrentTypeMember(identifier.Name) is { } methodGroupMember &&
						methodGroupMember is MethodDef or PropertyDef or EventDef)
					identifier.AddAnnotation(methodGroupMember);
				return EndNode(identifierExpression, new UnaryOperatorExpression(UnaryOperatorType.AddressOf, qualified));
			}

			return EndNode(identifierExpression, qualified);
		}

		/// <summary>
		/// Explicit 'Of T' for a bare generic call target the 3.2 C# engine printed without
		/// type arguments (C# re-infers them): VB cannot always re-infer, and a Me-qualified
		/// generic call inside a lambda loses inference outright (BC32050). When the resolved
		/// method's inferred arguments are all method type parameters - which are in lexical
		/// scope - emit them explicitly, like the 2.3 engine did ('Create(Of T)()').
		/// </summary>
		void AppendExplicitTypeArgumentsIfNeeded(CS.IdentifierExpression identifierExpression, Expression target) {
			var typeArguments = target switch {
				IdentifierExpression identifier => identifier.TypeArguments,
				MemberAccessExpression memberAccess => memberAccess.TypeArguments,
				_ => null,
			};
			if (typeArguments is null ||
					identifierExpression.TypeArguments.Count > 0 ||
					!(identifierExpression.Parent is CS.InvocationExpression invocation) ||
					invocation.Target != identifierExpression ||
					typeArguments.Count > 0)
				return;
			if (invocation.Annotation<ICSharpCode.Decompiler.Semantics.ResolveResult>() is not ICSharpCode.Decompiler.CSharp.Resolver.CSharpInvocationResolveResult invRR ||
					invRR.Member is not ICSharpCode.Decompiler.TypeSystem.IMethod genericMethod ||
					genericMethod.TypeParameters.Count == 0 ||
					genericMethod.TypeParameters.Count != genericMethod.TypeArguments.Count)
				return;
			foreach (var arg in genericMethod.TypeArguments) {
				if (arg is ICSharpCode.Decompiler.TypeSystem.ITypeParameter tp &&
						tp.OwnerType == ICSharpCode.Decompiler.TypeSystem.SymbolKind.Method)
					typeArguments.Add(new SimpleType(
						new object[] {
							(object?)FindGenericParameterReference(tp.Name, identifierExpression)
								?? GenericParameterColorByName(tp.Name, identifierExpression)
								?? BoxedTextColor.TypeGenericParameter,
						},
						tp.Name));
				else
					return;
			}
		}

		/// <summary>
		/// True when an identifier usage refers to an instance member through the implicit
		// this (no explicit target node): the resolver hangs a MemberResolveResult on the
		// identifier itself, or - for a bare call target - on the enclosing invocation.
		/// Static members keep their plain form (VB needs ClassName.Member for those).
		/// </summary>
		bool ShouldQualifyWithMe(CS.IdentifierExpression usage) {
			ICSharpCode.Decompiler.Semantics.MemberResolveResult? rr;
			if (usage.Parent is CS.InvocationExpression invocation && invocation.Target == usage) {
				rr = usage.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>()
					?? invocation.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>();
				// The event field itself is an instance member, but the converter rewrites
				// its raise into RaiseEvent with a plain name - leave the target alone.
				if (rr?.Member is ICSharpCode.Decompiler.TypeSystem.IEvent)
					return false;
			}
			else {
				rr = usage.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>();
			}
			if (rr?.Member is not { IsStatic: false } member)
				return false;
			if (IsInsideInitializer(usage))
				return false;
			var kind = member.DeclaringType?.GetDefinition()?.Kind;
			// Interface members come through explicit interface calls elsewhere, and a
			// delegate's Invoke behind a delegate-valued field/local invocation
			// ('this.dField(...)') is not a real instance access - VB needs the bare
			// delegate name there (Me inside a Module is BC32001 outright).
			if (kind is ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface or ICSharpCode.Decompiler.TypeSystem.TypeKind.Delegate)
				return false;
			return true;
		}

		/// <summary>
		/// True when the node sits in a field/property/event initializer: VB rejects Me there
		/// (BC30043 in instance initializers, BC32001 inside a Module, BC30369 for shared
		/// ones), so implicit-this references keep their bare form. The first declaration
		/// above the node decides: method bodies (locals included) come after a method node,
		/// initializers come after the field/property node.
		/// </summary>
		static bool IsInsideInitializer(CS.AstNode node) {
			for (var n = node; n is not null; n = n.Parent) {
				switch (n) {
				case CS.FieldDeclaration:
				case CS.PropertyDeclaration:
				case CS.EventDeclaration:
					return true;
				case CS.MethodDeclaration:
				case CS.ConstructorDeclaration:
				case CS.OperatorDeclaration:
				case CS.Accessor:
				case CS.AnonymousMethodExpression:
				case CS.LambdaExpression:
					return false;
				}
			}
			return false;
		}

		/// <summary>
		/// The dnlib type of the innermost enclosing (decompiled) type declaration - the
		/// reference the 2.3 engine attached to ThisReferenceExpression via the method's
		/// declaring type.
		/// </summary>
		TypeDef? FindEnclosingType(CS.AstNode node) {
			for (var n = node; n is not null; n = n.Parent)
				if (n.Annotation<TypeDef>() is { } typeDef)
					return typeDef;
			return null;
		}
		/// <summary>
		/// Annotations for a converted identifier usage. The VB output stage colors an
		/// identifier by its FIRST object annotation (MetadataTextColorProvider.GetColor), so
		/// the decisive annotation must come first: a dnlib reference from the engine's resolve
		/// results, then the IL variable's dnlib parameter/local, then a syntactic fallback
		/// color. Engine resolve-result objects are dropped - they color as Text and hide the
		/// useful annotations behind them.
		/// </summary>
		System.Collections.Generic.IEnumerable<object> ResolveIdentifierAnnotations(CS.IdentifierExpression identifierExpression) {
			object? decisive = ResolveDnlibReference(identifierExpression);
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1" && identifierExpression.Identifier == "num" && decisive is null) {
				var ilv = ICSharpCode.Decompiler.CSharp.AnnotationExtensions.GetILVariable(identifierExpression);
				Console.Error.WriteLine("NUM-DIAG ilv=" + (ilv is null ? "NULL" : ilv.Kind + "#" + ilv.Index) + " method=" + (FindEnclosingMethod(identifierExpression)?.Name ?? "null"));
			}
			if (decisive is null) {
				var ilv = ICSharpCode.Decompiler.CSharp.AnnotationExtensions.GetILVariable(identifierExpression);
				if (ilv is not null) {
					decisive = ResolveILVariableReference(ilv, FindEnclosingMethod(identifierExpression))
						?? ILVariableFallbackColor(ilv);
				}
			}
			if (decisive is null)
				decisive = SyntacticUsageColor(identifierExpression);
			if (decisive is null) {
				foreach (var annotation in identifierExpression.IdentifierToken.Annotations)
					yield return annotation;
				yield break;
			}
			yield return decisive;
			foreach (var annotation in identifierExpression.IdentifierToken.Annotations) {
				if (IsEngineResolveResult(annotation))
					continue;
				yield return annotation;
			}
		}

		static bool IsEngineResolveResult(object annotation) =>
			annotation is ICSharpCode.Decompiler.Semantics.ResolveResult ||
			annotation is ICSharpCode.Decompiler.TypeSystem.IType;

		/// <summary>
		/// Maps the engine's IL variable to its dnlib parameter (by name) or local (by body
		/// index), giving parameter and local usages the same color and navigation reference
		/// the 2.3 engine emits.
		/// </summary>
		object? ResolveILVariableReference(ICSharpCode.Decompiler.IL.ILVariable ilv, MethodDef? method) {
			if (ilv.Kind == ICSharpCode.Decompiler.IL.VariableKind.Parameter && method is not null) {
				foreach (var parameter in method.Parameters) {
					if (parameter.Name == ilv.Name)
						return parameter;
				}
				// Parameter names can be missing; fall back to position (the engine's index
				// includes the implicit this, matching dnlib MethodDef.Parameters).
				if (ilv.Index >= 0 && ilv.Index < method.Parameters.Count)
					return method.Parameters[ilv.Index];
				// A lambda parameter belongs to the compiled lambda method, not the host
				// method - the 2.3 engine attached that hidden method's dnlib Param, so the
				// declaration highlights together with its usages and navigates there.
				var lambdaParam = ResolveLambdaParameter(ilv);
				if (lambdaParam is not null)
					return lambdaParam;
				return null;
			}
			if ((ilv.Kind == ICSharpCode.Decompiler.IL.VariableKind.Local ||
					ilv.Kind == ICSharpCode.Decompiler.IL.VariableKind.PinnedLocal ||
					ilv.Kind == ICSharpCode.Decompiler.IL.VariableKind.UsingLocal ||
					ilv.Kind == ICSharpCode.Decompiler.IL.VariableKind.ForeachLocal) &&
					method?.Body is not null) {
				var variables = method.Body.Variables;
				if (ilv.Index >= 0 && ilv.Index < variables.Count)
					return variables[ilv.Index];
			}
			// Captured/hoisted variables have no IL slot in this method (the engine promotes
			// them to display-class fields and hides the display class). Synthesize a
			// SourceLocal cached per variable instance so all occurrences share one reference
			// and dnSpy highlights them together - the 2.3 engine emitted a SourceLocal (with
			// the hoisted field set) for the same sites.
			if (hoistedSourceLocals.TryGetValue(ilv, out var hoisted))
				return hoisted;
			var synthesized = new SourceLocal(null, ilv.Name ?? string.Empty,
				module.CorLibTypes.Object, SourceVariableFlags.None);
			hoistedSourceLocals[ilv] = synthesized;
			return synthesized;
		}

		readonly Dictionary<ICSharpCode.Decompiler.IL.ILVariable, SourceLocal> hoistedSourceLocals = new Dictionary<ICSharpCode.Decompiler.IL.ILVariable, SourceLocal>();

		/// <summary>Parameter/local color for variables whose dnlib object could not be mapped.</summary>
		static object ILVariableFallbackColor(ICSharpCode.Decompiler.IL.ILVariable ilv) =>
			ilv.Kind == ICSharpCode.Decompiler.IL.VariableKind.Parameter
				? BoxedTextColor.Parameter
				: BoxedTextColor.Local;

		/// <summary>Nearest enclosing member with a body, through the accessor chain.</summary>
		MethodDef? FindEnclosingMethod(CS.AstNode node) {
			for (var n = node; n is not null; n = n.Parent) {
				if (n.Annotation<MethodDef>() is { } methodDef)
					return methodDef;
			}
			return null;
		}

		/// <summary>
		/// Syntactic fallback color for an identifier usage the resolver could not annotate:
		/// bare calls are methods, generic parameters in scope, then parameter names, then locals.
		/// </summary>
		object? SyntacticUsageColor(CS.IdentifierExpression usage) {
			var name = usage.IdentifierToken.Name;
			if (usage.Parent is CS.NamespaceDeclaration)
				return BoxedTextColor.Namespace;
			if (usage.Parent is CS.InvocationExpression invocation && invocation.Target == usage)
				return MethodColorByName(name, false);
			// Method group passed as an argument: 'New Thread(Method)', 'UI(handler)'. A
			// variable argument is indistinguishable syntactically, so parameter names win.
			if (usage.Role == CS.Roles.Argument &&
					(usage.Parent is CS.ObjectCreateExpression ||
					 (usage.Parent is CS.InvocationExpression invocationArgument && invocationArgument.Target != usage)) &&
					!IsParameterName(name, usage) &&
					GenericParameterColorByName(name, usage) is null)
				return BoxedTextColor.InstanceMethod;
			var genericColor = GenericParameterColorByName(name, usage);
			if (genericColor != null)
				return genericColor;
			if (IsParameterName(name, usage))
				return BoxedTextColor.Parameter;
			return BoxedTextColor.Local;
		}

		public AstNode VisitIndexerExpression(CS.IndexerExpression indexerExpression, object data)
		{
			Expression target = (Expression)indexerExpression.Target.AcceptVisitor(this, data);
			// VB rejects indexing a base reference directly ("MyBase(i)"); base indexers must
			// be accessed through the Item property: "MyBase.Item(i)".
			if (indexerExpression.Target is CS.BaseReferenceExpression) {
				target = new MemberAccessExpression {
					Target = target,
					MemberName = Identifier.Create((object)null, "Item")
				};
			}
			var expr = new InvocationExpression(target);
			ConvertNodes(indexerExpression.Arguments, expr.Arguments);
			return EndNode(indexerExpression, expr);
		}

		public AstNode VisitInvocationExpression(CS.InvocationExpression invocationExpression, object data)
		{
			var expr = new InvocationExpression((Expression)invocationExpression.Target.AcceptVisitor(this, data));
			ConvertNodes(invocationExpression.Arguments, expr.Arguments);
			return EndNode(invocationExpression, expr);
		}

		public AstNode VisitIsExpression(CS.IsExpression isExpression, object data)
		{
			var expr = new TypeOfIsExpression() {
				Type = (AstType)isExpression.Type.AcceptVisitor(this, data),
				TypeOfExpression = (Expression)isExpression.Expression.AcceptVisitor(this, data)
			};

			return EndNode(isExpression, expr);
		}

		public AstNode VisitLambdaExpression(CS.LambdaExpression lambdaExpression, object data)
		{
			LambdaExpression expr = null;

			if (lambdaExpression.Body is CS.Expression) {
				var singleLine = new SingleLineFunctionLambdaExpression() {
					EmbeddedExpression = (Expression)lambdaExpression.Body.AcceptVisitor(this, data)
				};
				ConvertNodes(lambdaExpression.Parameters, singleLine.Parameters);
				expr = singleLine;
			} else
				throw new NotImplementedException();

			return EndNode(lambdaExpression, expr);
		}

		public AstNode VisitMemberReferenceExpression(CS.MemberReferenceExpression memberReferenceExpression, object data)
		{
			var memberAccessExpression = new MemberAccessExpression();

			memberAccessExpression.Target = (Expression)memberReferenceExpression.Target.AcceptVisitor(this, data);
			memberAccessExpression.MemberName = Identifier.Create(memberReferenceExpression.MemberNameToken.Annotations, memberReferenceExpression.MemberName);
			var memberRef = ResolveDnlibReference(memberReferenceExpression) ?? (object?)memberReferenceExpression.Annotation<IMemberRef>();
			if (memberRef is not null)
				memberAccessExpression.MemberName.AddAnnotation(memberRef);
			else if (memberReferenceExpression.Parent is CS.InvocationExpression invocationOfMember &&
					invocationOfMember.Annotation<ICSharpCode.Decompiler.Semantics.ResolveResult>() is ICSharpCode.Decompiler.Semantics.InvocationResolveResult invocationRR &&
					invocationRR.Member != null &&
					ToDnlibRef(TryGetCecilRef(invocationRR.Member)) is { } invokedRef) {
				// 'items.Add(...)': the engine annotates the invocation, not the member
				// reference it targets; resolve the invoked member from there (the same probe
				// the C# token writer uses). A member usage that resolved on its own (fields,
				// properties) already returned above. Only the reference is attached - the
				// renderer's color pipeline handles the kind color, which matched the 2.3
				// engine on every checked site except property getters (recorded in the audit).
				memberAccessExpression.MemberName.AddAnnotation(invokedRef);
			}
			else if (FindCurrentTypeMember(memberReferenceExpression.MemberName) is { } sameTypeMember)
				memberAccessExpression.MemberName.AddAnnotation(sameTypeMember);
			else if (MemberUsageFallbackColor(memberReferenceExpression) is { } fallbackColor)
				memberAccessExpression.MemberName.AddAnnotation(fallbackColor);
			bool isExtensionMethod = false;
			if (memberReferenceExpression.Parent is CS.InvocationExpression invocExpr) {
				// The engine leaves no dnlib method annotation here; without it the extension
				// call keeps its target form, which the VB renderer writes as an instance call.
				var method = invocExpr.Annotation<dnlib.DotNet.IMethod>()?.ResolveMethodDef();
				isExtensionMethod = method?.CustomAttributes.IsDefined("System.Runtime.CompilerServices.ExtensionAttribute") == true;
			}
			if (!isExtensionMethod)
				ConvertNodes(memberReferenceExpression.TypeArguments, memberAccessExpression.TypeArguments);
			if (provider.IsMethodGroup(memberReferenceExpression)) {
				return EndNode(memberReferenceExpression, new UnaryOperatorExpression(UnaryOperatorType.AddressOf, memberAccessExpression));
			}

			return EndNode(memberReferenceExpression, memberAccessExpression);
		}

		/// <summary>
		/// Annotation for a parameter declaration name: the enclosing method's dnlib parameter
		/// (matched by name) when it belongs to the method itself, else the Parameter color for
		/// lambda/catch parameters.
		/// </summary>
		object? ResolveParameterAnnotation(CS.ParameterDeclaration parameterDeclaration) {
			if (FindEnclosingMethod(parameterDeclaration)?.Parameters.FirstOrDefault(p => p.Name == parameterDeclaration.Name) is { } parameter)
				return parameter;
			// Lambda parameters: resolve through the compiled lambda method's dnlib Param.
			// The engine hangs an ILVariableResolveResult on lambda parameters (MakeParameters).
			var ilv = parameterDeclaration.Annotation<ICSharpCode.Decompiler.IL.ILVariable>()
				?? (parameterDeclaration.Annotation<ICSharpCode.Decompiler.Semantics.ResolveResult>() as ICSharpCode.Decompiler.CSharp.ILVariableResolveResult)?.Variable;
			if (ilv is not null) {
				var lambdaParam = ResolveLambdaParameter(ilv);
				if (lambdaParam is not null)
					return lambdaParam;
			}
			// Lambdas inside field initializers have no enclosing method (no MethodDef in the
			// ancestor chain), but the LambdaExpression itself carries the engine's ILFunction
			// annotation (WithILInstruction) - the same compiled-method mapping by position.
			if (parameterDeclaration.Parent is CS.LambdaExpression lambdaExpr &&
					lambdaExpr.Annotation<ICSharpCode.Decompiler.IL.ILFunction>() is { } lambdaFn2 &&
					lambdaFn2.CecilMethod is { } cecilLambda2 &&
					module.ResolveToken(new MDToken(cecilLambda2.MetadataToken.ToInt32())) is MethodDef lambdaDef2) {
				var position = 0;
				var found2 = false;
				foreach (var pd in lambdaExpr.Parameters) {
					if (pd == parameterDeclaration) {
						found2 = true;
						break;
					}
					position++;
				}
				position += lambdaDef2.HasThis ? 1 : 0;
				if (found2 && position < lambdaDef2.Parameters.Count)
					return lambdaDef2.Parameters[position];
			}
			return BoxedTextColor.Parameter;
		}

		/// <summary>
		/// The dnlib Param of the compiled lambda method for a lambda's ILVariable - cached
		/// per variable so the declaration and every usage share one reference instance
		/// (dnSpy matches references by identity/SigComparer for highlight grouping).
		/// </summary>
		dnlib.DotNet.Parameter? ResolveLambdaParameter(ICSharpCode.Decompiler.IL.ILVariable ilv) {
			if (ilv.Kind != ICSharpCode.Decompiler.IL.VariableKind.Parameter || ilv.Index < 0)
				return null;
			if (lambdaParameters is null)
				lambdaParameters = new Dictionary<ICSharpCode.Decompiler.IL.ILVariable, dnlib.DotNet.Parameter>();
			if (lambdaParameters.TryGetValue(ilv, out var cached))
				return cached;
			dnlib.DotNet.Parameter? found = null;
			// ILFunction.CecilMethod is the compiled lambda method (<M>b__N) - a real module
			// method. dnlib's Parameters include the hidden this at index 0 for instance
			// methods, while the engine's lambda parameter index counts only the explicit
			// parameters - shift by one when the method is instance.
			if (ilv.Function?.CecilMethod is { } cecilLambda &&
					module.ResolveToken(new MDToken(cecilLambda.MetadataToken.ToInt32())) is MethodDef lambdaDef) {
				var paramIndex = ilv.Index + (lambdaDef.HasThis ? 1 : 0);
				if (paramIndex >= 0 && paramIndex < lambdaDef.Parameters.Count)
					found = lambdaDef.Parameters[paramIndex];
			}
			lambdaParameters[ilv] = found;
			return found;
		}

		Dictionary<ICSharpCode.Decompiler.IL.ILVariable, dnlib.DotNet.Parameter>? lambdaParameters;

		/// <summary>
		/// Metadata definition the binder attached to an entity declaration; the output stage
		/// colors the name by the FIRST identifier annotation, so the definition must come
		/// first (its kind color and navigation reference match the 2.3 engine).
		/// </summary>
		object? ResolveDefinitionNameAnnotation(CS.EntityDeclaration entity) =>
			(object?)entity.Annotation<MethodDef>() ?? (object?)entity.Annotation<PropertyDef>() ??
			(object?)entity.Annotation<EventDef>() ?? (object?)entity.Annotation<FieldDef>() ??
			(object?)entity.Annotation<TypeDef>();

		/// <summary>
		/// The enclosing type's member with this name, for member usages whose engine
		/// resolve results are missing - attaches the same navigation reference the
		/// 2.3 engine emitted.
		/// </summary>
		IMemberDef? FindCurrentTypeMember(string name) {
			var type = typeResolver.CurrentType;
			if (type is null || string.IsNullOrEmpty(name))
				return null;
			IMemberDef? found = type.Methods.FirstOrDefault(m => m.Name == name);
			if (found is null)
				found = type.Properties.FirstOrDefault(p => p.Name == name);
			if (found is null)
				found = type.Events.FirstOrDefault(ev => ev.Name == name);
			if (found is null) {
				foreach (var field in type.Fields) {
					if (field.Name == name) {
						found = field;
						break;
					}
				}
			}
			return found;
		}

		/// <summary>
		/// Syntactic fallback color for a member usage the resolver could not annotate:
		/// invoked members are methods, others are fields or properties; a type-name target
		/// ('Console.WriteLine') implies a static member.
		/// </summary>
		object MemberUsageFallbackColor(CS.MemberReferenceExpression memberRef) {
			bool invoked = memberRef.Parent is CS.InvocationExpression inv && inv.Target == memberRef;
			bool targetIsTypeName = memberRef.Target is CS.TypeReferenceExpression;
			if (invoked)
				return targetIsTypeName ? BoxedTextColor.StaticMethod : MethodColorByName(memberRef.MemberName, false);
			return targetIsTypeName ? BoxedTextColor.InstanceField : BoxedTextColor.InstanceProperty;
		}

		/// <summary>
		/// Instance/static color for a method usage: when a same-named method of the enclosing
		/// type settles it, use that; else fall back to the type-name heuristic.
		/// </summary>
		object MethodColorByName(string name, bool targetIsTypeName) {
			var methods = typeResolver.CurrentType?.Methods?.Where(m => m.Name == name).ToList();
			if (methods is not null && methods.Count > 0) {
				if (methods.All(m => m.IsStatic))
					return BoxedTextColor.StaticMethod;
				if (methods.All(m => !m.IsStatic))
					return BoxedTextColor.InstanceMethod;
			}
			return targetIsTypeName ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
		}

		public AstNode VisitNamedArgumentExpression(CS.NamedArgumentExpression namedArgumentExpression, object data)
		{
			Expression expr = new NamedArgumentExpression {
				Identifier = Identifier.Create(namedArgumentExpression.NameToken.Annotations, namedArgumentExpression.Name),
				Expression = (Expression)namedArgumentExpression.Expression.AcceptVisitor(this, data)
			};

			return EndNode(namedArgumentExpression, expr);
		}

		public AstNode VisitNamedExpression(CS.NamedExpression namedExpression, object data)
		{
			// Attribute arguments use "Name := value" in VB. The ".Name = value" form
			// (FieldInitializerExpression) is only valid inside an object initializer, and
			// emitting it inside "<...>" makes the VB parser fall back to XML literals.
			// The member name keeps its resolved reference (property/field of the object being
			// initialized) or at least the InstanceProperty color - matching the 2.3 engine.
			var memberAnnotations = ComposeAnnotations(
				ResolveDnlibReference(namedExpression) ?? ResolveDnlibReference(namedExpression.NameToken) ?? (object)BoxedTextColor.InstanceProperty,
				namedExpression.NameToken.Annotations);
			if (namedExpression.Parent is CS.Attribute) {
				return EndNode(namedExpression, new NamedArgumentExpression {
					Identifier = Identifier.Create(memberAnnotations, namedExpression.Name),
					Expression = (Expression)namedExpression.Expression.AcceptVisitor(this, data)
				});
			}

			Expression expr = new FieldInitializerExpression {
				IsKey = true,
				Identifier = Identifier.Create(memberAnnotations, namedExpression.Name),
				Expression = (Expression)namedExpression.Expression.AcceptVisitor(this, data)
			};
			return EndNode(namedExpression, expr);
		}

		public AstNode VisitNullReferenceExpression(CS.NullReferenceExpression nullReferenceExpression, object data)
		{
			return EndNode(nullReferenceExpression, new PrimitiveExpression(null));
		}

		public AstNode VisitObjectCreateExpression(CS.ObjectCreateExpression objectCreateExpression, object data)
		{
			var expr = new ObjectCreationExpression((AstType)objectCreateExpression.Type.AcceptVisitor(this, data));
			// The 2.3 engine attached the constructor's MemberRef to the type name token of
			// an object creation; the output stage reads it from this node (the formatter
			// redirects a Type-role child of ObjectCreationExpression to its parent), so the
			// ctor ref must live here. External ctors fall back to the module's MemberRef
			// table (constructor name is always .ctor).
			var ctorRef = ResolveCtorRefForCreation(objectCreateExpression);
			if (ctorRef is not null)
				expr.AddAnnotation(ctorRef);
			ConvertNodes(objectCreateExpression.Arguments, expr.Arguments);
			// "new Delegate(M)" is just "AddressOf M" in VB, but only when the type really is a
			// delegate: "new Lazy(Of T)(M)" must keep its constructor call (BC30581).
			var arg1 = expr.Arguments.FirstOrDefault() as UnaryOperatorExpression;
			if (arg1 != null && arg1.Operator == UnaryOperatorType.AddressOf &&
					typeResolver.IsDelegateType(objectCreateExpression.Type.ToString())) {
				arg1.Remove();
				return EndNode(objectCreateExpression, arg1);
			}
			if (!objectCreateExpression.Initializer.IsNull)
				expr.Initializer = (ArrayInitializerExpression)objectCreateExpression.Initializer.AcceptVisitor(this, data);

			return EndNode(objectCreateExpression, expr);
		}

		public AstNode VisitAnonymousTypeCreateExpression(CS.AnonymousTypeCreateExpression anonymousTypeCreateExpression, object data)
		{
			var expr = new AnonymousObjectCreationExpression();

			ConvertNodes(anonymousTypeCreateExpression.Initializers, expr.Initializer);

			return EndNode(anonymousTypeCreateExpression, expr);
		}

		public AstNode VisitParenthesizedExpression(CS.ParenthesizedExpression parenthesizedExpression, object data)
		{
			var expr = parenthesizedExpression.Expression;
			if (expr is CS.CastExpression || expr is CS.AsExpression || expr is CS.TypeOfExpression || expr is CS.ConditionalExpression || expr is CS.ParenthesizedExpression)
				return expr.AcceptVisitor(this, data);

			var result = new ParenthesizedExpression();

			result.Expression = (Expression)expr.AcceptVisitor(this, data);

			return EndNode(parenthesizedExpression, result);
		}

		public AstNode VisitPointerReferenceExpression(CS.PointerReferenceExpression pointerReferenceExpression, object data)
		{
			return EndNode(pointerReferenceExpression, ((Expression)pointerReferenceExpression.Target.AcceptVisitor(this, data)).Invoke2(BoxedTextColor.InstanceMethod, "Dereference").Member(pointerReferenceExpression.MemberNameToken.Annotation<object>(), pointerReferenceExpression.MemberName));
		}

		public AstNode VisitPrimitiveExpression(CS.PrimitiveExpression primitiveExpression, object data)
		{
			Expression expr;

			if (!string.IsNullOrEmpty(primitiveExpression.Value as string))
				expr = ConvertToConcat(primitiveExpression.Value.ToString());
			else if (primitiveExpression.Value is char)
				expr = ConvertToSpecialChar((char)primitiveExpression.Value);
			else if (primitiveExpression.Value is long && (long)primitiveExpression.Value == long.MinValue)
				expr = CreateLongMinValueReference();
			else if (primitiveExpression.Value is double infinity
					&& (double.IsInfinity(infinity) || double.IsNaN(infinity))) {
				// The 3.2 engine folds double.PositiveInfinity/NegativeInfinity/NaN corlib
				// constants into primitive expressions; the VB output visitor then renders the
				// value as Double.PositiveInfinity with no references. The 2.3 engine kept the
				// member-access shape with the field reference - synthesize the same here.
				expr = CreateInfinityMemberAccess(infinity);
			}
			else if (primitiveExpression.Value is float singleInfinity
					&& (float.IsInfinity(singleInfinity) || float.IsNaN(singleInfinity))) {
				expr = CreateInfinityMemberAccess(singleInfinity);
			}
			else
				expr = new PrimitiveExpression(primitiveExpression.Value);

			return EndNode(primitiveExpression, expr);
		}

		/// <summary>
		/// Double.PositiveInfinity / Single.NaN shape for the folded corlib constants: a
		/// member access whose target primitive keyword carries the corlib TypeRef and whose
		/// field name carries a FieldRefUser - the same references the 2.3 engine emitted
		/// (the keyword navigates to the corlib type, the name to the field).
		/// </summary>
		Expression CreateInfinityMemberAccess(object value) {
			bool isDouble = value is double;
			string keywordName = isDouble ? "Double" : "Single";
			string fieldName;
			if (value is double dd) {
				if (double.IsPositiveInfinity(dd))
					fieldName = "PositiveInfinity";
				else if (double.IsNegativeInfinity(dd))
					fieldName = "NegativeInfinity";
				else
					fieldName = "NaN";
			}
			else {
				var ff = (float)value;
				if (float.IsPositiveInfinity(ff))
					fieldName = "PositiveInfinity";
				else if (float.IsNegativeInfinity(ff))
					fieldName = "NegativeInfinity";
				else
					fieldName = "NaN";
			}
			var typeRef = module.CorLibTypes.GetTypeRef("System", isDouble ? "Double" : "Single");
			var fieldRef = new MemberRefUser(module, fieldName,
				new FieldSig(isDouble ? module.CorLibTypes.Double : module.CorLibTypes.Single), typeRef);
			var target = new PrimitiveType(keywordName);
			target.AddAnnotation(typeRef);
			target.AddAnnotation(BoxedTextColor.Keyword);
			return new MemberAccessExpression {
				Target = new TypeReferenceExpression(target),
				MemberName = Identifier.Create(new object[] { fieldRef, BoxedTextColor.InstanceField }, fieldName),
			};
		}

		Expression CreateLongMinValueReference()
		{
			// VB applies unary minus after the literal and decimal literals cannot wrap,
			// so C#'s special-cased "-9223372036854775808L" overflows Long when parsed
			// (BC30036). Emit the constant as a member reference instead; the 2.3
			// engine's VB output used the same form.
			return new MemberAccessExpression {
				Target = new TypeReferenceExpression(new PrimitiveType("Long")),
				MemberName = Identifier.CreateLiteralField("MinValue")
			};
		}

		Expression ConvertToConcat(string literal)
		{
			Stack<Expression> parts = new Stack<Expression>();
			int start = 0;

			for (int i = 0; i < literal.Length; i++) {
				string part;
				switch (literal[i]) {
					case '\0':
					case '\b':
					case '\f':
					case '\n':
					case '\t':
					case '\v':
					case '\u0085':
					case '\u2028':
					case '\u2029':
						part = literal.Substring(start, i - start);
						if (!string.IsNullOrEmpty(part))
							parts.Push(new PrimitiveExpression(part));
						parts.Push(ConvertToSpecialChar(literal[i]));
						start = i + 1;
						break;
					case '\r':
						part = literal.Substring(start, i - start);
						if (!string.IsNullOrEmpty(part))
							parts.Push(new PrimitiveExpression(part));
						if (i + 1 < literal.Length && literal[i + 1] == '\n') {
							i++;
							parts.Push(new IdentifierExpression(Identifier.CreateLiteralField("vbCrLf")));
						} else
							parts.Push(new IdentifierExpression(Identifier.CreateLiteralField("vbCr")));
						start = i + 1;
						break;
					default:
						if (char.IsControl(literal[i])) {
							part = literal.Substring(start, i - start);
							if (!string.IsNullOrEmpty(part))
								parts.Push(new PrimitiveExpression(part));
							parts.Push(new InvocationExpression(new IdentifierExpression(Identifier.Create(BoxedTextColor.StaticMethod, "ChrW")), new PrimitiveExpression((int)literal[i])));
						} else
							continue;
						start = i + 1;
						break;
				}

			}

			if (start < literal.Length) {
				string part = literal.Substring(start);
				parts.Push(new PrimitiveExpression(part));
			}

			Expression current = parts.Pop();

			while (parts.Any())
				current = new BinaryOperatorExpression(parts.Pop(), BinaryOperatorType.Concat, current);

			return current;
		}

		Expression ConvertToSpecialChar(char ch)
		{
			switch (ch) {
				case '\0':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbNullChar"));
				case '\b':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbBack"));
				case '\f':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbFormFeed"));
				case '\r':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbCr"));
				case '\n':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbLf"));
				case '\t':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbTab"));
				case '\v':
					return new IdentifierExpression(Identifier.CreateLiteralField("vbVerticalTab"));
				default:
					if (char.IsControl(ch))
						return new InvocationExpression(new IdentifierExpression(Identifier.Create(BoxedTextColor.StaticMethod, "ChrW")), new PrimitiveExpression((int)ch));
					return new PrimitiveExpression(ch);
			}
		}

		public AstNode VisitSizeOfExpression(CS.SizeOfExpression sizeOfExpression, object data)
		{
			return EndNode(
				sizeOfExpression,
				new InvocationExpression(
					new IdentifierExpression() { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__SizeOf") },
					new TypeReferenceExpression((AstType)sizeOfExpression.Type.AcceptVisitor(this, data))
				)
			);
		}

		public AstNode VisitStackAllocExpression(CS.StackAllocExpression stackAllocExpression, object data)
		{
			return EndNode(
				stackAllocExpression,
				new InvocationExpression(
					new IdentifierExpression() { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__StackAlloc") },
					new TypeReferenceExpression((AstType)stackAllocExpression.Type.AcceptVisitor(this, data)),
					(Expression)stackAllocExpression.CountExpression.AcceptVisitor(this, data)
				)
			);
		}

		public AstNode VisitThisReferenceExpression(CS.ThisReferenceExpression thisReferenceExpression, object data)
		{
			InstanceExpression result = new InstanceExpression(InstanceExpressionType.Me, ToNRefactoryLocation(thisReferenceExpression.StartLocation));
			// The 2.3 engine annotated the C# this node with the method's declaring type, so
			// its formatter emitted the Me keyword with that reference; the formatter side
			// (node is InstanceExpression) is already ported - supply the annotation.
			if (FindEnclosingType(thisReferenceExpression) is { } enclosingType)
				result.AddAnnotation(enclosingType);
			return EndNode(thisReferenceExpression, result);
		}

		public AstNode VisitTypeOfExpression(CS.TypeOfExpression typeOfExpression, object data)
		{
			var expr = new GetTypeExpression();
			expr.Type = (AstType)typeOfExpression.Type.AcceptVisitor(this, data);
			return EndNode(typeOfExpression, expr);
		}

		public AstNode VisitTypeReferenceExpression(CS.TypeReferenceExpression typeReferenceExpression, object data)
		{
			var expr = new TypeReferenceExpression((AstType)typeReferenceExpression.Type.AcceptVisitor(this, data));
			return EndNode(typeReferenceExpression, expr);
		}

		public AstNode VisitUnaryOperatorExpression(CS.UnaryOperatorExpression unaryOperatorExpression, object data)
		{
			Expression expr;

			switch (unaryOperatorExpression.Operator) {
				case CS.UnaryOperatorType.Not:
				case CS.UnaryOperatorType.BitNot:
					expr = new UnaryOperatorExpression() {
						Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
						Operator = UnaryOperatorType.Not
					};
					break;
				case CS.UnaryOperatorType.Minus:
					expr = new UnaryOperatorExpression() {
						Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
						Operator = UnaryOperatorType.Minus
					};
					break;
				case CS.UnaryOperatorType.Plus:
					expr = new UnaryOperatorExpression() {
						Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
						Operator = UnaryOperatorType.Plus
					};
					break;
				case CS.UnaryOperatorType.Increment:
					expr = ConvertIncrement(unaryOperatorExpression, data, true, false);
					break;
				case CS.UnaryOperatorType.PostIncrement:
					expr = ConvertIncrement(unaryOperatorExpression, data, true, true);
					break;
				case CS.UnaryOperatorType.Decrement:
					expr = ConvertIncrement(unaryOperatorExpression, data, false, false);
					break;
				case CS.UnaryOperatorType.PostDecrement:
					expr = ConvertIncrement(unaryOperatorExpression, data, false, true);
					break;
				case CS.UnaryOperatorType.AddressOf:
					expr = new UnaryOperatorExpression() {
						Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
						Operator = UnaryOperatorType.AddressOf
					};
					break;
				case CS.UnaryOperatorType.Dereference:
					expr = new InvocationExpression();
					((InvocationExpression)expr).Target = new IdentifierExpression() { Identifier = Identifier.Create(BoxedTextColor.Keyword, "__Dereference") };
					((InvocationExpression)expr).Arguments.Add((Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data));
					break;
				case CS.UnaryOperatorType.Await:
					expr = new UnaryOperatorExpression() {
						Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
						Operator = UnaryOperatorType.Await
					};
					break;
				default:
					throw new Exception("Invalid value for UnaryOperatorType");
			}

			return EndNode(unaryOperatorExpression, expr);
		}

		public AstNode VisitUncheckedExpression(CS.UncheckedExpression uncheckedExpression, object data)
		{
			blocks.Peek().AddChild(new Comment(" The following expression was wrapped in a unchecked-expression", false), AstNode.Roles.Comment);
			return EndNode(uncheckedExpression, uncheckedExpression.Expression.AcceptVisitor(this, data));
		}

		public AstNode VisitQueryExpression(CS.QueryExpression queryExpression, object data)
		{
			var expr = new QueryExpression();
			ConvertNodes(queryExpression.Clauses, expr.QueryOperators);
			return EndNode(queryExpression, expr);
		}

		public AstNode VisitQueryContinuationClause(CS.QueryContinuationClause queryContinuationClause, object data)
		{
			// VB has no C#-style 'into'; express "... into g <rest>" as
			// "From g In (<preceding query>) <rest>" - the engine nests the preceding
			// clauses in QueryContinuationClause.PrecedingQuery.
			var fromOp = new FromQueryOperator();
			var declaration = new CollectionRangeVariableDeclaration {
				Identifier = new VariableIdentifier { Name = Identifier.Create(queryContinuationClause.IdentifierToken.Annotations, queryContinuationClause.Identifier) },
			};
			declaration.Expression = (Expression)queryContinuationClause.PrecedingQuery.AcceptVisitor(this, data);
			fromOp.Variables.Add(declaration);
			return EndNode(queryContinuationClause, fromOp);
		}

		public AstNode VisitQueryFromClause(CS.QueryFromClause queryFromClause, object data)
		{
			var op = new FromQueryOperator();
			var fromDecl = new CollectionRangeVariableDeclaration {
				Identifier = new VariableIdentifier { Name = Identifier.Create(queryFromClause.IdentifierToken.Annotations, queryFromClause.Identifier) },
				Expression = (Expression)queryFromClause.Expression.AcceptVisitor(this, data)
			};
			if (!queryFromClause.Type.IsNull)
				fromDecl.Type = (AstType)queryFromClause.Type.AcceptVisitor(this, data);
			op.Variables.Add(fromDecl);

			return EndNode(queryFromClause, op);
		}

		public AstNode VisitQueryLetClause(CS.QueryLetClause queryLetClause, object data)
		{
			var op = new LetQueryOperator();
			op.Variables.Add(new VariableInitializer {
				Identifier = new VariableIdentifier { Name = Identifier.Create(queryLetClause.IdentifierToken.Annotations, queryLetClause.Identifier) },
				Expression = (Expression)queryLetClause.Expression.AcceptVisitor(this, data)
			});
			return EndNode(queryLetClause, op);
		}

		public AstNode VisitQueryWhereClause(CS.QueryWhereClause queryWhereClause, object data)
		{
			var op = new WhereQueryOperator {
				Condition = (Expression)queryWhereClause.Condition.AcceptVisitor(this, data)
			};
			return EndNode(queryWhereClause, op);
		}

		public AstNode VisitQueryJoinClause(CS.QueryJoinClause queryJoinClause, object data)
		{
			// "join y in ys on a equals b" -> "Join y In ys On a Equals b";
			// "join y in ys on a equals b into j" -> "Group Join ... Into j = Group"
			// (VB has no bare join-into; the group form binds the grouped sequence).
			JoinQueryOperator op;
			if (queryJoinClause.IsGroupJoin) {
				var groupJoin = new GroupJoinQueryOperator();
				groupJoin.IntoExpressions.Add(new VariableInitializer {
					Identifier = new VariableIdentifier { Name = Identifier.Create(queryJoinClause.IntoIdentifierToken.Annotations, queryJoinClause.IntoIdentifier) },
					Expression = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Keyword, "Group") }
				});
				op = groupJoin;
			}
			else {
				op = new JoinQueryOperator();
			}
			var declaration = new CollectionRangeVariableDeclaration {
				Identifier = new VariableIdentifier { Name = Identifier.Create(queryJoinClause.JoinIdentifierToken.Annotations, queryJoinClause.JoinIdentifier) },
				Expression = (Expression)queryJoinClause.InExpression.AcceptVisitor(this, data)
			};
			if (!queryJoinClause.Type.IsNull)
				declaration.Type = (AstType)queryJoinClause.Type.AcceptVisitor(this, data);
			// JoinVariable's setter goes through SetChildByRole, whose Role has no
			// NullObject (first assignment NREs); AddChild attaches directly.
			op.AddChild(declaration, ICSharpCode.NRefactory.VB.Ast.CollectionRangeVariableDeclaration.CollectionRangeVariableDeclarationRole);
			// JoinCondition's Left/Right setters go through roles without a null object
			// (SetChildByRole NREs on first assignment); AddChild attaches directly.
			var condition = new JoinCondition();
			condition.AddChild((Expression)queryJoinClause.OnExpression.AcceptVisitor(this, data),
				ICSharpCode.NRefactory.VB.Ast.BinaryOperatorExpression.LeftExpressionRole);
			condition.AddChild((Expression)queryJoinClause.EqualsExpression.AcceptVisitor(this, data),
				ICSharpCode.NRefactory.VB.Ast.BinaryOperatorExpression.RightExpressionRole);
			op.JoinConditions.Add(condition);
			return EndNode(queryJoinClause, op);
		}

		public AstNode VisitQueryOrderClause(CS.QueryOrderClause queryOrderClause, object data)
		{
			var op = new OrderByQueryOperator();

			ConvertNodes(queryOrderClause.Orderings, op.Expressions);

			return EndNode(queryOrderClause, op);
		}

		public AstNode VisitQueryOrdering(CS.QueryOrdering queryOrdering, object data)
		{
			var expr = new OrderExpression();

			expr.Direction = (QueryOrderingDirection)queryOrdering.Direction;
			expr.Expression = (Expression)queryOrdering.Expression.AcceptVisitor(this, data);

			return EndNode(queryOrdering, expr);
		}

		int selectVarCount = 0;

		public AstNode VisitQuerySelectClause(CS.QuerySelectClause querySelectClause, object data)
		{
			var op = new SelectQueryOperator();

			op.Variables.Add(
				new VariableInitializer {
					Identifier = new VariableIdentifier { Name = Identifier.Create(BoxedTextColor.Local, "SelectVar" + selectVarCount) },
					Expression = (Expression)querySelectClause.Expression.AcceptVisitor(this, data)
				});

			return EndNode(querySelectClause, op);
		}

		int groupVarCount = 0;

		public AstNode VisitQueryGroupClause(CS.QueryGroupClause queryGroupClause, object data)
		{
			var op = new GroupByQueryOperator();
			op.GroupExpressions.Add(new VariableInitializer {
				Identifier = new VariableIdentifier { Name = Identifier.Create(BoxedTextColor.Local, "g") },
				Expression = (Expression)queryGroupClause.Projection.AcceptVisitor(this, data)
			});
			op.ByExpressions.Add(new VariableInitializer {
				Expression = (Expression)queryGroupClause.Key.AcceptVisitor(this, data)
			});
			// VB requires an aggregate after Into; an unnamed 'Group' yields the IGrouping
			// sequence that the C# 'group ... by ...' (and its 'into g') binds.
			op.IntoExpressions.Add(new VariableInitializer {
				Identifier = new VariableIdentifier { Name = Identifier.Create(BoxedTextColor.Local, "Group" + groupVarCount++) },
				Expression = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Keyword, "Group") }
			});
			return EndNode(queryGroupClause, op);
		}

		public AstNode VisitAttribute(CS.Attribute attribute, object data)
		{
			var attr = new VB.Ast.Attribute();
			AttributeTarget target;
			Enum.TryParse(((CS.AttributeSection)attribute.Parent).AttributeTarget, true, out target);
			attr.Target = target;
			attr.Type = (AstType)attribute.Type.AcceptVisitor(this, data);
			foreach (var argument in attribute.Arguments) {
				var converted = (Expression)argument.AcceptVisitor(this, data);
				// An untyped null literal lands on every primitive constructor of an attribute
				// like DefaultValueAttribute (15 overloads) and VB finds no most-specific one
				// (BC30521) - C# only considers the reference-type constructors. Spelling the
				// null as DirectCast(Nothing, Object) restores a single candidate and stores the
				// same MemberValue (null) as the C# attribute.
				if (IsNullLiteral(argument))
					converted = new CastExpression {
						CastType = CastType.DirectCast,
						Expression = converted,
						Type = new PrimitiveType("Object")
					};
				attr.Arguments.Add(converted);
			}

			return EndNode(attribute, attr);
		}

		static bool IsNullLiteral(CS.AstNode node) {
			switch (node) {
			case CS.NullReferenceExpression:
				return true;
			case CS.PrimitiveExpression primitive:
				return primitive.Value is null;
			default:
				return false;
			}
		}

		public AstNode VisitAttributeSection(CS.AttributeSection attributeSection, object data)
		{
			// NullableAttribute / NullableContextAttribute are C# compiler metadata for nullable
			// reference types. VB cannot apply them (BC30662) and does not need them.
			var attributes = attributeSection.Attributes
				.Where(a => !IsIgnoredCompilerAttribute(a)).ToList();
			if (attributes.Count == 0)
				return null;

			var block = new AttributeBlock();
			foreach (var attribute in attributes) {
				if (attribute.AcceptVisitor(this, null) is VB.Ast.Attribute converted)
					block.Attributes.Add(converted);
			}
			return EndNode(attributeSection, block);
		}

		bool IsIgnoredCompilerAttribute(CS.Attribute attribute) {
			var name = NormalizeAttributeName(attribute.Type?.ToString());
			// Nullable*/NullableContext describe C# nullable annotations; VB cannot apply them
			// (BC30662) but 2.3 emitted them, so keep them unless the caller opts out.
			if (name == "Nullable" || name == "NullableContext")
				return !EmitNullableAttributes;
			// TupleElementNames describes tuple element names, which VB cannot apply explicitly (BC37269).
			return name == "TupleElementNames";
		}

		public AstNode VisitDelegateDeclaration(CS.DelegateDeclaration delegateDeclaration, object data)
		{
			var result = new DelegateDeclaration();

			ConvertNodes(delegateDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
			ConvertNodes(delegateDeclaration.ModifierTokens, result.ModifierTokens);
			result.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(delegateDeclaration), delegateDeclaration.NameToken.Annotations), delegateDeclaration.Name);
			result.IsSub = IsSub(delegateDeclaration.ReturnType);
			ConvertNodes(delegateDeclaration.Parameters, result.Parameters);
			ConvertNodes(delegateDeclaration.TypeParameters, result.TypeParameters);
			ConvertNodes(delegateDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), result.ReturnTypeAttributes);
			if (!result.IsSub)
				result.ReturnType = (AstType)delegateDeclaration.ReturnType.AcceptVisitor(this, data);
			return EndNode(delegateDeclaration, result);
		}

		// True when the identifier is the root segment of the namespace we are currently in, i.e.
		// the reference would be resolved relative to that namespace and could hit a type of the
		// same name instead of the namespace.
		bool ShouldQualifyWithGlobal(string identifier) {
			if (namespaceStack.Count == 0)
				return false;
			var current = namespaceStack.Peek();
			int dot = current.IndexOf('.');
			var root = dot < 0 ? current : current.Substring(0, dot);
			return root == identifier;
		}

		public AstNode VisitNamespaceDeclaration(CS.NamespaceDeclaration namespaceDeclaration, object data)
		{
			var newNamespace = new NamespaceDeclaration();

			var declaredNs = string.Join(".", namespaceDeclaration.Identifiers);
			foreach (var name in namespaceDeclaration.Identifiers) {
				// Each declaration segment carries the NamespaceReference the 2.3 engine
				// emitted (module's own assembly + segment name) for navigation.
				var newId = new Identifier(
					new object[] { new NamespaceReference(module.Assembly, name), BoxedTextColor.Namespace },
					name, ICSharpCode.NRefactory.TextLocation.Empty);
				newNamespace.Identifiers.Add(newId);
			}
			var parent = namespaceStack.Count > 0 ? namespaceStack.Peek() + "." : string.Empty;
			namespaceStack.Push(parent + string.Join(".", newNamespace.Identifiers.Select(i => i.Name)));
			try {
				ConvertMembers(namespaceDeclaration, newNamespace, CS.NamespaceDeclaration.MemberRole, NamespaceDeclaration.MemberRole);
			}
			finally {
				namespaceStack.Pop();
			}

			return EndNode(namespaceDeclaration, newNamespace);
		}

		public AstNode VisitTypeDeclaration(CS.TypeDeclaration typeDeclaration, object data)
		{
			// TODO add missing features!

			if (typeDeclaration.ClassType == CS.ClassType.Enum) {
				var type = new EnumDeclaration();
				CopyAnnotations(typeDeclaration, type);

				ConvertNodes(typeDeclaration.Attributes, type.Attributes);
				ConvertNodes(typeDeclaration.ModifierTokens, type.ModifierTokens);

				if (typeDeclaration.BaseTypes.Any()) {
					var first = typeDeclaration.BaseTypes.First();

					type.UnderlyingType = (AstType)first.AcceptVisitor(this, data);
				}

				type.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(typeDeclaration), typeDeclaration.NameToken.Annotations), typeDeclaration.Name);

				ConvertMembers(typeDeclaration, type, CS.Roles.TypeMemberRole, EnumDeclaration.MemberRole);

				return EndNode(typeDeclaration, type);
			} else {
				var type = new TypeDeclaration();
				CopyAnnotations(typeDeclaration, type);

				CS.Attribute stdModAttr;

				if (typeDeclaration.ClassType == CS.ClassType.Class && HasAttribute(typeDeclaration.Attributes, "Microsoft.VisualBasic.CompilerServices.StandardModuleAttribute", out stdModAttr)) {
					type.ClassType = ClassType.Module;
					// remove AttributeSection if only one attribute is present
					var attrSec = (CS.AttributeSection)stdModAttr.Parent;
					if (attrSec.Attributes.Count == 1)
						attrSec.Remove();
					else
						stdModAttr.Remove();
				} else {
					switch (typeDeclaration.ClassType) {
						case CS.ClassType.Class:
							type.ClassType = ClassType.Class;
							break;
						case CS.ClassType.Struct:
							type.ClassType = ClassType.Struct;
							break;
						case CS.ClassType.Interface:
							type.ClassType = ClassType.Interface;
							break;
						default:
							throw new InvalidOperationException("Invalid value for ClassType");
					}
				}

				if ((typeDeclaration.Modifiers & CS.Modifiers.Static) == CS.Modifiers.Static) {
					type.ClassType = ClassType.Module;
					typeDeclaration.Modifiers &= ~CS.Modifiers.Static;
				}

				ConvertNodes(typeDeclaration.Attributes, type.Attributes);
				ConvertNodes(typeDeclaration.ModifierTokens, type.ModifierTokens);
				ConvertNodes(typeDeclaration.TypeParameters, type.TypeParameters);

				if (typeDeclaration.Parent is CS.TypeDeclaration && type.ClassType == ClassType.Module) {
					type.ClassType = ClassType.Class;
					type.Modifiers &= ~Modifiers.Static;
					type.Modifiers |= Modifiers.NotInheritable;
				}

				if (typeDeclaration.BaseTypes.Any()) {
					var first = typeDeclaration.BaseTypes.First();

					if (!IsInterfaceType(first)) {
						ConvertNodes(typeDeclaration.BaseTypes.Skip(1), type.ImplementsTypes);
						type.InheritsType = (AstType)first.AcceptVisitor(this, data);
					} else
						ConvertNodes(typeDeclaration.BaseTypes, type.ImplementsTypes);
				}

				type.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(typeDeclaration), typeDeclaration.NameToken.Annotations), typeDeclaration.Name);

				CollectMemberRenames(typeDeclaration);
				types.Push(type);
				ConvertMembers(typeDeclaration, type, CS.Roles.TypeMemberRole, TypeDeclaration.MemberRole);
				types.Pop();

				return EndNode(typeDeclaration, type);
			}
		}

		// VB cannot declare two members whose signatures differ only by the return type
		// (BC30301), and every call of that name then fails overload resolution (BC30521).
		// C# builds such pairs around an explicit interface implementation - the non-generic
		// GetEnumerator next to the IEnumerable(Of T) one, or a private helper next to an
		// explicit implementation (GetCommands) - because two ordinary members with the same
		// name and parameters are illegal in C# itself. The explicit implementation side is
		// renamed (VB idiom: GetEnumerator1): C# can never call it with a simple name, so no
		// call site - in this class or in any other dump - binds to it, and interface dispatch
		// keeps working through the emitted Implements clause. The output's member name no
		// longer matches the IL method name one-to-one; see README item 46 for the equivalence
		// argument.
		readonly Dictionary<CS.EntityDeclaration, string> memberRenames =
			new Dictionary<CS.EntityDeclaration, string>();

		class ConflictGroupMember {
			public CS.EntityDeclaration Node;
			public string RefPattern;
			public string ReturnKey;
		}

		void CollectMemberRenames(CS.TypeDeclaration typeDeclaration) {
			if (typeDeclaration.ClassType == CS.ClassType.Interface || typeDeclaration.ClassType == CS.ClassType.Enum)
				return;
			var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var groups = new Dictionary<string, List<ConflictGroupMember>>(StringComparer.Ordinal);
			foreach (var member in typeDeclaration.Members) {
				var name = GetMemberDeclarationName(member);
				if (name is null)
					continue;
				usedNames.Add(name);
				var key = GetMemberSigKey(member, out var refPattern, out var returnKey);
				if (key is null)
					continue;
				if (!groups.TryGetValue(key, out var group))
					groups[key] = group = new List<ConflictGroupMember>();
				group.Add(new ConflictGroupMember { Node = member, RefPattern = refPattern, ReturnKey = returnKey });
			}
			// The binder renames fields for VB's case-insensitive name space; do not reuse those
			// names either (both declaration and references go through Identifier).
			CS.AstNode root = typeDeclaration;
			while (root.Parent != null)
				root = root.Parent;
			if (root is CS.SyntaxTree tree &&
					tree.Annotation<Dictionary<string, string>>() is Dictionary<string, string> binderRenames) {
				foreach (var value in binderRenames.Values)
					usedNames.Add(value);
			}
			foreach (var group in groups.Values) {
				// Rename only on a definite conflict: the members' VB signatures cannot coexist.
				// VB signatures exclude the return type but include ref-ness, so two shapes are
				// illegal: differing return types (BC30301) and ByRef/ByVal-only differences
				// (BC30345). Each call of the name then fails overload resolution (BC30521).
				// Unknown shapes keep the old output.
				var returnKeys = new HashSet<string>(StringComparer.Ordinal);
				var refPatterns = new HashSet<string>(StringComparer.Ordinal);
				bool returnsKnown = true;
				bool refsKnown = true;
				foreach (var member in group) {
					if (member.ReturnKey is null)
						returnsKnown = false;
					else
						returnKeys.Add(member.ReturnKey);
					if (member.RefPattern is null)
						refsKnown = false;
					else
						refPatterns.Add(member.RefPattern);
				}
				bool conflicts = (returnsKnown && returnKeys.Count >= 2) || (refsKnown && refPatterns.Count >= 2);
				if (!conflicts)
					continue;
				var explicitImpls = group.Where(m => IsExplicitInterfaceImplementation(m.Node)).Select(m => m.Node).ToList();
				if (explicitImpls.Count == 0)
					continue;
				// A non-explicit member keeps its name: every simple-name call site in the class
				// body binds to it (C# cannot call an explicit implementation by simple name), so
				// renaming only the explicit implementations preserves every call site. When the
				// whole group consists of explicit implementations, the first one keeps the name.
				var targets = group.Any(m => !IsExplicitInterfaceImplementation(m.Node))
					? explicitImpls
					: explicitImpls.Skip(1).ToList();
				foreach (var target in targets) {
					var original = GetMemberDeclarationName(target);
					if (string.IsNullOrEmpty(original))
						continue;
					var index = 1;
					string renamed;
					do {
						renamed = original + index;
						index++;
					} while (usedNames.Contains(renamed));
					usedNames.Add(renamed);
					memberRenames[target] = renamed;
				}
			}
		}

		static string GetMemberDeclarationName(CS.AstNode member) {
			switch (member) {
			case CS.MethodDeclaration method:
				return method.Name;
			case CS.PropertyDeclaration property:
				return property.Name;
			case CS.IndexerDeclaration indexer:
				return indexer.Name;
			case CS.EventDeclaration @event:
				return @event.Name;
			case CS.CustomEventDeclaration customEvent:
				return customEvent.Name;
			case CS.FieldDeclaration field:
				return field.Variables.FirstOrDefault()?.Name;
			case CS.TypeDeclaration nested:
				return nested.Name;
			case CS.OperatorDeclaration op:
				return op.Name;
			default:
				return null;
			}
		}

		// Groups members that VB sees as candidates for the same call: same name (VB is
		// case-insensitive), same number of type and value parameters and the same parameter
		// types. The parameter type spelling keeps its arity (IEnumerator`1 vs IEnumerator) so
		// the non-generic IEnumerable.GetEnumerator does not group with the generic one -
		// NormalizeParameterTypeName strips the arity and must not be used here. Generic type
		// arguments are stripped instead: two members returning IEnumerable`1 of different
		// element types are exactly the "differ only by return type" conflict (VB signatures
		// do not include the return type), while a real overload (different parameters) is not
		// grouped and is left alone. ref-ness is deliberately left out of the key so the
		// ByRef/ByVal-only pair (BC30345) lands in one group; the pattern itself is reported
		// through refPattern and decides the conflict. Return keys never join the key.
		string GetMemberSigKey(CS.AstNode member, out string refPattern, out string returnKey) {
			refPattern = null;
			returnKey = null;
			string name;
			int typeParameterCount;
			IEnumerable<CS.ParameterDeclaration> parameters;
			switch (member) {
			case CS.MethodDeclaration method:
				name = method.Name;
				typeParameterCount = method.TypeParameters.Count;
				parameters = method.Parameters;
				returnKey = GetAstTypeKeyWithArity(method.ReturnType);
				break;
			case CS.PropertyDeclaration property:
				name = property.Name;
				typeParameterCount = 0;
				parameters = GetIndexerParameters(property);
				returnKey = GetAstTypeKeyWithArity(property.ReturnType);
				break;
			case CS.IndexerDeclaration indexer:
				name = indexer.Name;
				typeParameterCount = 0;
				parameters = indexer.Parameters;
				returnKey = GetAstTypeKeyWithArity(indexer.ReturnType);
				break;
			default:
				return null;
			}
			if (string.IsNullOrEmpty(name))
				return null;
			var parts = new List<string>();
			var refs = new StringBuilder();
			foreach (var parameter in parameters) {
				var part = GetAstTypeKeyWithArity(parameter?.Type);
				if (part is null)
					return null;
				parts.Add(part);
				// VB spells ref/out parameters ByRef; the modifier is part of the VB signature.
				var modifier = parameter.ParameterModifier;
				refs.Append(modifier == CS.ParameterModifier.Ref || modifier == CS.ParameterModifier.Out ? "R" : "V");
			}
			refPattern = refs.ToString();
			return name.ToUpperInvariant() + "#" + typeParameterCount + "#" + parts.Count + "#" + string.Join("|", parts);
		}

		// ILSpy 3.2 keeps indexer parameters out of PropertyDeclaration.Parameters; the
		// environment provider reconstructs them from the metadata annotation.
		IEnumerable<CS.ParameterDeclaration> GetIndexerParameters(CS.PropertyDeclaration property) {
			try {
				return provider.GetParametersForProperty(property);
			}
			catch {
				return Array.Empty<CS.ParameterDeclaration>();
			}
		}

		static bool IsExplicitInterfaceImplementation(CS.AstNode member) {
			switch (member) {
			case CS.MethodDeclaration method:
				return method.PrivateImplementationType is { IsNull: false };
			case CS.PropertyDeclaration property:
				return property.PrivateImplementationType is { IsNull: false };
			case CS.IndexerDeclaration indexer:
				return indexer.PrivateImplementationType is { IsNull: false };
			default:
				return false;
			}
		}

		// Canonical spelling of a syntax-tree type that keeps the generic arity: IEnumerator
		// and IEnumerator`1 compare unequal, IEnumerator`1<A> and IEnumerator`1<B> compare
		// equal. Both sides come from the same decompiled tree and use the same conventions,
		// so no normalisation against VB/metadata spellings is involved here.
		static string GetAstTypeKeyWithArity(CS.AstType type) {
			switch (type) {
			case null:
			case CS.AstType t when t.IsNull:
				return null;
			case CS.PrimitiveType primitive:
				return primitive.Keyword?.ToUpperInvariant();
			case CS.SimpleType simple:
				var simpleName = simple.Identifier;
				if (string.IsNullOrEmpty(simpleName))
					return null;
				return simple.TypeArguments.Count > 0 ? simpleName.ToUpperInvariant() + "`" + simple.TypeArguments.Count : simpleName.ToUpperInvariant();
			case CS.MemberType memberType:
				var targetKey = GetAstTypeKeyWithArity(memberType.Target);
				if (string.IsNullOrEmpty(targetKey) || string.IsNullOrEmpty(memberType.MemberName))
					return null;
				var memberName = memberType.TypeArguments.Count > 0 ? memberType.MemberName.ToUpperInvariant() + "`" + memberType.TypeArguments.Count : memberType.MemberName.ToUpperInvariant();
				return targetKey + "." + memberName;
			case CS.ComposedType composed:
				var baseKey = GetAstTypeKeyWithArity(composed.BaseType);
				if (string.IsNullOrEmpty(baseKey))
					return null;
				if (composed.HasNullableSpecifier)
					baseKey += "?";
				foreach (var specifier in composed.ArraySpecifiers)
					baseKey += "[" + specifier.Dimensions + "]";
				return baseKey;
			default:
				return null;
			}
		}

		string GetRenamedMemberName(CS.EntityDeclaration member, string original) =>
			memberRenames.TryGetValue(member, out var renamed) ? renamed : original;

		public AstNode VisitUsingAliasDeclaration(CS.UsingAliasDeclaration usingAliasDeclaration, object data)
		{
			var imports = new ImportsStatement();

			var clause = new AliasImportsClause() {
				Name = Identifier.Create(usingAliasDeclaration.GetChildByRole(CS.UsingAliasDeclaration.AliasRole).Annotations, usingAliasDeclaration.Alias),
				Alias = (AstType)usingAliasDeclaration.Import.AcceptVisitor(this, data)
			};

			imports.AddChild(clause, ImportsStatement.ImportsClauseRole);

			return EndNode(usingAliasDeclaration, imports);
		}

		public AstNode VisitUsingDeclaration(CS.UsingDeclaration usingDeclaration, object data)
		{
			var imports = new ImportsStatement();

			var member = RebuildWithNamespaceColors((AstType)usingDeclaration.Import.AcceptVisitor(this, data));

			var clause = new MemberImportsClause() {
				Member = member
			};

			imports.AddChild(clause, ImportsStatement.ImportsClauseRole);

			return EndNode(usingDeclaration, imports);
		}

		/// <summary>
		/// Every segment of a namespace Imports is a namespace segment; make the Namespace
		/// color the FIRST annotation so the output stage picks it up (engine resolve results
		/// on the import names would otherwise color them Text).
		/// </summary>
		/// <summary>Rebuilds the import name so every segment carries the Namespace color AND
		/// the NamespaceReference (assembly + segment) the 2.3 engine emitted for navigation.</summary>
		AstType RebuildWithNamespaceColors(AstType type) {
			// The top call passes the node's own full namespace: the name segment uses it to
			// resolve a TypeRef of that namespace (for the NamespaceReference), so the last -
			// most specific - segment carries the reference like the 2.3 engine's output.
			return RebuildWithNamespaceColors(type, type.ToString());
		}

		AstType RebuildWithNamespaceColors(AstType type, string? fullNamespace) {
			if (type is QualifiedType qualified) {
				// The target's full namespace is the prefix of this node's full namespace.
				var thisNs = type.ToString();
				var targetNs = thisNs.EndsWith("." + qualified.Name, StringComparison.Ordinal)
					? thisNs.Substring(0, thisNs.Length - qualified.Name.Length - 1)
					: null;
				return new QualifiedType(RebuildWithNamespaceColors(qualified.Target, targetNs),
					Identifier.Create(ComposeNamespaceSegmentAnnotations(qualified.Name, fullNamespace), qualified.Name));
			}
			if (type is SimpleType simple)
				return new SimpleType(ComposeNamespaceSegmentAnnotations(simple.Identifier, fullNamespace), simple.Identifier);
			return type;
		}

		/// <summary>
		/// Annotations for one namespace segment: Namespace color first, then the
		/// NamespaceReference (resolved from any TypeRef of that namespace in the module's
		/// reference table, so dnSpy navigates to the declaring assembly).
		/// </summary>
		System.Collections.Generic.IEnumerable<object> ComposeNamespaceSegmentAnnotations(string segment, string? fullNamespace) {
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1")
				Console.Error.WriteLine("NSSEG segment=" + segment + " full=" + (fullNamespace ?? "null"));
			yield return BoxedTextColor.Namespace;
			if (fullNamespace is not null) {
				var typeRef = module.GetTypeRefs().FirstOrDefault(tr => tr.Namespace == fullNamespace)
					?? module.GetTypeRefs().FirstOrDefault(tr => tr.Namespace.StartsWith(fullNamespace + ".", StringComparison.Ordinal));
				if (typeRef?.ResolutionScope is dnlib.DotNet.IAssembly assembly)
					yield return new NamespaceReference(assembly, segment);
				else if (typeRef is not null && typeRef.ResolutionScope is dnlib.DotNet.IAssembly scope)
					yield return new NamespaceReference(scope, segment);
				else
					yield return new NamespaceReference(module.CorLibTypes.AssemblyRef, segment);
			}
		}

		public AstNode VisitExternAliasDeclaration(CS.ExternAliasDeclaration externAliasDeclaration, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitBlockStatement(CS.BlockStatement blockStatement, object data)
		{
			var block = new BlockStatement();
			blocks.Push(block);
			block.HiddenStart = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();
			block.HiddenEnd = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();
			ConvertNodes(blockStatement, block.Statements);
			blocks.Pop();
			return EndNode(blockStatement, block);
		}

		public AstNode VisitBreakStatement(CS.BreakStatement breakStatement, object data)
		{
			var exit = new ExitStatement(ExitKind.None);

			foreach (var stmt in breakStatement.Ancestors) {
				if (stmt is CS.MethodDeclaration) {
					exit.ExitKind = IsSub(((CS.MethodDeclaration)stmt).ReturnType) ? ExitKind.Sub : ExitKind.Function;
					break;
				}
				if (stmt is CS.PropertyDeclaration) {
					exit.ExitKind = ExitKind.Property;
					break;
				}
				if (stmt is CS.DoWhileStatement) {
					exit.ExitKind = ExitKind.Do;
					break;
				}
				if (stmt is CS.ForStatement || stmt is CS.ForeachStatement) {
					ConvertedStatementKind kind;
					if (!convertedKind.TryGetValue(stmt, out kind) || kind != ConvertedStatementKind.While)
						exit.ExitKind = ExitKind.For;
					else
						exit.ExitKind = ExitKind.While;
					break;
				}
				if (stmt is CS.WhileStatement) {
					exit.ExitKind = ExitKind.While;
					break;
				}
				if (stmt is CS.SwitchStatement) {
					exit.ExitKind = ExitKind.Select;
					break;
				}
			}

			return EndNode(breakStatement, exit);
		}

		public AstNode VisitCheckedStatement(CS.CheckedStatement checkedStatement, object data)
		{
			blocks.Peek().AddChild(new Comment(" The following expression was wrapped in a checked-statement", false), AstNode.Roles.Comment);
			var body = (BlockStatement)checkedStatement.Body.AcceptVisitor(this, data);

			foreach (var stmt in body) {
				stmt.Remove();
				blocks.Peek().Add(stmt);
			}

			return EndNode<AstNode>(checkedStatement, null);
		}

		public AstNode VisitContinueStatement(CS.ContinueStatement continueStatement, object data)
		{
			var @continue = new ContinueStatement(ContinueKind.None);

			foreach (var stmt in continueStatement.Ancestors) {
				if (stmt is CS.DoWhileStatement) {
					@continue.ContinueKind = ContinueKind.Do;
					break;
				}
				if (stmt is CS.ForStatement || stmt is CS.ForeachStatement) {
					ConvertedStatementKind kind;
					if (!convertedKind.TryGetValue(stmt, out kind) || kind != ConvertedStatementKind.While)
						@continue.ContinueKind = ContinueKind.For;
					else
						@continue.ContinueKind = ContinueKind.While;
					break;
				}
				if (stmt is CS.WhileStatement) {
					@continue.ContinueKind = ContinueKind.While;
					break;
				}
			}

			return EndNode(continueStatement, @continue);
		}

		public AstNode VisitDoWhileStatement(CS.DoWhileStatement doWhileStatement, object data)
		{
			var stmt = new DoLoopStatement();

			stmt.ConditionType = ConditionType.LoopWhile;
			stmt.Expression = (Expression)doWhileStatement.Condition.AcceptVisitor(this, data);
			stmt.Body = (BlockStatement)doWhileStatement.EmbeddedStatement.AcceptVisitor(this, data);

			return EndNode(doWhileStatement, stmt);
		}

		public AstNode VisitEmptyStatement(CS.EmptyStatement emptyStatement, object data)
		{
			return EndNode<Statement>(emptyStatement, null);
		}

		public AstNode VisitExpressionStatement(CS.ExpressionStatement expressionStatement, object data)
		{
			// "x.Event += handler" is AddHandler in VB; += on an event is not an operator there.
			// The C# engine's resolve result on the left side is authoritative (an event
			// subscription targets IEvent; "fs.Position += 20" targets IProperty and must stay
			// a compound assignment), so consult it before the syntactic heuristic.
			if (expressionStatement.Expression is CS.AssignmentExpression subscription &&
					(subscription.Operator == CS.AssignmentOperatorType.Add || subscription.Operator == CS.AssignmentOperatorType.Subtract) &&
					IsEventSubscription(subscription)) {
				var handler = new AddRemoveHandlerStatement() {
					IsAddHandler = subscription.Operator == CS.AssignmentOperatorType.Add
				};
				handler.EventExpression = (Expression)subscription.Left.AcceptVisitor(this, data);
				handler.DelegateExpression = (Expression)subscription.Right.AcceptVisitor(this, data);
				return EndNode(expressionStatement, handler);
			}

			// C# raises an event by invoking it like a delegate; VB needs a RaiseEvent statement
			// (BC32022). Checked before the expression is converted so nothing is converted twice.
			var raisedEvent = expressionStatement.Expression is CS.InvocationExpression inv ? GetRaisedEventName(inv) : null;
			if (raisedEvent != null) {
				var eventInvocation = (CS.InvocationExpression)expressionStatement.Expression;
				var raise = new RaiseEventStatement();
				raise.Event = MakeIdentifierExpression(ResolveLocalName(raisedEvent));
				foreach (var argument in eventInvocation.Arguments)
					raise.Arguments.Add((Expression)argument.AcceptVisitor(this, data));
				return EndNode(expressionStatement, raise);
			}

			var node = expressionStatement.Expression.AcceptVisitor(this, data);
			if (node is Expression) {
				// VB rejects a statement that begins with New. A call chain can use "Call":
				// "Call New T().M()". A bare "New T()" cannot ("Call New T()" is BC30454),
				// so it becomes a local: "Dim __unused_obj_1 As T = New T()".
				if (expressionStatement.Expression is CS.InvocationExpression invocation
						&& NeedsCallKeyword(invocation.Target))
					node = new CallStatement { Expression = (Expression)node };
				else if (expressionStatement.Expression is CS.ObjectCreateExpression && node is ObjectCreationExpression created)
					node = CreateDiscardedObjectDeclaration(created);
				else
					node = new ExpressionStatement((Expression)node);
			}
			return EndNode(expressionStatement, node);
		}

		/// <summary>True when the expression names an event (this.Event or obj.Event).</summary>
		/// <summary>
		/// True when this compound assignment targets an event. The engine's
		/// MemberResolveResult on the left side decides when present (IEvent vs IProperty);
		/// without one the syntactic heuristic answers.
		/// </summary>
		bool IsEventSubscription(CS.AssignmentExpression subscription) {
			var rr = (subscription.Left ?? (CS.AstNode)subscription).Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>()
				?? subscription.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>();
			if (rr?.Member is not null) {
				if (rr.Member is ICSharpCode.Decompiler.TypeSystem.IEvent)
					return true;
				if (rr.Member is ICSharpCode.Decompiler.TypeSystem.IProperty || rr.Member is ICSharpCode.Decompiler.TypeSystem.IField)
					return false;
			}
			return IsEventAccess(subscription.Left);
		}

		bool IsEventAccess(CS.Expression expression) {
			switch (expression) {
			case CS.IdentifierExpression identifier:
				return IsEventName(identifier.Identifier);
			case CS.MemberReferenceExpression member:
				if (member.Target is CS.TypeReferenceExpression typeReference) {
					// Static event: "MetroWindow.MetroWindowCreated += handler" — the receiver is
					// a type name, which GetTypeName cannot resolve.
					var staticTypeName = typeReference.Type?.ToString();
					if (string.IsNullOrEmpty(staticTypeName))
						return false;
					return typeResolver.HasEvent(staticTypeName, member.MemberName) ?? true;
				}
				var targetType = typeResolver.GetTypeName(member.Target);
				if (targetType is null)
					return false;
				return typeResolver.HasEvent(targetType, member.MemberName) ?? true;
			default:
				return false;
			}
		}

		/// <summary>Name of the event the invocation raises, or null.</summary>
		string GetRaisedEventName(CS.InvocationExpression invocation) {
			if (!(invocation.Target is CS.IdentifierExpression identifier))
				return null;
			if (eventAliases.TryGetValue(identifier.Identifier, out var aliased))
				return aliased;
			return IsEventName(identifier.Identifier) ? identifier.Identifier : null;
		}

		/// <summary>Name of the event the expression evaluates to, or null.</summary>
		string GetEventName(CS.Expression expression) {
			switch (expression) {
			case CS.IdentifierExpression identifier:
				return IsEventName(identifier.Identifier) ? identifier.Identifier : null;
			case CS.MemberReferenceExpression member when member.Target is CS.ThisReferenceExpression:
				return IsEventName(member.MemberName) ? member.MemberName : null;
			default:
				return null;
			}
		}

		bool IsEventName(string name) {
			for (var current = typeResolver.CurrentType; current != null; current = current.BaseType?.Resolve()) {
				foreach (var ev in current.Events) {
					if (ev.Name == name)
						return true;
				}
			}
			return false;
		}

		LocalDeclarationStatement CreateDiscardedObjectDeclaration(ObjectCreationExpression created) {
			// The object is constructed for its side effects and immediately discarded, which
			// matches C# semantics; VB simply has no statement form for it.
			var decl = new LocalDeclarationStatement();
			decl.Modifiers = Modifiers.Dim;
			var variable = new VariableDeclaratorWithTypeAndInitializer();
			variable.Type = (AstType)created.Type.Clone();
			variable.Identifiers.Add(new VariableIdentifier() {
				Name = Identifier.Create((object)null, "__unused_obj_" + (++unusedObjectCounter))
			});
			variable.Initializer = created;
			decl.Variables.Add(variable);
			return decl;
		}

		// VB parses these at statement start as statements, not expressions: "New T()" and
		// the conditional "If(c, a, b)". The Call keyword forces expression parsing.
		static bool NeedsCallKeyword(CS.Expression expression) {
			var current = expression;
			while (current != null) {
				if (current is CS.ObjectCreateExpression || current is CS.ConditionalExpression)
					return true;
				// '(From ...).ToArray()' starts a statement with '(' - VB reads it as a
				// continuation of the previous line unless a Call keyword opens the statement.
				if (current is CS.QueryExpression)
					return true;
				if (current is CS.MemberReferenceExpression member) { current = member.Target; continue; }
				if (current is CS.ParenthesizedExpression parenthesized) { current = parenthesized.Expression; continue; }
				if (current is CS.InvocationExpression invocation) { current = invocation.Target; continue; }
				if (current is CS.IndexerExpression indexer) { current = indexer.Target; continue; }
				return false;
			}
			return false;
		}

		public AstNode VisitFixedStatement(CS.FixedStatement fixedStatement, object data)
		{
			var block = blocks.Peek();
			block.AddChild(new Comment(" Emulating fixed-Statement, might not be entirely correct!", false), AstNode.Roles.Comment);

			var variables = new LocalDeclarationStatement();
			variables.Modifiers = Modifiers.Dim;
			var stmt = new TryStatement();
			stmt.FinallyBlock = new BlockStatement();
			foreach (var decl in fixedStatement.Variables) {
				var v = new VariableDeclaratorWithTypeAndInitializer {
					Identifiers = { new VariableIdentifier { Name = Identifier.Create(decl.NameToken.Annotations, decl.Name) } },
					Type = SimpleType.CreateWithColor(BoxedTextColor.ValueType, "GCHandle"),
					Initializer = new InvocationExpression(
						new MemberAccessExpression { Target = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.ValueType, "GCHandle") }, MemberName = Identifier.Create(BoxedTextColor.StaticMethod, "Alloc") },
						(Expression)decl.Initializer.AcceptVisitor(this, data),
						new MemberAccessExpression { Target = new IdentifierExpression { Identifier = Identifier.Create(BoxedTextColor.Enum, "GCHandleType") }, MemberName = Identifier.Create(BoxedTextColor.EnumField, "Pinned") }
					)
				};
				variables.Variables.Add(v);
				stmt.FinallyBlock.Add(new IdentifierExpression { Identifier = Identifier.Create(decl.NameToken.Annotations, decl.Name) }.Invoke2(BoxedTextColor.InstanceMethod, "Free"));
			}

			block.Add(variables);

			stmt.Body = (BlockStatement)fixedStatement.EmbeddedStatement.AcceptVisitor(this, data);

			foreach (var ident in stmt.Body.Descendants.OfType<IdentifierExpression>()) {
				ident.ReplaceWith(expr => ((Expression)expr).Invoke2(BoxedTextColor.InstanceMethod, "AddrOfPinnedObject"));
			}

			return EndNode(fixedStatement, stmt);
		}

		public AstNode VisitForeachStatement(CS.ForeachStatement foreachStatement, object data)
		{
			// Register before the body is converted: the loop variable is visible inside it.
			variableTypes[foreachStatement.VariableName] = foreachStatement.VariableType.ToString();
			var stmt = new ForEachStatement() {
				Body = (BlockStatement)foreachStatement.EmbeddedStatement.AcceptVisitor(this, data),
				InExpression = (Expression)foreachStatement.InExpression.AcceptVisitor(this, data),
				Variable = new VariableInitializer() {
					Identifier = new VariableIdentifier() { Name = Identifier.Create(foreachStatement.VariableNameToken.Annotations, foreachStatement.VariableName) },
					Type = (AstType)foreachStatement.VariableType.AcceptVisitor(this, data)
				}
			};
			stmt.HiddenInitializer = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();
			stmt.HiddenGetEnumeratorILSpans = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();
			stmt.HiddenMoveNextILSpans = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();
			stmt.HiddenGetCurrentILSpans = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();

			return EndNode(foreachStatement, stmt);
		}

		public AstNode VisitForStatement(CS.ForStatement forStatement, object data)
		{
			// for (;;) ;
			if (!forStatement.Initializers.Any() && forStatement.Condition.IsNull && !forStatement.Iterators.Any())
				return EndNode(forStatement, new WhileStatement() { Condition = new PrimitiveExpression(true), Body = (BlockStatement)forStatement.EmbeddedStatement.AcceptVisitor(this, data) });

			CS.AstNode counterLoop = new CS.ForStatement() {
				Initializers = {
					new CS.PatternMatching.NamedNode(
						"iteratorVar",
						new CS.PatternMatching.Choice {
							new CS.VariableDeclarationStatement {
								Type = new CS.PatternMatching.Choice {
									new CS.PrimitiveType("long"),
									new CS.PrimitiveType("ulong"),
									new CS.PrimitiveType("int"),
									new CS.PrimitiveType("uint"),
									new CS.PrimitiveType("short"),
									new CS.PrimitiveType("ushort"),
									new CS.PrimitiveType("sbyte"),
									new CS.PrimitiveType("byte")
								},
								Variables = {
									new CS.PatternMatching.AnyNode()
								}
							},
							new CS.ExpressionStatement(
								new CS.AssignmentExpression()
							)
						})
				},
				Condition = new CS.PatternMatching.NamedNode(
					"condition",
					new CS.BinaryOperatorExpression {
						Left = new CS.PatternMatching.NamedNode("ident", new CS.IdentifierExpression(Pattern.AnyString)),
						Operator = CS.BinaryOperatorType.Any,
						Right = new CS.PatternMatching.AnyNode("endExpr")
					}),
				Iterators = {
					new CS.ExpressionStatement(
						new CS.PatternMatching.NamedNode(
							"increment",
							new CS.AssignmentExpression {
								Left = new CS.PatternMatching.Backreference("ident"),
								Operator = CS.AssignmentOperatorType.Any,
								Right = new CS.PatternMatching.NamedNode("factor", new CS.PatternMatching.AnyNode())
							}
						)
					)
				},
				EmbeddedStatement = new CS.PatternMatching.NamedNode("body", new CS.PatternMatching.AnyNode())
			};

			var match = counterLoop.Match(forStatement);

			if (match.Success) {
				var init = match.Get<CS.Statement>("iteratorVar").SingleOrDefault();

				AstNode iteratorVariable;

				if (init is CS.VariableDeclarationStatement) {
					var var = ((CS.VariableDeclarationStatement)init).Variables.First();
					iteratorVariable = new VariableInitializer() {
						Identifier = new VariableIdentifier { Name = Identifier.Create(var.NameToken.Annotations, var.Name) },
						Type = (AstType)((CS.VariableDeclarationStatement)init).Type.AcceptVisitor(this, data),
						Expression = (Expression)var.Initializer.AcceptVisitor(this, data)
					};
					CopyAnnotations(var, iteratorVariable);
				} else if (init is CS.ExpressionStatement) {
					iteratorVariable = init.AcceptVisitor(this, data);
				} else goto end;

				bool copiedCondAnns = false;
				bool copiedIncAnns = false;
				Expression toExpr = Expression.Null;

				var cond = match.Get<CS.BinaryOperatorExpression>("condition").SingleOrDefault();
				var endExpr = (Expression)match.Get<CS.Expression>("endExpr").SingleOrDefault().AcceptVisitor(this, data);

				if (cond.Operator == CS.BinaryOperatorType.LessThanOrEqual ||
				    cond.Operator == CS.BinaryOperatorType.GreaterThanOrEqual) {
					toExpr = endExpr;
				}

				if (cond.Operator == CS.BinaryOperatorType.LessThan)
					toExpr = new BinaryOperatorExpression(endExpr, BinaryOperatorType.Subtract, new PrimitiveExpression(1));
				if (cond.Operator == CS.BinaryOperatorType.GreaterThan)
					toExpr = new BinaryOperatorExpression(endExpr, BinaryOperatorType.Add, new PrimitiveExpression(1));

				Expression stepExpr = Expression.Null;

				var increment = match.Get<CS.AssignmentExpression>("increment").SingleOrDefault();
				var factorExpr = (Expression)match.Get<CS.Expression>("factor").SingleOrDefault().AcceptVisitor(this, data);

				if (increment.Operator == CS.AssignmentOperatorType.Add && (factorExpr is PrimitiveExpression && !IsEqual(((PrimitiveExpression)factorExpr).Value, 1)))
					stepExpr = factorExpr;
				if (increment.Operator == CS.AssignmentOperatorType.Subtract)
					stepExpr = new UnaryOperatorExpression(UnaryOperatorType.Minus, factorExpr);

				// Copy all IL ranges
				if (!toExpr.IsNull) {
					CopyAnnotations(cond, toExpr);
					copiedCondAnns = true;
				}
				if (!stepExpr.IsNull) {
					CopyAnnotations(increment, stepExpr);
					copiedIncAnns = true;
				}
				if (!copiedCondAnns) {
					if (!stepExpr.IsNull)
						CopyAnnotations(cond, stepExpr);
				}
				if (!copiedIncAnns) {
					if (!toExpr.IsNull)
						CopyAnnotations(increment, toExpr);
				}

				return new ForStatement() {
					Variable = iteratorVariable,
					ToExpression = toExpr,
					StepExpression = stepExpr,
					Body = (BlockStatement)match.Get<CS.Statement>("body").Single().AcceptVisitor(this, data)
				};
			}

		end:
			convertedKind[forStatement] = ConvertedStatementKind.While;
			// Visit the initializers first: they declare the loop variable, and VB needs the
			// declaration to exist before the condition and body refer to it. Converting them
			// afterwards also left the rename map empty while the body was being converted.
			foreach (var initializer in forStatement.Initializers)
				blocks.Peek().Statements.Add((Statement)initializer.AcceptVisitor(this, data));
			var stmt = new WhileStatement() {
				Condition = (Expression)forStatement.Condition.AcceptVisitor(this, data),
				Body = (BlockStatement)forStatement.EmbeddedStatement.AcceptVisitor(this, data)
			};
			ConvertNodes(forStatement.Iterators, stmt.Body.Statements);
			convertedKind.Remove(forStatement);

			return EndNode(forStatement, stmt);
		}
		readonly Dictionary<CS.AstNode, ConvertedStatementKind> convertedKind = new Dictionary<CS.AstNode, ConvertedStatementKind>();

		bool IsEqual(object value, int num)
		{
			if (value is byte)
				return (byte)value == num;
			if (value is sbyte)
				return (sbyte)value == num;
			if (value is short)
				return (short)value == num;
			if (value is ushort)
				return (ushort)value == num;
			if (value is int)
				return (int)value == num;
			if (value is uint)
				return (uint)value == num;
			if (value is long)
				return (long)value == num;
			if (value is ulong)
				return (ulong)value == (ulong)num;

			throw new InvalidCastException();
		}

		public AstNode VisitGotoCaseStatement(CS.GotoCaseStatement gotoCaseStatement, object data)
		{
			if (switchLabelMaps.Count > 0) {
				var key = gotoCaseStatement.LabelExpression.ToString();
				if (switchLabelMaps.Peek().ByCaseValue.TryGetValue(key, out var label))
					return EndNode(gotoCaseStatement, new GoToStatement { Label = MakeLabelExpression(label) });
			}
			throw new NotImplementedException("VB output for 'goto case' outside a switch");
		}

		public AstNode VisitGotoDefaultStatement(CS.GotoDefaultStatement gotoDefaultStatement, object data)
		{
			if (switchLabelMaps.Count > 0 && switchLabelMaps.Peek().DefaultLabel is string label)
				return EndNode(gotoDefaultStatement, new GoToStatement { Label = MakeLabelExpression(label) });
			throw new NotImplementedException("VB output for 'goto default' outside a switch");
		}

		static Expression MakeLabelExpression(string name) {
			var ident = new IdentifierExpression();
			ident.Identifier = Identifier.Create((object)null, name);
			return ident;
		}

		public AstNode VisitGotoStatement(CS.GotoStatement gotoStatement, object data)
		{
			return EndNode(gotoStatement, new GoToStatement() { Label = new IdentifierExpression() { Identifier = Identifier.Create(BoxedTextColor.Label, gotoStatement.Label) } });
		}

		public AstNode VisitIfElseStatement(CS.IfElseStatement ifElseStatement, object data)
		{
			// "If h IsNot Nothing Then RaiseEvent ..." where h is a suppressed copy: RaiseEvent
			// already skips the call when there are no subscribers.
			if (ifElseStatement.FalseStatement.IsNull && ifElseStatement.TrueStatement is CS.BlockStatement guardBody &&
					guardBody.Statements.Count == 1 && ifElseStatement.Condition is CS.BinaryOperatorExpression guardCondition &&
					guardCondition.Operator == CS.BinaryOperatorType.InEquality &&
					guardCondition.Left is CS.IdentifierExpression guardedIdentifier &&
					guardCondition.Right is CS.NullReferenceExpression &&
					suppressedEventAliases.Contains(guardedIdentifier.Identifier)) {
				if (guardBody.Statements.First().AcceptVisitor(this, data) is Statement guardedStatement)
					return EndNode(ifElseStatement, guardedStatement);
			}

			var stmt = new IfElseStatement();

			stmt.Condition = (Expression)ifElseStatement.Condition.AcceptVisitor(this, data);
			stmt.Body = (Statement)ifElseStatement.TrueStatement.AcceptVisitor(this, data);
			stmt.ElseBlock = (Statement)ifElseStatement.FalseStatement.AcceptVisitor(this, data);

			return EndNode(ifElseStatement, stmt);
		}

		public AstNode VisitLabelStatement(CS.LabelStatement labelStatement, object data)
		{
			return EndNode(labelStatement, new LabelDeclarationStatement() { Label = new IdentifierExpression() { Identifier = Identifier.Create(BoxedTextColor.Label, labelStatement.Label) } });
		}

		public AstNode VisitLockStatement(CS.LockStatement lockStatement, object data)
		{
			var stmt = new SyncLockStatement();

			stmt.Expression = (Expression)lockStatement.Expression.AcceptVisitor(this, data);
			stmt.Body = (BlockStatement)lockStatement.EmbeddedStatement.AcceptVisitor(this, data);

			return EndNode(lockStatement, stmt);
		}

		public AstNode VisitReturnStatement(CS.ReturnStatement returnStatement, object data)
		{
			var stmt = new ReturnStatement((Expression)returnStatement.Expression.AcceptVisitor(this, data));

			return EndNode(returnStatement, stmt);
		}

		public AstNode VisitSwitchStatement(CS.SwitchStatement switchStatement, object data)
		{
			var stmt = new SelectStatement() { Expression = (Expression)switchStatement.Expression.AcceptVisitor(this, data) };

			// "goto case"/"goto default" only need labels when they actually occur; adding a
			// label to every switch would change the output of the whole corpus for nothing.
			var sections = switchStatement.SwitchSections.ToList();
			bool needsLabels = switchStatement.Descendants.Any(n => n is CS.GotoCaseStatement || n is CS.GotoDefaultStatement);
			if (needsLabels) {
				var map = new SwitchLabelMap();
				for (int i = 0; i < sections.Count; i++) {
					var section = sections[i];
					string label = "__case_" + (i + 1);
					map.BySection[section] = label;
					foreach (var caseLabel in section.CaseLabels) {
						if (caseLabel.Expression.IsNull)
							map.DefaultLabel = label;
						else
							map.ByCaseValue[caseLabel.Expression.ToString()] = label;
					}
				}
				switchLabelMaps.Push(map);
			}
			try {
				// VB requires "Case Else" to be the last section, while C# allows default
				// anywhere. Reordering is safe: the default branch only runs when no case matches.
				foreach (var section in sections.OrderBy(s => s.CaseLabels.Any(l => l.Expression.IsNull) ? 1 : 0))
					stmt.Cases.Add((CaseStatement)section.AcceptVisitor(this, data));
			}
			finally {
				if (needsLabels)
					switchLabelMaps.Pop();
			}
			stmt.HiddenEnd = new System.Collections.Generic.List<dnSpy.Contracts.Decompiler.ILSpan>();

			return EndNode(switchStatement, stmt);
		}

		public AstNode VisitSwitchSection(CS.SwitchSection switchSection, object data)
		{
			var caseStmt = new CaseStatement();
			ConvertNodes(switchSection.CaseLabels, caseStmt.Clauses);
			if (switchSection.Statements.Count == 1 && switchSection.Statements.FirstOrDefault() is CS.BlockStatement)
				caseStmt.Body = (BlockStatement)switchSection.Statements.FirstOrDefault().AcceptVisitor(this, data);
			else {
				caseStmt.Body = new BlockStatement();
				ConvertNodes(switchSection.Statements, caseStmt.Body.Statements);
			}
			if (caseStmt.Body.LastOrDefault() is ExitStatement && ((ExitStatement)caseStmt.Body.LastOrDefault()).ExitKind == ExitKind.Select)
				caseStmt.Body.LastOrDefault().Remove();
			if (switchLabelMaps.Count > 0 && switchLabelMaps.Peek().BySection.TryGetValue(switchSection, out var labelStatement)) {
				var labelDecl = new LabelDeclarationStatement { Label = MakeLabelExpression(labelStatement) };
				var first = caseStmt.Body.FirstOrDefault();
				if (first is null)
					caseStmt.Body.Add(labelDecl);
				else
					caseStmt.Body.Statements.InsertBefore(first, labelDecl);
			}
			return EndNode(switchSection, caseStmt);
		}

		public AstNode VisitCaseLabel(CS.CaseLabel caseLabel, object data)
		{
			return EndNode(caseLabel, new SimpleCaseClause() { Expression = (Expression)caseLabel.Expression.AcceptVisitor(this, data) });
		}

		public AstNode VisitThrowStatement(CS.ThrowStatement throwStatement, object data)
		{
			return EndNode(throwStatement, new ThrowStatement((Expression)throwStatement.Expression.AcceptVisitor(this, data)));
		}

		public AstNode VisitTryCatchStatement(CS.TryCatchStatement tryCatchStatement, object data)
		{
			// Reset the implicit-catch-variable counter per try statement, matching
			// CodeConverter's behavior of numbering catch clauses within each try.
			catchVariableCount = 0;

			var stmt = new TryStatement();

			stmt.Body = (BlockStatement)tryCatchStatement.TryBlock.AcceptVisitor(this, data);
			stmt.FinallyBlock = (BlockStatement)tryCatchStatement.FinallyBlock.AcceptVisitor(this, data);
			ConvertNodes(tryCatchStatement.CatchClauses, stmt.CatchBlocks);

			return EndNode(tryCatchStatement, stmt);
		}

		public AstNode VisitCatchClause(CS.CatchClause catchClause, object data)
		{
			var clause = new CatchBlock();

			if (!catchClause.Type.IsNull) {
				clause.ExceptionType = (AstType)catchClause.Type.AcceptVisitor(this, data);
				// VB requires a variable name in "Catch ex As Exception". C# 6 allows
				// "catch (Exception)" without a variable, which the 3.2 engine emits
				// with an empty VariableNameToken. Generate a unique name in that case,
				// otherwise NRefactory's OutputVisitor asserts (and the output would be
				// invalid VB anyway). The naming scheme mirrors CodeConverter:
				// "__unused{SimpleTypeName}{n}__" (n restarts per try statement).
				if (catchClause.VariableNameToken.IsNull) {
					string typeName = clause.ExceptionType.ToString();
					string simpleName = typeName.Split('.').Last().Split('`')[0];
					clause.ExceptionVariable = Identifier.Create(catchClause.Annotations, "__unused" + simpleName + (++catchVariableCount) + "__");
				}
			}
			if (!catchClause.VariableNameToken.IsNull)
				clause.ExceptionVariable = Identifier.Create(catchClause.VariableNameToken.Annotations, catchClause.VariableName);
			if (!catchClause.Condition.IsNull)
				clause.WhenExpression = (Expression)catchClause.Condition.AcceptVisitor(this, data);
			ConvertNodes(catchClause.Body.Statements, clause.Statements);

			return EndNode(catchClause, clause);
		}

		public AstNode VisitUncheckedStatement(CS.UncheckedStatement uncheckedStatement, object data)
		{
			var body = uncheckedStatement.Body.AcceptVisitor(this, data);
			return EndNode<AstNode>(uncheckedStatement, body);
		}

		public AstNode VisitUnsafeStatement(CS.UnsafeStatement unsafeStatement, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitUsingStatement(CS.UsingStatement usingStatement, object data)
		{
			var stmt = new UsingStatement();

			stmt.Resources.Add(usingStatement.ResourceAcquisition.AcceptVisitor(this, data));
			stmt.Body = (BlockStatement)usingStatement.EmbeddedStatement.AcceptVisitor(this, data);

			return EndNode(usingStatement, stmt);
		}

		public AstNode VisitVariableDeclarationStatement(CS.VariableDeclarationStatement variableDeclarationStatement, object data)
		{
			bool allSuppressed = variableDeclarationStatement.Variables.Count > 0;
			foreach (var variable in variableDeclarationStatement.Variables) {
				var eventName = GetEventName(variable.Initializer);
				if (eventName != null)
					eventAliases[variable.Name] = eventName;
				if (!suppressedEventAliases.Contains(variable.Name))
					allSuppressed = false;
				variableTypes[variable.Name] = variableDeclarationStatement.Type.ToString();
				localDeclarationNames[variable] = DeclareLocal(variable.Name);
			}
			// The copy exists only for the null check that RaiseEvent makes redundant.
			if (allSuppressed)
				return null;
			var decl = new LocalDeclarationStatement();
			decl.Modifiers = Modifiers.Dim;
			ConvertNodes(variableDeclarationStatement.Variables, decl.Variables);

			return EndNode(variableDeclarationStatement, decl);
		}

		public AstNode VisitWhileStatement(CS.WhileStatement whileStatement, object data)
		{
			// "while (--i >= 0)" has no direct VB form: an assignment is not an expression, and a
			// helper function would have to exist. Lower it to
			//     While True
			//         i -= 1
			//         If Not (i >= 0) Then Exit While
			//         <body>
			//     End While
			// which re-runs the update on every iteration, exactly like the original condition.
			var updates = new List<CS.UnaryOperatorExpression>();
			if (TryCollectConditionUpdates(whileStatement.Condition, updates)) {
				foreach (var update in updates)
					loopConditionUpdates[update] = update.Operator == CS.UnaryOperatorType.PostDecrement ||
						update.Operator == CS.UnaryOperatorType.PostIncrement;
				Expression condition;
				try {
					condition = (Expression)whileStatement.Condition.AcceptVisitor(this, data);
				}
				finally {
					foreach (var update in updates)
						loopConditionUpdates.Remove(update);
				}
				var body = (BlockStatement)whileStatement.EmbeddedStatement.AcceptVisitor(this, data);

				var loop = new WhileStatement();
				loop.Condition = new PrimitiveExpression(true);
				var block = new BlockStatement();
				foreach (var update in updates) {
					var target = (Expression)update.Expression.AcceptVisitor(this, data);
					block.Add(new ExpressionStatement(new AssignmentExpression(target,
						update.Operator == CS.UnaryOperatorType.Increment || update.Operator == CS.UnaryOperatorType.PostIncrement
							? AssignmentOperatorType.Add
							: AssignmentOperatorType.Subtract,
						new PrimitiveExpression(1))));
				}
				var guard = new IfElseStatement();
				// The parentheses are required: without them "Not a AndAlso b" negates only a.
				var negated = new ParenthesizedExpression();
				negated.Expression = condition;
				guard.Condition = new UnaryOperatorExpression(UnaryOperatorType.Not, negated);
				guard.Body = new ExitStatement(ExitKind.While);
				block.Add(guard);
				foreach (var statement in body.Statements.ToList()) {
					body.Statements.Remove(statement);
					block.Add(statement);
				}
				loop.Body = block;

				return EndNode(whileStatement, loop);
			}

			var stmt = new WhileStatement() {
				Condition = (Expression)whileStatement.Condition.AcceptVisitor(this, data),
				Body = (BlockStatement)whileStatement.EmbeddedStatement.AcceptVisitor(this, data)
			};

			return EndNode(whileStatement, stmt);
		}

		static bool IsIncrementOrDecrement(CS.UnaryOperatorType op) {
			return op == CS.UnaryOperatorType.Increment || op == CS.UnaryOperatorType.Decrement ||
				op == CS.UnaryOperatorType.PostIncrement || op == CS.UnaryOperatorType.PostDecrement;
		}

		/// <summary>
		/// Collects the increments a loop condition performs before any short-circuit can skip
		/// them. Returns false when the condition holds an increment that cannot be lowered this
		/// way, in which case the caller falls back to the previous behaviour.
		/// </summary>
		static bool TryCollectConditionUpdates(CS.Expression expression, List<CS.UnaryOperatorExpression> updates) {
			switch (expression) {
			case CS.UnaryOperatorExpression unary when IsIncrementOrDecrement(unary.Operator):
				// The target must be re-evaluated safely: an identifier or member reference.
				if (!(unary.Expression is CS.IdentifierExpression) && !(unary.Expression is CS.MemberReferenceExpression))
					return false;
				// Only the prefix forms are lowered; the postfix value needs the old one, which
				// complicates the rewrite for no gain (the helper is not used for those anyway).
				if (unary.Operator != CS.UnaryOperatorType.Increment && unary.Operator != CS.UnaryOperatorType.Decrement)
					return false;
				updates.Add(unary);
				return true;
			case CS.BinaryOperatorExpression binary:
				// The left operand is always evaluated; the right one may be short-circuited.
				if (!TryCollectConditionUpdates(binary.Left, updates))
					return false;
				return !ContainsIncrement(binary.Right);
			case CS.ParenthesizedExpression parenthesized:
				return TryCollectConditionUpdates(parenthesized.Expression, updates);
			default:
				return !ContainsIncrement(expression);
			}
		}

		static bool ContainsIncrement(CS.Expression expression) {
			return expression.Descendants.OfType<CS.UnaryOperatorExpression>()
				.Any(u => IsIncrementOrDecrement(u.Operator));
		}

		public AstNode VisitYieldBreakStatement(CS.YieldBreakStatement yieldBreakStatement, object data)
		{
			var frame = members.Peek();
			frame.inIterator = true;
			return EndNode(yieldBreakStatement, new ReturnStatement());
		}

		public AstNode VisitYieldReturnStatement(CS.YieldReturnStatement yieldReturnStatement, object data)
		{
			var frame = members.Peek();
			frame.inIterator = true;
			return EndNode(yieldReturnStatement, new YieldStatement((Expression)yieldReturnStatement.Expression.AcceptVisitor(this, data)));
		}

		public AstNode VisitAccessor(CS.Accessor accessor, object data)
		{
			var result = new Accessor();

			ConvertNodes(accessor.Attributes, result.Attributes);
			ConvertNodes(accessor.ModifierTokens, result.ModifierTokens);
			result.Body = (BlockStatement)accessor.Body.AcceptVisitor(this, data);

			// "uint Start => start;" keeps its value in PropertyDeclaration.ExpressionBody.
			// The synthesized accessor body is then just "return;", which VB rejects inside
			// a Get and which silently loses the value.
			if (accessor.Parent is CS.PropertyDeclaration property && !property.ExpressionBody.IsNull
					&& (result.Body is null || result.Body.Statements.Count == 0
						|| (result.Body.Statements.Count == 1
							&& result.Body.FirstOrDefault() is ReturnStatement emptyReturn
							&& emptyReturn.Expression.IsNull))) {
				var body = new BlockStatement();
				body.Add(new ReturnStatement((Expression)property.ExpressionBody.AcceptVisitor(this, data)));
				result.Body = body;
			}

			return EndNode(accessor, result);
		}

		public AstNode VisitConstructorDeclaration(CS.ConstructorDeclaration constructorDeclaration, object data)
		{
			ResetMethodState(constructorDeclaration);
			// A Module's constructor must not be Shared (BC30433); methods get the same
			// treatment before their modifiers are converted.
			if (IsOwnerAModule(constructorDeclaration))
				constructorDeclaration.Modifiers &= ~CS.Modifiers.Static;
			var result = new ConstructorDeclaration();

			ConvertNodes(constructorDeclaration.Attributes, result.Attributes);
			ConvertNodes(constructorDeclaration.ModifierTokens, result.ModifierTokens);
			ConvertNodes(constructorDeclaration.Parameters, result.Parameters);
			result.Body = (BlockStatement)constructorDeclaration.Body.AcceptVisitor(this, data);
			if (!constructorDeclaration.Initializer.IsNull)
				result.Body.Statements.InsertBefore(result.Body.FirstOrDefault(), (Statement)constructorDeclaration.Initializer.AcceptVisitor(this, data));

			return EndNode(constructorDeclaration, result);
		}

		public AstNode VisitConstructorInitializer(CS.ConstructorInitializer constructorInitializer, object data)
		{
			InstanceExpression instExpr;
			Identifier identifier;
			var result = new InvocationExpression(
				new MemberAccessExpression() {
					Target = instExpr = new InstanceExpression(constructorInitializer.ConstructorInitializerType == CS.ConstructorInitializerType.This ? InstanceExpressionType.Me : InstanceExpressionType.MyBase, ICSharpCode.NRefactory.TextLocation.Empty),
					MemberName = identifier = new Identifier(BoxedTextColor.Keyword, "New", ICSharpCode.NRefactory.TextLocation.Empty)
				}
			);
			// The engine's ConvertConstructorCallIntoInitializer copies the invocation's
			// annotations onto the initializer, so the ctor resolves through the same chain
			// as a call target. Fall back to the enclosing type's base type / default ctor
			// when the resolver yields nothing - the 2.3 engine emitted the base type
			// reference on MyBase and the ctor reference on New.
			var ctor = constructorInitializer.Annotation<dnlib.DotNet.IMethod>()
				?? ResolveDnlibReference(constructorInitializer) as dnlib.DotNet.IMethod;
			var enclosingType = FindEnclosingType(constructorInitializer);
			if (ctor is null && constructorInitializer.ConstructorInitializerType == CS.ConstructorInitializerType.Base)
				ctor = (enclosingType?.BaseType.Resolve() as TypeDef)?.Methods
					.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == constructorInitializer.Arguments.Count);
			instExpr.AddAnnotation(ctor?.DeclaringType
				?? (constructorInitializer.ConstructorInitializerType == CS.ConstructorInitializerType.Base
					? (object?)enclosingType?.BaseType.Resolve()
					: (object?)enclosingType));
			identifier.AddAnnotation(ctor);
			CopyAnnotations(constructorInitializer, instExpr);
			ConvertNodes(constructorInitializer.Arguments, result.Arguments);

			return EndNode(constructorInitializer, new ExpressionStatement(result));
		}

		public AstNode VisitDestructorDeclaration(CS.DestructorDeclaration destructorDeclaration, object data)
		{
			ResetMethodState(destructorDeclaration);
			var finalizer = new MethodDeclaration() { Name = Identifier.Create(BoxedTextColor.InstanceMethod, "Finalize"), IsSub = true };

			ConvertNodes(destructorDeclaration.Attributes, finalizer.Attributes);
			ConvertNodes(destructorDeclaration.ModifierTokens, finalizer.ModifierTokens);
			finalizer.Modifiers |= Modifiers.Overrides;
			finalizer.Body = (BlockStatement)destructorDeclaration.Body.AcceptVisitor(this, data);

			return EndNode(destructorDeclaration, finalizer);
		}

		public AstNode VisitEnumMemberDeclaration(CS.EnumMemberDeclaration enumMemberDeclaration, object data)
		{
			var result = new EnumMemberDeclaration();

			ConvertNodes(enumMemberDeclaration.Attributes, result.Attributes);
			result.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(enumMemberDeclaration), enumMemberDeclaration.NameToken.Annotations), enumMemberDeclaration.Name);
			result.Value = (Expression)enumMemberDeclaration.Initializer.AcceptVisitor(this, data);

			return EndNode(enumMemberDeclaration, result);
		}

		bool IsOwnerAModule(CS.AstNode node)
		{
			if (types.Count > 0 && types.Peek().ClassType == ClassType.Module)
				return true;
			var def = node.Annotation<dnlib.DotNet.IMemberDef>();
			if (def == null)
				return false;
			var td = def.DeclaringType;
			return td != null && td.DeclaringType == null && td.IsSealed && td.IsDefined(stringMicrosoftVisualBasicCompilerServices, stringStandardModuleAttribute);
		}
		static readonly UTF8String stringMicrosoftVisualBasicCompilerServices = new UTF8String("Microsoft.VisualBasic.CompilerServices");
		static readonly UTF8String stringStandardModuleAttribute = new UTF8String("StandardModuleAttribute");

		public AstNode VisitEventDeclaration(CS.EventDeclaration eventDeclaration, object data)
		{
			members.Push(new MemberInfo());

			EventDeclaration result = null;
			foreach (var evt in eventDeclaration.Variables) {
				result = new EventDeclaration();

				ConvertNodes(eventDeclaration.Attributes, result.Attributes);
				if (types.Any()) {
					if (IsOwnerAModule(eventDeclaration))
						eventDeclaration.Modifiers &= ~CS.Modifiers.Static;
				}
				// VB has no abstract events (BC30243): the modifier is dropped and the event stays
				// field-like, so derived classes can still raise it.
				eventDeclaration.Modifiers &= ~CS.Modifiers.Abstract;
				result.Modifiers = ConvertModifiers(eventDeclaration.Modifiers, eventDeclaration);
				result.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(eventDeclaration), evt.NameToken.Annotations), evt.Name);
				result.ReturnType = (AstType)eventDeclaration.ReturnType.AcceptVisitor(this, data);

				CreateImplementsClausesForEvent(eventDeclaration, result);
			}

			members.Pop();

			return EndNode(eventDeclaration, result);
		}

		/// <summary>
		/// True for the compiler-generated field event pattern: no raise accessor and both accessors
		/// marked CompilerGenerated.
		/// </summary>
		bool IsCompilerGeneratedFieldEvent(CS.CustomEventDeclaration declaration) {
			CS.Attribute attribute;
			return HasAttribute(declaration.AddAccessor.Attributes, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", out attribute) &&
				HasAttribute(declaration.RemoveAccessor.Attributes, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", out attribute);
		}

		/// <summary>
		/// Builds the RaiseEvent body BC31132 requires, when the C# accessors say raising is
		/// expressible in VB. Three deterministic forms (verified against the corpus classification):
		/// - empty add/remove: the event has no storage, raising does nothing;
		/// - a single Throw in both accessors: the event is a stub, VB throws the same;
		/// - both accessors delegate to one private delegate-typed field: raising invokes that
		///   field (the copy+guard pattern the class itself uses for manual raises).
		/// List-backed (WeakEventList.Add/Remove) and forwarding accessors stay unsynthesized:
		/// their raise semantics cannot be expressed in VB, so they keep the explicit compile
		/// error instead of silently dropping the raise.
		/// </summary>
		/// <summary>
		/// VB's Custom Event accessors must carry their value parameter in the header
		/// ("AddHandler(value As EventHandler(Of T))", BC31133) while C# keeps it implicit; the
		/// parameter node is added here, after the body conversion has already run.
		/// </summary>
		void SynthesizeEventAccessorValueParameter(Accessor accessor, CS.CustomEventDeclaration customEventDeclaration, object data) {
			if (accessor.Parameters.Any())
				return;
			accessor.Parameters.Add(new ParameterDeclaration {
				Name = new Identifier(BoxedTextColor.Parameter, "value", ICSharpCode.NRefactory.TextLocation.Empty),
				Type = (AstType)customEventDeclaration.ReturnType.AcceptVisitor(this, data),
			});
		}

		BlockStatement CreateSynthesizedRaiseEventBody(CS.CustomEventDeclaration declaration, List<ParameterDeclaration> parameters) {
			parameters.Clear();
			var eventDef = declaration.Annotation<EventDef>();
			if (eventDef is null || eventDef.DeclaringType?.IsInterface == true)
				return null;
			var addAccessor = declaration.AddAccessor;
			var removeAccessor = declaration.RemoveAccessor;
			if (addAccessor is null || removeAccessor is null || addAccessor.Body is null || removeAccessor.Body is null)
				return null;

			// (a) empty add/remove
			if (addAccessor.Body.Statements.Count == 0 && removeAccessor.Body.Statements.Count == 0)
				return new BlockStatement();

			// (b) both accessors are a single throw
			var addThrow = SingleThrowStatement(addAccessor.Body);
			if (addThrow != null) {
				var removeThrow = SingleThrowStatement(removeAccessor.Body);
				if (removeThrow != null) {
					var throwBody = new BlockStatement();
					throwBody.Add((Statement)addThrow.AcceptVisitor(this, null));
					return throwBody;
				}
				return null;
			}

			// (c) both accessors assign the same private delegate-typed field
			var typeDef = eventDef.DeclaringType;
			var addFields = AssignedFieldNames(addAccessor.Body, typeDef);
			var removeFields = AssignedFieldNames(removeAccessor.Body, typeDef);
			var commonFields = addFields.Where(removeFields.Contains).ToList();
			if (commonFields.Count != 1)
				return null;
			var field = typeDef.Fields.FirstOrDefault(f => f.Name == commonFields[0]);
			if (field is null || !IsFieldOfDelegateType(field.FieldType, eventDef.EventType))
				return null;
			var delegateParameters = GetDelegateInvokeParameters(eventDef);
			if (delegateParameters is null)
				return null;

			var copyName = "copy";
			while (delegateParameters.Any(p => string.Equals(p.Name, copyName, StringComparison.OrdinalIgnoreCase)) ||
					string.Equals(field.Name?.String, copyName, StringComparison.OrdinalIgnoreCase))
				copyName += "_";
			var copyLocal = MakeIdentifierExpression(copyName);

			var decl = new LocalDeclarationStatement();
			decl.Modifiers = Modifiers.Dim;
			var declarator = new VariableDeclaratorWithTypeAndInitializer();
			declarator.Identifiers.Add(new VariableIdentifier() { Name = Identifier.Create((object)null, copyName) });
			declarator.Type = CreateType(field.FieldType.ToTypeDefOrRef());
			declarator.Initializer = MakeIdentifierExpression(field.Name?.String ?? commonFields[0]);
			decl.Variables.Add(declarator);

			var guard = new IfElseStatement();
			guard.Condition = new BinaryOperatorExpression((Expression)copyLocal.Clone(),
				BinaryOperatorType.ReferenceInequality, new PrimitiveExpression(null));
			var guardedBody = new BlockStatement();
			var invoke = new InvocationExpression((Expression)copyLocal.Clone());
			foreach (var parameter in delegateParameters)
				invoke.Arguments.Add(MakeIdentifierExpression(parameter.Name));
			guardedBody.Add(new ExpressionStatement(invoke));
			guard.Body = guardedBody;

			var body = new BlockStatement();
			body.Add(decl);
			body.Add(guard);
			parameters.AddRange(delegateParameters.Select(p => new ParameterDeclaration() {
				Name = Identifier.Create((object)null, p.Name),
				Type = p.Type,
			}));
			return body;
		}

		/// <summary>The body's only statement when it is a throw, null otherwise.</summary>
		static CS.ThrowStatement SingleThrowStatement(CS.BlockStatement body) {
			if (body is null || body.Statements.Count != 1)
				return null;
			return body.Statements.First() as CS.ThrowStatement;
		}

		/// <summary>
		/// Names of fields of <paramref name="typeDef"/> directly assigned in the block. Event
		/// subscription statements (left side is a member of another object) are not field
		/// assignments and do not participate.
		/// </summary>
		static HashSet<string> AssignedFieldNames(CS.BlockStatement body, TypeDef typeDef) {
			var result = new HashSet<string>(StringComparer.Ordinal);
			if (body is null || typeDef is null)
				return result;
			foreach (var assignment in body.Descendants.OfType<CS.AssignmentExpression>()) {
				if (assignment.Operator != CS.AssignmentOperatorType.Assign)
					continue;
				if (!(assignment.Left is CS.IdentifierExpression identifier))
					continue;
				if (typeDef.Fields.Any(f => f.Name == identifier.Identifier))
					result.Add(identifier.Identifier);
			}
			return result;
		}

		/// <summary>
		/// True when the field's type is the event's delegate type (name-level comparison, both
		/// sides through the same normalization - the arity and generic arguments differ in
		/// spelling between the metadata field and the event reference).
		/// </summary>
		static bool IsFieldOfDelegateType(TypeSig fieldType, ITypeDefOrRef delegateType) {
			if (fieldType is null || delegateType is null)
				return false;
			var expected = AstAnnotationBinder.NormalizeParameterTypeName(fieldType.TypeName ?? "");
			var actual = AstAnnotationBinder.NormalizeParameterTypeName(delegateType.FullName ?? "");
			return expected.Length > 0 && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
		}

		sealed class DelegateParameter {
			public string Name;
			public AstType Type;
		}

		/// <summary>
		/// The RaiseEvent accessor must repeat the delegate's Invoke signature. The delegate is
		/// resolved in this module first, then through cecil for framework delegates
		/// (System.EventHandler&lt;T&gt;); unresolvable delegates mean the raise cannot be
		/// synthesized safely and the caller keeps the BC31132 error.
		/// </summary>
		List<DelegateParameter> GetDelegateInvokeParameters(EventDef eventDef) {
			var delegateType = eventDef.EventType;
			if (delegateType is null)
				return null;

			// The event's own generic arguments, used to substitute the delegate's type
			// parameters ("TEventArgs" in EventHandler<T>).
			GenericInstSig constructed = null;
			if (delegateType is TypeSpec spec && spec.TypeSig?.RemovePinnedAndModifiers() is GenericInstSig generic)
				constructed = generic;

			var delegateDef = delegateType.ResolveTypeDef() ?? constructed?.GenericType?.TypeDefOrRef?.ResolveTypeDef();
			if (delegateDef != null)
				return GetInvokeParameters(delegateDef, constructed);

			var cecilReference = DnlibCecilBridge.TryGetTypeReference(module, delegateType);
			var cecilDelegate = cecilReference?.Resolve();
			if (cecilDelegate != null)
				return GetInvokeParameters(cecilDelegate, cecilReference as Mono.Cecil.GenericInstanceType);
			return null;
		}

		List<DelegateParameter> GetInvokeParameters(TypeDef delegateDef, GenericInstSig constructed) {
			var invoke = delegateDef.Methods.FirstOrDefault(m => m.Name == "Invoke" && m.MethodSig != null);
			var sig = invoke?.MethodSig;
			if (invoke is null || sig is null)
				return null;
			var normalParameters = invoke.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
			if (normalParameters.Count != sig.Params.Count)
				return null;
			var result = new List<DelegateParameter>();
			for (int i = 0; i < sig.Params.Count; i++) {
				var parameter = normalParameters[i];
				var parameterSig = sig.Params[i];
				// A delegate's own type parameter ("TEventArgs") is replaced by the argument the
				// event spelled out; anything still generic cannot be spelled and fails synthesis.
				if (parameterSig.RemovePinnedAndModifiers() is GenericSig genericParameter) {
					var argument = constructed?.GenericArguments.ElementAtOrDefault(genericParameter.GenericParam?.Number ?? -1);
					if (argument is null)
						return null;
					parameterSig = argument;
				}
				var type = CreateTypeFromSig(module, parameterSig);
				if (type is null)
					return null;
				result.Add(new DelegateParameter { Name = parameter.Name ?? "arg" + (i + 1), Type = type });
			}
			return result;
		}

		List<DelegateParameter> GetInvokeParameters(Mono.Cecil.TypeDefinition delegateDef, Mono.Cecil.GenericInstanceType constructed) {
			// Invoke is abstract on delegates (no IL body), so only the name decides.
			var invoke = delegateDef.Methods.FirstOrDefault(m => m.Name == "Invoke");
			if (invoke is null)
				return null;
			var result = new List<DelegateParameter>();
			foreach (var parameter in invoke.Parameters) {
				var parameterType = parameter.ParameterType;
				if (parameterType is Mono.Cecil.GenericParameter genericParameter) {
					if (constructed is null || genericParameter.Position >= constructed.GenericArguments.Count)
						return null;
					parameterType = constructed.GenericArguments[genericParameter.Position];
				}
				var type = CreateTypeFromCecil(parameterType);
				if (type is null)
					return null;
				result.Add(new DelegateParameter { Name = string.IsNullOrEmpty(parameter.Name) ? "arg" + (result.Count + 1) : parameter.Name, Type = type });
			}
			return result;
		}

		/// <summary>
		/// Same conversion path as <see cref="CreateType"/>, for a type that only exists as a
		/// cecil reference (framework delegates reached through the bridge).
		/// </summary>
		AstType CreateTypeFromCecil(Mono.Cecil.TypeReference cecilType) {
			if (cecilType is null)
				return null;
			try {
				var csAstType = ICSharpCode.Decompiler.CSharp.CSharpDecompiler.ConvertType(cecilType);
				var vbType = (AstType)csAstType.AcceptVisitor(this, null);
				return QualifyMemberType(vbType, cecilType);
			}
			catch {
				return null;
			}
		}

		public AstNode VisitCustomEventDeclaration(CS.CustomEventDeclaration customEventDeclaration, object data)
		{
			var result = new EventDeclaration();

			members.Push(new MemberInfo());

			// Accessor bodies are member bodies: clear stale locals and set CurrentType so
			// resolver lookups inside them work.
			ResetMethodState(customEventDeclaration);

			ConvertNodes(customEventDeclaration.Attributes, result.Attributes);
			if (IsOwnerAModule(customEventDeclaration))
				customEventDeclaration.Modifiers &= ~CS.Modifiers.Static;
			result.Modifiers = ConvertModifiers(customEventDeclaration.Modifiers, customEventDeclaration);
			// VB requires all three accessors on a Custom Event, while a C# compiler-generated
			// field event has no RaiseEvent counterpart at all (BC31132). Its add/remove only do
			// the thread-safe bookkeeping a VB field event does itself, so such an event is
			// emitted field-like instead of dropping or inventing a RaiseEvent body.
			result.IsCustom = !IsCompilerGeneratedFieldEvent(customEventDeclaration);
			result.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(customEventDeclaration), customEventDeclaration.NameToken.Annotations), customEventDeclaration.Name);
			result.ReturnType = (AstType)customEventDeclaration.ReturnType.AcceptVisitor(this, data);
			CreateImplementsClausesForEvent(customEventDeclaration, result);
			if (result.IsCustom) {
				// The C# add/remove accessors keep their value parameter implicit, but VB requires
				// it in the accessor header ("AddHandler(value As EventHandler(Of T))", BC31133)
				// and the bodies reference it. Register the type before the bodies are converted.
				variableTypes["value"] = customEventDeclaration.ReturnType.ToString();
				result.AddHandlerBlock = (Accessor)customEventDeclaration.AddAccessor.AcceptVisitor(this, data);
				result.RemoveHandlerBlock = (Accessor)customEventDeclaration.RemoveAccessor.AcceptVisitor(this, data);
				SynthesizeEventAccessorValueParameter(result.AddHandlerBlock, customEventDeclaration, data);
				SynthesizeEventAccessorValueParameter(result.RemoveHandlerBlock, customEventDeclaration, data);
				// A VB Custom Event must declare RaiseEvent (BC31132) and the C# tree has no raise
				// accessor at all. Only the forms whose raise semantics VB can express get one
				// synthesized; list-backed or forwarding accessors keep the explicit error.
				var parameters = new List<ParameterDeclaration>();
				var raiseBody = CreateSynthesizedRaiseEventBody(customEventDeclaration, parameters);
				if (raiseBody != null) {
					var raiseAccessor = new Accessor();
					foreach (var parameter in parameters)
						raiseAccessor.Parameters.Add(parameter);
					raiseAccessor.Body = raiseBody;
					result.RaiseEventBlock = raiseAccessor;
				}
			}

			members.Pop();

			return EndNode(customEventDeclaration, result);
		}

		public AstNode VisitFieldDeclaration(CS.FieldDeclaration fieldDeclaration, object data)
		{
			var decl = new FieldDeclaration();

			members.Push(new MemberInfo());

			// The engine annotates compiler-generated data fields with an inline
			// "Not supported: data(...)" comment; emitted mid-declaration it splits the
			// field in half (BC30203). Drop the comments from the field subtree.
			foreach (var comment in fieldDeclaration.Descendants.OfType<CS.Comment>().ToList())
				comment.Remove();

			ConvertNodes(fieldDeclaration.Attributes, decl.Attributes);
			if (IsOwnerAModule(fieldDeclaration))
				fieldDeclaration.Modifiers &= ~CS.Modifiers.Static;
			var modifiers = fieldDeclaration.Modifiers;
			if (modifiers == CS.Modifiers.None)
				modifiers |= CS.Modifiers.Private;
			decl.Modifiers = ConvertModifiers(modifiers, fieldDeclaration);
			ConvertNodes(fieldDeclaration.Variables, decl.Variables);

			members.Pop();

			return EndNode(fieldDeclaration, decl);
		}

		public AstNode VisitIndexerDeclaration(CS.IndexerDeclaration indexerDeclaration, object data)
		{
			var decl = new PropertyDeclaration();

			members.Push(new MemberInfo());

			// Same as VisitPropertyDeclaration: reset per-member state and register the
			// synthesized setter parameter before the accessor bodies are converted. The index
			// parameters move up with them: the getter body may reference them.
			ResetMethodState(indexerDeclaration);
			if (!indexerDeclaration.Setter.IsNull)
				variableTypes["value"] = indexerDeclaration.ReturnType.ToString();
			ConvertNodes(indexerDeclaration.Parameters, decl.Parameters);

			ConvertNodes(indexerDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), decl.Attributes);
			decl.Getter = (Accessor)indexerDeclaration.Getter.AcceptVisitor(this, data);
			if (IsOwnerAModule(indexerDeclaration))
				indexerDeclaration.Modifiers &= ~CS.Modifiers.Static;
			decl.Modifiers = ConvertModifiers(indexerDeclaration.Modifiers, indexerDeclaration);
			decl.Name = Identifier.Create(BoxedTextColor.InstanceProperty, GetRenamedMemberName(indexerDeclaration, indexerDeclaration.Name));
			ConvertNodes(indexerDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), decl.ReturnTypeAttributes);
			CreateImplementsClausesForProperty(indexerDeclaration, decl);
			decl.ReturnType = (AstType)indexerDeclaration.ReturnType.AcceptVisitor(this, data);
			decl.Setter = (Accessor)indexerDeclaration.Setter.AcceptVisitor(this, data);

			if (!decl.Setter.IsNull) {
				Identifier id;
				decl.Setter.Parameters.Add(new ParameterDeclaration() {
				                           	Name = id = new Identifier(BoxedTextColor.Parameter, "value", ICSharpCode.NRefactory.TextLocation.Empty),
				                           	Type = (AstType)indexerDeclaration.ReturnType.AcceptVisitor(this, data),
				                           });
				var method = decl.Setter.Annotation<MethodDef>();
				if (method != null) {
					var p = method.Parameters.FirstOrDefault(a => a.IsNormalMethodParameter);
					if (p != null)
						id.AddAnnotation(p);
				}
			}

			members.Pop();

			return EndNode(indexerDeclaration, decl);
		}

		/// <summary>
		/// True when the method is the engine-generated forwarder for an accessor-level .override
		/// that sits on an interface property's getter (its body carries the engine's ".override
		/// directive" comment and the name follows the get_ convention). The real accessor keeps
		/// its method form; only the forwarder becomes a property. Setter-only forwarders keep
		/// the method form too: their return type is void, not the property type.
		/// </summary>
		bool IsGetterForwarderForInterfaceProperty(CS.MethodDeclaration method, MethodDef methodDef,
				out ITypeDefOrRef interfaceType, out string propertyName) {
			interfaceType = null!;
			propertyName = null!;
			if (!method.Name.StartsWith("get_", StringComparison.Ordinal) || methodDef.Overrides.Count == 0)
				return false;
			var body = method.Body;
			if (body is null || !body.Descendants.OfType<CS.Comment>().Any(c => c.Content != null && c.Content.Contains(".override directive")))
				return false;
			foreach (var o in methodDef.Overrides) {
				var declaration = o.MethodDeclaration;
				var accessorName = declaration.Name;
				if (accessorName is null || !accessorName.StartsWith("get_", StringComparison.Ordinal))
					continue;
				// dnlib cannot resolve types from other modules here, so cecil confirms that the
				// override target really is an interface property getter.
				var iface = DnlibCecilBridge.TryGetTypeReference(module, declaration.DeclaringType)?.Resolve();
				if (iface is null || !iface.IsInterface)
					continue;
				var property = iface.Properties.FirstOrDefault(p => p.GetMethod?.Name == accessorName);
				if (property is null)
					continue;
				interfaceType = declaration.DeclaringType;
				propertyName = property.Name;
				return true;
			}
			return false;
		}

		/// <summary>
		/// Builds the VB property form of an accessor forwarder: a Private ReadOnly Property named
		/// "&lt;iface&gt;_&lt;name&gt;" whose getter is the forwarding body, with an Implements clause
		/// naming the interface property. The synthetic name avoids clashing with the real
		/// accessor method and with any inherited member of the same name.
		/// </summary>
		PropertyDeclaration CreatePropertyFromGetterForwarder(CS.MethodDeclaration orig, ITypeDefOrRef interfaceType, string propertyName, object data) {
			var result = new PropertyDeclaration();

			members.Push(new MemberInfo());

			ConvertNodes(orig.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
			var ifaceName = interfaceType.Name ?? "I";
			var tick = ifaceName.IndexOf('`');
			if (tick >= 0)
				ifaceName = ifaceName.Substring(0, tick);
			result.Name = Identifier.Create(BoxedTextColor.InstanceProperty, ifaceName + "_" + propertyName);
			result.ReturnType = (AstType)orig.ReturnType.AcceptVisitor(this, data);
			var getter = new Accessor();
			getter.Body = (BlockStatement)orig.Body.AcceptVisitor(this, data);
			result.Getter = getter;
			result.Modifiers = Modifiers.Private | Modifiers.ReadOnly;
			result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(interfaceType), propertyName, orig.Body));

			members.Pop();

			return result;
		}

		public AstNode VisitMethodDeclaration(CS.MethodDeclaration methodDeclaration, object data)
		{
			CS.Attribute attr;

			ResetMethodState(methodDeclaration);

			if (IsOwnerAModule(methodDeclaration))
				methodDeclaration.Modifiers &= ~CS.Modifiers.Static;

			if ((methodDeclaration.Modifiers & CS.Modifiers.Extern) == CS.Modifiers.Extern && HasAttribute(methodDeclaration.Attributes, "System.Runtime.InteropServices.DllImportAttribute", out attr)) {
				var result = new ExternalMethodDeclaration();

				members.Push(new MemberInfo());

				// remove AttributeSection if only one attribute is present
				var attrSec = (CS.AttributeSection)attr.Parent;
				if (attrSec.Attributes.Count == 1)
					attrSec.Remove();
				else
					attr.Remove();

				result.Library = (attr.Arguments.First().AcceptVisitor(this, data) as PrimitiveExpression).Value.ToString();
				result.CharsetModifier = ConvertCharset(attr.Arguments);
				result.Alias = ConvertAlias(attr.Arguments);

				ConvertNodes(methodDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
				ConvertNodes(methodDeclaration.ModifierTokens, result.ModifierTokens);
				result.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(methodDeclaration), methodDeclaration.NameToken.Annotations), methodDeclaration.Name);
				result.IsSub = IsSub(methodDeclaration.ReturnType);
				ConvertNodes(methodDeclaration.Parameters, result.Parameters);
				ConvertNodes(methodDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), result.ReturnTypeAttributes);
				if (!result.IsSub)
					result.ReturnType = (AstType)methodDeclaration.ReturnType.AcceptVisitor(this, data);

				if (members.Pop().inIterator) {
					result.Modifiers |= Modifiers.Iterator;
				}
				result.Modifiers &= ~Modifiers.Shared;

				return EndNode(methodDeclaration, result);
			} else {
				// The engine renders an accessor-level .override to an interface property as a
				// forwarding method named get_X plus a comment-marked helper; VB cannot implement
				// a property member with a method (README 37). Turn the helper into a Private
				// ReadOnly Property whose Implements clause names the property.
				var accessorMethod = methodDeclaration.Annotation<MethodDef>();
				if (accessorMethod != null &&
						IsGetterForwarderForInterfaceProperty(methodDeclaration, accessorMethod, out var ifaceType, out var ifacePropertyName)) {
					return EndNode(methodDeclaration, CreatePropertyFromGetterForwarder(methodDeclaration, ifaceType, ifacePropertyName, data));
				}

				var result = new MethodDeclaration();

				members.Push(new MemberInfo());

				ConvertNodes(methodDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
				ConvertNodes(methodDeclaration.ModifierTokens, result.ModifierTokens);
				result.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(methodDeclaration), methodDeclaration.NameToken.Annotations), GetRenamedMemberName(methodDeclaration, methodDeclaration.Name));
				result.IsSub = IsSub(methodDeclaration.ReturnType);
				ConvertNodes(methodDeclaration.Parameters, result.Parameters);
				ConvertNodes(methodDeclaration.TypeParameters, result.TypeParameters);
				ConvertNodes(methodDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), result.ReturnTypeAttributes);
				CreateImplementsClausesForMethod(methodDeclaration, result);
				if (!result.IsSub)
					result.ReturnType = (AstType)methodDeclaration.ReturnType.AcceptVisitor(this, data);

				if (methodDeclaration.IsExtensionMethod) {
					AttributeBlock block = new AttributeBlock();
					var attrRef = module.UpdateRowId(module.CorLibTypes.GetTypeRef("System.Runtime.CompilerServices", "ExtensionAttribute"));
					block.Attributes.Add(new VB.Ast.Attribute() { Type = CreateType(attrRef) });
					result.Attributes.Add(block);
				}

				var md = methodDeclaration.Annotation<MethodDef>();
				if (md != null)
					result.Modifiers |= GetExtraMethodModifiers(md);

				result.Body = (BlockStatement)methodDeclaration.Body.AcceptVisitor(this, data);

				if (members.Pop().inIterator) {
					result.Modifiers |= Modifiers.Iterator;
				}

				return EndNode(methodDeclaration, result);
			}
		}

		Dictionary<TypeDef, Dictionary<string, uint>> modifiersDict = new Dictionary<TypeDef, Dictionary<string, uint>>();
		Modifiers GetExtraMethodModifiers(MethodDef md) {
			Dictionary<string, uint> dict;
			uint count;
			if (!modifiersDict.TryGetValue(md.DeclaringType, out dict)) {
				modifiersDict.Add(md.DeclaringType, dict = new Dictionary<string, uint>());
				foreach (var m in md.DeclaringType.Methods) {
					dict.TryGetValue(m.Name, out count);
					if (!m.IsNewSlot && (m.IsAbstract || m.IsFinal || m.IsVirtual))
						count++;
					count += (1U << 16);
					dict[m.Name] = count;
				}
			}
			if (dict.TryGetValue(md.Name, out count)) {
				uint numOverrides = count & 0xFFFF;
				uint totalMethods = count >> 16;
				if (numOverrides >= 1 && totalMethods > 1)
					return Modifiers.Overloads;
			}
			return Modifiers.None;
		}

		static MethodBaseSig GetMethodBaseSig(ITypeDefOrRef type, MethodBaseSig msig, IList<TypeSig> methodGenArgs = null) {
			IList<TypeSig> typeGenArgs = null;
			var ts = type as TypeSpec;
			if (ts != null) {
				var genSig = ts.TypeSig.ToGenericInstSig();
				if (genSig != null)
					typeGenArgs = genSig.GenericArguments;
			}
			if (typeGenArgs == null && methodGenArgs == null)
				return msig;
			return GenericArgumentResolver.Resolve(msig, typeGenArgs, methodGenArgs);
		}

		static ITypeDefOrRef GetReplacedType(ITypeDefOrRef iface, ITypeDefOrRef typeToFix) {
			IList<TypeSig> typeGenArgs = null;
			var ts = iface as TypeSpec;
			if (ts != null) {
				var genSig = ts.TypeSig.ToGenericInstSig();
				if (genSig != null)
					typeGenArgs = genSig.GenericArguments;
			}
			if (typeGenArgs == null)
				return typeToFix;
			return GenericArgumentResolver.Resolve(typeToFix.ToTypeSig(), typeGenArgs, null).ToTypeDefOrRef();
		}

		struct ImplementsResult {
			public ITypeDefOrRef Type { get; }
			public string OriginalName { get; }
			public object Reference { get; }
			public ImplementsResult(IMethodDefOrRef method, object reference)
				: this(method.DeclaringType, method.Name, reference) {
			}
			public ImplementsResult(ITypeDefOrRef type, string originalName, object reference) {
				Type = type;
				OriginalName = originalName;
				Reference = reference;
			}
		}

		IEnumerable<ImplementsResult> MatchViaCecil(MethodDef method, ITypeDefOrRef iface) {
			var cecilType = DnlibCecilBridge.TryGetTypeReference(module, iface);
			var definition = cecilType?.Resolve();
			if (definition is null) {
				// The bridge cannot express every type spec - a tuple type argument, for one. Its
				// generic type definition is a plain type reference though, so bridge that and
				// match by name and arity; VB validates the signature itself.
				var ifaceDef = ResolveInterfaceDefinition(iface);
				if (ifaceDef is null)
					yield break;
				foreach (var ifaceMethod in ifaceDef.Methods) {
					if (ifaceMethod.Name != method.Name && !method.Name.EndsWith("." + ifaceMethod.Name, StringComparison.Ordinal))
						continue;
					if (ifaceMethod.Parameters.Count != method.MethodSig.Params.Count)
						continue;
					// Derived interfaces may redeclare a member with a different return type
					// (IWpfTextView.GetTextViewLineContainingBufferPosition hides the ITextView one).
					// A same-name/same-arity match against the wrong redeclaration makes one method
					// emit two clauses (BC30583); the return types are both metadata spellings here,
					// so a definite mismatch can be rejected outright.
					if (!ReturnTypesCompatible(method.MethodSig.RetType, ifaceMethod.ReturnType))
						continue;
					yield return new ImplementsResult(iface, ifaceMethod.Name, ifaceMethod);
				}
				yield break;
			}

			// IEquatable<AddressReference>: map the interface's own type parameters to those
			// arguments so Equals(T) does not match an unrelated Equals(object) overload.
			var genericArguments = (iface.ToTypeSig() as GenericInstSig)?.GenericArguments;
			var comparer = new SigComparer(0, method.Module);
			foreach (var ifaceMethod in definition.Methods) {
				if (ifaceMethod.Name != method.Name && !method.Name.EndsWith("." + ifaceMethod.Name, StringComparison.Ordinal))
					continue;
				if (ifaceMethod.Parameters.Count != method.MethodSig.Params.Count)
					continue;
				bool matches = true;
				for (int i = 0; i < ifaceMethod.Parameters.Count; i++) {
					var parameterType = ifaceMethod.Parameters[i].ParameterType;
					if (parameterType is Mono.Cecil.GenericParameter gp) {
						if (genericArguments is null || gp.Position >= genericArguments.Count)
							continue;
						if (!comparer.Equals(genericArguments[gp.Position], method.MethodSig.Params[i])) {
							matches = false;
							break;
						}
						continue;
					}
					// Only names can be compared here: dnlib did not resolve the interface, so its
					// signature is not available. Without this every Write(x, String) overload
					// matched every Write(y, String) interface member and claimed all of them
					// (BC30583/BC30401). Generic instantiations are left to the arity check above.
					var ifaceName = parameterType.FullName;
					var implName = method.MethodSig.Params[i].FullName;
					if (ifaceName.IndexOf('<') >= 0 || implName.IndexOf('<') >= 0)
						continue;
					if (!string.Equals(ifaceName, implName, StringComparison.Ordinal)) {
						matches = false;
						break;
					}
				}
				if (matches && ReturnTypesCompatible(method.MethodSig.RetType, ifaceMethod.ReturnType))
					yield return new ImplementsResult(iface, ifaceMethod.Name, method);
			}
		}

		/// <summary>
		/// Return type filter for the cecil fallback matches. Both sides are metadata spellings
		/// (dnlib signature vs cecil reference), so they are compared as the generic type
		/// definition's full name: the arity is kept (IEnumerator`1 vs IEnumerator) while the
		/// interface's own type arguments are stripped (IEnumerator`1&lt;T&gt; vs
		/// IEnumerator`1&lt;Foo&gt; stay equal). <see cref="AstAnnotationBinder.NormalizeParameterTypeName"/>
		/// must not be used here: it strips the arity, which would let the non-generic
		/// IEnumerable.GetEnumerator claim the generic one and back (33 files). Any side that
		/// cannot be expressed (a generic parameter, a multi-dimensional array, a pointer) keeps
		/// the candidate - only a definite mismatch vetoes the clause.
		/// </summary>
		static bool ReturnTypesCompatible(TypeSig dnlibRetType, Mono.Cecil.TypeReference cecilRetType) {
			var dnlibName = GetGenericDefFullName(dnlibRetType);
			var cecilName = GetGenericDefFullName(cecilRetType);
			if (dnlibName is null || cecilName is null)
				return true;
			return string.Equals(dnlibName, cecilName, StringComparison.Ordinal);
		}

		static string GetGenericDefFullName(TypeSig sig) {
			while (true) {
				switch (sig) {
				case null:
					return null;
				case ByRefSig byRef:
					sig = byRef.Next;
					continue;
				case PinnedSig pinned:
					sig = pinned.Next;
					continue;
				case CModReqdSig:
				case CModOptSig:
					// A modreq/modopt is part of the signature identity; neither spelling both
					// sides compatibly nor ignoring it is safe, so keep the candidate.
					return null;
				case GenericVar:
				case GenericMVar:
					return null;
				case GenericInstSig genericInst:
					return genericInst.GenericType?.TypeDefOrRef?.FullName;
				case SZArraySig szArray:
					var szElement = GetGenericDefFullName(szArray.Next);
					return szElement is null ? null : szElement + "[]";
				case TypeDefOrRefSig typeDefOrRef:
					return typeDefOrRef.TypeDefOrRef?.FullName;
				default:
					// Multi-dimensional arrays, pointers and function pointers are too rare to
					// spell both sides compatibly; keeping the candidate is always safe.
					return null;
				}
			}
		}

		static string GetGenericDefFullName(Mono.Cecil.TypeReference type) {
			while (true) {
				if (type is null)
					return null;
				if (type is Mono.Cecil.GenericParameter)
					return null;
				if (type is Mono.Cecil.GenericInstanceType genericInst)
					return genericInst.ElementType?.FullName;
				if (type is Mono.Cecil.ByReferenceType byRef) {
					type = byRef.ElementType;
					continue;
				}
				if (type is Mono.Cecil.PinnedType pinned) {
					type = pinned.ElementType;
					continue;
				}
				if (type is Mono.Cecil.RequiredModifierType || type is Mono.Cecil.OptionalModifierType)
					// See the dnlib side: a modreq/modopt cannot be spelled safely, keep the candidate.
					return null;
				if (type is Mono.Cecil.ArrayType arrayType) {
					if (arrayType.Rank != 1)
						return null;
					var element = GetGenericDefFullName(arrayType.ElementType);
					return element is null ? null : element + "[]";
				}
				if (type is Mono.Cecil.PointerType || type is Mono.Cecil.FunctionPointerType || type is Mono.Cecil.SentinelType)
					return null;
				return type.FullName;
			}
		}

		/// <summary>
		/// Resolves an interface definition through its generic type definition. Used when the
		/// bridge cannot handle the constructed type (a tuple type argument defeats it).
		/// </summary>
		Mono.Cecil.TypeDefinition ResolveInterfaceDefinition(ITypeDefOrRef iface) {
			var genericType = (iface as TypeSpec)?.TypeSig?.ToGenericInstSig()?.GenericType?.TypeDefOrRef;
			if (genericType is null)
				return null;
			try {
				return DnlibCecilBridge.TryGetTypeReference(module, genericType)?.Resolve();
			}
			catch {
				return null;
			}
		}

		// A type can list the same interface more than once (dnSpy.dll does, and every entry then
		// matches the same member), which used to emit 'Implements I.Foo, I.Foo' — BC30583.
		// Deduplicate per (interface, member signature): two overloads of one name are distinct
		// members and must both keep their clause.
		static bool TryAddClause(List<ImplementsResult> emitted, ImplementsResult result) {
			var comparer = new SigComparer(0);
			var key = MemberKey(result);
			foreach (var item in emitted) {
				if (MemberKey(item) != key)
					continue;
				if (comparer.Equals(result.Type, item.Type))
					return false;
			}
			emitted.Add(result);
			return true;
		}

		static string MemberKey(ImplementsResult result) =>
			result.OriginalName + "|" + (result.Reference?.ToString() ?? "");

		IEnumerable<ImplementsResult> GetMethods(MethodDef method) {
			var emitted = new List<ImplementsResult>();
			foreach (var o in method.Overrides) {
				var m = o.MethodDeclaration.ResolveMethodDef();
				if (m == null)
					continue;
				if (!m.DeclaringType.IsInterface)
					continue;
				var res = new ImplementsResult(o.MethodDeclaration, o.MethodDeclaration);
				if (!TryAddClause(emitted, res))
					continue;
				yield return res;
			}
			var comparer = new SigComparer(0, method.Module);
			foreach (var ii in method.DeclaringType.Interfaces) {
				var iface = ii.Interface.Resolve();
				if (iface == null) {
					// dnlib has no assembly resolver here, so an instantiated interface such as
					// IEquatable<T> does not resolve. cecil can resolve it; the clause still uses
					// the dnlib type so the emitted name matches.
					foreach (var fallback in MatchViaCecil(method, ii.Interface)) {
						if (TryAddClause(emitted, fallback))
							yield return fallback;
					}
					continue;
				}
				foreach (var ifaceMethod in iface.Methods) {
					if (ifaceMethod.Name != method.Name && !method.Name.EndsWith("." + ifaceMethod.Name, StringComparison.Ordinal))
						continue;
					var ifaceMethodSig = GetMethodBaseSig(ii.Interface, ifaceMethod.MethodSig);
					if (!comparer.Equals(ifaceMethodSig, method.MethodSig))
						continue;
					var res = new ImplementsResult(ii.Interface, ifaceMethod.Name, ifaceMethod);
					if (!TryAddClause(emitted, res))
						continue;
					yield return res;
				}
			}
		}

		IEnumerable<ImplementsResult> GetProperties(PropertyDef prop) {
			var emitted = new List<ImplementsResult>();
			var pm = prop.GetMethod ?? prop.SetMethod;
			if (pm != null) {
				foreach (var o in pm.Overrides) {
					var m = o.MethodDeclaration.ResolveMethodDef();
					if (m == null)
						continue;
					if (!m.DeclaringType.IsInterface)
						continue;
					var p = m.DeclaringType.Properties.FirstOrDefault(a => a.GetMethod == m || a.SetMethod == m);
					if (p == null)
						continue;
					var res = new ImplementsResult(o.MethodDeclaration.DeclaringType, p.Name, (object)GetProperty(o.MethodDeclaration) ?? o.MethodDeclaration);
					if (!TryAddClause(emitted, res))
						continue;
					yield return res;
				}
			}
			var comparer = new SigComparer(0, prop.Module);
			foreach (var ii in prop.DeclaringType.Interfaces) {
				var iface = ii.Interface.Resolve();
				if (iface == null) {
					// See GetEvents: cecil resolves the interface when dnlib cannot, which keeps
					// the Implements clause for properties declared in referenced assemblies.
					var cecilType = DnlibCecilBridge.TryGetTypeReference(module, ii.Interface)?.Resolve();
					if (cecilType != null) {
						foreach (var ifaceProp in cecilType.Properties) {
							if (ifaceProp.Name != prop.Name && !prop.Name.EndsWith("." + ifaceProp.Name, StringComparison.Ordinal))
								continue;
							if (ifaceProp.Parameters.Count != prop.PropertySig.Params.Count)
								continue;
							if (ifaceProp.PropertyType?.FullName != prop.PropertySig.RetType?.FullName)
								continue;
							// A raw Mono.Cecil PropertyDefinition is unusable as a reference (the
						// formatter only reads dnlib IMemberRef) - synthesize the getter's
						// MemberRef in the interface's scope, which dnSpy resolves to the property.
							var res = new ImplementsResult(ii.Interface, ifaceProp.Name,
								DnlibCecilBridge.TryGetTypeReference(module, ii.Interface) is Mono.Cecil.TypeReference ifaceRef && ifaceRef.Resolve() is Mono.Cecil.TypeDefinition itd && module is ModuleDefMD moduleMd2
									? Common.DnlibReferenceMapper.MapExternalMember(moduleMd2, itd.Methods.FirstOrDefault(mm => mm.Name == "get_" + ifaceProp.Name) ?? (Mono.Cecil.MemberReference)ifaceProp)
									: ifaceProp);
							if (!TryAddClause(emitted, res))
								continue;
							yield return res;
						}
					}
					continue;
				}
				foreach (var ifaceProp in iface.Properties) {
					if (ifaceProp.Name != prop.Name && !prop.Name.EndsWith("." + ifaceProp.Name, StringComparison.Ordinal))
						continue;
					var ifaceMethodSig = GetMethodBaseSig(ii.Interface, ifaceProp.PropertySig);
					// mcs doesn't set HasThis if it's an instance property
					if (ifaceMethodSig.HasThis != prop.PropertySig.HasThis) {
						ifaceMethodSig = new MethodSig(ifaceMethodSig.CallingConvention, ifaceMethodSig.GenParamCount, ifaceMethodSig.RetType, ifaceMethodSig.Params, ifaceMethodSig.ParamsAfterSentinel);
						ifaceMethodSig.HasThis = prop.PropertySig.HasThis;
					}
					if (!comparer.Equals(ifaceMethodSig, prop.PropertySig))
						continue;
					var res = new ImplementsResult(ii.Interface, ifaceProp.Name, ifaceProp);
					if (!TryAddClause(emitted, res))
						continue;
					yield return res;
				}
			}
		}

		static PropertyDef GetProperty(IMethodDefOrRef method) {
			var md = method.ResolveMethodDef();
			if (md == null)
				return null;
			foreach (var p in md.DeclaringType.Properties) {
				if (p.GetMethods.Contains(md))
					return p;
				if (p.SetMethods.Contains(md))
					return p;
				if (p.OtherMethods.Contains(md))
					return p;
			}
			return null;
		}

		static EventDef GetEvent(IMethodDefOrRef method) {
			var md = method.ResolveMethodDef();
			if (md == null)
				return null;
			foreach (var e in md.DeclaringType.Events) {
				if (e.AddMethod == md || e.RemoveMethod == md || e.InvokeMethod == md)
					return e;
				if (e.OtherMethods.Contains(md))
					return e;
			}
			return null;
		}

		IEnumerable<ImplementsResult> GetEvents(EventDef evt) {
			var emitted = new List<ImplementsResult>();
			var em = evt.AddMethod ?? evt.RemoveMethod ?? evt.InvokeMethod;
			if (em != null) {
				foreach (var o in em.Overrides) {
					var m = o.MethodDeclaration.ResolveMethodDef();
					if (m == null)
						continue;
					if (!m.DeclaringType.IsInterface)
						continue;
					var e = m.DeclaringType.Events.FirstOrDefault(a => a.AddMethod == m || a.RemoveMethod == m || a.InvokeMethod == m);
					if (e == null)
						continue;
					var res = new ImplementsResult(o.MethodDeclaration.DeclaringType, e.Name, (object)GetEvent(o.MethodDeclaration) ?? o.MethodDeclaration);
					if (!TryAddClause(emitted, res))
						continue;
					yield return res;
				}
			}
			var comparer = new SigComparer(0, evt.Module);
			foreach (var ii in evt.DeclaringType.Interfaces) {
				var iface = ii.Interface.Resolve();
				if (iface == null) {
					// dnlib cannot resolve the interface here; cecil can, and the event is matched
					// by name and type so the Implements clause is still emitted (BC30149).
					var cecilType = DnlibCecilBridge.TryGetTypeReference(module, ii.Interface)?.Resolve();
					if (cecilType != null) {
						foreach (var ifaceEvent in cecilType.Events) {
							if (ifaceEvent.Name != evt.Name && !evt.Name.EndsWith("." + ifaceEvent.Name, StringComparison.Ordinal))
								continue;
							if (ifaceEvent.EventType?.FullName != evt.EventType?.FullName)
								continue;
							var res = new ImplementsResult(ii.Interface, ifaceEvent.Name, ifaceEvent);
							if (!TryAddClause(emitted, res))
								continue;
							yield return res;
						}
					}
					continue;
				}
				foreach (var ifaceEvent in iface.Events) {
					if (ifaceEvent.Name != evt.Name && !evt.Name.EndsWith("." + ifaceEvent.Name, StringComparison.Ordinal))
						continue;
					var eventType = GetReplacedType(ii.Interface, ifaceEvent.EventType);
					if (!comparer.Equals(eventType, evt.EventType))
						continue;
					var res = new ImplementsResult(ii.Interface, ifaceEvent.Name, ifaceEvent);
					if (!TryAddClause(emitted, res))
						continue;
					yield return res;
				}
			}
		}

		AstType CreateType(ITypeDefOrRef type) {
			// A type spec (generic instance, array) is built from the signature: the cecil bridge
			// cannot express every spec - a tuple type argument defeats it - and the C# type
			// builder writes short names the output file cannot resolve without Imports.
			if (type is TypeSpec spec) {
				var fromSig = CreateTypeFromSig(module, spec.TypeSig);
				if (fromSig != null)
					return fromSig;
			}
			var cecilType = DnlibCecilBridge.TryGetTypeReference(module, type);
			if (cecilType is null) {
				var fromSig = CreateTypeFromSig(module, type.ToTypeSig());
				return fromSig ?? (AstType)AstType.FromName(type.FullName, BoxedTextColor.Type);
			}
			var csAstType = ICSharpCode.Decompiler.CSharp.CSharpDecompiler.ConvertType(cecilType);
			var vbType = (AstType)csAstType.AcceptVisitor(this, null);
			return QualifyMemberType(vbType, cecilType);
		}

		/// <summary>
		/// Builds a fully qualified VB type straight from a metadata signature: every segment with
		/// Global, the arity stripped and the generic arguments converted recursively.
		/// </summary>
		static AstType CreateTypeFromSig(ModuleDef module, TypeSig sig) {
			sig = sig?.RemovePinnedAndModifiers();
			switch (sig) {
			case null:
				return null;
			case GenericInstSig generic:
				return CreateNamedType(module, generic.GenericType?.TypeDefOrRef?.FullName, generic.GenericArguments);
			case ArraySigBase array:
				var element = CreateTypeFromSig(module, array.Next);
				if (element is null)
					return null;
				var composed = new ComposedType { BaseType = element };
				composed.ArraySpecifiers.Add(new ArraySpecifier(array is ArraySig multi ? (int)multi.Rank : 1));
				return composed;
			case GenericSig parameter: {
				var name = parameter.GenericParam?.Name?.String ?? parameter.TypeName;
				return string.IsNullOrEmpty(name) ? null : new SimpleType((IEnumerable<object>)null, name);
			}
			case TypeDefOrRefSig named:
				return named.TypeDefOrRef is null ? null : CreateNamedType(module, named.TypeDefOrRef.FullName, null);
			default:
				return null;
			}
		}

		/// <summary>
		/// NamespaceReference for a dotted namespace prefix: the assembly scope comes from any
		/// TypeRef of that namespace in the module's reference table - the same resolution the
		/// 2.3 engine emitted on every namespace segment.
		/// </summary>
		static NamespaceReference? NamespaceReferenceFor(ModuleDefMD moduleMd, string fullNamespace) {
			if (string.IsNullOrEmpty(fullNamespace))
				return null;
			var typeRef = moduleMd.GetTypeRefs().FirstOrDefault(tr => tr.Namespace == fullNamespace)
				// Intermediate namespaces often have no exact TypeRef; any deeper namespace of
				// the same root resolves to the same owning assembly.
				?? moduleMd.GetTypeRefs().FirstOrDefault(tr => tr.Namespace.StartsWith(fullNamespace + ".", StringComparison.Ordinal));
			if (typeRef?.ResolutionScope is dnlib.DotNet.IAssembly scope)
				return new NamespaceReference(scope, fullNamespace);
			// The module's own namespaces have no TypeRef entries (its types are TypeDefs, not
			// TypeRefs); the 2.3 engine used the module's own assembly as the scope there.
			if (moduleMd.Types.Any(t => t.Namespace == fullNamespace || t.Namespace.StartsWith(fullNamespace + ".", StringComparison.Ordinal)))
				return new NamespaceReference(moduleMd.Assembly, fullNamespace);
			return null;
		}

		static AstType CreateNamedType(ModuleDef module, string fullName, IList<TypeSig> genericArguments) {
			if (string.IsNullOrEmpty(fullName))
				return null;
			var moduleMd = module as ModuleDefMD;
			var segments = fullName.Split('/');
			// The dotted parts of the first segment are namespace and top-level type name; the
			// rest are nested type names - namespace references only fit the true namespaces.
			var dotted = segments[0].Split('.');
			var namespaceCount = dotted.Length - 1;
			var names = new List<string>(dotted);
			for (int i = 1; i < segments.Length; i++)
				names.Add(segments[i]);

			AstType result = new PrimitiveType("Global");
			for (int i = 0; i < names.Count; i++) {
				var name = names[i];
				int tick = name.LastIndexOf('`');
				if (tick > 0)
					name = name.Substring(0, tick);
				object nameAnnotation;
				NamespaceReference? nsRef = null;
				if (i < namespaceCount) {
					var prefix = string.Join(".", names.Take(i + 1));
					nsRef = moduleMd is not null ? NamespaceReferenceFor(moduleMd, prefix) : null;
					nameAnnotation = BoxedTextColor.Namespace;
				}
				else if (i == namespaceCount) {
					// The top-level type segment: local types keep their definition, external
					// ones map to the module's own TypeRef (the 2.3 engine's shapes). When the
					// table has no entry, synthesize one - the 2.3 engine's importer created
					// these TypeRefs on demand (compiler-emitted attribute types like
					// OutAttribute often have no pre-existing entry).
					var local = LookupLocalTypeStatic(module, name);
					var tableRef = local is not null ? null : (moduleMd is not null ? Common.DnlibReferenceMapper.ResolveTypeRefByName(moduleMd, name, -1) : null);
					if (local is not null)
						nameAnnotation = local;
					else if (tableRef is not null)
						nameAnnotation = tableRef;
					else if (moduleMd is not null)
						nameAnnotation = new TypeRefUser(moduleMd, string.Join(".", names.Take(namespaceCount)), name, moduleMd.CorLibTypes.AssemblyRef);
					else
						nameAnnotation = BoxedTextColor.Type;
				}
				else {
					nameAnnotation = BoxedTextColor.Type;
				}
				var segment = new QualifiedType(result, Identifier.Create(nameAnnotation, name));
				// Color first, reference second (see QualifyMemberType). The reference is
				// attached to both the identifier token and the node (the output stage reads
				// member references from the nodeStack's current node, not the identifier).
				if (nsRef is not null) {
					segment.NameToken.AddAnnotation(nsRef);
					segment.AddAnnotation(nsRef);
				}
				else if (nameAnnotation is dnlib.DotNet.ITypeDefOrRef) {
					segment.AddAnnotation(nameAnnotation);
				}
				result = segment;
			}
			if (genericArguments is null || genericArguments.Count == 0 || !(result is QualifiedType leaf))
				return result;
			foreach (var argument in genericArguments) {
				var converted = CreateTypeFromSig(module, argument);
				if (converted is null)
					return null;
				leaf.TypeArguments.Add(converted);
			}
			return result;
		}

		// CSharpDecompiler.ConvertType names a type the way the C# output would: the simple name
		// whenever a using directive is in scope. A member-level Implements clause, however, is
		// written with no imports in scope, so a bare 'IBackgroundImageOptionDefinition.Id' does
		// not resolve (BC30002). Rebuild the name from the metadata as Global.<ns>.<name>.
		AstType QualifyMemberType(AstType vbType, Mono.Cecil.TypeReference cecilType) {
			var element = cecilType is Mono.Cecil.GenericInstanceType git ? git.ElementType : cecilType;
			// Arrays, byrefs and type parameters cannot be reached through a namespace.
			if (element is Mono.Cecil.GenericParameter || element is Mono.Cecil.TypeSpecification)
				return vbType;

			var arguments = new List<AstType>();
			if (vbType is SimpleType simple) {
				foreach (var a in simple.TypeArguments)
					arguments.Add(a);
			}
			else if (vbType is QualifiedType qualified) {
				foreach (var a in qualified.TypeArguments)
					arguments.Add(a);
			}
			else
				return vbType;

			AstType root = vbType;
			while (root is QualifiedType qt)
				root = qt.Target;
			if (root is PrimitiveType primitive && primitive.Keyword == "Global")
				return vbType;

			var names = new List<string>();
			for (var t = element; t != null; t = t.DeclaringType)
				names.Insert(0, StripGenericArity(t.Name));
			if (!string.IsNullOrEmpty(element.Namespace))
				names.InsertRange(0, element.Namespace.Split('.'));

			AstType result = new PrimitiveType("Global");
			for (int i = 0; i < names.Count; i++) {
				// Namespace segments get the Namespace color; the last segment is the type name
				// and keeps its kind color (local type) or plain Type (external, via the module's
				// own TypeRef tables) - matching the 2.3 engine.
				object nameAnnotation;
				NamespaceReference? nsRef = null;
				if (i == names.Count - 1) {
					var typeName = names[i];
					var localType = LookupLocalType(typeName);
					nameAnnotation = localType is not null
						? (object)localType
						: (object)(module is ModuleDefMD moduleMd
							? Common.DnlibReferenceMapper.ResolveTypeRefByName(moduleMd, typeName, -1) ?? (object)BoxedTextColor.Type
							: BoxedTextColor.Type);
				}
				else {
					// The 2.3 engine emitted a NamespaceReference on every namespace segment;
					// resolve the owning assembly from any TypeRef of the segment's namespace.
					var prefix = string.Join(".", names.Take(i + 1));
					nsRef = module is ModuleDefMD moduleMd ? NamespaceReferenceFor(moduleMd, prefix) : null;
					nameAnnotation = BoxedTextColor.Namespace;
				}
				var segment = new QualifiedType(result, Identifier.Create(nameAnnotation, names[i]));
				// Color first, reference second: the output stage colors by the FIRST annotation
				// and picks the reference up by its type. The reference is attached to both the
				// identifier token and the node (the output stage reads member references from
				// the nodeStack's current node, not the identifier).
				if (nsRef is not null) {
					segment.NameToken.AddAnnotation(nsRef);
					segment.AddAnnotation(nsRef);
				}
				else if (nameAnnotation is dnlib.DotNet.ITypeDefOrRef) {
					// A resolved type segment (local TypeDef or mapped TypeRef): the token got it
					// from Identifier.Create; the node needs it too for the output stage.
					segment.AddAnnotation(nameAnnotation);
				}
				result = segment;
			}
			// Re-attach the converted type arguments; clone so the old tree keeps its children.
			if (result is QualifiedType leaf && arguments.Count > 0) {
				foreach (var argument in arguments)
					leaf.TypeArguments.Add((AstType)argument.Clone());
			}
			return result;
		}

		// Metadata names carry the generic arity ('IEquatable`1'); the type arguments in the
		// emitted type already express it, and a backtick would be escaped into '_' (BC30149).
		static string StripGenericArity(string name) {
			var tick = name.LastIndexOf('`');
			return tick > 0 ? name.Substring(0, tick) : name;
		}

		void CreateImplementsClausesForMethod(CS.MethodDeclaration orig, MethodDeclaration result) {
			var md = orig.Annotation<MethodDef>();
			if (md == null || !md.IsVirtual)
				return;
			// The binder falls back to another same-name overload when no signature matches.
			// Claiming its interface members would make several overloads implement the same
			// member (BC30583), so only a definition with matching parameters is trusted.
			if (!AstAnnotationBinder.ParametersMatch(md, orig))
				return;
			var type = md.DeclaringType;
			if (type.IsInterface)
				return;

			var matched = GetMethods(md).ToList();
			foreach (var t in matched)
				result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(t.Type), t.OriginalName, t.Reference));
			// VB requires the implementation to repeat the Optional/default of the interface
			// member (BC30401); C# only needs it on the interface declaration.
			foreach (var t in matched)
				ApplyOptionalParameters(result, t);
		}

		/// <summary>
		/// Copies Optional and the default value from the implemented member onto the parameters
		/// that lack them (VB rejects the implementation otherwise, BC30401). The interface member
		/// is looked up by name and arity because the matched reference may be the implementation
		/// itself - that is what the cecil fallback yields.
		/// </summary>
		void ApplyOptionalParameters(MethodDeclaration result, ImplementsResult target) {
			if (result.Parameters.Count == 0)
				return;
			var iface = target.Type?.Resolve();
			if (iface != null) {
				foreach (var method in iface.Methods) {
					if (method.Name != target.OriginalName)
						continue;
					var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
					if (parameters.Count != result.Parameters.Count)
						continue;
					// Overloads share the name and often the arity; applying Load#2's Optional to
					// Load#1's parameter changed the implementation's signature (BC30401).
					if (!InterfaceParameterTypesMatch(parameters, result))
						continue;
					if (!parameters.Any(p => p.ParamDef?.IsOptional == true))
						continue;
					for (int i = 0; i < parameters.Count; i++) {
						var paramDef = parameters[i].ParamDef;
						if (paramDef != null && paramDef.IsOptional)
							SetOptionalValue(result.Parameters.ElementAt(i), paramDef.Constant?.Value);
					}
					return;
				}
				return;
			}
			var cecilType = DnlibCecilBridge.TryGetTypeReference(module, target.Type)?.Resolve();
			if (cecilType is null)
				return;
			foreach (var method in cecilType.Methods) {
				if (method.Name != target.OriginalName)
					continue;
				if (method.Parameters.Count != result.Parameters.Count)
					continue;
				if (!CecilInterfaceParameterTypesMatch(method.Parameters, result))
					continue;
				if (!method.Parameters.Any(p => p.IsOptional))
					continue;
				for (int i = 0; i < method.Parameters.Count; i++) {
					if (method.Parameters[i].IsOptional)
						SetOptionalValue(result.Parameters.ElementAt(i), method.Parameters[i].HasConstant ? method.Parameters[i].Constant : null);
				}
				return;
			}
		}

		/// <summary>
		/// Compares the interface method's parameter types with the emitted VB parameters by
		/// their last name segment, so the Optional/default values only travel to the overload
		/// that really has them.
		/// </summary>
		static bool InterfaceParameterTypesMatch(List<dnlib.DotNet.Parameter> ifaceParameters, MethodDeclaration result) {
			var implParameters = result.Parameters.ToList();
			if (ifaceParameters.Count != implParameters.Count)
				return false;
			for (int i = 0; i < ifaceParameters.Count; i++) {
				var expected = AstAnnotationBinder.NormalizeParameterTypeName(NormalizeParamSigName(ifaceParameters[i].Type));
				var actual = AstAnnotationBinder.NormalizeParameterTypeName(implParameters[i].Type?.ToString() ?? "");
				if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
					continue;
				if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		static bool CecilInterfaceParameterTypesMatch(System.Collections.Generic.IList<Mono.Cecil.ParameterDefinition> ifaceParameters, MethodDeclaration result) {
			var implParameters = result.Parameters.ToList();
			if (ifaceParameters.Count != implParameters.Count)
				return false;
			for (int i = 0; i < ifaceParameters.Count; i++) {
				var expected = AstAnnotationBinder.NormalizeParameterTypeName(NormalizeCecilParamName(ifaceParameters[i].ParameterType));
				var actual = AstAnnotationBinder.NormalizeParameterTypeName(implParameters[i].Type?.ToString() ?? "");
				if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
					continue;
				if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		// Nullable<T> params must compare by their element type: the VB text says "Span?" while
		// the metadata name says "Nullable`1".
		static string NormalizeParamSigName(dnlib.DotNet.TypeSig sig) {
			sig = sig?.RemovePinnedAndModifiers();
			if (sig is dnlib.DotNet.GenericInstSig nullable && nullable.GenericType?.TypeDefOrRef?.FullName == "System.Nullable`1" &&
					nullable.GenericArguments.Count == 1)
				return nullable.GenericArguments[0]?.TypeName ?? "";
			return sig?.TypeName ?? "";
		}

		static string NormalizeCecilParamName(Mono.Cecil.TypeReference type) {
			if (type is Mono.Cecil.GenericInstanceType nullable && nullable.ElementType?.FullName == "System.Nullable`1" &&
					nullable.GenericArguments.Count == 1)
				return nullable.GenericArguments[0]?.Name ?? "";
			return type?.Name ?? "";
		}

		static void SetOptionalValue(ParameterDeclaration parameter, object value) {
			if (!parameter.OptionalValue.IsNull)
				return;
			parameter.Modifiers |= Modifiers.Optional;
			parameter.OptionalValue = new PrimitiveExpression(value);
		}

		void CreateImplementsClausesForProperty(CS.IndexerDeclaration orig, PropertyDeclaration result) {
			var pd = orig.Annotation<PropertyDef>();
			if (pd == null)
				return;
			if (pd.GetMethod?.IsVirtual == false && pd.SetMethod?.IsVirtual == false)
				return;
			var type = pd.DeclaringType;
			if (type.IsInterface)
				return;

			foreach (var t in GetProperties(pd))
				result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(t.Type), t.OriginalName, t.Reference));
		}

		void CreateImplementsClausesForProperty(CS.PropertyDeclaration orig, PropertyDeclaration result) {
			var pd = orig.Annotation<PropertyDef>();
			if (pd == null)
				return;
			if (pd.GetMethod?.IsVirtual == false && pd.SetMethod?.IsVirtual == false)
				return;
			var type = pd.DeclaringType;
			if (type.IsInterface)
				return;

			foreach (var t in GetProperties(pd))
				result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(t.Type), t.OriginalName, t.Reference));
		}

		void CreateImplementsClausesForEvent(CS.CustomEventDeclaration orig, EventDeclaration result) {
			var ed = orig.Annotation<EventDef>();
			if (ed == null)
				return;
			if (ed.AddMethod?.IsVirtual == false && ed.RemoveMethod?.IsVirtual == false && ed.InvokeMethod?.IsVirtual == false)
				return;
			var type = ed.DeclaringType;
			if (type.IsInterface)
				return;

			foreach (var t in GetEvents(ed))
				result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(t.Type), t.OriginalName, t.Reference));
		}

		void CreateImplementsClausesForEvent(CS.EventDeclaration orig, EventDeclaration result) {
			var ed = orig.Annotation<EventDef>();
			if (ed == null)
				return;
			if (ed.AddMethod?.IsVirtual == false && ed.RemoveMethod?.IsVirtual == false && ed.InvokeMethod?.IsVirtual == false)
				return;
			var type = ed.DeclaringType;
			if (type.IsInterface)
				return;

			foreach (var t in GetEvents(ed))
				result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(t.Type), t.OriginalName, t.Reference));
		}

		string ConvertAlias(CS.AstNodeCollection<CS.Expression> arguments)
		{
			var pattern = new CS.AssignmentExpression() {
				Left = new CS.IdentifierExpression("EntryPoint"),
				Operator = CS.AssignmentOperatorType.Assign,
				Right = new CS.PatternMatching.AnyNode("alias")
			};

			var result = arguments
				.Select(expr => pattern.Match(expr))
				.FirstOrDefault(r => r.Success);

			if (result.Success && result.Has("alias")) {
				return result.Get<CS.PrimitiveExpression>("alias")
					.First().Value.ToString();
			}

			return null;
		}

		CharsetModifier ConvertCharset(CS.AstNodeCollection<CS.Expression> arguments)
		{
			var pattern = new CS.AssignmentExpression() {
				Left = new CS.IdentifierExpression("CharSet"),
				Operator = CS.AssignmentOperatorType.Assign,
				Right = new CS.PatternMatching.NamedNode(
					"modifier",
					new CS.MemberReferenceExpression() {
						Target = new CS.TypeReferenceExpression { Type = new CS.PatternMatching.AnyNode() },
						MemberName = Pattern.AnyString
					})
			};

			var result = arguments
				.Select(expr => pattern.Match(expr))
				.FirstOrDefault(r => r.Success);

			if (result.Success && result.Has("modifier")) {
				switch (result.Get<CS.MemberReferenceExpression>("modifier").First().MemberName) {
					case "Auto":
						return CharsetModifier.Auto;
					case "Ansi":
						return CharsetModifier.Ansi;
					case "Unicode":
						return CharsetModifier.Unicode;
				}
			}

			return CharsetModifier.None;
		}

		bool IsSub(CS.AstType returnType)
		{
			var t = returnType as CS.PrimitiveType;
			return t != null && t.Keyword == "void";
		}

		public AstNode VisitOperatorDeclaration(CS.OperatorDeclaration operatorDeclaration, object data)
		{
			ResetMethodState(operatorDeclaration);
			MemberDeclaration result;
			members.Push(new MemberInfo());

			if (IsOwnerAModule(operatorDeclaration))
				operatorDeclaration.Modifiers &= ~CS.Modifiers.Static;

			if (operatorDeclaration.OperatorType == CS.OperatorType.Increment || operatorDeclaration.OperatorType == CS.OperatorType.Decrement) {
				var m = new MethodDeclaration();
				result = m;

				ConvertNodes(operatorDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), m.Attributes);
				ConvertNodes(operatorDeclaration.ModifierTokens, m.ModifierTokens);
				m.Name = Identifier.Create(VisualBasicMetadataTextColorProvider.Instance.GetColor((object)operatorDeclaration.Annotation<dnlib.DotNet.IMethod>() ?? BoxedTextColor.InstanceMethod), operatorDeclaration.OperatorType == CS.OperatorType.Increment ? "op_Increment" : "op_Decrement");
				ConvertNodes(operatorDeclaration.Parameters, m.Parameters);
				ConvertNodes(operatorDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), m.ReturnTypeAttributes);
				m.ReturnType = (AstType)operatorDeclaration.ReturnType.AcceptVisitor(this, data);
				m.Body = (BlockStatement)operatorDeclaration.Body.AcceptVisitor(this, data);
			} else {
				var op = new OperatorDeclaration();
				result = op;

				ConvertNodes(operatorDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), op.Attributes);
				ConvertNodes(operatorDeclaration.ModifierTokens, op.ModifierTokens);
				switch (operatorDeclaration.OperatorType) {
					case CS.OperatorType.LogicalNot:
					case CS.OperatorType.OnesComplement:
						op.Operator = OverloadableOperatorType.Not;
						break;
					case CS.OperatorType.True:
						op.Operator = OverloadableOperatorType.IsTrue;
						break;
					case CS.OperatorType.False:
						op.Operator = OverloadableOperatorType.IsFalse;
						break;
					case CS.OperatorType.Implicit:
						op.Modifiers |= Modifiers.Widening;
						op.Operator = OverloadableOperatorType.CType;
						break;
					case CS.OperatorType.Explicit:
						op.Modifiers |= Modifiers.Narrowing;
						op.Operator = OverloadableOperatorType.CType;
						break;
					case CS.OperatorType.Addition:
						op.Operator = OverloadableOperatorType.Add;
						break;
					case CS.OperatorType.Subtraction:
						op.Operator = OverloadableOperatorType.Subtract;
						break;
					case CS.OperatorType.UnaryPlus:
						op.Operator = OverloadableOperatorType.UnaryPlus;
						break;
					case CS.OperatorType.UnaryNegation:
						op.Operator = OverloadableOperatorType.UnaryMinus;
						break;
					case CS.OperatorType.Multiply:
						op.Operator = OverloadableOperatorType.Multiply;
						break;
					case CS.OperatorType.Division:
						op.Operator = OverloadableOperatorType.Divide;
						break;
					case CS.OperatorType.Modulus:
						op.Operator = OverloadableOperatorType.Modulus;
						break;
					case CS.OperatorType.BitwiseAnd:
						op.Operator = OverloadableOperatorType.BitwiseAnd;
						break;
					case CS.OperatorType.BitwiseOr:
						op.Operator = OverloadableOperatorType.BitwiseOr;
						break;
					case CS.OperatorType.ExclusiveOr:
						op.Operator = OverloadableOperatorType.ExclusiveOr;
						break;
					case CS.OperatorType.LeftShift:
						op.Operator = OverloadableOperatorType.ShiftLeft;
						break;
					case CS.OperatorType.RightShift:
						op.Operator = OverloadableOperatorType.ShiftRight;
						break;
					case CS.OperatorType.Equality:
						op.Operator = OverloadableOperatorType.Equality;
						break;
					case CS.OperatorType.Inequality:
						op.Operator = OverloadableOperatorType.InEquality;
						break;
					case CS.OperatorType.GreaterThan:
						op.Operator = OverloadableOperatorType.GreaterThan;
						break;
					case CS.OperatorType.LessThan:
						op.Operator = OverloadableOperatorType.LessThan;
						break;
					case CS.OperatorType.GreaterThanOrEqual:
						op.Operator = OverloadableOperatorType.GreaterThanOrEqual;
						break;
					case CS.OperatorType.LessThanOrEqual:
						op.Operator = OverloadableOperatorType.LessThanOrEqual;
						break;
					default:
						throw new Exception("Invalid value for OperatorType");
				}
				ConvertNodes(operatorDeclaration.Parameters, op.Parameters);
				ConvertNodes(operatorDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), op.ReturnTypeAttributes);
				op.ReturnType = (AstType)operatorDeclaration.ReturnType.AcceptVisitor(this, data);
				op.Body = (BlockStatement)operatorDeclaration.Body.AcceptVisitor(this, data);
			}

			members.Pop();

			return EndNode(operatorDeclaration, result);

		}

		public AstNode VisitParameterDeclaration(CS.ParameterDeclaration parameterDeclaration, object data)
		{
			var param = new ParameterDeclaration();

			ConvertNodes(parameterDeclaration.Attributes, param.Attributes);
			param.Modifiers = ConvertParamModifiers(parameterDeclaration.ParameterModifier);
			if ((parameterDeclaration.ParameterModifier & CS.ParameterModifier.Out) == CS.ParameterModifier.Out) {
				AttributeBlock block = new AttributeBlock();
				var attrRef = module.UpdateRowId(module.CorLibTypes.GetTypeRef("System.Runtime.InteropServices", "OutAttribute"));
				block.Attributes.Add(new VB.Ast.Attribute() { Type = CreateType(attrRef) });
				param.Attributes.Add(block);
			}
			var emittedParamName = RegisterParameterName(parameterDeclaration.Name, parameterDeclaration.Type?.ToString());
			var paramAnnotations = ComposeAnnotations(
				ResolveParameterAnnotation(parameterDeclaration), parameterDeclaration.NameToken.Annotations);
			param.Name = Identifier.Create(paramAnnotations, emittedParamName);
			var parameterTypeName = parameterDeclaration.Type.ToString();
			variableTypes[emittedParamName] = parameterTypeName;
			// The body's identifiers keep the C# spelling, so the original name must still
			// resolve; C# cannot shadow a parameter, so both keys can coexist.
			if (emittedParamName != parameterDeclaration.Name)
				variableTypes[parameterDeclaration.Name] = parameterTypeName;
			param.Type = (AstType)parameterDeclaration.Type.AcceptVisitor(this, data);
			param.OptionalValue = (Expression)parameterDeclaration.DefaultExpression.AcceptVisitor(this, data);
			if (!param.OptionalValue.IsNull)
				param.Modifiers |= Modifiers.Optional;

			return EndNode(parameterDeclaration, param);
		}

		Modifiers ConvertParamModifiers(CS.ParameterModifier mods)
		{
			switch (mods) {
				case CS.ParameterModifier.None:
				case CS.ParameterModifier.This:
					return Modifiers.None;
				case CS.ParameterModifier.Ref:
				case CS.ParameterModifier.Out:
					return Modifiers.ByRef;
				case CS.ParameterModifier.Params:
					return Modifiers.ParamArray;
				default:
					throw new Exception("Invalid value for ParameterModifier");
			}
		}

		public AstNode VisitPropertyDeclaration(CS.PropertyDeclaration propertyDeclaration, object data)
		{
			var decl = new PropertyDeclaration();

			members.Push(new MemberInfo());

			// The setter's "value" parameter is synthesized below, after the bodies are converted;
			// register its type first so the bodies can resolve it.
			ResetMethodState(propertyDeclaration);
			if (!propertyDeclaration.Setter.IsNull)
				variableTypes["value"] = propertyDeclaration.ReturnType.ToString();

			if (IsOwnerAModule(propertyDeclaration))
				propertyDeclaration.Modifiers &= ~CS.Modifiers.Static;

			ConvertNodes(propertyDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), decl.Attributes);
			decl.Getter = (Accessor)propertyDeclaration.Getter.AcceptVisitor(this, data);
			decl.Modifiers = ConvertModifiers(propertyDeclaration.Modifiers, propertyDeclaration);
			decl.Name = Identifier.Create(ComposeAnnotations(ResolveDefinitionNameAnnotation(propertyDeclaration), propertyDeclaration.NameToken.Annotations), GetRenamedMemberName(propertyDeclaration, propertyDeclaration.Name));
			ConvertNodes(propertyDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), decl.ReturnTypeAttributes);
			CreateImplementsClausesForProperty(propertyDeclaration, decl);
			decl.ReturnType = (AstType)propertyDeclaration.ReturnType.AcceptVisitor(this, data);
			decl.Setter = (Accessor)propertyDeclaration.Setter.AcceptVisitor(this, data);

			if (!decl.Setter.IsNull) {
				Identifier id;
				decl.Setter.Parameters.Add(new ParameterDeclaration() {
				                           	Name = id = new Identifier(BoxedTextColor.Parameter, "value", ICSharpCode.NRefactory.TextLocation.Empty),
				                           	Type = (AstType)propertyDeclaration.ReturnType.AcceptVisitor(this, data),
				                           });
				var method = decl.Setter.Annotation<MethodDef>();
				if (method != null) {
					var p = method.Parameters.FirstOrDefault(a => a.IsNormalMethodParameter);
					if (p != null)
						id.AddAnnotation(p);
				}
			}

			if (members.Pop().inIterator) {
				decl.Modifiers |= Modifiers.Iterator;
			}

			// ILSpy 3.2 stores automatic-property initializers in PropertyDeclaration.Initializer
			// (there is no Variables collection). Surface it as a VB auto-property initializer.
			if (!propertyDeclaration.Initializer.IsNull) {
				var v = new VariableDeclaratorWithTypeAndInitializer();
				// Do NOT assign decl.ReturnType here: that AstType is already attached to the
				// property, and an AstNode can only have one parent ("Node is already used in
				// another tree"). The output visitor ignores the declarator's Type when its
				// parent is a PropertyDeclaration and writes "= initializer" only.
				// The name needs the binder's dnlib PropertyDef (ResolveDefinitionNameAnnotation)
				// like every other declaration - the engine's own name-token annotations are
				// Mono.Cecil objects the formatter cannot use.
				v.Identifiers.Add(new VariableIdentifier() {
					Name = Identifier.Create(ComposeAnnotations(
						ResolveDefinitionNameAnnotation(propertyDeclaration),
						propertyDeclaration.NameToken.Annotations), propertyDeclaration.Name)
				});
				v.Initializer = (Expression)propertyDeclaration.Initializer.AcceptVisitor(this, data);
				decl.Variables.Add(v);
			}
			ConvertNodes(provider.GetParametersForProperty(propertyDeclaration), decl.Parameters);

			return EndNode(propertyDeclaration, decl);
		}

		/// <summary>
		/// The dnlib Local (or synthesized hoisted SourceLocal) for a local declaration name -
		// the same reference the usages carry, so dnSpy highlights them together.
		/// </summary>
		object? ResolveLocalDeclarationRef(CS.VariableInitializer variableInitializer) {
			var ilv = ICSharpCode.Decompiler.CSharp.AnnotationExtensions.GetILVariable(variableInitializer);
			if (ilv is null)
				return null;
			var method = FindEnclosingMethod(variableInitializer);
			if (ilv.Kind is ICSharpCode.Decompiler.IL.VariableKind.Local or ICSharpCode.Decompiler.IL.VariableKind.PinnedLocal &&
					method?.Body is not null && ilv.Index >= 0 && ilv.Index < method.Body.Variables.Count)
				return method.Body.Variables[ilv.Index];
			if (hoistedSourceLocals.TryGetValue(ilv, out var hoisted))
				return hoisted;
			var synthesized = new SourceLocal(null, ilv.Name ?? string.Empty,
				module.CorLibTypes.Object, SourceVariableFlags.None);
			hoistedSourceLocals[ilv] = synthesized;
			return synthesized;
		}

		public AstNode VisitVariableInitializer(CS.VariableInitializer variableInitializer, object data)
		{
			var decl = new VariableDeclaratorWithTypeAndInitializer();

			// look for type in parent
			decl.Type = (AstType)variableInitializer.Parent
				.GetChildByRole(CS.Roles.Type)
				.AcceptVisitor(this, data);
			// Fields go through this visitor too; only locals have a recorded emitted name.
			var emittedName = localDeclarationNames.TryGetValue(variableInitializer, out var mappedName)
				? mappedName
				: variableInitializer.Name;
			object? declaratorAnnotation = null;
			if (variableInitializer.Parent is CS.FieldDeclaration fieldDecl) {
				declaratorAnnotation = ResolveDefinitionNameAnnotation(fieldDecl);
				// Multi-declarator statements match by name: the declaration-level FieldDef
				// only fits its first variable.
				if (declaratorAnnotation is FieldDef namedField && namedField.Name != variableInitializer.Name)
					declaratorAnnotation = FindCurrentTypeMember(variableInitializer.Name);
			}
			// Local declaration names carry the dnlib Local (or the synthesized hoisted
			// SourceLocal) so dnSpy highlights the declaration together with its usages.
			var localDeclRef = ResolveLocalDeclarationRef(variableInitializer);
			decl.Identifiers.Add(new VariableIdentifier() {
				Name = Identifier.Create(ComposeAnnotations(
					localDeclRef ?? declaratorAnnotation ?? (variableInitializer.Parent is CS.FieldDeclaration ? (object)BoxedTextColor.InstanceField : BoxedTextColor.Local),
					variableInitializer.NameToken.Annotations), emittedName)
			});
			var initializer = (Expression)variableInitializer.Initializer.AcceptVisitor(this, data);
			var declaredTypeName = variableInitializer.Parent.GetChildByRole(CS.Roles.Type).ToString();
			var valueTypeName = typeResolver.GetTypeName(variableInitializer.Initializer);
			if (IsNumericTypeName(declaredTypeName) && IsCharTypeName(valueTypeName))
				initializer = WrapAscW(initializer);
			else if (IsCharTypeName(declaredTypeName) && IsNumericTypeName(valueTypeName))
				initializer = WrapChrW(initializer);
			decl.Initializer = initializer;

			return EndNode(variableInitializer, decl);
		}

		public AstNode VisitFixedFieldDeclaration(CS.FixedFieldDeclaration fixedFieldDeclaration, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitFixedVariableInitializer(CS.FixedVariableInitializer fixedVariableInitializer, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitSyntaxTree(CS.SyntaxTree syntaxTree, object data)
		{
			var unit = new CompilationUnit();

			foreach (var node in syntaxTree.Children)
				unit.AddChild(node.AcceptVisitor(this, null), CompilationUnit.MemberRole);

			return EndNode(syntaxTree, unit);
		}

		public AstNode VisitSimpleType(CS.SimpleType simpleType, object data)
		{
			var resolvedSimpleRef = ResolveDnlibReference(simpleType) ?? ResolveTypeNameFallback(simpleType);
			var type = new SimpleType(
				ComposeAnnotations(resolvedSimpleRef, simpleType.IdentifierToken.Annotations), simpleType.Identifier);
			// The output stage reads member references from the nodeStack's current node (the
			// SimpleType), not the identifier token child - attach the decisive reference to
			// both, or type usages like 'As TabService' render without references.
			if (resolvedSimpleRef is not null)
				type.AddAnnotation(resolvedSimpleRef);
			ConvertNodes(simpleType.TypeArguments, type.TypeArguments);

			return EndNode(simpleType, type);
		}

		/// <summary>
		/// Annotations for a converted identifier/type name: the decisive annotation (a dnlib
		/// reference or a fallback color) must come first because the output stage colors by
		/// the FIRST annotation; engine resolve results are dropped - they color as Text and
		/// would hide everything behind them. The decisive reference is additionally returned
		/// so callers can attach it to the enclosing AST node (the formatter looks up member
		/// references on the node, not the identifier token).
		/// </summary>
		static System.Collections.Generic.IEnumerable<object> ComposeAnnotations(object? decisive, System.Collections.Generic.IEnumerable<object> original) {
			if (decisive is not null)
				yield return decisive;
			foreach (var annotation in original) {
				if (IsEngineResolveResult(annotation))
					continue;
				yield return annotation;
			}
		}

		public AstNode VisitMemberType(CS.MemberType memberType, object data)
		{
			AstType target = null;

			if (memberType.Target is CS.SimpleType && ((CS.SimpleType)(memberType.Target)).Identifier.Equals("global", StringComparison.Ordinal))
				target = new PrimitiveType("Global");
			else if (memberType.Target is CS.SimpleType root && ShouldQualifyWithGlobal(root.Identifier))
				// Inside namespace dnSpy.*, the name "dnSpy.Contracts.DnSpy.X" resolves "dnSpy" to
				// the type dnSpy.Contracts.DnSpy and fails (BC30456). Qualifying with Global makes
				// VB start the lookup at the root namespace.
				target = new QualifiedType(new PrimitiveType("Global"),
					Identifier.Create(ComposeAnnotations(BoxedTextColor.Namespace, root.IdentifierToken.Annotations), root.Identifier));
			else
				target = (AstType)memberType.Target.AcceptVisitor(this, data);

			// The CS type node carries the IType annotation; resolve it so the name keeps its
			// color and navigation reference.
			var resolvedTypeRef = ResolveDnlibReference(memberType) ?? ResolveTypeNameFallback(memberType);
			var nameAnnotations = ComposeAnnotations(resolvedTypeRef, memberType.MemberNameToken.Annotations);
			var type = new QualifiedType(target, Identifier.Create(nameAnnotations, memberType.MemberName));
			if (resolvedTypeRef is not null)
				type.AddAnnotation(resolvedTypeRef);
			ConvertNodes(memberType.TypeArguments, type.TypeArguments);

			return EndNode(memberType, type);
		}

		// ILSpy 3.2 has no FunctionPointerAstType (that was a dnSpy-only AST extension).

		public AstNode VisitComposedType(CS.ComposedType composedType, object data)
		{
			AstType type = new ComposedType();

			ConvertNodes(composedType.ArraySpecifiers, ((ComposedType)type).ArraySpecifiers);
			((ComposedType)type).BaseType = (AstType)composedType.BaseType.AcceptVisitor(this, data);
			((ComposedType)type).HasNullableSpecifier = composedType.HasNullableSpecifier;

			for (int i = 0; i < composedType.PointerRank; i++) {
				var tmp = new SimpleType(Identifier.Create(BoxedTextColor.Keyword, "__Pointer"));
				tmp.TypeArguments.Add(type);
				type = tmp;
			}

			if (composedType.HasRefSpecifier) {
				var tmp = new SimpleType(Identifier.Create(BoxedTextColor.Keyword, "__ByRef"));
				tmp.TypeArguments.Add(type);
				type = tmp;
			}

			return EndNode(composedType, type);
		}

		public AstNode VisitArraySpecifier(CS.ArraySpecifier arraySpecifier, object data)
		{
			return EndNode(arraySpecifier, new ArraySpecifier(arraySpecifier.Dimensions));
		}

		public AstNode VisitPrimitiveType(CS.PrimitiveType primitiveType, object data)
		{
			string typeName;

			switch (primitiveType.Keyword) {
				case "object":
					typeName = "Object";
					break;
				case "bool":
					typeName = "Boolean";
					break;
				case "char":
					typeName = "Char";
					break;
				case "sbyte":
					typeName = "SByte";
					break;
				case "byte":
					typeName = "Byte";
					break;
				case "short":
					typeName = "Short";
					break;
				case "ushort":
					typeName = "UShort";
					break;
				case "int":
					typeName = "Integer";
					break;
				case "uint":
					typeName = "UInteger";
					break;
				case "long":
					typeName = "Long";
					break;
				case "ulong":
					typeName = "ULong";
					break;
				case "float":
					typeName = "Single";
					break;
				case "double":
					typeName = "Double";
					break;
				case "decimal":
					typeName = "Decimal";
					break;
				case "string":
					typeName = "String";
					break;
					// generic constraints
				case "new":
					typeName = "New";
					break;
				case "struct":
					typeName = "Structure";
					break;
				case "class":
					typeName = "Class";
					break;
				case "void":
					typeName = "Void";
					break;
				case "__arglist":
					typeName = "__ArgList";
					break;
				default:
					typeName = "unknown";
					break;
			}

			var primitive = new PrimitiveType(typeName);
			// The 2.3 engine attached a corlib TypeRef to every primitive type keyword, so
			// its formatter emitted 'As String' etc. with the reference; the formatter side
			// (node is PrimitiveType) is already ported - supply the annotation here.
			if (primitiveTypeCorlibNames.TryGetValue(typeName, out var corlibName))
				primitive.AddAnnotation(module.CorLibTypes.GetTypeRef("System", corlibName));
			return EndNode(primitiveType, primitive);
		}

		static readonly System.Collections.Generic.Dictionary<string, string> primitiveTypeCorlibNames =
			new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal) {
				{ "Object", "Object" },
				{ "Boolean", "Boolean" },
				{ "Char", "Char" },
				{ "SByte", "SByte" },
				{ "Byte", "Byte" },
				{ "Short", "Int16" },
				{ "UShort", "UInt16" },
				{ "Integer", "Int32" },
				{ "UInteger", "UInt32" },
				{ "Long", "Int64" },
				{ "ULong", "UInt64" },
				{ "Single", "Single" },
				{ "Double", "Double" },
				{ "Decimal", "Decimal" },
				{ "String", "String" },
				{ "Void", "Void" },
			};

		public AstNode VisitComment (CS.Comment comment, object data)
		{
			// Non-documentation comments are emitted too: the token comments (' Token: 0x... RID: ...')
			// reach the converter as plain single-line comments, and the 2.3 engine shows them.
			var c = new Comment (comment.Content, comment.CommentType == CS.CommentType.Documentation);

			if (comment.CommentType == CS.CommentType.MultiLine)
				throw new NotImplementedException ();

			return EndNode (comment, c);
		}

		public AstNode VisitPreProcessorDirective (CS.PreProcessorDirective preProcessorDirective, object data)
		{
			// TODO
			return null;
		}

		public AstNode VisitTypeParameterDeclaration(CS.TypeParameterDeclaration typeParameterDeclaration, object data)
		{
			var param = new TypeParameterDeclaration() {
				Variance = (VarianceModifier)typeParameterDeclaration.Variance,
				NameToken = Identifier.Create(
					ComposeAnnotations(
						(object?)FindGenericParameterReference(typeParameterDeclaration.Name, typeParameterDeclaration)
							?? (typeParameterDeclaration.Parent is CS.MethodDeclaration
								? BoxedTextColor.MethodGenericParameter
								: BoxedTextColor.TypeGenericParameter),
						typeParameterDeclaration.NameToken.Annotations),
					typeParameterDeclaration.Name)
			};
			param.NameToken.AddAnnotation(typeParameterDeclaration.Annotation<object>());

			var constraint = typeParameterDeclaration.Parent
				.GetChildrenByRole(CS.Roles.Constraint)
				.SingleOrDefault(c => c.TypeParameter.Identifier == typeParameterDeclaration.Name);

			if (constraint != null)
				ConvertNodes(constraint.BaseTypes, param.Constraints);

			// TODO : typeParameterDeclaration.Attributes get lost?
			//ConvertNodes(typeParameterDeclaration.Attributes

			return EndNode(typeParameterDeclaration, param);
		}

		public AstNode VisitConstraint(CS.Constraint constraint, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitCSharpTokenNode(CS.CSharpTokenNode cSharpTokenNode, object data)
		{
			var mod = cSharpTokenNode as CS.CSharpModifierToken;
			if (mod != null) {
				var convertedModifiers = ConvertModifiers(mod.Modifier, mod.Parent);
				VBModifierToken token = null;
				if (convertedModifiers != Modifiers.None) {
					token = new VBModifierToken(ICSharpCode.NRefactory.TextLocation.Empty, convertedModifiers);
					return EndNode(cSharpTokenNode, token);
				}
				return EndNode(cSharpTokenNode, token);
			} else {
				throw new NotSupportedException("Should never visit individual tokens");
			}
		}

		Modifiers ConvertModifiers(CS.Modifiers modifier, CS.AstNode container)
		{
			if ((modifier & CS.Modifiers.Any) == CS.Modifiers.Any)
				return Modifiers.Any;

			var mod = Modifiers.None;

			if ((modifier & CS.Modifiers.Const) == CS.Modifiers.Const)
				mod |= Modifiers.Const;
			if ((modifier & CS.Modifiers.Partial) == CS.Modifiers.Partial)
				mod |= Modifiers.Partial;
			if ((modifier & CS.Modifiers.Abstract) == CS.Modifiers.Abstract) {
				if (container is CS.TypeDeclaration)
					mod |= Modifiers.MustInherit;
				else
					mod |= Modifiers.MustOverride;
			}
			if ((modifier & CS.Modifiers.Static) == CS.Modifiers.Static)
				mod |= Modifiers.Shared;

			if ((modifier & CS.Modifiers.Public) == CS.Modifiers.Public)
				mod |= Modifiers.Public;
			if ((modifier & CS.Modifiers.Protected) == CS.Modifiers.Protected)
				mod |= Modifiers.Protected;
			if ((modifier & CS.Modifiers.Internal) == CS.Modifiers.Internal)
				mod |= Modifiers.Friend;
			if ((modifier & CS.Modifiers.Private) == CS.Modifiers.Private)
				mod |= Modifiers.Private;
			if (container is CS.IndexerDeclaration)
				mod |= Modifiers.Default;
			bool writeable = IsWriteableProperty(container);
			bool readable = IsReadableProperty(container);
			if (writeable && !readable)
				mod |= Modifiers.WriteOnly;
			if (readable && !writeable)
				mod |= Modifiers.ReadOnly;

			if ((modifier & CS.Modifiers.Override) == CS.Modifiers.Override)
				mod |= Modifiers.Overrides;
			if ((modifier & CS.Modifiers.Virtual) == CS.Modifiers.Virtual)
				mod |= Modifiers.Overridable;
			if ((modifier & CS.Modifiers.Async) == CS.Modifiers.Async)
				mod |= Modifiers.Async;
			if ((modifier & CS.Modifiers.Sealed) == CS.Modifiers.Sealed)
				mod |= Modifiers.NotInheritable;

			return mod;
		}

		bool IsReadableProperty(CS.AstNode container)
		{
			if (container is CS.IndexerDeclaration) {
				var i = container as CS.IndexerDeclaration;
				return !i.Getter.IsNull;
			}

			if (container is CS.PropertyDeclaration) {
				var p = container as CS.PropertyDeclaration;
				return !p.Getter.IsNull;
			}

			return false;
		}

		bool IsWriteableProperty(CS.AstNode container)
		{
			if (container is CS.IndexerDeclaration) {
				var i = container as CS.IndexerDeclaration;
				return !i.Setter.IsNull;
			}

			if (container is CS.PropertyDeclaration) {
				var p = container as CS.PropertyDeclaration;
				return !p.Setter.IsNull;
			}

			return false;
		}

		public AstNode VisitIdentifier(CS.Identifier identifier, object data)
		{
			var ident = Identifier.Create(identifier.Annotations, identifier.Name, ToNRefactoryLocation(identifier.StartLocation));
			// The 3.2 tree carries resolve results instead of the dnlib annotations the output
			// formatter consumes; resolve them so identifiers keep their colors and references.
			var reference = ResolveDnlibReference(identifier.Parent ?? identifier) ?? ResolveDnlibReference(identifier);
			if (reference != null)
				ident.AddAnnotation(reference);
			else
				AddSyntacticFallbackAnnotation(ident, identifier);
			return EndNode(identifier, ident);
		}

		/// <summary>
		/// Colors identifiers whose metadata resolution failed, mirroring the 2.3 engine:
		/// namespace segments, generic parameters, parameters, locals and type names. The
		/// output stage reads the first identifier annotation through
		/// MetadataTextColorProvider.GetColor, which passes TextColor/BoxedTextColor values
		/// through unchanged, so a color annotation is a valid fallback marker.
		/// </summary>
		void AddSyntacticFallbackAnnotation(Identifier ident, CS.Identifier identifier) {
			var color = SyntacticFallbackColor(identifier);
			if (color is not null)
				ident.AddAnnotation(color);
		}

		object? SyntacticFallbackColor(CS.Identifier identifier) {
			var parent = identifier.Parent;
			if (parent is null)
				return null;

			// Generic parameter declarations and in-scope usages.
			if (parent is CS.TypeParameterDeclaration) {
				return (object?)FindGenericParameterReference(identifier.Name, identifier)
					?? GenericParameterColorByName(identifier.Name, identifier)
					?? BoxedTextColor.TypeGenericParameter;
			}

			// Qualified type name segments: only the last segment is a type name; the
			// intermediate namespace segments match the 2.3 engine's Namespace color.
			if (parent is CS.SimpleType simple) {
				if (simple.Role == CS.MemberType.TargetRole ||
						simple.Parent is CS.NamespaceDeclaration || simple.Parent is CS.UsingDeclaration)
					return BoxedTextColor.Namespace;
				return TypeUsageColor(identifier.Name, identifier);
			}
			if (parent is CS.MemberType memberType && memberType.MemberNameToken == identifier) {
				if (memberType.Parent is CS.MemberType || memberType.Parent is CS.NamespaceDeclaration || memberType.Parent is CS.UsingDeclaration)
					return BoxedTextColor.Namespace;
				return TypeUsageColor(identifier.Name, identifier);
			}

			// Attribute named argument: '[AttributeUsage(AllowMultiple = true)]' renders as an
			// assignment inside the attribute; the left side is a property of the attribute type.
			if (parent is CS.IdentifierExpression attributeArg && attributeArg.Parent is CS.AssignmentExpression assignment &&
					assignment.Left == attributeArg && assignment.Parent is CS.Attribute)
				return BoxedTextColor.InstanceProperty;

			// Usage sites.
			if (parent is CS.IdentifierExpression usage) {
				if (usage.Parent is CS.NamespaceDeclaration)
					return BoxedTextColor.Namespace;
				if (usage.Parent is CS.InvocationExpression invocation && invocation.Target == usage)
					return BoxedTextColor.InstanceMethod;
				if (FindGenericParameterReference(identifier.Name, identifier) is { } usageGp)
					return usageGp;
				var genericColor = GenericParameterColorByName(identifier.Name, identifier);
				if (genericColor != null)
					return genericColor;
				if (IsParameterName(identifier.Name, identifier))
					return BoxedTextColor.Parameter;
				return BoxedTextColor.Local;
			}

			// Member usage: 'expr.Name', 'Console.WriteLine', 'MyEnum.Value'.
			if (parent is CS.MemberReferenceExpression memberRef && memberRef.MemberNameToken == identifier) {
				bool invoked = memberRef.Parent is CS.InvocationExpression inv && inv.Target == memberRef;
				// A type-name target is wrapped in a TypeReferenceExpression ('Console.WriteLine').
				bool targetIsTypeName = memberRef.Target is CS.TypeReferenceExpression;
				if (invoked)
					return targetIsTypeName ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
				return targetIsTypeName ? BoxedTextColor.InstanceField : BoxedTextColor.InstanceProperty;
			}

			// Declaration sites that carry no metadata annotation.
			if (parent is CS.ParameterDeclaration)
				return BoxedTextColor.Parameter;
			if (parent is CS.VariableInitializer || parent is CS.ForeachStatement || parent is CS.CatchClause)
				return BoxedTextColor.Local;

			return null;
		}

		object TypeUsageColor(string name, CS.AstNode scopeRoot) {
			return (object?)FindGenericParameterReference(name, scopeRoot)
				?? GenericParameterColorByName(name, scopeRoot)
				?? BoxedTextColor.Type;
		}

		/// <summary>
		/// The dnlib generic parameter for an in-scope generic parameter name: scans the
		/// enclosing declarations' dnlib annotations (innermost method wins, mirroring the
		/// color precedence), matching by name, then by declaration order for renamed
		/// parameters. The 2.3 engine emitted the GenericParam as a navigation reference on
		/// every generic parameter token, declaration or usage.
		/// </summary>
		dnlib.DotNet.GenericParam? FindGenericParameterReference(string name, CS.AstNode scopeRoot) {
			if (string.IsNullOrEmpty(name))
				return null;
			for (var node = scopeRoot; node is not null; node = node.Parent) {
				if (node is CS.MethodDeclaration md && md.Annotation<MethodDef>() is { } methodDef) {
					var methodGp = methodDef.GenericParameters.FirstOrDefault(g => g.Name == name)
						?? (md.TypeParameters.Any(tp => tp.Name == name) ? methodDef.GenericParameters.FirstOrDefault() : null);
					if (methodGp is not null)
						return methodGp;
				}
				if (node is CS.TypeDeclaration td && td.Annotation<TypeDef>() is { } typeDef) {
					var typeGp = typeDef.GenericParameters.FirstOrDefault(g => g.Name == name)
						?? (td.TypeParameters.Any(tp => tp.Name == name) ? typeDef.GenericParameters.FirstOrDefault() : null);
					if (typeGp is not null)
						return typeGp;
				}
			}
			return null;
		}

		/// <summary>
		/// Colors a generic parameter name by scanning the enclosing declarations; innermost
		/// (method) parameters win over type parameters.
		/// </summary>
		object? GenericParameterColorByName(string name, CS.AstNode scopeRoot) {
			if (string.IsNullOrEmpty(name))
				return null;
			for (var node = scopeRoot; node is not null; node = node.Parent) {
				if (node is CS.MethodDeclaration md && md.TypeParameters.Any(tp => tp.Name == name))
					return BoxedTextColor.MethodGenericParameter;
				if (node is CS.TypeDeclaration td && td.TypeParameters.Any(tp => tp.Name == name))
					return BoxedTextColor.TypeGenericParameter;
			}
			return null;
		}

		/// <summary>True when 'name' is a parameter of an enclosing method or lambda.</summary>
		bool IsParameterName(string name, CS.AstNode scopeRoot) {
			if (string.IsNullOrEmpty(name))
				return false;
			for (var node = scopeRoot; node is not null; node = node.Parent) {
				System.Collections.Generic.IEnumerable<CS.ParameterDeclaration>? parameters = node switch {
					CS.MethodDeclaration md => md.Parameters,
					CS.ConstructorDeclaration cd => cd.Parameters,
					CS.OperatorDeclaration od => od.Parameters,
					CS.LambdaExpression lambda => lambda.Parameters,
					_ => null,
				};
				if (parameters != null && parameters.Any(p => p.Name == name))
					return true;
			}
			return false;
		}

		/// <summary>
		/// dnlib metadata reference for a node: resolve results and symbols map to cecil and
		/// back through the shared dnlib/cecil token table (PreserveRids keeps them equal).
		/// </summary>
		object? ResolveDnlibReference(CS.AstNode node) {
			if (engineTypeSystem is null || node is null)
				return null;
			var rr = node.Annotation<ICSharpCode.Decompiler.Semantics.MemberResolveResult>();
			if (rr?.Member != null) {
				var resolved = ToDnlibRef(TryGetCecilRef(rr.Member));
				if (resolved is not null)
					return resolved;
				// External members (x.Dispatcher, x.TextSnapshot on WPF/BCL types) fail
				// GetCecil; the module's own MemberRef table carries the authoritative entry
				// - the same fallback the C# token writer uses.
				return FindMemberRefForTypeSystemMember(rr.Member, node);
			}
			// Call targets: the engine annotates the InvocationExpression, not the target
			// member access (CallBuilder's RequireTarget path creates a bare member access).
			// Resolve the called method from there - the 2.3 engine attached the same ref to
			// the target identifier.
			if (node.Parent is CS.InvocationExpression invocation && invocation.Target == node &&
					node.Annotation<ICSharpCode.Decompiler.Semantics.ResolveResult>() is null &&
					invocation.Annotation<ICSharpCode.Decompiler.Semantics.ResolveResult>() is ICSharpCode.Decompiler.Semantics.InvocationResolveResult targetRR &&
					targetRR.Member != null) {
				var targetResolved = ToDnlibRef(TryGetCecilRef(targetRR.Member));
				if (targetResolved is not null)
					return targetResolved;
				return FindMemberRefForTypeSystemMember(targetRR.Member, node);
			}
			var trr = node.Annotation<ICSharpCode.Decompiler.Semantics.TypeResolveResult>();
			if (trr?.Type?.GetDefinition() != null)
				return ToDnlibRef(TryGetCecilRef(trr.Type.GetDefinition()));
			var type = node.Annotation<ICSharpCode.Decompiler.TypeSystem.IType>();
			if (type != null) {
				var definition = type.GetDefinition();
				if (definition != null)
					return ToDnlibRef(TryGetCecilRef(definition));
			}
			var symbol = ICSharpCode.Decompiler.CSharp.AnnotationExtensions.GetSymbol(node);
			switch (symbol) {
			case ICSharpCode.Decompiler.TypeSystem.IMember member:
				return ToDnlibRef(TryGetCecilRef(member));
			case ICSharpCode.Decompiler.TypeSystem.IType typeSymbol:
				var typeDefinition = typeSymbol.GetDefinition();
				return typeDefinition != null ? ToDnlibRef(TryGetCecilRef(typeDefinition)) : null;
			}
			return null;
		}

		/// <summary>
		/// Constructor reference for an object creation: the engine annotates the
		/// ObjectCreateExpression with a CSharpInvocationResolveResult whose Member is the
		/// ctor; external ctors fall back to the module's MemberRef table (constructor name
		/// is always .ctor). Mirrors the C# token writer's new-keyword path.
		/// </summary>
		object? ResolveCtorRefForCreation(CS.ObjectCreateExpression node) {
			var rr = node.Annotation<ICSharpCode.Decompiler.Semantics.ResolveResult>();
			ICSharpCode.Decompiler.TypeSystem.IMethod? ctor = rr switch {
				ICSharpCode.Decompiler.CSharp.Resolver.CSharpInvocationResolveResult csInv => (ICSharpCode.Decompiler.TypeSystem.IMethod?)csInv.Member,
				ICSharpCode.Decompiler.Semantics.InvocationResolveResult inv => (ICSharpCode.Decompiler.TypeSystem.IMethod?)inv.Member,
				_ => null,
			};
			if (ctor is null)
				return null;
			var resolved = ToDnlibRef(TryGetCecilRef(ctor));
			if (resolved is not null)
				return resolved;
			if (ctor.DeclaringType is not null && module is ModuleDefMD moduleMd)
				return Common.DnlibReferenceMapper.FindMemberRefInTable(moduleMd, ctor.DeclaringType.FullName, ".ctor")
					?? Common.DnlibReferenceMapper.FindMemberRefInTable(moduleMd, ctor.DeclaringType.FullName, ctor.Name);
			return null;
		}

		/// <summary>
		/// Resolves a type-system member to a dnlib reference when the cecil bridge fails
		/// (external members): looks up the module's own MemberRef table by declaring type and
		/// member name, with accessor-name variants for properties/events and prefix matching
		/// for generic instantiations.
		/// </summary>
		object? FindMemberRefForTypeSystemMember(ICSharpCode.Decompiler.TypeSystem.IMember member, CS.AstNode node) {
			var declaringNames = new List<string>();
			if (member.DeclaringType is not null)
				declaringNames.Add(member.DeclaringType.FullName);
			switch (node) {
			case CS.MemberReferenceExpression usageMre when usageMre.Target is CS.TypeReferenceExpression tre:
				// 'StaticType.Member' - the target names the declaring type directly.
				declaringNames.Insert(0, tre.Type.ToString());
				break;
			}
			if (declaringNames.Count == 0)
				return null;
			var memberNames = new List<string> { member.Name };
			if (member is ICSharpCode.Decompiler.TypeSystem.IProperty property) {
				if (property.Getter is not null)
					memberNames.Add(property.Getter.Name);
				if (property.Setter is not null)
					memberNames.Add(property.Setter.Name);
			}
			if (member is ICSharpCode.Decompiler.TypeSystem.IEvent ev) {
				if (ev.AddAccessor is not null)
					memberNames.Add(ev.AddAccessor.Name);
				if (ev.RemoveAccessor is not null)
					memberNames.Add(ev.RemoveAccessor.Name);
			}
			foreach (var declaringName in declaringNames)
				foreach (var memberName in memberNames)
					if (module is ModuleDefMD moduleMd && Common.DnlibReferenceMapper.FindMemberRefInTable(moduleMd, declaringName, memberName) is { } found)
						return found;
			// Array.Length/LongLength compiles to the raw ldlen instruction (no MemberRef in
			// the table); the 2.3 engine synthesized the get_Length MemberRefUser, so match it.
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

		Mono.Cecil.MemberReference? TryGetCecilRef(ICSharpCode.Decompiler.TypeSystem.IMember? member) {
			if (member is null || engineTypeSystem is null)
				return null;
			try {
				return engineTypeSystem.GetCecil(member);
			}
			catch {
				return null;
			}
		}

		Mono.Cecil.MemberReference? TryGetCecilRef(ICSharpCode.Decompiler.TypeSystem.ITypeDefinition? definition) {
			if (definition is null || engineTypeSystem is null)
				return null;
			try {
				return engineTypeSystem.GetCecil(definition);
			}
			catch {
				return null;
			}
		}

		object? ToDnlibRef(Mono.Cecil.MemberReference? cecilMember) {
			if (cecilMember is null || module is not ModuleDefMD moduleMd)
				return null;
			return Common.DnlibReferenceMapper.ToDnlib(moduleMd, cecilMember);
		}

		/// <summary>
		/// Reference or color for a type name the engine left unannotated, mirroring the 2.3
		/// engine: a type defined in this module keeps its kind color, an external type maps
		/// by name to a TypeRef from the module's own reference tables (the formatter colors
		/// those plain Type), a generic parameter in scope gets its generic-parameter color,
		/// and namespace segments get the Namespace color. Attribute type names drop the
		/// 'Attribute' suffix in C# and VB output, hence the suffix fallback.
		/// </summary>
		/// <summary>Full dotted name of a type node's MemberType chain ('A.B.C' of 'C').</summary>
		static string? BuildQualifiedNamespace(CS.AstType typeNode) {
			switch (typeNode) {
			case CS.SimpleType simple:
				return simple.Identifier;
			case CS.MemberType memberType:
				var prefix = BuildQualifiedNamespace(memberType.Target);
				return prefix is null ? null : prefix + "." + memberType.MemberName;
			default:
				return null;
			}
		}

		object? ResolveTypeNameFallback(CS.AstType typeNode) {
			if (module is not ModuleDefMD moduleMd)
				return null;
			string name;
			int arity;
			bool isSegment;
			switch (typeNode) {
			case CS.SimpleType simple:
				name = simple.Identifier;
				arity = simple.TypeArguments.Count;
				isSegment = simple.Role == CS.MemberType.TargetRole;
				break;
			case CS.MemberType memberType:
				name = memberType.MemberName;
				arity = memberType.TypeArguments.Count;
				isSegment = memberType.Parent is CS.MemberType || memberType.Parent is CS.NamespaceDeclaration || memberType.Parent is CS.UsingDeclaration;
				break;
			default:
				return null;
			}
			if (string.IsNullOrEmpty(name))
				return null;
			if (isSegment) {
				// Every namespace segment the 2.3 engine emitted carried a NamespaceReference
				// (assembly resolved from any TypeRef of that namespace). Rebuild the segment's
				// full namespace from the AST chain to find the same TypeRef.
				if (BuildQualifiedNamespace(typeNode) is { } fullNamespace) {
					var typeRef = moduleMd.GetTypeRefs().FirstOrDefault(tr => tr.Namespace == fullNamespace)
						?? moduleMd.GetTypeRefs().FirstOrDefault(tr => fullNamespace.StartsWith(tr.Namespace + ".", StringComparison.Ordinal));
					if (typeRef?.ResolutionScope is dnlib.DotNet.IAssembly scope)
						return new NamespaceReference(scope, name);
				}
				return BoxedTextColor.Namespace;
			}
			if (FindGenericParameterReference(name, typeNode) is { } fallbackGp)
				return fallbackGp;
			var genericColor = GenericParameterColorByName(name, typeNode);
			if (genericColor != null)
				return genericColor;
			// A type defined in this module keeps its kind color (2.3 behavior); Find needs the
			// full name, so look up by simple name through a lazily built table.
			if (localTypesByName is null) {
				localTypesByName = new System.Collections.Generic.Dictionary<string, TypeDef>(StringComparer.Ordinal);
				foreach (var typeDef in moduleMd.Types) {
					if (!localTypesByName.ContainsKey(typeDef.Name))
						localTypesByName[typeDef.Name] = typeDef;
				}
			}
			if (LookupLocalType(name) is { } localType)
				return localType;
			return (object?)Common.DnlibReferenceMapper.ResolveTypeRefByName(moduleMd, name, arity)
				?? Common.DnlibReferenceMapper.ResolveTypeRefByName(moduleMd, name + "Attribute", -1);
		}

		System.Collections.Generic.Dictionary<string, TypeDef>? localTypesByName;

		static TypeDef? LookupLocalTypeStatic(ModuleDef module, string simpleName) {
			if (module is not ModuleDefMD moduleMd)
				return null;
			if (sharedLocalTypesByName is null || !ReferenceEquals(sharedLocalTypesByNameModule, module)) {
				sharedLocalTypesByName = new System.Collections.Generic.Dictionary<string, TypeDef>(StringComparer.Ordinal);
				sharedLocalTypesByNameModule = module;
				foreach (var typeDef in moduleMd.Types) {
					if (!sharedLocalTypesByName.ContainsKey(typeDef.Name))
						sharedLocalTypesByName[typeDef.Name] = typeDef;
				}
			}
			return sharedLocalTypesByName.TryGetValue(simpleName, out var found) ? found : null;
		}

		static System.Collections.Generic.Dictionary<string, TypeDef>? sharedLocalTypesByName;
		static ModuleDef? sharedLocalTypesByNameModule;

		TypeDef? LookupLocalType(string simpleName) {
			if (module is not ModuleDefMD moduleMd)
				return null;
			if (localTypesByName is null) {
				localTypesByName = new System.Collections.Generic.Dictionary<string, TypeDef>(StringComparer.Ordinal);
				foreach (var typeDef in moduleMd.Types) {
					if (!localTypesByName.ContainsKey(typeDef.Name))
						localTypesByName[typeDef.Name] = typeDef;
				}
			}
			localTypesByName.TryGetValue(simpleName, out var found);
			return found;
		}

		public AstNode VisitPatternPlaceholder(CS.AstNode placeholder, CS.PatternMatching.Pattern pattern, object data)
		{
			throw new NotImplementedException();
		}

		void ConvertNodes<T>(IEnumerable<CS.AstNode> nodes, VB.AstNodeCollection<T> result, Func<T, T> transform = null) where T: VB.AstNode
		{
			foreach (var node in nodes) {
				T n = (T)node.AcceptVisitor(this, null);
				if (transform != null)
					n = transform(n);
				if (n != null)
					result.Add(n);
			}
		}

		void ConvertMembers<T, S, M>(CS.AstNode parent, T result, CS.Role<S> sourceRole, ICSharpCode.NRefactory.Role<M> targetRole) where T : VB.AstNode where S : CS.AstNode where M : VB.AstNode
		{
			foreach (var node in parent.Children) {
				if (node.Role == CS.Roles.Comment) {
					var n = (Comment)node.AcceptVisitor(this, null);
					if (n != null)
						result.AddChild(n, AstNode.Roles.Comment);
				}

				if (node.Role == sourceRole) {
					var n = (M)node.AcceptVisitor(this, null);
					if (n != null)
						result.AddChild(n, targetRole);
				}
			}
		}

		T EndNode<T>(CS.AstNode node, T result) where T : VB.AstNode
		{
			if (result != null) {
				CopyComments(node, result);
				CopyAnnotations(node, result);
			}

			return result;
		}

		// ILSpy 3.2 uses its own TextLocation struct; the VB AST uses the
		// NRefactory one. Both are line/column structs, convert field by field.
		static ICSharpCode.NRefactory.TextLocation ToNRefactoryLocation(CS.TextLocation loc) =>
			new ICSharpCode.NRefactory.TextLocation(loc.Line, loc.Column);

		void CopyAnnotations<T>(CS.AstNode node, T result) where T : VB.AstNode
		{
			foreach (var ann in node.Annotations)
				result.AddAnnotation(ann);
		}

		static string NormalizeAttributeName(string name) {
			if (string.IsNullOrEmpty(name))
				return string.Empty;
			int ctor = name.IndexOf("::", StringComparison.Ordinal);
			if (ctor >= 0)
				name = name.Substring(0, ctor);
			int dot = name.LastIndexOf('.');
			if (dot >= 0)
				name = name.Substring(dot + 1);
			// ILSpy writes the attribute type without the "Attribute" suffix
			// ("System.Runtime.InteropServices.DllImport"), so compare the bare names.
			const string suffix = "Attribute";
			if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
				name = name.Substring(0, name.Length - suffix.Length);
			return name;
		}

		readonly Dictionary<string, bool> interfaceLookup = new Dictionary<string, bool>(StringComparer.Ordinal);

		// Base types reach the converter without a TypeReference annotation, so
		// GetTypeKindForAstType answers Unknown and interfaces ended up in Inherits, which VB
		// rejects (BC30258). Look the name up in the dnlib module instead.
		bool IsInterfaceType(CS.AstType astType) {
			if (provider.GetTypeKindForAstType(astType) == TypeKind.Interface)
				return true;

			var name = GetShortTypeName(astType);
			if (string.IsNullOrEmpty(name))
				return false;
			if (interfaceLookup.TryGetValue(name, out var cached))
				return cached;

			bool result = false;
			bool found = false;
			foreach (var type in module.GetTypes()) {
				if (type.Name != name)
					continue;
				result = type.IsInterface;
				found = true;
				break;
			}
			if (!found) {
				foreach (var typeRef in module.GetTypeRefs()) {
					if (typeRef.Name != name)
						continue;
					var definition = typeRef.Resolve();
					if (definition != null)
						result = definition.IsInterface;
					break;
				}
			}
			// Not found in this module or its type refs: fall back to the .NET naming
			// convention (I followed by an upper-case letter). A wrong guess only moves the
			// error, whereas leaving it as a class is always wrong for an interface.
			if (!found && name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
				result = true;
			interfaceLookup[name] = result;
			return result;
		}

		static string GetShortTypeName(CS.AstType astType) {
			switch (astType) {
			case CS.SimpleType simple:
				return simple.Identifier;
			case CS.MemberType member:
				return member.MemberName;
			case CS.ComposedType composed:
				return GetShortTypeName(composed.BaseType);
			default:
				return null;
			}
		}

		bool HasAttribute(CS.AstNodeCollection<CS.AttributeSection> attributes, string name, out CS.Attribute foundAttribute)
		{
			foreach (var attr in attributes.SelectMany(a => a.Attributes)) {
				// The provider resolves the attribute through a MemberReference annotation, but
				// decompiled type nodes carry no such annotation (see the DateTime note), so the
				// name came back empty and P/Invoke methods were emitted as bodyless Function
				// declarations. Fall back to the attribute type as written in the C# output.
				// Try both the annotation-derived name and the syntactic type: the annotation can
				// be the attribute constructor ("...DllImportAttribute::.ctor(System.String)").
				var resolved = provider.GetTypeNameForAttribute(attr);
				var syntactic = attr.Type?.ToString();
				if (NormalizeAttributeName(resolved) == NormalizeAttributeName(name)
						|| NormalizeAttributeName(syntactic) == NormalizeAttributeName(name)) {
					foundAttribute = attr;
					return true;
				}
			}
			foundAttribute = null;
			return false;
		}

		public AstNode VisitDocumentationReference(CS.DocumentationReference documentationReference, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitNewLine(CS.NewLineNode newLineNode, object data)
		{
			return null;
		}

		public AstNode VisitWhitespace(CS.WhitespaceNode whitespaceNode, object data)
		{
			return null;
		}

		public AstNode VisitText(CS.TextNode textNode, object data)
		{
			return null;
		}

		public AstNode VisitNullNode(CS.AstNode nullNode, object data)
		{
			return null;
		}

		public AstNode VisitErrorNode(CS.AstNode errorNode, object data)
		{
			return null;
		}

		void CopyComments(CS.AstNode node, AstNode result)
		{
			// Plain comments (the token comments) are converted by ConvertMembers in source
			// order; copying them here as well would render every one of them twice.
			if (node is CS.EntityDeclaration || node is CS.NamespaceDeclaration || node is CS.SyntaxTree)
				return;
			foreach (CS.Comment c in node.GetChildrenByRole(CS.Roles.Comment).Reverse()) {
				if (c.IsDocumentation)
					continue;
				result.InsertChildAfter(null, new Comment(c.Content), AstNode.Roles.Comment);
			}
		}

		// --- ILSpy 3.2 added these C# AST nodes; the 2.3-based converter had no visitors for them.
		// The engine is configured (DecompilerSettings32) to expand tuples / string interpolation
		// into their legacy forms, so these should rarely be reached. If they are, the member
		// fails to convert and dnSpy shows an error note instead of wrong VB code.

		public AstNode VisitInterpolatedStringExpression(CS.InterpolatedStringExpression interpolatedStringExpression, object data) =>
			throw new NotSupportedException("VB output for interpolated strings is not implemented");

		public AstNode VisitInterpolatedStringText(CS.InterpolatedStringText interpolatedStringText, object data) =>
			throw new NotSupportedException("VB output for interpolated strings is not implemented");

		public AstNode VisitInterpolation(CS.Interpolation interpolation, object data) =>
			throw new NotSupportedException("VB output for interpolated strings is not implemented");

		public AstNode VisitOutVarDeclarationExpression(CS.OutVarDeclarationExpression outVarDeclarationExpression, object data)
		{
			// C# 7 "M(out var x)" has no VB equivalent. Hoist "Dim x As T" in front of the
			// statement being converted and pass x by reference: VB derives ByRef from the
			// parameter signature and needs no out/ref keyword.
			// ILSpy 3.2 writes the inferred type explicitly (not "var"), so no type inference
			// is needed here. The declaration is added to the enclosing block while that block
			// has not yet received the statement being converted, which keeps the right order.
			// DeclareLocal keeps the name valid in VB: it avoids hiding an enclosing local and
			// gives a discard ("_") a usable unique name.
			string name = DeclareLocal(outVarDeclarationExpression.Variable.Name);
			variableTypes[name] = outVarDeclarationExpression.Type.ToString();
			var decl = new LocalDeclarationStatement();
			decl.Modifiers = Modifiers.Dim;
			var variable = new VariableDeclaratorWithTypeAndInitializer();
			variable.Type = (AstType)outVarDeclarationExpression.Type.AcceptVisitor(this, data);
			variable.Identifiers.Add(new VariableIdentifier() {
				Name = Identifier.Create(outVarDeclarationExpression.Variable.NameToken.Annotations, name)
			});
			decl.Variables.Add(variable);
			if (blocks.Count > 0)
				blocks.Peek().Add(decl);

			var expr = new IdentifierExpression();
			expr.Identifier = Identifier.Create(outVarDeclarationExpression.Variable.NameToken.Annotations, name);
			return EndNode(outVarDeclarationExpression, expr);
		}

		public AstNode VisitThrowExpression(CS.ThrowExpression throwExpression, object data) =>
			throw new NotSupportedException("VB output for throw expressions is not implemented");

		public AstNode VisitTupleExpression(CS.TupleExpression tupleExpression, object data)
		{
			var expr = new TupleExpression();
			ConvertNodes(tupleExpression.Elements, expr.Elements);
			return EndNode(tupleExpression, expr);
		}

		public AstNode VisitTupleType(CS.TupleAstType tupleType, object data)
		{
			var type = new TupleType();
			ConvertNodes(tupleType.Elements, type.Elements);
			return EndNode(tupleType, type);
		}

		public AstNode VisitTupleTypeElement(CS.TupleTypeElement tupleTypeElement, object data)
		{
			var element = new TupleTypeElement();
			// The name is optional: (Integer, String) is a valid tuple type.
			var name = tupleTypeElement.NameToken;
			if (name != null && !string.IsNullOrEmpty(name.Name))
				element.Identifier = Identifier.Create(name.Annotations, name.Name);
			element.Type = (AstType)tupleTypeElement.Type.AcceptVisitor(this, data);
			return EndNode(tupleTypeElement, element);
		}
	}
}
