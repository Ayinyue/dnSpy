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
| 引用/导航（token reference） | ✅ | 本地定义 ✓；外部类型 TypeRef 映射 ✓；keyword 引用（corlib）✓；同类型成员使用 ✓（107ad96b4）；**外部类型成员引用 ✓（2026-11-10）**：引擎把方法解析结果挂在外层 InvocationExpression 上而非 MemberReferenceExpression（CallBuilder 裸 new），token writer/转换器从 invocation 注解取 IMember，mapper 新增外部方法/字段 → MemberRef 合成（ceci 签名转换 + 模块 MemberRef 表优先匹配，bridge 的 ModuleDefinition scope 归一化到裸程序集名）。probe 实测 C# 5/5、VB 6/7 站点与 2.3 同形挂载（MemberRefMD/MemberRefUser） |
| async/await 重建 | 🔧 | 现代版 Roslyn 布局（builder 无局部拷贝/catch 内局部清理）已支持——引擎子模块工作区补丁（MatchTaskCreationPattern 双布局 + setResult/catch 位置无关扫描）。C# 门禁 syntax 641→633、real 1468→1460（dnSpy.dll 样本已重建为真 async/await）。剩余拒绝（匹配直方图 m4=1136/m5=245）经方法名核实**全部为普通方法正常跳过**（非状态机 kickoff）——引擎级状态机重写覆盖已收敛至双布局支持的能力边界 |
| 语句级调试映射 | 🔧→✅ | 2026-11-10：C# 侧接入引擎 `CreateSequencePoints(SyntaxTree)`（public API；渲染经 WrapInWriterThatSetsLocationsInAST 回填行列，`IL.SequencePoint` 的 Offset/EndOffset 与 dnSpy ILSpan 同为排他端点直接映射；`ILFunction.CecilMethod.MetadataToken` 经 rid 保真桥回 resolve 到 dnlib MethodDef）。**VB 侧同日补齐**：VBTextOutputFormatter32 的 DebugStart/DebugExpression/DebugEnd 按 2.3 形状实现——转换器把 C# 树的 ILInstruction 注解拷到 VB 节点，DebugEnd 从 visitor 已渲染节点（state.Nodes）的注解收集 IL→ILSpan（mappedInstructions 先到先得防跨语句重复），无序列点方法保留方法级 v1 span 作 fallback。probe spdump 实测：C# 5/5、VB 5/5 方法语句级 SP（ControlFlow C# 19 条/VB 15 条、LinqAndLock C# 9 条/VB 12 条，IL↔文本逐条对位）；真实库 BookmarksVM（dnSpy.dll）37 个 debug info 中 14+ 方法语句级。**CalculateILSpans 对齐**：与 2.3 一样只在调用方请求时计算（导出工程/Console 管线跳过语句级收集） |
| 迭代器重建 | 🔧→✅ | 2026-11-10：per-checkpoint 直方图定位真根因——CallBuilder 的显式接口实现 cast 分支与普通成员引用分支**顺序执行**（移植丢了互斥 else），`((IDisposable)this).Dispose()` 的 target.Expression 第二次 AddChild 抛 "Node is already used in another tree"，使全部状态机类型（含 416 处 Iterator.Dispose 形态）反编译失败。恢复 if/else 互斥后状态机类型渲染与 2.3 同形状（<NestedLoops>d__3 3044 chars vs 2.3 的 3431，kickoff/MoveNext switch 形态一致；2.3 dnSpy 集成本身即显示 kickoff+原始状态机，yield 重建形态非两者目标）。剩余 yield:e1-e8 拒绝点经上下文标签直方图核实为非状态机方法本底（设计内跳过）。C# 门禁 OK（875/1818 vs 2.3 的 908/2168；645/1588→875/1818 是失败文件恢复输出后的真实计数） |
| 查询续接子句 `into`（VB） | ✅ | 2026-11-09：VB 兼容模式关 QueryExpressions（与 2.3 的方法链形态一致，degraded 197→0）；查询子句 visitor 全实现（Where/Let/Group/Continuation 嵌套 From），Join 仍 throw（无 degraded 站点） |
| BC30345 / BC30521 余量 | 🟡 | C# out/值重载对、Show(Nothing) 歧义——VB 语言无法表达，按既定策略不修 |
| 全量输出形态差异（2026-11-10 对拍 dnSpy.dll 全部 3267 文件） | 🟡 | 3.2 零失败、零空文件；与 2.3 的剩余形态差异定性为有意：①自动属性 `=> "x"` 表达式体 vs 2.3 的 get/set 块——2.3 引擎时代不支持 expression-bodied，3.2 输出更现代；②状态机类型的 `<>1__state` 字段与 `Current` 属性 3.2 完整显示而 2.3 集成版隐藏——3.2 更完整；③using 集差异为既有家族。已知局限仅剩 VB 属性访问器/构造器的方法级 span（非 MethodDeclaration 节点）、外部属性/事件引用不合成（站点已正确着色）与调试 locals 的 hoistedField 映射（3.2 引擎的 symbols 管线未激活，SourceLocal 为空——PDB 主名的 locals 显示不受影响，仅迭代器/async 提升字段的聚合显示退化；引擎级专项，见第 7 节 7） |
| **功能接线全仓 sweep**（2026-11-10，137 个 IDecompiler/服务消费点） | ✅ | ①语言判断 11 处全部族级 GUID（GenericGuid）或语言无关路径——2.3 字面量 GUID 判断仅 Analyzer 一处（已加 3.2）；②dnSpy.Console 修复（3.2 语言注册 + 族 GUID 确定性，1dfa6b5d3，实测 default==32/!=23）；③CalculateILSpans 门控对齐 2.3（5afad216b）；④选项接线全量翻转验证 10/19 实测响应；⑤真实库 VB 语句级 SP 抽验（BookmarksVM 14+ 方法语句级） |

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
| **选项接线全量翻转验证**（2026-11-10） | ✅ | probe `options-wiring` 模式：19 项 bool 选项逐一 flip→反编译输出响应断言，0 错误；10 项实测 CHANGED（UsingDeclarations/YieldReturn/AsyncAwait/AnonymousMethods/ForEach/Lock/QueryExpressions/IntroduceIncrementAndDecrement/FullyQualifyAmbiguousTypeNames/Token 注释），6 项 SAME 为样本形态不匹配（XML 文档/表达式树/自动属性/事件字段/字符串 switch/局部初始化器），3 项（AlwaysGenerateExceptionVariable/UseDebugSymbols/ObjectOrCollectionInitializers）传导链路正确（setter 回读 ✓、引擎持 EngineSettings 引用 ✓、反编译缓存经 RaiseVersionChanged 失效 ✓）但引擎 transform 对其不响应——入已知局限 |
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
| **dnSpy.Console** | 🔧 | 2026-11-10 修复：语言列表只加载 2.3 程序集（GetAllLanguages 的 asmNames 缺 3.2），3.2 语言在 Console 下完全不可用；且 .NET Core 的 Assembly.Load 受 deps.json 探测限制、3.2 的 provider 实现 IDecompilerCreator（2.3 是 IDecompilerProvider）。改为 LoadFrom 同 bin 磁盘 + 双接口分支 + 族 GUID 多命中按 OrderUI 确定性取最新引擎。实测：语言列表含 C#/VB (ILSpy 3.2)，默认族 GUID 命中 3.2（default==32、!=23），`-l <UniqueGuid>` 显式选择工作 |
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

1. ~~语句级调试映射精度（引擎 IL↔语句映射）~~ ✅ 已关闭（2026-11-10，C# 侧接入引擎 CreateSequencePoints；VB 侧同日经 VBTextOutputFormatter32 的 DebugStart/DebugExpression/DebugEnd 实现，见第 1 节）
2. ~~迭代器重建（YieldReturnDecompiler 引擎专项）~~ ✅ 已关闭（2026-11-10：CallBuilder 显式接口 cast 分支双挂树移植 bug 修复后，状态机类型反编译失败清零、形态与 2.3 对齐；state-diag 直方图（e1-e8@上下文标签 + kickout/rej 透传，Enabled 门控零生产开销）核实剩余拒绝均为非状态机方法本底。详见第 1 节迭代器行）
3. ~~VB 查询续接子句 `into`~~ ✅ 已关闭（ad85ab3d0，degraded 197→0）
4. ~~外部类型成员引用挂载~~ ✅ 已关闭（2026-11-10，invocation 注解探测 + mapper MemberRef 合成，见第 1 节；剩余：外部属性/事件引用不合成（颜色语义需 property/event 色，且站点已被句法 fallback 正确着色））
5. ~~2.3 选项值 → 3.2 的一次性选项值迁移~~ ✅ 已关闭（DecompilerSettings32Impl/VbDecompilerSettings32Impl 持久化 + 2.3 section 一次性迁移，空目标守卫）
6. ~~嵌套类型反编译~~ ✅ 已关闭（2026-11-10：`DecompileType(FullTypeName)` 内部 `FindType(fullTypeName.TopLevelTypeName)` 把嵌套名截断到顶层类型，嵌套节点渲染出整个父类型——probe 实测 Inner 835 chars vs 2.3 的 484；C#/VB 两侧均改 `DecompileTypes(new[]{cecilType})` 后 Inner 只渲染自身。**同名歧义嵌套类型**（dnSpy.dll 两个 ScrollDirection）经 binder 简名回退绑定失败导致 Token 注释缺失——`Attach` 增加 knownType 直绑（dnSpy 传入的确切 TypeDef 及其嵌套按名直绑，绕过歧义回退），probe 实测 Token 注释与 2.3 对齐。VB 门禁基线随形态修复移位 942/526/11→1731/741/11（嵌套从"整个父类型"（可编译但错位）变为独立正确输出，门禁脚本已注记）；C# OK 875/1818）
7. ~~调试 locals 映射~~ ✅ 已关闭（2026-11-10：C# 侧把引擎变量表（ILVariable 携带 IL slot Index 与反编译名）映射到方法 dnlib locals 产 SourceLocal——Local/UsingLocal/ForeachLocal kind 按 slot 直绑（Parameter/StackSlot/Exception 正确排除），调试器 Locals 窗口显示反编译名；probe 实测 Release 样本逐方法 locals 计数正确（LinqAndLock 3、Debug 构建 5）、提升变量（async kickoff 的 length）正确排除。**VB 侧同日补齐**：VBDecompiler32 反编译后从 C# 树收集（节点 ILInstruction 注解的祖先链得 ILFunction，同引擎序列点构建器的发现方式），经 formatter 注入 builder——实测 LinqAndLock 5、状态机类型 MoveNext 9 locals/37 条语句级 SP。**hoistedField 不适用**：2.3 的 HoistedField 只在 yield 重建成功时填充（YieldReturnDecompiler.cs:349），而 2.3 集成版的实际输出形态为 kickoff+原始状态机（重建未跑），即 2.3 集成版的 debug info 同样不含 hoistedField——3.2 行为一致，无覆盖缺口）
