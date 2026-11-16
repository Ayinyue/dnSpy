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
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Disassembler;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// Three-state value, ported from the 2.3 engine's <c>DisassemblerHelpers</c>.
	/// </summary>
	public enum ThreeState : byte {
		Unknown,
		No,
		Yes,
	}

	/// <summary>
	/// dnlib object → IL 风格文本的写入器。逐字移植 2.3 引擎
	/// (<c>ICSharpCode.Decompiler.Disassembler.DisassemblerHelpers</c>) 的行为，供
	/// <see cref="ILDecompilerUtils32"/>、<c>SimpleILPrinter32</c> 以及后续
	/// <c>ILDecompiler32</c>/<c>ILAstDecompiler32</c>/<c>ILWithCSharpDecompiler32</c> 的
	/// <c>WriteToolTip</c>/<c>TypeToString</c> 使用。
	///
	/// 与 2.3 的差异仅在依赖的等价物：<see cref="TextWriterTokenWriter"/> 用 3.2 引擎的
	/// <c>ICSharpCode.Decompiler.CSharp.OutputVisitor.TextWriterTokenWriter</c>；
	/// <c>PrimitiveTypeName</c>/<c>GetParameters</c>/<c>GetScopeName</c> 等 2.3 fork 的
	/// 内部扩展在此就地实现（dnlib 4.5.0 已内置 <c>FullNameFactory</c>、
	/// <c>IType.IsValueType</c>、<c>TypeSig.ToTypeDefOrRef</c>）。
	/// </summary>
	public static class DnlibILText32 {
		const int MAX_CONVERTTYPE_DEPTH = 50;

		static readonly char[] _validNonLetterIdentifierCharacter = { '_', '$', '@', '?', '`', '.' };

		/// <summary>
		/// Writes a method (its signature plus declaring type/name) to <paramref name="writer"/>.
		/// 2.3 的 <c>WriteMethodTo</c> 调用 <c>writer.Write(sb, null, method)</c>；3.2 侧没有该
		/// 重载，改为直接调用 <see cref="Write(IDecompilerOutput, StringBuilder, MethodSig, IMethod)"/>。
		/// </summary>
		public static void WriteTo(this IMethod method, IDecompilerOutput writer, StringBuilder sb) =>
			Write(writer, sb, method.MethodSig, method);

		public static void Write(IDecompilerOutput writer, StringBuilder sb, MethodSig sig, IMethod? method = null) {
			if (sig == null && method != null)
				sig = method.MethodSig;
			if (sig == null)
				return;
			if (sig.ExplicitThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("explicit", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (sig.HasThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			if (sig.CallingConvention == CallingConvention.VarArg) {
				writer.Write("vararg", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			sig.RetType.WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			writer.Write(" ", BoxedTextColor.Text);
			if (method != null) {
				if (method.DeclaringType != null) {
					method.DeclaringType.WriteTo(writer, sb, ILNameSyntax.TypeName);
					writer.Write("::", BoxedTextColor.Operator);
				}
				if (method is MethodDef md && md.IsCompilerControlled) {
					writer.Write(Escape(method.Name + "$PST" + method.MDToken.ToInt32().ToString("X8")), method, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(method));
				}
				else {
					writer.Write(Escape(method.Name), method, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(method));
				}
			}
			if (method is MethodSpec gim && gim.GenericInstMethodSig != null) {
				var bh1 = BracePairHelper.Create(writer, "<", CodeBracesRangeFlags.AngleBrackets);
				for (int i = 0; i < gim.GenericInstMethodSig.GenericArguments.Count; i++) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}
					gim.GenericInstMethodSig.GenericArguments[i].WriteTo(writer, sb);
				}
				bh1.Write(">");
			}
			var bh2 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
			var parameters = GetParameters(sig);
			for (int i = 0; i < parameters.Count; ++i) {
				if (i > 0) {
					writer.Write(",", BoxedTextColor.Punctuation);
					writer.Write(" ", BoxedTextColor.Text);
				}
				parameters[i].WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			}
			bh2.Write(")");
		}

		public static void WriteTo(this MethodSig sig, IDecompilerOutput writer, StringBuilder sb) {
			if (sig.ExplicitThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("explicit", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (sig.HasThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			sig.RetType.WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			writer.Write(" ", BoxedTextColor.Text);
			var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
			var parameters = GetParameters(sig);
			for (int i = 0; i < parameters.Count; ++i) {
				if (i > 0) {
					writer.Write(",", BoxedTextColor.Punctuation);
					writer.Write(" ", BoxedTextColor.Text);
				}
				parameters[i].WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			}
			bh1.Write(")");
		}

		public static void WriteTo(this IField field, IDecompilerOutput writer, StringBuilder sb) {
			if (field == null || field.FieldSig == null)
				return;
			field.FieldSig.Type.WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			writer.Write(" ", BoxedTextColor.Text);
			field.DeclaringType.WriteTo(writer, sb, ILNameSyntax.TypeName);
			writer.Write("::", BoxedTextColor.Operator);
			writer.Write(Escape(field.Name), field, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(field));
		}

		public static void WriteTo(this TypeSig type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax = ILNameSyntax.Signature) => type.WriteTo(writer, sb, syntax, 0);

		public static void WriteTo(this TypeSig type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax, int depth) {
			if (depth++ > MAX_CONVERTTYPE_DEPTH)
				return;
			ILNameSyntax syntaxForElementTypes = syntax == ILNameSyntax.SignatureNoNamedTypeParameters ? syntax : ILNameSyntax.Signature;
			if (type is PinnedSig sig) {
				sig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("pinned", BoxedTextColor.Keyword);
			}
			else if (type is ArraySig arraySig) {
				arraySig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				var bh1 = BracePairHelper.Create(writer, "[", CodeBracesRangeFlags.SquareBrackets);
				for (int i = 0; i < arraySig.Rank; i++) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}

					if (i < arraySig.LowerBounds.Count || i < arraySig.Sizes.Count) {
						int lower = i < arraySig.LowerBounds.Count ? arraySig.LowerBounds[i] : 0;
						writer.Write(lower.ToString(), BoxedTextColor.Number);
						writer.Write("...", BoxedTextColor.Operator);
						if (i < arraySig.Sizes.Count)
							writer.Write((lower + (int)arraySig.Sizes[i] - 1).ToString(), BoxedTextColor.Number);
					}
				}
				bh1.Write("]");
			}
			else if (type is SZArraySig at) {
				at.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				var bh1 = BracePairHelper.Create(writer, "[", CodeBracesRangeFlags.SquareBrackets);
				bh1.Write("]");
			}
			else if (type is GenericSig genericSig) {
				if (genericSig.IsMethodVar)
					writer.Write("!!", BoxedTextColor.Operator);
				else
					writer.Write("!", BoxedTextColor.Operator);
				sb.Clear();
				string typeName = FullNameFactory.Name(genericSig, false, sb);
				if (string.IsNullOrEmpty(typeName) || typeName[0] == '!' || syntax == ILNameSyntax.SignatureNoNamedTypeParameters)
					writer.Write(genericSig.Number.ToString(), BoxedTextColor.Number);
				else
					writer.Write(Escape(typeName), genericSig.GenericParam, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
			}
			else if (type is ByRefSig refSig) {
				refSig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				writer.Write("&", BoxedTextColor.Operator);
			}
			else if (type is PtrSig ptrSig) {
				ptrSig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				writer.Write("*", BoxedTextColor.Operator);
			}
			else if (type is GenericInstSig instSig) {
				instSig.GenericType.WriteTo(writer, sb, syntaxForElementTypes, depth);
				var bh1 = BracePairHelper.Create(writer, "<", CodeBracesRangeFlags.AngleBrackets);
				var arguments = instSig.GenericArguments;
				for (int i = 0; i < arguments.Count; i++) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}
					arguments[i].WriteTo(writer, sb, syntaxForElementTypes, depth);
				}
				bh1.Write(">");
			}
			else if (type is CModOptSig optSig) {
				optSig.Next.WriteTo(writer, sb, syntax, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("modopt", BoxedTextColor.Keyword);
				var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
				optSig.Modifier.WriteTo(writer, sb, ILNameSyntax.TypeName, ThreeState.Unknown, depth);
				bh1.Write(")");
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (type is CModReqdSig reqdSig) {
				reqdSig.Next.WriteTo(writer, sb, syntax, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("modreq", BoxedTextColor.Keyword);
				var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
				reqdSig.Modifier.WriteTo(writer, sb, ILNameSyntax.TypeName, ThreeState.Unknown, depth);
				bh1.Write(")");
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (type is SentinelSig)
				writer.Write("...", BoxedTextColor.Operator);
			else if (type is FnPtrSig fnPtrSig) {
				writer.Write("method", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
				if (fnPtrSig.MethodSig.ExplicitThis) {
					writer.Write("instance", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
					writer.Write("explicit", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}
				else if (fnPtrSig.MethodSig.HasThis) {
					writer.Write("instance", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}
				var callingConvention = fnPtrSig.MethodSig.CallingConvention & CallingConvention.Mask;
				if (callingConvention != CallingConvention.Default) {
					var callConv = callingConvention switch {
						CallingConvention.C => "unmanaged cdecl",
						CallingConvention.StdCall => "unmanaged stdcall",
						CallingConvention.ThisCall => "unmanaged thiscall",
						CallingConvention.FastCall => "unmanaged fastcall",
						CallingConvention.VarArg => "vararg",
						CallingConvention.Unmanaged => "unmanaged",
						_ => callingConvention.ToString().ToLowerInvariant(),
					};
					writer.Write(callConv, BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}
				fnPtrSig.MethodSig.RetType.WriteTo(writer, sb, syntax, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("*", BoxedTextColor.Operator);
				var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
				var parameters = GetParameters(fnPtrSig.MethodSig);
				for (int i = 0; i < parameters.Count; ++i) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}
					parameters[i].WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
				}
				bh1.Write(")");
			}
			else if (type is TypeDefOrRefSig tdrs) {
				ThreeState isVT;
				if (tdrs is ClassSig)
					isVT = ThreeState.No;
				else if (tdrs is ValueTypeSig)
					isVT = ThreeState.Yes;
				else if (tdrs is CorLibTypeSig corLibTypeSig)
					isVT = IsValueType(corLibTypeSig);
				else
					isVT = ThreeState.Unknown;
				WriteTo(tdrs.TypeDefOrRef, writer, sb, syntax, isVT, depth);
			}
		}

		public static void WriteTo(this ITypeDefOrRef type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax = ILNameSyntax.Signature) => type.WriteTo(writer, sb, syntax, ThreeState.Unknown, 0);

		public static void WriteTo(this ITypeDefOrRef type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax, ThreeState isValueType, int depth = 0) {
			if (depth++ > MAX_CONVERTTYPE_DEPTH || type == null)
				return;
			if (type is TypeSpec ts) {
				WriteTo(ts.TypeSig, writer, sb, syntax, depth);
				return;
			}
			sb.Clear();
			string typeFullName = FullNameFactory.FullName(type, false, null, sb);
			string typeName = type.Name.String;
			TypeSig typeSig = null;
			string name = type.DefinitionAssembly.IsCorLib() ? PrimitiveTypeName(typeFullName, type.Module, out typeSig) : null;
			if (syntax == ILNameSyntax.ShortTypeName) {
				if (name != null)
					WriteKeyword(writer, name, typeSig.ToTypeDefOrRef());
				else
					writer.Write(Escape(typeName), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
			}
			else if ((syntax == ILNameSyntax.Signature || syntax == ILNameSyntax.SignatureNoNamedTypeParameters) && name != null) {
				WriteKeyword(writer, name, typeSig.ToTypeDefOrRef());
			}
			else {
				if (syntax == ILNameSyntax.Signature || syntax == ILNameSyntax.SignatureNoNamedTypeParameters) {
					bool isVT;
					if (isValueType != ThreeState.Unknown)
						isVT = isValueType == ThreeState.Yes;
					else
						isVT = IsValueType(type);
					writer.Write(isVT ? "valuetype" : "class", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}

				if (type.DeclaringType != null) {
					type.DeclaringType.WriteTo(writer, sb, ILNameSyntax.TypeName, ThreeState.Unknown, depth);
					writer.Write("/", BoxedTextColor.Operator);
					WriteNamespace(writer, type.Namespace, type.DefinitionAssembly, sb);
					if (!string.IsNullOrEmpty(type.Namespace))
						writer.Write(".", BoxedTextColor.Operator);
					writer.Write(Escape(typeName), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
				}
				else {
					if (!(type is TypeDef) && type.Scope != null) {
						var bh1 = BracePairHelper.Create(writer, "[", CodeBracesRangeFlags.SquareBrackets);
						writer.Write(Escape(GetScopeName(type.Scope)), type.Scope, DecompilerReferenceFlags.None, BoxedTextColor.ILModule);
						bh1.Write("]");
					}
					if (MustEscape(typeFullName))
						writer.Write(Escape(typeFullName), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
					else {
						WriteNamespace(writer, type.Namespace, type.DefinitionAssembly, sb);
						if (!string.IsNullOrEmpty(type.Namespace))
							writer.Write(".", BoxedTextColor.Operator);
						writer.Write(IdentifierEscaper.Escape(type.Name), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
					}
				}
			}
		}

		static void WriteNamespace(IDecompilerOutput writer, string ns, IAssembly nsAsm, StringBuilder sb) {
			sb.Clear();
			var parts = ns.Split('.');
			for (int i = 0; i < parts.Length; i++) {
				if (i > 0) {
					sb.Append('.');
					writer.Write(".", BoxedTextColor.Operator);
				}
				var nsPart = parts[i];
				sb.Append(nsPart);
				if (!string.IsNullOrEmpty(nsPart)) {
					var nsRef = new NamespaceReference(nsAsm, sb.ToString());
					writer.Write(IdentifierEscaper.Escape(nsPart), nsRef, DecompilerReferenceFlags.None, BoxedTextColor.Namespace);
				}
			}
		}

		static void WriteKeyword(IDecompilerOutput writer, string name, ITypeDefOrRef tdr) {
			var parts = name.Split(' ');
			for (int i = 0; i < parts.Length; i++) {
				if (i > 0)
					writer.Write(" ", BoxedTextColor.Text);
				if (tdr != null)
					writer.Write(parts[i], tdr, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
				else
					writer.Write(parts[i], BoxedTextColor.Keyword);
			}
		}

		static bool IsValidIdentifierCharacter(char c) =>
			char.IsLetterOrDigit(c) || _validNonLetterIdentifierCharacter.Contains(c);

		static bool IsValidIdentifier(string identifier) {
			if (string.IsNullOrEmpty(identifier))
				return false;

			char c = identifier[0];
			if (char.IsDigit(c))
				return false;

			// As a special case, .ctor and .cctor are valid despite starting with a dot
			if (c == '.')
				return identifier == ".ctor" || identifier == ".cctor";

			if (identifier.Contains(".."))
				return false;

			if (ilKeywords.Contains(identifier))
				return false;

			for (var i = 0; i < identifier.Length; i++) {
				if (!IsValidIdentifierCharacter(identifier[i]))
					return false;
			}
			return true;
		}

		static readonly HashSet<string> ilKeywords = BuildKeywordList(
			"abstract", "algorithm", "alignment", "ansi", "any", "arglist",
			"array", "as", "assembly", "assert", "at", "auto", "autochar", "beforefieldinit",
			"blob", "blob_object", "bool", "brnull", "brnull.s", "brzero", "brzero.s", "bstr",
			"bytearray", "byvalstr", "callmostderived", "carray", "catch", "cdecl", "cf",
			"char", "cil", "class", "clsid", "const", "currency", "custom", "date", "decimal",
			"default", "demand", "deny", "endmac", "enum", "error", "explicit", "extends", "extern",
			"false", "famandassem", "family", "famorassem", "fastcall", "fault", "field", "filetime",
			"filter", "final", "finally", "fixed", "float", "float32", "float64", "forwardref",
			"fromunmanaged", "handler", "hidebysig", "hresult", "idispatch", "il", "illegal",
			"implements", "implicitcom", "implicitres", "import", "in", "inheritcheck", "init",
			"initonly", "instance", "int", "int16", "int32", "int64", "int8", "interface", "internalcall",
			"iunknown", "lasterr", "lcid", "linkcheck", "literal", "localloc", "lpstr", "lpstruct", "lptstr",
			"lpvoid", "lpwstr", "managed", "marshal", "method", "modopt", "modreq", "native", "nested",
			"newslot", "noappdomain", "noinlining", "nomachine", "nomangle", "nometadata", "noncasdemand",
			"noncasinheritance", "noncaslinkdemand", "noprocess", "not", "not_in_gc_heap", "notremotable",
			"notserialized", "null", "nullref", "object", "objectref", "opt", "optil", "out",
			"permitonly", "pinned", "pinvokeimpl", "prefix1", "prefix2", "prefix3", "prefix4", "prefix5", "prefix6",
			"prefix7", "prefixref", "prejitdeny", "prejitgrant", "preservesig", "private", "privatescope", "protected",
			"public", "record", "refany", "reqmin", "reqopt", "reqrefuse", "reqsecobj", "request", "retval",
			"rtspecialname", "runtime", "safearray", "sealed", "sequential", "serializable", "special", "specialname",
			"static", "stdcall", "storage", "stored_object", "stream", "streamed_object", "string", "struct",
			"synchronized", "syschar", "sysstring", "tbstr", "thiscall", "tls", "to", "true", "typedref",
			"unicode", "unmanaged", "unmanagedexp", "unsigned", "unused", "userdefined", "value", "valuetype",
			"vararg", "variant", "vector", "virtual", "void", "wchar", "winapi", "with", "wrapper",

			// These are not listed as keywords in spec, but ILAsm treats them as such
			"property", "type", "flags", "codelabel", "callconv", "strict",
			// ILDasm uses these keywords for unsigned integers
			"uint8", "uint16", "uint32", "uint64"
		);

		static HashSet<string> BuildKeywordList(params string[] keywords) {
			HashSet<string> s = new HashSet<string>(keywords);
			foreach (var field in typeof(OpCodes).GetFields()) {
				if (field.FieldType != typeof(OpCode))
					continue;
				OpCode opCode = (OpCode)field.GetValue(null);
				if (opCode.OpCodeType != OpCodeType.Nternal)
					s.Add(opCode.Name);
			}
			return s;
		}

		public static bool MustEscape(string identifier) => !IsValidIdentifier(identifier);

		public static string Escape(string identifier) {
			if (MustEscape(identifier)) {
				// The ECMA specification says that ' inside SQString should be ecaped using an octal escape sequence,
				// but we follow Microsoft's ILDasm and use \'.
				return "'" + IdentifierEscaper.Truncate(TextWriterTokenWriter.ConvertString(identifier).Replace("'", "\\'")) + "'";
			}
			else {
				return IdentifierEscaper.Truncate(identifier)!;
			}
		}

		public static string PrimitiveTypeName(string fullName, ModuleDef module, out TypeSig typeSig) {
			var corLibTypes = module?.CorLibTypes;
			typeSig = null;
			switch (fullName) {
				case "System.SByte":
					if (corLibTypes != null)
						typeSig = corLibTypes.SByte;
					return "int8";
				case "System.Int16":
					if (corLibTypes != null)
						typeSig = corLibTypes.Int16;
					return "int16";
				case "System.Int32":
					if (corLibTypes != null)
						typeSig = corLibTypes.Int32;
					return "int32";
				case "System.Int64":
					if (corLibTypes != null)
						typeSig = corLibTypes.Int64;
					return "int64";
				case "System.Byte":
					if (corLibTypes != null)
						typeSig = corLibTypes.Byte;
					return "uint8";
				case "System.UInt16":
					if (corLibTypes != null)
						typeSig = corLibTypes.UInt16;
					return "uint16";
				case "System.UInt32":
					if (corLibTypes != null)
						typeSig = corLibTypes.UInt32;
					return "uint32";
				case "System.UInt64":
					if (corLibTypes != null)
						typeSig = corLibTypes.UInt64;
					return "uint64";
				case "System.Single":
					if (corLibTypes != null)
						typeSig = corLibTypes.Single;
					return "float32";
				case "System.Double":
					if (corLibTypes != null)
						typeSig = corLibTypes.Double;
					return "float64";
				case "System.Void":
					if (corLibTypes != null)
						typeSig = corLibTypes.Void;
					return "void";
				case "System.Boolean":
					if (corLibTypes != null)
						typeSig = corLibTypes.Boolean;
					return "bool";
				case "System.String":
					if (corLibTypes != null)
						typeSig = corLibTypes.String;
					return "string";
				case "System.Char":
					if (corLibTypes != null)
						typeSig = corLibTypes.Char;
					return "char";
				case "System.Object":
					if (corLibTypes != null)
						typeSig = corLibTypes.Object;
					return "object";
				case "System.IntPtr":
					if (corLibTypes != null)
						typeSig = corLibTypes.IntPtr;
					return "native int";
				case "System.UIntPtr":
					if (corLibTypes != null)
						typeSig = corLibTypes.UIntPtr;
					return "native unsigned int";
				case "System.TypedReference":
					if (corLibTypes != null)
						typeSig = corLibTypes.TypedReference;
					return "typedref";
				default:
					return null;
			}
		}

		static ThreeState IsValueType(CorLibTypeSig corlib) {
			switch (corlib.ElementType) {
				case ElementType.Void:
				case ElementType.Boolean:
				case ElementType.Char:
				case ElementType.I1:
				case ElementType.U1:
				case ElementType.I2:
				case ElementType.U2:
				case ElementType.I4:
				case ElementType.U4:
				case ElementType.I8:
				case ElementType.U8:
				case ElementType.R4:
				case ElementType.R8:
				case ElementType.TypedByRef:
				case ElementType.I:
				case ElementType.U:
				case ElementType.R:
					return ThreeState.Yes;
				case ElementType.String:
				case ElementType.Object:
					return ThreeState.No;
				default:
					return ThreeState.Unknown;
			}
		}

		static bool IsValueType(ITypeDefOrRef tdr) {
			if (tdr == null)
				return false;
			var ts = tdr as TypeSpec;
			if (ts != null)
				return IsValueType(ts.TypeSig);
			return tdr.IsValueType;
		}

		static bool IsValueType(TypeSig ts) => ts?.IsValueType ?? false;

		// 2.3 fork 的 MethodBaseSig.GetParameters 扩展（dnlib 4.5.0 未内置）。
		static IList<TypeSig> GetParameters(MethodBaseSig methodSig) {
			if (methodSig == null)
				return new List<TypeSig>();
			if (methodSig.ParamsAfterSentinel != null)
				return methodSig.Params
					.Concat(new TypeSig[] { new SentinelSig() })
					.Concat(methodSig.ParamsAfterSentinel)
					.ToList();
			else
				return methodSig.Params;
		}

		// 2.3 fork 的 IScope.GetScopeName 扩展。
		static string GetScopeName(IScope scope) {
			if (scope == null)
				return string.Empty;
			if (scope is IFullName fullName)
				return fullName.Name;
			return scope.ScopeName;
		}

		/// <summary>
		/// 2.3 版 BracePairHelper（原在 <c>ReflectionDisassembler.cs</c>）。
		/// </summary>
		readonly struct BracePairHelper {
			readonly IDecompilerOutput output;
			readonly CodeBracesRangeFlags flags;
			readonly int leftStart, leftEnd;

			BracePairHelper(IDecompilerOutput output, int leftStart, int leftEnd, CodeBracesRangeFlags flags) {
				this.output = output;
				this.leftStart = leftStart;
				this.leftEnd = leftEnd;
				this.flags = flags;
			}

			public static BracePairHelper Create(IDecompilerOutput output, string s, CodeBracesRangeFlags flags) {
				int start = output.NextPosition;
				output.Write(s, BoxedTextColor.Punctuation);
				return new BracePairHelper(output, start, output.NextPosition, flags);
			}

			public void Write(string s) {
				int start = output.NextPosition;
				output.Write(s, BoxedTextColor.Punctuation);
				output.AddBracePair(new TextSpan(leftStart, leftEnd - leftStart), new TextSpan(start, output.NextPosition - start), flags);
			}
		}
	}
}
