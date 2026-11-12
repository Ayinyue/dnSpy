# 3.2 引擎架构原理与缺陷根因全析

> 本文是对整个 2.3→3.2 迁移的系统性总结：先讲清 3.2 引擎扩展的**工作原理**（数据流全图），
> 再把迁移过程中发现的所有缺陷按**根因分类学**归档。配合 `Engine-Coverage-Audit.md`（逐项接线
> 清单）与 `baseline/README.md`（工具用法）阅读。

## 一、架构原理：数据流全图

### 1.1 总体管线

```
dnSpy GUI（树节点点击）
    │  TypeDef / MethodDef / FieldDef / ...（dnlib 对象）
    ▼
IDecompiler.Decompile(...)          ← CSharpDecompiler32 / VBDecompiler32
    │
    ├─① DnlibCecilBridge.TryGetModule(module)
    │      dnlib ModuleDef ──(内存快照写入，PreserveRids)──► Mono.Cecil ModuleDefinition
    │      缓存：ConditionalWeakTable<ModuleDef, Entry>（随 dnlib 模块生命周期回收）
    │      解析器：FailSoftAssemblyResolver（三 tier：精确匹配 → dnlib 全局解析器 → GAC 探测）
    │
    ├─② GetOrCreateEngine(cecilModule, settings)
    │      CSharpDecompiler（ILSpy 3.2 引擎本体，ILSpy32.Decompiler.dll）
    │      缓存键 = 模块 + 设置（C#/VB 共用引擎池但设置不同，防 VB 兼容设置污染 C#）
    │      引擎设置：DecompilerSettings32.EngineSettings（TupleTypes/StringInterpolation 关）
    │
    ├─③ decompile(engine)               ← 策略委托（DecompileTypes / Decompile(member) / ...）
    │      产出 C# SyntaxTree（带 Mono.Cecil 引用注解，无 dnlib 注解）
    │      fallback 判定：无 TypeDeclaration 且无 NamespaceDeclaration → 成员列表重反编译
    │
    ├─④ 后处理（顺序敏感）
    │      AstAnnotationBinder.Attach(module, tree, knownType)
    │          把 dnlib 定义挂到 C# AST 节点（knownType 直绑优先，简单名回退兜底）
    │          同时收集 VB 大小写重命名字典 + 方法名集合（VB AddressOf 判定用）
    │      CSharpDecompiler32.InsertTokenComments(tree, module, ShowTokenAndRvaComments)
    │          'Token: 0x... RID: ...' 注释（C# 直插注释节点；VB 由转换器转成引言）
    │      ReorderMembers(tree, module)                ← SortMembers 开关时
    │          按 2.3 分组序（Methods→Properties→Events→Fields→NestedTypes）+ MemberComparer
    │
    ├─⑤a C# 渲染
    │      DnSpyTokenWriter（ILocatable：行/列跟踪 + positionMap 行列→偏移表）
    │      ├─ WriteIdentifier：注解解析链
    │      │    MemberResolveResult → TypeResolveResult → IType 注解
    │      │    → invocation 探测（CallBuilder 把方法 RR 挂在 InvocationExpression 上）
    │      │    → GetSymbol → 同类型成员（FindEnclosingTypeMember）
    │      │    → TypeRef 表按名（IsTypeNameIdentifier）
    │      │    → 句法 fallback 色（SyntacticColor / MethodColorByName）
    │      ├─ WriteKeyword：基元类型关键字带 corlib 引用
    │      └─ MethodSpans：方法级调试 span（v1 fallback 形状）
    │      渲染后：engine.CreateSequencePoints(tree)
    │          IL.SequencePoint（Offset/EndOffset 排他）↔ dnSpy ILSpan 1:1
    │          → CompactILSpans(bodySize)：排序+首条拉回 0+填洞+末条扩尾（覆盖率 100%）
    │          → 调试 locals：ILVariable.Index（IL slot）→ dnlib Local → SourceLocal
    │
    ├─⑤b VB 渲染
    │      CSharpToVBConverter32（C# AST → NRefactory VB AST，125 个 visitor）
    │      ├─ C# 节点的 ILInstruction 注解随 CopyAnnotations 拷到 VB 节点
    │      ├─ ResolveIdentifierAnnotations / VisitMemberReferenceExpression：
    │      │    与 C# token writer 相同的解析链（decisive 注解首置）
    │      └─ ILSpyEnvironmentProvider32：Cecil 注解直读 + methodNames 字典回退（AddressOf）
    │      VBDecompiler32.CollectPendingLocals：C# 树 ILFunction 祖先链 → 变量表 → SourceLocal
    │      VBTextOutputFormatter32：
    │      ├─ DebugStart/DebugExpression/DebugEnd（2.3 形状）：语句级 IL↔text 映射
    │      │    空 Nodes 时子树扫描（foreach 头部指令）；声明节点跳过（防抢 IL）
    │      └─ CompactILSpans（同 C#）
    │
    └─⑥ output.AddDebugInfo(MethodDebugInfo)   ← CalculateILSpans 门控
           消费端：DbgMethodDebugInfoProvider（调试映射）、MethodDebugService（书签/定位）
           查找 API：GetSourceStatementByCodeOffset / GetSourceStatementByTextOffset
```

### 1.2 关键设计决策及原理

| 决策 | 原理 |
|---|---|
| **内存快照而非文件解析** | cecil 对混淆程序集的解析弱于 dnlib；让 dnlib 先归一化，cecil 只读正常化的字节 |
| **PreserveRids** | dnlib 写入器默认重排 TypeDef 表（嵌套类型后置），会令 MDToken 错位——引擎按 token 寻址 |
| **引擎按「模块+设置」缓存** | C#/VB 共享引擎池；VB 兼容设置（关 QueryExpressions 等）不得污染 C# 输出 |
| **注解桥而非类型系统移植** | 3.2 AST 节点带 Mono.Cecil 引用注解；dnlib 侧通过 token 验证 + 按名映射回接 |
| **knownType 直绑** | 嵌套类型/歧义简名场景下简单名回退不可靠；dnSpy 调用方天然持有确切 TypeDef |
| **CalculateILSpans 门控** | 语句级收集有成本；仅 DocViewer/调试映射需要，导出/Console 跳过 |
| **SortMembers 扩展侧实现** | 3.2 引擎无成员排序 transform；在扩展层重排（分组序+MemberComparer），不碰引擎 |
| **CompactILSpans** | 语句 SP 之间有洞（分支/spill/prolog）；2.3 的 SP 全覆盖，这里补齐等价语义 |

### 1.3 双引擎并存的隔离原理

- **程序集名不同**：2.3 = `dnSpy.Decompiler.ILSpy.Core`（引擎 `ICSharpCode.Decompiler.dll`），
  3.2 = `dnSpy.Decompiler.ILSpy32.x`（引擎 `ILSpy32.Decompiler.dll`）——无类型身份冲突，可同进程对拍
- **GUID 隔离**：UniqueGuid 各自独立（`8C1F4A62...`/`9C5E4C7A...`），GenericGuid 共享族
  （`F5A318D4...`/`B6849618...`）——语言下拉成组、族级判断对两代自动生效
- **OrderUI 隔离**：3.2 = 2.3 + 0.5（列表 2.3 在前；族 GUID 多命中时确定性取最新）

## 二、缺陷根因分类学

迁移期间发现并修复的全部缺陷，按根因归为六类。每类给出机理、代表案例与修复模式——
**这个分类学本身就是"逐文件深入分析机制"的产出**：后续遇到新缺陷可先归类再定位。

### 类别 A：移植遗漏（2.3 有、3.2 移植时丢失的语义）

| 缺陷 | 机理 | 修复 |
|---|---|---|
| `using System;` 缺失 | 2.3 的 IntroduceUsingDeclarations 有 `// always import System`，3.2 移植丢弃 | 补回 ImportedNamespaces.Add("System")（不入 VerifiedNamespaces，让 superset 检查自然过滤 fallback 树） |
| using 输出逆序 | 2.3 注释"we go backwards (OrderByDescending)"——升序输出靠降序遍历+同点插入；3.2 误用升序 | 改 OrderByDescending |
| CallBuilder cast 双挂树 | 上游 if/else 互斥（cast 分支 vs 普通 mre 分支），移植成顺序执行 → target.Expression 第二次 AddChild 抛 "Node is already used in another tree" → **全部状态机类型反编译失败**（416 处 Iterator.Dispose 形态的真身） | 恢复互斥 else |
| attribute 顺序 | 2.3 表属性在前、flags 伪属性（ComImport 等）在后；3.2 CecilLoader 把伪属性加在前面 | AddAttributes 重排：表属性→security→伪属性 |
| SortMembers 开关 | 2.3 有选项（默认 false），3.2 无实现 | 扩展侧 ReorderMembers（分组序+MemberComparer）+ 选项暴露 |

**模式**：这类缺陷的共同特征是 *2.3 的输出是正确的、3.2 的输出是错的*，且机理在 2.3 源码里
有注释或结构可对照。**修复策略 = 拿 2.3 源码做 ground truth 对照移植**。

### 类别 B：桥接语义差异（dnlib 与 cecil 的世界观差异）

| 缺陷 | 机理 | 修复 |
|---|---|---|
| 跨模块 token 错位 | cecil 引用别的程序集时带那个程序集的 token，在本模块 ResolveToken 命中无关定义 | token 验证（SameIdentity：种类+名字+声明类型）+ 外部引用按名映射 |
| 嵌套类型名格式 | cecil 用 `/` 分隔嵌套（`Ns.Outer/Inner`），引擎 FullTypeName 只认 `+` | TypeNameHelper.ToReflectionName |
| 外部引用 Scope 是 ModuleDefinition | 桥接把外部引用解析到已加载模块，Scope 名带 `.dll` 后缀而非裸程序集名 | ScopeAssemblyName 归一化（Assembly 名优先，退化剥 `.dll`） |
| GetCecil 对嵌套编译器生成类型失败 | 桥接的 lookup 覆盖不全 | dnlib 按名回退（NestedTypes 名字+IsCompilerGenerated 查找） |

**模式**：这类缺陷的特征是 *两套元数据 API 对同一概念有不同表示*。**修复策略 = 在桥接层
归一化，不让下游感知差异**。

### 类别 C：引擎注解拓扑假设错误（下游对 AST 注解位置的假设与 CallBuilder 实际行为不符）

| 缺陷 | 机理 | 修复 |
|---|---|---|
| 方法调用无引用（ref-no-symbol） | CallBuilder 把方法解析结果挂在外层 InvocationExpression（CSharpInvocationResolveResult），MemberReferenceExpression 是裸的 | token writer/转换器从 invocation 探测（带"节点已有自身 RR 不抢"防护） |
| VB 扩展方法检测 NRE | `invocExpr.Annotation<dnlib.IMethod>()` 假设存在 dnlib 方法注解，实际是 null（引擎只挂 CSharpInvocationResolveResult） | 空保护 + invocation RR 探测 |
| foreach 头部无 SP | VB32 visitor 不对 foreach 调 DebugExpression，头部指令无映射 | DebugEnd 空 Nodes 时子树扫描（内层先到先得保护） |
| 方法签名级 DebugEnd 抢 IL | 声明节点的 StartNode/DebugEnd 对在方法体渲染前触发 | 跳过 MemberDeclaration/TypeDeclaration/Accessor |

**模式**：这类缺陷的特征是 *注解在 AST 上的位置与直觉不符*。**修复策略 = 探测上游真实挂载点
（instrument + sink 打印注解类型序列），不要凭直觉假设**。

### 类别 D：计数/判定被 using 声明污染（MemberRole 语义陷阱）

| 缺陷 | 机理 | 修复 |
|---|---|---|
| namespace 包裹丢失 | always-System 修复后，空类型树的 Members.Count 从 0 变 1（using 占位），绕过 fallback 判定 → 真正的 fallback 从不执行 → 全部类型丢 namespace | fallback 判定改为检查 OfType<TypeDeclaration/NamespaceDeclaration>（实质内容），不用裸 Members.Count |

**模式**：SyntaxTree.Members = MemberRole 的所有子节点（using 也在内）。**任何以
Members.Count 为空判定的代码都要防 using 占位**。修复一个缺陷时必须审视它对所有
依赖同一判定的下游的影响——本次就是 A 类修复（补 always-System）触发了 D 类连锁。

### 类别 E：引擎对现代编译器形态的覆盖缺口（ILSpy 3.2 上游本身的限制）

| 缺陷 | 机理 | 修复 |
|---|---|---|
| 现代 Roslyn async 布局拒绝 | C# 5.0+ 编译器的 builder 不拷局部、catch 块内插清理存储——上游 MatchTaskCreationPattern 只认经典布局 | 引擎子模块补丁：双布局分支 + setResult/catch 位置无关扫描 |
| 语句 SP 覆盖率 34-95% | 语句间指令（分支/spill/prolog）无映射；2.3 为 98-100% | CompactILSpans（Contracts 层，C#/VB 共用）：排序+拉回 0+填洞+扩尾 |

**模式**：这类缺陷的特征是 *上游引擎的能力边界*，不是移植错误。**修复策略 = 引擎子模块
工作区补丁（按约定不入库），保持与上游 diff 最小**。

### 类别 F：测试/工具基础设施缺陷（不影响生产输出，影响验证能力）

| 缺陷 | 机理 | 修复 |
|---|---|---|
| 上游测试套无法构建 | net46 不兼容 netstandard2.0；Immutable 版本降级；NUnit3TestAdapter 3.9 自带旧版 Mono.Cecil 与引擎 cecil 类型身份冲突 | net462 + Immutable 1.7.1 + NUnit 3.13.1/adapter 4.6.0 |
| 上游测试 snippet 反编译失败 | vstest host 下模块引用解析不到框架程序集 | Tester.SnippetResolver（参考程序集+运行时目录） |
| VB 单成员 knownType 缺失 | DecompileMember 未传声明类型，简单名歧义回退可能失败 | C#/VB 双端传入 (member as IMemberDef)?.DeclaringType |
| debug info 白跑 | 3.2 无条件计算语句级收集，2.3 只在 CalculateILSpans 时 | C#/VB 双端门控 |
| 门禁基线随修复移位 | 嵌套类型独立输出/namespace 恢复改变编译噪音分布 | run-regression.cmd 基线+注记（历史演进留痕） |

**模式**：这类缺陷的特征是 *验证链路本身的问题*。**修复策略 = 先修工具再修引擎，否则
无法证明引擎修复有效**。

## 三、根因分类学的使用方法

1. **新缺陷出现时先归类**：输出错 → A（对照 2.3 源码）；引用/解析错 → B 或 C（看是元数据
   差异还是注解拓扑）；覆盖率/映射错 → E；构建/测试错 → F；修复某缺陷后出现新缺陷 → D
   （连锁效应，查所有共用判定的下游）。
2. **定位手段按类别选择**：A 类直接 diff 2.3 源码；B/C 类用 probe 的 sink 机制
   （ColorPathSink/MAPPER sink/DNSPY_ILSPY32_DIAG）打印实际运行时状态；E 类用 state-diag
   直方图；D 类用 IUD-CHILDREN/FALLBACK 追踪。
3. **每修一个缺陷必须双门禁 + engine-tests**：文本缺陷看门禁数字（syntax/realBad），
   行为缺陷看 engine-tests 断言。

## 四、遗留已知局限（均定性，见审计文档第 1/7 节）

| 局限 | 类别 | 说明 |
|---|---|---|
| VB 属性访问器/构造器的方法级 span | C | 非 MethodDeclaration 节点，2.3 集成版同形 |
| 外部属性/事件引用不合成 | 设计取舍 | 站点已被句法 fallback 正确着色；合成需 property/event 色 |
| 调试 locals 的 hoistedField 聚合 | E | 两引擎集成版均不产（yield/async 重建未跑），行为一致 |
| 3 项选项传导链路正确但引擎不响应 | E | AlwaysGenerateExceptionVariable/UseDebugSymbols/ObjectOrCollectionInitializers |
| MemberOrder/十六进制/字符串长度上限选项 | 设计取舍 | 3.2 引擎无等价能力，避免死开关 |
| 4 项 VB 不可表达构造 | 语言限制 | goto case 外部、unsafe、fixed 字段、多行注释内部处理 |
| 显式接口属性 getter 单语句 TextSpan 0/1 | 边缘 | IL 侧仍 100% |

## 五、验证资产清单（全部可复跑）

| 资产 | 命令 | 覆盖 |
|---|---|---|
| C# 门禁 | `baseline\run-regression-cs.cmd` | dnSpy.dll/AsmEditor/Contracts 三样本 vs 2.3 实时对拍（syntax+realBad） |
| VB 门禁 | `baseline\run-regression.cmd` | 同上 VB 后端（基线 1731/741/11） |
| 上游测试套 | `dotnet vstest ICSharpCode.Decompiler.Tests.exe` | 564 测试（407 过/142 败定性/15 跳） |
| engine-tests | `m3probe engine-tests <样本> <dnspyBin>` | 18 项回归断言 |
| dbg-map / dbg-map2 | `m3probe dbg-map2 <目标> <bin> <引擎> <过滤> <数量>` | 消费端 API 覆盖率（IL+Text） |
| options-wiring | `m3probe options-wiring <目标> <bin> <过滤>` | 19 项选项翻转响应 |
| spdump | `m3probe spdump <目标> <bin> <过滤> [vb]` | 语句级 SP + locals 明细 |
| state-diag | `m3probe state-diag <目标> <N>` | 状态机拒绝点直方图 |
| colordump / text | `m3probe colordump/text ...` | 双引擎逐 token / 全文本对拍 |
| settings-tests | `m3probe settings-tests` | 2.3 选项值迁移 |
