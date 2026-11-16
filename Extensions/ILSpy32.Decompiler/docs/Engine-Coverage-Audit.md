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
| ShowXmlDocumentation | 无条件启用（B\IL:212、B\Mixed:239） | 有能力、未做开关 |
| ShowTokenAndRvaComments | 无条件启用（B\IL:213、B\Mixed:242） | 同上 |
| ShowILBytes | 无条件启用（B\IL:216、B\IL:424-439、B\Common\MixedDisassemblyComments32.cs:93） | 同上 |
| ShowPdbInfo | 无条件启用（B\IL:216,431-433、B\Common\MixedDisassemblyComments32.cs:61） | 同上 |
| ShowILComments（opcode 文档） | 无 | **引擎能力缺口**：3.2 `ReflectionDisassembler` 无 `GetOpCodeDocumentation`（`ReflectionDisassembler.cs:38-63`） |
| SortMembers | 无 | **引擎能力缺口**：3.2 无成员排序 |
| MaxStringLength | 无 | **引擎能力缺口**：3.2 `DisassemblerHelpers` 无字符串截断 |
| HexadecimalNumbers | 无 | **引擎能力缺口**：3.2 反汇编数字格式化无 hex |
| —（3.2 新增） | ILAst 4 项：Run IL transforms / Show IL ranges / logic sugar / field sugar（B\ILAst\ILAstDecompilerSettings32.cs:94-106） | 2.3 无对应 |

补充：Mixed 的 C# 叠加层使用独立 `new DecompilerSettings32()`（B\Mixed:53,313），**不读持久化的 C# 单例**——2.3 Mixed 读的是持久化 `CSharpVBDecompilerSettings`（A\Mixed:42,102）。

### 8.3 注册面差异（M4 移除 2.3 的影响）

> ✅ **P0 已修（2026-11-16）**：① 3.2 扩展已补 6 个 `ContentTypeDefinition` 导出（`B\ContentTypeDefinitions.cs`，名称与父链同 2.3）；② 已补 `ISimpleILPrinter` 导出（`B\IL\SimpleILPrinter32.cs`，与 2.3 逐字一致）。下文保留原始影响分析作为记录。

- **内容类型 100% 依赖 2.3**：3.2 扩展**无 `ContentTypeDefinition` 导出**（`B\ContentTypeDefinitions.cs` 仅常量；全扩展只有 3 个 `[Export]`：`CSharp\DecompilerCreator.cs:30` + `Settings\DecompilerSettings32Impl.cs:31,86`）。2.3 注册 6 个（`dnSpy.Decompiler.ILSpy\ContentTypeDefinitions.cs:28,33,39,45,51,56`）。→ **M4 后 3.2 五语言的 ContentTypeString 悬空**（语法高亮 / 文本视图设置失效）。
- **`ISimpleILPrinter` 唯一导出在 2.3**（`dnSpy.Decompiler.ILSpy\IL\SimpleILPrinter.cs:29`）→ `Extensions\dnSpy.AsmEditor\MethodBody\BodyUtils.cs:45` 回退 `DummyPrinter`（:47 输出 "Missing ISimpleILPrinter"）。3.2 无导出。
- **设置页**：2.3 有 `IAppSettingsPageProvider` 两页（C#/IL，`Settings\DecompilerSettingsPageProvider.cs:26-40`）；3.2 扩展零设置页，且 IL/ILAst/Mixed 的 `Settings` 均为语言内 `new`（B\IL:52、B\ILAst:56、B\Mixed:52-53）→ **未持久化、无 UI**。
- **设置迁移**：`dnSpy\dnSpy\Decompiler\DecompilerServiceSettings.cs:79-96` 只映射 2.3 C#/VB 的 UniqueGuid；2.3 IL 选项段 `DD6752B1` 无迁移方；2.3 段 `6745457F`/`DD6752B1` 会变孤儿（无清理逻辑）。
- **2.3 引擎子模块不可删**：`Extensions\dnSpy.BamlDecompiler\dnSpy.BamlDecompiler.csproj:27` 与 `Extensions\ILSpy32.Decompiler\ICSharpCode.NRefactory.VB32\ICSharpCode.NRefactory.VB32.csproj:24-31` 引用 2.3 的 `ICSharpCode.Decompiler` / NRefactory 源。

### 8.4 消费面（已核）

- `Extensions\dnSpy.Analyzer\TreeNodes\AnalyzerTreeNodeData.cs:117` 已含 3.2 C# UniqueGuid；2.3 分支在 M4 后成死代码（非缺陷）。
- Debugger 侧（`DbgDotNetLanguageServiceImpl`、`DbgMethodDebugInfoProvider` 等）走族 GUID + `IDecompilerLanguageSettings.LanguageGuid`，语言无关。
- `dnSpy\dnSpy.Console\Program.cs:228` 硬编码 `"dnSpy.Decompiler.ILSpy.Core"`；M4 需改为只加载 3.2。

### 8.5 结论（按建议优先级）

- **P0（已修）**：① ✅ 3.2 扩展补 6 个 `ContentTypeDefinition` 导出（`ContentTypeDefinitions.cs`，五语言改用常量）；② ✅ 补 `ISimpleILPrinter` 导出（`IL/SimpleILPrinter32.cs`）+ `Common/DnlibILText32.cs`（2.3 dnlib→IL 写入器移植）+ `Common/ILDecompilerUtils32.cs`；对拍 **8565 样本 diffSamples=0**（两侧各 1,543,426 字符逐字相同）。
- **P1（对齐）**：③ ✅ 三语言补 `WriteToolTip` / `TypeToString`（`Common/DnlibILText32` 写入器 + `Common/ILDecompilerUtils32`）；④ ✅ ILAst 的 `Decompile(TypeDef)` 已补 `// Type:`/`// Base type:`/嵌套类型/字段/属性/事件（属性/事件用 2.3 短式）；⑤ IL/ILAst/Mixed 的 2.3 装饰做成开关（至少 `Options`，理想加设置页）。
- **构建管线（新发现，已修）**：`dotnet build dnSpy.sln -c Release -f net10.0-windows` 会把 **Debug** 版 3.2 引擎 DLL 放进 dnSpy bin（引擎不在 sln 内，MSBuild 对引用外用默认配置）——已实测：sln 构建后 `ILSpy32.Decompiler.dll` 为 1,832,960 字节（Debug），单独构建扩展后为 1,585,152（Release）。已在 `build.ps1` 的 `Build-Net` 里显式以 Release 重建引擎与扩展（两条分支都加）；把引擎加入 sln 的替代方案与 `-f` 冲突（NETSDK1005，引擎是 netstandard2.0-only），已回退。
- **P2（引擎能力缺口 / 清理）**：⑥ 评估在 3.2 引擎补 ShowILComments / SortMembers / MaxStringLength / HexadecimalNumbers 的 public API；⑦ Mixed 的 C# 叠加改用持久化 C# 设置；⑧ 2.3 设置段清理。
