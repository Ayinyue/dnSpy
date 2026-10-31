# CodeConverter 集成评估报告

> 日期：2026-10-31
> 分支：feature/ilspy32
> 目的：评估是否用 [CodeConverter](https://github.com/icsharpcode/CodeConverter)（Roslyn 源码级 C#↔VB 转换器）参考/替换自研 `CSharpToVBConverter32`。

## 1. 背景

dnSpy 的 ILSpy 3.2 VB 反编译器链路：

```
IL (dnlib) → dnlib→cecil 桥接 → ILSpy 3.2 引擎反编译 → C# SyntaxTree (ILSpy AST)
    → CSharpToVBConverter32（自研，2735 行，NRefactory VB AST）→ OutputVisitor → VB 文本
```

自研转换器基于 **ILSpy 3.2 C# AST → NRefactory VB AST**，是 ILSpy 3.2 上游 `ICSharpCode.ILSpy.VB` 转换器的移植/适配。

CodeConverter 是 **Roslyn 源码级转换器**：解析 C# 源码文本 → 构造 Roslyn 编译与语义模型 → `CSharpSyntaxVisitor` 转换为 VB 语法树 → 输出 VB 源码。

## 2. 两条技术路线对比

| 维度 | 自研 CSharpToVBConverter32 | CodeConverter (ICSharpCode.CodeConverter) |
|---|---|---|
| 输入 | ILSpy 3.2 C# AST（带 Cecil 注解） | C# 源码文本（Roslyn 解析） |
| 语义模型 | 无完整编译上下文，靠 AST 注解 | 完整 Roslyn Compilation/SemanticModel |
| 转换机制 | 手写 IAstVisitor，约 2700 行 | 成熟 NodesVisitor（约 2000 行）+ 大量辅助 |
| 维护状态 | 我们自研，需自行维护 | icsharpcode 活跃维护，NuGet 发布 |
| 质量 | 可用，但 VB 地道性有限 | 更地道的 VB 输出 |
| 集成成本 | 已集成 | 需新增"反编译 C# 文本 → Roslyn → CodeConverter"步骤 |

## 3. 量化对比（CodeConverter 转换 ILSpy 3.2 反编译产物）

| 测试集 | 类型数 | 转换成功 | 失败 |
|---|---|---|---|
| dnSpy.Contracts.Logic.dll（public） | 84 | **84** | 0 |
| dnSpy.dll（public，实际 8 个） | 8 | **8** | 0 |
| dnSpy.dll（all，前 30） | 30 | **30** | 0 |
| dnSpy.dll（all，前 200） | 200 | **200** | 0 |
| CatchShapes.dll（含全部 catch 形态） | 2 | **2** | 0 |
| IndexerShapes.dll（含多形态索引器） | 2 | **2** | 0 |

**结论：CodeConverter 对 ILSpy 3.2 反编译出的 C# 文本有接近 100% 的转换成功率**（样本 326 类型，0 失败）。

> 注：ILSpy 3.2 引擎自身对个别 internal 类型（如含迭代器的 `get_Spans`）反编译失败，该问题与 CodeConverter 无关，自研转换器同样受影响。

## 4. 质量对比（CatchShapes 样例）

### 无变量名 catch

```vb
' 自研 CSharpToVBConverter32
Catch ex1 As System.IO.IOException

' CodeConverter
Catch __unusedIOException1__ As IOException
```

### When 过滤器

```vb
' 自研
Catch ex2 As System.IO.IOException When DateTime.Now.Day > 1

' CodeConverter
Catch __unusedIOException1__ As IOException When Date.Now.Day > 1
```

### 全限定名

```vb
' 自研：System.IO.File.OpenRead("x")
' CodeConverter：File.OpenRead("x")（自动 Imports System.IO）
```

## 5. 质量对比（IndexerShapes 样例）

```vb
' 自研
Public Default Property Item(index As Integer) As Integer
Return CInt((key + If(flag, 1, 0)))

' CodeConverter
Default Public Property Item(index As Integer) As Integer
Return key + If(flag, 1, 0)   ' 类型推断消除显式 CInt
```

## 6. 复杂类型转换样例（dnSpy.AsmEditor.Hex.PE.ImageCor20HeaderVM）

CodeConverter 正确输出：

```vb
Namespace dnSpy.AsmEditor.Hex.PE
    Friend NotInheritable Class ImageCor20HeaderVM
        Inherits HexVM
        Private ReadOnly hexFieldsField As HexField()
        Public Overrides ReadOnly Property Name As String
        Public ReadOnly Property CbVM As UInt32HexField
        ...
        Public Sub New(buffer As dnSpy.Contracts.Hex.HexBuffer, cor20 As dnSpy.Contracts.Hex.Files.DotNet.DotNetCor20Data)
            MyBase.New(cor20.Span)
            ...
        End Sub
    End Class
End Namespace
```

- `internal` → `Friend` ✓
- `override` → `Overrides` ✓
- `base` 调用 → `MyBase.New(...)` ✓
- 泛型 `IEnumerable(Of HexField)` ✓
- 数组初始化 `New HexField(18) {...}` ✓

## 7. 集成方案分析

### 方案 A：替换自研转换器（CodeConverter 作为 VB 输出后端）

```
IL (dnlib) → 桥接 → ILSpy 3.2 → C# SyntaxTree → C# 文本 → Roslyn ParseText
    → CodeConverter (CSToVBConversion) → VB 文本
```

**优点**：
- 消除 2700 行自研转换器的维护负担
- VB 输出更地道（自动 Imports、类型推断、VB 惯用命名）
- 使用成熟、活跃维护的转换器

**缺点**：
- 每次反编译多一步 Roslyn 解析 + CodeConverter 转换（性能开销，约 2-3 秒/类型）
- 反编译代码可能含 CodeConverter 不支持的构造（goto、指针、dynamic 等），需处理
- CodeConverter 是 netstandard2.0 库，依赖 Roslyn 4.14，需评估与 dnSpy 的 Roslyn 版本兼容
- 丢失 ILSpy AST 的 Cecil 语义注解（当前转换器直接利用），可能导致某些 VB 输出质量下降

### 方案 B：参考 CodeConverter 改进自研转换器

**优点**：
- 保持现有架构与性能
- 针对性改进 catch 命名、VB 惯用表达等

**缺点**：
- 仍需长期维护自研转换器
- 无法获得 CodeConverter 的完整语义分析能力

**已落地改进**（2026-10-31）：
- **catch 隐式变量命名**：从 `exN` 改为 `__unused{TypeShortName}{n}__`（如
  `__unusedIOException1__`），计数器在每个 try 语句内重置——完全对齐 CodeConverter
  方案。验证：`dnSpy.AsmEditor.x.dll` 全量回归 `ok=1278 failed=0`，CatchShapes 六种
  catch 形态输出全部为合法 VB。
- **`DateTime`→`Date` 别名：验证不可行并回滚**。反编译生成的类型节点不带
  Cecil `TypeReference` 注解（`ConvertType` 只在显式类型转换路径添加注解），转换器
  无法安全识别 `System.DateTime` 并缩写为 `Date`。该结论由
  `vbverify/testsrc/DateTimeShapes.cs` 固化为回归锁（输出仍为 `DateTime`/`DateTime?`）。

### 方案 C：混合（推荐候选）

- 默认使用自研转换器（快、无额外依赖）
- 对自研转换器失败或质量不佳的类型，可选用 CodeConverter 兜底

## 8. 建议

基于 326 类型 0 失败的量化证据，**方案 A（CodeConverter 作为 VB 输出后端）技术可行**，且能显著提升 VB 输出质量。但需先解决：
1. 性能：批量反编译时 CodeConverter 转换开销（可考虑并行/缓存）
2. Roslyn 版本兼容：CodeConverter 依赖 Roslyn 4.14，dnSpy 使用 Roslyn 5.9（可共存）
3. 特殊构造：反编译代码中的 `goto`/指针/`dynamic` 等测试

**建议下一步**：实现一个最小集成原型（VBDecompiler32 可选使用 CodeConverter 后端），对 dnSpy.dll 全部 3266 类型做完整回归对比，再决定是否默认切换。
