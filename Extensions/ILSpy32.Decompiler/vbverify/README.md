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

| 程序集 | 类型数 | VB | C# |
|---|---|---|---|
| dnSpy.Contracts.Logic.dll | 84 public | 84/84 | — |
| dnSpy.dll | 3266 all | 3266/3266 | — |
| dnSpy.AsmEditor.x.dll | 1278 all | 1278/1278 | 1278/1278 |

期间发现并修复了引擎级 bug：`IntroduceUsingDeclarations` 在 dnlib→cecil 桥接场景下
对不在 `RequiredNamespacesSuperset` 中的命名空间触发 `Debug.Assert` 崩溃。
修复见 `engine.patch`（追加该文件的 diff）。