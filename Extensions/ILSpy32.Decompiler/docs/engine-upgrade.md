# 引擎升级手册（3.2 → 下一个 ILSpy 版本）

状态：**手册**（2026-11-16 建立）。目的：把「换引擎版本」从一次性手工工程变成可重复流程。

## 0. 现状与硬伤

- 3.2 引擎子模块 `Extensions/ILSpy32.Decompiler/ICSharpCode.Decompiler/` pin 在 `v3.2.0` 之上，
  dnSpy 侧改动**已提交进子模块**：`git log v3.2.0..HEAD` 共 8 个提交（见下表），
  主仓库通过 submodule 指针 pin 住（当前 `48953af`）。
- **硬伤**：这 8 个提交**不在任何远端分支上**（`git branch -r --contains <sha>` 为空），
  子模块还是 shallow（本地只有 `v3.2.0` 一个标签）。因此
  「`git submodule update` 即可拿到完整补丁状态」**对新克隆不成立**——新克隆只会拿到
  pristine `v3.2.0`，缺全部 dnSpy 改动。
- `engine.patch` / `apply-engine-patch.ps1` 是**过渡产物**：patch 只是 pristine `v3.2.0` 的引导
  （含构建配置与 `IntroduceUsingDeclarations` 的一小部分），脚本仅在 HEAD==`v3.2.0` 时应用。

**升级前必须先解决可克隆性**（三选一）：

| 方案 | 做法 | 代价 |
|---|---|---|
| A（推荐） | 把子模块的 dnSpy 提交推到 fork 分支（如 `Ayinyue/ILSpy` 的 `dnspy32`），`.gitmodules` 加 `branch = dnspy32`，并 `git fetch --unshallow` | 需要远端权限；一次配置长期受益 |
| B | 改为 vendored 源码：把引擎源码复制进仓库（不用 submodule），patch 队列用 `git format-patch` 管理 | 仓库变大；丢失上游 diff 视图 |
| C | 保留 submodule，但把 8 个提交导出为 `patches/*.patch` 入库，并写脚本在 pristine 标签上按序 `git am` | 每次升级要维护 patch 队列 |

## 1. 现有 8 个提交（按主题）

| 提交 | 主题 | 类别 | 升级时 |
|---|---|---|---|
| `4f5efb6` | async/yield 双布局、using/属性顺序、token 引用、测试环境 | 反编译逻辑 + 构建配置 | **逐条重新裁决**（上游可能已改同文件） |
| `f92581d` | `ITextOutput.NextPosition`/`AddCodeBracesRange`、`ILAstWritingOptions` 开关、`ILAstDebugInfoWriter`、`CodeBracesRangeFlags` | 引擎 public API | 新增成员会波及所有 `ITextOutput` 实现，注意冲突 |
| `5aa02d5` | `ReflectionDisassembler.GetXmlDocComments` | 反汇编器钩子 | 小组装，易冲突 |
| `c84f041` | `ReflectionDisassembler.GetTokenAndRvaComment` | 反汇编器钩子 | 同上 |
| `dfc1b97` | 局部名、2.3 body 头、`UseDnSpyLayout` | 反汇编器 | 落在高频文件，易冲突 |
| `70c90c9` | 属性/事件短式 `full` 参数 | 反汇编器 | 小 |
| `c79ce0e` | ShowILComments/SortMembers/MaxStringLength/HexadecimalNumbers + `NumberFormatter` + `CecilMemberComparer` | 反汇编器 | 新增文件大概率干净；钩子部分易冲突 |
| `48953af` | 类型级逐字对齐（参数特性缩进、访问器注释、`.locals` 转义） | 反汇编器 | 小 |

**冲突热点**：`ICSharpCode.Decompiler.csproj`（构建配置 + 3 个提交）、`NuGet.config`、
`ReflectionDisassembler.cs`（5 个提交）、`MethodBodyDisassembler.cs` / `DisassemblerHelpers.cs`
（各 2 个）、`ITextOutput.cs`（接口新增成员）、`IntroduceUsingDeclarations.cs`（patch 与提交双改）。

**构建配置与 `v3.2.0` 树强绑定，必须重生成**：目标框架/程序集名、`NuGet.config`、cecil API 适配，
以及**机器相关的 cecil HintPath**（`engine.patch` 里指向 `D:\Codebase\ILSPY\...`，升级前参数化）。

## 2. 升级步骤（清单）

1. **备份**：主仓库打 tag；记录当前 submodule 指针。
2. **取新标签**：`git -C Extensions/ILSpy32.Decompiler/ICSharpCode.Decompiler fetch --unshallow --tags`，
   基于新标签建分支 `dnspy32-<ver>`。
3. **重放改动**：`git cherry-pick <8 个提交>`；对每个冲突按第 1 节标注的类别裁决
   （构建配置→重新生成；逻辑补丁→逐条比对上游；钩子→重新装配）。
4. **重生成 patch**（可选）：对 pristine 新标签 `git diff <tag>..HEAD -- <构建配置文件>` → `engine.patch`；
   把 cecil HintPath 改为参数/相对路径。
5. **重建**：`build.ps1 -NoMsbuild -c Release`（注意：sln 构建会把引擎编成 Debug，
   `build.ps1` 已显式以 Release 重建引擎与扩展；见 `Engine-Coverage-Audit.md` §8.5）。
6. **验证**（见第 3 节）——**全绿才算升级完成**。
7. **更新文档**：本文件、`Engine-Coverage-Audit.md`、`README.md` 的提交表。

## 3. 验收清单（probe / 对拍）

`Extensions/ILSpy32.Decompiler/baseline/probe/m3probe.dll`：

| 模式 | 命令 | 判据 |
|---|---|---|
| engine-tests | `m3probe.dll engine-tests <SpTestAssembly.dll> <dnspyBin>` | `ENGINE-TESTS: ALL PASS` |
| ilprint-diff | `m3probe.dll ilprint-diff <asm> <dnspyBin> <o23> <o32>` | `diffSamples=0`（M4 后无 2.3 程序集 → SKIP，需从归档恢复对比） |
| ilast-verify | `m3probe.dll ilast-verify <asm> <token> <dnspyBin>` | `braces>0` 且 `debugInfo` 存在 |
| mixed-verify | `m3probe.dll mixed-verify <asm> <token> <dnspyBin>` | 叠加行数与 `debugInfo` 符合预期 |
| settings-wiring | `m3probe.dll settings-wiring <dnspyBin>` | 8/8 `True` |
| bench | `m3probe.dll bench ...` | 与升级前基线对比不回退 |

对拍（2.3 归档可用时）：`dnSpy.Console -l "IL"` vs `-l "IL (ILSpy 3.2)"`，`--md`/`-t` 逐字 diff = 0。

## 4. 建议新增的验证资产（尚未做）

- `verify-all` 编排脚本：一条命令跑完上表并归档报告。
- 黄金基线入库（`baseline/out/` 目前被 gitignore）。
- 把 `run-regression*.cmd` 接进 CI（`.github/workflows/build.yml` 目前只 build）。
- 统一基线数字来源（`run-regression.cmd` / audit / VB-Action-Plan 三处数字漂移）。
