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
	sealed class DecompilerSettings32 : DecompilerSettingsBase {
		int version;
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

		public DecompilerSettings32() : this(vbCompatibility: false) {
		}

		/// <param name="vbCompatibility">
		/// When true, the engine is asked to avoid AST forms the VB converter cannot
		/// represent. The C# decompiler keeps the original engine settings.
		/// </param>
		public DecompilerSettings32(bool vbCompatibility) {
			EngineSettings = new ICSharpCode.Decompiler.DecompilerSettings();
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

			// Extension-owned option: the 3.2 engine has no token-comment switch.
			yield return new DecompilerOption<bool>(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID,
					() => ShowTokenAndRvaComments, v => ShowTokenAndRvaComments = v) {
				Description = "Show 'Token: 0x... RID: ...' comments before member declarations",
				Name = DecompilerOptionConstants.ShowTokenAndRvaComments_NAME,
			};
		}

		static DecompilerOption<bool> BooleanOption(Guid guid, string description, Func<bool> getter, Action<bool> setter) =>
			new DecompilerOption<bool>(guid, getter, setter) { Description = description };

		void RaiseVersionChanged() {
			version++;
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		public override bool Equals(object? obj) => obj is DecompilerSettings32 other && ReferenceEquals(EngineSettings, other.EngineSettings);

		public override int GetHashCode() => EngineSettings.GetHashCode();
	}
}
