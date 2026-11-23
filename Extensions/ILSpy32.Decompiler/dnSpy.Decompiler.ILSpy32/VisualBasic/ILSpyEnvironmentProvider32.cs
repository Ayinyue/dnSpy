// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// Copyright (c) 2014-2019 de4dot@gmail.com
//
// ILSpyEnvironmentProvider adapted for the ILSpy 3.2 engine. The 3.2 engine
// annotates its C# AST nodes with Mono.Cecil references (TypeReference,
// MethodReference, PropertyReference, ...), so this provider reads those
// annotations directly.

using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.VB.Ast;
using Mono.Cecil;
using CS = ICSharpCode.Decompiler.CSharp.Syntax;

namespace dnSpy.Decompiler.ILSpy32.VisualBasic {
	sealed class ILSpyEnvironmentProvider32 : IEnvironmentProvider {
		public string RootNamespace => string.Empty;

		public string GetTypeNameForAttribute(CS.Attribute attribute) {
			var mr = attribute.Type.Annotations.OfType<MemberReference>().FirstOrDefault();
			return mr is null ? string.Empty : mr.FullName;
		}

		public TypeKind GetTypeKindForAstType(CS.AstType type) {
			var annotation = type.Annotation<TypeReference>();
			if (annotation is null)
				return TypeKind.Unknown;
			var definition = annotation.Resolve();
			if (definition is null)
				return TypeKind.Unknown;
			if (definition.IsClass)
				return TypeKind.Class;
			if (definition.IsInterface)
				return TypeKind.Interface;
			if (definition.IsEnum)
				return TypeKind.Enum;
			if (definition.IsValueType)
				return TypeKind.Struct;
			return TypeKind.Unknown;
		}

		public TypeCode ResolveExpression(CS.Expression expression) {
			// The ILSpy 3.2 engine does not annotate expressions with TypeInformation.
			// Returning Object keeps the VB converter on the safe path.
			return TypeCode.Object;
		}

		public bool? IsReferenceType(CS.Expression expression) {
			if (expression is CS.NullReferenceExpression)
				return true;
			// No TypeInformation annotations in 3.2; return unknown.
			return null;
		}

		public IType ResolveType(AstType type, TypeDeclaration? entity = null) =>
			SpecialType.UnknownType;

		public bool IsMethodGroup(CS.Expression expression) {
			var annotation = expression.Annotation<MethodReference>();
			if (annotation is not null) {
				var def = annotation.Resolve();
				return def is not null && !def.IsGetter && !def.IsSetter && !def.IsAddOn && !def.IsRemoveOn;
			}
			// No annotations in 3.2. Fall back to the method names the binder recorded: a bare
			// reference to a method must become AddressOf in VB, a field/property must not.
			if (expression.Parent is CS.InvocationExpression invocation && invocation.Target == expression)
				return false;
			// The resolver's results do exist on the 3.2 tree (unlike the dnlib member
			// annotations): a method reference used as a delegate value carries a
			// MethodGroupResolveResult. This covers static methods on external types
			// ('New Func(Of String, Boolean)(File.Exists)'), which the per-type method-name
			// table below cannot see because it only knows the enclosing type's own members.
			if (expression.Annotation<ICSharpCode.Decompiler.CSharp.Resolver.MethodGroupResolveResult>() is not null)
				return true;
			var tree = expression.Ancestors.OfType<CS.SyntaxTree>().FirstOrDefault();
			var methodNames = tree?.Annotation<Dictionary<string, HashSet<string>>>();
			if (methodNames is null)
				return false;
			switch (expression) {
			case CS.IdentifierExpression identifier:
				var owner = GetEnclosingTypeName(expression);
				return owner is not null && methodNames.TryGetValue(owner, out var ownNames) && ownNames.Contains(identifier.Identifier);
			case CS.MemberReferenceExpression member:
				var name = GetTypeNameText(member.Target);
				return name is not null && methodNames.TryGetValue(name, out var names) && names.Contains(member.MemberName);
			default:
				return false;
			}
		}

		static string GetEnclosingTypeName(CS.AstNode node) {
			for (var current = node.Parent; current != null; current = current.Parent) {
				if (current is CS.TypeDeclaration type)
					return type.Name;
			}
			return null;
		}

		static string GetTypeNameText(CS.AstNode node) {
			switch (node) {
			case CS.SimpleType simple:
				return simple.Identifier;
			case CS.MemberType member:
				return member.MemberName;
			// In an expression context a type name can be a TypeReferenceExpression wrapping an AstType.
			case CS.TypeReferenceExpression typeReference:
				return GetTypeNameText(typeReference.Type);
			case CS.IdentifierExpression identifier:
				return identifier.Identifier;
				// 'this.M' / 'base.M': the type name is the enclosing type, and VB needs
				// AddressOf for such method groups (audit 8.100).
			case CS.ThisReferenceExpression _:
			case CS.BaseReferenceExpression _:
				return GetEnclosingTypeName(node);
			case CS.MemberReferenceExpression reference:
				return reference.MemberName;
			default:
				return null;
			}
		}

		public bool HasEvent(Expression expression) =>
			expression.Annotation<EventDefinition>() is not null;

		public CS.ParameterDeclaration[] GetParametersForProperty(CS.PropertyDeclaration property) {
			// ILSpy 3.2 annotates property/indexer declarations with a Mono.Cecil
			// PropertyReference. Reuse the engine's ConvertType to build C# AST
			// parameter declarations (same path as the upstream provider).
			var propInfo = property.Annotation<PropertyReference>();
			if (propInfo is null)
				return Array.Empty<CS.ParameterDeclaration>();
			return propInfo.Parameters.Select(p => new CS.ParameterDeclaration(
				ICSharpCode.Decompiler.CSharp.CSharpDecompiler.ConvertType(p.ParameterType),
				p.Name,
				GetModifiers(p)
			)).ToArray();
		}

		static CS.ParameterModifier GetModifiers(ParameterDefinition p) {
			if (p.IsOut && p.IsIn)
				return CS.ParameterModifier.Ref;
			if (p.IsOut)
				return CS.ParameterModifier.Out;
			return CS.ParameterModifier.None;
		}
	}
}
