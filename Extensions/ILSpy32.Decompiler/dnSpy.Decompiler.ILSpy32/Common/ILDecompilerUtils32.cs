using System;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy32.IL;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using Mono.Cecil;

namespace dnSpy.Decompiler.ILSpy32.Common {
	/// <summary>
	/// Member → IL 文本，供 asm editor 的 <see cref="ISimpleILPrinter"/> 使用。方法/字段/类型走
	/// <see cref="DnlibILText32"/> 的写入器（与 2.3 的 <c>DisassemblerHelpers</c> 逐字一致）；
	/// 属性/事件经 3.2 引擎的 <see cref="ReflectionDisassembler"/> 输出，因此需要 dnlib→cecil 桥接。
	/// </summary>
	static class ILDecompilerUtils32 {
		public static bool Write(IDecompilerOutput output, IMemberRef? member) {
			if (member is IMethod method && method.IsMethod) {
				method.WriteTo(output, new StringBuilder());
				return true;
			}

			if (member is IField field && field.IsField) {
				field.WriteTo(output, new StringBuilder());
				return true;
			}

			if (member is PropertyDef prop)
				return WriteProperty(output, prop, full: false);

			if (member is EventDef evt)
				return WriteEvent(output, evt, full: false);

			if (member is ITypeDefOrRef type) {
				type.WriteTo(output, new StringBuilder(), ILNameSyntax.TypeName);
				return true;
			}

			return false;
		}

		// Cecil 的 ModuleDefinition.LookupToken 不解析 Property(0x17)/Event(0x14) 定义行
		// （返回 null），与 ILDecompiler32 相同，这里通过声明类型的属性/事件列表按 rid 查找。
		/// <summary>
		/// 属性 → IL。<paramref name="full"/>=true 输出完整访问器块（视图用），false 输出 2.3 的短式
		/// <c>{ .get; }</c>（asm editor 单元格与类型清单用）。
		/// </summary>
		public static bool WriteProperty(IDecompilerOutput output, PropertyDef property, bool full) {
			var cecilProperty = ResolveProperty(property);
			if (cecilProperty is null)
				return false;
			var bridge = new DecompilerOutputTextOutput(output, property.Module);
			new ReflectionDisassembler(bridge, CancellationToken.None).DisassembleProperty(cecilProperty, full);
			return true;
		}

		public static bool WriteEvent(IDecompilerOutput output, EventDef evt, bool full) {
			var cecilEvent = ResolveEvent(evt);
			if (cecilEvent is null)
				return false;
			var bridge = new DecompilerOutputTextOutput(output, evt.Module);
			new ReflectionDisassembler(bridge, CancellationToken.None).DisassembleEvent(cecilEvent, full);
			return true;
		}
		static PropertyDefinition? ResolveProperty(PropertyDef property) {
			var cecilModule = DnlibCecilBridge.TryGetModule(property.Module);
			if (cecilModule is null || property.DeclaringType is null)
				return null;
			var declaringType = DnlibCecilBridge.TryGetMember(cecilModule, property.DeclaringType.MDToken.Raw) as TypeDefinition;
			if (declaringType is null)
				return null;
			var rid = property.MDToken.Rid;
			foreach (var p in declaringType.Properties) {
				if (p.MetadataToken.RID == rid)
					return p;
			}
			return null;
		}

		static EventDefinition? ResolveEvent(EventDef evt) {
			var cecilModule = DnlibCecilBridge.TryGetModule(evt.Module);
			if (cecilModule is null || evt.DeclaringType is null)
				return null;
			var declaringType = DnlibCecilBridge.TryGetMember(cecilModule, evt.DeclaringType.MDToken.Raw) as TypeDefinition;
			if (declaringType is null)
				return null;
			var rid = evt.MDToken.Rid;
			foreach (var e in declaringType.Events) {
				if (e.MetadataToken.RID == rid)
					return e;
			}
			return null;
		}
	}
}
