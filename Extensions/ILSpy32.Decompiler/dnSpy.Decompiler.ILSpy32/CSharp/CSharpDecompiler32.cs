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
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy32.Common;
using dnSpy.Decompiler.ILSpy32.Settings;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.CSharp {
	/// <summary>
	/// C# decompiler backed by the ILSpy 3.2 engine (cecil-based), bridged from dnlib modules.
	/// </summary>
	sealed class CSharpDecompiler32 : DecompilerBase {
		static readonly Guid THE_GUID = new Guid("8C1F4A62-9D3B-4E77-8A51-2F6B0C9D4E13");

		readonly DecompilerSettings32 settings;
		readonly string uniqueNameUI;

		// CSharpDecompiler is not thread-safe and building its type system is costly, so
		// instances are cached per bridged module and per engine settings. The C# and VB
		// decompilers configure the engine differently (VB disables null-propagation), and
		// the engine reads its settings when it is constructed, so they cannot share one.
		static readonly ConditionalWeakTable<ModuleDefinition, Dictionary<DecompilerSettings32, ICSharpCode.Decompiler.CSharp.CSharpDecompiler>> engineCache = new ConditionalWeakTable<ModuleDefinition, Dictionary<DecompilerSettings32, ICSharpCode.Decompiler.CSharp.CSharpDecompiler>>();
		internal static readonly object engineLock = new object();

		internal static ICSharpCode.Decompiler.CSharp.CSharpDecompiler GetOrCreateEngine(ModuleDefinition cecilModule, DecompilerSettings32 settings) {
			var engines = engineCache.GetOrCreateValue(cecilModule);
			if (!engines.TryGetValue(settings, out var engine)) {
				engine = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(cecilModule, settings.EngineSettings);
				engines.Add(settings, engine);
			}
			return engine;
		}

		/// <summary>
		/// Formats an exception for the failure note written into the output.
		/// Set <c>DNSPY_ILSPY32_DIAG=1</c> to include the stack trace, which is the only
		/// way to locate failures in the VB converter: it catches everything internally.
		/// Off by default, so normal notes stay one line.
		/// </summary>
		internal static string DescribeException(Exception ex) {
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1")
				return ex.ToString().Replace("\r", "").Replace("\n", " | ");
			return ex.GetType().Name + ": " + ex.Message;
		}

		public CSharpDecompiler32(DecompilerSettings32 settings, string uniqueNameUI) {
			this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
			this.uniqueNameUI = uniqueNameUI ?? throw new ArgumentNullException(nameof(uniqueNameUI));
		}

		public override DecompilerSettingsBase Settings => settings;
		public override string ContentTypeString => ContentTypeDefinitions.CSharpILSpy;
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_CSHARP;
		public override string UniqueNameUI => uniqueNameUI;
		public override double OrderUI => DecompilerConstants.CSHARP_ILSPY_ORDERUI + 0.5;
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_CSHARP;
		public override Guid UniqueGuid => THE_GUID;
		public override string FileExtension => ".cs";
		public override string? ProjectFileExtension => ".csproj";

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(method, output, ctx);

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(property, output, ctx);

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(field, output, ctx);

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMember(ev, output, ctx);

		public override bool CanDecompile(DecompilationType decompilationType) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
			case DecompilationType.AssemblyInfo:
			case DecompilationType.TypeMethods:
				return true;
			}
			return base.CanDecompile(decompilationType);
		}

		public override void Decompile(DecompilationType decompilationType, object data) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
				DecompilePartial((DecompilePartialType)data);
				return;
			case DecompilationType.AssemblyInfo:
				DecompileAssemblyInfo((DecompileAssemblyInfo)data);
				return;
			case DecompilationType.TypeMethods:
				DecompileTypeMethods((DecompileTypeMethods)data);
				return;
			}
			base.Decompile(decompilationType, data);
		}

		/// <summary>
		/// Export-to-project partial type. The 2.3 engine filtered designer-file members out
		/// of the partial class; the 3.2 engine renders the whole type, so the code-behind
		/// file contains the full class (the designer part stays in its own excluded file).
		/// </summary>
		void DecompilePartial(DecompilePartialType info) {
			var cecilType = TryGetCecil(info.Type.Module, info.Type.MDToken.Raw) as TypeDefinition;
			if (cecilType is null) {
				WriteNote(info.Output, "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(info.Type.Module, info.Output, info.Context,
				d => d.DecompileType(new ICSharpCode.Decompiler.TypeSystem.FullTypeName(Common.TypeNameHelper.ToReflectionName(cecilType.FullName))),
				d => d.Decompile(cecilType),
				knownType: info.Type);
		}

		void DecompileAssemblyInfo(DecompileAssemblyInfo info) {
			// The engine decompiles the assembly/module attributes; attributes that MSBuild
			// would re-add are kept (KeepAllAttributes=false is not filtered - documented).
			DecompileCore(info.Module, info.Output, info.Context,
				d => d.DecompileModuleAndAssemblyAttributes());
		}

		void DecompileTypeMethods(DecompileTypeMethods info) {
			// No dnSpy consumer passes TypeMethods today; decompile the whole type.
			DecompilePartial(new DecompilePartialType(info.Output, info.Context, info.Type));
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			if (type is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var cecilType = TryGetCecil(type.Module, type.MDToken.Raw) as TypeDefinition;
			if (cecilType is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			// DecompileTypes (not DecompileType): the latter resolves the name to its TOP-LEVEL
			// type (CSharpDecompiler.DecompileType uses fullTypeName.TopLevelTypeName), so a
			// nested type node rendered its whole declaring type. DecompileTypes decompiles the
			// passed type definitions themselves.
			DecompileCore(type.Module, output, ctx,
				d => d.DecompileTypes(new[] { cecilType }),
				d => d.Decompile(cecilType),
				knownType: type);
		}

		void DecompileMember(IMDTokenProvider? member, IDecompilerOutput output, DecompilationContext ctx) {
			if (member is null || output is null || ctx is null)
				throw new ArgumentNullException();
			var module = (member as IMemberDef)?.Module;
			if (module is null) {
				WriteNote(output, "// ILSpy 3.2: member has no module.");
				return;
			}
			// Like the 2.3 engine: single-member decompiles start with the declaring type as a
			// comment line; the reference lets dnSpy jump to that type.
			if (member is IMemberDef memberDef)
				WriteCommentLineDeclaringType(output, memberDef);
			var cecilMember = TryGetCecil(module, member.MDToken.Raw) as IMemberDefinition;
			if (cecilMember is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module (no in-memory snapshot available).");
				return;
			}
			DecompileCore(module, output, ctx, d => d.Decompile(cecilMember));
		}

		static IMemberDefinition? TryGetCecil(ModuleDef module, uint rawToken) {
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null)
				return null;
			return DnlibCecilBridge.TryGetMember(cecilModule, rawToken);
		}

		void DecompileCore(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx,
				Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree> decompile,
				Func<ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree>? fallbackDecompile = null,
				TypeDef? knownType = null) {
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var cecilModule = DnlibCecilBridge.TryGetModule(module);
			if (cecilModule is null) {
				WriteNote(output, "// ILSpy 3.2: cannot bridge this module.");
				return;
			}
			try {
				lock (engineLock) {
					var engine = GetOrCreateEngine(cecilModule, settings);
					var tree = decompile(engine);
					// The engine hides compiler-generated implementation-detail types entirely
					// (<PrivateImplementationDetails> renders empty via MemberIsHidden); the 2.3
					// engine showed their members. Re-decompile through the member list, which
					// has no such filter. Note: the tree may contain using declarations and
					// namespace declarations alongside types (IntroduceUsingDeclarations inserts
					// usings with MemberRole; DoDecompileTypes wraps types in NamespaceDeclaration),
					// so the emptiness check looks for actual type/namespace content, not the
					// raw member count.
					if (fallbackDecompile != null && !tree.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>().Any()
							&& !tree.Members.OfType<ICSharpCode.Decompiler.CSharp.Syntax.NamespaceDeclaration>().Any())
						tree = fallbackDecompile(engine);
					StripCommentsInsideFieldDeclarations(tree);
					// Definitions on the tree (the resolver reads them for colors/references),
					// then the uncompilable compiler-generated attributes, then render. A binder
					// failure must not kill the decompilation: degrade to unannotated output.
					try {
						AstAnnotationBinder.Attach(module, tree, knownType);
					}
					catch {
						// annotations stay absent; rendering continues
					}
					RemoveUncompilableCompilerAttributes(tree);
					// The 2.3 engine shows a 'Token: 0x... RID: ...' comment before every member
					// declaration when ShowTokenAndRvaComments is on (the dnSpy default).
					InsertTokenComments(tree, module as ModuleDefMD, settings.ShowTokenAndRvaComments);
					if (settings.SortMembers)
						ReorderMembers(tree, module as ModuleDefMD);
					var tokenWriter = new DnSpyTokenWriter(output, engine, module as ModuleDefMD, VisualBasicMetadataTextColorProvider.Instance, 1);
					tree.AcceptVisitor(new ICSharpCode.Decompiler.CSharp.OutputVisitor.CSharpOutputVisitor(
						ICSharpCode.Decompiler.CSharp.OutputVisitor.TokenWriter.WrapInWriterThatSetsLocationsInAST(tokenWriter),
						settings.EngineSettings.CSharpFormattingOptions));
					// Debug info: statement-level IL<->text mapping. The engine's
					// CreateSequencePoints walks the annotated AST (our token writer's ILocatable
					// implementation gave every rendered node its line/column), pairing each
					// statement's IL offsets with its text span. Methods without sequence points
					// fall back to one whole-body SourceStatement - the 2.3 engine's degraded shape
					// for bodies it could not track statement-level. Like the 2.3 engine, only
					// computed when the caller asked for it (CalculateILSpans); the export and
					// console pipelines skip it.
					if (ctx.CalculateILSpans) {
						Dictionary<MethodDef, MethodDebugInfoBuilder>? builders = null;
						var withStatements = new HashSet<MethodDef>();
						try {
							var sequencePoints = engine.CreateSequencePoints(tree);
							if (sequencePoints.Count > 0) {
								builders = new Dictionary<MethodDef, MethodDebugInfoBuilder>();
								foreach (var (method, start, end) in tokenWriter.MethodSpans) {
									var builder = new MethodDebugInfoBuilder(1, StateMachineKind.None,
										method, null, Array.Empty<SourceLocal>(), null, null);
									builder.StartPosition = start;
									builder.EndPosition = end;
									builder.Scope.Span = ILSpan.FromBounds(0, (uint)method.Body.GetCodeSize());
									builders.Add(method, builder);
								}
								foreach (var (function, sps) in sequencePoints) {
									// The bridge preserves rids, so the cecil method's token resolves
									// back to the dnlib method; lambdas map to compiler-generated
									// methods that are not rendered and get skipped here.
									if (function.CecilMethod is null || !(module.ResolveToken((uint)function.CecilMethod.MetadataToken.ToInt32()) is MethodDef method))
										continue;
									if (builders == null || !builders.TryGetValue(method, out var builder))
										continue;
								// Debug locals: the engine's variables carry the IL slot index and
								// the decompiled name - map them onto the method's dnlib locals so
								// the debugger's Locals window shows decompiled names.
								try {
									if (method.Body is not null) {
										var locals = new List<SourceLocal>();
										if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1")
											Console.Error.WriteLine("DBGLOCALS " + method.Name + " vars=[" + string.Join(",", function.Variables.Select(v => v.Kind + "#" + v.Index + ":" + v.Name)) + "]");
										foreach (var v in function.Variables) {
											// Local/UsingLocal/ForeachLocal carry real IL slots; parameters,
											// stack slots and exception variables are not locals.
											if (v.Index < 0 || v.Index >= method.Body.Variables.Count)
												continue;
											if (v.Kind == ICSharpCode.Decompiler.IL.VariableKind.Parameter ||
													v.Kind == ICSharpCode.Decompiler.IL.VariableKind.StackSlot ||
													v.Kind == ICSharpCode.Decompiler.IL.VariableKind.Exception)
												continue;
											var dnlibLocal = method.Body.Variables[v.Index];
											locals.Add(new SourceLocal(dnlibLocal, v.Name ?? dnlibLocal.Name ?? string.Empty,
												dnlibLocal.Type, SourceVariableFlags.None));
										}
										builder.Scope.Locals.AddRange(locals);
									}
								}
								catch {
									// locals mapping is an enhancement; never break the decompilation
								}
								foreach (var sp in sps) {
									if (sp.IsHidden || sp.EndOffset <= sp.Offset)
										continue;
									var textStart = tokenWriter.OffsetOf(sp.StartLine, sp.StartColumn);
									var textEnd = tokenWriter.OffsetOf(sp.EndLine, sp.EndColumn);
									if (textStart < 0 || textEnd < 0 || textEnd < textStart)
										continue;
									builder.Add(new SourceStatement(ILSpan.FromBounds((uint)sp.Offset, (uint)sp.EndOffset),
										TextSpan.FromBounds(textStart, textEnd)));
									withStatements.Add(method);
								}
							}
						}
					}
					catch {
						// Sequence points are an enhancement; any failure degrades to the
						// method-level spans below.
						builders = null;
						withStatements.Clear();
					}
					foreach (var (method, start, end) in tokenWriter.MethodSpans) {
						if (builders != null && withStatements.Contains(method)) {
							// Cover the whole body with the statement IL spans, like the 2.3
							// engine's sequence points did - otherwise instructions between
							// statements have no debugger highlight.
							builders[method].CompactILSpans((uint)method.Body.GetCodeSize());
							output.AddDebugInfo(builders[method].Create());
							continue;
						}
						var builder = new MethodDebugInfoBuilder(1, StateMachineKind.None,
							method, null, Array.Empty<SourceLocal>(), null, null);
						builder.StartPosition = start;
						builder.EndPosition = end;
						builder.Scope.Span = ILSpan.FromBounds(0, (uint)method.Body.GetCodeSize());
						builder.Add(new SourceStatement(new ILSpan(0, (uint)method.Body.GetCodeSize()),
							TextSpan.FromBounds(start, end)));
						output.AddDebugInfo(builder.Create());
					}
				}
				}
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) {
				WriteNote(output, "// ILSpy 3.2 decompilation failed: " + DescribeException(ex));
				return;
			}
		}

		/// <summary>
		/// Drops the IteratorStateMachine/AsyncStateMachine attributes. Their typeof(&lt;X&gt;d__N)
		/// argument names an identifier C# cannot express, so they render as syntax errors; they
		/// only name the compiler's own state-machine type, which is re-derived when the decompiled
		/// source compiles, and the 2.3 engine never emitted them.
		/// </summary>
		static void RemoveUncompilableCompilerAttributes(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree) {
			foreach (var section in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.AttributeSection>().ToList()) {
				var removed = false;
				foreach (var attribute in section.Attributes.ToList()) {
					var name = attribute.Type switch {
						ICSharpCode.Decompiler.CSharp.Syntax.SimpleType simple => simple.Identifier,
						ICSharpCode.Decompiler.CSharp.Syntax.MemberType member => member.MemberName,
						_ => null,
					};
					// C# lets attribute names drop the "Attribute" suffix, and the engine does.
					if (name == "IteratorStateMachineAttribute" || name == "AsyncStateMachineAttribute" ||
							name == "IteratorStateMachine" || name == "AsyncStateMachine") {
						attribute.Remove();
						removed = true;
					}
				}
				if (removed && !section.Attributes.Any())
					section.Remove();
			}
		}

		/// <summary>
		/// Removes comment nodes that the engine placed INSIDE field declarations (its
		/// "not supported: data(...)" notes on compiler-generated data fields). Rendered
		/// inline they split the declaration in half in both C# and VB.
		/// </summary>
		static void StripCommentsInsideFieldDeclarations(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree) {
			foreach (var field in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration>().ToList()) {
				foreach (var comment in field.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.Comment>().ToList())
					comment.Remove();
			}
		}

	static void WriteNote(IDecompilerOutput output, string text) =>
		output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);

	/// <summary>
	/// Inserts a 'Token: 0x... RID: ...' comment before every member declaration that has a
	/// metadata definition, mirroring the 2.3 engine's ShowTokenAndRvaComments output. Method
	/// bodies add the RVA and file offset segments. The comment goes right before the
	/// declaration, so documentation comments (inserted earlier by the engine transform)
	/// stay above it like in 2.3.
	/// </summary>
	/// <summary>
	/// Reorders the members of every type declaration to match the 2.3 engine's default
	/// grouping (methods, properties, events, fields, nested types - the order of its
	/// decompilationObjects array) and, when SortMembers is on, sorts each group with the
	/// same comparers the 2.3 engine used (dnSpy.Contracts.Logic MemberComparer). Types the
	/// binder could not bind keep their original order.
	/// </summary>
	static void ReorderMembers(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree, ModuleDefMD module) {
		try {
			foreach (var typeDecl in tree.Descendants.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>()) {
				if (typeDecl.ClassType == ICSharpCode.Decompiler.CSharp.Syntax.ClassType.Enum)
					continue; // enum members are ordered by their constant values, never sorted
				var typeDef = typeDecl.Annotation<TypeDef>();
				if (typeDef is null)
					continue;

				var methods = new List<ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration>();
				var properties = new List<ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration>();
				var events = new List<ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration>();
				var fields = new List<ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration>();
				var nestedTypes = new List<ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration>();
				foreach (var member in typeDecl.Members) {
					switch (member) {
					case ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration nested:
						nestedTypes.Add(nested);
						break;
					case ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration prop:
						properties.Add(prop);
						break;
					case ICSharpCode.Decompiler.CSharp.Syntax.EventDeclaration ev:
						events.Add(ev);
						break;
					case ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration field:
						fields.Add(field);
						break;
					default:
						methods.Add(member);
						break;
					}
				}

				// Sort each group by its dnlib definition when the binder attached one; items
				// without a definition keep their relative order (List.Sort is not stable, so
				// equal keys must return 0 without reordering guarantees - acceptable here
				// because unbound members are rare).
				methods.Sort((a, b) => {
					var ma = a.Annotation<MethodDef>();
					var mb = b.Annotation<MethodDef>();
					if (ma is null || mb is null)
						return 0;
					return dnSpy.Contracts.Decompiler.MethodDefComparer.Instance.Compare(ma, mb);
				});
				properties.Sort((a, b) => {
					var pa = a.Annotation<PropertyDef>();
					var pb = b.Annotation<PropertyDef>();
					if (pa is null || pb is null)
						return 0;
					return dnSpy.Contracts.Decompiler.PropertyDefComparer.Instance.Compare(pa, pb);
				});
				events.Sort((a, b) => {
					var ea = a.Annotation<EventDef>();
					var eb = b.Annotation<EventDef>();
					if (ea is null || eb is null)
						return 0;
					return dnSpy.Contracts.Decompiler.EventDefComparer.Instance.Compare(ea, eb);
				});
				fields.Sort((a, b) => {
					var fa = GetFieldFor(a, module);
					var fb = GetFieldFor(b, module);
					if (fa is null || fb is null)
						return 0;
					return dnSpy.Contracts.Decompiler.FieldDefComparer.Instance.Compare(fa, fb);
				});
				nestedTypes.Sort((a, b) => {
					var ta = a.Annotation<TypeDef>();
					var tb = b.Annotation<TypeDef>();
					if (ta is null || tb is null)
						return 0;
					return dnSpy.Contracts.Decompiler.TypeDefComparer.Instance.Compare(ta, tb);
				});

				// Re-add in the 2.3 grouping order.
				typeDecl.Members.Clear();
				foreach (var m in methods)
					typeDecl.Members.Add(m);
				foreach (var m in properties)
					typeDecl.Members.Add(m);
				foreach (var m in events)
					typeDecl.Members.Add(m);
				foreach (var m in fields)
					typeDecl.Members.Add(m);
				foreach (var m in nestedTypes)
					typeDecl.Members.Add(m);
			}
		}
		catch (Exception ex) {
			// member reordering must never break the decompilation
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1")
				Console.Error.WriteLine("REORDER-EX " + ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace);
		}
	}

	static dnlib.DotNet.FieldDef? GetFieldFor(ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration member, ModuleDefMD module) {
		var ownerType = member.Ancestors.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>().FirstOrDefault();
		var typeDef = ownerType?.Annotation<TypeDef>();
		if (typeDef is null)
			return null;
		var variableName = (member as ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration)?.Variables.FirstOrDefault()?.Name;
		return variableName is null ? null : typeDef.Fields.FirstOrDefault(f => f.Name == variableName);
	}

	internal static void InsertTokenComments(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree, ModuleDefMD module, bool showTokens) {
		if (module is null || !showTokens)
			return;
		foreach (var decl in tree.DescendantsAndSelf.OfType<ICSharpCode.Decompiler.CSharp.Syntax.EntityDeclaration>().ToList()) {
			var definition = (IMDTokenProvider?)decl.Annotation<MethodDef>() ?? (IMDTokenProvider?)decl.Annotation<PropertyDef>() ??
				(IMDTokenProvider?)decl.Annotation<EventDef>() ?? (IMDTokenProvider?)decl.Annotation<TypeDef>();
			if (definition is null) {
				// The binder does not annotate field declarations: match the variables against
				// the declaring type's fields (enum members are fields too).
				if (decl is ICSharpCode.Decompiler.CSharp.Syntax.FieldDeclaration fieldDecl) {
					var ownerType = decl.Ancestors.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>().FirstOrDefault();
					var typeDef = ownerType?.Annotation<TypeDef>();
					var variableName = fieldDecl.Variables.FirstOrDefault()?.Name;
					if (typeDef is not null && variableName is not null)
						definition = typeDef.Fields.FirstOrDefault(f => f.Name == variableName);
				}
				else if (decl is ICSharpCode.Decompiler.CSharp.Syntax.EnumMemberDeclaration enumMember) {
					var ownerType = decl.Ancestors.OfType<ICSharpCode.Decompiler.CSharp.Syntax.TypeDeclaration>().FirstOrDefault();
					var typeDef = ownerType?.Annotation<TypeDef>();
					if (typeDef is not null)
						definition = typeDef.Fields.FirstOrDefault(f => f.Name == enumMember.Name);
				}
			}
			if (definition is null)
				continue;
			try {
				var comment = new ICSharpCode.Decompiler.CSharp.Syntax.Comment(BuildTokenCommentText(definition, module), ICSharpCode.Decompiler.CSharp.Syntax.CommentType.SingleLine);
				decl.Parent?.InsertChildBefore(decl, comment, ICSharpCode.Decompiler.CSharp.Syntax.Roles.Comment);
			}
			catch {
				// a comment must never break the decompilation
			}
		}
	}

	static string BuildTokenCommentText(IMDTokenProvider definition, ModuleDefMD module) {
		var text = $" Token: 0x{definition.MDToken.Raw:X8} RID: {definition.MDToken.Rid}";
		if (definition is MethodDef method && method.RVA != 0) {
			try {
				var fileOffset = module.ToFileOffset((uint)method.RVA);
				text += $" RVA: 0x{method.RVA:X8} File Offset: 0x{fileOffset:X8}";
			}
			catch {
				// offsets unavailable in-memory; the token part stays
			}
		}
		return text;
	}
	}
}
