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
using dnSpy.Decompiler.ILSpy32.Settings;
using dnSpy.Decompiler.Settings;

namespace dnSpy.Decompiler.ILSpy32.ILAst {
	/// <summary>
	/// Settings for the ILAst view. The 3.2 engine exposes the ILAst writer options
	/// (<see cref="ICSharpCode.Decompiler.IL.ILAstWritingOptions"/>) directly, so unlike the
	/// 2.3 ILAst language (which had one language per optimizer step) there is a single
	/// language whose transform pipeline can be switched off to inspect the raw ILReader
	/// output.
	/// </summary>
	sealed class ILAstDecompilerSettings32 : DecompilerSettingsBase {
		internal static readonly Guid RunTransforms_GUID = new Guid("4E7A1C93-2B6D-4F81-9C35-7D2A8E1B60F4");
		internal static readonly Guid ShowILRanges_GUID = new Guid("6B0D5F28-9A47-4E13-B6C2-58F1D9A3E770");
		internal static readonly Guid UseLogicOperationSugar_GUID = new Guid("8C3E7A15-4D92-4B60-A1F8-2E6B9C4D5A11");
		internal static readonly Guid UseFieldSugar_GUID = new Guid("A51F8C63-7E24-49B0-8D5A-3C7E1F9B2066");

		int version;
		bool runTransforms = true;
		bool showILRanges;
		bool useLogicOperationSugar = true;
		bool useFieldSugar = true;
		IDecompilerOption[]? options;

		/// <summary>
		/// Engine settings used to build the <see cref="ICSharpCode.Decompiler.CSharp.CSharpDecompiler"/>
		/// whose type system feeds <see cref="ICSharpCode.Decompiler.IL.ILReader"/>.
		/// </summary>
		public DecompilerSettings32 DecompilerSettings { get; }

		public ILAstDecompilerSettings32() => DecompilerSettings = new DecompilerSettings32();

		ILAstDecompilerSettings32(ILAstDecompilerSettings32 other) {
			DecompilerSettings = new DecompilerSettings32();
			runTransforms = other.runTransforms;
			showILRanges = other.showILRanges;
			useLogicOperationSugar = other.useLogicOperationSugar;
			useFieldSugar = other.useFieldSugar;
			version = other.version;
		}

		/// <summary>Apply the engine's IL transforms (loop/condition/expression detection).</summary>
		public bool RunTransforms {
			get => runTransforms;
			set { if (runTransforms != value) { runTransforms = value; RaiseVersionChanged(); } }
		}

		/// <summary>Prefix every instruction with its <c>[IL_xxxx..IL_xxxx]</c> range.</summary>
		public bool ShowILRanges {
			get => showILRanges;
			set { if (showILRanges != value) { showILRanges = value; RaiseVersionChanged(); } }
		}

		public bool UseLogicOperationSugar {
			get => useLogicOperationSugar;
			set { if (useLogicOperationSugar != value) { useLogicOperationSugar = value; RaiseVersionChanged(); } }
		}

		public bool UseFieldSugar {
			get => useFieldSugar;
			set { if (useFieldSugar != value) { useFieldSugar = value; RaiseVersionChanged(); } }
		}

		public override DecompilerSettingsBase Clone() => new ILAstDecompilerSettings32(this);
		public override int Version => version;
		public override event EventHandler? VersionChanged;

		public override IEnumerable<IDecompilerOption> Options => options ??= CreateOptions().ToArray();

		IEnumerable<IDecompilerOption> CreateOptions() {
			yield return new DecompilerOption<bool>(RunTransforms_GUID, () => RunTransforms, v => RunTransforms = v) {
				Description = "Run the IL transforms (loop, condition and expression detection)",
				Name = "Run IL transforms",
			};
			yield return new DecompilerOption<bool>(ShowILRanges_GUID, () => ShowILRanges, v => ShowILRanges = v) {
				Description = "Show the IL range of every instruction",
				Name = "Show IL ranges",
			};
			yield return new DecompilerOption<bool>(UseLogicOperationSugar_GUID, () => UseLogicOperationSugar, v => UseLogicOperationSugar = v) {
				Description = "Use logic operation sugar (&&, ||, !) where possible",
				Name = "Use logic operation sugar",
			};
			yield return new DecompilerOption<bool>(UseFieldSugar_GUID, () => UseFieldSugar, v => UseFieldSugar = v) {
				Description = "Use field sugar (drop redundant ldfld/stfld self references)",
				Name = "Use field sugar",
			};
		}

		protected void RaiseVersionChanged() {
			version++;
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		public override bool Equals(object? obj) => obj is ILAstDecompilerSettings32;
		public override int GetHashCode() => 0x3200;
	}
}
