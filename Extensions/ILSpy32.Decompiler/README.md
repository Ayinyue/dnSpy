# ILSpy 3.2 引擎（dnSpy 专用构建）

本目录是 `icsharpcode/ILSpy` 的 submodule，pin 在 **v3.2.0**（2018-07-01），
用于 dnSpy 的 3.2 引擎演进（见仓库根目录 `引擎3.2演进方案.md`）。

## 为什么需要补丁

上游 v3.2.0 的工程配置已不可直接构建：
- `NuGet.config` 只剩失效的 AppVeyor feed（404）
- `Mono.Cecil 0.10.0` 等依赖版本过旧，且本机 nuget.org 不可达
- 现代 cecil 把泛型约束改成 `GenericParameterConstraint` 包装
- 产物程序集名与 dnSpy 现有引擎 `ICSharpCode.Decompiler` 冲突（强命名）

`engine.patch` 记录了全部改动（6 个文件）。

## 应用补丁

```powershell
# 在仓库根目录执行
powershell -NoProfile -ExecutionPolicy Bypass -File Extensions/ILSpy32.Decompiler/apply-engine-patch.ps1 -Build
```

或手动：

```bash
cd Extensions/ILSpy32.Decompiler/ICSharpCode.Decompiler
git apply ../engine.patch
dotnet build ICSharpCode.Decompiler/ICSharpCode.Decompiler.csproj -c Release
```

## 补丁内容

| 文件 | 改动 |
|---|---|
| `NuGet.config` | 源改为 nuget.org |
| `ICSharpCode.Decompiler.csproj` | 仅 `netstandard2.0`；`AssemblyName=ILSpy32.Decompiler`；包版本升级；cecil 改本地引用；关闭打包 |
| `TypeSystem/CecilLoader.cs` | 泛型约束取 `.ConstraintType` |
| `CSharp/CSharpDecompiler.cs` | 同上（2 处） |
| `Disassembler/ReflectionDisassembler.cs` | 同上 |
| `CSharp/Transforms/IntroduceUsingDeclarations.cs` | 跳过不在 `RequiredNamespacesSuperset` 中的命名空间，避免桥接场景下 `Debug.Assert` 崩溃 |

## 产物

```
ICSharpCode.Decompiler/ICSharpCode.Decompiler/bin/Release/netstandard2.0/ILSpy32.Decompiler.dll
```

## 注意

- 本机 `api.nuget.org` 不可达，依赖来自 `~/.nuget/packages` 缓存。若缓存缺失，需先补齐。
- cecil 使用本地 `D:\Codebase\ILSPY\...\Mono.Cecil.dll`（现代版本）。若要回到 cecil 0.10.0，
  需重新评估那 4 处 API 适配是否仍需要。
- 补丁是**可重复应用**的；升级 submodule 到其他标签时需重新生成。
- `IntroduceUsingDeclarations` 的健壮性修复是 **dnSpy 桥接场景必需的**：
  经 dnlib→cecil 桥接后，部分方法体引用的外部命名空间可能不在
  `RequiredNamespacesSuperset` 中，原 `Debug.Assert` 会直接终止进程。
  跳过这些命名空间后，类型仍以全限定名正确输出。
