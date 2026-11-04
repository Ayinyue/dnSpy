# baseline — 双引擎输出基线对拍

> **接手请先看**：`docs/VB-Action-Plan.md`（行动纲要、当前基线数字、下一步候选、交接 SOP）。

用于 Phase 2「正确性基线」：对同一程序集、同一批类型，分别用现有 2.3 引擎与新的
3.2 引擎反编译，再逐类型比对输出。

## 为什么需要它（重要）

3.2 的两个反编译器都会**在内部吞掉异常并写一行注释**：

```
' ILSpy 3.2 VB decompilation failed: <ExceptionType>: <message>
' ILSpy 3.2 VB conversion failed: <ExceptionType>: <message>
// ILSpy 3.2 decompilation failed: <ExceptionType>: <message>
```

也就是说，`Decompile()` 不抛异常 **不等于** 输出了正确代码。此前 vbverify 的
`ok=1278 failed=0` 只证明「没有异常逃逸」，无法发现这类降级输出。本工具把
`degraded` 单独统计，避免指标失真。

## 用法

```bash
cd Extensions/ILSpy32.Decompiler/baseline

# 1) 转储：--impl core = 2.3 引擎，--impl ilspy32 = 3.2 引擎
dotnet run -c Release -- dump \
    --asm ../../../dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.Contracts.Logic.dll \
    --impl core --lang vb --out out/core-vb-contracts

dotnet run -c Release -- dump \
    --asm ../../../dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.Contracts.Logic.dll \
    --impl ilspy32 --lang vb --out out/v32-vb-contracts

# 2) 对拍
dotnet run -c Release -- compare --a out/core-vb-contracts --b out/v32-vb-contracts \
    --out out/report-vb-contracts.md
```

# 语法验证（Roslyn 解析，只查语法，不做语义/引用解析）
dotnet run -c Release -- validate --dir out/v32-vb-contracts --lang vb \n    --out out/syntax-vb-contracts.md

# 语义编译（带引用）
dotnet run -c Release -- compile --dir out/v32-vb-contracts --lang vb \
    --refs ../../../dnSpy/dnSpy/bin/Release/net10.0-windows \
    --out out/compile-vb-contracts.md

# 单个类型的全部诊断（排查用）
dotnet run -c Release -- validate --dir out/v32-vb-asm --lang vb \n    --file "dnSpy.AsmEditor.Compiler.ModulePatcher"
```

选项：

| 选项 | 说明 |
|---|---|
| `--lang cs\|vb` | 选择语言 |
| `--all` | 含 internal / 编译器生成类型（默认只取 public 非 enum/interface）|
| `--max N` | 只处理前 N 个类型（冒烟）|
| `--dnspy-dir <dir>` | 显式指定 dnSpy 输出目录（默认取 `--asm` 所在目录）|
| `validate --dir <dir>` | 对某次 dump 的结果做语法验证 |
| `validate --file <name>` | 只验证一个类型并打印全部诊断 |
| `compile --refs <dir>[;<dir>…]` | 语义编译（引用目录可用 `;` 分隔多个），可访问性错误单列为 noise |
| `compile --type <name>` | 只对名字匹配的类型做语义编译，并逐条打印诊断（单类型排查用）|
| `--type <name>` | dump 只处理名字匹配的类型（调试单个类型用）|

输出：每个类型一个 `.txt`、`summary.tsv`（类型 / 状态 / 长度 / 文件 / 原因）、
`_meta.txt`（引擎与程序集信息）；`compare` 生成 markdown 报告。

### 状态定义

| 状态 | 含义 |
|---|---|
| `ok` | 无异常，且输出中没有失败注释 |
| `degraded` | 未抛异常，但输出是失败注释（**这是最有价值的指标**）|
| `fail` | `Decompile()` 抛出异常 |

## 已验证结果（2026-10-31，本机实测）

| 程序集 | 语言 | 2.3 引擎 | 3.2 修复前 | 3.2 修复后 |
|---|---|---|---|---|
| dnSpy.Contracts.Logic.dll（84 public） | VB | 84 ok | 38 ok / 46 degraded | **84 ok / 0 degraded** |
| dnSpy.Contracts.Logic.dll（84 public） | C# | 84 ok | 44 ok / 40 degraded | **84 ok / 0 degraded** |
| dnSpy.AsmEditor.x.dll（1278 all） | VB | — | 659 ok / 619 degraded | **1278 ok / 0 degraded** |
| dnSpy.AsmEditor.x.dll（1278 all） | C# | — | — | **1278 ok / 0 degraded** |

> 注意：修复前的数字才是「真实正确率」，`vbverify` 此前报的 1278/1278 是异常计数。

## 本次基线暴露并修复的两个缺陷

### 1. 桥接的元数据 token 错位（影响两语言，严重）

`DnlibCecilBridge` 用 `module.Write(stream)` 序列化 dnlib 模块后交给 cecil。
dnlib 默认写入会**重排 TypeDef 表**（嵌套类型被排到顶层类型之后），而扩展随后用
`LookupToken(dnlib.MDToken.Raw)` 找回 cecil 对象——token 全部错位。

实测证据（修复前）：

```
dnlib token 0x0200001E = dnSpy.Contracts.Decompiler.AsyncMethodDebugInfo
cecil  0x0200001E      = dnSpy.Contracts.Utilities.NuGetPackageLocator/NuGetPackageInfo
```

后果：顶层类型引用了嵌套类型时反编译失败（`Could not find type definition ...`）；
更危险的是**方法级反编译会静默取到错误的方法**（不报错，输出貌似合理）。

修复：写入时加 `MetadataFlags.PreserveRids`（无法保留时回退普通写入）。

### 2. 嵌套类型名分隔符不匹配

扩展把 cecil 的 `TypeDefinition.FullName`（嵌套用 `/`）直接传给
`new FullTypeName(string)`，而该构造函数只按反射风格 `+` 拆分。结果整个
`Outer/Inner` 被当成顶层名，`DecompileType` 查不到 → 抛 `Could not find type definition`。

修复：`Common.TypeNameHelper.ToReflectionName()` 统一把 `/` 换成 `+`（C# / VB 两条路径都用）。

## 后续又修复的两处 VB 缺口

### 3. `UnaryOperatorType` 未映射（173 例）

VB AST 没有空传播节点，无法表达 C# 的 `?.`。改成让引擎产出兼容形式：VB 的
`DecompilerSettings32(vbCompatibility: true)` 关闭 `EngineSettings.NullPropagation`，
引擎输出 `a != null ? a.b : null`，转换器已有的条件表达式路径即可处理。

由于 C# 与 VB 共用引擎缓存，缓存键改为「模块 + 设置」（
`CSharpDecompiler32.GetOrCreateEngine`），否则 VB 的设置会污染 C# 的输出。

### 4. `Node is already used in another tree`（63 例）

`VisitPropertyDeclaration` 为自动属性初始化器创建声明符时执行了
`v.Type = decl.ReturnType`，把已经挂在属性节点上的同一个 `AstType` 再挂给声明符，
违反 NRefactory 的单父约束。输出器在该声明符父节点为属性时本来就忽略 `Type`
（只写 `= 初始化值`），所以直接删除该赋值即可。

### 5. out-var 声明（90 例）

`M(out var x)` 在 VB 无对应语法。转换器现在把 `Dim x As T` 提升到当前语句之前，
参数位置改用 `x`（VB 按参数签名自动按引用传递，不需要 out/ref 关键字）。
ILSpy 3.2 写出的是**显式类型**而非 `var`，因此无需类型推断。

## 剩余缺口（VB，AsmEditor 1278 类型，43 例）

| 次数 | 原因 |
|---|---|
| 43 | `NotImplementedException`（查询表达式 / `goto case` / 指针相关等）|

C# 侧在当前样本上已无降级。

## 已知正确性局限（不体现为失败）

`ok` 只表示「产出了真实反编译文本」，**不代表语义一定正确**。

### 已修：表达式位置的赋值

```csharp
value = (dictionary[key] = new List<TypeDef>());   // C# 链式赋值
```
```vb
dictionary([key]) = New List(Of TypeDef)()
value = (dictionary([key]))                        ' 同一个实例
```

转换器把内层赋值提升为语句，并返回**左值的克隆**。返回右值会重复求值
（`New` 会创建两个对象），所以必须重读左值。

以下位置**刻意不提升**（保持原行为），因为提升会改变语义或作用域：

- 语句位置，含无花括号的 `if (c) x = y;`——提升会让赋值变成无条件执行
- 循环条件 / 迭代器（`while ((line = Read()) != null)`）——提升后只执行一次
- lambda 体内——会提升到外层方法
- 复合赋值（`x = (y += 1)`）与 `return` 位置——值不是右值，暂未处理

这些位置在当前样本（AsmEditor 1278 类型）中未出现；若后续样本命中，需要各自单独处理。

## 语法验证（2026-10-31 新增）

`validate` 用 Roslyn 对每个 `ok` 输出做**纯语法解析**。它与 `ok` 是互补的两个指标：
`ok` 只说明「产出了真实反编译文本」，`validate` 才能发现文本本身不合法。

首次运行（修复前）：AsmEditor VB **342 / 1278 个文件有语法错误（27.7%）**。

它一次性暴露了四类此前完全不可见的问题：

1. **属性命名参数用了 C# 语法**（`.Header = "..."`）。VB 要求 `Name := value` 且不能有
   前导点；错误写法让解析器跌进 XML 字面量模式，一个错误可级联出上百条诊断。
   修复：`VisitNamedExpression` 在父节点是 `CS.Attribute` 时改产 `NamedArgumentExpression`，
   同时补上 VB 输出器里 `VisitNamedArgumentExpression` 的桩实现。
2. **编译器生成名含 `<` `>`**（`<get_ModifiedObjects>d__15`）。方括号转义无效
   （`[<x>]` 仍非法，已用 VB 编译器实测），改为把非法字符替换成 `_`。
3. **`_` 不能作标识符**（`[_]` 同样非法，VB 视其为续行符），重命名为 `__`。
4. **`Char` 漏在关键字表外**，导致枚举成员/属性名未加方括号。

5. **语句以 `New` 开头**（`New T().M()`）——VB 会报语法错误，必须加 `Call`。
   为此在 VB AST 中新增了 `CallStatement` 节点（接口 + 两个输出器 + 转换器）。
6. **P/Invoke 方法被输出成无方法体的 `Function`**。根因是 `HasAttribute` 依赖
   MemberReference 注解，而反编译产物没有该注解 → 永远检测不到 `DllImport`，
   走不到已有的 `ExternalMethodDeclaration` 分支。改为回退到语法名后正确输出 `Declare ... Lib`。

7. **`++`/`--` 被输出成不存在的辅助函数**（`__PostIncrement(x)` 等）。VB 没有自增运算符：
   语句位置输出 `x += 1`；表达式位置前缀形式提升更新后重读目标，后缀形式先用
   `Dim __tmp_N = x` 保存旧值（输出器因此支持省略 `As` 的声明）；循环条件内保持原样，
   因为提升会改变执行次数。
8. **表达式体属性丢值**（`uint Start => start;`）。值在 `PropertyDeclaration.ExpressionBody`，
   而访问器体是空的 `return;`，既丢值又是 Get 内的非法语句。
9. **同一方法里重复/嵌套的局部变量名**（`BC30288` / `BC30616`）。VB 不允许同一块内重名，
   也不允许内层遮蔽外层，而 C# 两者都允许。转换器按 VB 块维护作用域表 + 成员内已用名集合，
   冲突时改名，并沿同一张表解析引用（声明与用途同步）。三个坑：`VisitForStatement` 必须先访问
   initializer（否则改名晚于引用）、判冲突要不区分大小写（`rVA`/`rva`）、生成名要避开
   ILSpy 已经用过的名字（`array`/`array2`）。`out var _` 因此变成 `_discard1`/`_discard2`。

10. **循环条件里的自增/自减**（`while (--i >= 0)`）。VB 没有赋值表达式，原先输出成对不存在的
    `__Decrement` 的调用。改为下沉到循环体：
    `While True : i -= 1 : If Not(cond) Then Exit While : … : End While`。
    只下沉「沿左操作数可达」的自增（右侧可能被短路跳过）；否定时必须加括号，
    否则 `Not a AndAlso b` 只否定 a。
11. **事件被当委托调用**（C# 的 `PropertyChanged(this, e)`）。VB 需要 `RaiseEvent`，
    为此给 VB AST 新增 `RaiseEventStatement`（与 `CallStatement` 同一个补丁）。
    `var h = Event; if (h != null) h(...)` 这种副本模式会连声明和守卫一起消掉：
    VB 无法读取事件，而 `RaiseEvent` 自带空检查。
12. **`Module` 的构造函数带 `Shared`**（`BC30433`）、**`TupleElementNames` 元数据**（`BC37269`）。
    静态构造函数与方法一样要先剔除 `Static`；`TupleElementNames` 与 `Nullable*` 同属
    C# 编译器元数据，VB 无法显式应用。

13. **`Char` 与数值互转**（`BC32006` / `BC32007`）。VB 的 `CType` 没有这种转换：
    `CChar(n)` 非法、`Dim n As UShort = c` 也非法，必须 `ChrW` / `AscW`。
    为此补全了类型推断的三个分支：二元运算（`char` 提升为 `int`）、`If(c, a, b)`、
    以及**括号表达式**——`(char)(val + 48)` 的操作数被包在 `ParenthesizedExpression` 里，
    不解包就永远判不出源类型。
14. **`as` 用在值类型上**（`BC30792`）。`TryCast` 只接受引用类型，`Boolean?` 走 `DirectCast`
    （即 CLR 的拆箱）。注意与 `as` 的差别：类型不符时 `DirectCast` 抛异常而非返回 `Nothing`。
15. **抽象事件**（`BC30243`）。VB 没有抽象事件，去掉 `MustOverride`，退化为字段式事件。

16. **元组**（类型与表达式）。VB AST 原本没有对应节点，导致 `dnSpy.dll` 有 18 个类型直接
    抛异常降级。新增 `TupleExpression` / `TupleType` / `TupleTypeElement`（元素名可选），
    输出 `(a, b)` 与 `(x As Integer, y As String)`。
17. **`new T(AddressOf M)` 无条件简写成 `AddressOf M`**。只对**委托类型**成立，
    `New Lazy(Of T)(AddressOf ...)` 被削掉了构造函数（`BC30581`）。现在先解析类型，
    确认派生自 `MulticastDelegate` 才省略。
18. **`New` 关键字判定区分大小写**，导致名为 `new` 的局部变量没被方括号转义（`BC30180`）；
    **属性初始化写在 `Implements` 之后**（`BC30205`，VB 要求 `Property X As T = v Implements I.X`）。
19. **事件订阅写成 `+=`**。C# 的 `x.Event += handler` 在 VB 里必须写成
    `AddHandler x.Event, AddressOf handler`（否则报「事件不能被直接调用」，`BC32022`）。
    事件查找要沿着**基类型与基接口**走——`ITextView.Closed` 这类事件声明在继承来的接口上。

20. **成员级 `Implements` 子句没限定命名空间**（`BC30002`）。类级子句写
    `Implements Global.dnSpy.Contracts.X.IFoo`，成员级却写成 `Implements IFoo.Id`——
    而输出文件通常没有 `Imports`，裸名解析不到。`CreateType` 现在按元数据重建
    `Global.<ns>.<name>`。细节：元数据名带 arity（``IEquatable`1``），直接拼进标识符会被
    转义成 `IEquatable_1`（`BC30149`），必须先剥掉。
21. **同一个接口在 `Interfaces` 列表里出现两次**，于是每个成员都拿到两条相同子句
    （`Implements I.Foo, I.Foo` → `BC30583`）。按 (接口, 成员**签名**) 去重。
    ⚠️ 按 (接口, 成员**名**) 去重会连合法重载一起吞掉，而报错（`BC30149`）出现在
    另一个方法上，很难联想到是去重造成的。
22. **泛型实例名参与类型判定**（`BC30452`）。`Classify("ReadOnlyCollection<string>")`
    查不到类型（没有剥泛型实参）→ 判不出引用比较 → 输出 `=`，而 VB 对引用类型要求 `Is`。
    现在同时吃 `Foo<T>`、``Foo`1<T>``、`Foo(Of T)` 三种拼写，查找时补上 arity。
23. **只有两侧都已知才判引用比较**，一侧解析失败（`MetroWindow` 来自未提供的程序集）
    就漏判。改为「任一侧已知是引用类型即可」——另一侧若是已知值类型则否决。
24. **注解绑定回退导致接口子句错位**。`MatchMethod` 在同名同参数个数但参数类型不符时
    会返回第一个候选，而 `CreateImplementsClausesForMethod` 直接用了它的签名。
    现在用 `ParametersMatch` 复核；`ref/out` 参数要剥掉尾部 `&` 再比，否则
    `ref object` 永远比不上，反而把正确的那条子句丢掉（`BC30149`）。

25. **泛型类型完全没有注解**（影响面最大的一处）。`AstAnnotationBinder` 用元数据全名匹配
    语法树里的类型名，而泛型类型的元数据名带 arity（``BulkObservableCollection`1``），
    语法树不带 → **整个类型绑定失败**，所有依赖注解的功能（接口子句、事件副本抑制、
    修饰符判定）对它静默失效。修法：给类型字典补一份剥掉 arity 的键（两遍填充，
    避免遮蔽同名的非泛型类型）。

26. **C# 自定义事件在 VB 里必须有 `RaiseEvent` 访问器**（`BC31132` / `BC30149`）。
    编译器生成的字段式事件（add/remove 都标 `[CompilerGenerated]`）没有 raise 访问器，
    而 VB 的 `Custom Event` 三件套缺一不可。判定为「自动事件」时改写成字段式
    `Public Event X As T`——add/remove 里那段 `Interlocked.CompareExchange` 循环正是
    字段事件自带的行为，语义等价。改写后原先的事件副本也能被消掉，直接输出 `RaiseEvent`。

27. **`UTF8String + String`**（`BC30452`）。C# 会把「隐式转换到 String」折进 `+`，
    VB 不会把用户定义转换和内置运算符串起来。新增 `HasImplicitStringConversion`
    （在元数据里找返回 `String` 的 `op_Implicit`），只在一侧确实是文本时给另一侧插 `CStr`。
    ⚠️ `CStr` 也是 VB 关键字：用 `InvocationExpression` 调用会被转义成 `[CStr](...)`，
    必须用 `CastType.CStr` 的 `CastExpression` 节点。
    顺带修了成员类型查找：`MemberTypeOf` 原来只查**声明**的成员，而 `IAssembly.Name`
    声明在基接口上，查不到就推不出操作数类型。

28. **接口实现缺 `Optional`/默认值**（`BC30401` / `BC30149`）。C# 的默认值只写在接口声明上，
    实现可以省略；VB 要求实现把 `Optional` 和默认值重复一遍。现在从**接口元数据**读取
    `[opt]` 标记与常量，补到实现的参数上。默认值必须照抄接口的那个常量——一律写 `Nothing`
    会让「省略参数」的调用悄悄改变行为（例如枚举默认值 `None` vs 0 之外的取值）。

29. **单独 dump 的嵌套类型绑不上注解**。一个嵌套类型被单独写成一个文件时，树里没有外层
    类型声明，`GetFullTypeName` 只能算出短名，于是整类没有 `TypeDef` 注解——所有依赖注解的
    功能对它静默失效。现在全名匹配不到时按**唯一简单名**兜底（同名多处时不猜）。

30. **`long.MinValue` 的字面量溢出**（`BC30036`）。C# 对 `-9223372036854775808L` 有特殊规则
    （一元负号 + 十进制字面量 `9223372036854775808` 恰好合法），VB 没有：VB 先解析字面量再做
    一元负号，而十进制字面量不允许回绕，`9223372036854775808` 超出 Long → Overflow。
    3.2 引擎的 C# 输出把该常量写成裸字面量，转换器原样传递。修法：`VisitPrimitiveExpression`
    特判 `long.MinValue`，发出 `Long.MinValue` 成员引用——2.3 引擎的 VB 输出正是这个形式。
    十六进制回绕形式（`&H8000000000000000L`）也可行，但成员引用与数字格式化选项无冲突。
    注意 `9223372036854775808UL`（ULong 的 2^63）是**合法**的，不需要处理。

31. **属性/索引器/自定义事件的访问器没有成员状态**（Batch 1 主根因，`BC30452` 92→37）。
    `ResetMethodState`（清 `variableTypes`、设 `CurrentType`）只在 ctor/dtor/method/operator
    调用——访问器体带着**上一个方法残留的状态**或全空状态运行；而 setter 的 `value` 参数又是
    在访问器体转换完之后才手工合成的，`variableTypes` 里根本没有。于是
    `fontFamilyVM_2 <> value` 这类访问器内的引用比较两侧都推不出类型，回落 `=`/`<>`。
    修法：三个声明访问器开头也调 `ResetMethodState`（`CurrentType` 从访问器节点上的
    `MethodDef` 注解解析），并在转换访问器体**之前**注册 `value` 的类型。索引器的索引参数
    一并提前（getter 体可以引用索引参数）。

32. **方法调用结果的类型推断**（Batch 1，resolver 扩展）。`GetTypeName` 原来不处理
    `InvocationExpression`/`BaseReferenceExpression`/一元运算/`as`，`svc.Value.Create(...) == x`
    的右侧永远未知。新增：方法返回类型查找（沿基类/接口 BFS），委托字段/局部调用解析为
    `Invoke` 的返回类型，`MyBase` 解析到基类型；泛型定义成员的类型（`T`）按构造名的实参
    **按位置替换**（`Lazy<T>.Value` → `T` 换成实参类型）。

33. **C# 字符串 `==` 被发成 `Is`（预存语义缺陷，本轮发现即修）**。C# 的 `s1 == s2` 绑定
    `String.op_Equality`（值语义），VB 对应运算符是 `=`；`Is` 是引用比较——对非驻留字符串
    行为不同。旧逻辑只看「是引用类型」就发 `Is`，`If e.PropertyName Is "HasDecompiled"`
    就是这么来的。修法：任一侧是已知字符串且另一侧不是已知 `object` 时否决引用比较
    （`object == literal` 在 C# 里确实是引用语义，`Is` 保留）。字符串是引用类型所以从不
    报错，这类缺陷语法/编译门禁都测不到，只有语义走查能发现。

34. **显式接口实现与同签名成员绑定混淆**（Batch 2 主根因，`BC30401` 108→49）。绑定器按
    「名字+元数」匹配元数据成员，遇到「显式接口实现 + 私有辅助方法」（两者同名同参数、仅返回
    类型不同，如 `AssemblyResolver` 的 `Resolve`）会把**同一个 MethodDef 绑给两个语法节点**，
    于是私有辅助方法也拿到 Implements 子句（VB 校验签名失败 → BC30401）。属性同理：override
    属性与显式接口实现共享短名（`UIObject`），两者都声称实现接口成员。修法：显式实现节点
    （`PrivateImplementationType` 已设置）优先匹配限定名（`IFoo.Bar`，同时容忍 .override 挂在
    普通名方法上的形态），普通节点永不匹配限定名；候选分级为「全签名 → 参数 → 名字」，新增
    **返回类型比较**做决胜（`Nullable<T>` 按 `T` 归一，`!!T` 泛型参数放行）。
35. **Optional 复制落到错误的重载**（Batch 2）。`ApplyOptionalParameters` 按名字+元数找接口
    成员抄 Optional/默认值，接口有同名重载时会把 `Load#2` 的 Optional 抄给 `Load#1` 的参数
    （`DocumentListLoader.Load` 的 `loadDocuments` 莫名变成 `Optional = Nothing`）——实现签名
    被改坏，直接 BC30401。修法：抄写前比较**全部参数类型**（末段归一后），类型不齐就跳过。
36. **名字近似比较必须过同一个归一化**（Batch 2 教训，回归 round 5 的 +34 由此而来）。VB 拼写
    `Integer`/`UInteger`/`Long`… 对元数据 `Int32`/`UInt32`/`Int64`…；`T?` 对 `Nullable\`1`；
    `Byte()` 对 `Byte[]`——三组拼写差异各造成一轮门禁误杀（子句丢失或 Optional 不复制 →
    BC30149 156→190）。归一化收敛三件事：VB 关键字映射表、`Nullable`/数组/指针/ByRef 修饰
    剥离（两侧都剥）、`(Of T)` 与 `<T>` 同切。**任何「比较两边名字」的新代码都必须走同一个
    `NormalizeParameterTypeName`**，不要手写最后一截。
37. **已知限制：`.override` 在属性访问器上时**（`DocumentViewer.get_DocumentTab`），引擎生成
    「转发函数」声称实现接口**属性**成员，VB 不接受方法实现属性成员（BC30401 残留）。正确输出
    是把转发函数变换成 `Private ReadOnly Property X ... Implements I.X`，属独立改动，留待后续。

38. **VB 大小写不敏感：局部/参数名吞掉类型名**（Batch 3 主根因，`BC30456` 229→215）。
    C# 里参数 `searchLocation` 与类型 `SearchLocation` 靠大小写区分，VB 靠不区分——
    `SearchLocation.SelectedType` 被解析到 String 参数上（`'SelectedType' is not a member
    of 'String'`），`Dim guid As String` 吞掉 `Guid.TryParse` 同理。修法：成员体里按
    `SimpleType`/`MemberType` 收集被引用类型的简单名，冲突的**方法头参数**改名（`guid2`）
    并保留原名→新名的引用映射；局部沿用既有 `DeclareLocal` 冲突机制（加入同一集合）。
    两个边界：**lambda 参数不改名也不注册**——并行 lambda 复用同名参数是合法的，而改名映射
    是成员级的，表达不了每个 lambda 的作用域（round 6 曾让 361 个文件报 `'a' is not
    declared`）；**改名参数要在 `variableTypes` 双注册**（原名 + 新名）——CS 树里的标识符
    仍是原拼写，事件订阅判定（`IsEventAccess`→`GetTypeName`）靠原名直查（round 6b 的
    `+=` 回退即此）。
39. **已知缺口：构造器/运算符没有 `MethodDef` 注解**——绑定器只走 `MethodDeclaration`，
    `ResolveCurrentType` 对 ctor 返回 null，ctor 体里的成员类型查找（`MemberType`）静默失效；
    之前被「参数原名直查」掩盖（上一条的双注册就是为了不依赖它）。后续可让绑定器给
    `ConstructorDeclaration`/`OperatorDeclaration` 也绑定定义。
40. **BC30456 其余形态定性**（229→215 后剩 215，噪声 12）：大头是 `'ImageReference' is not
    a member of 'BookmarkInfo'` 这类**成员真的没生成**（属性/字段未输出，属转换缺陷，待后续
    batch）与**互操作类型成员跨 dump 文件**（度量口径，`--whole` 下消失）。本轮未再修。

41. **ctor/dtor/operator/访问器没有 `MethodDef` 注解**（Batch 4 主根因，dnSpy.dll
    `BC32022` 266→66）。第 39 条入档的缺口正是主犯：绑定器只给 `MethodDeclaration` 绑
    `MethodDef`，于是 ctor/dtor/operator 体内 `CurrentType=null`，`GetTypeName(MyBase/字段/
    属性/裸事件名)` 全部返回 null，`IsEventAccess`（CSharpToVBConverter32.cs:1699）在
    :1706 提前 false → `x.Evt += h` 留成 `+=`（265/266 个残留文件在 ctor/dtor/访问器上下文）。
    访问器虽然在 Batch 1 接了 `ResetMethodState`（第 31 条），但它读取的访问器注解从来没人绑
    ——恒空转。事件副本抑制走同一条类型判定链，抑制失效时输出 `Dim x As T = Me.Evt`，而
    VB 连**读**字段式事件都不允许，每个未抑制副本必报 BC32022（实测 98 处副本在 Set/Get
    访问器、4 处在方法）。修法：三类声明各配匹配器——ctor 按「.ctor/.cctor + 参数类型 +
    是否静态」（同元数不同参数时保留首个候选，与 MatchMethod 同口径）、dtor 匹配唯一的
    virtual 无参 `Finalize` 覆写（非虚 Finalize 助手不得冒领）、operator 按 `op_*` 运算符
    类型 + 参数 + 返回类型；访问器的 `MethodDef` 从已匹配的 PropertyDef/EventDef 的
    Get/Set/Add/Remove 方法直接带下来（访问器定义就挂在成员定义上，无需二次匹配）。
    为什么与原 IL 等价：本条只补**注解**（类型判定的输入），不改任何输出结构；判定翻真后
    走的是第 19 条早已验证的 AddHandler/RemoveHandler 路径，而 AddHandler 与 C# 的
    `event +=` 编译成同一个 `Event::add_X` 调用。

42. **跨模块基链上的事件查找把「未知」当「否定」**（Batch 4 次根因）。事件声明在跨程序集的
    基类/基接口上时（`WinSysButton` 的 `MyBase.Loaded`，基链 TabButton→ButtonBase(外部)；
    `CultureSettingsImpl` → 外部 ViewModelBase 的 PropertyChanged），即使补了第 41 条注解仍
    输出 `+=`。根因：`HasEventIn` 沿基类型/接口 BFS 用 `ResolveTypeDef()` 只能走**同模块**，
    走到外部基类型即断链，而断链被当成「遍历完成且没找到」**确定地返回 false**——把
    `IsEventAccess` :1707 的 `?? true`（查不到时乐观按事件处理）兜底堵死了。修法：遍历因
    外部基类型/接口提前止步时返回 `null`（未知），只有完整走完且没找到才 `false`；顺带修了
    泛型实例基类（`Base<T>`）的解链——从 `GenericInstSig` 取出泛型定义再解析。
    为什么与原 IL 等价：`?? true` 只是把「证明它不是事件」改为「走得到就信」；C# 源码既然
    写了 `+=`，IL 就是事件 add 调用，按事件处理不引入新语义。
    **余量（未修）**：目标类型本身解析不到时 `GetTypeName` 直接 null（在 :1706 更早分支
    false）：`documentTabService.Value.DocumentCollectionChanged`（`System.Lazy`1` 链，
    dnSpy.dll 余 66 处）、`CommandManager.RequerySuggested`（WindowsBase 静态类，AsmEditor
    余 25 处）——cecil `GetType` 不跟随 type forward、WindowsBase 又不在 bin 目录。本批改动
    只会把 `+=` 转 AddHandler（false→null→true 单向），不会新增此形态；AsmEditor 本批前
    的逐诊断分布未存档（out/ 逐轮覆盖），总数 554→527 为对账依据。待后续 batch。

43. **Custom Event 缺 `RaiseEvent`：三种可确定形态合成，两种按策略保留报错**（dnSpy.dll
    `BC31132` 81→43，AsmEditor 余 12）。扫描全部 dump 的 Custom Event 块，缺 RaiseEvent 的
    共五形态：
    - **E1 空 add/remove**（显式接口事件 `add{} remove{}`，38 处）：合成**空** RaiseEvent 体
      ——事件没有存储，引发本来就无事发生；
    - **E4-stub 双 Throw**（add/remove 各一个 Throw，20 处）：合成同一个 Throw；
    - **E4d 私有委托字段支撑**（add=`[Delegate].Combine` / remove=`[Delegate].Remove`，字段
      确认存在且类型与事件委托类型一致）：合成 copy+guard 引发——与类内自己手工引发用的
      正是同一形态（HexBufferImpl 的 bufferSpanInvalidated_2）；
    - **E2 列表型**（`list.Add/Remove(value)`，约 40 处，背后是 WeakEventList，引发走列表
      内部 API）与 **E4c/E4e 转发型**（add 转发到另一对象的事件，52 处）：**不合成**。
      VB 自定义事件没有隐式 backing 字段，这两种的引发语义无法确定表达，乱合成会静默丢掉
      引发——宁可保持显式编译错误（既定策略，见 Dev-Plan 待确认项 3 的答案）。
    为什么与原 IL 等价：C# 字段式事件的引发就是「读 backing 字段→非空调用 Invoke」，E4d
    的类内手工 raise 段正是 copy+guard，合成访问器只是把同一段语义摆到 VB 要求的位置；
    E1 的 IL 里根本没有 raise 代码，空体即「无操作」。配套细节：RaiseEvent 访问器必须重复
    委托 `Invoke` 的签名——委托先在本模块解析，框架委托（`EventHandler<T>`）走 cecil 桥，
    委托自己的类型参数（`TEventArgs`）按事件声明处的实参替换，替换不掉就不合成。

44. **显式接口事件的元数据名带接口限定**（Batch 4 顺带）。`void IFoo.Bar { add … remove … }`
    的 EventDef 在元数据里叫 ``Foo.Bar``，`MatchEvent` 只按短名精确匹配 → 不绑 → 没有
    EventDef → 无 RaiseEvent 合成、无 Implements 子句。修法与方法侧第 34 条的限定名规则
    同构：精确名优先、限定后缀（`.Bar` 结尾）兜底、按「是否显式实现」选择取向；字段式事件
    无显式接口语法，只按短名。

45. **`Type.GetType("System.Nullable")` 解析到错误运行时类型**（Batch 4 顺带，`BC32128`
    1→0）。`GetRuntimeType` 把编译器别名 `System.Nullable` 原样交给 `Type.GetType`，解析到
    一个 `IsValueType=false` 的特殊运行时类型，而 `Nullable<T>` 本体是 struct——误判成引用
    类型让可空值比较发成 `Is/IsNot`。修法：别名重映射为 ``System.Nullable`1`` 再查
    （ExpressionTypeResolver.cs:621）。

46. **重复实现子句与「仅返回类型不同」的成员对**（Batch 5 主根因，`BC30583`/`BC30521`
    C+B 族）。两个独立缺陷、一处叠加：
    - **BC30583**：`MatchViaCecil` 的两条候选路径（dnlib 解析不到接口时的 cecil 兜底与泛型
      定义兜底）按「名字+参数个数+参数名」匹配、不比较返回类型。接口继承链上派生接口以不同
      返回类型重定义同名成员时（`IWpfTextView.GetTextViewLineContainingBufferPosition`
      隐藏 `ITextView` 的同签名成员；或 `IEnumerable(Of T).GetEnumerator` 与
      `IEnumerable.GetEnumerator`），类里每个同签名方法都会把两个接口成员都认领成自己的
      Implements 子句。修法：yield 前加**返回类型一致**过滤——两侧都是元数据拼写
      （dnlib `MethodSig.RetType` vs cecil `ReturnType`），比较「泛型定义全名」：保留
      arity（``IEnumerator`1`` ≠ `IEnumerator`）、剥掉泛型实参（``IEnumerator`1<T>`` 与
      ``IEnumerator`1<Foo>`` 相等，因为子句点的是接口而不是实参）；任一侧是泛型参数、
      多维数组、指针、modreq/modopt 时保持候选（只加确定事实）。
      **与第 36 条红线的偏差说明**：此处刻意不用 `NormalizeParameterTypeName` 做比较器——
      它剥 arity，会让非泛型 `IEnumerable.GetEnumerator` 冒领泛型孪生成员（GetEnumerator 族
      33/45 文件全靠这条）；同函数既有的参数名比较（:`3261`-`3265`）仍走旧口径，与第 36 条
      针对的「VB 拼写 vs 元数据拼写」场景不同——本条两侧都是元数据拼写，没有归一化的必要。
    - **BC30521（C/B 族）**：上面两族修好后，同一类里还剩两个「名字+参数相同、仅返回类型
      （或 ByRef/ByVal，BC30345）不同」的成员——VB 根本不允许（BC30301；补 Overloads 无效，
      勘察已实测证伪），调用点全部歧义。修法：**改名 C# 显式接口实现那一侧**（VB 惯用法
      `GetEnumerator1`）：C# 语法上显式接口实现永远不能以简单名调用，类内所有非限定调用点
      都绑定另一个成员，因此改名零调用点重写、语义严格等价（接口分发经 Implements 子句，
      子句跟着成员节点走）；跨文件同样安全——其它类型对显式实现的引用只能经接口成员。
      任务书原述「改不带 Implements 子句的一侧+重写调用点」按同一确定性依据落实为改
      「显式实现侧」：GetEnumerator 族两侧都带子句、WpfCommandService 族只有显式实现带，
      只有这个判据对两族都成立且不需要调用点重写（300 型切片实测除目标类型外全部逐字节
      不变）。判定在 `VisitTypeDeclaration` 预扫描：同名（VB 大小写不敏感）+同类型参数数+
      同参数类型（保 arity 的结构比较，剥泛型实参）成组，组内返回类型拼写出现≥2 个不同值
      （BC30301）或 ByRef/ByVal 模式不同（BC30345）才动手；组内无显式实现不动（C# 不允许
      这种成员对没有显式实现，出现即保守跳过）；全显式组保留第一个。**改名等价性**：IL
      可见方法名变了（`GetEnumerator`→`GetEnumerator1`），但接口映射经 Implements 子句保持，
      输出与原 IL 的成员名不再逐一对应；反射字符串（`GetType().GetMethod("…")`）按原 IL
      限定名书写时不再绑定——本语料未出现该形态。
    - **BC30521（A 族）**：`[DefaultValue(null)]` 直译成 `DefaultValue(Nothing)`，无类型
      Nothing 落到 `DefaultValueAttribute` 全部 14 个基元类型构造器上且无最具体候选。
      修法：特性实参的 null 字面量发成 `DirectCast(Nothing, Object)`（`VisitAttribute`），
      C# null 的 MemberValue 同为 null，语义不变；全语料扫描 null 特性实参仅
      `DefaultValue(Nothing)` 一族（dnSpy.dll 18 处/14 文件，AsmEditor/Contracts 0 处）。
    - **本条余量（未修，计数不变）**：属性对的子句归属仍由绑定器决定——
      `MatchProperty`（AstAnnotationBinder.cs:350）没有返回类型决胜（`MatchMethod` 有，
      第 34 条），显式实现属性对（`ITextView.TextViewLines` / `IWpfTextView.TextViewLines`，
      返回 `ITextViewLineCollection` / `IWpfTextViewLineCollection`）两个语法节点都绑到
      第一个限定名 PropertyDef，`IWpfTextView.TextViewLines` 无人认领（`HexTextView`
      BC30149+BC30401，改前后各 2 条，本批未回升）。后续 batch 给 MatchProperty 加
      `ReturnTypeMatches` 决胜即可。BC31035/BC30149 在 `HexToolTipInfoCollection` 一族是
      Friend 可见性（BC30389，判噪声）级联出的真错误，whole 口径消失，属度量口径。
      `BC30345` 余 3 个文件（`GetWordSpan`：两个 `Shared` 成员仅 ByRef/ByVal 不同，组内无
      显式接口实现，按上述判据保守跳过）。
      D 族（`.override` 挂在属性访问器上的转发函数）按第 37 条既定计划单列，本批不动——
      实测余量：`BC30521` 11 个文件（dnSpy 10 = `DocumentViewer.get_DocumentTab`、
      AsmEditor 1 = `HexBufferService.GetOrCreate`，均为「两个完全相同候选」的转发对）。

47. **静态事件订阅与自定义事件访问器参数**（打磨轮，realBadFiles 962→942）。两个独立根因：
    ①C# 静态事件订阅 `MetroWindow.MetroWindowCreated += h` 的接收者是**类型名**，`GetTypeName`
    对 `TypeReferenceExpression` 无分支 → 判不出是事件 → 发成 `+=`（VB 报 BC30456/BC30451，
    whole 模式 88 条）。修法：`IsEventAccess` 增加类型名目标分支，走 `HasEvent(类型名, 成员)`。
    ②C# 自定义事件 add/remove 访问器的 `value` 参数是**隐式**的，VB 必须写在访问器头
    （`AddHandler(value As EventHandler(Of T))`，BC31133 81→0），否则体内 `value` 被 VB 解析到
    无关的 `Value` 结构体（BC31419 52→7，余 7 为 internal 跨文件口径）。修法：转换访问器体前
    注册 `value` 类型，转换后给两个访问器合成参数节点（与 setter 的 `value` 合成同一模式）。
    与原 IL 等价：AddHandler/remove 与 C# 的 add/remove 语义一致；合成参数只是把隐式参数显式化，
    不改变访问器行为。

48. **`.override` 挂在属性访问器上的转发函数改为属性形态**（README 37 的收口，打磨轮）。
    引擎对「类自身方法直接 `.override` 到接口属性 getter」的形态会额外生成一个注释标记的转发
    函数（`get_DocumentTab`），旧输出让**函数**声称实现接口**属性**成员（VB 拒绝，BC30401）。
    现在识别该转发函数（body 含引擎的 ".override directive" 注释且名为 get_*），改发
    `Private ReadOnly Property <Iface>_<Prop> As T Implements I.<Prop>`——合成名避免与真实访问器
    方法及继承成员撞名。判定不经 dnlib 的 ResolveMethodDef/ResolveTypeDef（对外部接口返回
    null，既有 GetMethods 路径 1 因此静默失效），改走 Cecil 桥确认「目标是接口的属性 getter」。
    setter-only 转发器保留方法形态（其返回类型是 void，不是属性类型）。与原 IL 等价：接口映射
    不变，只是把引擎的 C# 转发技巧翻译成 VB 的显式属性实现。
    效果：BC30401 49→15（余 12 文件全为噪声）、BC30521 11→8。

49. **已知限制（打磨轮定性，不修）**。三组余量按「语义不成立时宁可保留显式报错」的既定哲学入档：
    ①`BC30345` 3 文件——C# 合法的 `(SnapshotPoint, out WordKind)` / `(SnapshotPoint, WordKind)`
    重载对，VB 的 ByRef/ByVal **不构成**重载差异（BC30345 直接拒绝）。可表达形式是改私有重载名
    并重绑定调用点，但绑定需要调用点级重载裁决，风险大于 3 个文件的收益。
    ②`BC30521` 8 文件——`Show(Nothing)` 单实参歧义：C# 的 `null` 不能隐式转值类型（Guid），
    只剩 `Show(Window)` 一个候选；**VB 的 `Nothing` 可以转任何类型的默认值**，`Show(Window)`
    与 `Show(Guid, [owner])` 同时适用且无法比出更具体者。可表达形式是按元数据参数类型给裸
    `Nothing` 实参插 `CType(Nothing, Window)`——需要方法组枚举与实参配对，暂缓。
    ③`BC30456` 余量主体（含 `BookmarkInfo.ImageReference` 一族）经 `--whole` 定点实证为 **internal
    跨文件类型的逐文件口径问题**，不是转换缺陷；「真缺成员」子类待后续批次再定性。
    ⚠️ 但 `--whole` 只能做**定点**实证：其聚合统计被嵌套类型重复声明支配（每个嵌套类型在
    自身文件与外层文件各声明一次，BC30179 ×10 万、BC30663 ×21 万、级联无数）——聚合数不可
    当缺陷度量用，只有「单簇 whole 编译干净」这类定点对比才算证据。

修复后的语法错误文件数：

| 程序集 | 语言 | 语法错误文件 |
|---|---|---|
| AsmEditor（1278） | VB | **0** |
| dnSpy.dll（3266） | VB | **0** |
| Contracts.Logic（84） | VB | **0** |

### 剩余已知形态（未修）

- `New T(...).Method()` 单独成句（VB 需要 `Call`）。dnSpy.dll 的 BC30035 主要来自这里。
- 无方法体的 P/Invoke / abstract 声明缺少 `End Function`（应改写成 `Declare` 形式）。

这些都需要各自的转换策略，且当前已不影响「是否产出代码」。

## 语义编译验证（2026-10-31 新增）

`compile` 用 `--refs` 目录里的程序集 + 运行时程序集做真正的语义编译。
**注意**：反编译出的类型与它引用的内部成员不在同一程序集，可访问性错误属于
测量假阳性，报告里单列为 noise（`CS0122` / `BC30390` 等）。同一文件里通过**类型限定**
访问内部成员（`MethodRefComparer.CompareNoName`）也属此类：类型来自"被 dump 的那个程序集"，
其中没有 internal 成员——用 `--whole` 全量编译可验证这些错误会消失。

Contracts.Logic（84 个 public 类型）的首次结果：**54 个文件编译失败**，真实问题包括：

| 次数 | 诊断 | 说明 |
|---|---|---|
| ~~18~~ **0** | ~~`BC30451 '__PostIncrement' is not declared`~~ | 已修：语句位置 `x += 1`，表达式位置用临时变量 |
| ~~12~~ **0** | ~~`BC30654 'Return' statement in a Function must return a value`~~ | 已修：表达式体属性从 `ExpressionBody` 重建 |
| 18 | `BC30258 Classes can inherit only from other classes` | `Inherits` 目标不是类型 |
| 15 | `BC30662 Attribute 'NullableAttribute' cannot be applied` | 特性目标不匹配 |
| 15 | `BC30456 'Contracts' is not a member of 'dnSpy.Contracts.DnSpy'` | 命名空间被同名类型遮蔽，需要 `Global.` |
| 12 | `BC30002 Type '_GetCustomAttributes_d__9' is not defined` | 重命名后的类型引用未全部一致 |
| 11 | `BC31409 'Equals' must be declared 'Overloads'` | 缺少 Overloads |

### 后续修复（累计）

| 诊断 | 起点 | 现状 |
|---|---|---|
| `BC30451 __PostIncrement` | 18 | **0** |
| `BC30654 Return 缺值` | 12 | **0** |
| `BC31409 缺 Overloads` | 11 | **0**（注解绑定）|
| `BC30258 接口当基类` | 18 | **0** |
| `BC30149 泛型接口子句` | 19 | **0** |
| `BC30662 Nullable 特性` | 15 | **0** |
| `BC30456 Global. 遮蔽` | 15 | **0** |
| `BC30260` / `BC31429` 大小写重名 | 18 | **0** |
| `BC30288` 同块重名 | 4 | **0** |
| `BC30616` 嵌套遮蔽（AsmEditor）| 9 | **0** |
| `BC32022` 事件当委托调用（AsmEditor）| 36 | **25**（余量为 `CommandManager.RequerySuggested` 静态外部事件，第 42 条余量）|
| `BC30433` Module 构造函数 `Shared` | 4 | **0** |
| `BC37269` TupleElementNames | 2 | **0** |
| `BC32006/BC32007` Char 与数值互转 | 10 | **0** |
| `BC30792` `as` 用于值类型 | 2 | **0** |
| `BC30243` 抽象事件 | 1 | **0** |
| `BC30581` 委托构造误简写 | 1 | **0** |
| `BC30180` 关键字大小写（dnSpy.dll）| 1 | **0** |
| `BC30205` 属性初始化顺序（dnSpy.dll）| 1 | **0** |
| 元组表达式/类型（dnSpy.dll 降级）| 18 | **0** |
| `BC30452` 泛型实例的引用比较 | 201 | **92**（余下两侧类型都推不出的情况）|
| `BC30583` 重复的接口子句 | 168 | **0**（Batch 5 返回类型过滤，第 46 条；原 45 中 34 个文件全为噪声）|
| `BC30521` 重载歧义（同名仅返回类型/ByRef 不同）| 108 | **11**（余为转发函数重复发出，第 37 条）|
| `BC30149` 成员子句未限定命名空间 | 3956（whole）| **0** |
| `BC30452` `UTF8String + String` | 116 | **0** |
| `BC30452` Char 与数值比较 | 112 | **0** |
| 泛型类型的注解绑定（整类静默失效）| — | 已修，见第 25 条 |
| `BC32022` 事件当委托调用（dnSpy.dll）| 379 | **66**（Batch 4 后；余量为 `Lazy`1` 链式类型解析失败，第 42 条余量）|
| `BC31132` 自定义事件缺 `RaiseEvent` | 92 | **43**（dnSpy；余下列表型/转发型按策略保留，见第 43 条；AsmEditor 余 12）|
| `BC30149` 接口实现缺 `Optional` | 255 | **156** |
| `BC30401` 同上/签名不符 | 108 | **109**（子句都发出来了，反而多暴露 12 处真不符）|
| `BC30002` 带元组实参的接口子句 | 675 | **669** |

Contracts.Logic 的真实失败文件 54 → **10**；AsmEditor 906 → 896 → 825 → 583 → 565 → 565 → 554 → **527** → **525**（Batch 5）；
dnSpy.dll（3266 类型）2039 → 1408 → 1120 → 1096 → 1088 → 1068 → 1050 → 1030 → **989** → **962**（Batch 5）。

### 带元组实参的接口（`IEqualityComparer(Of (Code, String, CpuidFeature()))`）

`DnlibCecilBridge` 无法把这种 TypeSpec 转成 cecil，`MatchViaCecil` 什么也拿不到，成员子句整条
消失（`BC30149`）。第一次尝试按「泛型定义兜底 + 用元数据补限定元组元素」时，单类型是修好了
（`MnemonicComparer` 43 → 35），但全局 `BC30002` 从 675 涨到 750——根因是 `CreateType` 的兜底
分支会把原始元数据名（``IEqualityComparer`1<...>``）当类型名输出。那次已回退。

真正的修法是**不再借道 cecil 生成子句类型**：`CreateTypeFromSig` 直接walk dnlib 的 `TypeSig`
（`GenericInstSig` / `ArraySigBase` / `GenericSig` / `TypeDefOrRefSig`），按元数据拼
`Global.<ns>.<name>`（每段剥掉 arity）并递归处理实参。于是

```vb
Implements Global.System.Collections.Generic.IEqualityComparer(Of Global.System.ValueTuple(Of Global.Iced.Intel.Code, Global.System.String, Global.Iced.Intel.CpuidFeature()))
```

元组实参用 `ValueTuple(Of ...)` 而不是 `(A, B)` 语法——和本文件里其它签名一致，也不依赖
`Imports`。配合「按泛型定义匹配接口成员」（`ResolveInterfaceDefinition`），这一类子句就齐了。

### 度量口径：Count 是文件数，Noise 列是全为噪声的文件数

表格里的 `Count` 一直是**出现该诊断的文件数**（不是诊断条数），新增的 `Noise` 列是
「该诊断在此文件中全部被判为噪声」的文件数。dnSpy.dll 实测：

| 诊断 | Count | Noise | 含义 |
|---|---|---|---|
| `BC30389` | 2416 | 2416 | 全部是 Friend 可见性假阳性 |
| `BC30284` | 367 | 367 | **全部是基类不可见的级联**，不是转换缺陷 |
| `BC30002` | 675 | 547 | 多为定义在另一个 dump 文件里的互操作类型 |

级联判据（`CollectInaccessibleBases`）：文件里出现 `BC30389` 且该类型名真的出现在
`Inherits` / `Implements` 子句中时，同一文件的 `BC30284` / `BC30401` / `BC30149` /
`BC30583` / `BC30610` / `BC30794` / `BC30517` 才算噪声——基类看不见时，VB 无法匹配
任何覆盖/实现关系，这些诊断必然是连锁反应。

### 不是转换缺陷：LINQ 推断族（`BC36645` / `BC32050` / `BC30518`）

`System.Linq.Enumerable.ToArray(x)` / `FirstOrDefault(x)` 报「无法推断 TSource」看起来很
像转换器丢了类型实参，实测是**不可见类型的连锁**：

- 整份编译（`--whole`，被 dump 的程序集内部类型可解析）时，`BC36645` 从 **163 个文件**
  掉到 **3 条诊断**；
- 逐文件样本 `RegisteredCommandElement:167` 的接收者是 `Friend` 类型
  `dnSpy.Commands.KeyShortcutCollection`（同文件 5 条 `BC30389`），VB 根本不知道参数类型，
  自然推不出 `TSource`。

所以这一族没有改转换器，也没有并入上面的级联判据（那会把「同一个文件里另有一处
Friend 引用、但推断确实有问题」的场面一起藏掉）。要复核类似结论，用 `--whole` 跑一遍最快。

另有两点关于「缺引用」的实测结论：

- dnSpy 的引用集（316 个程序集）里 `Microsoft.VisualStudio.Text/Language/Utilities`
  都在，**没有缺失的 VS SDK**；转储里也只用到这三个 VS 命名空间。
- 剩余上限不在引用，而在**逐文件编译**这种度量方式：被 dump 的程序集里 internal 类型/成员
  对「另一个程序集」不可见（`BC30389`），定义在别的 dump 文件里的类型也看不见
  （`BC30002`）。要再往下压只能换度量（整份 `--whole` 或给输出加 `InternalsVisibleTo`）。

这些都是「语义正确性」层面的后续工作，不影响「能否产出代码」。

## C# 质量门禁（2026-11-04，M3 补充）

M3 把 C# 默认引擎也切到了 3.2，但 VB 门禁从不度量 C# 输出。新增
`baseline/run-regression-cs.cmd`：三样本双引擎（3.2 + 2.3）dump + validate + compile，
**3.2 任一指标差于 2.3 即失败**。首轮实测（2026-11-04）：

| 样本 | 3.2 语法 | 2.3 语法 | 3.2 realBad | 2.3 realBad |
|---|---|---|---|---|
| dnSpy.dll | 635 | 908 | 1427 | 2167 |
| AsmEditor.x | 300 | 326 | 701 | 976 |
| Contracts.Logic | 8 | 9 | 81 | 81 |

3.2 C# 全面不差于 2.3 —— C# 切换质量有对拍依据。

### C# 侧已修与已知限制

**已修**：3.2 引擎会把 `[IteratorStateMachine(typeof(<X>d__N))]` / `[AsyncStateMachine(...)]`
带进输出——`typeof(<X>d__N)` 里的尖括号是 C# 无法表达的标识符（2.3 引擎不输出这两个特性）。
扩展的 C# 反编译器现在在渲染前删除这两类特性：它们只命名编译器自己的状态机类型，重编译时会
重新派生，删除无损。注意特性名可能写成**去后缀形式**（`IteratorStateMachine`），匹配须两种拼写
都收。实现走引擎的树级 API（`Decompile`/`DecompileType` → 清理 → 自行用 `CSharpOutputVisitor`
渲染），不动引擎子模块。

**已知限制（引擎级，暂缓）**：3.2 引擎的 `YieldReturnDecompiler` 对部分迭代器形态拒绝重建，
方法体退化为裸状态机桩（`<GetSecurityDeclarations>d__8 <GetSecurityDeclarations>d__ = ...`，
同样含非法标识符）。原方法确是 `yield return`（源码实证），2.3 引擎能重建。该家族是 C# 语法
错误的主导项（dnSpy.dll 上约 608 文件），修复需动引擎子模块的 IL→C# 变换（深水区），另立专项。

## 度量确定性：引用集必须显式（2026-11-03 教训）

`compile` 的引用 = 进程 TPA + `--refs` 目录。TPA 是否包含 Microsoft.WindowsDesktop.App
（PresentationCore 等 58 个程序集）是**非确定的**：同日两轮运行，dnSpy.dll 一次
`references: 316 / realBadFiles 1068`，一次 `references: 266 / realBadFiles 1710`——
暴涨的 642 个文件全是缺引用假阳性（`BC30652`/`BC30002`/`BC30456 'Media'`），不是转换缺陷。

修法：`Baseline.csproj` 显式 `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />`，
TPA 确定性包含桌面框架，refs 稳定在 320。**发现 realBadFiles 异常暴涨且错误形态为缺引用时，
先看报告里的 `references:` 行。**

## 诊断开关

失败注释默认只写一行（异常类型 + 消息）。设置 `DNSPY_ILSPY32_DIAG=1` 会把完整调用栈
追加进注释（单行 ` | ` 分隔），用于定位转换器内部失败——转换器吞掉所有异常，这是唯一的
栈来源。默认不生效，不影响正常输出。

## 局限

- 只验证「是否产出真实反编译文本」，**未做语法/编译验证**（编译需要完整引用集，属后续增量）。
- `compare` 的逐行差异只用于观察，不代表质量优劣；`identical=0` 是常态（两代引擎排版不同）。
