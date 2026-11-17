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
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.Settings;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// dnSpy-facing settings for the ILSpy 3.2 engine. Every option dnSpy's settings page
	/// shows for the 2.3 decompilers and that the 3.2 engine (or the extension) honors is
	/// exposed here, wired to <see cref="EngineSettings"/> or to extension-owned state.
	/// Options the 3.2 engine has no equivalent for (IL-viewer options, member sorting,
	/// numeric formatting) stay unexposed so the page doesn't offer dead switches.
	/// </summary>
	class DecompilerSettings32 : DecompilerSettingsBase {
		int version;
		/// <summary>Set by the persistence impls so the one-time 2.3 option migration can
		/// read the legacy section; null in unit-style constructed instances.</summary>
		protected internal dnSpy.Contracts.Settings.ISettingsService settingsService_Migrate;
		bool showTokenAndRvaComments = true;
		IDecompilerOption[]? options;

		public ICSharpCode.Decompiler.DecompilerSettings EngineSettings { get; }

		/// <summary>
		/// Whether member declarations get the 'Token: 0x... RID: ...' comment. The 2.3
		/// engine printed these under the same option; the 3.2 engine has no such switch so
		/// the extension owns it.
		/// </summary>
		public bool ShowTokenAndRvaComments {
			get => showTokenAndRvaComments;
			set {
				if (showTokenAndRvaComments != value) {
					showTokenAndRvaComments = value;
					RaiseVersionChanged();
				}
			}
		}

		bool sortMembers;

		/// <summary>
		/// Whether members are sorted alphabetically inside their groups (methods,
		/// properties, events, fields, nested types). The 2.3 engine had this switch
		/// (off by default); the extension implements the reorder itself.
		/// </summary>
		public bool SortMembers {
			get => sortMembers;
			set {
				if (sortMembers != value) {
					sortMembers = value;
					RaiseVersionChanged();
				}
			}
		}

		public DecompilerSettings32() : this(vbCompatibility: false) {
		}

		/// <param name="vbCompatibility">
		/// When true, the engine is asked to avoid AST forms the VB converter cannot
		/// represent. The C# decompiler keeps the original engine settings.
		/// </param>
		public DecompilerSettings32(bool vbCompatibility) {
			EngineSettings = new ICSharpCode.Decompiler.DecompilerSettings();
			// dnSpy's 2.3 engine put System.* usings first (SortSystemUsingStatementsFirst);
			// the 3.2 engine sorts all namespaces ordinal-ascending instead.
			EngineSettings.SortSystemUsingStatementsFirst = true;
			// dnSpy 2.3 always spelled out the delegate type for method-group conversions
			// ('obj.Event += new EventHandler(this.M)'); the 3.2 engine defaults to the
			// implicit form ('obj.Event += this.M').
			EngineSettings.UseImplicitMethodGroupConversion = false;
			// dnSpy's 2.3 formatting policy collapsed an all-empty accessor list onto the
			// declaration line ({ get; set; }); the 3.2 engine exposes the switch but its
			// output visitor never applied it. ForceOneLine is 2.3's PropertyFormatting.SingleLine.
			EngineSettings.CSharpFormattingOptions.AutoPropertyFormatting = ICSharpCode.Decompiler.CSharp.OutputVisitor.PropertyFormatting.ForceOneLine;
			// The VB converter (ported from the 2.3 era) has no visitors for tuple or
			// interpolated-string AST nodes, so ask the engine to emit legacy forms.
			try {
				EngineSettings.TupleTypes = false;
				EngineSettings.StringInterpolation = false;
				if (vbCompatibility) {
					// The VB AST has no null-propagation node. With ?. enabled the engine
					// wraps member-access targets in UnaryOperatorExpression(NullConditional),
					// which the converter cannot express. Disabling it makes the engine emit
					// "a != null ? a.b : null" instead, which the converter already handles.
					EngineSettings.NullPropagation = false;
					// The 2.3 engine emitted method chains for queries with anonymous-type
					// groupings ("group p by new {...} into g") because it could not express
					// them as query syntax; the VB converter cannot map query continuation
					// clauses either. Method chains match the 2.3 output exactly.
					EngineSettings.QueryExpressions = false;
				}
			}
			catch {
				// settings API differs slightly between engine versions; ignore
			}
		}

		DecompilerSettings32(DecompilerSettings32 other) {
			// Deep-copy the engine settings: clones must not write through to the original
			// (dnSpy clones before editing, and the decompiler cache keys on the instance).
			EngineSettings = other.EngineSettings.Clone();
			showTokenAndRvaComments = other.showTokenAndRvaComments;
			sortMembers = other.sortMembers;
			version = other.version;
		}

		public override DecompilerSettingsBase Clone() => new DecompilerSettings32(this);

		public override int Version => version;

		public override event EventHandler? VersionChanged;

		public override IEnumerable<IDecompilerOption> Options => options ??= CreateOptions().ToArray();

		IEnumerable<IDecompilerOption> CreateOptions() {
			// Presentation options the 3.2 engine honors directly.
			yield return BooleanOption(DecompilerOptionConstants.ShowXmlDocumentation_GUID,
				"Include XML documentation comments in the output",
				() => EngineSettings.ShowXmlDocumentation, v => { EngineSettings.ShowXmlDocumentation = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.UsingDeclarations_GUID,
				"Use using/imports statements instead of fully qualified type names",
				() => EngineSettings.UsingDeclarations, v => { EngineSettings.UsingDeclarations = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.FullyQualifyAmbiguousTypeNames_GUID,
				"Fully qualify type names that are ambiguous",
				() => EngineSettings.FullyQualifyAmbiguousTypeNames, v => { EngineSettings.FullyQualifyAmbiguousTypeNames = v; RaiseVersionChanged(); });

			// Code-shape options mapped 1:1 onto engine settings.
			yield return BooleanOption(DecompilerOptionConstants.YieldReturn_GUID,
				"Decompile yield return into iterator methods",
				() => EngineSettings.YieldReturn, v => { EngineSettings.YieldReturn = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.AsyncAwait_GUID,
				"Decompile async methods with await",
				() => EngineSettings.AsyncAwait, v => { EngineSettings.AsyncAwait = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.AnonymousMethods_GUID,
				"Decompile anonymous methods",
				() => EngineSettings.AnonymousMethods, v => { EngineSettings.AnonymousMethods = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.ExpressionTrees_GUID,
				"Decompile expression trees",
				() => EngineSettings.ExpressionTrees, v => { EngineSettings.ExpressionTrees = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.AutomaticProperties_GUID,
				"Use auto-implemented properties where possible",
				() => EngineSettings.AutomaticProperties, v => { EngineSettings.AutomaticProperties = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.AutomaticEvents_GUID,
				"Use field-like events where possible",
				() => EngineSettings.AutomaticEvents, v => { EngineSettings.AutomaticEvents = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.ForEachStatement_GUID,
				"Use foreach statements where possible",
				() => EngineSettings.ForEachStatement, v => { EngineSettings.ForEachStatement = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.LockStatement_GUID,
				"Use lock statements where possible",
				() => EngineSettings.LockStatement, v => { EngineSettings.LockStatement = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.QueryExpressions_GUID,
				"Use LINQ query expressions where possible",
				() => EngineSettings.QueryExpressions, v => { EngineSettings.QueryExpressions = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.SwitchStatementOnString_GUID,
				"Use switch statements on strings",
				() => EngineSettings.SwitchStatementOnString, v => { EngineSettings.SwitchStatementOnString = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.IntroduceIncrementAndDecrement_GUID,
				"Use ++ and -- where possible",
				() => EngineSettings.IntroduceIncrementAndDecrement, v => { EngineSettings.IntroduceIncrementAndDecrement = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.MakeAssignmentExpressions_GUID,
				"Use assignment expressions where possible",
				() => EngineSettings.MakeAssignmentExpressions, v => { EngineSettings.MakeAssignmentExpressions = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.ObjectOrCollectionInitializers_GUID,
				"Use object and collection initializers where possible",
				() => EngineSettings.ObjectOrCollectionInitializers, v => { EngineSettings.ObjectOrCollectionInitializers = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.UseDebugSymbols_GUID,
				"Use variable names and line info from debug symbols when available",
				() => EngineSettings.UseDebugSymbols, v => { EngineSettings.UseDebugSymbols = v; RaiseVersionChanged(); });
			yield return BooleanOption(DecompilerOptionConstants.AlwaysGenerateExceptionVariableForCatchBlocksUnlessTypeIsObject_GUID,
				"Always name the exception variable in catch blocks",
				() => EngineSettings.AlwaysGenerateExceptionVariableForCatchBlocks, v => { EngineSettings.AlwaysGenerateExceptionVariableForCatchBlocks = v; RaiseVersionChanged(); });

			// Extension-owned option: the 2.3 engine had a SortMembers switch (off by default)
			// and the decompiled member order must follow it, so the extension implements the
			// member reorder itself (see CSharpDecompiler32.ReorderMembers).
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.SortMembers_GUID,
				() => SortMembers, v => SortMembers = v) {
				Description = "Sort members alphabetically (methods, properties, events, fields, nested types)",
				Name = DecompilerOptionConstants.SortMembers_NAME,
			};

			// Extension-owned option: the 3.2 engine has no token-comment switch.
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID,
					() => ShowTokenAndRvaComments, v => ShowTokenAndRvaComments = v) {
				Description = "Show 'Token: 0x... RID: ...' comments before member declarations",
				Name = DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
			};
		}

		static DecompilerOption<bool> BooleanOption(Guid guid, string description, Func<bool> getter, Action<bool> setter) =>
			new DecompilerOption<bool>(guid, getter, setter) { Description = description };

		protected void RaiseVersionChanged() {
			version++;
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// Persisted boolean entries: 2.3 settings-section key, current value, setter. The
		/// 2.3 keys are reused so the one-time option migration reads the user's customized
		/// values directly from the 2.3 settings section.
		/// </summary>
		internal virtual IEnumerable<(string key, bool? value, Action<bool> set)> PersistedBooleanEntries { get; } = System.Array.Empty<(string, bool?, Action<bool>)>();

		/// <summary>
		/// 2.3 settings-section key -> this settings' key. Same names except the catch-variable
		/// option, whose 2.3 property name carries a longer suffix.
		/// </summary>
		protected static readonly Dictionary<string, string> Legacy23Mappings = new Dictionary<string, string>(StringComparer.Ordinal) {
			["AnonymousMethods"] = "AnonymousMethods",
			["ExpressionTrees"] = "ExpressionTrees",
			["YieldReturn"] = "YieldReturn",
			["AsyncAwait"] = "AsyncAwait",
			["QueryExpressions"] = "QueryExpressions",
			["FullyQualifyAmbiguousTypeNames"] = "FullyQualifyAmbiguousTypeNames",
			["UseDebugSymbols"] = "UseDebugSymbols",
			["ShowXmlDocumentation"] = "ShowXmlDocumentation",
			["ShowTokenAndRvaComments"] = "ShowTokenAndRvaComments",
			["AlwaysGenerateExceptionVariableForCatchBlocksUnlessTypeIsObject"] = "AlwaysGenerateExceptionVariableForCatchBlocks",
		};

		/// <summary>
		/// One-time carry-over of option values the user customized under the 2.3 decompilers
		/// (same GUIDs / same meaning where this engine honors them). Runs in the impls'
		/// constructors before saving is enabled; only keys present in the 2.3 section and
		/// mapped here are applied.
		/// </summary>
		/// <summary>
		/// One-time carry-over, guarded by the target section being empty: once the user
		/// edits anything under 3.2 (which writes the section), the 2.3 values must stop
		/// overriding their newer choices on subsequent starts.
		/// </summary>
		protected void MigrateFrom23(Guid targetSectionGuid) {
			var settings = settingsService_Migrate;
			if (settings is null)
				return;
			var target = settings.GetOrCreateSection(targetSectionGuid);
			if (target.Attributes.Length != 0)
				return; // the user already has 3.2-era values; migration is done
			var legacy = settings.GetOrCreateSection(DecompilerSettings32Impl.Legacy23SettingsGuid);
			var legacyAttrs = legacy.Attributes;
			if (legacyAttrs.Length == 0)
				return; // nothing customized under 2.3 (or the 2.3 decompilers never ran)
			// Reverse lookup: 3.2 key -> the 2.3 section key (the long catch-variable name).
			var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var kv in Legacy23Mappings) {
				if (!reverse.ContainsKey(kv.Value))
					reverse[kv.Value] = kv.Key;
			}
			foreach (var entry in PersistedBooleanEntries) {
				foreach (var (key, value) in legacyAttrs) {
					if (!string.Equals(key, entry.key, StringComparison.Ordinal) &&
						(!reverse.TryGetValue(entry.key, out var legacyKey) || !string.Equals(key, legacyKey, StringComparison.Ordinal)))
						continue;
					if (bool.TryParse(value, out var b))
						entry.set(b);
					break;
				}
			}
		}

		internal static readonly Guid Legacy23SettingsGuid = new Guid("6745457F-254B-4B7B-90F1-F948F0721C3B");

		public override bool Equals(object? obj) => obj is DecompilerSettings32 other && ReferenceEquals(EngineSettings, other.EngineSettings);

		public override int GetHashCode() => EngineSettings.GetHashCode();
	}
}
