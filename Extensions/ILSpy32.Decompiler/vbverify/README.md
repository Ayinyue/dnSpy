# vbverify — ILSpy 3.2 反编译器回归验证宿主

这个小工具通过反射加载 `dnSpy.Decompiler.ILSpy32.x.dll` 中的 `DecompilerCreator`，
枚举 C# / VB 两个 3.2 反编译器，并对指定程序集的类型做**批量反编译**，
用于回归验证引擎与桥接链路（dnlib → cecil → ILSpy 3.2 → C#/VB AST → 文本）。

## 用法

```bash
# 先构建 dnSpy 解决方案（确保 bin/Release/net10.0-windows 下产物齐全），再：
cd Extensions/ILSpy32.Decompiler/vbverify

# 默认：反编译 dnSpy.Contracts.Logic.dll 的全部 public 类型（VB）
dotnet run -c Release

# 指定程序集
dotnet run -c Release -- path/to/assembly.dll

# 反编译所有类型（含 internal / 编译器生成）
dotnet run -c Release -- path/to/assembly.dll --all

# 使用 C# 反编译器
dotnet run -c Release -- path/to/assembly.dll --cs
```

输出格式：

```
[RESULT] Visual Basic (ILSpy 3.2) batch: ok=3266 failed=0
```

任意类型反编译抛异常都会计入 `failed` 并打印前 10 条失败信息。

## 验证记录（2026-10-31）

> ⚠️ **口径说明**：下表的「类型数」只统计 `Decompile()` **未抛异常**的数量。
> 3.2 的反编译器会吞掉异常并写失败注释，因此该数字**不等于**正确输出数。
> 真实正确率（含降级识别）见 `../baseline/README.md`。

| 程序集 | 类型数 | VB | C# |
|---|---|---|---|
| dnSpy.Contracts.Logic.dll | 84 public | 84/84 | — |
| dnSpy.dll | 3266 all | 3266/3266 | — |
| dnSpy.AsmEditor.x.dll | 1278 all | 1278/1278 | 1278/1278 |

期间发现并修复了引擎级 bug：`IntroduceUsingDeclarations` 在 dnlib→cecil 桥接场景下
对不在 `RequiredNamespacesSuperset` 中的命名空间触发 `Debug.Assert` 崩溃。
修复见 `engine.patch`（追加该文件的 diff）。

### Catch 子句隐式变量修复（2026-10-31）

**现象**：C# 6 允许 `catch (Exception)` 省略变量名，ILSpy 3.2 引擎对这类子句产出空的
`VariableNameToken`；而 VB 语法要求 `Catch <var> As <Type>`。转换后
`CatchBlock.ExceptionVariable` 为 null，触发 NRefactory `OutputVisitor.VisitCatchBlock`
断言（`!catchBlock.ExceptionVariable.IsNull`），并使 AsmEditor 全量回归失败。

**修复**：`CSharpToVBConverter32.VisitCatchClause` 在 `ExceptionType` 非空但
`VariableNameToken` 为空时，生成隐式变量名 `__unused{TypeShortName}{n}__`
（如 `__unusedIOException1__`，`n` 在每个 try 语句内从 1 递增，`catchVariableCount`）。
命名方案参考 CodeConverter。裸 `catch`（无类型）不生成变量名，保持 VB 的 `Catch`。

**验证**：用 `testsrc/CatchShapes.cs`（覆盖命名 catch、无变量名 catch、`When` 过滤器、
多个连续无变量名 catch、混合、裸 catch 六种形态）编译后反编译，输出全部为合法 VB：
`Catch __unusedIOException1__ As ...` 且变量名在 try 语句内唯一；`dnSpy.AsmEditor.x.dll`
全量回归 `ok=1278 failed=0`。

### 索引器参数提取（2026-10-31）

**改进**：`ILSpyEnvironmentProvider32.GetParametersForProperty` 原返回空数组。现按
ILSpy 3.2 上游方式实现——读取 `PropertyDeclaration` 上的 Mono.Cecil `PropertyReference`
注解，用 `CSharpDecompiler.ConvertType` 把参数类型转成 C# AST，并映射 `out`/`ref` 修饰符。

**验证**：用 `testsrc/IndexerShapes.cs`（单参数、多参数、只读索引器）反编译，输出正确 VB：
`Public Default Property Item(index As Integer) As Integer`、
`Public ReadOnly Default Property Item(name As String, index As Integer) As Integer`。
`dnSpy.AsmEditor.x.dll` 全量回归 `ok=1278 failed=0`。

### DateTime→Date 类型别名：验证不可行（2026-10-31）

**尝试**：在 `CSharpToVBConverter32` 中把 `DateTime` 映射为 VB 内置别名 `Date`（更地道的 VB 输出）。

**结论**：不可行。反编译器生成的类型节点（`SimpleType`）**不带**
`Cecil TypeReference` 注解——`CSharpDecompiler.ConvertType` 仅在显式类型转换路径给
`AstType` 添加注解，而反编译生成的类型节点不走该路径。因此转换器无法从 AST 节点
得知其对应 BCL 类型，也就无法安全地把 `System.DateTime` 缩写成 `Date`。

**验证**：`testsrc/DateTimeShapes.cs`（普通属性、可空属性、全限定、方法参数/返回）
反编译输出仍为 `DateTime`/`DateTime?`，确认回滚正确。该测试资产保留为回归锁，
防止未来在缺少类型注解机制时误做该别名映射。
