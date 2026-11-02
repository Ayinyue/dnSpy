# baseline — 双引擎输出基线对拍

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

修复后的语法错误文件数：

| 程序集 | 语言 | 语法错误文件 |
|---|---|---|
| AsmEditor（1278） | VB | **0** |
| dnSpy.dll（3266） | VB | **5（0.15%）** |
| Contracts.Logic（84） | VB | **1（1.2%）** |

### 剩余已知形态（未修）

- `New T(...).Method()` 单独成句（VB 需要 `Call`）。dnSpy.dll 的 BC30035 主要来自这里。
- 无方法体的 P/Invoke / abstract 声明缺少 `End Function`（应改写成 `Declare` 形式）。
- 超出范围的整数字面量（BC30036）。

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
| `BC32022` 事件当委托调用（AsmEditor）| 36 | **0** |
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
| `BC30583` 重复的接口子句 | 168 | **45**（其中 34 个文件全为噪声）|
| `BC30149` 成员子句未限定命名空间 | 3956（whole）| **0** |
| `BC30452` `UTF8String + String` | 116 | **0** |
| `BC30452` Char 与数值比较 | 112 | **0** |
| 泛型类型的注解绑定（整类静默失效）| — | 已修，见第 25 条 |
| `BC32022` 事件当委托调用（dnSpy.dll）| 379 | **266** |
| `BC31132` 自定义事件缺 `RaiseEvent` | 92 | **81**（余下是真正带逻辑的自定义事件）|
| `BC30149` 接口实现缺 `Optional` | 255 | **156** |
| `BC30401` 同上/签名不符 | 108 | **109**（子句都发出来了，反而多暴露 12 处真不符）|
| `BC30002` 带元组实参的接口子句 | 675 | **669** |

Contracts.Logic 的真实失败文件 54 → **10**；AsmEditor 906 → 896 → 825 → 583 → 565 → **565**；
dnSpy.dll（3266 类型）2039 → 1408 → 1120 → 1096 → 1088 → **1068**。

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

## 诊断开关

失败注释默认只写一行（异常类型 + 消息）。设置 `DNSPY_ILSPY32_DIAG=1` 会把完整调用栈
追加进注释（单行 ` | ` 分隔），用于定位转换器内部失败——转换器吞掉所有异常，这是唯一的
栈来源。默认不生效，不影响正常输出。

## 局限

- 只验证「是否产出真实反编译文本」，**未做语法/编译验证**（编译需要完整引用集，属后续增量）。
- `compare` 的逐行差异只用于观察，不代表质量优劣；`identical=0` 是常态（两代引擎排版不同）。
