# ILSpy 3.2 引擎（dnSpy 专用构建）

本目录是 `icsharpcode/ILSpy` 的 submodule，pin 在 **v3.2.0**（2018-07-01），
用于 dnSpy 的 3.2 引擎演进（见仓库根目录 `引擎3.2演进方案.md`）。

## 为什么需要补丁

上游 v3.2.0 的工程配置已不可直接构建：
- `NuGet.config` 只剩失效的 AppVeyor feed（404）
- `Mono.Cecil 0.10.0` 等依赖版本过旧，且本机 nuget.org 不可达
- 现代 cecil 把泛型约束改成 `GenericParameterConstraint` 包装
- 产物程序集名与 dnSpy 现有引擎 `ICSharpCode.Decompiler` 冲突（强命名）

`engine.patch` 只承载**构建配置**改动（`NuGet.config`、csproj 目标框架/程序集名/依赖版本、
cecil API 适配），用于在** pristine v3.2.0** 上快速重建可构建状态。
引擎逻辑补丁（async/yield 双布局、using/属性顺序、token 引用、测试环境等）已**提交进本子模块的
git 历史**（`git log` 可见），主仓库通过 submodule 指针 pin 住，`git submodule update`
即可拿到完整补丁状态；`apply-engine-patch.ps1` 仅在 HEAD 恰为 `v3.2.0` 标签时才打 patch。

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

## dnSpy 视图所需的 public API（ILAst / Mixed）

3.2 的 `ICSharpCode.Decompiler.IL` 已是 2.3 `ILAst` 的后继实现（`ILReader` → `ILFunction`，
`CSharpDecompiler.GetILTransforms()` 跑优化，`ILInstruction.WriteTo(ITextOutput, ILAstWritingOptions)`
输出 ILAst 文本），因此 ILAst/Mixed 不需要移植 2.3 的 ILAst 管线，只需补齐 dnSpy 侧消费所需的
API（均为增量、默认无行为变化）：

- `Output/CodeBracesRangeFlags.cs`：括号对标志枚举，数值与 `dnSpy.Contracts.Decompiler.CodeBracesRangeFlags` 一致；
- `Output/ITextOutput.cs`：新增 `int NextPosition { get; }` 与
  `void AddCodeBracesRange(int start, int end, CodeBracesRangeFlags flags)`；
- `IL/ILAstWritingOptions.cs`：新增 `WriteCodeBracesRanges`（默认 false）与 `DebugInfoWriter`；
- `IL/ILAstDebugInfoWriter.cs`：`WriteStatement(Interval, int textStart, int textEnd)`，
  由 `Block.WriteTo` 逐指令回调；
- `IL/Instructions/{Block,BlockContainer,ILFunction}.cs`：启用开关时输出括号对
  （容器映射 Loop/Switch braces，方法体映射 MethodBraces）。

详见 `docs/ILAst-Mixed-3.2.md`。

## 产物

```
ICSharpCode.Decompiler/ICSharpCode.Decompiler/bin/Release/netstandard2.0/ILSpy32.Decompiler.dll
```

## 注意

- 本机 `api.nuget.org` 不可达，依赖来自 `~/.nuget/packages` 缓存。若缓存缺失，需先补齐。
- cecil 使用本地 `D:\Codebase\ILSPY\...\Mono.Cecil.dll`（现代版本）。若要回到 cecil 0.10.0，
  需重新评估那 4 处 API 适配是否仍需要。
- 补丁只在 submodule 处于 pristine `v3.2.0` 时应用（脚本按 HEAD 判断）；已打补丁的状态由
  submodule 自身的提交承载，升级标签后如需重建补丁请重新生成。
- `IntroduceUsingDeclarations` 的健壮性修复是 **dnSpy 桥接场景必需的**：
  经 dnlib→cecil 桥接后，部分方法体引用的外部命名空间可能不在
  `RequiredNamespacesSuperset` 中，原 `Debug.Assert` 会直接终止进程。
  跳过这些命名空间后，类型仍以全限定名正确输出。
