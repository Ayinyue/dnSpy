# VB 语义正确性任务行动纲要（供新模型接手）

> 本文件是交接用的**唯一行动依据**。配套阅读：`VB-Dev-Plan.md`（可勾选的开发计划）与
> `baseline/README.md`（29 条已修经验 + 度量口径）。
> 更新日期：2026-11-03。基线提交：`b13f42a34`。

## 1. 目标（不变）

dnSpy 反编译器换到 ILSpy 3.2 后端后，VB 输出从「能产出文本」推进到「语义正确」。
**排序优先级：真实验证 > 不回归 > 修一类错 > 消一类警告。**

绝不以「能编译」代替「语义正确」，也不以「真实正确率」的估算代替实测。

## 2. 不可妥协的 6 条规矩

1. **能编译 ≠ 对**。每个转换策略都要能论证「没改变语义」，必要时查 dnSpy C# 源码对照。
2. **噪声不修复**。`BC30389`（internal 可见性）、`BC30284`（基类不可见级联）、`BC36645/BC32050/BC30518`
   （LINQ 推断级联）是逐文件编译的产物，不是转换缺陷。先定性再动手。
3. **单类型局部修好 ≠ 全局更好**。每次改动必须全量三样本回归后才决定留不留。
4. **默认值、类型名、参数顺序必须来自元数据**，不能猜；宁可留显式错误，不写静默错误代码。
5. **一次只做一类诊断**，一次一个 commit，代码与文档分开提交。
6. **临时调试输出（`Console.WriteLine`、env 跟踪）一律不得进提交。**

## 3. 当前基线（2026-11-02 全量实测）

| 程序集 | 转换 | 语法错误 | 语义 realBadFiles |
|---|---|---|---|
| `dnSpy.dll`（3266 类型，--all）| 3266 ok / 0 degraded | 5（全 `BC30036` 整数字面量溢出）| **1068** |
| `dnSpy.AsmEditor.x.dll`（1278）| 1278 ok / 0 degraded | **0** | **565** |
| `dnSpy.Contracts.Logic.dll`（84 public）| 84 ok / 0 degraded | 1（溢出）| **10** |

`dnSpy.dll` 主要诊断分布（单位：**文件数**，Noise 列 = 全为噪声的文件数）：

| 诊断 | Count | Noise | 定性 |
|---|---|---|---|
| `BC30389` | 2408 | 2408 | 可见性假阳性，勿动 |
| `BC30002` | 669 | 563 | 跨 dump 文件的互操作/状态机类型（度量口径）|
| `BC30284` | 367 | 367 | 基类不可见级联，勿动 |
| `BC32022` | 266 | 0 | 事件被当委托调用（未消净，非泛型类的下一层）|
| `BC30149` | 156 | 112 | 成员实现缺失/签名不符（已部分修复）|
| `BC30456` | 229 | 12 | 成员不存在（`ImageReference`/`BookmarkInfo` 等）|
| `BC36645`+`BC32050`+`BC30518` | 434 | 0 | LINQ 推断级联，**勿当 bug 修** |
| `BC30401` | 109 | 56 | 签名不符（子句已发出来，诊断更精确）|
| `BC30452` | 92 | 0 | 引用比较漏判（两侧类型都推不出）|
| `BC31132` | 81 | 0 | 真自定义事件缺 `RaiseEvent` |
| `BC30583` | 45 | 34 | 重复实现同一接口成员（下一层）|
| `BC30451` | 115 | 0 | 成员不可见/未声明（疑似级联，待定性）|
| `BC30521` | 108 | 0 | 重载解析歧义（同名不同实现）|

## 4. 复现命令（在 `Extensions/ILSpy32.Decompiler/baseline` 下执行）

```bash
BIN=../../../dnSpy/dnSpy/bin/Release/net10.0-windows
WPF=$(ls -d "/c/Program Files/dotnet/packs/Microsoft.WindowsDesktop.App.Ref/"*/ref/net10.0 | tail -1)
EXE=bin/Release/net10.0/baseline.exe

# 改完引擎后必须重编：dotnet build -c Release -f net10.0-windows --no-restore
#  （在 dnSpy.Decompiler.ILSpy32 目录；产物直接落到 dnSpy 的 bin）

$EXE dump --asm $BIN/dnSpy.dll --impl ilspy32 --lang vb --out out/v32-vb-dnspy --all
$EXE validate --dir out/v32-vb-dnspy --lang vb
$EXE compile --dir out/v32-vb-dnspy --lang vb --refs "$BIN;$WPF" --out out/compile-vb-dnspy.md

# 另外两个样本：dnSpy.AsmEditor.x.dll（--all）、dnSpy.Contracts.Logic.dll（不带 --all）

# 单类型排查（最高频用法）
$EXE dump --asm $BIN/dnSpy.dll --impl ilspy32 --lang vb --out out/one-x --type <Name> --all
$EXE compile --dir out/one-x --lang vb --refs "$BIN;$WPF" --type <Name> --top 2

# 看某类诊断的具体文件:行号（整份编译）
$EXE compile --dir out/v32-vb-dnspy --lang vb --refs "$BIN;$WPF" --whole --id BC30149 --top 2
```

**两个必须记住的工具语义**
- `--all`：dump 含 internal 类型，否则只有 8 个 public，样本失真。
- 逐文件 vs `--whole`：差值是判断「真缺陷还是级联」最省事的证据。

## 5. 代码落点（改这里，别找错文件）

| 文件 | 职责 |
|---|---|
| `dnSpy.Decompiler.ILSpy32/VisualBasic/CSharpToVBConverter32.cs` | C#→VB AST 转换（绝大多数修复点）|
| `dnSpy.Decompiler.ILSpy32/Common/ExpressionTypeResolver.cs` | 轻量类型推断（引用比较、Char、隐式 String 转换）|
| `dnSpy.Decompiler.ILSpy32/Common/AstAnnotationBinder.cs` | 给 3.2 语法树补 dnlib 注解（泛型/嵌套类型的绑定曾整类失效）|
| `dnSpy.Decompiler.ILSpy32/Common/DnlibCecilBridge.cs` | dnlib↔cecil 桥（TypeSpec 覆盖不全）|
| `baseline/Program.cs` | 度量工具（dump/validate/compile；噪声判据在这里）|

## 6. 下一步候选（按优先级）

> 本节是**候选池**；可勾选的执行版在 `VB-Dev-Plan.md`（批次、任务、验收标准）。

### P1 `BC30452`（92）：引用比较漏判
- 现象：`IDocumentTab = TabContentImpl`，两侧类型都推不出 → 退化成 `=`。
- 思路：扩大 `ExpressionTypeResolver.GetTypeName` 的覆盖面（属性/字段/方法返回值/数组元素）。
- 风险：把值类型比较误写成 `Is`（VB 会报错）。**不确定就保持 `=`。**

### P2 `BC30401`（109）：子句已发出来，逐条看签名不符
- 已按接口元数据补 `Optional`（见提交 `272309359`）。剩余多为 `ByRef` / 泛型实参 / 元组类型差异。
- 思路：用 `--whole --id BC30401` 取 20 条样本归类，再决定改哪一条比较规则。
- 注意：`ResolveInterfaceDefinition` 只按「名字 + 元数」匹配，重载多时会错配，别扩大它的适用面。

### P3 `BC30456`（229）：成员不存在
- 代表：`'ImageReference' is not a member of 'BookmarkInfo'`。
- 思路：单类型看是「跨 dump 文件类型」（度量口径，勿动）还是「真没生成该成员」（转换缺陷）。

### P4 `BC32022`（266）/ `BC31132`（81）：事件
- `BC32022`：事件副本抑制目前只在「恰好两次引用」时生效；真正的自定义事件读取还没覆盖。
- `BC31132`：剩下的是**带真实逻辑**的自定义事件，需要合成 `RaiseEvent` 访问器（不能套自动事件的改写）。

### P5 `BC30583`（45）/ `BC30521`（108）
- `BC30583` 去重键已含签名，剩余 `ITextView.GetTextViewLineContainingBufferPosition` 需单独看。
- `BC30521` 同名重载歧义，可能需 `Overloads` 或更名。

### 明确不做
- `BC30389`、`BC30284`、`BC36645/BC32050/BC30518`：级联噪声。
- `BC30002`（互操作类型）：度量口径问题，若做请走「整份编译/`InternalsVisibleTo`」路线，不改转换器。

## 7. 已修且勿回退的 9 个关键点

1. 泛型类型注解绑定（`AstAnnotationBinder` 剥 arity）。
2. 嵌套类型注解兜底（唯一简单名）。
3. 自动事件改字段式事件（`[CompilerGenerated]` 判定）。
4. 成员 `Implements` 子句全限定（`Global.<ns>.<name>`）。
5. 接口子句按（接口, 成员**签名**）去重——按名字去重会吞重载。
6. `Optional`/默认值从接口元数据复制，不一律 `Nothing`。
7. **子句类型按 dnlib 签名构造**（`CreateTypeFromSig`），元组实参写 `ValueTuple(Of ...)`。
8. 隐式 String 转换插 `CStr`（必须 `CastType.CStr` 节点，不能写调用形式）。
9. 引用比较：任一侧已知是引用类型即可判 `Is`；泛型名先剥实参与 arity 再分类。

## 8. 交接 SOP

1. 读本文件 + `baseline/README.md`。
2. 跑第 4 节三条命令，把自己的数字和第 3 节对上；对不上先查环境，别先改代码。
3. 选一类诊断，`--type` 单类型复现，先写「真缺陷 / 级联」的判断。
4. 最小范围修改 → 单类型回归 → 全量三样本。
5. 通过标准：构建 0 错误；语法错误仍为 5/0/1；realBadFiles 不高于基线；无调试残留。
6. 代码 commit → 文档 commit（`baseline/README.md` 的经验表和本文件的基线数字同步更新）。

---

**给接手者的一句话**：这里最昂贵的错误不是「修得慢」，而是**把一个级联噪声当成缺陷修了几小时，或把一次局部成功当成全局改进直接提交**。先定性，再小步，再全量。
