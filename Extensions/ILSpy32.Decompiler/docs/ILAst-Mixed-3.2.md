# ILAst / Mixed 视图在 3.2 引擎上的落地

状态：**已实现并验证**（2026-11-16）。本文档记录设计、新增的引擎 public API、与 2.3 的差异、验证方式。

## 1. 背景

2.3 引擎（dnSpy fork 的 ILSpy 2.3，dnlib 版）自带 `ICSharpCode.Decompiler.ILAst` 管线
（`ILAstBuilder` + `ILAstOptimizer` + `ILNode.WriteTo`），dnSpy 的 **ILAst** 视图
（`#if DEBUG`）与 **IL with C#**（Mixed）视图建立在其上。3.2 引擎（上游 ILSpy 3.2，cecil 版）
没有 `ILAst` 命名空间，因此覆盖审计把这两项列为「保留 2.3 预编译程序集」的用户决策项。

## 2. 关键发现：3.2 已有 ILAst 的后继实现

3.2 的 `ICSharpCode.Decompiler.IL` 命名空间就是 2.3 ILAst 的现代版：

| 2.3 | 3.2 |
|---|---|
| `ILAstBuilder.Build(MethodDef, ...)` | `ILReader(IDecompilerTypeSystem).ReadIL(Cil.MethodBody, ct)` → `ILFunction` |
| `ILAstOptimizer.Optimize(context, block, abortBeforeStep)` | `CSharpDecompiler.GetILTransforms()` + `ILFunction.RunTransforms(transforms, context)` |
| `ILBlock` / `ILExpression` / `ILVariable` | `ILFunction` / `BlockContainer` / `Block` / `ILVariable` |
| `ILNode.WriteTo(IDecompilerOutput, MethodDebugInfoBuilder)` | `ILInstruction.WriteTo(ITextOutput, ILAstWritingOptions)` |

所以**不需要**把 2.3 那套 dnlib 版 ILAst（约 20 个文件）搬进 3.2；只需补 dnSpy 侧消费所需的
public API，并移植两个视图的胶水层。

## 3. 引擎侧新增的 public API

全部在 3.2 子模块内，均为**向后兼容的增量**（默认关闭/无行为变化）：

| 文件 | 新增 |
|---|---|
| `Output/CodeBracesRangeFlags.cs` | 新枚举，数值与 `dnSpy.Contracts.Decompiler.CodeBracesRangeFlags` 完全一致（可直接 cast） |
| `Output/ITextOutput.cs` | `int NextPosition { get; }`、`void AddCodeBracesRange(int start, int end, CodeBracesRangeFlags flags)` |
| `Output/PlainTextOutput.cs` | 实现上述两个成员（位置按字符计数，含待写缩进与换行） |
| `IL/ILAstDebugInfoWriter.cs` | 新接口：`void WriteStatement(Interval ilRange, int textStart, int textEnd)` |
| `IL/ILAstWritingOptions.cs` | `bool WriteCodeBracesRanges`（默认 false）、`ILAstDebugInfoWriter DebugInfoWriter`（默认 null） |
| `IL/Instructions/Block.cs` | 写指令时向 sink 报告文本 span；启用开关时对 `{`/`}` 发括号对 |
| `IL/Instructions/BlockContainer.cs` | 对容器发括号对（Loop/While/DoWhile/For→LoopBraces，Switch→SwitchBraces） |
| `IL/Instructions/ILFunction.cs` | 对方法体发 `MethodBraces` |
| `Disassembler/ReflectionDisassembler.cs` | `Func<IMemberDefinition, IEnumerable<string>> GetXmlDocComments`（成员头前写 `///` 行）与 `Func<IMemberDefinition, string> GetTokenAndRvaComment`（写 `// Token/RVA/File Offset` 行），均对齐 2.3 的 `DisassemblerOptions`；在 `DisassembleMethod/Field/Property/Event/Type` 调用 |
| `Disassembler/MethodBodyDisassembler.cs` | `Func<MethodDefinition, IEnumerable<string>> GetBodyHeaderComments`（替换默认的 `// Method begins at RVA` / `// Code size` 两行）、`Func<MethodDefinition, string[]> GetLocalNames`（`.locals` 名 + 指令操作数中的局部名）、`UseDnSpyLayout`/`OperandAlignment`（2.3 的操作数对齐列宽 10 + 大写 IL 标签）——均默认关闭 |
| `ILSpy/TextView/AvalonEditTextOutput.cs` | 实现新接口成员（ILSpy 应用工程，当前不在 dnSpy 构建集内） |

`NextPosition`/`AddCodeBracesRange` 是接口新增成员，因此所有 `ITextOutput` 实现都要实现
（引擎内 `PlainTextOutput`、`AvalonEditTextOutput`，扩展内 `DecompilerOutputTextOutput`）。

## 4. 扩展侧新增（`dnSpy.Decompiler.ILSpy32`）

| 文件 | 内容 |
|---|---|
| `ILAst/ILAstDecompiler32.cs` | `ILAst (ILSpy 3.2)` 语言：`ILReader` 构建 `ILFunction` → 可选跑 transform → 经共享桥接写 ILAst（带括号对与 debug info） |
| `ILAst/ILAstDecompilerSettings32.cs` | 选项：Run IL transforms / Show IL ranges / Use logic operation sugar / Use field sugar |
| `Mixed/ILWithCSharpDecompiler32.cs` | `IL with C# (ILSpy 3.2)` 语言：反汇编 + 逐指令 C# 源行叠加 |
| `Mixed/DecompilerOutputImpl32.cs` | 捕获单方法 C# 文本 + `MethodDebugInfo`（端口自 2.3） |
| `Mixed/SourceStatementProvider32.cs` | IL offset → 源行文本（端口自 2.3） |
| `Mixed/MixedDisassemblyComments32.cs` | 2.3 的反汇编装饰：body 头注释、局部名、`/* 0x… 字节 */`、`/* (line,col) 位置 */`、async 注释——全部从 dnlib 侧取（PDB 在 dnlib 加载） |
| `CSharp/DecompilerCreator.cs` | 注册两个新语言 |
| `Common/XmlDocComments32.cs` | XML 文档行提供者（端口自 2.3 `ILDecompiler.GetXmlDocComments`）：cecil 成员按 token 回解 dnlib → `XmlDocLoader` → 按行拆分并剥公共缩进 |
| `Common/TokenComment32.cs` | `// Token: 0x... RID: ... RVA: ... File Offset: ...` 行（端口自 2.3 `ReflectionDisassembler.AddComment`）：dnlib 侧取 RVA / 文件偏移 |
| `IL/ILDecompiler32.cs` | 桥接类实现新接口成员；新增 `TrackDelimiters` 开关（ILAst 关闭，避免括号对重复）；`BodyTrackingMethodBodyDisassembler` 去掉 `sealed` 以便 Mixed 继承 |

Mixed 的逐指令叠加用 `MixedMethodBodyDisassembler`（继承 `BodyTrackingMethodBodyDisassembler`，
因此同时保留 IL 视图的 per-instruction debug info）：`WriteInstruction` 在写指令前插入
`// <源行>` 注释与 `^` 下划线，然后调用基类。

## 5. 与 2.3 的差异

- **语言数量**：2.3 ILAst 是「每个优化步骤一个语言」（`#if DEBUG`，约 20 个下拉项）；
  3.2 版是**单个语言**，用 `Run IL transforms` 开关对比原始/优化输出。原因：3.2 暴露的是
  transform 列表而非编号的 optimizer 阶段。
- **注册条件**：不再 `#if DEBUG`，发布版也可用。
- **debug info 粒度**：2.3 由 `ILNode.WriteTo` 按表达式产生语句；3.2 版由 `Block.WriteTo`
  按指令产生语句（粒度更粗，但足够调试器高亮指令）。
- **Mixed 的 XML 文档注释**：已对齐（引擎新增 `GetXmlDocComments` 钩子，Mixed 无条件启用；`///` 与正文着色经 `IDecompilerOutput.WriteXmlDoc`，与 2.3 一致）。
- **Mixed 的 Token/RVA 注释**：已对齐（引擎 `GetTokenAndRvaComment` 钩子 + `Common/TokenComment32`，无条件启用）。
- **Mixed 的 IL 字节与 PDB 信息**：已对齐（`/* 0x… 字节 */`、`/* (line,col)-(line,col) url */`、async `/* Catch Handler / Yield / Resume */` 均由 `MixedDisassemblyComments32` 从 dnlib 产生）。
- **C# 源行叠加的粒度**：3.2 的叠加行比 2.3 多——3.2 的 C# 反编译器产出的语句级 sequence point 更细（引擎差异，非接线缺口；其余部分已与 2.3 逐字一致）。
- **括号对顺序**：与 2.3 一致，采用「写闭括号时登记」的后序（嵌套括号对会先登记内层）。

## 6. 验证

- `dnSpy.Console -l "ILAst (ILSpy 3.2)" -t <type> <asm>`：ILAst 文本正确，含
  `BlockContainer (for)`、`if (...)`、`logic.or/logic.not` 等 transform 产物。
- `dnSpy.Console -l "IL with C# (ILSpy 3.2)" --md <token> <asm>`：C# 源行与 `^` 下划线
  正确叠加在指令上方。
- `baseline/probe ilast-verify <asm> <token> <dnspyBin>`：新增的验证模式（反射创建内部语言
  类 + 记录型 `IDecompilerOutput`），断言括号对数量 > 0 且 debug info 存在。实测
  `CompactILSpans`：`braces=11`（含 `LoopBraces`）、`statements=30`。
- `baseline/probe mixed-verify ...`：Mixed 专用验证模式，记录颜色；实测 XML 文档注释
  9 行，`///` 为 `XmlDocCommentDelimiter`、正文为 `XmlDocCommentText/Name/AttributeValue`。
- 与 2.3 逐字对比：同一成员（`--md 0x0600024D`）的 Mixed 输出中，除 C# 叠加行外**全部逐字一致**——头部（XML 文档 + Token/RVA）、body 头（Header/Code Size/LocalVarSig）、`.locals` 名、每条指令的 `/* 0x… 字节 */` 前缀与操作数对齐/大写标签、`/* (line,col) 位置 */` 注释。全文件 diff 从 430 行降至 150 行，剩余全为 3.2 多出的叠加行。
- `baseline/probe engine-tests ...`：24/24 全部 PASS（无回归）。
- 构建：引擎 `netstandard2.0` 0 错 0 警；扩展 `net10.0-windows` 0 错。

## 7. 后续可选

- 把括号对顺序改为先序（需要 writer 在写 `{` 前登记，会改变 `AddCodeBracesRange` 语义）。
- ILAst 的「第 N 步」语言族（若需要逐 transform 对比，可按 `GetILTransforms()` 索引生成）。
- Mixed 的 `.try`/`.catch` 块结构（3.2 引擎写成 `.try` + `{`，2.3 写成带偏移的 `.try IL_x-IL_y`）。
- IL 视图的 XML 文档 / Token 注释 / 2.3 布局（钩子已就绪，只需接线）。
