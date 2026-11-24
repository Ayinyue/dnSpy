# 2.3 → 3.2 反编译后端迁移方案记录

> 状态：🟢 M3 已实施（2026-11-04，待手动全功能走查确认后进入 M4）。前置阅读：`VB-Dev-Plan.md`（里程碑 M2/M3/M4 的定义）。
> 2026-11-24 更新：审计 §8.112–§8.114 收尾（显示类容器缺陷、字面量显式转换修复、`checked((IntPtr)x)` 裁决），本文档状态与升级手册同步刷新。
> 本文是 M3「切换默认」与 M4「下线 2.3」的**实施方案记录**，勘察日期 2026-11-03。

## 1. 现状：两代引擎如何并存

dnSpy 通过 MEF 收集语言后端，与版本无关：

```
DecompilerService(ctor)
  ├─ [ImportMany] IDecompiler[] languages          // 直接导出的实现
  ├─ [ImportMany] IDecompilerCreator[] creators    // 工厂，Create() 返回若干实现
  └─ decompilers = 全部实现按 OrderUI 升序
     当前语言 = settings.LanguageGuid 经 Find(guid) 匹配
       （GenericGuid == guid || UniqueGuid == guid），匹配不到 → AllDecompilers.First()
```

切换点：`dnSpy/dnSpy/Decompiler/DecompilerService.cs`（用户改语言时写回
`DecompilerServiceSettings.LanguageGuid`，持久化在 dnSpy 设置里）。

### 注册清单（勘察结果）

| 语言 | 实现 | 所在项目 | UniqueGuid | OrderUI |
|---|---|---|---|---|
| C# | `CSharpDecompiler`（2.3 现役）| `dnSpy.Decompiler.ILSpy.Core/CSharp` | `LANGUAGE_CSHARP_ILSPY` | `CSHARP_ILSPY_ORDERUI` |
| C# | IL / ILAst / Mixed（2.3 附加视图）| 同上 Core | 各自常量 | 各自常量 |
| C# | `CSharpDecompiler32`（3.2 新）| `dnSpy.Decompiler.ILSpy32/CSharp/DecompilerCreator.cs` | `9C5E4C7A-…`（C# 侧）| 2.3 值 + 增量 |
| VB | `VBDecompiler`（2.3 现役）| `dnSpy.Decompiler.ILSpy.Core/VisualBasic/VBDecompiler.cs` | `LANGUAGE_VISUALBASIC_ILSPY` | `VISUALBASIC_ILSPY_ORDERUI` |
| VB | `VBDecompiler32`（3.2 新）| `dnSpy.Decompiler.ILSpy32/VisualBasic/VBDecompiler32.cs` | `9C5E4C7A-1F2B-4E6D-9A30-8B7D2E5F6A41` | `VISUALBASIC_ILSPY_ORDERUI + 0.5` |

**关键事实**
1. 3.2 的 C# 与 VB **都已注册**进 dnSpy（`CSharp/DecompilerCreator.cs` 一次 `yield` 两个），
   排在 2.3 对应实现**之后**——当前用户看到的默认语言仍是 2.3。
2. 3.2 与 2.3 的 VB 共享 `GenericGuid = LANGUAGE_VISUALBASIC`（同一「语言」的两个实现）。
3. 老用户设置里 `LanguageGuid` 存的是 **2.3 的 UniqueGuid**（`LANGUAGE_VISUALBASIC_ILSPY`），
   `Find` 按 UniqueGuid 精确命中 → 即使装了 3.2，老用户仍在 2.3 上。
4. `FindOrDefault` 的兜底是 `AllDecompilers.First()`（即 OrderUI 最小者）。

## 2. 目标与非目标

**目标**：dnSpy 默认反编译器切到 3.2（C# 与 VB），老设置平滑迁移，可回退；随后下线 2.3。
**非目标**：M3/M4 阶段不改转换逻辑（缺陷回到对应 Batch 修）；不新增设置页语言项。

## 3. 切换方案对比

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| **A 只调 OrderUI** | 3.2 的 `OrderUI` 改为小于 2.3 | 一行改动 | 老用户 `LanguageGuid` 仍精确命中 2.3，**对存量用户无效**；语言下拉顺序变化 |
| **B 设置迁移** | 启动时检测 `LanguageGuid == 2.3 的 UniqueGuid` → 改写为 3.2 的 UniqueGuid | 存量用户一次性切到 3.2，且**保留 2.3 可手动切回**（对比/兜底） | 需要一次性迁移代码；「切回 2.3 再重启」会被再次迁走（需迁移标记）|
| **C 移除 2.3 注册** | 删 2.3 的 creator/项目 | 状态最干净，`First()` 兜底自然命中 | 一步到位等于 M4，失去对比回退；破坏性变更 |

**推荐：B（M3）→ C（M4）两步走。**
- M3 用 B：迁移代码带一个设置标记（`LanguageGuidMigratedTo32`），用户手动切回 2.3 则尊重其选择，不再迁移。
- M4 用 C：删除 2.3 注册与项目；此时 `Find` 匹配不到旧 GUID，兜底 `First()` 命中 3.2（前提：3.2 `OrderUI` 已排到同语言首位）。
- C# 侧同规则处理：2.3 的 CSharp/IL/ILAst/Mixed 是四个不同 UniqueGuid；IL/ILAst/Mixed 是**附加视图**而非主语言，
  M3 只迁移 `LANGUAGE_CSHARP_ILSPY`，IL/ILAst/Mixed 留到 M4 一并处置。

## 4. 实施清单

### M2 质量对齐（前置，进行中）
- [ ] Batch 1~5 按 `VB-Dev-Plan.md` 推进；语法错误 5 / 0 / 1 → 0
- [ ] 切换决策点：realBadFiles 稳定低于 2.3 基线 2039，且连续两轮全量回归无回退

### M3 切换默认
- [x] **勘察补全**（2026-11-04 核对，`dnSpy/dnSpy.Contracts.Logic/Decompiler/DecompilerConstants.cs`）：
  2.3 VB `BBA40092-76B2-4184-8E81-0F1E3ED14E72`、2.3 C# `4162DADA-67C3-4DE4-A5F3-6552C8353ECE`、
  3.2 VB `9C5E4C7A-1F2B-4E6D-9A30-8B7D2E5F6A41`、3.2 C# `8C1F4A62-9D3B-4E77-8A51-2F6B0C9D4E13`；
  UI 写回恒为 UniqueGuid（`DecompilerService.cs:55`），新装用户无保存值、落字段默认
  `LANGUAGE_CSHARP`（Generic，按 OrderUI 命中 2.3）
- [x] 实现 `LanguageGuid` 一次性迁移（含迁移标记 `LanguageGuidMigratedTo32`，见方案 B；
  落点 `dnSpy/dnSpy/Decompiler/DecompilerServiceSettings.cs`——主程序无扩展程序集引用，
  3.2 GUID 以字面量书写并注明来源；**新装默认值一并切到 3.2 C#**）
- [x] C# 侧同样迁移 `LANGUAGE_CSHARP_ILSPY` → 3.2 C# 的 UniqueGuid（同一映射函数）
- [x] 单独 commit；构建 0 错误（2 个警告为上游 MessagePack 包既有漏洞通告，与本次无关）
- [x] **设置迁移端到端验证**（2026-11-04 自动化，`--settings-file` 临时文件 + 真实启动 ×4，未触碰用户真实设置）：
  反射探针 MigrateTo32 六例全过（映射/幂等/非目标 GUID 不动）；新装默认 → 3.2 C# + 标记位 True；
  老设置 2.3 VB → 启动后自动落 3.2 VB + 标记位 True；标记位 True + 手动切回 2.3 → 重启后**保持 2.3**。
  注意：验证必须用**最小设置文件**（只含反编译器节）——完整工作区文件里激活标签页的语言会经
  `DecompileDocumentTabContent.UpdateLanguage()` 写回全局设置，盖掉测试输入（这是 dnSpy 既有语义，非迁移缺陷）。
- [ ] **全功能走查**（手动，待执行；GUI 输出质量部分无法自动化）：
  - [ ] 代码窗口反编译（C# / VB / IL / ILAst / Mixed 各一次）
  - [ ] 导出工程（Export to Project）
  - [ ] 书签格式化、工具提示、`DummyDecompiler` 兜底路径（无程序集时启动）
- [ ] 如走查发现 3.2 输出阻塞切换的缺陷 → 回到对应 Batch 修复后重走

### M4 下线 2.3（用户确认分两步：归档 done 2026-11-07 + 移除 done 2026-11-08）
> IL spans 语句级精度延后已获用户接受，双引擎并存至专项落地
> 用户决策（2026-11-07）：**M4 分两步**——归档先行（已执行），注册与项目的移除延后到
> IL spans（调试步进）引擎级专项落地后；IL spans 延后已获用户接受，期间双引擎并存
> （调试场景可切回 2.3）。
- [x] M3 稳定运行（5 轮走查修复 + 迁移矩阵 + 双语言对拍，无回退）
- [x] 打 tag / 归档 `Extensions/ILSpy.Decompiler`（2026-11-07）：
  tag `archive/ilspy-2.3-engine`（887e46cac）+ 源码备份
  `D:/Codebase/dnSpyEx/_local_backup_20261107_ilspy23.tar.gz`（含 2.3 扩展与核心引擎，3374 文件）
- [x] **移除 2.3 的注册（2026-11-16，M4 阶段 1+2）**：
  - 阶段 1：3.2 扩展补上非语言注册——`Settings/DecompilerSettingsPageProvider32.cs`
    （C#/VB/IL 三页，C# 与 IL 页 GUID 同 2.3）、`Themes/wpf.styles.templates.xaml` + `TheExtension32.cs`
    （IExtension）、`Settings/DecompilerAppSettingsModifiedListener32.cs`（改设置即刷新）。
  - 阶段 2：`dnSpy.sln` 移除 2.3 扩展项目（15 条）；`dnSpy.Console/Program.cs` 不再加载
    `dnSpy.Decompiler.ILSpy.Core`；bin 内仅余 ILSpy32 程序集。2.3 扩展**源码目录保留但不再构建**；
    2.3 引擎子模块保留（`dnSpy.BamlDecompiler`、`ICSharpCode.NRefactory.VB32` 仍引用）。
  - 验证：全量构建 0 错误；`dnSpy.Console -l "C#"` 报未知语言、`-l "C# (ILSpy 3.2)"` 正常；
    IL 视图与 2.3 基线仍逐字一致（diff 0）；probe：engine-tests/ilast-verify/
    mixed-verify/settings-wiring 均通过（`ilprint-diff` 因缺 2.3 程序集改为 SKIP）。
  - 注意：M4 后审计文档中「2.3 段变孤儿」的担忧已消失（2.3 语言不再注册）。
- [x] 移除解决方案引用，全量构建 0 错误（2026-11-08：dnSpy.sln 移除 14 条 ILSpy.Core 引用，dnSpy 主项目 + 3.2 扩展均构建 0 错误）
- [x] 更新文档状态（2026-11-24）：`Engine-Coverage-Audit.md` §8.112–§8.114、`engine-upgrade.md`（§0/§1/§4/§8）、本文件；README / Dev-Plan 待手动走查后一并处理。

## 5. 兼容与回滚

- **回滚**：迁移代码只改 `LanguageGuid`，把 2.3 注册保留至 M4，回滚 = 还原该 commit 并重启动。
- **兼容**：迁移只在「设置指向 2.3 UniqueGuid」时触发一次；用户手动选 2.3 后不再迁移（标记位）。
- **IL/ILAst/Mixed**：IL 视图已由 3.2 接管；**ILAst/Mixed 也已于 2026-11-16 迁移到 3.2**
  （引擎补 public API + 扩展新增两语言，见 `docs/ILAst-Mixed-3.2.md`），M4 移除 2.3 时无需保留。

## 6. 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| 迁移后 3.2 输出在某功能回退（导出工程等）| 用户可感 | M3 走查清单全覆盖；发现即回 Batch 修复 |
| `Find` 双匹配（GenericGuid/UniqueGuid）导致顺序敏感 | 语言选择错乱 | 迁移直接写 3.2 的 UniqueGuid，不依赖兜底 |
| M4 删除 2.3 后发现隐式依赖（IL/ILAst/Mixed、设置页）| 构建失败 | 删除前先全量构建验证 + 归档可恢复 |
| 一次性迁移误触发（用户明确选了 2.3）| 违背用户意愿 | 迁移标记位；手动切回后不再迁移 |