# baseline — 双引擎输出基线对拍

用于 Phase 2「正确性基线」：对同一程序集、同一批类型，分别用现有 2.3 引擎与新的
3.2 引擎反编译，再逐类型比对输出。

## 为什么需要它（重要）

3.2 的两个反编译器都会**在内部吞掉异常并写一行注释**：

```
' ILSpy 3.2 VB decompilation failed: <ExceptionType>: <message>
' ILSpy 3.2 VB conversion failed: <ExceptionType>: <message>
// ILSpy 3.2 decompilation failed: <ExceptionType>: <message>
```

也就是说，`Decompile()` 不抛异常 **不等于** 输出了正确代码。此前 vbverify 的
`ok=1278 failed=0` 只证明「没有异常逃逸」，无法发现这类降级输出。本工具把
`degraded` 单独统计，避免指标失真。

## 用法

```bash
cd Extensions/ILSpy32.Decompiler/baseline

# 1) 转储：--impl core = 2.3 引擎，--impl ilspy32 = 3.2 引擎
dotnet run -c Release -- dump \
    --asm ../../../dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.Contracts.Logic.dll \
    --impl core --lang vb --out out/core-vb-contracts

dotnet run -c Release -- dump \
    --asm ../../../dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.Contracts.Logic.dll \
    --impl ilspy32 --lang vb --out out/v32-vb-contracts

# 2) 对拍
dotnet run -c Release -- compare --a out/core-vb-contracts --b out/v32-vb-contracts \
    --out out/report-vb-contracts.md
```

# 语法验证（Roslyn 解析，只查语法，不做语义/引用解析）
dotnet run -c Release -- validate --dir out/v32-vb-contracts --lang vb \n    --out out/syntax-vb-contracts.md

# 语义编译（带引用）
dotnet run -c Release -- compile --dir out/v32-vb-contracts --lang vb \
    --refs ../../../dnSpy/dnSpy/bin/Release/net10.0-windows \
    --out out/compile-vb-contracts.md

# 单个类型的全部诊断（排查用）
dotnet run -c Release -- validate --dir out/v32-vb-asm --lang vb \n    --file "dnSpy.AsmEditor.Compiler.ModulePatcher"
```

选项：

| 选项 | 说明 |
|---|---|
| `--lang cs\|vb` | 选择语言 |
| `--all` | 含 internal / 编译器生成类型（默认只取 public 非 enum/interface）|
| `--max N` | 只处理前 N 个类型（冒烟）|
| `--dnspy-dir <dir>` | 显式指定 dnSpy 输出目录（默认取 `--asm` 所在目录）|
| `validate --dir <dir>` | 对某次 dump 的结果做语法验证 |
| `validate --file <name>` | 只验证一个类型并打印全部诊断 |
| `compile --refs <dir>` | 语义编译（需要引用目录），可访问性错误单列为 noise |
| `--type <name>` | dump 只处理名字匹配的类型（调试单个类型用）|

输出：每个类型一个 `.txt`、`summary.tsv`（类型 / 状态 / 长度 / 文件 / 原因）、
`_meta.txt`（引擎与程序集信息）；`compare` 生成 markdown 报告。

### 状态定义

| 状态 | 含义 |
|---|---|
| `ok` | 无异常，且输出中没有失败注释 |
| `degraded` | 未抛异常，但输出是失败注释（**这是最有价值的指标**）|
| `fail` | `Decompile()` 抛出异常 |

## 已验证结果（2026-10-31，本机实测）

| 程序集 | 语言 | 2.3 引擎 | 3.2 修复前 | 3.2 修复后 |
|---|---|---|---|---|
| dnSpy.Contracts.Logic.dll（84 public） | VB | 84 ok | 38 ok / 46 degraded | **84 ok / 0 degraded** |
| dnSpy.Contracts.Logic.dll（84 public） | C# | 84 ok | 44 ok / 40 degraded | **84 ok / 0 degraded** |
| dnSpy.AsmEditor.x.dll（1278 all） | VB | — | 659 ok / 619 degraded | **1278 ok / 0 degraded** |
| dnSpy.AsmEditor.x.dll（1278 all） | C# | — | — | **1278 ok / 0 degraded** |

> 注意：修复前的数字才是「真实正确率」，`vbverify` 此前报的 1278/1278 是异常计数。

## 本次基线暴露并修复的两个缺陷

### 1. 桥接的元数据 token 错位（影响两语言，严重）

`DnlibCecilBridge` 用 `module.Write(stream)` 序列化 dnlib 模块后交给 cecil。
dnlib 默认写入会**重排 TypeDef 表**（嵌套类型被排到顶层类型之后），而扩展随后用
`LookupToken(dnlib.MDToken.Raw)` 找回 cecil 对象——token 全部错位。

实测证据（修复前）：

```
dnlib token 0x0200001E = dnSpy.Contracts.Decompiler.AsyncMethodDebugInfo
cecil  0x0200001E      = dnSpy.Contracts.Utilities.NuGetPackageLocator/NuGetPackageInfo
```

后果：顶层类型引用了嵌套类型时反编译失败（`Could not find type definition ...`）；
更危险的是**方法级反编译会静默取到错误的方法**（不报错，输出貌似合理）。

修复：写入时加 `MetadataFlags.PreserveRids`（无法保留时回退普通写入）。

### 2. 嵌套类型名分隔符不匹配

扩展把 cecil 的 `TypeDefinition.FullName`（嵌套用 `/`）直接传给
`new FullTypeName(string)`，而该构造函数只按反射风格 `+` 拆分。结果整个
`Outer/Inner` 被当成顶层名，`DecompileType` 查不到 → 抛 `Could not find type definition`。

修复：`Common.TypeNameHelper.ToReflectionName()` 统一把 `/` 换成 `+`（C# / VB 两条路径都用）。

## 后续又修复的两处 VB 缺口

### 3. `UnaryOperatorType` 未映射（173 例）

VB AST 没有空传播节点，无法表达 C# 的 `?.`。改成让引擎产出兼容形式：VB 的
`DecompilerSettings32(vbCompatibility: true)` 关闭 `EngineSettings.NullPropagation`，
引擎输出 `a != null ? a.b : null`，转换器已有的条件表达式路径即可处理。

由于 C# 与 VB 共用引擎缓存，缓存键改为「模块 + 设置」（
`CSharpDecompiler32.GetOrCreateEngine`），否则 VB 的设置会污染 C# 的输出。

### 4. `Node is already used in another tree`（63 例）

`VisitPropertyDeclaration` 为自动属性初始化器创建声明符时执行了
`v.Type = decl.ReturnType`，把已经挂在属性节点上的同一个 `AstType` 再挂给声明符，
违反 NRefactory 的单父约束。输出器在该声明符父节点为属性时本来就忽略 `Type`
（只写 `= 初始化值`），所以直接删除该赋值即可。

### 5. out-var 声明（90 例）

`M(out var x)` 在 VB 无对应语法。转换器现在把 `Dim x As T` 提升到当前语句之前，
参数位置改用 `x`（VB 按参数签名自动按引用传递，不需要 out/ref 关键字）。
ILSpy 3.2 写出的是**显式类型**而非 `var`，因此无需类型推断。

## 剩余缺口（VB，AsmEditor 1278 类型，43 例）

| 次数 | 原因 |
|---|---|
| 43 | `NotImplementedException`（查询表达式 / `goto case` / 指针相关等）|

C# 侧在当前样本上已无降级。

## 已知正确性局限（不体现为失败）

`ok` 只表示「产出了真实反编译文本」，**不代表语义一定正确**。

### 已修：表达式位置的赋值

```csharp
value = (dictionary[key] = new List<TypeDef>());   // C# 链式赋值
```
```vb
dictionary([key]) = New List(Of TypeDef)()
value = (dictionary([key]))                        ' 同一个实例
```

转换器把内层赋值提升为语句，并返回**左值的克隆**。返回右值会重复求值
（`New` 会创建两个对象），所以必须重读左值。

以下位置**刻意不提升**（保持原行为），因为提升会改变语义或作用域：

- 语句位置，含无花括号的 `if (c) x = y;`——提升会让赋值变成无条件执行
- 循环条件 / 迭代器（`while ((line = Read()) != null)`）——提升后只执行一次
- lambda 体内——会提升到外层方法
- 复合赋值（`x = (y += 1)`）与 `return` 位置——值不是右值，暂未处理

这些位置在当前样本（AsmEditor 1278 类型）中未出现；若后续样本命中，需要各自单独处理。

## 语法验证（2026-10-31 新增）

`validate` 用 Roslyn 对每个 `ok` 输出做**纯语法解析**。它与 `ok` 是互补的两个指标：
`ok` 只说明「产出了真实反编译文本」，`validate` 才能发现文本本身不合法。

首次运行（修复前）：AsmEditor VB **342 / 1278 个文件有语法错误（27.7%）**。

它一次性暴露了四类此前完全不可见的问题：

1. **属性命名参数用了 C# 语法**（`.Header = "..."`）。VB 要求 `Name := value` 且不能有
   前导点；错误写法让解析器跌进 XML 字面量模式，一个错误可级联出上百条诊断。
   修复：`VisitNamedExpression` 在父节点是 `CS.Attribute` 时改产 `NamedArgumentExpression`，
   同时补上 VB 输出器里 `VisitNamedArgumentExpression` 的桩实现。
2. **编译器生成名含 `<` `>`**（`<get_ModifiedObjects>d__15`）。方括号转义无效
   （`[<x>]` 仍非法，已用 VB 编译器实测），改为把非法字符替换成 `_`。
3. **`_` 不能作标识符**（`[_]` 同样非法，VB 视其为续行符），重命名为 `__`。
4. **`Char` 漏在关键字表外**，导致枚举成员/属性名未加方括号。

5. **语句以 `New` 开头**（`New T().M()`）——VB 会报语法错误，必须加 `Call`。
   为此在 VB AST 中新增了 `CallStatement` 节点（接口 + 两个输出器 + 转换器）。
6. **P/Invoke 方法被输出成无方法体的 `Function`**。根因是 `HasAttribute` 依赖
   MemberReference 注解，而反编译产物没有该注解 → 永远检测不到 `DllImport`，
   走不到已有的 `ExternalMethodDeclaration` 分支。改为回退到语法名后正确输出 `Declare ... Lib`。

7. **`++`/`--` 被输出成不存在的辅助函数**（`__PostIncrement(x)` 等）。VB 没有自增运算符：
   语句位置输出 `x += 1`；表达式位置前缀形式提升更新后重读目标，后缀形式先用
   `Dim __tmp_N = x` 保存旧值（输出器因此支持省略 `As` 的声明）；循环条件内保持原样，
   因为提升会改变执行次数。
8. **表达式体属性丢值**（`uint Start => start;`）。值在 `PropertyDeclaration.ExpressionBody`，
   而访问器体是空的 `return;`，既丢值又是 Get 内的非法语句。

修复后的语法错误文件数：

| 程序集 | 语言 | 语法错误文件 |
|---|---|---|
| AsmEditor（1278） | VB | **0** |
| dnSpy.dll（3266） | VB | **6（0.18%）** |
| Contracts.Logic（84） | VB | **1（1.2%）** |

### 剩余已知形态（未修）

- `New T(...).Method()` 单独成句（VB 需要 `Call`）。dnSpy.dll 的 BC30035 主要来自这里。
- 无方法体的 P/Invoke / abstract 声明缺少 `End Function`（应改写成 `Declare` 形式）。
- 超出范围的整数字面量（BC30036）。

这些都需要各自的转换策略，且当前已不影响「是否产出代码」。

## 语义编译验证（2026-10-31 新增）

`compile` 用 `--refs` 目录里的程序集 + 运行时程序集做真正的语义编译。
**注意**：反编译出的类型与它引用的内部成员不在同一程序集，可访问性错误属于
测量假阳性，报告里单列为 noise（`CS0122` / `BC30390` 等）。

Contracts.Logic（84 个 public 类型）的首次结果：**54 个文件编译失败**，真实问题包括：

| 次数 | 诊断 | 说明 |
|---|---|---|
| ~~18~~ **0** | ~~`BC30451 '__PostIncrement' is not declared`~~ | 已修：语句位置 `x += 1`，表达式位置用临时变量 |
| ~~12~~ **0** | ~~`BC30654 'Return' statement in a Function must return a value`~~ | 已修：表达式体属性从 `ExpressionBody` 重建 |
| 18 | `BC30258 Classes can inherit only from other classes` | `Inherits` 目标不是类型 |
| 15 | `BC30662 Attribute 'NullableAttribute' cannot be applied` | 特性目标不匹配 |
| 15 | `BC30456 'Contracts' is not a member of 'dnSpy.Contracts.DnSpy'` | 命名空间被同名类型遮蔽，需要 `Global.` |
| 12 | `BC30002 Type '_GetCustomAttributes_d__9' is not defined` | 重命名后的类型引用未全部一致 |
| 11 | `BC31409 'Equals' must be declared 'Overloads'` | 缺少 Overloads |

这些都是「语义正确性」层面的后续工作，不影响「能否产出代码」。

## 诊断开关## 诊断开关

失败注释默认只写一行（异常类型 + 消息）。设置 `DNSPY_ILSPY32_DIAG=1` 会把完整调用栈
追加进注释（单行 ` | ` 分隔），用于定位转换器内部失败——转换器吞掉所有异常，这是唯一的
栈来源。默认不生效，不影响正常输出。

## 局限

- 只验证「是否产出真实反编译文本」，**未做语法/编译验证**（编译需要完整引用集，属后续增量）。
- `compare` 的逐行差异只用于观察，不代表质量优劣；`identical=0` 是常态（两代引擎排版不同）。
