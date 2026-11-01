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

		public CSharpToVBConverter32(ModuleDef module, IEnvironmentProvider provider)
		{
			this.module = module;
			this.provider = provider;
			this.blocks = new Stack<BlockStatement>();
			this.types = new Stack<TypeDeclaration>();
			this.members = new Stack<MemberInfo>();
			this.switchLabelMaps = new Stack<SwitchLabelMap>();
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
			return EndNode(asExpression, new CastExpression(CastType.TryCast, (AstType)asExpression.Type.AcceptVisitor(this, data), (Expression)asExpression.Expression.AcceptVisitor(this, data)));
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

		// VB has no ++/--. A standalone "x++;" becomes "x += 1". In an expression the value
		// matters: prefix yields the new value (re-read the target after hoisting the update),
		// postfix yields the old one (copy to a temporary first). When hoisting is unsafe the
		// old helper call is kept, even though no such helper exists - it is at least visible.
		Expression ConvertIncrement(CS.UnaryOperatorExpression unaryOperatorExpression, object data, bool increment, bool postfix) {
			var target = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data);
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

		public AstNode VisitBaseReferenceExpression(CS.BaseReferenceExpression baseReferenceExpression, object data)
		{
			InstanceExpression result = new InstanceExpression(InstanceExpressionType.MyBase, ToNRefactoryLocation(baseReferenceExpression.StartLocation));

			return EndNode(baseReferenceExpression, result);
		}

		public AstNode VisitBinaryOperatorExpression(CS.BinaryOperatorExpression binaryOperatorExpression, object data)
		{
			var left = (Expression)binaryOperatorExpression.Left.AcceptVisitor(this, data);
			var op = BinaryOperatorType.None;
			var right = (Expression)binaryOperatorExpression.Right.AcceptVisitor(this, data);

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

			return EndNode(binaryOperatorExpression, new BinaryOperatorExpression(left, op, right));
		}

		bool IsReferentialEquality(CS.BinaryOperatorExpression binaryOperatorExpression)
		{
			var left = provider.IsReferenceType(binaryOperatorExpression.Left);
			var right = provider.IsReferenceType(binaryOperatorExpression.Right);

			var leftCode = provider.ResolveExpression(binaryOperatorExpression.Left);
			var rightCode = provider.ResolveExpression(binaryOperatorExpression.Right);

			return (left == true || right == true) && (leftCode != TypeCode.String && rightCode != TypeCode.String);
		}

		public AstNode VisitCastExpression(CS.CastExpression castExpression, object data)
		{
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
			var expr = new IdentifierExpression();
			expr.Identifier = Identifier.Create(identifierExpression.IdentifierToken.Annotations, identifierExpression.Identifier);
			ConvertNodes(identifierExpression.TypeArguments, expr.TypeArguments);
			if (provider.IsMethodGroup(identifierExpression)) {
				return EndNode(identifierExpression, new UnaryOperatorExpression(UnaryOperatorType.AddressOf, expr));
			}

			return EndNode(identifierExpression, expr);
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
			memberAccessExpression.MemberName.AddAnnotation(memberReferenceExpression.Annotation<IMemberRef>());
			bool isExtensionMethod = false;
			if (memberReferenceExpression.Parent is CS.InvocationExpression invocExpr) {
				var method = invocExpr.Annotation<dnlib.DotNet.IMethod>().ResolveMethodDef();
				isExtensionMethod = method?.CustomAttributes.IsDefined("System.Runtime.CompilerServices.ExtensionAttribute") == true;
			}
			if (!isExtensionMethod)
				ConvertNodes(memberReferenceExpression.TypeArguments, memberAccessExpression.TypeArguments);
			if (provider.IsMethodGroup(memberReferenceExpression)) {
				return EndNode(memberReferenceExpression, new UnaryOperatorExpression(UnaryOperatorType.AddressOf, memberAccessExpression));
			}

			return EndNode(memberReferenceExpression, memberAccessExpression);
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
			if (namedExpression.Parent is CS.Attribute) {
				return EndNode(namedExpression, new NamedArgumentExpression {
					Identifier = Identifier.Create(namedExpression.NameToken.Annotations, namedExpression.Name),
					Expression = (Expression)namedExpression.Expression.AcceptVisitor(this, data)
				});
			}

			Expression expr = new FieldInitializerExpression {
				IsKey = true,
				Identifier = Identifier.Create(namedExpression.NameToken.Annotations, namedExpression.Name),
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
			ConvertNodes(objectCreateExpression.Arguments, expr.Arguments);
			var arg1 = expr.Arguments.FirstOrDefault() as UnaryOperatorExpression;
			if (arg1 != null && arg1.Operator == UnaryOperatorType.AddressOf) {
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
			else
				expr = new PrimitiveExpression(primitiveExpression.Value);

			return EndNode(primitiveExpression, expr);
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
			throw new NotImplementedException();
		}

		public AstNode VisitQueryFromClause(CS.QueryFromClause queryFromClause, object data)
		{
			var op = new FromQueryOperator();
			op.Variables.Add(
				new CollectionRangeVariableDeclaration {
					Identifier = new VariableIdentifier { Name = Identifier.Create(queryFromClause.IdentifierToken.Annotations, queryFromClause.Identifier) },
					Type = (AstType)queryFromClause.Type.AcceptVisitor(this, data),
					Expression = (Expression)queryFromClause.Expression.AcceptVisitor(this, data)
				}
			);

			return EndNode(queryFromClause, op);
		}

		public AstNode VisitQueryLetClause(CS.QueryLetClause queryLetClause, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitQueryWhereClause(CS.QueryWhereClause queryWhereClause, object data)
		{
			throw new NotImplementedException();
		}

		public AstNode VisitQueryJoinClause(CS.QueryJoinClause queryJoinClause, object data)
		{
			throw new NotImplementedException();
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

		public AstNode VisitQueryGroupClause(CS.QueryGroupClause queryGroupClause, object data)
		{
			var op = new GroupByQueryOperator();

			throw new NotImplementedException();

			//return EndNode(queryGroupClause, op);
		}

		public AstNode VisitAttribute(CS.Attribute attribute, object data)
		{
			var attr = new VB.Ast.Attribute();
			AttributeTarget target;
			Enum.TryParse(((CS.AttributeSection)attribute.Parent).AttributeTarget, true, out target);
			attr.Target = target;
			attr.Type = (AstType)attribute.Type.AcceptVisitor(this, data);
			ConvertNodes(attribute.Arguments, attr.Arguments);

			return EndNode(attribute, attr);
		}

		public AstNode VisitAttributeSection(CS.AttributeSection attributeSection, object data)
		{
			AttributeBlock block = new AttributeBlock();
			ConvertNodes(attributeSection.Attributes, block.Attributes);
			return EndNode(attributeSection, block);
		}

		public AstNode VisitDelegateDeclaration(CS.DelegateDeclaration delegateDeclaration, object data)
		{
			var result = new DelegateDeclaration();

			ConvertNodes(delegateDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
			ConvertNodes(delegateDeclaration.ModifierTokens, result.ModifierTokens);
			result.Name = Identifier.Create(delegateDeclaration.NameToken.Annotations, delegateDeclaration.Name);
			result.IsSub = IsSub(delegateDeclaration.ReturnType);
			ConvertNodes(delegateDeclaration.Parameters, result.Parameters);
			ConvertNodes(delegateDeclaration.TypeParameters, result.TypeParameters);
			ConvertNodes(delegateDeclaration.Attributes.Where(section => section.AttributeTarget == "return"), result.ReturnTypeAttributes);
			if (!result.IsSub)
				result.ReturnType = (AstType)delegateDeclaration.ReturnType.AcceptVisitor(this, data);
			return EndNode(delegateDeclaration, result);
		}

		public AstNode VisitNamespaceDeclaration(CS.NamespaceDeclaration namespaceDeclaration, object data)
		{
			var newNamespace = new NamespaceDeclaration();

			foreach (var name in namespaceDeclaration.Identifiers) {
				var newId = new Identifier(BoxedTextColor.Namespace, name, ICSharpCode.NRefactory.TextLocation.Empty);
				newNamespace.Identifiers.Add(newId);
			}
			ConvertMembers(namespaceDeclaration, newNamespace, CS.NamespaceDeclaration.MemberRole, NamespaceDeclaration.MemberRole);

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

				type.Name = Identifier.Create(typeDeclaration.NameToken.Annotations, typeDeclaration.Name);

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

				type.Name = Identifier.Create(typeDeclaration.NameToken.Annotations, typeDeclaration.Name);

				types.Push(type);
				ConvertMembers(typeDeclaration, type, CS.Roles.TypeMemberRole, TypeDeclaration.MemberRole);
				types.Pop();

				return EndNode(typeDeclaration, type);
			}
		}

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

			var clause = new MemberImportsClause() {
				Member = (AstType)usingDeclaration.Import.AcceptVisitor(this, data)
			};

			imports.AddChild(clause, ImportsStatement.ImportsClauseRole);

			return EndNode(usingDeclaration, imports);
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
			var stmt = new WhileStatement() {
				Condition = (Expression)forStatement.Condition.AcceptVisitor(this, data),
				Body = (BlockStatement)forStatement.EmbeddedStatement.AcceptVisitor(this, data)
			};
			ConvertNodes(forStatement.Iterators, stmt.Body.Statements);
			foreach (var initializer in forStatement.Initializers)
				blocks.Peek().Statements.Add((Statement)initializer.AcceptVisitor(this, data));
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
			var decl = new LocalDeclarationStatement();
			decl.Modifiers = Modifiers.Dim;
			ConvertNodes(variableDeclarationStatement.Variables, decl.Variables);

			return EndNode(variableDeclarationStatement, decl);
		}

		public AstNode VisitWhileStatement(CS.WhileStatement whileStatement, object data)
		{
			var stmt = new WhileStatement() {
				Condition = (Expression)whileStatement.Condition.AcceptVisitor(this, data),
				Body = (BlockStatement)whileStatement.EmbeddedStatement.AcceptVisitor(this, data)
			};

			return EndNode(whileStatement, stmt);
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
			var ctor = constructorInitializer.Annotation<dnlib.DotNet.IMethod>();
			instExpr.AddAnnotation(ctor?.DeclaringType);
			identifier.AddAnnotation(ctor);
			CopyAnnotations(constructorInitializer, instExpr);
			ConvertNodes(constructorInitializer.Arguments, result.Arguments);

			return EndNode(constructorInitializer, new ExpressionStatement(result));
		}

		public AstNode VisitDestructorDeclaration(CS.DestructorDeclaration destructorDeclaration, object data)
		{
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
			result.Name = Identifier.Create(enumMemberDeclaration.NameToken.Annotations, enumMemberDeclaration.Name);
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
				result.Modifiers = ConvertModifiers(eventDeclaration.Modifiers, eventDeclaration);
				result.Name = Identifier.Create(evt.NameToken.Annotations, evt.Name);
				result.ReturnType = (AstType)eventDeclaration.ReturnType.AcceptVisitor(this, data);

				CreateImplementsClausesForEvent(eventDeclaration, result);
			}

			members.Pop();

			return EndNode(eventDeclaration, result);
		}

		public AstNode VisitCustomEventDeclaration(CS.CustomEventDeclaration customEventDeclaration, object data)
		{
			var result = new EventDeclaration();

			members.Push(new MemberInfo());

			ConvertNodes(customEventDeclaration.Attributes, result.Attributes);
			if (IsOwnerAModule(customEventDeclaration))
				customEventDeclaration.Modifiers &= ~CS.Modifiers.Static;
			result.Modifiers = ConvertModifiers(customEventDeclaration.Modifiers, customEventDeclaration);
			result.IsCustom = true;
			result.Name = Identifier.Create(customEventDeclaration.NameToken.Annotations, customEventDeclaration.Name);
			result.ReturnType = (AstType)customEventDeclaration.ReturnType.AcceptVisitor(this, data);
			CreateImplementsClausesForEvent(customEventDeclaration, result);
			result.AddHandlerBlock = (Accessor)customEventDeclaration.AddAccessor.AcceptVisitor(this, data);
			result.RemoveHandlerBlock = (Accessor)customEventDeclaration.RemoveAccessor.AcceptVisitor(this, data);

			members.Pop();

			return EndNode(customEventDeclaration, result);
		}

		public AstNode VisitFieldDeclaration(CS.FieldDeclaration fieldDeclaration, object data)
		{
			var decl = new FieldDeclaration();

			members.Push(new MemberInfo());

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

			ConvertNodes(indexerDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), decl.Attributes);
			decl.Getter = (Accessor)indexerDeclaration.Getter.AcceptVisitor(this, data);
			if (IsOwnerAModule(indexerDeclaration))
				indexerDeclaration.Modifiers &= ~CS.Modifiers.Static;
			decl.Modifiers = ConvertModifiers(indexerDeclaration.Modifiers, indexerDeclaration);
			decl.Name = Identifier.Create(BoxedTextColor.InstanceProperty, indexerDeclaration.Name);
			ConvertNodes(indexerDeclaration.Parameters, decl.Parameters);
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

		public AstNode VisitMethodDeclaration(CS.MethodDeclaration methodDeclaration, object data)
		{
			CS.Attribute attr;

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
				result.Name = Identifier.Create(methodDeclaration.NameToken.Annotations, methodDeclaration.Name);
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
				var result = new MethodDeclaration();

				members.Push(new MemberInfo());

				ConvertNodes(methodDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
				ConvertNodes(methodDeclaration.ModifierTokens, result.ModifierTokens);
				result.Name = Identifier.Create(methodDeclaration.NameToken.Annotations, methodDeclaration.Name);
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

		static bool IsSameType(List<ImplementsResult> overrides, ITypeDefOrRef type, UTF8String memberName) {
			if (overrides == null)
				return false;
			foreach (var ovr in overrides) {
				if (ovr.OriginalName != memberName)
					continue;
				var comparer = new SigComparer(0);
				if (comparer.Equals(type, ovr.Type))
					return true;
			}
			return false;
		}

		IEnumerable<ImplementsResult> GetMethods(MethodDef method) {
			List<ImplementsResult> overrides = null;
			foreach (var o in method.Overrides) {
				var m = o.MethodDeclaration.ResolveMethodDef();
				if (m == null)
					continue;
				if (!m.DeclaringType.IsInterface)
					continue;
				var res = new ImplementsResult(o.MethodDeclaration, o.MethodDeclaration);
				if (overrides == null)
					overrides = new List<ImplementsResult>();
				overrides.Add(res);
				yield return res;
			}
			var comparer = new SigComparer(0, method.Module);
			foreach (var ii in method.DeclaringType.Interfaces) {
				var iface = ii.Interface.Resolve();
				if (iface == null)
					continue;
				foreach (var ifaceMethod in iface.Methods) {
					if (ifaceMethod.Name != method.Name)
						continue;
					var ifaceMethodSig = GetMethodBaseSig(ii.Interface, ifaceMethod.MethodSig);
					if (!comparer.Equals(ifaceMethodSig, method.MethodSig))
						continue;
					if (IsSameType(overrides, ii.Interface, ifaceMethod.Name))
						continue;
					yield return new ImplementsResult(ii.Interface, ifaceMethod.Name, ifaceMethod);
				}
			}
		}

		static IEnumerable<ImplementsResult> GetProperties(PropertyDef prop) {
			List<ImplementsResult> overrides = null;
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
					if (overrides == null)
						overrides = new List<ImplementsResult>();
					overrides.Add(res);
					yield return res;
				}
			}
			var comparer = new SigComparer(0, prop.Module);
			foreach (var ii in prop.DeclaringType.Interfaces) {
				var iface = ii.Interface.Resolve();
				if (iface == null)
					continue;
				foreach (var ifaceProp in iface.Properties) {
					if (ifaceProp.Name != prop.Name)
						continue;
					var ifaceMethodSig = GetMethodBaseSig(ii.Interface, ifaceProp.PropertySig);
					// mcs doesn't set HasThis if it's an instance property
					if (ifaceMethodSig.HasThis != prop.PropertySig.HasThis) {
						ifaceMethodSig = new MethodSig(ifaceMethodSig.CallingConvention, ifaceMethodSig.GenParamCount, ifaceMethodSig.RetType, ifaceMethodSig.Params, ifaceMethodSig.ParamsAfterSentinel);
						ifaceMethodSig.HasThis = prop.PropertySig.HasThis;
					}
					if (!comparer.Equals(ifaceMethodSig, prop.PropertySig))
						continue;
					if (IsSameType(overrides, ii.Interface, ifaceProp.Name))
						continue;
					yield return new ImplementsResult(ii.Interface, ifaceProp.Name, ifaceProp);
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

		static IEnumerable<ImplementsResult> GetEvents(EventDef evt) {
			List<ImplementsResult> overrides = null;
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
					if (overrides == null)
						overrides = new List<ImplementsResult>();
					overrides.Add(res);
					yield return res;
				}
			}
			var comparer = new SigComparer(0, evt.Module);
			foreach (var ii in evt.DeclaringType.Interfaces) {
				var iface = ii.Interface.Resolve();
				if (iface == null)
					continue;
				foreach (var ifaceEvent in iface.Events) {
					if (ifaceEvent.Name != evt.Name)
						continue;
					var eventType = GetReplacedType(ii.Interface, ifaceEvent.EventType);
					if (!comparer.Equals(eventType, evt.EventType))
						continue;
					if (IsSameType(overrides, ii.Interface, ifaceEvent.Name))
						continue;
					yield return new ImplementsResult(ii.Interface, ifaceEvent.Name, ifaceEvent);
				}
			}
		}

		AstType CreateType(ITypeDefOrRef type) {
			// The ILSpy 3.2 engine works on cecil, so bridge the dnlib type reference to
			// cecil and reuse CSharpDecompiler.ConvertType (which understands generics,
			// nested types, arrays, pointers, etc.). The resulting C# AstType is then
			// converted to VB. If the bridge fails, fall back to a name-based AstType.
			var cecilType = DnlibCecilBridge.TryGetTypeReference(module, type);
			if (cecilType is null)
				return AstType.FromName(type.FullName, BoxedTextColor.Type);
			var csAstType = ICSharpCode.Decompiler.CSharp.CSharpDecompiler.ConvertType(cecilType);
			return (AstType)csAstType.AcceptVisitor(this, null);
		}

		void CreateImplementsClausesForMethod(CS.MethodDeclaration orig, MethodDeclaration result) {
			var md = orig.Annotation<MethodDef>();
			if (md == null || !md.IsVirtual)
				return;
			var type = md.DeclaringType;
			if (type.IsInterface)
				return;

			foreach (var t in GetMethods(md))
				result.ImplementsClause.Add(InterfaceMemberSpecifier.CreateWithData(CreateType(t.Type), t.OriginalName, t.Reference));
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
			param.Name = Identifier.Create(parameterDeclaration.NameToken.Annotations, parameterDeclaration.Name);
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

			if (IsOwnerAModule(propertyDeclaration))
				propertyDeclaration.Modifiers &= ~CS.Modifiers.Static;

			ConvertNodes(propertyDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), decl.Attributes);
			decl.Getter = (Accessor)propertyDeclaration.Getter.AcceptVisitor(this, data);
			decl.Modifiers = ConvertModifiers(propertyDeclaration.Modifiers, propertyDeclaration);
			decl.Name = Identifier.Create(propertyDeclaration.NameToken.Annotations, propertyDeclaration.Name);
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
				v.Identifiers.Add(new VariableIdentifier() {
					Name = Identifier.Create(propertyDeclaration.NameToken.Annotations, propertyDeclaration.Name)
				});
				v.Initializer = (Expression)propertyDeclaration.Initializer.AcceptVisitor(this, data);
				decl.Variables.Add(v);
			}
			ConvertNodes(provider.GetParametersForProperty(propertyDeclaration), decl.Parameters);

			return EndNode(propertyDeclaration, decl);
		}

		public AstNode VisitVariableInitializer(CS.VariableInitializer variableInitializer, object data)
		{
			var decl = new VariableDeclaratorWithTypeAndInitializer();

			// look for type in parent
			decl.Type = (AstType)variableInitializer.Parent
				.GetChildByRole(CS.Roles.Type)
				.AcceptVisitor(this, data);
			decl.Identifiers.Add(new VariableIdentifier() { Name = Identifier.Create(variableInitializer.NameToken.Annotations, variableInitializer.Name) });
			decl.Initializer = (Expression)variableInitializer.Initializer.AcceptVisitor(this, data);

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
			var type = new SimpleType(simpleType.IdentifierToken.Annotations, simpleType.Identifier);
			ConvertNodes(simpleType.TypeArguments, type.TypeArguments);

			return EndNode(simpleType, type);
		}

		public AstNode VisitMemberType(CS.MemberType memberType, object data)
		{
			AstType target = null;

			if (memberType.Target is CS.SimpleType && ((CS.SimpleType)(memberType.Target)).Identifier.Equals("global", StringComparison.Ordinal))
				target = new PrimitiveType("Global");
			else
				target = (AstType)memberType.Target.AcceptVisitor(this, data);

			var type = new QualifiedType(target, Identifier.Create(memberType.MemberNameToken.Annotations, memberType.MemberName));
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

			return EndNode(primitiveType, new PrimitiveType(typeName));
		}

		public AstNode VisitComment (CS.Comment comment, object data)
		{
			if (!comment.IsDocumentation)
				return null;

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
				NameToken = Identifier.Create(typeParameterDeclaration.NameToken.Annotations, typeParameterDeclaration.Name)
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

			return EndNode(identifier, ident);
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
			string name = outVarDeclarationExpression.Variable.Name;
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

		public AstNode VisitTupleExpression(CS.TupleExpression tupleExpression, object data) =>
			throw new NotSupportedException("VB output for tuple expressions is not implemented");

		public AstNode VisitTupleType(CS.TupleAstType tupleType, object data) =>
			throw new NotSupportedException("VB output for tuple types is not implemented");

		public AstNode VisitTupleTypeElement(CS.TupleTypeElement tupleTypeElement, object data) =>
			throw new NotSupportedException("VB output for tuple elements is not implemented");
	}
}
