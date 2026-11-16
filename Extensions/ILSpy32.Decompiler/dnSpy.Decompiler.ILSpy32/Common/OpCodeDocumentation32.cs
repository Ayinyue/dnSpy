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
using System.Reflection;
using DnlibOpCode = dnlib.DotNet.Emit.OpCode;
using dnSpy.Decompiler.IL;
using Mono.Cecil.Cil;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// Bridges the 3.2 engine's cecil <see cref="OpCode"/> to the dnlib one expected by
	/// <see cref="ILLanguageHelper.GetOpCodeDocumentation"/>. Cecil's <c>Code</c> enum is
	/// not the raw IL value (e.g. Call is 0x27 in Cecil but 0x28 in dnlib), so the lookup
	/// is by opcode name, which is the ECMA name in both; the result is cached by the
	/// helper itself.
	/// </summary>
	public static class OpCodeDocumentation32 {
		static readonly Dictionary<string, DnlibOpCode> opcodeMap = CreateOpcodeMap();

		static Dictionary<string, DnlibOpCode> CreateOpcodeMap() {
			var map = new Dictionary<string, DnlibOpCode>(StringComparer.Ordinal);
			foreach (var field in typeof(dnlib.DotNet.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
				if (field.FieldType != typeof(DnlibOpCode))
					continue;
				var opCode = (DnlibOpCode)field.GetValue(null);
				map[opCode.Name] = opCode;
			}
			return map;
		}

		public static string GetDocumentation(OpCode code) {
			if (opcodeMap.TryGetValue(code.Name, out var dnlibOpCode))
				return ILLanguageHelper.GetOpCodeDocumentation(dnlibOpCode);
			return null;
		}
	}
}
