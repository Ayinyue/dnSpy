# VB 语义正确性开发计划（TASK 清单）

> 配套阅读：`VB-Action-Plan.md`（方向与规矩）、`baseline/README.md`（45 条已修经验）。
> 本文件是**可勾选的执行计划**：每批 = 一类根因，独立验收，可独立回退。
> 状态标记：`[ ]` 未开始 / `[x]` 完成 / `[~]` 进行中 / `[!]` 受阻待确认。
> 基线提交：`50433f691`。基线数字（2026-11-02 实测）见下表，**回归时逐项对照**。

## 核心目标：完成 2.3 → 3.2 的升级迁移，并安全下线 2.3

近期核心目标不是「语义零错误」，而是**把 dnSpy 的反编译后端从 ILSpy 2.3 完整迁移到 3.2**。
VB 语义正确性只是迁移验收的一关：3.2 的输出质量必须**不低于现役 2.3**，否则不切换。

### 四个里程碑

| 里程碑 | 内容 | 判定 | 状态 |
|---|---|---|---|
| **M1 引擎可用** | 3.2 后端在 dnSpy 内可对 C#/VB 反编译，三样本 0 降级 | 三样本 3266/1278/84 全 `ok` | ✅ 已达成（2026-10-31）|
| **M2 质量对齐**（当前）| 语义错误不高于 2.3 基线且收敛；语法错误清零；每条修复语义可论证 | 见下方 M2 验收 | 🟡 进行中（本计划的 Batch 1~5）|
| **M3 切换默认** | dnSpy 默认反编译器切到 3.2 | 全功能走查（代码窗口/导出工程/书签/工具提示）无回退 | ⚠️ 待勘察 |
| **M4 下线 2.3** | 删除旧引擎代码路径与设置项，文档归档 | 解决方案不再引用 2.3 项目，全量构建 0 错误 | 未开始 |

### M2 的验收（即原 Definition of Done，降级为里程碑门槛）

1. 语法错误 0 / 0 / 0（2026-11-03 达成：`BC30036` 整数字面量溢出已修，见 baseline/README.md 第 30 条）
2. realBadFiles 不高于 **2.3 基线 2039**（当前 1066 ✅ 已达成），并沿 Batch 1~5 继续收敛
3. 不可归零的部分（互操作类型跨文件、internal 可见性）从度量口径剥离并写明证据
4. 每条被消掉的诊断有「为什么与原 IL 等价」的说明（README 经验表）

### M3 / M4 的已知接线（勘察结论，待细化）

- 切换点：`dnSpy/dnSpy/Decompiler/DecompilerService.cs`——反编译器按 `OrderUI` 排序，
  当前语言取自 `DecompilerServiceSettings.LanguageGuid`，找不到时回退 `AllDecompilers.First()`。
- 待勘察：3.2 后端（`dnSpy.Decompiler.ILSpy32`）如何注册进该列表、`OrderUI` 相对 2.3 的值、
  设置里 `LanguageGuid` 的持久化与升级兼容（老用户设置仍指向 2.3 时不能崩）。
- M4 前置条件：M3 稳定运行且无功能回退反馈；2.3 目录（`Extensions/ILSpy.Decompiler`）删除前归档。

### 当前差距（M2）

realBadFiles 989 / 527 / 10（Batch 4 后，2026-11-04 实测），可归零部分余约 200~350
（`BC30456 真缺成员/BC30521/BC30583`；`BC32022` 已收敛至「引擎解析不到目标类型」形态
66+25、`BC31132` 余 43+12 均为按策略保留的不可表达形态），其余为度量口径问题（见第 3 节）。
语法 0 / 0 / 0（`BC30036` 已修）。

## 0. 基线与验收红线（每批通用）

| 指标 | dnSpy.dll (3266) | AsmEditor.x (1278) | Contracts.Logic (84) |
|---|---|---|---|
| 转换 | 3266 ok / 0 degraded | 1278 ok / 0 degraded | 84 ok / 0 degraded |
| 语法错误 | 0 | 0 | 0 |
| realBadFiles | **989**（Batch 4 后） | **527**（Batch 4 后） | **10** |

**验收红线（任一违背即该批不通过）**
- [ ] 引擎构建 0 错误（`dotnet build -c Release -f net10.0-windows --no-restore`）
- [ ] 语法错误不高于 5 / 0 / 1
- [ ] realBadFiles 不高于基线（目标：该批对应诊断下降）
- [ ] 无 `Console.WriteLine` / 环境变量跟踪残留
- [ ] 全量三样本重跑（dump + validate + compile），不是只跑单类型

## 1. 批次总览与依赖

```
Batch 0 回归基建（一次性，可跳过）
   └─> Batch 1 BC30452 引用比较（92）
   └─> Batch 2 BC30401 签名不符归类（109）
   └─> Batch 3 BC30456 成员不存在定性（229）
   └─> Batch 4 事件族（BC32022 266 / BC31132 81）
   └─> Batch 5 BC30583(45) / BC30521(108)
Batch 6 度量口径改造（可选，独立）

Batch 1~5 = 里程碑 M2（质量对齐）；M3/M4 见「里程碑任务」节
```
批次之间无强依赖，可并行或换序；**每批独立提交、独立可回退**。

## 2. 批次明细

### Batch 0 回归基建

- [x] 把第 4 节三条命令固化成一个脚本（`baseline/run-regression.cmd`），一条命令输出三组数字
- [x] 脚本输出与本文基线表逐项对照，不一致即失败（2026-11-03 建；另修 compile 引用数非确定漂移：Baseline.csproj 显式引用 WindowsDesktop 共享框架）

**验收**：新会话不读文档也能一条命令得到 1066 / 565 / 10。
**说明**：纯工具，不改转换器；若认为没必要可跳过，但每次回归就要手敲四条命令。

### Batch 1 `BC30452` 引用比较漏判（92 个文件，0 噪声）

- [x] 用 `--whole --id BC30452 --top 3` 取样归类（2026-11-03）：主形态是**访问器内的引用比较**
  ——`ResetMethodState` 不覆盖属性/索引器/自定义事件，setter 的 `value` 又是体转换后才合成的，
  两侧类型都推不出；次形态是方法调用结果（`svc.Value.Create(...) == x`）与字符串 `==`
- [x] 访问器纳入成员状态重置 + `value` 参数提前注册（README 第 31 条）
- [x] 扩展 `ExpressionTypeResolver`：方法返回类型、委托 Invoke、`MyBase`、一元运算、`as`、
  泛型实参按位替换（README 第 32 条）
- [x] 顺带修复预存语义缺陷：C# 字符串 `==` 曾被发成 `Is`（引用比较），现按值语义发 `=`
  （README 第 33 条）

**已知事实**：`IsReferentialComparison` 已是「任一侧已知引用即判 `Is`」；剩余 37 条是**两侧都未知**。
**风险**：把值类型比较误写成 `Is` → 新增 `BC30037` 类错误。缓解：只有 `Classify()==true` 才改写，值类型与未知都保持 `=`。
**验收**：`BC30452` ≤ 50 ✅（92→37，AsmEditor 54→26）；`BC32022`/`BC30456` 不回升 ✅（266/229 持平）。

### Batch 2 `BC30401` 签名不符归类（109 个文件，56 噪声）

- [x] `--whole --id BC30401` 取样 21 条归类（2026-11-03）：显式接口实现与同签名成员的绑定混淆
  （方法对：`Resolve`/`GetCommands`；属性对：`UIObject`/`DocumentTreeView`）为主；
  Optional 复制到错误重载（`DocumentListLoader.Load`）次之
- [x] 定性：全部为**转换器给的签名错**（绑定/复制环节），非 VB 更严格
- [x] 修绑定器：显式实现感知 + 返回类型决胜 + 全签名门禁（README 第 34 条）
- [x] 修 Optional 复制：参数类型全比对（README 第 35 条）
- [x] 归一化统一：VB 关键字拼写、Nullable、数组（README 第 36 条；round 5 的 +34 回退即此课）
- [ ] 已知限制：`.override` 在属性访问器上的转发函数仍以方法身份声称实现属性成员（README 第 37 条）

**已知事实**：`Optional` 已从接口元数据复制（提交 `272309359`）；`BC30401` 比 `BC30149` 多 12 是**更精确**的诊断，不是回归。
**风险**：`ResolveInterfaceDefinition` 按「名字+元数」匹配，重载多的接口可能错配——不要扩大它的匹配面。
**验收**：`BC30401` ≤ 80 ✅（108→49）；`BC30149` 不回升 ✅（156→131）。

### Batch 3 `BC30456` 成员不存在定性（229 个文件，12 噪声）

- [x] 取样 20 条定性（2026-11-03）：
  - 主形态（**转换缺陷，已修**）：VB 大小写不敏感，局部/参数名吞掉类型引用
    （`searchLocation` 吞 `SearchLocation`、`guid` 吞 `Guid.TryParse`）→ 方法头参数改名 +
    引用映射（README 第 38 条）
  - 次形态（**真缺成员**，待后续）：`'ImageReference' is not a member of 'BookmarkInfo'`
    类——属性/字段没生成
  - 余量（**度量口径**）：互操作类型成员跨 dump 文件，`--whole` 下消失
- [x] 定性结论入档 README 第 38~40 条
- [x] 已知缺口入档：ctor/operator 无 MethodDef 注解（README 第 39 条）

**代表样本**：`'ImageReference' is not a member of 'BookmarkInfo'`（真缺成员，未修）。
**验收**：定性报告入档 ✅；realBadFiles 下降 ✅（1050→1030；AsmEditor 556→554）。

### Batch 4 事件族（`BC32022` 266 / `BC31132` 81）

- [x] 取样定性（2026-11-04）：`BC32022` 的订阅形态（`+=`/`-=` 残留）与副本形态（`Dim x As T = Me.Evt`）是**同一根因**——事件订阅判定链在 ctor/dtor/访问器里断掉（`MethodDef` 注解缺失，265/266 个残留文件含 ctor 上下文）。预设的「放宽副本抑制『恰好两次引用』」被取样证伪：语料中全部事件副本在 C# 树里都恰好 guard+call 两用，`uses>2` 的仅 4 处 `GetInvocationList` 形态（守卫是 `== null` 早退、调用是列表元素间接调用，谓词本就不匹配），放宽救不了它们，反而会新增孤儿守卫——修复落在注解缺失上，不动引用计数
- [x] 补注解：ctor（`.ctor`/`.cctor`+参数+静态）、dtor（唯一 virtual `Finalize` 覆写）、operator（`op_*`+参数+返回类型）、accessor（从已匹配的 PropertyDef/EventDef 带下 Get/Set/Add/Remove）（README 第 41 条）
- [x] `HasEventIn` 跨模块断链返回 null（未知≠否定，`IsEventAccess` 的 `?? true` 兜底恢复生效；泛型实例基类 `Base<T>` 解链）（README 第 42 条）
- [x] `BC31132`：E1 空 add/remove / E4-stub 双 Throw / E4d 私有委托字段三种可确定形态合成 `RaiseEvent`（签名重复委托 `Invoke`，`TEventArgs` 按事件实参替换）；E2 列表型 / E4c/E4e 转发型无法在 VB 确定表达，按策略保留显式报错（README 第 43 条）
- [x] 顺带修两处实施中发现的缺陷：显式接口事件的限定名绑定 `IFoo.Bar`（README 第 44 条）、`Type.GetType("System.Nullable")` 别名解析到非值类型运行时类型（`BC32128` 1→0，README 第 45 条）
- [x] 单类型回归（WinSysButton ctor 订阅、HexBufferImpl 的 E4d 合成、BookmarkTagger 的 E1 合成、DecompilerService 的 E2 保留逐一验证输出）+ 全量回归（三样本 dump+validate+compile，门禁通过）

**已知事实**：自动事件（双访问器都标 `[CompilerGenerated]`）已改写字段式事件；`BC31133`（81/12）是 E 形态自定义事件的伴生诊断，Batch 3 基线报告里就有（compile-r4ref-dnspy.md 同为 81），非本批引入。事件读点现直接输出 `RaiseEvent`（全量 dump 内 34+32+30+26… 按事件名分布）；C# 树里直接引用 backing 字段的引发点保持 copy+guard（合法 VB，语义相同）。
**风险（已规避）**：合成 `RaiseEvent` 语义不成立的形态一律不合成，保持 BC31132 显式报错。
**验收**：`BC31132` 剩「确实无法表达」并已写明 ✅（43 dnSpy + 12 asm，全部 E2/E4c/E4e）；`BC32022` 266→66（dnSpy）✅、`RaiseEvent`/`AddHandler` 输出与 C# 调用点一一对应 ✅；realBadFiles 1030→989 / 554→527 / 10→10 ✅。
**余量（待后续 batch，M3 口径）**：`BC32022` 66（dnSpy，`Lazy`1` 链如 `documentTabService.Value.DocumentCollectionChanged`）+ 25（asm，`CommandManager.RequerySuggested` 静态外部事件）——目标类型本身解析不到（cecil 不跟随 type forward、WindowsBase 不在 bin 目录），`GetTypeName` 返回 null 在 `IsEventAccess` :1706 提前 false，与本批修的 `HasEventIn` 兜底（:1707）是两个不同入口。

### Batch 5 重复实现与重载歧义（`BC30583` 45 / `BC30521` 108）

- [ ] `BC30583`：剩余代表 `ITextView.GetTextViewLineContainingBufferPosition`，查去重键为何没命中（疑似接口继承链上同名成员）
- [ ] `BC30521`：同名不同签名的重载歧义，可能需补 `Overloads` 或区分实现
- [ ] 全量回归

**风险**：去重逻辑改宽会重新引入 `Implements I.Foo, I.Foo`（上一轮已踩过：按名字去重会吞合法重载）。
**验收**：两者下降；`BC30149` 不回升。

### Batch 6 度量口径改造（可选，需用户拍板）

- [ ] 方案 A：`compile --whole` 作为默认口径，逐文件仅作调试
- [ ] 方案 B：给编译单元加 `InternalsVisibleTo` 式可见性，让 internal 成员可见
- [ ] 方案 C：维持现状，仅在报告里突出 Noise 列

**动机**：`BC30002`(669/563 噪声)、`BC30389`(2408 全噪声) 占了指标大头，剥离后 realBadFiles 才反映真实缺陷密度。
**不做代码改动**；只改 `baseline/Program.cs` 与报告口径。

## 3. 明确不做（已定性，别再投入）

| 诊断 | 结论 | 证据位置 |
|---|---|---|
| `BC30389`（2408）| 逐文件编译的可见性假阳性 | README「度量口径」节 |
| `BC30284`（367）| 基类不可见级联，全为噪声 | 同上 + `CollectInaccessibleBases` |
| `BC36645`/`BC32050`/`BC30518`（434）| LINQ 推断级联（whole 模式下 163→3）| README「不是转换缺陷」节 |
| `BC30002` 中互操作/状态机类型 | 定义在另一个 dump 文件 | 同上 |

## 4. 待用户确认（M-S-C 优先级）

1. 批次顺序：默认按 1 → 2 → 3 → 4 → 5，Batch 6 可选——是否调整？
2. Batch 0 的回归脚本要不要做（不做则每次手敲四条命令）？
3. Batch 4 的 `RaiseEvent` 合成：语义不成立时允许保留显式报错（BC31132），还是要求清零？
   ——Batch 4 已按「保留显式报错」执行（E2/E4c/E4e 不合成，见 README 第 43 条）；若要求
   清零，需要先给出列表 API 映射/跨对象转发的 VB 引发语义，属新决策。
4. Batch 6 选 A / B / C / 不做？

## 4.5 里程碑任务（M3 / M4，迁移收口）

### M3 切换默认反编译器（先勘察，再动手）

> 详细实施方案（接线事实、A/B/C 方案对比、兼容与回滚）见 `Migration-3.2-Plan.md`。

- [ ] 勘察 `dnSpy/dnSpy/Decompiler/DecompilerService.cs`：3.2 后端的注册方式、`OrderUI` 相对 2.3 的值
- [ ] 确认 `DecompilerServiceSettings.LanguageGuid` 的持久化行为：老用户设置仍指向 2.3 时的回退路径
- [ ] 方案评审（至少两案：默认直接切 3.2 / 保持用户可选仅调 `OrderUI`），确认后实施
- [ ] 全功能走查：代码窗口、导出工程、书签格式化、工具提示、`DummyDecompiler` 兜底路径
- [ ] 如发现 3.2 输出阻塞切换的缺陷，回到对应 Batch 修复后重走本清单

### M4 下线 2.3

- [ ] M3 稳定运行（全功能走查无回退）后才开始
- [ ] 归档：2.3 目录（`Extensions/ILSpy.Decompiler`）与相关文档打包归档，删除前打 tag
- [ ] 移除解决方案中对 2.3 项目的引用与设置项（这是破坏性变更，动手前需用户确认）
- [ ] 全量构建 0 错误；更新 README / Action-Plan / Dev-Plan 的状态标记

> ⚠️ M3/M4 触碰 dnSpy 主程序与删除代码，均属**破坏性变更**：动手前必须单独确认。

## 5. 每批交付物（固定）

1. 代码 commit（Conventional Commits，一次一批）
2. 文档 commit（`baseline/README.md` 经验表 + 本文件勾选 + 基线数字更新）
3. 变更摘要：改了哪些文件 / 为什么 / 影响范围 / 回退方式
4. 记忆沉淀（如环境支持）：`.workbuddy/memory/YYYY-MM-DD.md`
