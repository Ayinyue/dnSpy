# 2.3 → 3.2 引擎功能覆盖审计（逐模块接线差异清单）

审计方法：全仓 grep 2.3 引擎符号（`LANGUAGE_CSHARP_ILSPY`/`LANGUAGE_VISUALBASIC_ILSPY`、
`DecompilerConstants.*`、`IDecompiler`/`IMethodDebugService` 消费者、`UniqueGuid` 判断），
逐一核对每个消费方在 3.2 语言下的行为；配合 text 对拍（probe `text` 模式）与双门禁。
审计结论分四类：✅ 已对齐 / 🔧 本次修复 / 🟡 有意差异（已注记）/ ⛔ 已知缺口（专项跟踪）。

## 1. 反编译语言本体

| 项 | 状态 | 说明 |
|---|---|---|
| C# 反编译质量 | ✅ | 双门禁对 2.3 全面占优（syntax 641 vs 908、realBad 1467 vs 2167） |
| VB 反编译质量 | ✅ | syntax 0 vs 2.3 的 1110；realBad 868/426/11 vs 2.3 的 2396/918/51 |
| using/Imports | ✅ | 2026-11-08 修复（DecompileType 收集重载 + FindRequiredImports 链解析） |
| Token/RVA 注释 | ✅ | InsertTokenComments，受 ShowTokenAndRvaComments 选项控制 |
| using 升序 | ✅ | 与 2.3 排序一致 |
| XML 文档注释 | ✅ | AddXmlDocumentationTransform + 桥接 FileName 回填 |
| 导出工程（C#/VB .csproj） | ✅ | PartialType/AssemblyInfo/TypeMethods + ProjectFileExtension（330f7d6c7） |
| 着色（双引擎逐 token 对拍） | ✅ | 颜色差异收敛至 3.2 更准/既有家族（d11ab7a18、VB 批次、2bf4ffcd4） |
| 引用/导航（token reference） | ✅ | 本地定义 ✓；外部类型 TypeRef 映射 ✓；keyword 引用（corlib）✓；同类型成员使用 ✓（107ad96b4）。外部类型成员引用需数据流类型推断（引擎级，随语句级调试映射专项推进） |
| async/await 重建 | 🔧 | 现代版 Roslyn 布局（builder 无局部拷贝/catch 内局部清理）已支持——引擎子模块工作区补丁（MatchTaskCreationPattern 双布局 + setResult/catch 位置无关扫描 + StateMachineDiagnostics 诊断钩）。C# 门禁 syntax 641→633、real 1468→1460（SaveMefStateAsync 样本已重建为真 async/await）。剩余 per-checkpoint 迭代（Create 等 async-void 形态的 m4/m28）按诊断直方图继续 |
| 语句级调试映射 | ⛔ | 方法级 v1 已交付（8c5f1c83b）；语句级精度需引擎 IL↔语句映射专项（与迭代器重建共用诊断基础设施） |
| 迭代器重建（~608 文件 C# 侧退化） | ⛔ | 引擎级 YieldReturnDecompiler 专项（2.3 能重建部分形态；VB 侧经 QueryExpressions 关闭已用方法链覆盖） |
| 查询续接子句 `into`（VB） | ✅ | 2026-11-09：VB 兼容模式关 QueryExpressions（与 2.3 的方法链形态一致，degraded 197→0）；查询子句 visitor 全实现（Where/Let/Group/Continuation 嵌套 From），Join 仍 throw（无 degraded 站点） |
| BC30345 / BC30521 余量 | 🟡 | C# out/值重载对、Show(Nothing) 歧义——VB 语言无法表达，按既定策略不修 |

## 2. IDecompiler 契约面

| 项 | 状态 | 说明 |
|---|---|---|
| Decompile（类型/成员/属性/字段/事件） | ✅ | 全部实现；单成员带声明类型注释行（3f8e2ddb1） |
| DecompileAssemblyInfo / PartialType / TypeMethods | ✅ | 330f7d6c7 |
| ProjectFileExtension | ✅ | .cs/.vbproj |
| FileExtension / ContentTypeString / OrderUI | ✅ | 3.2 OrderUI = 2.3 + 0.5（语言列表 2.3 在前，便于对比） |
| GenericGuid（语言族） | ✅ | 2.3/3.2 共享族 GUID，语言下拉成组 |
| MetadataTextColorProvider | ✅ | VisualBasicMetadataTextColorProvider.Instance（两语言同款 kind 色规则） |
| 引用失败降级 | ✅ | GetCecil try/catch + binder 失败无注解渲染（6304532a5） |
| 隐藏类型空渲染 fallback | ✅ | 7b5b84c0a（<PrivateImplementationDetails> 等） |

## 3. 反编译选项（dnSpy 选项体系）

| 项 | 状态 | 说明 |
|---|---|---|
| Options 暴露 | 🔧 | 原 `Options => Empty`（设置页对 3.2 零选项）；现暴露 19 个引擎映射 bool + ShowTokenAndRvaComments（扩展自有，默认 true） |
| ShowTokenAndRvaComments | 🔧 | Token 注释接入（C#/VB），setter 触发 VersionChanged 重反编译 |
| ShowXmlDocumentation / UsingDeclarations / FullyQualifyAmbiguousTypeNames / 代码形态开关（yield/async/匿名方法/表达式树/自动属性事件/foreach/lock/查询表达式/switch 字符串/++--/赋值表达式/初始化器/调试符号/catch 命名） | 🔧 | 直通 EngineSettings 同名属性 |
| 不暴露 3.2 无对应的选项 | 🟡 | IL 视图三个（ShowILComments/ShowILBytes/ShowPdbInfo）、成员排序、十六进制数字、字符串长度上限、MemberOrder——3.2 引擎无等价能力，避免死开关 |
| Clone 深拷贝 | 🔧 | 原 EngineSettings 引用共享（clone 写穿），已改 EngineSettings.Clone() |
| 选项持久化 | ✅ | DecompilerSettings32Impl/VbDecompilerSettings32Impl MEF 单例持久化 + 2.3 选项值一次性迁移 |

## 4. 组件 / 子窗体 / 插件接线

| 组件 | 状态 | 说明 |
|---|---|---|
| **dnSpy.Analyzer** | 🔧 | `AnalyzerTreeNodeData.GetOriginalCodeLocation` 硬编码只认 2.3 C# UniqueGuid——匿名方法/lambda 的代码位置回溯在 3.2 下退化。已加 `LANGUAGE_CSHARP_ILSPY32` 判断（DecompilerConstants 新增常量） |
| **dnSpy.Debugger（语言服务）** | 🔧 | `DbgDotNetLanguageServiceImpl` 把 Roslyn 语言映射到**族 GUID 的第一个**反编译器（OrderBy OrderUI → 命中 2.3），调试映射/求值与用户选择的 3.2 脱节。改为族内优先匹配用户默认语言（新公共契约 `IDecompilerLanguageSettings.LanguageGuid`，主程序 DecompilerServiceSettingsImpl 实现导出），无匹配回退 OrderUI |
| **dnSpy.Debugger（调试映射）** | ✅ | `DbgMethodDebugInfoProvider` 以注入的 decompiler + `CalculateILSpans=true` 反编译产 SourceStatement——3.2 的方法级 span v1 使其工作（语言无关，无 GUID 判断） |
| **Bookmarks（书签定位/格式）** | ✅ | TextViewDotNetMethodBodyBookmarkLocationProvider 走 MethodDebugService（语言无关）；DotNetBookmarkLocationFormatter 按 **GenericGuid** 判 VB（族级，3.2 自动生效） |
| **DocViewer / DocumentViewer** | ✅ | MethodDebugService 消费，语言无关 |
| **Search（代码搜索）** | ✅ | 只引用 IDecompilerService（语言无关） |
| **TreeNodeReferenceDocumentTabContentProvider** | ✅ | MethodDebugService，语言无关 |
| **tooltip（悬停提示）** | ✅ | 由 reference（dnlib 对象）驱动，与引擎无关 |
| **SaveCommands / NodeTabSaver（保存代码）** | ✅ | 语言无关 |
| **BAML 反编译** | ✅ | 独立 IBamlDecompiler 契约，不受引擎切换影响 |
| **dnSpy.Console** | ✅ | 默认语言按族 GUID 匹配（LANGUAGE_CSHARP），语言无关 |
| **AsmEditor 编译（ILanguageCompilerProvider）** | ✅ | 按族 GUID 注释，语言无关 |
| **插件（第三方）** | 🟡 | 3.2 是新 UniqueGuid/GUID 字面量；依赖 2.3 GUID 字面量的插件需自行适配（仓库内无此类插件） |

## 5. 配置差异

| 项 | 状态 | 说明 |
|---|---|---|
| 语言 GUID 迁移（M3） | ✅ | MigrateTo32 + LanguageGuidMigratedTo32 标记；**本批把字面量换成 DecompilerConstants 共享常量** |
| 设置持久化 | ✅ | settings section 机制通用（按 decompiler GUID 分组）；3.2 默认值起步 |
| 引擎设置对象 | ✅ | DecompilerSettings32 → EngineSettings（TupleTypes/StringInterpolation 关 + VB 兼容 NullPropagation 关） |

## 6. 修复不在预期内的回归

- 门禁 Contracts.Logic realBad 10→11：重建「fork 引擎+旧扩展」对照，坏文件集合与当前完全一致（既有家族），上午的 10 为门禁非确定性，基线已同步注记。
- `(From ...).ToArray()` 语句以 `(` 开头被 VB 视为上一行延续（BC30035 ×7）：NeedsCallKeyword 加 QueryExpression，产 `Call (From ...)`。

## 7. 后续专项（不在本批）

1. 语句级调试映射精度（引擎 IL↔语句映射）
2. 迭代器重建（YieldReturnDecompiler 引擎专项）
3. ~~VB 查询续接子句 `into`~~ ✅ 已关闭（ad85ab3d0，degraded 197→0）
4. 外部类型成员引用挂载（需数据流类型推断；同类型成员/keyword/类型引用已挂）
5. ~~2.3 选项值 → 3.2 的一次性选项值迁移~~ ✅ 已关闭（DecompilerSettings32Impl/VbDecompilerSettings32Impl 持久化 + 2.3 section 一次性迁移，空目标守卫）
