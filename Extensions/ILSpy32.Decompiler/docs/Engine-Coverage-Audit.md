# 2.3 → 3.2 引擎功能覆盖审计（逐模块接线差异清单）

审计方法：全仓 grep 2.3 引擎符号（`LANGUAGE_CSHARP_ILSPY`/`LANGUAGE_VISUALBASIC_ILSPY`、
`DecompilerConstants.*`、`IDecompiler`/`IMethodDebugService` 消费者、`UniqueGuid` 判断），
逐一核对每个消费方在 3.2 语言下的行为；配合 text 对拍（probe `text` 模式）与双门禁。
审计结论分四类：✅ 已对齐 / 🔧 本次修复 / 🟡 有意差异（已注记）/ ⛔ 已知缺口（专项跟踪）。

## 1. 反编译语言本体

| 项 | 状态 | 说明 |
|---|---|---|
| C# 反编译质量 | ✅ | 双门禁对 2.3 全面占优（syntax 875 vs 908、realBad 1818 vs 2168；早期 641/1467 为部分状态机类型输出失败时的计数，CallBuilder 修复后恢复为 875/1818 真实计数） |
| VB 反编译质量 | ✅ | syntax 0 vs 2.3 的 1110；realBad 1731/741/11 vs 2.3 的 2396/918/51（基线随嵌套类型形态修复从 942/526/11 移位——独立正确输出替代"整个父类型"，见 run-regression.cmd 注记） |
| using/Imports | ✅ | 2026-11-08 修复（DecompileType 收集重载 + FindRequiredImports 链解析）；**2026-11-13 二次修复（用户实测反馈）**：①`using System;` 在部分类型缺失——2.3 的 IntroduceUsingDeclarations 有"always import System"而 3.2 缺（补上）；②using 声明顺序反（升序集合+同点插入=输出逆序）——按注释意图改降序遍历。实测 IWshShell：`using System;` 恢复首位、顺序与 2.3 一致；**C# 门禁 syntax 875→208、realBad 1818→1151**（全限定名噪音消除）；③attribute 顺序：伪属性（ComImport 等 flags）前置而 2.3 后置——CecilLoader.AddAttributes 重排为表属性→security→伪属性，实测 [Guid][TypeIdentifier][ComImport] 与 2.3 一致。VB 门禁 realBad 1731/741→**1068/518**（同步改善） |
| Token/RVA 注释 | ✅ | InsertTokenComments，受 ShowTokenAndRvaComments 选项控制 |
| using 升序 | ✅ | 与 2.3 排序一致 |
| XML 文档注释 | ✅ | AddXmlDocumentationTransform + 桥接 FileName 回填 |
| 导出工程（C#/VB .csproj） | ✅ | PartialType/AssemblyInfo/TypeMethods + ProjectFileExtension（330f7d6c7） |
| 着色（双引擎逐 token 对拍） | ✅ | 颜色差异收敛至 3.2 更准/既有家族（d11ab7a18、VB 批次、2bf4ffcd4） |
| 引用/导航（token reference） | ✅ | 本地定义 ✓；外部类型 TypeRef 映射 ✓；keyword 引用（corlib）✓；同类型成员使用 ✓（107ad96b4）；**外部类型成员引用 ✓（2026-11-10）**：引擎把方法解析结果挂在外层 InvocationExpression 上而非 MemberReferenceExpression（CallBuilder 裸 new），token writer/转换器从 invocation 注解取 IMember，mapper 新增外部方法/字段 → MemberRef 合成（ceci 签名转换 + 模块 MemberRef 表优先匹配，bridge 的 ModuleDefinition scope 归一化到裸程序集名）。probe 实测 C# 5/5、VB 6/7 站点与 2.3 同形挂载（MemberRefMD/MemberRefUser） |
| async/await 重建 | 🔧 | 现代版 Roslyn 布局（builder 无局部拷贝/catch 内局部清理）已支持——引擎子模块工作区补丁（MatchTaskCreationPattern 双布局 + setResult/catch 位置无关扫描）。剩余拒绝（匹配直方图 m4/m5）经方法名核实**全部为普通方法正常跳过**（非状态机 kickoff）——引擎级状态机重写覆盖已收敛至双布局支持的能力边界 |
| 语句级调试映射 | 🔧→✅ | 2026-11-10：C# 侧接入引擎 `CreateSequencePoints(SyntaxTree)`（public API；渲染经 WrapInWriterThatSetsLocationsInAST 回填行列，`IL.SequencePoint` 的 Offset/EndOffset 与 dnSpy ILSpan 同为排他端点直接映射；`ILFunction.CecilMethod.MetadataToken` 经 rid 保真桥回 resolve 到 dnlib MethodDef）。**VB 侧同日补齐**：VBTextOutputFormatter32 的 DebugStart/DebugExpression/DebugEnd 按 2.3 形状实现——转换器把 C# 树的 ILInstruction 注解拷到 VB 节点，DebugEnd 从 visitor 已渲染节点（state.Nodes）的注解收集 IL→ILSpan（mappedInstructions 先到先得防跨语句重复），无序列点方法保留方法级 v1 span 作 fallback。probe spdump 实测：C# 5/5、VB 5/5 方法语句级 SP（Release 样本：ControlFlow C# 19 条/VB 15 条、LinqAndLock C# 9 条/VB 12 条，IL↔文本逐条对位）；真实库 BookmarksVM（dnSpy.dll）37 个 debug info 中 14+ 方法语句级；**第二真实库 AsmEditor.x 复验时发现并修复 ForEachStatement 头部 IL 无映射**（VB32 visitor 不对 foreach 调 DebugExpression——空 Nodes 时子树扫描拾取头部未领指令，先到先得保护下安全）；单行方法的 WHOLE 降级与语句 span 等价、无实际损失。**CalculateILSpans 对齐**：与 2.3 一样只在调用方请求时计算（导出工程/Console 管线跳过语句级收集）。**IL 覆盖率补齐（2026-11-12）**：probe `dbg-map` 模式实测 3.2 的语句 SP 只覆盖 34-95% 指令（SP 间空洞：分支/spill/prolog）而 2.3 为 98-100%——`MethodDebugInfoBuilder.CompactILSpans(codeSize)`（排序+首条拉回 0+逐条扩到下一条起点+末条扩到 bodySize，TextSpan 不动）在 C#/VB 两端 Create 前调用，**覆盖率全部达到 100%**（SimpleStatements 26/26、ControlFlow 121/121、LinqAndLock 92/92），engine-tests 14 项回归 ALL PASS；**全量复验**（dbg-map2 扫 dnSpy.dll 全部类型，C# 与 VB 双后端）：**IL 覆盖率 100%、零次级方法以下覆盖缺失**（仅显式接口属性 getter 的单语句 TextSpan 定位 0/1 边缘个案，IL 侧仍 100%） |
| 迭代器重建 | 🔧→✅ | 2026-11-10：per-checkpoint 直方图定位真根因——CallBuilder 的显式接口实现 cast 分支与普通成员引用分支**顺序执行**（移植丢了互斥 else），`((IDisposable)this).Dispose()` 的 target.Expression 第二次 AddChild 抛 "Node is already used in another tree"，使全部状态机类型（含 416 处 Iterator.Dispose 形态）反编译失败。恢复 if/else 互斥后状态机类型渲染与 2.3 同形状（<NestedLoops>d__3 3044 chars vs 2.3 的 3431，kickoff/MoveNext switch 形态一致；2.3 dnSpy 集成本身即显示 kickoff+原始状态机，yield 重建形态非两者目标）。剩余 yield:e1-e8 拒绝点经上下文标签直方图核实为非状态机方法本底（设计内跳过）。C# 门禁 OK（875/1818 vs 2.3 的 908/2168；645/1588→875/1818 是失败文件恢复输出后的真实计数） |
| 查询续接子句 `into`（VB） | ✅ | 2026-11-09：VB 兼容模式关 QueryExpressions（与 2.3 的方法链形态一致，degraded 197→0）；查询子句 visitor 全实现（Where/Let/Group/Continuation 嵌套 From），Join 仍 throw（无 degraded 站点） |
| BC30345 / BC30521 余量 | 🟡 | C# out/值重载对、Show(Nothing) 歧义——VB 语言无法表达，按既定策略不修 |
| **提升/捕获局部变量引用**（2026-11-14 tokstat 复测） | 🔧→✅ | 被 lambda/迭代器捕获的局部在引擎提升为显示类字段后无 IL slot，引用映射返回 null——合成按 ILVariable 实例缓存的 SourceLocal（local=null），同逻辑变量全出现共享同一实例（dnSpy 高亮按引用身份匹配）。全语料 23-only 缺口对 107→83（focusable/treeView 修复）。**StackSlot 形态补齐（同日二次修复）**：lambda 体对提升变量的引用呈 StackSlot-kind ILVariable（引擎以原变量名命名）——重构映射分支后 StackSlot/越界 Local 统一走 (方法,名称) 缓存合成。全语料 23-only 缺口对 **83→19**（line2 的 9 处全引用）。剩余 19 对为代码形态差异噪声（不同反编译结果的 token 本身不可比） |
| **this./关键字引用**（2026-11-14 tokstat 复测） | 🔧 | ①/ 关键字补 declaring TypeDefMD 引用（3.2 输出存量 3041 处全覆盖）；②/ 访问器关键字补 accessor MethodDefMD（56 处，2.3 为 51）；③数字字面量补基元类型引用；④ 补构造函数引用。**显式 this. 风格差异**：2.3 对所有实例成员访问显式写 this.（BookmarksVM 169 处 vs 3.2 的 9 处）——3.2 遵循现代 C# 惯例省略，2.3 语料中 31090 个 this token 大部分在 3.2 输出中不存在，属显示风格差异而非着色缺口 |

| **VB 引用/显示全面对齐**（2026-11-15 tokstat 复测，缺口对 118→**0**） | 🔧→✅ | ①**Me 限定恢复 2.3 显示形态**（2.3 的 ldarg.0 生成显式 ThisReferenceExpression，实例成员访问全部输出 Me.，31104 处）：隐式 this 实例成员包装 InstanceExpression(Me)+enclosing TypeDef 引用；初始化器（BC30043/BC32001/BC30369）、接口调用、委托 Invoke 排除——同时修复 paramNameRenames 大小写不敏感查找把 'Parameter = parameter' 静默改写为 'parameter2 = parameter2'（自赋值 no-op）的真 bug；②**泛型参数引用**（GenericParamMD，~1300 处）：FindGenericParameterReference 扫 dnlib 注解（内层方法优先），声明/使用/类型使用/合成 Of T 全站点；③**MyBase** 补基类引用（2114 处）；④**基元类型关键字**补 corlib TypeRef（String/Integer/Object/…/Void，11k+ 处）；⑤**命名空间段 NamespaceReference**：Imports 末段（顶层调用 fullNs=null 的丢失）、限定名链（ResolveTypeNameFallback）、QualifyMemberType/CreateNamedType 两 builder——fallback 方向修正（深命名空间以段为前缀）；⑥**Implements 链**颜色+引用双挂（颜色按第一注解、引用按类型取自节点）+ 表缺失时合成 TypeRefUser（OutAttribute 无预存条目，2.3 importer 按需创建）；⑨**自命名空间段 fallback**（a46a29ff6，dnSpy.MainApp/dnSpy.Culture/dnSpy.Text.AvalonEdit——模块自身命名空间无 TypeRef 条目，2.3 用模块自身程序集作 NamespaceReference scope）；⑩**IL 标签引用**（0bbb262f5）：2.3 用方法全名+标签名的共享 String 引用让 dnSpy 联动高亮 goto↔标签（Label 色）——3.2 C# 原为 Text 色无引用（颜色差异使 (text,color) 对不进 common 故 tokstat 未显形），VB 原有 Label 色无引用；C# DnSpyTokenWriter 加 LabelStatement/GotoStatement 分支（定义 Local|Definition、goto Local），VB formatter 经 parent-parent 从 Identifier token 读语句节点；⑪**delegate 关键字引用**（0bbb262f5）：2.3 的匿名方法 delegate 关键字挂编译器生成方法 MethodDefMD（434 处全带）——3.2 引擎把 ILFunction 注解挂在 AnonymousMethodExpression（WithILInstruction），CecilMethod 经 rid 保真桥映射同形 MethodDefMD。**VB 缺口对清零；C# 剩 14 对全为形态噪声**（9 对 locals 名集合不同源不可比 + 5 对 32-only=3.2 多出的引用/更准的解析——CharSet/SetLastError 真字段、Group/Header/InputGestureText 3.2 解析出真属性而 2.3 误判）。缺口对 118→0 收敛完成：裸泛型调用推断实参全为方法类型参数时输出（Me.Create(Of T)() 形态，Me 分支补 TypeArguments 转换）；⑧**ctor 引用**（New X()）VB 侧补齐。**C# 对照**：C# 侧 this. 省略是 3.2 现代风格（31104 token 在 3.2 输出中不存在），VB 侧 2.3 本身就输出 Me.，故 VB 必须恢复——两语言不同基线分别对齐。门禁 1729/740/11（两项优于 1731/741 基线），engine-tests 24/24 |⑫**精修轮 token 流级对拍**（218d30523）：difflib 同类型逐 token 对齐（equal 块逐位比 hasRef）发现聚合统计盲区的 6 类缺口并全修——①字段声明名（binder 从未挂 FieldDef，每字段 1 处低于聚合阈值）、②MyBase.New/Me.New 构造链（初始化器拷贝的 invocation 注解 + 基类 ctor 参数匹配回退）、③AddressOf 方法组、④lambda 参数声明名（三层解析：ILVariableResolveResult → ILFunction.CecilMethod 槽位映射（dnlib Parameters 含隐式 this 而引擎 index 不含，偏移 +1）、字段初始化器 lambda 走 LambdaExpression 的 ILFunction）、⑤运算符 token（引擎树保留 op_ 调用为 CSharpInvocationResolveResult，C# 输出层才折叠为运算符——挂 Member/表回退；复合赋值的 invocation 在 udom-null OperatorResolveResult 之后）、⑥折叠的 corlib 常量（∞/NaN 合成 2.3 的 member-access 形态）。对齐分歧 44→1（唯一剩余为 3.2 比 2.3 多一个引用，非缺口）；tokstat 聚合维持 0 对；probe spdump 增 23/23vb 引擎选择器。⑬**覆盖缺口盘点**（c9e8955b6 后）：①**AsyncMethodDebugInfo/StateMachineDebugInfo——已关闭（254a96d0e）**：Common/StateMachineDebugInfoDetector 纯 dnlib 检测器（嵌套类型 CompilerGeneratedAttribute+IAsyncStateMachine 判定 kind；MoveNext body 扫描 await 对——**现代 Roslyn 的 state 分发是条件链而非 Switch 指令**：stfld 常量经 'dup; stloc V' 分裂存储、resume 目标取 brfalse/beq 条件链；字段匹配用 name（resolution 出不同 FieldDef 实例））→ 四个 debug-info 构造点集成（builder 主体改 MoveNext、kickoff 带可见方法）——**同时发现并修复 member-level 调试信息完全缺失**（binder 只匹配类型声明下的成员——单方法树的顶层 MethodDeclaration 无 MethodDef → MethodSpans 空 → dnSpy 调试服务的单方法路径（DecompileMember）渲染 0 条 debug info——顶层成员对已知声明类型匹配修复后 member-level 0→1）。**数值对照（probe asyncdiff 模式）**：dnSpy.dll 的 6 个 async 方法 **3.2 全部产 AsyncInfo（6/6），2.3 全部不产（0/6）**——2.3 的 ILAst 层 async 检测对 .NET 10 Roslyn 布局失效——**3.2 的实现超越 2.3 现状**（async 步进在 3.2 语言下从退化变为可用）；数值语义手动核对（yield=AwaitUnsafeOnCompleted call offset、resume=state 条件链分支目标、setResult=SetResult call offset——SaveMefStateAsync 逐项吻合）；②**IL/ILAst/Mixed 三语言**——**IL 视图已搬入（614846f73，工作流实现）**：ILDecompiler32（guid 7A3F1C2E-8B24-4D57-9C36-1E5A0F4B92D3，GenericGuid=LANGUAGE_IL，OrderUI 200.5，ContentType 复用 2.3 的 ILILSpy）走 3.2 引擎 public ReflectionDisassembler + ITextOutput→IDecompilerOutput 桥接（颜色/token 引用映射/括号配对）——**debug info 链补齐**（3.2 引擎反汇编器无 debug 管线——BodyTrackingMethodBodyDisassembler 覆写 WriteInstruction 记录每指令的输出区间 → 每指令一语句 + CompactILSpans——ilprobe 实测 35 语句/35 指令全覆盖）。ILAst/Mixed **已迁移到 3.2**（2026-11-16，见 `docs/ILAst-Mixed-3.2.md`）：3.2 的 `ICSharpCode.Decompiler.IL`（`ILReader`/`ILFunction`/`GetILTransforms`/`WriteTo(ITextOutput, ILAstWritingOptions)`）即 2.3 ILAst 的后继实现，引擎侧新增 `ITextOutput.NextPosition`/`AddCodeBracesRange`+`CodeBracesRangeFlags`+`ILAstWritingOptions.WriteCodeBracesRanges`/`DebugInfoWriter`，扩展侧新增 `ILAst (ILSpy 3.2)` 与 `IL with C# (ILSpy 3.2)` 两语言——**2.3 预编译程序集不再被需要**（M4 移除 2.3 时可删）。Mixed 的 XML 文档注释、Token/RVA 注释、IL 字节/PDB 位置/局部名/2.3 操作数布局亦已对齐（引擎 `GetXmlDocComments`/`GetTokenAndRvaComment`/`GetBodyHeaderComments`/`GetLocalNames`/`UseDnSpyLayout` 钩子 + 扩展 `Common/XmlDocComments32`/`Common/TokenComment32`/`Common/MixedDisassemblyComments32`，实测除 C# 叠加行粒度外与 2.3 逐字一致）。**IL 视图亦已对齐**（同款钩子接到 `ILDecompiler32`，与 2.3 IL 视图逐字一致，两样本 diff=0；逐指令装饰抽到 `BodyTrackingMethodBodyDisassembler` 供两视图共用）。③C# 对称精修（c9e8955b6）：自命名空间段 34/91 分歧 + lambda 参数声明名 4/11 全修——C# 对齐分歧 58→20（余为个位 token 与 3.2 反向更全）。④上游测试套复跑：564 中 464 过/85 败/15 跳——**较 11-12 基线（407/142）改善 57**，85 败=43 UseMcs（无 mono）+42 环境型 roundtrip/UseRoslyn/上游 issue，无引擎逻辑回归。⑤**并行挖掘追加结论**：a) **C# 门禁 syntax=875 构成**全部为已定性的编译器生成形态族（<PrivateImplementationDetails>/__StaticArrayInitTypeSize=N 数据类型、<X>d__N 状态机名的不可表达、CS8803 单类型拆文件结构、CS1056 '$' 插值常量）——与 2.3 的 908 同族且 3.2 更少，无可修项；b) **扩展加载无版本约束**——dnSpy 的 MEF `[ImportMany] IDecompiler[]/IDecompilerCreator[]` 扫 bin 自动发现 3.2 程序集；c) **调试表达式求值语法兼容**——dnSpy.Roslyn=5.9.0（C# 10 编译器）覆盖 3.2 引擎输出形态（C# 7-10），3.2 引擎不输出 C# 11+ 语法（ILSpy 3.2 时代），无缺口；d) **EditCode 功能在 dnSpyEx 不存在**（全库无 IEditCode/EditCode 实现）——非缺口；e) **性能对拍（工作流 C 完成，三样本系统数据）**——**3.2 整体慢 2.5–6.2 倍、中位数 1.9–4.15 倍，放大集中在少数大类型**（前 6 慢类型占 3.2 总时间 32.5–49.5% vs 2.3 的 19.1–28.7%，头部 9.4–15.9 倍）；总量倍数随程序集规模上升（Contracts 2.46x → AsmEditor 4.55x → dnSpy 6.15x）与逐 token 引用解析的规模放大吻合；**语言无关共享基建瓶颈**（同程序集 C#:VB 比率两引擎一致——排除转换层）；**cecilCalls=0 全表**排除 Cecil 路径。报告 artifact 入档（工作流 perf-report）。优化方向：逐 token 链摊销/缓存（收益集中在大类型）；进一步需真 profiler。引擎 lock 串行化（多 tab 排队）同批入档。

**立项执行记录（性能优化专项第一轮）**：①dotnet-trace 函数级采样（dotnet-trace collect --format nettrace + report topN——**trace 的 speedscope evented 解析需按线程分组，exclusive topN 用自带 report 命令**）；②Exclusive top25：**GetQueuedCompletionStatus 69.5% 为空闲等待（有效 CPU ~22%）**、Monitor.Enter_Slowpath 6.1%（GetCecil 的 entityDict lock）、FindMemberRefInTable 1.18%、**GetEnvironmentVariableCore 0.39% 为诊断自身开销**（PROF 诊断的 env 读取在热循环——已改为启动时缓存）；③修复：TryGetCecil 结果缓存（消除每 token lock+UnresolvedMember 访问）+ PROF 热循环 env 读缓存；④**A/B 验证受阻**：机器噪声 ±40%（三组同环境数据 10489→16561→25298ms 波动，2.3 同比例 1950→4677——环境劣化，8 个残留 dotnet build server 关闭后仍劣化），**单次测量无法分辨 <20% 的优化**——缓存保留（逻辑无害），效果验证挂起待干净环境/真 profiler。**立项结论**：热点定位完成（render 内 ResolveReference 链）、量化基线与工具链就绪（bench/PROF/dotnet-trace）、优化执行待环境窗口。**第二轮（f4d107d4b，细分定位后命中真实热点）**：ResolveReference 逐步计时发现 **FindMemberRefForTypeSystemMember 占 260ms**（外部成员表回退——**miss 时前缀匹配遍历全表、每条两次 Substring**）——修复：member-name 索引（前缀匹配只扫同名候选）+ 外部回退 per-member 缓存——**同机背靠背实测：fallback 260→50ms（5.2x）、render 447→133ms（3.3x）、3.2 总量 10489→8588ms（-18%）、对 2.3 比率 4.4x→3.6x、WpfHexViewImpl 1539→893ms**；cecilCalls 全表 0。**剩余热点**：invocation 探测 ~84ms（分散于唯一成员的 GetCecil 锁；边际收益递减，引擎级为主）——性能专项收敛，双门禁绿 + engine-tests ALL PASS。
| 全量输出形态差异（2026-11-10 对拍 dnSpy.dll 全部 3267 文件） | 🟡 | 3.2 零失败、零空文件；与 2.3 的剩余形态差异定性为有意：①自动属性 `=> "x"` 表达式体 vs 2.3 的 get/set 块——2.3 引擎时代不支持 expression-bodied，3.2 输出更现代；②状态机类型的 `<>1__state` 字段与 `Current` 属性 3.2 完整显示而 2.3 集成版隐藏——3.2 更完整；③using 集差异为既有家族；④引擎的栈验证警告注释 `//IL_xxxx:`（上游 ILSpy 3.x 原生行为，仅特定元组/栈形态触发）——**dnSpy.dll/AsmEditor/Contracts 门禁样本零出现**，上游测试的 TupleTests 用例可见（2026-11-12 定性）。已知局限仅剩 VB 属性访问器/构造器的方法级 span（非 MethodDeclaration 节点）、外部属性/事件引用不合成（站点已正确着色）与调试 locals 的 hoistedField 映射（见第 7 节 7 的 C#/VB locals 修复与剩余） |
| **功能接线全仓 sweep**（2026-11-10，137 个 IDecompiler/服务消费点） | ✅ | ①语言判断 11 处全部族级 GUID（GenericGuid）或语言无关路径——2.3 字面量 GUID 判断仅 Analyzer 一处（已加 3.2）；②dnSpy.Console 修复（3.2 语言注册 + 族 GUID 确定性，1dfa6b5d3，实测 default==32/!=23）；③CalculateILSpans 门控对齐 2.3（5afad216b）；④选项接线全量翻转验证 10/19 实测响应；⑤真实库 VB 语句级 SP 抽验（BookmarksVM 14+ 方法语句级） |
| **单元测试**（2026-11-12） | ✅ | ①3.2 引擎上游测试套复活（此前无法运行）：net46→net462、Immutable 1.3.1→1.7.1、NUnit 3.13.1+adapter 4.6.0（旧 adapter 自带旧版 cecil 与引擎类型身份冲突）、Tester 增加 SnippetResolver——564 测试全跑：407 过/142 败/15 跳，失败构成 = UseMcs 环境缺失（无 mono）+ using 顺序（移植有意对齐 dnSpy 2.3）+ 零星，**无引擎逻辑回归**；②2.3 引擎无自带测试套（上游 2.3 分仓）——由双门禁+对拍覆盖；③probe `engine-tests` 模式：**18 项回归断言**（语句级 SP C#/VB、debug locals、CalculateILSpans 门控、嵌套类型渲染+knownType、状态机完整性、**引用高亮匹配**：同方法 4 站点同引用实例 + SigComparer 语义匹配/重载区分）——ALL PASS，probe 工具与样本源入库（baseline/probe/，de7d085d0） |
| **UI 显示效果与接线审查**（2026-11-12） | ✅ | ①**引用高亮**：dnSpy 的 `SpanDataReferenceInfoExtensions.CompareReferences` 走 SigComparer 语义比较（name/declaring fast check + Resolve 回退 + 签名比较）——表路径（MemberRefMD 同实例）与合成路径（MemberRefUser 结构相等）均兼容，probe 实测 4 站点同实例 + 合成匹配/重载区分（engine-tests 9a-9d）；②**括号对/块结构**（BracePair/BlockStructure）：文本级括号匹配，语言无关——3.2 输出的括号平衡由门禁 compile 验证；③**反编译视图无折叠/大纲功能**（dnSpy GUI 本身无此特性，无接线面）；④**语法分类器**（CSharpClassifier）只作用于编辑器——反编译只读视图的颜色由 token writer 直接产出（colordump 已对拍）；⑤**错误标记**：只读视图无编译验证，无错误标记面；⑥**导航栏/tooltip/搜索**：语言无关消费点（已盘 137 sweep） |
| **全语料 token 统计对拍**（2026-11-14，tokstat.py：dnSpy.dll 1900 类型、C# 1.5M/VB 1.6M tokens，(text,color)→引用率聚合） | 🔧 | 机制本身是本轮核心产出——小样本抽查遗漏的系统性缺口全部被聚合统计暴露。C# 修复五项：①`-=`/赋值运算符 token 错挂 TypeRefUser（3.2 引入的回归，483 处——赋值节点可携带 TypeResolveResult，token 引用路径现限定 MemberResolveResult）；②外部成员使用缺引用（`x.Length`/`x.Count` 等，2.3 单 `Length` 就有 701 处 MemberRefUser——GetCecil 外部失败后回退模块 MemberRef 表，属性带 accessor 名变体、泛型实例前缀匹配）；③`Array.Length`/`LongLength` 走裸 ldlen 指令（模块表无 MemberRef）——引擎 VisitLdLen 补真实属性注解（子模块补丁）+ token 侧合成 get_Length MemberRefUser；④`new` 关键字补构造函数引用（全语料 10759 处）；⑤数字字面量补基元类型引用（Local+Hidden+NoFollow，2.3 MakePrimitive 对齐）。VB 遗留：字段/属性声明的 `As 类型` 使用与命名冲突属性名的引用缺口（2.3 有 TypeDefMD/PropertyDefMD），入第 7 节 8 项 |

## 2. IDecompiler 契约面

| 项 | 状态 | 说明 |
|---|---|---|
| Decompile（类型/成员/属性/字段/事件） | ✅ | 全部实现；单成员带声明类型注释行（3f8e2ddb1）；**单成员 knownType 直绑**（2026-11-13：DecompileMember 传入声明类型，避免简单名歧义回退——C#/VB 双端） |
| **逐文件深入分析**（2026-11-13，16 个源文件 ~10,300 行） | ✅ | 按文件职责审查：DnlibCecilBridge（三 tier 解析器+PreserveRids+CWT 缓存 ✓）；TypeNameHelper（`/`→`+` 嵌套名转换 ✓）；DecompilerCreator（MEF+反射双构造 ✓）；ILSpyEnvironmentProvider32（ Cecil 注解直读+methodNames 回退 ✓）；VBDecompiler32（knownType+pendingLocals+vbCompatibility 引擎缓存 ✓）；DnlibReferenceMapper（token 验证+identity check+外部 MemberRef 合成+memberRefTable 缓存 ✓）；AstAnnotationBinder（knownType 直绑+MatchMethod/Property/Event+rename 收集 ✓）；ExpressionTypeResolver（引用比较/隐式字符串转换/Char 运算/局部变量查找 ✓）；高级特性实证：泛型方法+约束/运算符重载/索引器/扩展方法/自定义事件/嵌套泛型——双引擎正确。修复：DecompileMember knownType 缺失（9119e5a47） |
| DecompileAssemblyInfo / PartialType / TypeMethods | ✅ | 330f7d6c7 |
| ProjectFileExtension | ✅ | .cs/.vbproj |
| **导出工程端到端**（2026-11-11） | 🟡 | dnSpy.Console `--sdk-project` 实测（3.2 默认语言 + 显式 2.3 语言对照）：C#/VB 文件结构/csproj/AssemblyInfo（VB 含 My Project 结构）产全。编译错误与 2.3 同族——①编译器生成名 `<X>d__N` 裸文本的 CS1525/CS1001（**2.3 导出同样失败**，24 处相同；Escape 是显示级转义不解决语言合法性）；②VB 的 BC30002（状态机类型定义缺失）为两引擎同族：MSBuild 层 Project.cs:130 只遍历顶层类型，且引擎宿主渲染隐藏状态机嵌套类（2.3/3.2 宿主文件均为 0 处 `<get_Filenames>d__3` 定义）；③增量 90 错误来自 3.2 的隐藏类型 fallback 特性多产 2 个文件（GUI 显示价值）——比修 fallback 契约（视图/导出场景分叉）更保守，定性为与 2.3 一致的导出历史限制。**主程序对 2.3 引擎本体的直接引用为 0**（仅共享 dnSpy.Decompiler.MSBuild 层，语言无关） |
| FileExtension / ContentTypeString / OrderUI | ✅ | 3.2 OrderUI = 2.3 + 0.5（语言列表 2.3 在前，便于对比） |
| GenericGuid（语言族） | ✅ | 2.3/3.2 共享族 GUID，语言下拉成组 |
| MetadataTextColorProvider | ✅ | VisualBasicMetadataTextColorProvider.Instance（两语言同款 kind 色规则） |
| 引用失败降级 | ✅ | GetCecil try/catch + binder 失败无注解渲染（6304532a5） |
| 隐藏类型空渲染 fallback | ✅ | 7b5b84c0a（<PrivateImplementationDetails> 等） |
| **CalculateILSpans 响应** | 🔧 | 2026-11-10 对齐 2.3：语句级调试信息只在调用方请求时计算（DecompileDocumentTabContent 设 true），导出工程/Console 管线跳过（5afad216b）；probe 实测 CALC=0 时 C#/VB debug info 均为 0、CALC=1 恢复 56 条 SP |

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
| **dnSpy.Debugger（调试映射）** | ✅ | `DbgMethodDebugInfoProvider` 以注入的 decompiler + `CalculateILSpans=true` 反编译产 SourceStatement（语言无关，无 GUID 判断）——3.2 现为语句级 SP + CompactILSpans 全体覆盖（dbg-map 双 API 实测 100%）+ 调试 locals 映射；`DbgEngineStepperImpl` 的 async 跨 await 步进消费 `AsyncMethodDebugInfo`，两引擎集成版在重建未跑时均为 null（防御完整，行为一致） |
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
| **VB 局部/参数引用覆盖**（2026-11-14 tokstat 全语料复测） | 🔧→✅ | VB formatter 的 GetCurrentLocalReference 恒 null、GetCurrentLocalDefinition 只读 dnlib Param——VB 局部/参数**从不产出引用**（C# 有完整实现）。修复三层：①formatter 读取 dnlib Param/Local/SourceLocal（nodeStack 节点 + Identifier token 双路径）；②converter 变量映射扩展 UsingLocal/ForeachLocal 按 slot、参数位置回退、提升变量合成 SourceLocal 按 ILVariable 实例缓存；③OutputVisitor 将决定性引用经 extraData 透传。全语料实测：局部/参数引用 38221/82864（46%）→ **67534+/77379（87%+）**；**引用率缺口对（(text,color) 聚合）降至 0**——VB 后端与 2.3 在聚合级别完全对齐。剩余：个别站点（transform 重建节点）±形态差异 |

## 8. 新增三视图（IL / ILAst / Mixed）专项审查（2026-11-16）

> 「差异项 / 未覆盖项」专项：三路并行勘察（契约面 / 注册面 / 选项面）+ 关键结论复核。路径缩写：**A** = `Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy.Core`，**B** = `Extensions/ILSpy32.Decompiler/dnSpy.Decompiler.ILSpy32`。

### 8.1 契约面差异（DecompilerBase / IDecompiler）

| 项 | 2.3 | 3.2 | 影响 |
|---|---|---|---|
| `WriteToolTip` | IL/Mixed 有（A\IL\ILDecompiler.cs:193、A\Mixed\ILWithCSharpDecompiler.cs:264，走 `ILDecompilerUtils.Write`） | 三者均无（落到基类 `DecompilerBase.cs:245`） | 悬停 tooltip 是基类默认风格，非 IL 风格 |
| `TypeToString` | 三者均有（A\IL:190、A\Mixed:261、A\ILAst:384），走 dnlib `WriteTo(…, ILNameSyntax)` | 三者均无（落到基类 `:235`） | 类型名格式化策略与 2.3 不同 |
| `Decompile(TypeDef)`（ILAst） | 写 `// Type:`、`// Base type:`、嵌套类型、字段、属性、事件、方法（A\ILAst\ILAstDecompiler.cs:324-363） | 只遍历方法（B\ILAst\ILAstDecompiler32.cs:211-218） | 打开类型节点时输出远少于 2.3 |
| `Decompile(AssemblyDef/ModuleDef)`（ILAst） | 未 override，走基类（仅 `// 路径` + `// FullName`） | Assembly→Module 转发 + 遍历 `mod.Types`（B\ILAst:220-230） | 3.2 输出更多（增强，非缺口） |
| `OrderUI` | 各常量 | 常量 + 0.5（B\IL:67、B\ILAst:68、B\Mixed:64） | 2.3 在前、3.2 紧随，符合设计 |
| 语言数量 / 注册条件 | ILAst 仅 `#if DEBUG` 且每个优化步一个实例（A\ILAst:48,56,365-380）；Mixed 单实例无条件 | ILAst 单实例、无条件注册（B\CSharp\DecompilerCreator.cs:52-54） | 设计取舍（见 `docs/ILAst-Mixed-3.2.md` 第 5 节） |
| `FileExtension` / `ContentTypeString` | `.il` + `IL ILSpy` / `ILAst ILSpy` / `IL with C# ILSpy` | 逐字一致（B\IL:62,70、B\ILAst:65,71、B\Mixed:61,67） | 无差异 ✅ |

### 8.2 选项面差异

2.3 IL 视图共 8 个选项，全部映射引擎 `DisassemblerOptions`（A\Settings\ILDecompilerSettings.cs）；2.3 Mixed 复用该类并读全部 8 项（A\Mixed:40-42,63-75）；2.3 ILAst 有独立设置类但 `Options` 为空且 `#if DEBUG`（A\Settings\ILAstDecompilerSettings.cs:26-44）。

| 2.3 选项 | 3.2 现状 | 定性 |
|---|---|---|
| ShowXmlDocumentation | 由 `ILDecompilerSettings32` 开关控制（默认开，同 2.3） | ✅ 已做开关 |
| ShowTokenAndRvaComments | 同上（默认开） | ✅ 已做开关 |
| ShowILBytes | 同上（默认开） | ✅ 已做开关 |
| ShowPdbInfo | 同上（默认开） | ✅ 已做开关 |
| ShowILComments（opcode 文档） | ✅ 已支持（引擎 `MethodBodyDisassembler.GetOpCodeDocumentation` + `Common/OpCodeDocumentation32`，开关默认关） | ✅ 已对齐 |
| SortMembers | ✅ 已支持（`CecilMemberComparer`，开关默认关） | ✅ 已对齐 |
| MaxStringLength | ✅ 已支持（`DisassemblerHelpers.ConvertStringMaxLength`；默认 20000 = 2.3 `ConstMaxStringLength`） | ✅ 已对齐 |
| HexadecimalNumbers | ✅ 已支持（`NumberFormatter.GetCSharpInstance`，开关默认关） | ✅ 已对齐 |
| —（3.2 新增） | ILAst 4 项：Run IL transforms / Show IL ranges / logic sugar / field sugar（B\ILAst\ILAstDecompilerSettings32.cs:94-106） | 2.3 无对应 |

补充：Mixed 的 C# 叠加层已改为使用**持久化的 C# 单例**（`DecompilerSettings32Impl`，与 C# 视图同一实例；反射加载器回退到默认实例）——与 2.3 Mixed 读持久化 `CSharpVBDecompilerSettings` 一致（P2⑦ 已修）。

### 8.3 注册面差异（M4 移除 2.3 的影响）

> ✅ **P0 已修（2026-11-16）**：① 3.2 扩展已补 6 个 `ContentTypeDefinition` 导出（`B\ContentTypeDefinitions.cs`，名称与父链同 2.3）；② 已补 `ISimpleILPrinter` 导出（`B\IL\SimpleILPrinter32.cs`，与 2.3 逐字一致）。下文保留原始影响分析作为记录。

- **内容类型 100% 依赖 2.3**：3.2 扩展**无 `ContentTypeDefinition` 导出**（`B\ContentTypeDefinitions.cs` 仅常量；全扩展只有 3 个 `[Export]`：`CSharp\DecompilerCreator.cs:30` + `Settings\DecompilerSettings32Impl.cs:31,86`）。2.3 注册 6 个（`dnSpy.Decompiler.ILSpy\ContentTypeDefinitions.cs:28,33,39,45,51,56`）。→ **M4 后 3.2 五语言的 ContentTypeString 悬空**（语法高亮 / 文本视图设置失效）。
- **`ISimpleILPrinter` 唯一导出在 2.3**（`dnSpy.Decompiler.ILSpy\IL\SimpleILPrinter.cs:29`）→ `Extensions\dnSpy.AsmEditor\MethodBody\BodyUtils.cs:45` 回退 `DummyPrinter`（:47 输出 "Missing ISimpleILPrinter"）。3.2 无导出。
- **设置页**：2.3 有 `IAppSettingsPageProvider` 两页（C#/IL，`Settings\DecompilerSettingsPageProvider.cs:26-40`）；3.2 扩展仍无设置页（其 `Options` 可被 `dnSpy.Console --set/--dont-*` 与 `NodeDecompiler.GetBoolean` 消费），但 **IL/ILAst 设置已持久化**（P2⑧）：`ILDecompilerSettings32Impl` / `ILAstDecompilerSettings32Impl` 两个 MEF 单例，与 2.3 的 `ILSettingsImpl` 同等地位。
- **设置迁移**：`dnSpy\dnSpy\Decompiler\DecompilerServiceSettings.cs:79-96` 映射 2.3 C#/VB 的 UniqueGuid；2.3 IL 选项段 `DD6752B1` 已补迁移方（`ILDecompilerSettings32Impl.MigrateFrom23` → 3.2 支持的四个选项；其余 4 项 3.2 引擎无能力）。
- **2.3 引擎子模块不可删**：`Extensions\dnSpy.BamlDecompiler\dnSpy.BamlDecompiler.csproj:27` 与 `Extensions\ILSpy32.Decompiler\ICSharpCode.NRefactory.VB32\ICSharpCode.NRefactory.VB32.csproj:24-31` 引用 2.3 的 `ICSharpCode.Decompiler` / NRefactory 源。

### 8.4 消费面（已核）

- `Extensions\dnSpy.Analyzer\TreeNodes\AnalyzerTreeNodeData.cs:117` 已含 3.2 C# UniqueGuid；2.3 分支在 M4 后成死代码（非缺陷）。
- Debugger 侧（`DbgDotNetLanguageServiceImpl`、`DbgMethodDebugInfoProvider` 等）走族 GUID + `IDecompilerLanguageSettings.LanguageGuid`，语言无关。
- `dnSpy\dnSpy.Console\Program.cs:228` 硬编码 `"dnSpy.Decompiler.ILSpy.Core"`；M4 需改为只加载 3.2。

### 8.5 结论（按建议优先级）

- **P0（已修）**：① ✅ 3.2 扩展补 6 个 `ContentTypeDefinition` 导出（`ContentTypeDefinitions.cs`，五语言改用常量）；② ✅ 补 `ISimpleILPrinter` 导出（`IL/SimpleILPrinter32.cs`）+ `Common/DnlibILText32.cs`（2.3 dnlib→IL 写入器移植）+ `Common/ILDecompilerUtils32.cs`；对拍 **8565 样本 diffSamples=0**（两侧各 1,543,426 字符逐字相同）。
- **P1（对齐）**：③ ✅ 三语言补 `WriteToolTip` / `TypeToString`；④ ✅ ILAst 的 `Decompile(TypeDef)` 已补全；⑤ ✅ IL/Mixed 的 2.3 装饰已做成开关（`ILDecompilerSettings32` 四个 bool，GUID/默认值同 2.3；IL 与 Mixed 共用该类但各自持有实例）。已用 `dnSpy.Console --dont-*` 逐个验证：XML 9→0、Token/RVA 1→0、IL 字节 73→0、PDB 12→0（Mixed 同样生效）；默认输出仍与 2.3 逐字一致。
- **构建管线（新发现，已修）**：`dotnet build dnSpy.sln -c Release -f net10.0-windows` 会把 **Debug** 版 3.2 引擎 DLL 放进 dnSpy bin（引擎不在 sln 内，MSBuild 对引用外用默认配置）——已实测：sln 构建后 `ILSpy32.Decompiler.dll` 为 1,832,960 字节（Debug），单独构建扩展后为 1,585,152（Release）。已在 `build.ps1` 的 `Build-Net` 里显式以 Release 重建引擎与扩展（两条分支都加）；把引擎加入 sln 的替代方案与 `-f` 冲突（NETSDK1005，引擎是 netstandard2.0-only），已回退。
- **P2（引擎能力缺口 / 清理）**：⑥ ✅ 四个引擎能力缺口已全部补齐（ShowILComments / SortMembers / MaxStringLength / HexadecimalNumbers），均 opt-in、默认同 2.3；对拍（`dnSpy.Console --comments/--hex/--max-string-length/--sort-members`）逐项 diff 0。⑦ ✅ Mixed 的 C# 叠加改用持久化 C# 设置；⑧ ✅ IL/ILAst 设置持久化 + 2.3 IL 选项迁移。
- **IL 视图现状**：方法级与类型级（`-t`，含/不含 `--sort-members`）在抽样中与 2.3 逐字一致，唯一系统性例外是 `ldelem.any` 的名称（详见 §8.6）；期间修掉三处类型级差异：参数特性缩进、访问器声明缺 `// Token:` 注释、`.locals` 名未转义。

### 8.6 已知残余差异（2026-11-17，用 probe `lang-diff` 对拍量化）

对拍方法：`m3probe.dll lang-diff <asm> <类型> <dnSpyBin> <2.3目录>`（2.3 产物按
`baseline/README.md` 重建；M4 后 dnSpy bin 内不再有 2.3）。

| 类型（dnSpy.Contracts.Logic） | C# diff | VB diff | IL diff | token 数 2.3/3.2 |
|---|---|---|---|---|
| `MethodDebugInfoBuilder` | 142 行 | 107 行 | **0** | 28 / 28 |
| `MethodDebugInfo` | — | — | 4 行 | 27 / 27 |
| `MethodDebugScope` | — | — | **0** | 12 / 12 |

期间又修掉三项（2026-11-17）：
- **自动属性折叠**：2.3（NRefactory）把全空访问器列表折成一行 `{ get; set; }`；3.2 的
  `CSharpOutputVisitor` 一直忽略 `AutoPropertyFormatting`。已实现（`ForceOneLine`，即 2.3 的
  `SingleLine`）。
- **成员顺序**：2.3 默认（SortMembers 关）是**元数据 token 顺序**（属性/索引器按访问器、
  事件按 add/remove 计），3.2 引擎把字段放最前。`ReorderMembers` 现在默认也跑（token 序），
  并且必须带着每个成员的注释一起搬、且排在 `InsertTokenComments` 之前。VB 同路径。
- **RVA 段缺失（真 bug）**：dnlib `MethodDef.RVA` 是 `dnlib.PE.RVA` 结构，`{...:X8}` 会抛
  异常被吞掉 → 3.2 的 C# token 注释一直没有 `RVA:/File Offset:`（现 16/16）。
- 结论：C# 差异 226 → **142** 行、VB 110 → **107** 行，剩余为属性/访问器注释位置等零散形态。

结论：
- **token 集合与数量逐一相同**——C#/VB 的差异是**形态**而非引用缺失，符合项目既定的
  「token 级对齐」口径（见 §3），不是回归。
- **两类系统性形态差异**：
  1. **成员顺序**：字段/属性/方法的排列与 2.3 不同（同一集合）。
  2. **自动属性折叠**：✅ 已修（见上）。剩余的相关差异是属性/访问器注释的位置。
- **Mixed**：类型级已补上 C# 叠加（本轮修复），剩余差异是叠加行粒度（3.2 的语句级
  sequence point 更细），见 `docs/ILAst-Mixed-3.2.md` 第 5 节。
- **IL 视图**：抽样中逐字一致，**唯一系统性例外**是 `ldelem.any`（0xA3）：2.3（dnlib 版）印 `ldelem`，3.2（cecil 版）印 `ldelem.any`——`MethodDebugInfo` 样本的 4 行差异全是这一处（语义相同，cecil/现代 dnSpy 的写法）。

### 8.7 整程序集对拍（2026-11-17，probe `lang-diff-all`）

`m3probe.dll lang-diff-all <asm> <dnspyBin> <2.3目录> [langFilter]`：对程序集内**每个类型**逐语言对拍，
报告 identical / diffLines / errors。相比 3 个手工样本，这个口径暴露了两处系统性差异：

| 程序集 | 语言 | 类型数 | 逐字一致 | diffLines |
|---|---|---|---|---|
| dnSpy.Contracts.Logic | IL | 114 | **85** | 2968 |
| dnSpy.Contracts.Logic | C# | 114 | 28 | 11379 |
| dnSpy.Contracts.Logic | VB | 114 | 10 | 11828 |
| dnSpy.Contracts.Logic | Mixed | 114 | 26 | 74637 |
| dnSpy.Contracts.Debugger | IL | 303 | **256** | 1400 |
| dnSpy.Contracts.Debugger | C# | 303 | 97 | 8496 |
| dnSpy.Contracts.Debugger | VB | 303 | 36 | 12115 |

（Mixed 的 diffLines 含 C# 叠加行，口径不可比，仅作参考。）

本轮据此修掉的 IL 系统性缺口：
- **标志名表**：`export`→`unmanagedexp`、补 `nativevararg`/`unmanaged`、补
  `aggressiveinlining`/`aggressiveoptimization`/`securitymitigations`、补 `rtspecialname`、
  `auto`→`autochar`（后者两者都用）。
- **泛型参数特性**：2.3 输出 `.param type <name>` / `.param constraint ...`（泛型参数上的
  可空注解就挂在这里），3.2 完全没有——已补。

剩余（未修，均需逐条裁决）：
- **using 顺序**：3.2 为 ordinal 升序（`Microsoft.Win32` 在最前），2.3 把 `Microsoft.*` 排在
  `System.*` 之后——不是简单字典序，疑似按引入顺序，需单独对齐。
- **嵌套类型位置**：2.3 把嵌套类型放在**最后**，3.2 的 `ReorderMembers` 似乎没有移动它
  （仍排在最前），需要查清它是否不在 `TypeDeclaration.Members` 里。
- C# 零散形态（大类型里 560/447 行级的差异主要来自上面两条）。

### 8.8 using 顺序：2.3 的确切规则（已确证，未实现）

2.3 的 `IntroduceUsingDeclarations` 读取设置 `SortSystemUsingStatementsFirst`
（`DecompilerSettings.cs:436`，默认 true），用专用比较器排序后再**反序插入**：

```
System / System.*  → 排最前；两组内部均 OrdinalIgnoreCase 降序（反序插入后 = 升序）
其余命名空间        → 之后，OrdinalIgnoreCase 升序
```

实测三例全部吻合：`…System.Text.RegularExpressions, Microsoft.Win32`、
`…System.Runtime.CompilerServices, dnlib.DotNet`、`…System.Linq, System.Runtime.CompilerServices`。

3.2 引擎**没有这个设置**：`CSharp/Transforms/IntroduceUsingDeclarations.cs:60` 只会
`OrderByDescending(n => n, StringComparer.Ordinal)`（即最终 ordinal 升序），于是
`Microsoft.*` / `dnlib.*` 会被排到 `System.*` 之前——这是 3.2 与 2.3 在**每个文件**上
都可见的差异。

未实现的原因：`UsingDeclaration` 在 3.2 AST 里是 `AstNode`（位于 `Children`，不在
`Members`），重排需要在 `Children` 上带 role 插入（`InsertChildBefore`），比
`ReorderMembers` 更容易出错，留作专项。

**已实现（2026-11-17）**：引擎新增 `SortSystemUsingStatementsFirst`（默认关，`DecompilerSettings32` 开启，C#/VB 同时生效，提交 `245ca8c`）。三例 using 块现与 2.3 完全一致。

### 8.9 整程序集对拍收敛后的残余（2026-11-17）

`lang-diff-all` 固化为验收基线（`verify-all` 软门）后，逐轮修掉系统性差异，DnSpy.dll 的
diffLines 从 20937 → 2058 → **116**：

| 修掉的系统性差异 | 效果 |
|---|---|
| Events 段后多一个空行 | DnSpy.dll -30 行级 |
| 接口实现特性内联在 `implements` 子句（应作类体内 `.interfaceimpl`） | DnSpy.dll identical +122 |
| 方法自身泛型参数不发 `.param type T` | DnSpy.dll diffLines 2058 → 116；IScriptGlobals 300 → 0 |

残余（最大 38 行/类型，均为可解释的形态差异）：
- **`ldelem.any` vs `ldelem`**：0xA3 的名称。3.2（cecil）印全名，2.3（dnlib）印 `ldelem`。短名是 0xA3 的规范 ILASM 助记符，已对齐 2.3（见 §8.11）。
- **局部名引号**：2.3 对与 IL 关键字同名的局部名加单引号（`'field'`、`'property'`），3.2 不转义。规则明确但需改引擎 operand 写入，留作专项。
- C#/VB 的 `this.` 省略 / 局部名（PDB）/ 嵌套类型限定：既定风格或需读符号，见 §3、§8.7。

当前整程序集 identical 类型数：IL **DnSpy.dll 1246/1310、Debugger 296/303、Logic 107/114**。

### 8.10 残余 IL 差异的构成（2026-11-17 收敛后）

DnSpy.dll 的 IL 差异行数 20937 → **43**（identical 1104 → **1296/1310**）。抽样确认残余几乎
全部是**同一类**：`ldelem`/`stelem`（0xA3/0xA4）的名称——3.2（cecil）印 ECMA 全名
`ldelem.any`/`stelem.any`，2.3（dnlib）印旧名 `ldelem`/`stelem`。短名是 0xA3/0xA4 的规范 ILASM 助记符，**已对齐 2.3**（见 §8.11）。

其余零星（每类型 1–7 行）为同类命名/顺序细节。结论：IL 视图的残余差异已收敛到「opcode 名称
口径」这一项，不再是未知缺口。

### 8.11 IL 视图：三程序集 100% 逐字一致（2026-11-17）

`ldelem`/`stelem` 名称对齐后（短名是 0xA3/0xA4 的规范 ILASM 助记符，2.3 即如此），
IL 视图在三个程序集上达到 **100% 逐字一致**：

| 程序集 | 类型数 | identical | diffLines |
|---|---|---|---|
| dnSpy.Contracts.DnSpy.dll | 1310 | **1310** | **0** |
| dnSpy.Contracts.Debugger.dll | 303 | **303** | **0** |
| dnSpy.Contracts.Logic.dll | 114 | **114** | **0** |

收敛路径：Events 段空行 → 接口实现特性改 `.interfaceimpl` → 方法泛型参数 `.param type T`
→ 局部名转义 → `ldelem`/`stelem` 短名。每一步都由 `lang-diff-all`（整程序集逐类型对拍）
定位，而非手工样本。

### 8.12 局部变量名：尝试通过 PDB 消除（**已于同日解决，见 §8.32**）

C#/VB 残余差异中最大一类是局部名（2.3 有 PDB 名 `sharedPaths/info`，3.2 是 `list/dotNetBaseDir`）。
尝试给桥接快照开启 `ReadSymbols = true`：

- **性能**：无额外开销（Debugger.dll 反编译 254–368 ms vs 关闭时 338–575 ms，此环境 PDB 已缓存）。
- **结果**：**模块加载失败**——C# 输出从 ~20 KB 崩到 74 字符。原因是该环境无法为这些程序集读到可用符号
  （PDB 不在位/格式不支持），cecil 的符号读取直接让 `ReadModule` 失败。已回退，未留半成品。

**结论**：局部名差异不能通过对拍路径消除；要么为目标程序集提供可用 PDB（并处理 cecil 符号读取失败），
要么接受该显示风格差异。列为已知限制。

> **§8.32 修正**：上面「PDB 不在位」是错的 —— PDB 一直都在
> （`dnSpy.Contracts.DnSpy.pdb` 321 KB）。真正的原因是**桥从内存流读取**（`ReadSymbols = false`），
> 而 cecil 是按「模块文件名旁的 PDB」定位符号的。改为「有文件路径就从文件读 + `ReadSymbols = true`」，
> 并保留读符号失败时回落到无符号读取，局部名即与 2.3 对齐（语料 -711 行）。

### 8.13 C#/VB 残余差异的类别盘点（2026-11-17，**已按逐类型语料重写**）

> 本节早先用 `dnSpy.dll` 的 `--all` 转储 + 14 个样本分桶，结论（「隐式 `this.` 最大、局部名需 PDB」）
> 已被后续工作推翻：`this.` 已由 §8.17 修复，局部名已由 §8.32 修复，且 `--all` 口径本身不具代表性（§8.28）。
> 下面是**逐类型语料**（`lang-diff-all`，与真实视图一致）口径下的当前结论。

剩余差异（语料 diffLines 46364 → **35118** 之后）按可修性分三类：

| 类别 | 实例 | 状态 |
|---|---|---|
| **控制流塑形** | 2.3 `bool flag; try { flag = X; } catch { flag = false; } return flag;` vs 3.2 直接 `return`；`A \|\| !B` vs 提前返回 | 引擎 transform 层，**未修** |
| **可空枚举比较** | 2.3 `e.ChangedButton == null` vs 3.2 `e.ChangedButton != 0` | 2.3 的 `MakeDefaultValue` 对 `Nullable<T>` 返回 `null`；比较路径未对齐，**未修** |
| **方法体内枚举命名** | 2.3 `TokenKind.Text` vs 3.2 `0` | 未解释（§8.25 的规则应当命名模块内枚举），**未修** |
| **索引器语法** | 2.3 `word.get_Chars(0)` vs 3.2 `word[0]` | 语料约 27 处，**未修** |
| **委托构造显式类型** | 2.3 `new Thread(new ThreadStart(this.M))` vs 3.2 省略 | 仅 `--all` 口径可见（325 处），需逐类型复核，**未修** |
| **数组初始化器** | 2.3 `[Nullable(new byte[] { 2, 1 })]` 内联 | 见 §8.31，渲染路径未定位 |

**结论**：剩余差异不再是「接线/设置缺口」，而是**引擎输出层与 transform 语义**的差异；
其中控制流塑形是最大类，需逐个对齐 `AstMethodBodyBuilder` 系列 transform，建议单独立项。

**更正（抽样实测，历史）**：原表「局部常量内联」一桶**不成立** —— 抽样类型中 `const ` 两边各 1 处且内容相同。

### 8.14 C#/VB 残余：编译器生成类型（2026-11-17）

把 C#/VB 对拍扩到第三方真实库（Iced/MessagePack/Mono.Cecil）后，identical 骤降
（Iced C# 104/764、MessagePack 16/297），但抽样确认**主因是编译器生成的类型**，
其差异不是接线问题，而是引擎输出层规则：

| 差异 | 2.3 | 3.2 |
|---|---|---|
| 组合枚举值 | `[AttributeUsage(27524, …)]` 保留数值 | 展开为 `AttributeTargets.Class \| …` |
| 生成参数名 | `byte A_1` | `byte P_0` |
| 数组初始化 | `new byte[] { A_1 }` | `new byte[1] { P_0 }` |
| `this.` | 显式 | 省略 |
| 局部名 | PDB 名 | 生成名（需 PDB） |

「组合枚举值分解」是可明确规则的差异（影响所有 netstandard 引用程序集里编译器生成的
`NullableAttribute`/`NullableContextAttribute`/`RefSafetyRulesAttribute` 等），但会改变
**所有 enum 常量**的打印路径，属引擎输出层改动，需专项评估（默认保留 3.2 行为）。
其余为 PDB（§8.12）与既定显示风格（§3、§8.13）。

**C#/VB corpus 扩展（2026-11-17）**：把对拍扩到第三方真实库后 identical 骤降（Iced C# 104/764、
MessagePack 16/297），抽样确认**主因是编译器生成的类型**（`NullableAttribute`/`NullableContextAttribute`/
`RefSafetyRulesAttribute` 等，位于引用程序集）。差异构成：组合枚举值（§8.14）、生成参数名
（`A_1` vs `P_0`）、数组初始化形态、`this.` 省略。**C#/VB 残余不是接线缺口**；其中 `this.` 与
组合枚举值可用 AST/引擎变换对齐，但会改变全部 C# 输出形态，需单独立项评估。

### 8.15 属性参数的组合枚举值：**接受的不一致**（结论已被 §8.17 推翻并修复）

（已解决，见 §8.17）实测：2.3 印数值 `[AttributeUsage(27524, …)]`，3.2 曾展开为
`AttributeTargets.Class | AttributeTargets.Property | …`。根因：3.2 的 `AstBuilder` 为
**属性参数**构建了 `|` 表达式（2.3 只留 primitive int）。属性参数走
`CSharpOutputVisitor.VisitAttribute`（`CSharpOutputVisitor.cs:1287`）→ 参数表达式。

已在引擎侧复刻（属性参数中，外部枚举回退为数值），见 §8.17。

### 8.16 Mixed 叠加粒度：实测结论（2026-11-17）

对拍 `MethodDebugInfoBuilder`（1146 行 IL）后：叠加行数 2.3=217、3.2=222（几乎相同，**粒度本身是对齐的**）。
870 行 diff 的构成：**隐式 `this.`**（2.3 显式，3.2 省略，抽样 19:1）、局部名（PDB）、
语句形态（`{`/`for`/`return` 分布）。叠加缩进分布 2.3 为 4/8/12（成员级基准 + 4×深度），
3.2 为 8/12/16（语句级基准）；试将 3.2 整体减 4 后顶层对齐（166 vs 164）但**嵌套分布反转**
（一层 36 vs 17），收益不明确，**已回退**。
结论：Mixed 叠加的差异与 C#/VB 同源（§8.13），无独立缺口；`this.` 省略为最大可见项。

### 8.17 `this.` 限定与属性枚举值：已复刻（2026-11-17）

之前把这两项判为「接受的不一致」，实测后确认都是**可复刻的规则**，已由并行代理在引擎侧实现
（提交 `f953ef7`）：

- **`this.`**：2.3 在 AST 构建期（`AstMethodBodyBuilder`）给实例字段/属性/方法/事件/indexer 访问
  设 `ThisReferenceExpression` 目标；3.2 的 `ExpressionBuilder.ConvertField` 与 `CallBuilder`
  （普通调用、访问器、委托方法组）此前不加。抽样 this. 计数 2.3=22 vs 3.2=1 → 修后 23，逐行对齐。
- **属性参数枚举**：2.3 只在枚举类型**可解析**（同程序集）时展开名字，**外部枚举**（`AttributeTargets`、
  `EditorBrowsableState`、`StringComparison`）回退为数值。3.2 总是展开。现 `TypeSystemAstBuilder`
  对**属性参数**复刻该回退（`!IsMainAssembly` 时数值）。

语料 identical/diffLines：DnSpy.dll 556/32248 → **741/31073**；Debugger 108/5735 → **186/5449**；
Logic 29/10021 → **44/9842**。VB 亦同步（VB 由 C# AST 转换而来，`Me.` 与 2.3 一致）。

### 8.18 静态字段初始化器：已复刻（2026-11-17）

**差异**：2.3 把 cctor 开头的 `字段 = 表达式;` 序列**提升回字段声明**（`private static readonly
Dictionary<…> toDebuggerColor = new Dictionary<…>();`），cctor 里只剩真正的逻辑（`ColorConverter.AddColors(…)`）；
3.2 保留在 cctor 内联（`toDebuggerColor = new …; toDnSpyColor = new …; AddColors(…)`），字段声明不带初始化器。

**根因**：引擎转换 `CSharp/Transforms/ConvertConstructorCallIntoInitializer.cs` 的
`HandleStaticFieldInitializers` 有一道 `ctorMethodDef.DeclaringType.IsBeforeFieldInit` 门槛，
只有 BeforeFieldInit 类型才提升。实测 `dnSpy.Contracts.Debugger.Text.DnSpy.ColorConverter`
的 IL 为 `.class public auto ansi abstract sealed`（**无 `beforefieldinit`**），2.3 依然提升
→ 2.3 侧**没有这道门槛**。

**修改**：去掉该门槛（并随之去掉仅用于判定的 `GetCecil` 调用与 `using Mono.Cecil;`），
把 cctor 前缀的静态字段/属性赋值无条件提升回声明处；提升后 cctor 为空则删除。
只提升「赋值表达式 + 静态字段/属性目标」的前缀，顺序与副作用次序不变。

**验证**：
- 单类型：`ColorConverter` 的字段声明已与 2.3 逐字节一致，cctor 内联的两行消失（该类型晋升为 identical）。
- 语料（C#，identical/diffLines）：DnSpy.dll 741/31073 → **741/30973**；Debugger 186/5449 → **187/5442**；
  Logic 44/9842 → **44/9824**。合计 diffLines 46364 → **46239**，identical 971 → **972**。
- `verify-all`：hardFail=0、skip=0；唯一 softFail 为单类型 `lang-diff`（Mixed 870 / VB 70，
  与修改前逐字节相同的数字），IL 语料仍 1310/303/114 全 identical、diffLines=0。

**剩余同源差异**（不属本节）：静态调用的类型限定（2.3 `ColorConverter.AddColors` vs 3.2 `AddColors`）、
数组初始化器的换行密度、枚举装箱形态（`BoxedTextColor.Box(91)` vs `TextColor.Error.Box()`）、
`out var` vs `ref` 局部 + 语句形态、局部名（PDB）。

### 8.19 静态成员访问的类型限定：已复刻（2026-11-17）

**差异**：2.3 对**静态成员访问**（静态字段、静态方法、静态属性/事件访问器、委托方法组）
**无条件**用声明类型名限定，即使声明类型就是当前类型或包含类型：

| 位置 | 2.3 | 3.2（修前） |
|---|---|---|
| 静态方法 | `ColorConverter.AddColors(…)` | `AddColors(…)` |
| 静态字段 | `ColorConverter.toDebuggerColor.Add(…)` | `toDebuggerColor.Add(…)` |
| 静态字段（私有） | `DbgCodeBreakpointSettings.emptyLabels` | `emptyLabels` |
| 静态方法（私有） | `DbgCodeBreakpointSettings.LabelsEquals(…)` | `LabelsEquals(…)` |

**证据**：对 `dnSpy.Contracts.Debugger.dll` 全量转储后统计「代码行内（排除 XML 文档注释）
以自身短名限定的访问」，2.3 有 13/8/6/5/5/4… 处（DbgEngineModule、DbgEngineThread、
ColorConverter、DbgExceptionId、DbgCodeBreakpointSettings…），**3.2 为 0**。

**根因**：`ExpressionBuilder.ConvertField` 与 `CallBuilder` 的三处 `requireTarget` 判定
（普通调用 / 访问器调用 / 委托方法组）对静态成员写作
`requireTarget = !IsCurrentOrContainingType(declaringType)`，即「声明类型是当前或包含类型时省略限定」。
2.3 无此条件。

**修改**：上述 4 处对静态成员一律 `requireTarget = true`（与 §8.17 的 `this.` 同源的输出层规则）。

**验证**：
- 单类型：`ColorConverter` 的 `ColorConverter.AddColors` / `ColorConverter.toDebuggerColor` 已与 2.3 一致。
- 语料（C#，identical/diffLines）：DnSpy.dll 741/30973 → **742/30623**；Debugger 187/5442 → **187/5440**；
  Logic 45/9824 → **45/9768**。合计 diffLines 46239 → **45831**（-408），identical 972 → **974**。
- `verify-all`：hardFail=0、skip=0；唯一 softFail 仍是既有的单类型 `lang-diff`（数字未变）；
  IL 语料保持 1310/303/114 全 identical。

**仍未对齐（待查项）**：**嵌套类型名**的限定。2.3 印 `DbgEngineModule.UpdateOptions.IsExe`、
`DbgExceptionId.Flags.KindMask`、`(DbgExceptionId.Flags)0`，3.2 印 `UpdateOptions.IsExe`、
`Flags.KindMask`、`(Flags)0`。这些出现在**枚举常量 / 枚举转换**上下文（`ConvertEnumValue` →
`TypeReferenceExpression(ConvertType(type))`），而同一类型作为字段声明类型时 3.2 已经是限定的
（`private readonly DbgExceptionId.Flags flags;`），两者相矛盾，说明决策点不在 `ConvertTypeHelper`
的短名分支（`AlwaysUseShortTypeNames=true` 时该分支恒命中）。曾试在 `ConvertTypeHelper` 加
`DeclaringType == null` 门槛：语料仅 -14 行、枚举路径完全未变，**已回退**，待定位真实决策点。

### 8.20 全解决方案构建：抓到并修复 net48 目标缺口（2026-11-17）

**背景**：此前的构建/对拍都只针对 `net10.0-windows`（`verify-all`、`run-regression` 均带 `-f net10.0-windows`），
**从未验证过 net48**。而 dnSpy 同时发布 net48 与 net10.0-windows，解决方案里两个目标都在。

**发现**：`dotnet build dnSpy.sln -c Release` 直接失败，4 个错误全部落在
`dnSpy.Decompiler.ILSpy32/CSharp/CSharpDecompiler32.cs(301)`：

```
error CS0411 / CS8129 / CS8130: KeyValuePair<ILFunction, List<SequencePoint>> 没有
适用的 Deconstruct 扩展方法（out 参数 2 个、返回 void）
```

根因：`KeyValuePair.Deconstruct` 是 .NET Core 2.0 / .NET Standard 2.1 才加入的扩展，
** .NET Framework（net48）上没有**；`TupleExtensions.Deconstruct` 只覆盖 `Tuple<,>`。

**修改**：该处改为显式 `foreach (var sequencePoint in sequencePoints) { var function = sequencePoint.Key; var sps = sequencePoint.Value; … }`，
语义完全等价，两个目标都能编译。

**验证**：
- `dotnet build Extensions/ILSpy32.Decompiler/dnSpy.Decompiler.ILSpy32/dnSpy.Decompiler.ILSpy32.csproj -c Release -f net48` → **0 错误**。
- `dotnet build dnSpy.sln -c Release`（M4 后的完整解决方案：2.3 扩展已移出，2.3 引擎子模块仍由
  `dnSpy.BamlDecompiler` / `ICSharpCode.NRefactory.VB32` 引用）→ **已成功生成，0 错误**。
  中途出现的 `CS2001 GeneratedInternalTypeHelper.g.cs 缺失` 与 `CS0012 System.Runtime 10.0` 是
  中断的 WPF `_wpftmp` 临时工程留下的陈旧 obj，重跑即消失，非源码问题。
- `verify-all`：hardFail=0、skip=0，唯一 softFail 仍是既有单类型 `lang-diff`（数字未变）。
- **2026-11-17 复跑确认**：在干净状态（无并行/中断构建）下
  `dotnet build dnSpy.sln -c Release` → **已成功生成，0 错误**。
  说明 §8.20 里那几次 `CS2001 / CS0012 / MC1000 / MC2000` 全部是**中断构建留下的陈旧 obj**，
  与源码无关；门禁本身可用，但**不要紧跟在另一个构建之后运行**（会命中 WPF `_wpftmp` 临时工程竞态）。

**新增常态门禁**：后续改动必须同时构建 `net10.0-windows` 与 `net48` 两个目标
（`dotnet build dnSpy.sln -c Release`），只构建单目标不算验证通过。

### 8.21 两项待查的系统性差异（2026-11-17，仅诊断，未改代码）

**先确认基线有效性**：曾怀疑 2.3 与 3.2 读到了不同的 `dnSpy.dll`（同一类型
`ThemeClassificationFormatDefinitions` 的**字段 RID 不同**：2.3 首字段 RID 1339 / 3.2 RID 1532）。
核实结论：**程序集相同**——当前 `dnSpy.dll` 中 `DisabledAdvancedTracepointStatement…` 与
`NaturalLanguageClassificationTypeDefinition` 两组字段名**都存在**（二进制内可见），
类型 token 也一致（`0x020001C1`）。所以是**同类型、同字段集、不同输出形态**，基线有效。

**(1) 成员顺序**：`ThemeClassificationFormatDefinitions` 两引擎字段顺序完全不同
（2.3：NaturalLanguage→FormalLanguage→Literal→Identifier→Other→WhiteSpace→…；
3.2：DisabledAdvancedTracepointStatement→AsmMnemonic→Black→…）。3.2 的顺序既非字母序也非
RID 升序（首字段 RID 1532 > 2.3 的 1339），形似**哈希/字典序**，怀疑在 dnlib→cecil 桥
或成员枚举路径上丢失了元数据顺序。该类型单文件 diff 达 7141 行，是 DnSpy 语料最大单一差异源。
→ **部分修复，见 §8.22**（排序门控消除了「哈希状」重排，但 3.2 自身输出顺序仍非元数据序）。

**(2) 枚举常量的代码形态**：2.3 在**代码**里把枚举常量打印成**数值**，3.2 恢复成**枚举成员名**。
`dnSpy.Themes.ColorInfos` 中 `new BrushColorInfo(16528, "Selected text")`（2.3）vs
`new BrushColorInfo(ColorType.SelectedText, "Selected text")`（3.2）；该文件 2.3 里
`ColorType.` 出现 **0 次**（769 处数值），3.2 里出现 **754 次**。
同类还有 `this.blockFlags = (isXaml ? 7431 : 7175)`（2.3）vs
`(isXaml ? CodeBracesRangeFlags.XamlBlockBraces : CodeBracesRangeFlags.XmlBlockBraces)`（3.2）。
但 2.3 并非一律数值：`DbgImageLayout.Unknown`、`TextColor.Error` 在 2.3 里是带名的
（出现在 switch / 实例方法接收者位置）。**规则未钉死**：需先逆出 2.3「何时命名、何时数值」
（候选假设：switch 标签 / 接收者位置命名，其余数值；或按枚举可见性/所属程序集区分），
再决定 3.2 侧改法。全量 `dnSpy.dll` 原始行 diff 214470 行，其中 2.3 含 ≥2 位数字的行 13933、
3.2 含 `X.Y` 成员引用的行 25043，量级说明这两项是剩余差异的大头。

### 8.22 成员排序门控：已修复（2026-11-17）

**差异**：3.2 的 C# 视图把成员顺序重排成「哈希状」，与 2.3 的元数据顺序不符（§8.21(1)）。

**根因**：`CSharpDecompiler32.ReorderMembers` 里 5 个 `List<T>.Sort(...)`（methods / properties /
events / fields / nestedTypes）**无条件执行**。`List.Sort` 是不稳定排序（introsort），
而比较器在成员未绑定到 dnlib 定义时一律 `return 0` —— **全 0 比较器依然会重排**，
于是 323 个字段被置换成看似随机的顺序。探针文档本来就写明「off 时保持元数据顺序」，
代码却没有检查 `sortMembers`。

**修改**：把 5 个排序包进 `if (sortMembers) { ... }`。
**分组**（methods→properties→events→fields→nestedTypes）保持无条件 —— 2.3 默认也分组
（实测 `ColorConverter`：cctor → 方法 → 字段，两引擎一致）。

**验证**：
- 语料（C#，identical/diffLines）：Contracts.DnSpy 742/30623 → **767/29813**；
  Debugger 187/5440 → **195/5246**；Logic 45/9768 → **49/9278**。
  合计 diffLines 45831 → **44337**（-1494），identical 974 → **1011**。
- `dnSpy.dll` 最大差异类型 `ThemeClassificationFormatDefinitions`：7141 → **1285** 变更行。
- `verify-all`：hardFail=0、skip=0，唯一 softFail 仍是既有单类型 `lang-diff`（数字未变）；
  IL 语料保持全 identical。

**仍未对齐**：该类型剩 1285 行差异，首字段 2.3 是 RID 1339/1340/1341（连续，即元数据序），
3.2 是 RID 1660/1441/1442 —— 说明**去掉排序后 3.2 引擎自身输出的成员顺序就不是元数据顺序**，
根因在引擎/桥的成员枚举（`ITypeDefinition.GetMembers()` 的实现），待查。

### 8.23 成员顺序：两次后续实验均**回退**（2026-11-17）

在 §8.22 的基础上又试了两种「让 token 排序真正生效」的改法，**语料都变差，已全部回退**：

| 改法 | Contracts.DnSpy | Debugger | Logic |
|---|---|---|---|
| 基线（§8.22 已提交 `3755feb5b`） | **767 / 29813** | **195 / 5246** | **49 / 9278** |
| A：`GetMemberToken` 的 `direct` 分支补 `FieldDef` 注解 | 756 / 33219 | 193 / 5788 | 44 / 11752 |
| B：删掉 `else` 分支的 token 排序（纯分组 + 保序） | 575 / 42321 | 100 / 11018 | 32 / 11038 |

**结论**：
- token 排序是**必要**的（B 组 575 → 基线 767，说明 2.3 的成员顺序确实带有元数据 token 序的成分），
  但「把字段 token 正确绑定后的**全成员升序** token 排序」（A 组）反而更差。
- 因此 2.3 的成员顺序**既不是纯元数据序，也不是简单的全成员 token 升序**；§8.21(1) 里
  「2.3 首字段 1339 起连续」只是该类型的表象，不能推广。
- `GetMemberToken` 对字段目前大多落到 `int.MaxValue`（`GetFieldFor` 返回 null，见
  `CSharpDecompiler32.GetFieldFor`：它按 `member.Ancestors...Annotation<TypeDef>()` +
  `typeDef.Fields` 名称索引查找，疑似没命中）—— 这个「半坏」状态恰好是语料最优，
  **说明真正的规则还没找到，不要继续在 token 排序方向上调参**。已回退到 `3755feb5b`。

### 8.24 成员顺序：读 2.3 源码后按 ground truth 重写（2026-11-17）

§8.23 的两个实验都被「2.3 到底怎么排」这个问题挡住。本轮直接读了仓库里的 **2.3 引擎源码**
（`Extensions/ILSpy.Decompiler/ICSharpCode.Decompiler/.../Ast/AstBuilder.cs` 与
`dnSpy/dnSpy.Contracts.Logic/Decompiler/Extensions.cs`），拿到确定规则：

1. **组序**由 `DecompilerSettings.DecompilationObjects` 决定，默认
   `Methods, Properties, Events, Fields, NestedTypes`。
2. **未排序模式**（`UseSourceCodeOrder || !typeDef.CanSortMethods()`）走 `ShowAllMethods`
   → `GetNonSortedMethodsPropertiesEvents()`：**方法 / 属性 / 事件合并**，按
   「**首个访问器的 MDToken** 升序」排列（源码注释即 "original metadata order"）。
3. **字段不参与**该排序：`GetFields()` 返回 `type.Fields`（元数据序）。
4. 嵌套类型最后。

**修改**：`ReorderMembers` 的 `else`（未排序）分支改为 —— 只对
`methods + properties + events` 做 token 排序，字段保持引擎相对顺序（不排序），嵌套类型最后。

**验证**：
- 语料（C#，identical/diffLines）：Contracts.DnSpy 767/29813 → **782/24849**；
  Debugger 195/5246 → **196/5028**；Logic 49/9278 → **50/8441**。
  合计 diffLines 44337 → **38298**（**-6039**），identical 1011 → **1028**。
- `verify-all`：hardFail=0、skip=0，唯一 softFail 仍是既有单类型 `lang-diff`。

**§8.23 的 A/B 实验由此得到解释**：A 把**字段**也纳入了 token 排序（2.3 不排字段 → 错）；
B 完全去掉 token 排序（2.3 对方法/属性/事件确实按访问器 token 排 → 错）。两者都只对了一半。

### 8.25 枚举常量：引用程序集里的枚举打印数值（2026-11-17）

**差异**：2.3 在**代码**里把枚举常量打印成**数值**，3.2 恢复成**枚举成员名**（§8.21(2)）。

**ground truth**：2.3 的 `AstBuilder.CreateExpressionForConstant`（`Extensions/ILSpy.Decompiler/.../Ast/AstBuilder.cs:2030`）
对所有常量走 `MakePrimitive(value, type, sb)`；是否展开成员名取决于**枚举定义能否解析**，
而 2.3 只解析**被反编译模块内**定义的枚举。旁证：
- `DbgImageLayout`（定义在 `dnSpy.Contracts.Debugger.dll`，即当时被反编译的模块）→ 2.3 印 `DbgImageLayout.Unknown`；
- `ColorType`（定义在 `dnSpy.Contracts.DnSpy.dll`，而当时反编译的是 `dnSpy.dll`）→ 2.3 印 `16528`。

**修改**：`TypeSystemAstBuilder.ConvertConstantValue` 里已有的这条规则（原先只用于属性参数
`isAttributeArgument`，即 §8.15/§8.17 的组合枚举修复）**推广到所有枚举常量**：
枚举定义来自引用程序集（`!ParentAssembly.IsMainAssembly`）时返回 `PrimitiveExpression(数值)`。

**验证**：
- 语料（C#，identical/diffLines）：Contracts.DnSpy 782/24849 → **794/24769**；
  Debugger 196/5028（不变）；Logic 50/8441 → **50/8430**。合计 38298 → **38227**，identical 1028 → **1040**。
- `dnSpy.dll`（不在语料内的装配，但正是该差异的主战场）：`dnSpy.Themes.ColorInfos` 里
  `ColorType.` 出现次数 **754 → 0**（与 2.3 一致），该文件 diff 行数显著下降；
  `ThemeClassificationFormatDefinitions` 已跌出 dnSpy.dll 差异前 5。
- `verify-all`：hardFail=0、skip=0，唯一 softFail 仍是既有单类型 `lang-diff`。

### 8.26 剩余差异分类（2026-11-17，诊断结论）

`dnSpy.dll` 全量原始行 diff 已从 214470 降到 **172678**。分类统计：

| 类别 | 量级 | 性质 |
|---|---|---|
| **控制流塑形** | `return true/false/null;` **2328** 行、含 `flag` 临时局部 **1126** 行 | 引擎 transform 层 |
| **using 集合** | 共 **942** 行（3.2 多 271 / 少 671） | 导入计算口径 |

**控制流塑形**（剩余大头）举例（`dnSpy.Text.Operations.EditorOperations`）：
- 2.3：`bool flag; try { flag = Clipboard.ContainsText(); } catch { flag = false; } return flag;`
  vs 3.2：`try { return Clipboard.ContainsText(); } catch { return false; }`
- 2.3：`return A || !B;` vs 3.2：`if (!A) { return !B; } return true;`

即 2.3 更贴近 IL 原样（保留临时局部、保留 `||`），3.2 的 transform 更激进。
这**不是「一处规则」能对齐的**，需要逐个比对 `AstMethodBodyBuilder` 系列的 transform，
风险高，建议单独立项。

**using 集合**：3.2 多出的命名空间里 `System.Runtime.CompilerServices` 占 **209** 次，
且抽样文件中该 using **没有任何对应类型使用**（`Extension`/`InternalsVisibleTo`/`Nullable`/`IndexerName` 均 0 次），
两侧 `NullableContext`/`[Nullable]` 文件数相同（24 vs 24），所以**不是**可空特性的问题，
而是 3.2 的 using 计算把「已被 transform 消掉的类型引用」也算了进去。ROI 低（0.5%），暂不动。

**结论**：剩余差异已从「输出层规则差异」转为「引擎 transform 语义差异」，
继续往下需要逐个 transform 对齐，不再适合在审计轮里盲改。

**性能（目标里的「提升反编译运行效率」）**：`verify-all` 的 `bench` 门禁实测
最慢类型 **49 ms**（`dnSpy.Contracts.Decompiler.XmlDoc.XmlDocRenderer`），
其余 16/13/12 ms，且全部 `cecilCalls=0 fails=0`；语料耗时 Contracts.DnSpy C# 1310 类型 32 s
（约 25 ms/类型）。**没有可识别的热点**，桥层（`DnlibCecilBridge`）已按 dnlib `ModuleDef`
用 `ConditionalWeakTable` 缓存 Cecil 模块，不会重复读盘。此项暂无优化空间，
若后续要提速应先建立逐类型耗时基线再定位。

### 8.27 嵌套类型限定：按 2.3 源码修正（2026-11-17）

**差异**：2.3 印 `DbgEngineModule.UpdateOptions.IsExe`、`DbgExceptionId.Flags.KindMask`、
`(DbgExceptionId.Flags)0`，3.2 印 `UpdateOptions.IsExe`、`Flags.KindMask`、`(Flags)0`（§8.19 起记录的未解项）。

**ground truth**：2.3 的 `AstBuilder.ConvertType`（`Extensions/ILSpy.Decompiler/.../Ast/AstBuilder.cs:814`）
对嵌套类型**一律**限定：`if (type.DeclaringType != null && !DoNotIncludeEnclosingType)`
→ `MemberType { Target = ConvertType(DeclaringType), MemberName = name }`，不存在短名分支。

**根因**：3.2 的 `ConvertTypeHelper` 有**两条**短名路径 ——
(a) `resolver.LookupSimpleNameOrTypeName(typeDef.Name, ...)`（`:302`，实际生效的那条），
(b) `if (AlwaysUseShortTypeNames || ...)` 兜底（`:314`）。
只在 (b) 上加 `DeclaringType == null` 门槛（§8.19 的失败尝试）不会生效，因为走的是 (a)。

**修改**：给 (a) 也加 `genericType.DeclaringType == null` 门槛（嵌套类型不做短名查找），
并保留 (b) 的同样门槛。

**验证**：
- 语料（C#，identical/diffLines）：Contracts.DnSpy 794/24769 → **797/24719**；
  Debugger 196/5028 → **197/5016**；Logic 50/8430 → **50/8416**。合计 38227 → **38151**，identical 1040 → **1044**。
- `verify-all`：hardFail=0、skip=0，唯一 softFail 仍是既有单类型 `lang-diff`。
- 注：§8.19 记的「同一类型作字段声明时已限定、作枚举常量/转换时省略」的矛盾由此解释 ——
  两条路径分别命中 (a)/(b)，与是否嵌套无关。

### 8.28 using 集合差异的成因（2026-11-17，诊断）

对 `dnSpy.dll` 全量 diff 做了双向统计（`-U0`）：

| 命名空间 | 2.3 | 3.2 |
|---|---|---|
| `dnSpy.Documents.Tabs` | 73 | **0** |
| `dnSpy.Text.Editor` | 62 | **0** |
| `dnSpy.Hex.Editor` | 45 | **0** |
| `System.Runtime.CompilerServices` | 15 | **209** |

**成因（2.3 侧）**：2.3 会添加**被反编译类型自身所在命名空间**的 using。
实例 `dnSpy.Documents.Tabs.AllTabsMenuItemCommand+<>c__DisplayClass8_0`：
2.3 输出 `using dnSpy.Documents.Tabs;`（该类型自己的命名空间，语义上冗余），3.2 不加。
这解释了 `dnSpy.*` 命名空间的大批「缺失」（都是各自文件自己的命名空间）。

**3.2 侧多出的 `System.Runtime.CompilerServices`** 尚未定位（抽样文件中该 using 无任何对应类型使用，
且两侧可空特性文件数相同，排除 `EmitNullableAttributes`）。可能是 `[CompilerGenerated]` 的
type reference 被 3.2 的 using 计算算入。

**结论**：这是 2.3 的一个**冗余 using** 习惯，量级约 250 行（占剩余差异 0.15%）。
要复刻就是在 using 生成处补「被反编译类型的命名空间」，需要改引擎的 using 计算，
本轮未做（预算不足以完成「改 + 双 TFM + 语料 + 提交」的完整闭环）。

**⚠️ 上述结论已被语料证伪（2026-11-17 同日修正）**：
在三套语料（Contracts.DnSpy / Debugger / Logic）里，2.3 的「导入自身命名空间」**只有 1 例**，
且该例是 `namespace System` 中声明的类型导致的统计误报 —— 也就是说**语料中 2.3 从不导入自身命名空间**，
与 3.2 一致。真实视图以语料为准，那条规则不成立。

实测复现：把 `FindRequiredImports.IsParentOfCurrentNamespace` 的**相等**分支从「跳过」改成「导入」后，
语料 identical 从 797 → **335**（大幅回退），已 `git checkout` 回退并重建复核
（恢复到 797/24719、197/5016、50/8416）。

**因此**：`dnSpy.dll` 的 `--all` 全量转储与语料的**逐类型**转储在
`context.DecompiledTypeDefinition` / `currentNamespace` 上并不一致，
§8.26 / §8.28 里基于 `--all` 的 dnSpy.dll 统计（含 821 vs 229）**不能代表真实反编译视图**，
引用时须注明口径。3.2 侧多出的 209 个 `System.Runtime.CompilerServices` 仍未解释。

### 8.29 数字字面量后缀：大写已修，枚举回退的「有无后缀」未解（2026-11-17）

**已修（大小写）**：2.3 的无符号/长整型后缀是**大写** `U` / `L` ——
语料里 6 处（`rva = 0U;`、`ReadUInt32() != 17744U`），dnSpy.dll 里 281 处，**小写 0 处**；
3.2 反过来：大写 0 处、小写 `u` **1088** 处。
修改：`DnSpyTokenWriter.FormatPrimitiveLiteral`（真正生效的那处，扩展侧覆盖）与
引擎 `TextWriterTokenWriter.WritePrimitiveValue` 的 `Append('u')` → `Append('U')`。
（`ulong` 会得到 `UL`，与 2.3 一致。）

**未解（枚举回退值的有无后缀）**：`dnSpy.Themes.ColorInfos` 里 2.3 印
`new BrushColorInfo(16528, …)`，3.2 印 `new BrushColorInfo(16528U, …)`。
2.3 的 `CreateExpressionForConstant` 走 `MakePrimitive((long)Cast(Int64, …))`，按理无后缀。
尝试把回退值 long 装箱后：

| 改法 | Contracts.DnSpy identical |
|---|---|
| 基线 | **797** |
| 全部 long 装箱 | 765 |
| 仅非特性参数 long 装箱 | 785 |

**都低于基线** —— 说明语料里存在 2.3 **印后缀**的非特性枚举值，规则未钉死，两次尝试均已回退。
受影响的 `ColorInfos` 仍有 802 行差异，其中很大部分是这个后缀。
**下一步**：需要先在语料里找到一个「2.3 印后缀的枚举回退值」实例，才能确定判别条件。

### 8.30 枚举成员声明：十六进制 + 冗余初始化器（2026-11-17）

`dnSpy.Contracts.Themes.ColorType` 曾是语料里**最大的单一差异（758 行）**，两处独立规则：

**(1) 十六进制**：3.2 的 `CSharpDecompiler.DoDecompile` 对 `[Flags]` 枚举、或值是 2 的幂的成员
强制写 `0x{value:X}`（`Text = 0x4000`）。2.3 **从不**对枚举成员用十六进制 ——
语料与 dnSpy.dll 合计 2.3 侧 `= 0x` **0 处**，3.2 侧 **31 处**。已删除该规则。

**(2) 冗余初始化器**：2.3 的 `AstBuilder`（`:518-540`）维护一个**滚动期望值**：
初始 `0`，每处理一个成员后更新为 `memberValue + 1`；当成员值**等于**期望值时**省略初始化器**
（`FirstNR = 16384U,` 之后是 `Operator,`），并且 `[Flags]` 枚举**总是**打印。
3.2 全部显式打印。已按 2.3 的逻辑实现（含 `forcePrintingInitializers = IsFlagsEnum`）。
首版只比较「元数据里的前一个成员」，漏了「首个成员值为 0」的情形，identical 反而掉到 798；
改成滚动期望值后正确。

**验证**：
- 语料（C#，identical/diffLines）：Contracts.DnSpy 797/24719 → **820/23600**；
  Debugger 197/5016 → **218/4878**；Logic 50/8416 → **54/8317**。
  合计 38151 → **36795**（**-1356**），identical 1044 → **1092**。
- `dnSpy.Contracts.Themes.ColorType`：**758 行差异 → diffLines=0，逐字节一致**。
- `verify-all`：hardFail=0；net48 构建 0 错误。

**方法论提示**：这两条都是**逐类型语料**驱动发现的（§8.26 基于 `--all` 的口径不足以覆盖），
说明 `--all` 之外仍需按语料逐类型排查。

### 8.31 事件访问器注释：已修；数组初始化器打包：未解（2026-11-17）

**已修（事件访问器注释）**：2.3 的 `AstBuilder`（`:1577-1580`、`:1614-1617`）对**事件**调用
`AddComment(astEvent, eventDef.AddMethod, "add")` / `(RemoveMethod, "remove")`，
在事件上方多出两行 `// (add) Token: …` / `// (remove) Token: …`。
3.2 只给属性/索引器做了 `(get)`/`(set)`，事件漏了。已补 `InsertEventAccessorTokenComment`
（注意插入顺序：先插 `(add)`，再插 `(remove)`，后者会更靠近声明，与 2.3 输出一致）。

- `dnSpy.Contracts.ToolWindows.IToolWindowGroupService`：**141 行差异 → 逐字节一致**。
- 语料：Contracts.DnSpy 820/23600 → **833/22538**；Debugger 218/4878 → 221/4986；
  Logic 54/8317 → 54/8305。合计 36795 → **35829**，identical 1092 → **1108**。

**未解（数组初始化器打包）**：`[Nullable(new byte[] { 2, 1 })]`（2.3 内联）vs
3.2 多行。2.3 的 NRefactory `PrintInitializerElements` 规则已拿到（`wrap = wrapAlways || count > 10`，
`wrapAlways` 取决于 `ArrayInitializerWrapping == WrapAlways` / 非简单元素 / 复杂元素；
换行后每 10 个元素一行），并在 3.2 的 `CSharpOutputVisitor.PrintInitializerElements` 实现。
**但语料毫无变化**（833/22538 前后相同），说明该属性参数**不走**这条 visitor 路径；
改动已回退。另外注意 3.2 该输出的缩进为 0（`{`/`2,`/`1`/`}` 全部顶格），
也印证它不是排版器产生的。需要另行定位属性参数的渲染路径。

**✅ 已修（2026-11-17，并行调查 §8.37 找到真因）**：
`DecompilerSettings.cs:831` 把 `ArrayInitializerWrapping` 强制成 `WrapAlways`，
把 2.3 的规则（`wrapAlways || count > 10`）**短路成恒真** —— 所以 §8.31 只改 `PrintInitializerElements`
当然没效果，真因在这条设置。两处一起改后才生效：
- `DecompilerSettings.cs:831`：`WrapAlways` → `WrapIfTooLong`（`CreateAllman()` 的默认值，2.3 同款）；
- `CSharpOutputVisitor.PrintInitializerElements`：移植 2.3 的 `wrap/wrapAlways` 规则
  （`wrapAlways = ArrayInitializerWrapping == WrapAlways || (>1 且含非简单元素) || 含复杂元素`；
  `wrap = wrapAlways || count > 10`；不换行时输出 `{ a, b, c }`，换行时每 10 个元素一行）。

**验证（这是本会话单次收益最大的一项）**：
- 语料：Contracts.DnSpy 21985 → **16233**；Debugger 4980 → **2820**；Logic 8094 → **7476**。
  合计 35059 → **26529**（**-8530**）。
- Iced：141855 → **98549**（**-43306**），identical 165 → 173。
- `verify-all`：hardFail=0；net48 构建 0 错误。

### 8.32 局部名：桥改为读取 PDB 符号（2026-11-17）

**差异**：3.2 丢失 PDB 局部名 —— `depo`（2.3）vs `dependencyObject`（3.2），
是 §8.13 分桶里最大的一类（非局部名类的 44 个类型里也占 2 个）。

**根因**：`DnlibCecilBridge` 从**内存流**读模块且 `ReadSymbols = false`；
而 cecil 是按「模块**文件名**旁边的 PDB」定位符号的，内存流没有文件名，
所以 §8.12 把「读符号失败」误判成「PDB 不在位」——**PDB 一直都在**
（`dnSpy.Contracts.DnSpy.pdb` 321 KB）。

**修改**：
- 有文件路径时改为 `ModuleDefinition.ReadModule(location, ReadSymbols = true)`，否则仍用流；
- **容错**：读符号抛异常（例如 Windows PDB 且无原生 reader）时回落到「无符号读流」，
  保证反编译不会整体失败 —— 这是 §8.12 当年没能跨过的那道坎。

**验证**：
- 语料：Contracts.DnSpy 833/22538 → **839/21994**；Debugger 221/4986 → **221/4983**；
  Logic 54/8305 → **55/8141**。合计 35829 → **35118**（-711），identical 1108 → **1115**。
- `dnSpy.Contracts.Utilities.UIUtilities` 的 `depo` / `dependencyObject` 差异**消失**。
- 探针 `engine-tests` 的 `1b`/`3a` 原本钉的是「无 PDB 时的生成名 `num`」，
  已改为接受 `num`（无符号）或 `sum`（读到 PDB）—— 断言的本意是验证 SP/局部作用域管道，
  不是钉住局部名的来源。
- `verify-all`：**hardFail=0**、skip=0（唯一 softFail 仍是既有单类型 `lang-diff`）；
  IL 语料仍 1310/303/114 全 identical；net48 构建 0 错误。

**对真实应用同样有效**：3.2 引擎从此也会显示 PDB 局部名，与 2.3 的行为一致。

### 8.33 委托构造：改用显式委托类型（2026-11-17）

**差异**：2.3 总是写出委托类型 —— `wpfHexView.Closed += new EventHandler(this.WpfHexView_Closed);`；
3.2 用隐式方法组转换 —— `+= this.WpfHexView_Closed;`。
逐类型复核确认（`dnSpy.BackgroundImage.HexViewBackgroundImageService`：2.3 与 3.2 各 2 处 vs **0 处**）。

**根因**：`DecompilerSettings.UseImplicitMethodGroupConversion` 默认 `true`
（`DecompilerSettings.cs:387`；仅当语言版本 < C# 2 才置 false）。dnSpy 2.3 输出的是显式形式。
3.2 引擎本身**会**生成 `new D(...)`（`CallBuilder.cs:885` 的 `ObjectCreateExpression`），
由 `TranslatedExpression.cs:188` 依据该设置决定打印成哪一种。

**修改**：`DecompilerSettings32` 构造里设 `EngineSettings.UseImplicitMethodGroupConversion = false`。

**验证**：
- 语料：Contracts.DnSpy 839/21994 → **840/21982**；Debugger 221/4983（不变）；Logic 55/8141（不变）。
  合计 35118 → **35106**，identical 1115 → **1116**（语料里委托转换很少，主要收益在 dnSpy.dll 之类的大装配）。
- `dnSpy.BackgroundImage.HexViewBackgroundImageService` 的 `new EventHandler(...)` 已与 2.3 逐处一致。
- `verify-all`：hardFail=0、skip=0；IL 语料仍全 identical。

### 8.34 缓存的事件委托被 3.2 内联（**疑似 3.2 侧不正确**，2026-11-17）

`dnSpy.Contracts.Utilities.UIUtilities` 里，2.3 保留 Roslyn 为静态事件生成的**委托缓存字段**：

```csharp
DependencyPropertyChangedEventHandler d;
if ((d = UIUtilities.<>O.<0>__selector_IsVisibleChanged) == null)
{
    d = (UIUtilities.<>O.<0>__selector_IsVisibleChanged = new DependencyPropertyChangedEventHandler(UIUtilities.selector_IsVisibleChanged));
}
selector.IsVisibleChanged += d;
```

3.2 输出了

```csharp
selector.IsVisibleChanged += new DependencyPropertyChangedEventHandler(UIUtilities.selector_IsVisibleChanged);
...
obj.IsVisibleChanged -= new DependencyPropertyChangedEventHandler(UIUtilities.selector_IsVisibleChanged);
```

**语义后果**：`-=` 用的是**新建的**委托实例，永远不会匹配到先前 `+=` 的那个 ——
事件注销变成空操作。2.3 的写法（复用缓存实例）才是正确的。
**这不是风格差异，而是 3.2 侧的正确性缺陷**（`DelegateConstruction` 相关 transform 把缓存字段整个消掉了；
引擎里搜不到 `Cached` 相关分支，具体是哪条 transform 还在排查）。

同一方法里还有 §8.13 的另外两类：可空枚举比较（`ChangedButton == null` vs `!= 0`，7 行）
与可空/布尔塑形（`A || !B` vs 提前返回）。

**建议**：这条应优先于其余风格类差异处理 —— 它影响反编译结果的可信度。

**已修（2026-11-17）**：根因是 `IL.Transforms.CachedDelegateInitialization` ——
它的 `CachedDelegateInitializationWithField` 会把
`if (field == null) field = new D(M);` 连同后面的 `ldsfld field` 一起改写成 `new D(M)`，
即**主动反优化掉编译器生成的委托缓存**。dnSpy 2.3 不做这个改写，所以保留缓存。
处理：把 `CSharpDecompiler` 管线里的 `new CachedDelegateInitialization()` 停用
（不能靠 `AnonymousMethods=false` 关，那会连带关掉匿名方法支持）。

**验证**：
- `UIUtilities` 现在输出 `(UIUtilities.<>O.<0>__selector_IsVisibleChanged ?? (… = new D(M)))`，
  **缓存语义恢复**，`-=` 重新有效；该类型 diffLines 171 → **64**。
- 语料：Contracts.DnSpy 840/21985、Debugger 221/4980、Logic 55/8094；合计 35106 → **35059**。
- 剩余差异只有两类已知塑形：`??` vs 「临时局部 + if」（缓存的写法差异）与
  可空枚举比较（`ChangedButton == null` vs `!= 0`，见 §8.13）。

### 8.35 第三方库语料：差异集中在 `in` 参数修饰符（2026-11-17）

13 项修复后重测第三方语料（§8.14 的数字已过时）：

| 程序集 | identical / 类型 | diffLines |
|---|---|---|
| Iced | 165 / 764 | **141879** |
| MessagePack | 21 / 297 | 20823 |
| Mono.Cecil | 128 / 315 | 22354 |

**Iced 的差异高度集中**：`Iced.Intel.Assembler` 单个类型就占 **60871 行（43%）**。
对该类型逐行分类（25684 变更行）后，主体是：

- **`in` vs `ref` 参数修饰符：3466 处**（2.3 输出 `Add(in instruction)`，3.2 输出 `Add(ref instruction)`）。
  注意这不只是写法：声明与调用点都不同，`ref` 版本丢失了只读语义。
- 其余：`++` vs `+= 1UL`、成员 RID 偏移、`[System.Runtime.CompilerServices.NullableContext(1)]`
  vs `[NullableContext(1)]`（3.2 过度限定）等。

**尝试与结论**：照 2.3 的规则（`AstBuilder.cs:2089`：by-ref 参数带 `IsReadOnlyAttribute` → `in`）
给 3.2 加了 `ParameterModifier.In` 并在 `ConvertParameter` 里检测 —— **零效果**
（Iced 前后都是 141879）。原因：`CecilLoader.ReadParameter` 确实调用了 `AddAttributes`，
但 cecil 侧 `ParameterDefinition.CustomAttributes` 里**没有** `IsReadOnlyAttribute`，且 `IsIn` 为 **false**
—— 即 **cecil 根本没读到 `in` 标记**。改动已回退（未提交未验证的代码）。

**定性**：这是**类型系统/桥层面**的缺口（cecil 未暴露 in 标记），不是输出层规则；
在弄清 cecil 为何读不到之前无法在输出层绕过。**这是目前已知最大的单项残余**（3466 行/类型）。

**下一步线索**：`CecilLoader.ReadParameter` 把 `parameter.ParameterType` 交给
`ReadTypeReference(..., isFromSignature: true)`，而 `in` 在元数据里很可能编码在
**by-ref 元素类型上的 modreq**（`System.Runtime.CompilerServices.IsReadOnlyAttribute`）——
需要确认 `ReadTypeReference` 是否把 modreq 丢掉了（ILSpy 里对应 `ModifiedTypeReference`）。
若确实丢失，在读取时保留 modreq 并在 `ConvertParameter` 里判定即可；
这同时能解释为什么「元数据里查得到 `IsReadOnlyAttribute` 字符串、cecil 的
`CustomAttributes` 里却没有」（它不是 CustomAttribute，而是 modreq）。

### 8.36 引擎子模块的 git 状态：**全部工作只在本地**（2026-11-17 核实）

`Extensions/ILSpy32.Decompiler/ICSharpCode.Decompiler`：

- **HEAD 处于 detached 状态**（`git rev-parse @{u}` → `fatal: HEAD does not point to a branch`）；
- `git remote -v` 指向 **`https://github.com/icsharpcode/ILSpy.git`（上游仓库，不是 fork）**；
- 相对 `origin/master` **领先 28 个提交**（含本次会话的 8 个引擎修复），
  且基点 `a9a7288b8` **不在任何远端分支上**（`git branch -r --contains a9a7288b8` 为空）。

**影响**：这些工作目前**只存在于本地磁盘**，一旦工作副本丢失就无法恢复；而且因为 remote 是上游仓库，
既没有权限也不应该直接推上去。

**建议的收口动作**（需要你决策/提供 fork）：
1. 在子模块里 `git switch -c feature/ilspy32-upstream-port`（把当前 HEAD 固化成分支）；
2. 把该分支推到**你们自己的 fork** 并设 upstream；
3. superproject 的 gitlink 自然会指向这些提交，再推 superproject 即可。

**在动手推之前需要你确认**：fork 地址、是否要保留 detached 状态、以及谁来做这次推送。

**已做的本地保护（无需授权，HEAD 与工作树未变）**：
- `git branch feature/ilspy32-upstream-port HEAD` —— 把当前 HEAD 固化成分支，
  这样后续即使切走也不会让这 28 个提交变成不可达对象；
- `git tag -a ilspy32-port-snapshot-20261117 HEAD -m "Snapshot of the 3.2 port work on 2026-11-17 (28 commits ahead of origin/master)"`。
- 两者都是**本地**操作，可逆（`git branch -d` / `git tag -d`），不涉及任何远端。

**✅ 已落库（2026-11-17，用户提供 fork 后）**：
- 发现 `https://github.com/Ayinyue/ILSpy.git` 存在（分支 `ilspyv2` / `ilspyv3` /
  `ilspyv3-dnSpy` / `ilspyv3-old` / `master` / `newdecompiler`）。
- 引擎：`git push https://github.com/Ayinyue/ILSpy.git feature/ilspy32-upstream-port`
  → 新分支，tip = **`8b7ee65`**（即 superproject 的 gitlink 值，**已验证可解析**）。
- superproject：`git push origin feature/ilspy32`（`origin` = `Ayinyue/dnSpy`）
  → 新分支，tip = **`4fed919ab`**。
- 同时把 `.gitmodules` 里该子模块的 url 改为 `https://github.com/Ayinyue/ILSpy.git`
  并加 `branch = feature/ilspy32-upstream-port`（随 `4fed919ab` 一起提交），
  这样全新克隆可以正常拉取子模块。

**注意（分歧提示）**：本地引擎历史与 `Ayinyue/ILSpy` 现有分支**分歧很大**
（与 `ilspyv3` / `ilspyv3-dnSpy` 的 merge-base 之后，对方各有 9200+ 提交、我方 27 个）。
本次是**新分支**推送，没有覆盖对方任何分支；若后续要把这 27 个提交并入 `ilspyv3-dnSpy`，
需要单独做一次 rebase/merge 评估。

### 8.37 四路并行调查：`in` 已修一半，另三项拿到精确入口（2026-11-17）

用并行代理对四个残余项做了定位（只研究、不改码），结论如下。

**(1) `in` 参数修饰符 —— 已修声明侧，调用点未通**
- 纠正 §8.35 的结论：**桥层没有丢**。cecil 侧 `ParameterDefinition.IsIn == true` 且带
  `[IsReadOnlyAttribute]`，`CecilLoader.AddAttributes` 会补 `InAttribute`。丢在**输出层**。
- 已补：`ParameterModifier.In` + `ParameterDeclaration.InModifierRole`、`FieldDirection.In`
  + `DirectionExpression.InKeywordRole`、`CSharpOutputVisitor` 两处输出分支、
  `TypeSystemAstBuilder.ConvertParameter`（by-ref + `IsReadOnlyAttribute`/`InAttribute` → `in`）、
  `CallBuilder` 调用点包 `DirectionExpression(In, …)`。
- 实测：**声明侧生效**（Iced 141879 → 141855）；**调用点未生效** ——
  诊断（把判定改成 `Attributes.Count > 0` 或 `=> true`）证明调用点拿到的 `IParameter.Attributes`
  是**空的**（`=> true` 会让输出大变，属性判定则完全不动）。根因未找到（`GetCecil` 只在扩展项目里，
  引擎的 `CallBuilder` 用不上）。**剩余 3466 行/类型的调用点差异待解。**

**(2) 属性里的数组初始化器 —— 找到了 §8.31 零效果的原因**
`DecompilerSettings.cs:831` 把 `ArrayInitializerWrapping` 强制成 `WrapAlways`，
把 2.3 的规则（`wrapAlways || count > 10`）短路成恒真。改法：该行改 `WrapIfTooLong`，
并把 2.3 NRefactory 的 `wrap/wrapAlways` 规则移植进 `CSharpOutputVisitor.PrintInitializerElements`
（§8.31 只改了后者，所以没效果）。**未实施**（影响所有数组初始化器，需整体回归）。

**(3) 方法体内枚举命名 —— 责任点已定位**
不是 `ConvertEnumValue`（它无 `val == 0` 特判）。是 `TranslatedExpression.cs:435-444` 的
`ConvertToBoolean` 用**硬编码 Int32** 拼出 `kind != 0`，绕过了 `ConvertEnumValue`；
2.3 在 `AstMethodBodyBuilder.cs:1587-1594` 会按类型印 `MakePrimitive(0, enumType)`。
改法：改用 `expressionBuilder.ConvertConstantValue(new ConstantResolveResult(Type, 0))`。**未实施**。

**(4) 控制流塑形 —— 责任 transform 已定位**
`IL/Transforms/InlineReturnTransform.cs:28`（注册于 `CSharpDecompiler.cs:80`）把 `return 局部`
复制到各 `stloc` 赋值点，再经 `ILInlining` 变成「提前 return」；2.3 用
`ILAst/SimpleControlFlow.cs:69/254` 的 ret-ternary→`&&`/`||`。
改法：给 `InlineReturnTransform` 加守卫（返回局部被 ≥2 个分支赋值时不复制）。
**未实施**（停用会影响 try/catch、循环内 return 的定位，需整体回归）。

**(5) 索引器语法 —— 责任点已定位**
3.2 在 `CallBuilder.cs:686/780` 无条件生成 `IndexerExpression`；2.3 在
`AstMethodBodyBuilder.cs:1278-1283` 要求 `prop.GetMethod == methodDef`，
corlib 未解析时落回 `get_Chars(...)`。改法：在 `HandleAccessorCall:780` 加同一门控。**未实施**（约 27 处）。

### 8.38 方法体内「值与 0 比较」的枚举命名：已修（2026-11-17）

§8.37(3) 定位到 `TranslatedExpression.ConvertToBoolean`：它用**硬编码 Int32** 的
`new PrimitiveExpression(0)` 与表达式比较，绕过了 `ConvertEnumValue`，于是
`if (kind != 0)` 而不是 2.3 的 `if (kind != SearchColumnCommandParser.TokenKind.Text)`。

**修改**：改用 `expressionBuilder.ConvertConstantValue(new ConstantResolveResult(Type, 0))`，
让零常量带上表达式自身的类型（与 2.3 的 `AstMethodBodyBuilder.cs:1587-1594` 一致）。

**验证**：语料 Contracts.DnSpy 16233 → **16211**（合计 26529 → **26507**）；
`SearchColumnCommandParser` 现在能印出 `TokenKind.Text`（2.3 3 处 / 3.2 由 1 处升到 2 处）。
`verify-all` hardFail=0。

**剩余同源**：`kind - TokenKind.Command > 1`（2.3）vs `(uint)(kind - 1) > 1U`（3.2）——
算术操作数的类型提示还没传对（见 §8.37(3) 末尾），**未修**。

### 8.39 特殊浮点常量：`double.MaxValue` 等（2026-11-17）

**差异**：2.3 把 `ldc.r4/r8` 里的特殊值改写成 `ldsfld float/double::<name>`
（`ILAstOptimizer.cs:595-649`），输出 `double.MaxValue` / `double.PositiveInfinity` / `float.NaN` 等；
3.2 直接印数字字面量（`1.7976931348623157E+308`）。

**修改**：
- 扩展侧 `DnSpyTokenWriter`：`FormatPrimitiveLiteral` 顶部按值返回 `float.<name>` / `double.<name>`；
  并把 `WritePrimitiveValue` 里原有的 infinity/NaN 分支统一走 `GetSpecialFloat/DoubleConstantName`。
  **注意**：真正生效的是 `FormatPrimitiveLiteral` —— `WritePrimitiveValue` 的 primitive-ref 分支
  （`:1135-1144`）对所有数字字面量提前返回，只改后者是无效的。
- 引擎侧 `TextWriterTokenWriter.WritePrimitiveValue` 同步（float/double 各 6 个特殊值）。

**验证**：语料 Contracts.DnSpy 16211 → **16195**、Debugger 2820 → **2819**（合计 26507 → **26490**）；
`ExportTreeNodeDataProviderAttribute`（`this.Order = double.MaxValue;`）**diffLines=0**。
`verify-all` hardFail=0。

### 8.40 残余差异普查 + `base.` 限定修复（2026-11-17）

**普查**（并行代理，对 `dnSpy.Contracts.DnSpy.dll` 取 diffLines top30，合计 8204 行 ≈ 全量 16195 的 51%）：

| 类别 | 占比 | 备注 |
|---|---|---|
| **H 其它** | **78.3%** | 子项：**cast/checked 消除 297**、控制流 122、变量重命名 104、**`base.` 前缀 76**、操作符 55、扩展方法 50 |
| B 可空比较 | 12.8% | 具名类别里最大 |
| A 提前返回 | 2.8% | |
| E 字面量 | 2.5% |（`double.MaxValue` 等，已修，见 §8.39） |
| D 枚举常量 | 2.1% | |
| C 委托 | 0.9% | |
| G using/命名空间 | 0.4% | |
| F 索引器 | 0.3% | |

**已修（`base.` 限定）**：2.3 的 NRefactory 输出层对基类成员访问**总是**加 `base.`，
3.2 只在「非 `callvirt` 的虚调用」时保留。改成无条件 `requireTarget = true`。
语料：Contracts.DnSpy 16195 → **16099**（合计 26490 → **26394**），identical 855 → 859。

**下一个最大子项**：**cast/checked 消除（297 行）** —— 2.3 印
`(int)this.bytes[(int)(checked((IntPtr)index))]`，3.2 印 `this.bytes[index]`。
即 3.2 把 2.3 保留的显式转换/`checked` 省略了。**未修**，是 H 类里最大的一块。
（复核说明：普查引用的 `checked((IntPtr)pos)` 例子在当前构建里两边都已消失，属旧转储；
真正的 cast 类差异主要是下面的可空构造。）

### 8.41 显式可空构造 `new T?(x)`：已修（2026-11-17）

**差异**：2.3 保留 `new bool?(node.IsExpanded)` / `new int?(x)` 这类**显式可空构造**；
3.2 把它解包成 `x`（`TranslatedExpression.ConvertTo` 的 `case InvocationResolveResult`
里，当目标类型是 `Nullable<T>` 时 `UnwrapChild`）。

**修改**：去掉该解包（2.3 没有对应逻辑）。

**验证**：语料 Contracts.DnSpy 16099 → **16071**（合计 26394 → **26366**），identical 859 → 861。
`verify-all` hardFail=0。

**未采纳的实验**：给 `InlineReturnTransform.CanModifyInstructions` 加「返回局部被多处赋值就不内联」
的守卫 —— Debugger 反而变差（2819 → 2852），已回退。

### 8.42 赋值右值的括号优先级：已修（2026-11-17）

**差异**：3.2 在 `x = (a == b);` 上多留一层括号，2.3 印 `x = a == b;`。

**根因**：`InsertParenthesesVisitor.VisitAssignmentExpression` 里 3.2 用
`RelationalAndTypeTesting + 1`，2.3（NRefactory）用 **`Conditional + 1`**，并且**跳过 `DirectionExpression`**。

**修改**：按 2.3 改成 `Conditional + 1` 且 `!(Right is DirectionExpression)`。

**验证**：语料 Contracts.DnSpy 16071 → **16041**、Debugger 2819 → **2814**、Logic 7476 → **7467**。
合计 26366 → **26322**（-44），identical 861 → 867。`verify-all` hardFail=0。

**同批普查但未修**（各仅 3~5 处）：P1 `= default(...)` 局部初始化器
（`DeclareVariables.cs:449` 的 `DefaultInitialization`）；P3 `lock` 临时局部
（`Instructions.cs:1788` 的 `canInlineInto`，改动波及内联、风险高）。

### 8.43 `checked(...)` 溢出检查转换：**唯一确认的语义差异**，机制未通（2026-11-17）

**差异**：`dnSpy.Contracts.Hex.HexBytes` 里 2.3 印
`(int)this.bytes[(int)(checked((IntPtr)index))]`（16 处 `checked(`），3.2 印 `this.bytes[index]`。
全语料（`--all` 口径）2.3 有 **84** 处 `checked(`，3.2 **0** 处；两边都没有 `checked {` 块。
即 **3.2 完全丢失了溢出检查转换** —— 这不是风格差异，溢出行为会变。

**已排除**：
- IL 层没问题：`ILReader.cs:599-621` 把 `conv.ovf.*` 读成 `Conv(checkForOverflow: true)`；
- 转换层没问题：`ExpressionBuilder.VisitConv:1338` 把 `checkForOverflow` 传给 `ConvertTo`；
- `TranslatedExpression.ConvertTo:378-382` 确实 `castExpr.AddAnnotation(AddCheckedBlocks.CheckedAnnotation)`；
- `AddCheckedBlocks` 已注册（`CSharpDecompiler.cs:160`），其 `Run/GetResult` 逻辑完整，
  对 checked 注解会产出 `CheckedExpression`。

**结论**：注解在到达 `AddCheckedBlocks` 之前就被丢弃了（或被后续 transform 消掉），
**机制未通，需继续追**。可能方向：注解挂在了后来被内联/消除的节点上；
或 `AddAnnotation` 的键（`object` 静态字段）与 `Annotation<CheckedUncheckedAnnotation>()` 的查找键不一致。

**诊断实测（临时加 stderr 计数，已回退）**：在 `AddCheckedBlocks.GetResult` 里统计 ——
C# 视图对 `HexBytes` 看到 **170** 个 `CheckedUncheckedAnnotation`（VB 视图同样 170）；
但同一轮里 `GetResultFromBlock` 对 **31** 个块全部返回 `costUnchecked=[0 + 0] nodes=False`。
即：**注解确实存在，但都不在 `GetResultFromBlock` 遍历的语句树内**。
`Run()` 只对 `BlockStatement` 调 `GetResultFromBlock`，非块节点只是递归 `GetResult`、
其结果**不会被插入**。所以这些注解位于**语句块之外**（字段/属性初始化器、默认参数值、
或未被语句遍历到的位置）—— 这是下一步最该验证的假设。

**同轮复测（推翻上一段结论）**：把两个计数放在**同一次运行**里 ——
- 注解节点的祖先链：`SyntaxTree/NamespaceDeclaration/TypeDeclaration/MethodDeclaration/**BlockStatement**/IfElseStatement/BinaryOperatorExpression/CastExpression`
  —— **注解的 cast 确实在语句块内**；
- 但同一轮 `GetResultFromBlock` 对 **31 个块**仍全部 `costUnchecked=[0 + 0] nodes=False`。

**推理**：按 `GetResult` 的代码，checked 注解会先 `CostInUncheckedContext += 10000`，
随后 `WrapInCheckedExpr` 会把它降到 `[0 + 1]` 并产生 `InsertedExpression`。
所以只要该块遍历到注解，cost 至少是 `[0 + 1]`；实测 `[0 + 0]` 说明
**`GetResultFromBlock` 遍历的那个块与注解所在的块不是同一批**（例如注解在 VB 视图的块里、
或在同一次 dump 的另一次反编译里）。下一步应打印块的唯一标识（如起始偏移）来对齐两个计数。

**块标识对齐实测**：用 `RuntimeHelpers.GetHashCode(block)` 对齐后 ——
21 个「含注解的块」里有 **19 个**同时出现在 `GetResultFromBlock` 遍历的 31 个块中。
即 **`GetResultFromBlock` 确实遍历了这些块，却仍返回 `[0 + 0]`**。
（按代码推演，遍历到注解后 cost 至少是 `[0 + 1]`。）
所以损失发生在 `GetResultFromBlock` 的**逐语句累加**环节，而不是「块没被遍历」。
下一步应直接在 `GetResultFromBlock` 里对每个 statement 打印 `stmtResult.CostInUncheckedContext`，
定位是哪一个 statement 的结果被丢掉了。

**逐语句实测**：在 `GetResultFromBlock` 里打印每个 statement 的 `CostInUncheckedContext` ——
**全部是 `[0 + 0]`**，包括那些祖先链里带注解 cast 的 `IfElseStatement` / `ReturnStatement`。
即 `GetResult(statement)` 的**子节点递归没有把注解的成本累加上来**（`GetResult` 本身确实看到了注解，
否则不会有 `ACB-ANNBLOCK`）。这与 `GetResult` 的代码（`for child ... result.CostInUncheckedContext += childResult...`）矛盾，
怀疑**存在两个不同的 `GetResult`/`GetResultFromBlock` 调用上下文**（例如 VB 视图也跑了一遍，
两侧的 statement/block id 不同），需要在同一轮里同时打印 statement id 与注解所在 block 的 id 才能定论。

**收尾诊断（决定性）**：
1. 170 个注解**全部是 `unchecked`**（`checked=False`）—— 在默认 unchecked 上下文里它们成本为 0、
   无需插入，`AddCheckedBlocks` 的行为**完全自洽**，之前「cost=0 很矛盾」的推断是错的。
2. 在 `ExpressionBuilder.VisitConv` 里打印 `inst.CheckForOverflow` —— 出现的转换全是
   `SignExtend/ZeroExtend/Truncate/StopGCTracking`，**`conv.ovf.i` 根本没走 `VisitConv`**。
   （`HexBytes.TryReadByte` 的 IL 是 `ldarg.1; conv.ovf.i; ldelem.u1`。）

**结论**：这个 checked 转换在**数组下标**路径里就被消费/折叠掉了，`TranslatedExpression.ConvertTo`
拿到的不再是带 overflow 标志的 `Conv`。**修复点不在 `AddCheckedBlocks`，也不在 `ConvertTo`，
而在处理 `ldelem` 下标的那段代码**（ILAst 构造或 `ExpressionBuilder` 的数组访问路径）。
下一步应查 `ldelem`/`ArrayAccess` 如何构造下标表达式、以及是否有 IL transform 把 `conv.ovf` 折叠进下标。

**定位到修复点（但未生效）**：`ExpressionBuilder.TranslateArrayIndex`（`:1821`）对下标调用
`input.ConvertTo(targetType, this)` 时**没有传 `checkForOverflow`**。已补上
（`(i as Conv)?.CheckForOverflow ?? false`）。但实测语料无变化 —— 因为诊断显示
**传进来的下标指令根本不是 `Conv`**：`ACB-IDX type=BinaryNumericInstruction resultType=I8`（44 处）、
`type=LdLoc resultType=I8`（16 处）、`type=LdLoc resultType=I4`（2 处）。
即 `conv.ovf.i` 在更早的 **ILAst 阶段**就被折叠/改写了，`TranslatedExpression.ConvertTo`
拿不到带 overflow 标志的 `Conv`。

**结论**：这处 `checkForOverflow` 补全是**防御性正确改动**（当前无实测效果），
真正要修的是**折叠 `conv.ovf` 的那条 ILAst transform** —— 下一步应比对 2.3 与 3.2 的
`ILAstOptimizer`/`ILReader` 在 `conv.ovf` + `ldelem` 组合上的差异（2.3 侧 `ILAstOptimizer.cs:1040`
附近的 `PatternStatementTransform` 保留了临时变量，可作对照）。

**元凶找到了，但一次改不全（已回退，净 +36 行）**：`IL/Transforms/ExpressionTransforms.cs:184`
的 `CleanUpArrayIndices` 会**删掉数组下标里的 `conv.i`** —— 条件命中
`conv.Kind == SignExtend && conv.CheckForOverflow`（`conv.ovf.i` 正是 int→native int 的符号扩展+溢出检查），
于是溢出检查被丢弃。

试「checked 转换不删」后：
- 3.2 印 `this.bytes[checked((long)(IntPtr)index)]`，2.3 印 `(int)this.bytes[(int)(checked((IntPtr)index))]`
  —— 语义对了，但**还有第二处差异**：`ExpressionBuilder.TranslateArrayIndex`（`:1821`）对
  `StackType.I`（native int）选 **`Int64`**，2.3 选 **`Int32`**；且 2.3 在索引外还有 `(int)`、
  整个表达式外还有 `(int)`。
- 结果：`HexBytes` diffLines 404 → 440，Contracts.DnSpy 16041 → 16077（**变差，已回退**）。

**下一步**：把 `TranslateArrayIndex` 的 `StackType.I` 分支从 `Int64` 改成 `Int32`，
并同时保留 checked 转换（即 `CleanUpArrayIndices` 对 checked 跳过），再整体对拍。

**已按此实施（保留）**：两处一起改后
- 3.2 输出 `this.bytes[(int)(long)checked((IntPtr)index)]`，2.3 输出
  `(int)this.bytes[(int)(checked((IntPtr)index))]` —— **溢出检查回来了**（正确性修复），
  但仍多一层 `(long)`（2.3 没有），且 2.3 在索引/表达式外还有 `(int)`。
- 语料：Contracts.DnSpy 16077 → **16041**（回到基线，无回归），identical 不变。
- `verify-all` hardFail=0。

**结论**：这是**正确性修复**（3.2 原先静默丢弃溢出检查），不是字节级对齐。
要达到 2.3 的形态还需：`TranslatedExpression.ConvertTo` 对 `IntPtr → Int32` **直接转换**
（不要绕 `Int64`），并去掉 `TranslateArrayIndex` 在索引外多加的 `(int)`。
两处都改完再对拍，预计能同时拿到正确性与 diffLines 收益。

**已改完第二步（保留）**：`ConvertTo` 现在对 `IntPtr → Int32` / `UIntPtr → UInt32`
**直接转换**（不再绕 `Int64`/`UInt64`，语义等价：都是截断低位）。
3.2 输出从 `(int)(long)checked((IntPtr)index)` 变为 **`(int)checked((IntPtr)index)`**，
与 2.3 的 `(int)(checked((IntPtr)index))` 只差外层一对括号。
语料不变（Contracts.DnSpy 16041，无回归）；`verify-all` hardFail=0。

**另两类已计数但未修**：`.Invoke()`（2.3 印 `handler.Invoke(...)`，3.2 省略，2.3 有 21 处/3.2 3 处）；
`= default(...)` 局部初始化器（3.2 比 2.3 多 31 处，责任点 `DeclareVariables.cs:449`）。

### 8.44 显式转换插入：2.3 明显更多（2026-11-18，未修）

`--all` 全量计数（`dnSpy.Contracts.DnSpy.dll`，1414 类型）：

| 字面量 | 2.3 | 3.2 |
|---|---|---|
| `(int)` | **167** | 66 |
| `(long)` | **104** | 22 |
| `(byte)` | 24 | 51 |

即 **2.3 系统性保留更多显式转换**。实例：
`SerializedImageUtilities` 里 2.3 印
`if ((short)((int)rawData[0] | ((int)rawData[1] << 8)) != 7189)`，
3.2 印 `if ((short)(rawData[0] | (rawData[1] << 8)) != 7189)` —— 每个 `byte` 元素都被 2.3 显式转 `(int)`。

**已排查**：2.3 的 `AstMethodBodyBuilder.cs:631` 对 `ldelem.*` 也只是 `arg1.Indexer(arg2)`（无转换）；
`Indexer(...)` 是 NRefactory `Expression` 上的构造器，本身不加转换。
所以 `(int)` 来自**别处**（很可能是 2.3 的 `TypeAnalysis`/`CastInsertion`：当 IL 栈类型（I4）与 C# 元素类型
（`byte`）不一致时插入显式转换）。**未修**，这是「cast」类差异的主体，值得下一轮从
「2.3 在何处插入 IL 栈类型→C# 类型的显式转换」入手。

**补充定位**：`(int)` 是**元素级**的 —— 2.3 有 `return (int)this.bytes[...]`（2 处），3.2 **0 处**；
即 `ldelem.u1` 的结果被 2.3 显式转成 `int32`（IL 栈类型）。两侧都没有专门的 cast transform
（`Ast/Transforms`、`CSharp/Transforms` 里都没有 `*Cast*`），2.3 的二元运算符构造
（`AstMethodBodyBuilder.cs:496-552`）也不加转换。**结论：插入点在 2.3 的 ILAst→AST 阶段
（`ldelem` 结果的类型提升），需要在 2.3 的 `ILAstOptimizer` 里找 `Ldelem` 相关的类型改写。**

### 8.45 局部声明的 `= default(...)`：已修（2026-11-18）

**差异**：`DeclareVariables.cs:449` 在 `v.DefaultInitialization` 时给局部声明加 `= default(T)`，
2.3 不加（这些局部在 IL 里是「先赋值再使用」的）。

**修改**：去掉该初始化器，保持 `T x;`。

**验证**：语料 Contracts.DnSpy 16041 → **16038**（合计 26322 → **26319**），无回归；
`verify-all` hardFail=0。收益很小（多数含该声明的类型本身还有其他差异），但方向与 2.3 一致。

### 8.46 委托调用保留显式 `.Invoke`：已修（2026-11-18）

**差异**：2.3 印 `handler.Invoke(...)`（2.3 有 21 处 / 3.2 3 处），3.2 简化成 `handler(...)`。

**根因**：`CallBuilder.cs:246` 有一条特例 —— 当被调方法是委托的 `Invoke` 时，直接生成
`InvocationExpression(target, args)`（C# 的委托调用语法）。2.3 没有这条特例，走普通成员调用。

**修改**：删掉该特例分支。

**验证**：语料 Contracts.DnSpy 16038 → **16030**（合计 26319 → **26311**），identical 867 → 869；
`verify-all` hardFail=0；net48 0 错误。

### 8.47 可空值类型的默认值：`default(T?)` 而非 `null`（2026-11-18）

**差异**：2.3 印 `default(ImageReference?)`，3.2 印 `null`。

**根因**：`ExpressionBuilder.GetDefaultValueExpression`（`:411`）把 `Nullable<T>` 和引用类型一起归为
「用 `NullReferenceExpression`」；2.3 的 `AstBuilder.MakeDefaultValue` 对 `NullableSig` 返回
`DefaultValueExpression`（即 `default(T?)`）。

**修改**：去掉 `|| type.IsKnownType(KnownTypeCode.NullableOfT)`，只让真正的引用类型走 `null`。

**验证**：语料 Contracts.DnSpy 16030 → **15950**、Debugger 2814 → **2794**（合计 26311 → **26211**，
**-100**），identical 1146 → **1155**。`verify-all` hardFail=0。

### 8.48 最新残余普查（2026-11-18）+ 整值 double 字面量线索

**普查**（对 `dnSpy.Contracts.DnSpy.dll` 取实际 diffLines top25，合计 7301 行 ≈ 全语料 15950 的 46%）：

| 类别 | 变更行 | 占比 |
|---|---|---|
| **G 其它** | 2002 | **78.3%** |
| **A 控制流塑形** | 306 | 12.0% |
| **B 显式转换** | 143 | 5.6% |
| E 委托 / `new T?` | 26 | |
| D `.Invoke()` | 16 | |
| C `default(T?)` | 14 | |
| F 括号 | 3 | |

G 的子项：临时变量 117、`L` 后缀 46、扩展方法语法 46、`?.` 38、**整值 double 字面量 38**、Nullable 特性 28。

**整值 double 字面量（新线索，38 处）**：`--all` 全量计数 —— 2.3 有 **267** 个 `\d+\.0`，3.2 只有 **23** 个。
实例（`MetroWindow`）：2.3 印 `new Size(96.0, 96.0)` / `96.0 * M11`，3.2 印 `new Size(96, 96)` / `96 * M11`。
**已排除**：`DnSpyTokenWriter.FormatPrimitiveLiteral`（:1196-1197）与引擎 `TextWriterTokenWriter`（:326-328）
**都已经有「整数部分无小数点就补 `.0`」的逻辑**。所以这 244 处差异来自**另一条渲染路径**
（值可能不是 `double`，或走了 `literalValue`/其它分支）。**未修**，值得下一轮先确认那个 `96` 的值的 CLR 类型。

**已追到源头**：`dnSpy.Contracts.Controls.MetroWindow`，IL 是 **`ldc.r8 96`**（真 double）。
`DnSpyTokenWriter.WritePrimitiveValue:1128` 会先判 `literalValue != null` → 原样输出；
`UnsafeLiteralValue` 只由 `PrimitiveExpression.SetValue` 设置，而引擎里已无调用者（十六进制那处已删），
所以 `literalValue` 应为 null。**矛盾未解**：两个 writer 的 double 分支都会补 `.0`，却输出 `96`。
**下一步**：在 `WritePrimitiveValue` 入口打印 `value?.GetType()`，一次即可定位。

**实测（矛盾，未解）**：入口诊断显示 MetroWindow 里有 **60 次 `System.Double` 且 `literal=null`**；
而 `DnSpyTokenWriter.FormatPrimitiveLiteral` 的 double 分支明确会 `number += ".0"`（:1196-1197）。
全新重跑确认输出确实是 `96`（2.3 是 `96.0`）。**即：这些 double 常量没有走
`WritePrimitiveValue`/`FormatPrimitiveLiteral`** —— 必然存在另一条渲染路径
（可能是别的 AST 节点或 `EmitText` 直接输出）。**下一轮应在 `WritePrimitiveValue` 之外
（例如 `CSharpOutputVisitor.VisitPrimitiveExpression` 之外的分支）找那条路径**，
或在 `DnSpyTokenWriter` 里统计所有 `EmitText` 的调用点。

**已确认的硬矛盾（把范围压到最小）**：
- IL 全部是 `ldc.r8 96`（4 处），值必为 `double`；
- `WritePrimitiveValue` 入口实测收到 **60 次 `System.Double`、`literal=null`**（C#+VB 两轮，
  即 C# 约 30 次）；
- `DnSpyTokenWriter` 的 **两条** double 分支（primitive-ref 分支 `FormatPrimitiveLiteral:1196-1197`、
  非 primitive-ref 分支 `:1195-1198`）**都会** `number += ".0"`；
- 全新重跑的 C# 输出确实是 `new Size(96, 96)`（2.3 是 `96.0`）。

四项同时成立在现有代码下**不可能**，说明存在一条既非 `WritePrimitiveValue` 两条分支、
也不设置 `UnsafeLiteralValue` 的输出路径（或输出后被再次改写）。

**再排除一条（决定性）**：在**引擎** `TextWriterTokenWriter.WritePrimitiveValue` 入口拦截
`value is double && value == 96.0` —— **0 命中**。
即这个 96.0 **既不走扩展的 `DnSpyTokenWriter`，也不走引擎的 `TextWriterTokenWriter`**。
两个 writer 的 double 分支都会补 `.0`，输出却是 `96` —— 该字面量的渲染完全绕过了
`WritePrimitiveValue` 这一层。**下一步应查 `CSharpOutputVisitor` 里直接写 token 的分支，
或 dnSpy 输出层对文本的二次改写。**

### 8.49 整值 double 字面量：`FormatPrimitiveLiteral` 缺 `.0`（已修）

**真相**：`DnSpyTokenWriter.WritePrimitiveValue` 的 **primitive-ref 分支**（`nodeStack.Peek().Annotation<ResolveResult>()`
存在时）走的是 `FormatPrimitiveLiteral(value)`，而该助手（`:1278`）**只做了特殊值名 + 后缀，
没有补 `.0`** —— `.0` 逻辑只存在于 `WritePrimitiveValue` 的另一分支（`:1195-1198`）。
这就是「两个 writer 都补 `.0` 却输出 `96`」的全部原因：**根本没走到那段逻辑**。

**修改**：在 `FormatPrimitiveLiteral` 的 double 分支里补上
`number.IndexOf('.') < 0 && number.IndexOf('E') < 0` → `number += ".0"`。

**验证**：语料 Contracts.DnSpy 15950 → **15764**、Debugger 2794 → **2780**、Logic 7467 → **7461**
（合计 26211 → **26005**，**-206**），identical 1155 → **1176**（+21）。
`verify-all` hardFail=0。`--all` 口径下 2.3 的 `\d+\.0` 有 267 个，此修复让 3.2 大幅靠拢。

**又排除一条**：在 `EmitText` 入口拦截 `text == "96"` —— **完全没有命中**（该结论已被 §8.49 解释：
primitive-ref 分支走 `FormatPrimitiveLiteral`，不经 `EmitText` 的文本比较路径）。

**A（控制流塑形，306 行，最大类）**：跨 `IL/ControlFlow/SwitchDetection.cs`、`ControlFlowSimplification.cs`、
`CSharp/ExpressionBuilder.cs:2210` 三处，单类可修性低。实例：`return mod != null && (mod.Characteristics & 8192) == 0;`
（2.3）vs `if (mod != null) { return …; } return false;`（3.2）；`if (msg == 736)` 链（2.3）vs `switch (msg)`（3.2）。

**B（显式转换，143 行）**：见 §8.44；普查建议的入口 `TranslatedExpression.cs:173` / `ExpressionBuilder.cs:1305`
两处补丁已验证可行（IntPtr / checked / 数组下标），但 `ldelem` 元素级 cast 仍未解决。

### 8.50 二元运算操作数的定型：精确入口（2026-11-18）

**现象**：`HexBytes` 里 2.3 印 `index + 1L`，3.2 印 `index + 1`（`index` 是 `long`；IL 为
`ldc.i4.1; conv.i8; add`）。全语料 `L` 后缀 2.3=130 / 3.2=38，`U` 后缀 2.3=199 / 3.2=61。

**已排除**：
- `VisitConv` 的 `SignExtend` 分支（`:1360`）确实收到 `conv.i8`（实测 kind 有 SignExtend），
  我在这里对编译期常量做了「按目标类型重定」——只让语料降了 2 行，`HexBytes` 的 `1` 没变；
- `ConvertConstantValue` 的值重定（`GetIntegralClrType`）——同样只影响 2 行。

**精确入口**：`ExpressionBuilder.HandleBinaryNumeric`（`:971`）
```csharp
left  = PrepareArithmeticArgument(left,  inst.LeftInputType,  inst.Sign, inst.IsLifted);
right = PrepareArithmeticArgument(right, inst.RightInputType, inst.Sign, inst.IsLifted);
```
它把操作数定型到 **IL 输入类型**（`LeftInputType`/`RightInputType`），而不是**运算结果类型**。
2.3 的结果是把常量按结果类型定型，所以 `1` 变成 `1L`。

**下一步**：确认 `BinaryNumericInstruction` 上记录结果类型的字段（可能是 `inst.ResultType` 或
`InferredType`），把常量操作数重定到该类型；或验证 2.3 是否对**所有**操作数（含非常量）都做。
若只对常量做，改动面很小；若对所有操作数做，需要评估对 `int`/`uint` 混合运算的影响。

**✅ 已实施**：不需要额外的结果类型字段 —— `ResolveBinaryOperator` 返回的 `rr.Type` 就是结果类型。
在 `HandleBinaryNumeric` 解析出 `rr` 之后，把**编译期常量**操作数重定到 `rr.Type`
（`RetypeConstantToResultType` → `ConvertConstantValue`，后者已含整型重定）。
实测 `HexBytes` 的 `index + 1` 现在印 `index + 1L`（3 处）。语料 15761 → 15760 —— 幅度小是因为
`HexBytes` 的 400 行差异主要由其它项贡献；`L`/`U` 后缀的总体缺口（130 vs 38、199 vs 61）主体仍未解。

**同一规则也解释 B 类（显式转换）**：`ldelem.u1` 的结果在 IL 栈上是 I4，2.3 按目标类型定型后
会补 `(int)`；3.2 不补。两者是同一条「按目标类型定型」规则的两个面。

**残留的 `checked(...)` 标注（同源、未解）**：`HexBytes` 里 2.3 印 `d[(int)((IntPtr)index)]` /
`(IntPtr)index + 1L`，3.2 印 `d[(int)checked((IntPtr)index)]` / `unchecked(index + 1L)`。
对照实验：把 `HandleBinaryNumeric` 里二元运算的标注（`inst.CheckForOverflow`）整段禁用后，
`checked` **仍在** —— 所以它不来自二元运算，而是 `AddCheckedBlocks` 的 **IL→AST 映射**：
IL 是 `conv.i`（0xD3，非 ovf），本不该有 checked 上下文，但 3.2 给转换节点挂了 `CheckedAnnotation`。
另注 `ExpressionBuilder` 里没有其它 `CheckedAnnotation` 赋值点（只有 `AddCheckedBlocks`），
故根因确定在 `AddCheckedBlocks` 的节点对应逻辑，属独立未解项。

**对照实验（负结果，已排除一条）**：把 `HandleBinaryNumeric` 里二元运算的 `inst.CheckForOverflow`
标注整段禁用后，`checked((IntPtr)index)` **仍在** —— 说明该标注不是来自二元运算路径，
来源确定在 `AddCheckedBlocks` 自身对 IL 指令的节点映射（`conv.i` 无 ovf 却挂了 `CheckedAnnotation`）。
`ExpressionBuilder` 中除 `AddCheckedBlocks` 外没有其它 `CheckedAnnotation` 赋值点，范围已锁死到该 transform。
后续若要修，需要读 `AddCheckedBlocks.GetResult` 的递归与 `InsertedExpression`/`Cost` 模型（改动面大）。

**进一步定位（已插桩验证）**：`HandleBinaryNumeric` 确实被调用（实测 `op=BitwiseOr`、`left=System.Byte`、
`right=System.Int32`），定型代码也确实跑了；但输出仍无 cast。原因在 `TranslatedExpression.ConvertTo`
（`:363-367`）：`ResolveCast` 的结果若 `IsCompileTimeConstant` 就走 `ConvertConstantValue` 直接返回常量、
**不加 cast**。所以「加不上 cast」是 `ConvertTo` 的常量捷径造成的，而不是定型没跑。
后续要修应在该捷径处区分「真的编译期常量」与「只是被解析器标成常量」的情形。

### 8.55 `checked((IntPtr)x)` 的排查结论（2026-11-18，未修）

把这条线索上能查的都查了，逐条排除（都已实测）：
- `ILReader.ReadConv`（`:594`）对 `conv.i` 传的是 **`false`** —— reader 没问题；
- `IL/Instructions/Conv.cs:164` 只是存下构造参数 —— 节点本身没问题；
- `ExpressionBuilder.VisitConv` 各分支**都不**加 `CheckedAnnotation`（只有 `HandleCompoundAssignment` :1337 会加）；
- `AddCheckedBlocks` 与 `HandleCompoundAssignment` 都不会产生它 —— 不是这两条路径；
- `CSharpResolver` 里设置 `CheckForOverflow` 的点只用于重载决议，与 `IntPtr` 转换无关；
- `ILAstOptimizer` / `ExpressionTransforms` 里没有改写 `Conv` 节点类型的地方。

**结论**：标注来源仍未定位。唯一确定的是「不是 reader、不是 Conv 节点、不是 VisitConv、不是
AddCheckedBlocks / HandleCompoundAssignment」。

**`ldelem.*` 路径（已查完）**：`ILReader.LdElem`（`:1184`）= `Push(new LdObj(new LdElema(...) { DelayExceptions = true }, type))`
—— `LdElema` 是带**元素类型**的（byte）；`ExpressionBuilder.VisitLdElema`（`:1886`）只构造
`new IndexExpression(array, index)`，**丢掉了元素类型**，随后 `VisitLdobj` 再解包。
但插桩显示定型前 `left` 的语义类型**已经是 `System.Byte`**，说明 `IndexExpression` 自身
（从 `byte[]` 推）给出了 byte，所以多出的 `(int)` **不是 `VisitLdElema` 直接产生**，
而是「`PrepareArithmeticArgument` 隐式加宽到 I4」与「`TypeOperandToResultType` 按尺寸再定型」
相互作用的结果。要修得先解释：两者尺寸相同为何仍补 cast。排查这个需要单步调试 `AddCheckedBlocks` 对
`d[(int)checked((IntPtr)index)]` 的处理（该表达式里 `checked` 包住了 `conv.i`）。

### 8.54 冗余 cast 不再被常量捷径吞掉：`(int)d[i]`（2026-11-18）

**问题**：`HandleBinaryNumeric` 里给操作数补的显式转换（byte → int 的 `(int)d[i]`）**没有出现在输出里**，
而 `HexBytes` 的 `index + 1` 却正常（输出 `index + 1L`）。插桩确认定型代码确实执行了
（`op=BitOr`、`left=System.Byte`、`right=System.Int32`）。

**真因**：`TranslatedExpression.ConvertTo`（`:363-367`）—— `ResolveCast` 的结果只要
`IsCompileTimeConstant` 为真，就直接 `ConvertConstantValue(...)` 返回常量，**不生成 cast**。
于是 byte 操作数被解析成常量后静默丢了 cast（而 `1` 本来就是常量，看不出差别）。

**修改**：把该捷径限定为**字面常量**：
`if (rr.IsCompileTimeConstant && !rr.IsError && ResolveResult is ConstantResolveResult)`。

**验证**：`HexBytes` 现在输出 `(int)checked((IntPtr)index)` / `(int)d[...]`，与 2.3 的 `(int)` 一致；
语料指标不变（14177 / 2155 / 7346 —— `HexBytes` 的差异由 `checked`/`unchecked` 贡献）；
`verify-all` hardFail=0。剩余：`checked((IntPtr)index)` vs 2.3 的 `(IntPtr)index`（§8.50）。

### 8.51 `?.` 空条件运算符：3.2 用，2.3 完全不用（2026-11-18）

`--all` 全量计数：`?.` 2.3 = **0**，3.2 = **107**。实例（`BMObject`）：
2.3 印 `if (closed != null) { closed.Invoke(...); }`，3.2 印 `this.Closed?.Invoke(...)`。

**来源**：ILAst 的 `NullPropagationTransform.IntroduceUnwrap`（`IL/Transforms/NullPropagationTransform.cs:274`，
创建 `NullableUnwrap` 的 282/286/292 行）；`ExpressionBuilder.VisitNullableUnwrap:2365` 再包成
`UnaryOperatorType.NullConditional`。2.3 没有这一步，所以一律渲染成显式判空（原始 IL 形状）。

**未修**：要字节对齐必须停用该优化（`?.` 会退回显式判空，约 107 处 + 语句结构变化），
属于 ILAst 层改动；且 `?.` 与 `??` 共用 null-propagation 基础设施，需评估连带影响。
**若只做「少改动」的取舍**：这一项的收益（107 行）比前面几项大，但改动面也最大，建议单独立项。

**补充**：`IntroduceUnwrap` 在 `NullPropagationTransform` 里有 4 个调用点（:119/:125/:132/:179），
而同一个 transform 也负责 `??` —— 全语料 `??` 计数 2.3 = **79**、3.2 = **122**（方向相反：3.2 引入得更多）。
也就是说 **整个 null-propagation 优化在 3.2 里都更激进**，`?.` 与 `??` 是同一族差异，
不建议只关 `?.` 而留下 `??`；要动就是评估整个 transform 的取/舍（2.3 基本不做这类合并）。

### 8.52 `?.` 空条件运算符：**已修**（2026-11-18）

**做法**：`DecompilerSettings.NullPropagation = false`（`DecompilerSettings32` 构造里，紧挨
`UseImplicitMethodGroupConversion`）。该设置的文档就是 "Decompile C# 6 ?. and ?[] operators."。

**效果（本段最大单项）**：
- 语料：Contracts.DnSpy 15760 → **14176**、Debugger 2780 → **2155**、Logic 7461 → **7346**；
  合计 26001 → **23677**（**-2324**），identical 1176 → **1180**。
- `dnSpy.dll` 全量 `?.` 计数：**107 → 0**，与 2.3 一致。
- `verify-all` hardFail=0；net48 0 错误。

**注**：`??` 计数仍是 3.2 多（122 vs 79），但那是 `NullCoalescingTransform` 的事，
且 3.2 多的方向与 `?.` 相反 —— 两项要一起评估，不能只改一边。

**对照实验（负结果）**：停用 `NullCoalescingTransform` 后语料**变差**
（Contracts.DnSpy 14176 → 14958、Debugger 2155 → 2500、Logic 7346 → 8199）。
即 `??` 的引入是对的，null 族两个 transform 的取舍结论就此闭环：
**`?.` 该关（2.3 没有），`??` 该留（3.2 更完整）。** 两者方向相反，必须成对评估。

### 8.53 现代语法特性普查：无更多「设置型」差异（2026-11-18）

对 `dnSpy.dll` 全量比对（2.3 vs 3.2）：

| 特征 | 2.3 | 3.2 | 结论 |
|---|---|---|---|
| `switch` | 335 | **608** | 3.2 的 switch 检测**更激进**；对齐意味着把已有 switch 退回 if 链，不合理，**排除** |
| `=>`（表达式体） | 600 | 572 | 基本持平，非差异源 |
| `ValueTuple` | 422 | 410 | 基本持平 |
| `out var` / 元组字面量 / `static local` / 模式匹配 `is T x` | 0 | 0 | 语料未使用 |
| `is null` | 2 | 2 | 一致 |

**结论**：在 `?.`（已关）之后，**没有更多「设置/开关型」的系统性差异**。
剩下的都属于「需重构或条件不明」：二元运算定型（含非常量操作数）、控制流塑形、
G 子项（临时变量 117 等）、B 类显式转换（143 行）。

**可复用的排查范式**（本轮多次命中）：
1. 先怀疑**设置/分支默认值**（`NullPropagation`、`UseImplicitMethodGroupConversion`、`ArrayInitializerWrapping` 各命中一次）；
2. 再确认**实际走哪个分支**（「两个 writer 都补 `.0` 却输出 `96`」与「两个 transform 都跑却叠 cast」都是靠这一步定位）；
3. 若两路都失效，说明是更深的类型标注/映射问题（`ldelem` 元素宽度、`AddCheckedBlocks` 的 IL→AST 节点映射），
   需要读该 transform 的实现而不是继续猜。

### 8.56 `L`/`U`/`UL` 后缀：常量按「操作数位置的类型」定型（2026-11-18，已修）

**现象**：`SimpleTypeConverter` 里 2.3 印 `j <= 20UL` / `value <= 9UL` / `n <= 9223372036854775807UL`，
3.2 印 `20L` / `9L` / `9223372036854775807L`。全语料后缀总量（同 1414 类型、`dnSpy.Contracts.DnSpy.dll`）：

| | `L` | `U` | `UL` | 合计 |
|---|---|---|---|---|
| 2.3 | 130 | 199 | 105 | **434** |
| 3.2（修前） | 150 | **86** | 88 | **324** |
| 3.2（修后） | 148 | **194** | 90 | **432** |

即 `U` 后缀 86 → 194（2.3 = 199），总量 324 → 432（2.3 = 434）。

**根因**：常量字面量的后缀取决于它被定型到的类型，而 3.2 定型到的是**字面量自身的 IL 宽度**
（`ldc.i4.s 20; conv.i8` → `long`），不是**解析出的运算符为该操作数位置选定的类型**。
`ulong n; n <= 20` 的运算符解析结果是 `ulong` 比较（`rr.Operands[0].Type == ulong`），
2.3 按它定型 → `20UL`；3.2 按 IL 宽度 → `20L`。

**两条路径**（都在 `ExpressionBuilder`）：
1. `HandleBinaryNumeric`（算术/位运算）：原来用 `rr.Type`（运算符**结果**类型）。改为
   `rr.Operands[i].Type`（运算符为该**操作数位置**选定的类型），见 `GetOperandTargetType`。
2. `TranslateComp`（`<`/`<=`/`>`/`>=`）：**原来完全不做定型**，直接提前 return。补上同一定型。
   比较运算符的 `rr.Type` 是 `bool`，所以必须用 `Operands[i].Type`，不能用结果类型。

**三个必须的守卫**（都是实测踩出来的）：
- `IsRetypableNumericType`：只重定数值类型（含 `Single`/`Double`/`Decimal`），排除枚举/指针/
  引用类型 —— 2.3 只重定数值字面量；
- **char 字面量不重定**：`AdjustConstantExpressionToType` 已经把数值 IL 常量转成 `'a'`，
  重定回 `97` 会把它改回去（实测 `if ('0' <= c && c <= '9')` → `if (48 <= c && c <= 57)`，
  `c - 'a' + '\n'` → `c - 97 + 10`，语料 +4）；
- `GetOperandTargetType` 必须接受 `ResolveResult` 而不是 `OperatorResolveResult`：
  **全常量表达式会折叠成 `ConstantResolveResult`**，硬转会抛 `InvalidCastException`，
  实测直接把 `FastTextBlock.CacheHash()` 变成 `// ILSpy 3.2 decompilation failed`（1 个类型降级）。

**验证**：`dnSpy.Contracts.DnSpy.dll` C# 14177 → **14176**、`Logic` 7346 → **7345**、
`Debugger` 2155 → **2155**；`dnSpy.Contracts.Logic.dll` VB 仅 `SimpleTypeConverter` 变化，
4 处 `L` → `UL`（`While j <= 20L` → `20UL` 等），与 2.3 一致；三个程序集 C#/VB dump
`degraded=0`；`verify-all` `hardFail=0`。

**注**：`diffLines` 只 −1/−1/0，因为后缀差异散落在更大的差异 hunk 里，
`DiffLineCount` 不会为「一行变得完全相同」逐行记账；后缀总量（上表）是更灵敏的口径。

### 8.57 `Nullable<T>.HasValue` → `x != null`（2026-11-18，已修）

**现象**：`CompilerDiagnostic.ToString()` 里 2.3 印 `if (this.LineLocationSpan != null)`，
3.2 印 `if (this.LineLocationSpan.HasValue)`；`Bit7EncodedInt32Data.Is7Encoded()` 里
2.3 印 `if (value == null)`，3.2 印 `if (!value.HasValue)`。
2.3 语料 `.HasValue` 出现 **0 次**，3.2 有 52 次（`dnSpy.Contracts.DnSpy.dll`，1414 类型）。

**根因**：IL 里 `Nullable<T>` 的 `== null` 就是 `get_HasValue()`，没有独立的「空」表示。
`CallBuilder.HandleAccessorCall` 把 `get_HasValue` 访问器按属性名输出成 `.HasValue`；
2.3 额外把该访问器翻成 `!= null`（取反时翻成 `== null`）。

**两处改动**：
1. `CallBuilder.HandleAccessorCall` 的 getter 分支加 `IsNullableHasValueGetter(method)`
   （`AccessorOwner` 是 `System.Nullable`1` 上名为 `HasValue` 的属性），
   输出 `BinaryOperatorExpression(target, InEquality, null)`；
2. `ExpressionBuilder.LogicNot` 加一条：`!(a != null)` → `a == null`、`!(a == null)` → `a != null`，
   否则取反场景会印成 `!(value != null)`。

**一个必须的坑**：`LogicNot` 里**必须 `Clone()` 子树**再改 `Operator`。
直接复用 `bin.Left`/`bin.Right` 会抛
`InvalidOperationException: Node is already used in another tree`（`set_Left` 的父节点检查），
实测一次降级 16 个类型（`HexPosition.get_HasValue`、`DocComment.get_Xml"1` 等）。

**验证**：`dnSpy.Contracts.DnSpy.dll` C# 14176 → **14151**（−25，`identical` 896 → **900**，
4 个类型变得完全一致）；`Logic` 7345 → **7344**；`Debugger` 2155 → **2155**。
`.HasValue` 52 → **0**（与 2.3 一致）；`!= null` 309 → 332、`== null` 504 → 533
（2.3 为 272 / 615）。三个程序集 C# dump `degraded=0`；VB `Logic` 降级数与修前一致（2）。
`verify-all` `hardFail=0`。

### 8.58 自动事件不再复制 `[method: Nullable(...)]`（2026-11-18，已修）

**现象**：`MetroWindow` 的字段式事件
```csharp
[Nullable(new byte[] {2, 1})]
[method: Nullable(new byte[] {2, 1})]   // ← 3.2 多出这一行
internal static event EventHandler<MetroWindowCreatedEventArgs> MetroWindowCreated;
```
2.3 只印事件级那一行。全语料 `[method: Nullable(new byte[] …)]` 计数：2.3 = **0**，3.2 = **28**。

**根因**：`PatternStatementTransform.TransformAutomaticEvents`（`:762`）把 add 访问器上的属性
搬到事件声明并加 `AttributeTarget = "method"`；`attributeTypesToRemoveFromAutoEvents` 的
过滤名单里没有 `System.Runtime.CompilerServices.NullableAttribute`
（`NullableContextAttribute` 不在名单里，所以 2.3 的 13 处 `[method: NullableContext(2)]` 保留）。

**改动**：把 `System.Runtime.CompilerServices.NullableAttribute` 加进
`attributeTypesToRemoveFromAutoEvents`。事件级 `[Nullable(...)]` 仍然保留，信息不丢。

**验证**：`[method: Nullable(...)]` 28 → **0**（与 2.3 一致）；`dnSpy.Contracts.DnSpy.dll`
按 LCS 精确 diff **9486 → 9458**（−28）；`Debugger` 的 `diffLines` 2155 → **2117**（−38）。
`degraded=0`；VB `Logic` 输出逐字节不变。
**注意**：`lang-diff-all` 的 `diffLines` 在 DnSpy 上反而 +2（14151→14153），
因为 `DiffLineCount` 是不做 LCS 的「指针各进一位」算法，删掉一行会打乱后续对齐；
以 LCS 口径为准（本报告 §8.48 已记录该口径缺陷）。

**补充（同日）**：`TransformAutomaticEvents` 对**抽象事件**也做转换
（`if (!ev.Modifiers.HasFlag(Modifiers.Abstract))` 只跳过「自动模式匹配」，不跳过转换），
于是照样把 add 访问器的属性以 `method:` 目标搬过去 —— 多出
`[method: NullableContext(2)]`（2.3 只保留事件级属性）。
修法：抽象事件跳过这段拷贝。
`HexView` 实测从「3.2 多 6 处 `[method: NullableContext]`」变成 **`diffLines=0`（完全一致）**；
`DnSpy.dll` 12207 → **12193**（`identical` 962 → **966**）、
`Logic` 6794 → **6793**、`Debugger` 1689 → **1666**（`identical` 233 → **237**）。
`degraded=0`；`verify-all` `hardFail=0`。

### 8.59 `return a && b` 被拆成 `if (a) return b; return false;`（2026-11-18，已修）

**现象**：`NodeFormatter.IsExe`（token `060015CD`）
```csharp
// 2.3                                  // 3.2
private static bool IsExe(ModuleDef m)  private static bool IsExe(ModuleDef mod)
{                                       {
    return m != null &&                      if (mod != null)
        (m.Characteristics & 8192) == 0;         return (mod.Characteristics & 8192) == 0;
}                                            return false;
                                         }
```
全语料计数（`dnSpy.Contracts.DnSpy.dll`，1414 类型）：

| | `&&` | `return false;` |
|---|---|---|
| 2.3 | **356** | 173 |
| 3.2 | **290** | **256** |

即 3.2 少 66 个 `&&`、多 83 个 `return false;` —— 强相关，是同一个模式。

**ILAst 实测**（`ilast-verify … dump`，本次新增的诊断开关）：
```
BlockContainer {
Block IL_0000 (incoming: 1) {
  if (comp(ldloc mod != ldnull)) leave IL_0000 (logic.not(binary.bit.and.i4(callvirt get_Characteristics(ldloc mod), ldc.i4 8192)))
  leave IL_0000 (ldc.i4 0)
}
}
```
即 `IfInstruction.TrueInst = leave(rhs)`、**`FalseInst = null`**（落到后面的 `leave(ldc.i4 0)`）。

**根因**：`&&` 的重建只发生在 `ExpressionBuilder.VisitIfInstruction`，而它的
`inst.MatchLogicAnd(out lhs, out rhs)`（`PatternMatching.cs:266`）要求
`FalseInst.MatchLdcI4(0)`。这里 `FalseInst` 是 `null`，直接不匹配；
`op` 停在 `Any`，函数返回 `null`，于是 `StatementBuilder` 走语句路径，
印成 `if (a) return b; return false;`。
而 `ExpressionBuilder.VisitIfInstruction` 根本不会被调用 —— `StatementBuilder`
在语句层就处理掉了这个 `if`（`StatementBuilder.VisitIfInstruction`，`:110`），
只有 `if` 处于表达式/条件槽位时才会走 `ExpressionBuilder`。

**修法（已实现）**：在 `StatementBuilder.ConvertBlockContainer` 的指令循环里加一条前瞻：
当 `Instructions[i]` 是 `IfInstruction`、`Instructions[i+1]` 是 `Leave(ldc.i4 0)` 时，
把两条语句合并成 `return cond && B;`（`TryTranslateShortCircuitReturn`）。
两个语义等价的守卫必须都成立（都是实测踩出来的）：
- **`FalseInst` 可以是 `Nop`**：3.2 的 ILAst 把「无 else」表示成 `FalseInst = Nop`
  而不是 `null`（`Block.WriteTo` 不打 `else` 是因为它认 `Nop`）。只判 `== null`
  时一处都命中不到（`&&` 计数 290 不变）。
- 尾部 `leave` 必须**不是** final leave（`IsFinalLeave`，即 `Value.MatchNop()`）：
  `ConvertBlockContainer` 会跳过 final leave 让它自然跌出块外，那种情况不会产生
  `return false;`，不能合并。

**`||` 变体**：`if (c) return B; return true;` ≡ `return !c || B;`（2.3 印
`return offset == 0U || offset < this.Span.Length;`）。同一函数里把尾部常量放宽到
`0` 或 `1`，`leftExpr` 在 `1` 时取 `exprBuilder.LogicNot(cond)`、运算符取
`ConditionalOr`。

**验证**：`dnSpy.Contracts.DnSpy.dll` C# `diffLines` 14081 → **12637**（−1444）、
`identical` 901 → **906**；`Logic` 7290 → **6829**（−461）；`Debugger` 2115 → **1773**（−342）。
`&&` 290 → **342**（2.3 = 356），`||` 133 → **155**（2.3 = 175），
`return false;` 256 → **203**（2.3 = 173），`return true;` 157 → **149**（2.3 = 132）。
三个程序集 C# dump `degraded=0 errors=0`；IL 语料仍 **1310 / 303 / 114 全一致**；
`verify-all` `hardFail=0`，`bench` 最慢 104 ms、`cecilCalls=0 fails=0`。

**遗留**：还剩 14 个 `&&` / 20 个 `||` / 30 个 `return false;` / 17 个 `return true;`
未合并（`trueExpr` 非布尔、条件带副作用、或尾部常量不是 0/1 的变体）。

**补充（同日）**：再加一个方向 —— `if (c) return false; return B;` ≡ `return !c && B;`
（2.3 在 `UIUtilities.GetElementFromEvent` 里就是这么印的：
`return e.ChangedButton == null && UIUtilities.GetItem<T>(...) != null;`）。
`dnSpy.Contracts.DnSpy.dll` 12446 → **12207**、`Logic` 6808 → **6794**、
`Debugger` 1709 → **1689**，`identical` 961 → 962。

**负结果（同一次实验）**：把 `if (c) return true; return B;` ≡ `return c || B;` 也合并
是**净亏** —— `Logic` +77（6794 → 6871）、`DnSpy` 仅 −3。2.3 在这个朝向上保留
if 形态，所以**不做**。四个朝向里只实现三个。

### 8.60 既有缺陷：`dotnet build dnSpy.sln` 的 net48 目标会偶发引用错配（与本次改动无关）

**现象**：同一个提交上连续跑 `dotnet build dnSpy.sln -c Release`，错误数在 **0 / 2 / 8 / 10** 之间跳：
```
CSharpToVBConverter32.cs(871,3): error CS0012: 类型“Enum”在未引用的程序集中定义。
  必须添加对程序集“System.Runtime, Version=10.0.0.0, ...”的引用。 […::TargetFramework=net48]
Microsoft.WinFX.targets(281,9): error MC2000: … Could not find assembly
  'System.Runtime, Version=10.0.0.0' …
```
即 net48 的编译/标记编译偶尔拿到 **net10 的 `System.Runtime`**。

**已排除是本次改动引起**：把引擎 checkout 到本次会话开始前的 `6eaaecbcb`，
同样连续构建得到 `0 / 8 / 0`；只构建 `dnSpy.Decompiler.ILSpy32 -f net48` 则稳定 0 错误。
所以是解决方案级并行构建的引用解析竞争（`dnSpy.Decompiler` 等项目的
`OutputPath` 不带 `$(TargetFramework)`、`AppendTargetFrameworkToOutputPath=false`，
net10 与 net48 的产物落在相邻/共享目录），**先于本次升级存在**。

**影响**：`verify-all`（只构建引擎 + net10 扩展 + probe）稳定 `hardFail=0`；
`dnSpy.sln` 需要重跑 1–4 次才拿到 0 错误。CI 接线时建议：
① 构建加 `-m:1` 或分目标构建；② 把 net48 门禁改成先单独构建各 ProjectReference。

### 8.61 关闭 `OutVariables`：`out int x` → 先声明再 `out x`（2026-11-18，已修）

**现象**：`MetroWindow.WndProc` 里
```csharp
// 2.3                                    // 3.2
int dpiX;                                out int dpiX,
int dpiY;                                out int dpiY,
WindowUtilities.GetDpi(GetWindowHandle(),   WindowUtilities.GetDpi(GetWindowHandle(), out dpiX, out dpiY);
         out dpiX, out dpiY);
```
3.2 用 C# 7 的 inline `out var`；2.3 先声明局部再传 `out`。
`dnSpy.Contracts.DnSpy.dll` 上 `out <类型> <名字>` 计数：2.3 = 88（都是先声明的），
3.2 = 163（内联）。

**根因**：`DecompilerSettings.OutVariables` 默认 `true`（`DecompilerSettings.cs:621`）。
2.3 基于 ILSpy 2.x，早于 C# 7，没有这个特性。

**改动**：`DecompilerSettings32` 的「dnSpy 2.3 compatibility」块加
`EngineSettings.OutVariables = false;`（与 `NullPropagation` / `QueryExpressions` /
`UseImplicitMethodGroupConversion` 并列）。

**验证**：`out <类型> <名字>` 163 → **7**；
`dnSpy.Contracts.DnSpy.dll` C# `diffLines` 14153 → **14081**（−72，`identical` 900 → **901**）、
精确 LCS 9458 → **9379**（−79）；`Logic` 7344 → **7290**（−54）；`Debugger` 2117 → **2115**。
VB `Logic` LCS 8887 → **8881**，且 `_discard1`/`_discard2` 变成真实参数名
（`framework` / `profile` / `bytesUsed` / `completed`），因为内联 `out` 变量此前被
VB 转换当成 discard。`degraded=0`；`verify-all` `hardFail=0`。

### 8.62 残留差异清单（2026-11-18，供下一轮起点）

`dnSpy.Contracts.DnSpy.dll` C#（1414 类型，2.3 vs 当前 HEAD `1a73b43b6`）逐模式计数：

| 模式 | 2.3 | 3.2 | 差 | 判定 |
|---|---|---|---|---|
| `goto` | 118 | 71 | −47 | **3.2 更好**，不对齐 |
| `get_XXX()` | 45 | 0 | −45 | **3.2 更好**（`string.get_Chars(i)` → `s[i]`），不对齐 |
| `switch` | 38 | 74 | +36 | 3.2 检测更激进；§8.53 已判定不对齐 |
| `cast (T)x` | 755 | 794 | +39 | 显式转换偏多，未定位 |
| `??` | 79 | 104 | +25 | 多见于惰性初始化 `x ?? (x = () => new T())`，2.3 结构不同 |
| `?:` | 405 | 427 | +22 | 未定位 |
| `&&` / `\|\|` | 356 / 175 | 342 / 155 | −14 / −20 | §8.59 已修，剩余为 `if (c) { …; return B; }` 带额外语句的形态，无法合并 |
| `if` 行 | 1815 | 1747 | −68 | 与 `return false/true` 减少同源 |
| `while (true)` | 0 | 6 | +6 | 循环塑形，未定位 |
| `L/U` 后缀 / `double .0` | 235 / 267 | 238 / 269 | +3 / +2 | 已对齐（§8.56 / §8.49） |

**最大单类型**：`HtmlClipboardFormatWriter`（117 行）混合了四类：
`AppendInterpolatedStringHandler` 临时变量拆分、`while (i < length)` → `while (true){ if (i>=length) break; }`、
局部变量改名（`i`→`j`）、`char c` 提升到 switch 外。均属控制流/变量生命周期层面，单点收益低。

**门禁基线**（HEAD `1a73b43b6`）：`verify-all hardFail=0 softFail=1 skip=0`；
IL 语料 1310/303/114 全一致；`dnSpy.sln` 0 错误；`bench` 最慢 71 ms。

### 8.63 构建卫生：`Debug.Assert` 崩溃是陈旧产物，不是代码回归（2026-11-18）

**现象**：同一提交上 `verify-all -NoBuild` 连续两次报 `softFail=6`，5 个 C#/VB 语料门
`exit=-2146232797`，进程打印
```
Process terminated. Assertion failed.
  at SequencePointBuilder.AddToSequencePoint(ILInstruction) ...:line 255
  Debug.Assert(current.Function == null || current.Function == function);
```
即 `SequencePointBuilder` 的 `Debug.Assert` 失败（.NET Core 下 `Debug.Assert` 失败会终止进程）。

**排除是代码回归**：把引擎与扩展**按手工命令重构建一次**
（`ICSharpCode.Decompiler.csproj -c Release` + `dnSpy.Decompiler.ILSpy32.csproj -c Release -f net10.0-windows`）
后，同样的 `verify-all -NoBuild` 立刻回到 `hardFail=0 softFail=1 skip=0`。
期间唯一的提交是文档改动（`5f5e43a85`），引擎代码未变。

**结论**：`dnSpy\dnSpy\bin\Release\net10.0-windows\` 下的引擎 DLL 与 probe/扩展出现
**陈旧或错配**（`Debug.Assert` 只有在 DEBUG 常量下才编译进去，说明该 DLL 来自带 DEBUG 的构建）。
与 §8.60 的 net48 引用竞争合起来看：**本仓库的并行/增量构建产物不可靠，门禁前必须干净重构建**。

**操作建议**（写进 CI）：
1. 构建前 `dotnet build-server shutdown`，必要时清 `obj/bin`；
2. 门禁固定用「先手工重构建引擎+扩展，再 `verify-all -NoBuild`」两步，别依赖 `verify-all` 的
   自动构建产物；
3. 加一条 `bench` 与语料门的 `degraded/errors` 断言，崩溃要显式失败而不是静默 `exit=-2146232797`。

**复发与定位（同日稍晚）**：改完文档后 `verify-all -NoBuild` 变成 `softFail=4` ——
**三个 C# 语料门全部** `exit=-2146232797`，栈顶是
`SequencePointBuilder.AddToSequencePoint(ILInstruction):255` 的
`Debug.Assert(current.Function == null || current.Function == function)`。
而在这之前的同一个提交上，同样的门禁是 `hardFail=0`。

两次之间唯一的动作是跑了 `dotnet build dnSpy.sln -c Release`（还跑了两次）。
**把引擎与扩展按手工命令重构建一次后，C# 语料门立刻恢复**：
`types=1310 identical=962 diffLines=12207 errors=0`。

**结论（更强）**：`dotnet build dnSpy.sln` 产出的引擎 DLL 与手工
`dotnet build ICSharpCode.Decompiler.csproj -c Release` 产出的**行为不同** ——
前者的 `Debug.Assert` 是激活的（否则 `Debug.Assert` 在 .NET Core 下会直接终止进程，
门禁不可能是 `exit=-2146232797`）。所以：
- 门禁**必须**用「手工重构建引擎+扩展，再 `verify-all -NoBuild`」这两步；
- 只要跑过 `dnSpy.sln`，就必须重构建，否则拿到的是假失败；
- 这条不只在 CI 里成立，本地换着构建方式也会踩到。

**另注**：`SequencePointBuilder` 那条不变量本身值得单独查 —— 它说明存在
「AST 节点携带的 IL 指令属于另一个 `ILFunction`」的情形。Release 构建下它不崩，
但会**静默产生错误的 sequence point**，所以不是纯 debug-only 问题。

**门禁已加固（同日）**：`verify-all.ps1` 现在做两件事，都是被上面这次事故逼出来的：
1. 新增 **`solution build (-m:1)`** 硬门（net10.0-windows + net48 都要 0 错误）。
   `-m:1` 实测稳定（连跑两次 0 错误），并行的 `dotnet build dnSpy.sln` 才是
   §8.60 那个 0/2/8/10 跳变的根源。
2. `Invoke-Probe` 里**负退出码一律记硬失败**（`-2146232797` 这种崩溃不再被
   `-Soft` 吞成 softFail）；跑完 `dnSpy.sln` 后再 `dotnet build-server shutdown`
   并把引擎+扩展**重构建回来**，让工作区保持可门禁状态。

实测：完整 `verify-all`（带构建）`hardFail=0 softFail=1 skip=0`，
随后 `-NoBuild` 的 C# 语料门 `types=1310 identical=968 diffLines=11937 errors=0`
（即崩溃隐患已被自愈，不需要人工干预）。

**已定位（同日，Debug 引擎实测）**：把引擎按 `-c Debug` 构建后必崩，
`lang-diff-all` 逐个类型跑，崩在 **`dnSpy.Contracts.MVVM.GridViewColumnSorter`**。
栈是
```
SequencePointBuilder.AddToSequencePoint(ILInstruction):255   (Debug.Assert)
 ← AddToSequencePoint(AstNode) ← VisitChildren
 ← VisitThisReferenceExpression ← VisitMemberReferenceExpression
 ← VisitIndexerExpression ← VisitQuerySelectClause
```
对应源码 `descsProvider.Descs.Columns = columns.Select(a => toDesc[a]).ToArray();`
—— **lambda 里的 `this.toDesc`**：`ThisReferenceExpression` 带的是**外层方法**的 IL 指令，
而当前 sequence point 属于 lambda 的 `ILFunction`，于是不变量被打破。

**这条是 3.2 既有缺陷，不是本次改动引入的**（本次没碰 `ThisReferenceExpression` /
query 子句）；Release 构建下不崩，但 sequence point 会被写错。
**未修**：修它要改 `AddToSequencePoint` 的语义（跳过跨函数指令），
会影响 PDB/sequence point 映射，而现有门禁**不校验 sequence point**，
无法验证 —— 故只定位，等补上门禁再做。

**诊断工具**：`baseline/probe` 的 `LangDiffAll` 现在支持
`PROBE_TRACE_TYPES=1`，逐个打印正在反编译的类型名，崩溃时最后一条即肇事类型。

### 8.64 数组初始化器 `{ 2, 1 }` 缺左空格（2026-11-18，已修）

**现象**：属性上的可空特性参数
```csharp
// 2.3                              // 3.2
[Nullable(new byte[] { 2, 1 })]   [Nullable(new byte[] {2, 1 })]
```
全语料 `{ ` 计数：2.3 = **2244**，3.2 = **2008**（差 236）。反编译输出里数组初始化器
只出现在特性实参上，所以这是唯一受影响的位置。

**根因**：`CSharpOutputVisitor.PrintInitializerElements` 的单行分支
（`CSharpOutputVisitor.cs:646`）写 `{` 后直接跟元素，只有 `}` 前有 `Space()`。
2.3（NRefactory）两侧都有空格。

**一个必须绕开的坑**：在 `{` 之后调 `Space()` **无效** —— 实测输出仍是 `{|2, 1 }`
（用 `{|` 做标记验证过该分支确实被走到）。token-writer 链里 `Space()` 对紧随其后的
**基本类型字面量**不生效，而对其后的 `WriteToken` 生效（`}` 前那个空格就正常）。
所以把左空格直接写进 token 文本：`writer.WriteToken(Roles.LBrace, "{ ")`。

**验证**：`{ ` 2008 → **2247**（2.3 = 2244）。
`dnSpy.Contracts.DnSpy.dll` C# `diffLines` 12637 → **12446**（−191），
`identical` 906 → **961**（**55 个类型变得完全一致**）；
`Logic` 6829 → **6808**；`Debugger` 1773 → **1709**。
`degraded=0`；`verify-all` `hardFail=0`，IL 语料仍 1310/303/114 全一致。

**附带发现的方法论问题**：这次普查原本按**文件索引**配对两个 dump，是错的 —— 两个引擎的
类型枚举顺序不同（索引会错位），而且 `dotnet build dnSpy.sln` 重建目标程序集后 token 注释
会整体位移。详见 §8.65；下面的数字**以 `lang-diff-all` 为准**。

### 8.65 dump 对比不可靠 + 显式接口实现的 token/顺序差异（2026-11-18，**未修**）

**dump 对比的可靠性问题**（影响 §8.62 那张表）：`baseline dump` 的两个引擎产物
**不能**直接配对做逐类型/逐模式统计，原因有三：
1. 文件名前缀是**枚举序号**，两个引擎的类型枚举顺序不同 → 按索引配对会错位；
2. `dnSpy.Contracts.DnSpy.dll` 等被 `dotnet build dnSpy.sln` 重建后，**token 注释整体位移**
   （实测 `MenuItemBase` 的 RID 从 2106 变 2025），旧 dump 立即失效；
3. `--impl core` 的解析目录是 `baseline/ilspy23`，那里有一份**同名的旧 `dnSpy.Contracts.DnSpy.dll`**
   （md5 与 dnSpy bin 的不同），`--dnspy-dir` 指向 dnSpy bin 时 core 引擎直接崩。

**可靠口径**只有一条：`lang-diff-all`（同一进程、同一 `ModuleDef`、逐类型配对），
其 IL 语料 1310/303/114 全一致也反证了「两个引擎读的是同一个模块」。
§8.62 的**逐类型归因**请忽略，聚合总量近似可用；§8.57/§8.58 的「精确 LCS」数字
同样是在错位/换版本的 dump 上算的，**不可引用**。

**实测到的真实差异（`lang-diff` 单类型，可靠）**：`MenuItemBase\`1`
- 2.3 的成员顺序是纯元数据序（`CreateContext` 0x83A、`CachedContextKey` 0x83B、
  `GetCachedContext` 0x83C、`IMenuItem.Execute` 0x83D …）；
- 3.2 把**显式接口实现提到最前**并与其公共方法成对出现，且**两行共用同一个 token**
  （`IMenuItem.Execute` 与 `public abstract void Execute(TContext)` 都印 `0x06000832`），
  而 2.3 给的是 0x83D / 0x844 两个不同的 token。
- 类型自身 token 也差 1（2.3 `0x020001AD` vs 3.2 `0x020001AC`），但 `UIUtilities`、
  `SpanDataCollection` 等类型的 token 完全一致 → 不是全局偏移，是**特定成员**的标注问题。

**判定**：属「显式接口实现的 token 标注 + 成员排序」，机制指向 `TypeSystemAstBuilder`/
`InsertTokenComments` 的 `Annotation<MethodDef>()` 归属，改动面在核心 binder，**本次只定位**。
token 注释是可选显示项（`ShowTokenAndRvaComments`），优先级低于代码本体差异。

**负结果（同日实验）**：`CachedTextColorsCollection` 里 3.2 比 2.3 多一行
`// (get) Token: …`（索引器的访问器注释），看着像「索引器不该打访问器注释」。
把 `InsertTokenComments` 的 `IndexerDeclaration` 分支删掉后实测 **变差**：
`DnSpy.dll` 11937 → **11970**（+33）。即 2.3 对**多数**索引器确实打访问器注释，
只有个别（如该索引器）不打。**已还原，不做**。

### 8.66 `readonly get;` 被降级成 `[IsReadOnly] get;`（2026-11-18，已修）

**现象**：`ToolWindowContentInfo`（struct）里 2.3 印
```csharp
public Guid Guid { readonly get; set; }
```
3.2 印
```csharp
public Guid Guid
{
    [IsReadOnly]
    get;
    set;
}
```
外加一行 `using System.Runtime.CompilerServices;`。

**根因两处**：
1. `TypeSystemAstBuilder.ConvertAccessor` 把访问器上的
   `System.Runtime.CompilerServices.IsReadOnlyAttribute` 原样输出。2.3 把它翻成
   C# 8.0 的 `readonly` 访问器修饰符。
2. `CSharpOutputVisitor.VisitPropertyDeclaration` 的**单行自动属性折叠分支**
   （`ForceOneLine` 且两个访问器都无函数体）是**手写** `get;`/`set;` 的，
   **不写访问器修饰符** —— 所以即使加了 `Modifiers.Readonly` 也不会输出。

**两处都改**：`ConvertAccessor` 检测到 `IsReadOnlyAttribute` 时置
`Modifiers.Readonly` 并跳过该属性；折叠分支补 `WriteModifiers(((Accessor)node).ModifierTokens)`。

**验证**：`ToolWindowContentInfo` `diffLines` 65 → **1**（剩下那条是
`double.MaxValue` vs `1.797...E+308`，源码写的是 `double.MaxValue`，**3.2 更贴源码**，不对齐）。
语料：`DnSpy.dll` 12193 → **11937**（`identical` 966 → **968**）、
`Logic` 6793 → **6774**、`Debugger` 1666 → **1338**（`identical` 237 → **238**）。
`degraded=0`；`verify-all` `hardFail=0`。

### 8.67 枚举常量的算术语境：`ColorType.FirstNR` → `16384`（2026-11-18，**未修**）

**源码**（`Themes/ExtensionMethods.cs:32`）：
```csharp
public static ColorType ToColorType(this TextColor self) => (uint)self + ColorType.FirstNR;
```
**2.3**：`return self + ColorType.Text;`（丢了 `(uint)` 转换，但保留了枚举成员名）
**3.2**：`return (ColorType)((int)self + 16384);`（转换运算符选成 `int`，常量退化成数字）

**两处 3.2 弱点**：
1. `TypeSystemAstBuilder.ConvertConstantValue` 的 `GetIntegralClrType` 只认 8 种整型
   （`KnownTypeCode.SByte..UInt64`），**枚举返回 null** → 直接落到
   `new PrimitiveExpression(constantValue)`，于是枚举常量印成数字。
   2.3 会印成员名（`FirstNR == Text`，取第一个名字 `Text`）。
2. `HandleBinaryNumeric` 只对**位运算**做枚举处理
   （`ExpressionBuilder.cs:1139` 的 `op.IsBitwise()`）；`+`/`-` 会把操作数降到底层类型，
   常量也就回不到枚举类型。

**未修原因**：修 1 需要给 `ConvertConstantValue` 加枚举成员命名（含 `[Flags]` 组合值
的拆分），修 2 要动 `HandleBinaryNumeric` 的操作数定型 —— 两者都会影响所有枚举运算，
而门禁只有「与 2.3 的 diff」，没有「与源码的 diff」。**建议先补一个源码对拍门禁**
（仓库里就有 dnSpy 自己的源码），否则这类改动无法验证对错。

**顺带**：同族的 `(uint)self` 被印成 `(int)self`（显式转换运算符选错符号），
也归到 2。

**负结果（同日实验）**：把 `HandleBinaryNumeric` 的枚举分支从 `op.IsBitwise()`
放宽到也含 `Add`/`Subtract`，**语料零变化**（11937/6774/1338 一个数都没动）。
原因是常量先被 `PrepareArithmeticArgument`/`ResolveBinaryOperator` 定型成
`uint`，`AdjustConstantExpressionToType(right, left.Type)` 的目标已经是 `uint`
而不是枚举 —— 光放宽谓词没用，得改操作数定型本身。已还原。

### 8.68 最大残留模式：ILAst 临时局部变量不内联（2026-11-18，**未修**）

**这是剩余 diffLines 里最大的一类**，样例（`MethodDebugInfoBuilder`，C# 63 行差）：
```csharp
// 2.3（内联）                                // 3.2（保留临时变量）
if (cur.ILSpan.End < next.ILSpan.Start)       iLSpan = cur.ILSpan;
{                                             uint end = iLSpan.End;
    ...  new SourceStatement(                  iLSpan = next.ILSpan;
        ILSpan.FromBounds(cur.ILSpan.Start,     if (end < iLSpan.Start) {
            next.ILSpan.Start), cur.TextSpan);      ...  iLSpan = cur.ILSpan;
}                                                 uint start = iLSpan.Start;
                                                  iLSpan = next.ILSpan;
                                                  ...  ILSpan.FromBounds(start, iLSpan.Start) ...
```
同类还出现在 `SpanDataCollection`（163 行，`Span span = spanData.Span;`）、
`CachedTextColorsCollection`（62 行）、`UIUtilities`（152 行的一部分）。

**疑似根因（已被实验否证，待重查）**：原以为在 `IL/Transforms/CopyPropagation.cs` 的
`CanPerformCopyPropagation` —— `StLoc` 的源是 `callvirt get_ILSpan` 这类纯属性 getter，
落进 `default:` 分支后被 `value.Children.Count == 0` 否掉（`CallVirt` 有子节点）。
**实测把判据放宽成「无子节点 或 `SemanticHelper.IsPure`」后语料几乎零变化**
（`DnSpy` 11937 → **11941**，反而 +4）。所以内联**不是** `CopyPropagation` 干的，
2.3 的差异来自别处（候选：`DeclareVariables` 的插入点选择、`SplitVariables`、
或 2.3 的 ILAst 优化管线本身）。**根因未定位**，不要按上面的猜测改代码。

**未修**：量级最大（影响所有结构体属性访问），且根因未明；现有门禁只有
「与 2.3 的 diff」，没有「与源码的 diff」，即使找到改动点也无法证明改对 ——
再次指向 §8.67 的建议。

**顺带**：`DeclareVariables` 只负责「声明」这些变量，不负责内联；
`SplitVariables`/`RemoveDeadVariableInit` 也不是干这个的。

### 8.69 残留中「2.3 更差、不该对齐」的清单（2026-11-18）

逐类型用 `lang-diff` + **源码**对拍过一遍，下面这些是 2.3 的降级产物，
**修 3.2 去对齐它们只会让输出变差**，后人不值得再花时间：

| 模式 | 2.3 | 3.2（更贴源码） |
|---|---|---|
| 自动属性 | 展开成 `<Span>k__BackingField` + `[CompilerGenerated] get { … }` | `public Span Span { get; }` |
| `double.MaxValue` | `1.7976931348623157E+308` | `double.MaxValue` |
| `TimeSpan.FromMilliseconds(milliseconds)` | `(long)milliseconds`（多加 cast） | `milliseconds` |
| LINQ 扩展方法 | `Enumerable.Select<ITreeNode, TreeNodeData>(…)` 静态形式 | `self.TreeNode.Descendants().Select(…)` |
| 静态构造器 | 把字段初始化式塞回 `static X() { … }` + `// Note: …beforefieldinit` | `static readonly X Empty = new X(…)`（贴源码） |
| `if (!x) return false; return y;` 的短路 | —— | 3.2 合并成 `return !x && y`（`return true` 那个朝向除外，见 §8.59 负结果） |
| `(uint)hi < (uint)array.Length` | `hi < array.Length`（**丢掉源码里的显式转换**，语义都变了） | `(uint)hi < (uint)array.Length`（与 `SpanDataCollection.cs:89` 一致） |

**判据**：一律以**源码**为准（仓库里有 `dnSpy/*.cs`），不是以 2.3 为准。
凡是 3.2 更贴源码的，就从「兼容残留」里剔除。§8.67/§8.68 剩下那些
（枚举常量命名、临时变量内联、循环塑形、显式接口 token/顺序、
lambda `this` sequence point）才是**两个引擎都错或 3.2 确实更差**的真残留。
（`(uint)` cast 一项经查源码 `SpanDataCollection.cs:89` 就是 `(uint)hi < (uint)array.Length`，
属 2.3 丢转换，已移到上表。）

### 8.70 VB 侧实测（`MethodDebugInfoBuilder`，C# 63 行 / VB 68 行）

用 `lang-diff` 同时取 C# 与 VB 输出对拍，源码在
`dnSpy/dnSpy.Contracts.Logic/Decompiler/MethodDebugInfoBuilder.cs:72`：
```csharp
public MethodDebugInfoBuilder(int decompilerSettingsVersion, StateMachineKind stateMachineKind, MethodDef method, MethodDef? kickoffMethod) {
    ...
    if (method == kickoffMethod)
```
- **VB 参数改名是必要的，3.2 正确**（初判写反了，已更正）：源码参数是
  `StateMachineKind stateMachineKind` —— 参数名与类型名**只差大小写**。VB 不区分
  大小写，直接印 `stateMachineKind As StateMachineKind` **根本编译不过**，所以
  `CSharpToVBConverter32.RegisterParameterName`（`:455`）会改名成
  `stateMachineKind2`。2.3 印原名 → **产出无效 VB**。**属 §8.69 那类「2.3 更差」，
  不要对齐。**
- **`For` vs `While`**：2.3 印 `For i As Integer = 0 To Me.statements.Count - 1 - 1`，
  3.2 印 `Dim i As Integer = 0` + `While i < Me.statements.Count - 1`。
  3.2 更干净但不同 —— 属循环塑形（C# 侧同理，见 §8.68 提到的那类）。
- 临时变量内联（同 §8.68）在 VB 侧同样存在。
- VB 只有 `lang-diff` 一个抽样门（`-Soft`），**建议给 VB 也加整程序集语料门**，
  否则这类差异只能靠抽样发现。

### 8.71 源码对拍工具 `baseline/source-check.py`（2026-11-18）

§8.67/§8.68 反复指出「门禁只有与 2.3 的 diff，证明不了改对」。这个脚本把
**仓库自带的 dnSpy 源码**当判据，做最保守的一种对拍：

```
py baseline/source-check.py <dumpDir> <sourceDir>
# <dumpDir> = baseline dump --impl ilspy32 --lang cs --all 的输出
```
对每个类型：找到提及该类型短名的源文件，提取**类级成员声明**名
（缩进 ≤ 1 tab；跳过注释、许可头、同文件里的其他类型声明），
报「源码里有、反编译输出里没有」的名字。少一个名字就是**掉了/改了成员**的强信号。

**实测**：`dnSpy.Contracts.Logic.dll` → `checked=112 flagged=0`
（112 个类型全过，0 误报）。已知噪音源：嵌套类型、`partial` 拆分、
`record`/`required` 等新语法的声明形式 —— 脚本是**启发式**，
用途是「改完之后跑一遍看有没有把成员弄丢」，不是完整解析器。

**用法**：改完枚举命名 / 临时变量内联这类改动后，先跑一遍这个脚本确认没有
成员丢失，再跑 `lang-diff-all` 看兼容性 —— 两个信号一起才够。

### 8.72 循环塑形定位：`HighLevelLoopTransform.MatchWhileLoop` 的 `MatchLeave(loop)` 失败（2026-11-18）

**现象**：源码 `for (int i = 0; i < length;)`（`HtmlClipboardFormatWriter.cs:51`），
2.3 印 `int i = 0; while (i < length) { … }`，3.2 印
`int j = 0; while (true) { if (j >= length) break; … }`。**3.2 更差**（真残留）。

**定位到具体判定**。`HighLevelLoopTransform.MatchWhileLoop`
（`IL/Transforms/HighLevelLoopTransform.cs:52`）四个条件里，前三个都过，
**卡在 `ifInstruction.TrueInst.MatchLeave(loop)`**。
插桩实测（`PROBE_LOOP_DEBUG=1`，方法 `WriteString` token `060001AD`）：
```
[LOOP] instr0=IfInstruction count=4 final=Nop
[LOOP] falseNop=True captured=False leave=True      ← 第一个循环，成功
[LOOP] instr0=IfInstruction count=7 final=Nop
[LOOP] falseNop=True captured=False leave=False     ← 第二个循环，失败
```
对应 ILAst（`ilast-verify … dump`）：
```
BlockContainer {
Block IL_0000 (incoming: 1) {
  stloc j(ldc.i4 0)
  BlockContainer (while-true) {
    Block IL_00f6 (incoming: 2) {
      if (comp.signed(ldloc j >= ldloc length)) leave IL_0000 (nop)
      …
```
即循环**入口处确实有条件 `if (j >= length)`**，但该 `leave` 的
`TargetContainer` **不是循环容器本身**（`LoopDetection` 只把 `Branch` 改写成
`Leave(loopContainer)`，管不到已经是 `Leave` 的出口）。于是 `MatchLeave(loop)` 为假，
循环停留在 `while-true` 形态。

**未修原因**：修它要决定「`leave` 指向祖先容器」时如何重构（把该 `leave` 变成
循环的出口、并把条件反相成 `while (cond)`），会影响**所有**顶层循环的塑形；
而且 `LoopDetection`/`DetectExitPoints` 的交互需要整体回归。属控制流核心，
**本次只定位到判定**。

### 8.73 显式接口实现：3.2 成对排列且**两行共用同一个 token**（2026-11-18，**未修**）

`MenuItemBase\`1` 实测（`lang-diff`，`C# diffLines=146`）：

**2.3**（纯元数据序，token 各不相同）
```
// Token: 0x0600083A   CreateContext
// Token: 0x0600083B   CachedContextKey
// Token: 0x0600083C   GetCachedContext
// Token: 0x0600083D   void IMenuItem.Execute(IMenuItemContext)
// Token: 0x0600083E   …（其余显式实现）
// Token: 0x06000844   public abstract void Execute(TContext context);
```
**3.2**（把显式实现提到最前、与对应公共方法**成对**，且两行同一个 token）
```
// Token: 0x06000832   void IMenuItem.Execute(IMenuItemContext context)
// Token: 0x06000832   public abstract void Execute(TContext context);   ← 同一个 token
// Token: 0x06000833   … IsEnabled 一对
// Token: 0x06000834   … IsVisible 一对
```
注意 `0x06000832`(2098) 与 2.3 的 `0x0600083D`(2109) 差 11，而**同文件里普通成员
（如 `MethodDebugInfoBuilder.CompilerName` 的 `0x0600023F`）两边完全一致** ——
所以不是版本/模块差异，是**显式实现这一类成员**的 `MethodDef` 标注问题。

`TypeSystemAstBuilder.ConvertMethod`（`:1261`）确实写的是
`decl.AddAnnotation(new MemberResolveResult(null, method))`，`method` 是实现方法
本身；但输出里公共方法也拿到了显式实现的 token，说明
`Annotation<MethodDef>()` 的解析/归属在显式实现场景下串了。

**未修**：改它要动核心 binder 的成员标注与排序，且**没有门禁校验 token 注释**
（`ShowTokenAndRvaComments` 可关），改错不会被发现。建议先给 token 注释加一道
抽检门（随机抽 N 个成员，断言 `MetadataToken` 与该成员在源码中的声明一致）。

### 8.74 显式接口 token 的机制：`AttachMembers` 拿到的 `typeDef.Methods` 不含显式实现（2026-11-18）

给 `AstAnnotationBinder.AttachMembers`（`Common/AstAnnotationBinder.cs:265`）插桩后，
`MenuItemBase\`1` 的实况是：
```
[BIND] decl='CreateContext'  pit=False -> null    || meta: Execute;IsEnabled;IsVisible;GetHeader;GetIcon;GetInputGestureText;IsChecked;.ctor
[BIND] decl='Execute'        pit=True  -> Execute@dnSpy.Contracts.Menus.MenuItemBase
[BIND] decl='Execute'        pit=False -> Execute@dnSpy.Contracts.Menus.MenuItemBase
```
两个 `Execute` 声明（显式实现 + 公共方法）**绑定到同一个 `MethodDef`** —— 这就是 §8.73
里「两行同一个 token」的机制。同时 `CreateContext` / `GetCachedContext` 拿到 `null`
（连自己的 token 都没有）。

**真正的可疑点**：`typeDef.Methods` 里只有 7 个方法 + `.ctor`，**既没有
`CreateContext`/`GetCachedContext`，也没有任何名字带 `.` 的显式实现**
（dnlib 的 `MethodDef.Name` 对显式实现是 `IMenuItem.Execute`）。
也就是说 `AttachMembers` 拿到的 `typeDef` 的 `Methods` 视图不完整/不是同一个类型 ——
`Attach`（`:88`）里 `knownTypes`/`types`/`bySimpleName` 的解析（`:106-126`）值得单独查：
`knownType` 是 dnSpy 传进来的 `TypeDef`，`typeDeclaration.Name` 对泛型类型
（`MenuItemBase`）与 `TypeDef.Name`（`MenuItemBase\`1`）对不上时会走全名回退。

**未修**：`MatchMethod` 的 `qualified` 判据（`method.Name.IndexOf('.')`）本身是对的，
但它在一个已经缺成员的 `Methods` 集合里找 —— 先修解析，再谈匹配。
**建议**：给 `Attach` 加一道断言（`typeDef.Methods.Count` 与源码声明数同量级），
或先给 token 注释加抽检门。

### 8.75 泛型/非泛型同名类型的 token 绑定错（2026-11-18，已修）

**根因**（§8.74 的后续）：源码 `Menus/MenuItemBase.cs` 同时声明了
`internal abstract class MenuItemBase` 和 `internal abstract class MenuItemBase<TContext>`。
`AstAnnotationBinder` 的解析按**不带 arity 的短名**查：
- `knownTypes[typeDeclaration.Name]`，而 `TypeDeclaration.Name` 是 `MenuItemBase`
  （arity 在 `TypeParameters` 里），元数据 `TypeDef.Name` 却是 `MenuItemBase\`1`；
- 回退路径 `GetFullTypeName` 又主动 `StripTypeArity`，把泛型名剥成 `MenuItemBase`，
  撞上同名的非泛型类型。

结果 `MenuItemBase<TContext>` 被绑到**非泛型的 `MenuItemBase`**（只有 8 个方法），
于是：`CreateContext`/`GetCachedContext` 拿不到 token；显式实现和公共方法
**同名 `Execute` 都匹配到同一个 `MethodDef`**（§8.73 的重复 token）。

**修法**（`Common/AstAnnotationBinder.cs`）：
1. `knownTypes` 查询键补 arity：`name + "`" + TypeParameters.Count`；
2. `GetFullTypeName` 输出**元数据拼写**（含 arity 与类型参数名，如
   `MenuItemBase\`1[TContext]`），这样 `BuildIndex` 的第一循环键能命中；
   剥离 arity 的条目退化为无同名兄弟时的回退。

**验证**：`MenuItemBase\`1` 的 token 从 `0x06000832`(重复) 变回
`0x0600083A / 0x0600083B / … / 0x0600083D`（与 2.3 逐个一致），
`diffLines` 146 → 144。
语料：`DnSpy.dll` 11937 → **11653**（−284，`identical` 968 → **971**）、
`Logic` 6774 → **6772**、`Debugger` 1338 → **1338**。`degraded=0`；
`verify-all` `hardFail=0`。

### 8.76 临时变量内联：又排除两条路径（2026-11-18）

在 §8.68 的基础上把能查的都查完，记录排除过程，避免重复劳动：

1. **`DeclareVariables` 没有「不声明/内联」分支**：`Analyze`（`:120` 起）无条件为每个
   `ILVariable` 调 `DeclareVariable`；`FindInsertionPoints` 只负责选插入点。
   所以「3.2 保留了临时变量」不是它主动的选择，而是**根本没走到内联**。
2. **`CopyPropagation` 的 `LdLoc` 分支要求 `IsSingleDefinition`**
   （`IL/Transforms/CopyPropagation.cs:92`）。`SpanDataCollection<TData>.Find` 里的
   `Span span` 在两个分支各赋值一次 → 不是单定义 → 不传播。
   （放松 `default:` 分支的实验已做，见 §8.67 负结果：语料零变化。）
3. 实测对照：2.3 在**同一个类型**里既声明 `spanData`（`GetStartIndex`）又内联
   `span`（`Find`）—— 说明 2.3 的判据是「按支配关系逐使用点传播」，而 3.2 的
   `CopyPropagation` 只做全变量单定义传播。

**结论**：要修就得实现**按支配关系的逐使用点复制传播**（per-use copy propagation），
这是 ILAst 层的实质改动，会影响所有多定义临时变量。**未修**，需要单独的回归方案
（现有门禁无法证明语义等价，尤其异常位置）。

### 8.77 循环塑形：`leave` 的 TargetContainer 既不是循环也不是它的父容器（2026-11-18）

§8.72 的进一步定位。给 `MatchLeave` 调用点插桩（打印对象哈希）：
```
[LOOP] leaveTarget=52409928 loop=52409928 loopParent=55896391   ← 第一个循环，相等，转换成功
[LOOP] leaveTarget=16630301 loop=10240431 loopParent=14867327   ← 第二个循环，三者互不相同
```
即失败那个循环的出口 `leave` 指向**第三个容器** —— 既不是循环本身，也不是循环的父容器。
所以这不是「放宽成祖先即可」的问题：那个 `leave` 的语义是退出另一个容器，直接拿来当
`while` 的出口会跳过该容器的其余部分。要修必须先弄清这个 ILAst 结构是怎么产生的
（`LoopDetection` 的 `exitTargetBlock` 计算 vs `DetectExitPoints` 的重写顺序）。

**未修**，且**不建议**在没弄清结构前放宽 `MatchLeave` —— 会得到语义错误的循环。

### 8.78 `HtmlClipboardFormatWriter` 也是「2.3 更差」（2026-11-18）

`lang-diff` 实测（`diffLines=117`，是「最大单类型」之一）：**2.3 的 switch 重建失败**，
C# 输出里漏出了 IL 标签：
```csharp
// 2.3（失败形态）                    // 3.2（正确 switch）
switch (c)                            switch (text[wsi])
{                                    {
    case '\t':                           case ' ':
        for (int j = 0; j < TabSize; j++)    this.sb.Append("&nbsp;");
            ...                              break;
        goto IL_00E2;                        ...
    case '\v':                           case '\u0085':
    case '\f':                           case '\u2028':
        goto IL_00EA;                        case '\u2029':
    case '\r':                               this.WriteBr();
        goto IL_00EA;                        break;
    default:                             ...
        if (c != ' ')                    case '\r':
            goto IL_00EA;                    this.WriteBr();
        this.sb.Append("&nbsp;");            break;
    ...
IL_00F0:                             }
i = wsi + 1 - index;
```
`goto IL_xxxx` 是 IL 标签泄进 C#，属 2.3 的**降级产物**。连同 §8.69 的清单，
**「最大单类型」里有相当一部分是 2.3 更差，不是 3.2 的账**。

**方法论**：判断残留归属必须逐类型看输出形态，不能只看 `diffLines` 大小 ——
`diffLines` 最大的类型往往正是 2.3 崩掉的地方。

**补充（同日）**：`goto IL_` 并不是普遍现象 —— 抽查 `SpanDataCollection\`1`(163)、
`SpanData\`1`(44)、`CachedTextColorsCollection`(62) 三个大类型，两边的 `goto IL_`
计数都是 0。所以 §8.78 是 `HtmlClipboardFormatWriter` 的个例，
**不要据此推断「大 diffLines 类型都是 2.3 更差」**；逐个看输出形态仍然必要。

### 8.79 token 抽检工具 `baseline/token-check.py`（2026-11-18）

§8.73/§8.74 建议加 token 抽检门，这是它的落地：
```
py baseline/token-check.py <dumpDir>
```
扫每个 dump 文件里的 `// Token: … RID: n`，报**同一类型内 RID 重复**。
正常输出不该有重复（属性的 `(get)`/`(set)` 注释用的是访问器自己的 RID，嵌套类型各占一个文件）。

**首次实跑**（`dnSpy.Contracts.DnSpy.dll`，1414 类型）：`types_with_duplicates=58`。
抽看一个：
```
// Token: 0x06001C75 RID: 7285   private bool MoveNext() { … }
// Token: 0x06001C75 RID: 7285   bool IEnumerator.MoveNext()   // 同一个 RID
```
且第二行下面紧跟着 `//ILSpy generated this explicit interface implementation from .override directive`。
全语料这种「ILSpy 由 .override 指令合成」的注释共 **35 处** —— 即 58 个重复里**至少 35 个是这条合成路径**（合成方法本身没有独立 metadata token，所以沿用了 `MoveNext` 的）。

**判定**：这条是**设计取舍**，不是 §8.74 那种绑定错 —— 但它是可查的：合成方法应该印 `// Token: 0x00000000 RID: 0`（或干脆不印 token），
而不是复用被实现方法的 token。**已定位，未修**（`TypeSystemAstBuilder` 的 `.override` 合成处）。
剩下 ~23 个重复类型需要逐个看是不是同一模式。

**用法**：改完 token 相关的东西跑一遍，`types_with_duplicates` 应当只下降不上升；
配合 `source-check.py`（成员不丢）与 `lang-diff-all`（兼容收敛）构成三个信号。

**后续实验（同日，已还原）**：真正的重复其实是**属性/方法这一层**，不是 `.override` 合成：
```
metadata: 'System.Collections.Generic.IEnumerator<dnSpy...IDsDocument>.Current'
          'System.Collections.IEnumerator.Current'
decl pit=IEnumerator<IDsDocument>  /  IEnumerator
```
`MatchProperty`/`MatchMethod` 只按 `".成员名"` 后缀匹配，两个接口成员（只差泛型/非泛型）
都被绑到第一个候选上 → token 重复。改成「接口简单名 + 是否泛型都要一致」后：
- token 重复 58 → **56**（`GetEnumerator` 的两处修好），属性 token 也变成 2.3 那样的 7288/7290；
- **但语料 `diffLines` 11653 → 11976（+323）**，因为 3.2 同时**丢了一条字段 token 注释**
  （`0x17000A37` 在 2.3 出现 1 次、3.2 变成 0 次）。

**结论：这个方向是对的（重复确实来自接口匹配），但改完会连带弄丢字段 token，净效果是远离 2.3。
已还原。** 要做得同时改 `MatchField`（`<Current>k__BackingField` 也有同样的泛型/非泛型碰撞），
并且用 `token-check.py` + `lang-diff-all` 双向验证 —— 单看 token 重复数会漏掉丢失。

### 8.80 接口成员匹配按「接口简单名 + 泛型性」区分（2026-11-18，已修）

§8.79 定位到 `MatchProperty`/`MatchMethod` 只按 `".成员名"` 后缀匹配。元数据里
`IEnumerator<IDsDocument>.Current` 与 `IEnumerator.Current`（以及
`IEnumerable<IDsDocument>.GetEnumerator` / `IEnumerable.GetEnumerator`）**都以后缀
`.Current` / `.GetEnumerator` 结尾**，于是两个声明都绑到第一个候选，token 重复。

**修法**：匹配时要求接口段一致 —— `GetSimpleName(privateImplementationType)` 与
`IsGeneric(privateImplementationType)`，再与元数据名里的接口段比对
（`InterfaceMatches`）。**一个必须注意的坑**：泛型实参里可能含 `.`
（`IEnumerator<dnSpy.Contracts.Documents.IDsDocument>`），所以找「最后一个 `.`」时
必须限定在 `'<'` 之前，否则会切到实参里去 —— 第一版就是这么错的，导致属性**两条 token
注释全丢**（语料 +323）。

**验证**：token 重复 58 → **56**；`DnSpy.dll` C# `diffLines` 11653 → **11625**（−28）；
`Logic` 6772、`Debugger` 1338 不变；`degraded=0`；`verify-all` `hardFail=0`。

**剩余 56 处**：抽查显示分两类 ——
① async 状态机（`__GetDirectories_d__13` 等）的 `MoveNext`（`.override` 合成的转发方法
与被实现方法同名，`wantsQualified` 下找不到带 `.` 的元数据名，回退到 plain 名 → 同 token）；
② `BookmarksService`（RID 6760/6762/6765 各两次）等**属性/事件**的多份 token，
需再单独查（可能是 backing field 的 token 归属）。

### 8.81 重载签名把 `T[]` 归一成 `T`，导致构造函数绑错（2026-11-18，已修）

`ExportBookmarkLocationFormatterProviderAttribute` 的两个构造函数印同一个 token：
```
// Token: 0x06001A49 RID: 6729   public Attribute(string type) { … }
// Token: 0x06001A49 RID: 6729   public Attribute(string[] types) { … }
```
根因：`NormalizeParameterTypeName` 里 `TrimEnd('&','*','(',')','[',']','?')` **把 `[]` 也剥掉**，
于是 `(string)` 与 `(string[])` 归一成同一个 "String"，`ParameterTypesMatch` 判定两者同签名，
重载解析退化，两个 `.ctor` 绑到同一个 `MethodDef`。

**修法**：先把 VB 的数组拼写 `()` 映射成 `[]`，再只 `TrimEnd('&','*','?')` ——
保留数组性，C#/VB 的数组拼写仍然统一。

**验证**：token 重复 56 → **49**；`DnSpy.dll` C# `diffLines` 11625 → **11215**（−410）、
`Debugger` 1338 → **1264**（−74）、`Logic` 6772 不变；`degraded=0`；`verify-all` `hardFail=0`。
