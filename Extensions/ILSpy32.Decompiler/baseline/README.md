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

选项：

| 选项 | 说明 |
|---|---|
| `--lang cs\|vb` | 选择语言 |
| `--all` | 含 internal / 编译器生成类型（默认只取 public 非 enum/interface）|
| `--max N` | 只处理前 N 个类型（冒烟）|
| `--dnspy-dir <dir>` | 显式指定 dnSpy 输出目录（默认取 `--asm` 所在目录）|

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
| dnSpy.Contracts.Logic.dll（84 public） | VB | 84 ok | 38 ok / 46 degraded | **71 ok / 13 degraded** |
| dnSpy.Contracts.Logic.dll（84 public） | C# | 84 ok | 44 ok / 40 degraded | **84 ok / 0 degraded** |
| dnSpy.AsmEditor.x.dll（1278 all） | VB | — | 659 ok / 619 degraded | **983 ok / 295 degraded** |
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

## 剩余缺口（VB，AsmEditor 1278 类型，295 例）

| 次数 | 原因 |
|---|---|
| 173 | `Exception: Invalid value for UnaryOperatorType`（`NullConditional` 等未映射）|
| 63 | `ArgumentException: Node is already used in another tree` |
| 36 | `VB output for out-var declarations is not implemented` |
| 23 | `NotImplementedException`（查询表达式 / goto case 等）|

C# 侧在当前样本上已无降级。

## 局限

- 只验证「是否产出真实反编译文本」，**未做语法/编译验证**（编译需要完整引用集，属后续增量）。
- `compare` 的逐行差异只用于观察，不代表质量优劣；`identical=0` 是常态（两代引擎排版不同）。
