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

using System.ComponentModel.Composition;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy32.Common;

namespace dnSpy.Decompiler.ILSpy32.IL {
	/// <summary>
	/// asm editor 的简单 IL 打印器。2.3 的对应实现在 2.3 扩展里，M4 移除 2.3 后若本导出缺失，
	/// asm editor 会退回 <c>DummyPrinter</c>（输出 "Missing ISimpleILPrinter"）。
	/// </summary>
	[Export(typeof(ISimpleILPrinter))]
	sealed class SimpleILPrinter32 : ISimpleILPrinter {
		double ISimpleILPrinter.Order => -100;

		bool ISimpleILPrinter.Write(IDecompilerOutput output, IMemberRef? member) => ILDecompilerUtils32.Write(output, member);
		void ISimpleILPrinter.Write(IDecompilerOutput output, MethodSig? sig) => sig?.WriteTo(output, new StringBuilder());
		void ISimpleILPrinter.Write(IDecompilerOutput output, TypeSig? type) => type?.WriteTo(output, new StringBuilder());
	}
}
