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
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy32.Settings {
	/// <summary>
	/// dnSpy-facing settings for the ILSpy 3.2 engine.
	/// Exposes no individual options yet (the engine uses its defaults);
	/// the underlying <c>ICSharpCode.Decompiler.DecompilerSettings</c> is reachable
	/// through <see cref="EngineSettings"/> for future option mapping.
	/// </summary>
	sealed class DecompilerSettings32 : DecompilerSettingsBase {
		int version;

		public ICSharpCode.Decompiler.DecompilerSettings EngineSettings { get; }

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
			EngineSettings = other.EngineSettings;
			version = other.version;
		}

		public override DecompilerSettingsBase Clone() => new DecompilerSettings32(this);

		public override int Version => version;

		public override event EventHandler? VersionChanged;

		public override IEnumerable<IDecompilerOption> Options => Array.Empty<IDecompilerOption>();

		void RaiseVersionChanged() {
			version++;
			VersionChanged?.Invoke(this, EventArgs.Empty);
		}

		public override bool Equals(object? obj) => obj is DecompilerSettings32 other && ReferenceEquals(EngineSettings, other.EngineSettings);

		public override int GetHashCode() => EngineSettings.GetHashCode();
	}
}
