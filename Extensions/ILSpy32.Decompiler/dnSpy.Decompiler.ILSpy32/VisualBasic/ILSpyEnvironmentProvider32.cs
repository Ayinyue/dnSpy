// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// Copyright (c) 2014-2019 de4dot@gmail.com
//
// ILSpyEnvironmentProvider adapted for the ILSpy 3.2 engine. The 3.2 engine
// annotates its C# AST nodes with Mono.Cecil references (TypeReference,
// MethodReference, PropertyReference, ...), so this provider reads those
// annotations directly.

using System;
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
			if (annotation is null)
				return false;
			var def = annotation.Resolve();
			return def is not null && !def.IsGetter && !def.IsSetter && !def.IsAddOn && !def.IsRemoveOn;
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
