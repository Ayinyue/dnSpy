# 2.3 → 3.2 反编译后端迁移方案记录

> 状态：🟡 方案待评审（未实施）。前置阅读：`VB-Dev-Plan.md`（里程碑 M2/M3/M4 的定义）。
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
- [ ] **勘察补全**：核对 `DecompilerConstants` 里 `LANGUAGE_VISUALBASIC_ILSPY` / C# 侧各常量的确切 GUID 值
- [ ] 实现 `LanguageGuid` 一次性迁移（含迁移标记，见方案 B）
- [ ] C# 侧同样迁移 `LANGUAGE_CSHARP_ILSPY` → 3.2 C# 的 UniqueGuid
- [ ] 单独 commit；构建 0 错误
- [ ] **全功能走查**（手动）：
  - [ ] 代码窗口反编译（C# / VB / IL / ILAst / Mixed 各一次）
  - [ ] 导出工程（Export to Project）
  - [ ] 书签格式化、工具提示、`DummyDecompiler` 兜底路径（无程序集时启动）
  - [ ] 老设置文件启动：语言自动落到 3.2，无异常、无空白窗口
- [ ] 如走查发现 3.2 输出阻塞切换的缺陷 → 回到对应 Batch 修复后重走

### M4 下线 2.3（破坏性，需用户单独确认后执行）
- [ ] M3 稳定运行（走查无回退）后才开始
- [ ] 打 tag / 归档 `Extensions/ILSpy.Decompiler`
- [ ] 移除 2.3 的 `IDecompilerCreator` 导出与 IL/ILAst/Mixed 视图，同步删除设置页
- [ ] 移除解决方案引用，全量构建 0 错误
- [ ] 更新 README / Action-Plan / Dev-Plan / 本文件状态

## 5. 兼容与回滚

- **回滚**：迁移代码只改 `LanguageGuid`，把 2.3 注册保留至 M4，回滚 = 还原该 commit 并重启动。
- **兼容**：迁移只在「设置指向 2.3 UniqueGuid」时触发一次；用户手动选 2.3 后不再迁移（标记位）。
- **IL/ILAst/Mixed**：依赖 2.3 引擎，M3 不动；M4 时若确需保留，需单独评估工作量（默认随 2.3 一并下线）。

## 6. 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| 迁移后 3.2 输出在某功能回退（导出工程等）| 用户可感 | M3 走查清单全覆盖；发现即回 Batch 修复 |
| `Find` 双匹配（GenericGuid/UniqueGuid）导致顺序敏感 | 语言选择错乱 | 迁移直接写 3.2 的 UniqueGuid，不依赖兜底 |
| M4 删除 2.3 后发现隐式依赖（IL/ILAst/Mixed、设置页）| 构建失败 | 删除前先全量构建验证 + 归档可恢复 |
| 一次性迁移误触发（用户明确选了 2.3）| 违背用户意愿 | 迁移标记位；手动切回后不再迁移 |
