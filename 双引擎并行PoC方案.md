# 双引擎并行 PoC 方案

> 目标：在 dnSpy 中并行接入现代 ILSpy 引擎（C# 反编译），与现有 2.3.x 引擎共存并可切换。
> 本文所有 API/契约细节均已在仓库中核实，附出处行号，可直接照着写代码。

---

## 0. 前置事实（已核实）

| # | 事实 | 出处 |
|---|---|---|
| 1 | 反编译器契约输入 **dnlib 类型**，输出 `IDecompilerOutput`（语义文本汇） | `dnSpy/dnSpy.Contracts.Logic/Decompiler/IDecompiler.cs:112` |
| 2 | 纯文本输出是受支持路径（已有 `StringBuilderDecompilerOutput`） | `.../StringBuilderDecompilerOutput.cs:28` |
| 3 | **文档/UI 层零处引用 `AstNode`** → 适配器无需产出 NRefactory AST | `dnSpy/dnSpy/Documents/Tabs/DocViewer/` grep = 0 |
| 4 | 扩展发现模式为 `*.x.dll` | `dnSpy/dnSpy/MainApp/App.xaml.cs:351` |
| 5 | 已有 5 个 `IDecompilerCreator` 并存（C#/VB/IL/ILAst/Mixed） | `Extensions/ILSpy.Decompiler/dnSpy.Decompiler.ILSpy/*/DecompilerCreator.cs` |
| 6 | `DecompilationContext.IsBodyModified` 可判定方法体是否被编辑 | `.../DecompileDocumentTabContent.cs:181` |
| 7 | 现代引擎单目标 **`netstandard2.0`**，依赖仅 Immutable 9.0 + SRM 9.0 | `icsharpcode/ILSpy` 的 `ICSharpCode.Decompiler.csproj` |
| 8 | 现代引擎 API：`new CSharpDecompiler(file, DecompilerSettings)`、`DecompileAsString(EntityHandle…)` | 上游 `CSharp/CSharpDecompiler.cs` |
| 9 | `BoxedTextColor` 为 public static | `dnSpy/dnSpy.Contracts.Logic/Text/TextColor.cs:358` |
| 10 | `DecompilerSettingsBase` 仅 6 个抽象成员 | `.../DecompilerSettingsBase.cs` |

**`IDecompiler` 共 27 个成员**（10 属性 + 17 方法）。PoC 只真正实现其中 2～3 个，其余抛 `NotSupportedException`。

---

## 1. 范围

### 目标（PoC 必达）
- 语言下拉出现第二个 C# 引擎
- 能对**磁盘上的、未修改的**方法体显示反编译结果
- 与旧引擎可切换、可对比
- 对内存模块 / 已编辑方法**明确回退**并给出提示文本

### 非目标（PoC 不做）
- ❌ VB 反编译（现代 ILSpy 无 VB）
- ❌ 调试步进的行映射（`MethodDebugInfo`）
- ❌ Edit Method 往返
- ❌ 语法高亮着色 / 语义引用跳转（先用单一颜色输出）
- ❌ 设置项映射（用默认 `DecompilerSettings`）
- ❌ 类型/命名空间/程序集级反编译（可后续加）

### 验收标准（硬指标）
1. 语言下拉出现 `C# (ILSpy 11 PoC)`
2. 打开 #529 复现工程的 `Module1.OpenWithRetry`，可看到新引擎输出
3. 切回 `C# (ILSpy)` 旧引擎，行为无变化（零回归）
4. **net48 与 net10.0-windows 两个 TFM 下都能实际运行**（不只是编译通过）
5. 三目标构建（`net` / `netframework` / `net-x64`）全绿

---

## 2. 架构

```
dnSpy.exe
 ├─ dnSpy.Decompiler.ILSpy.x.dll          ← 旧引擎扩展（不动）
 │    └─ ICSharpCode.Decompiler.dll       ← 2.3.2 + dnlib（不动）
 └─ dnSpy.Decompiler.ILSpyModern.x.dll    ← 新增 PoC 扩展
      └─ ILSpyModern.Decompiler.dll       ← 现代引擎，已改名重编
```

关键：**两个引擎必须是不同 AssemblyName**，否则强命名冲突无法共存。

数据流：

```
dnlib MethodDef
  → method.Module as ModuleDefMD → .Location（磁盘路径）
  → MetadataTokens.MethodDefinitionHandle((int)method.MDToken.Rid)
  → CSharpDecompiler.DecompileAsString(handle)
  → IDecompilerOutput.Write(text, BoxedTextColor.Text)
```

> **API 校正（已核实）**：dnlib 的 `MDToken` 提供 `.Raw` 与 `.Rid`（仓库内分别有 58 / 2 处用法），**没有 `.Row`**。行号用 `.Rid`。
> `ContentTypeString` 取字面量 `"C# ILSpy"` —— 因为 `ContentTypesInternal` 是 `internal` 且位于 `dnSpy.Decompiler.ILSpy.Core`（本方案禁止引用的项目）。复用同一 content type 可直接获得既有语法高亮。

---

## 3. 分阶段步骤

### Phase 0 — 现代引擎改名为可并存（0.5 天）

**0.1** 以 submodule 形式引入上游（只取引擎目录即可，避免拉整个 UI）：

```bash
cd D:/Codebase/dnSpyEx/dnSpyEx
git submodule add https://github.com/icsharpcode/ILSpy.git Ext/ILSpy11
git -C Ext/ILSpy11 sparse-checkout init --cone
git -C Ext/ILSpy11 sparse-checkout set ICSharpCode.Decompiler ICSharpCode.Decompiler.Generators BuildTools
```

> 若不想动 submodule，也可直接复制 `ICSharpCode.Decompiler/` 目录到 `Ext/ILSpy11/` 并记录上游 commit。

**0.2** 改 `Ext/ILSpy11/ICSharpCode.Decompiler/ICSharpCode.Decompiler.csproj`：

```xml
<AssemblyName>ILSpyModern.Decompiler</AssemblyName>
<PackageId>ILSpyModern.Decompiler</PackageId>
<!-- 取消强命名，避免与原引擎 key 冲突；或换一把自有 snk -->
<SignAssembly>false</SignAssembly>
<!-- 该 csproj 的 ILSpyUpdateAssemblyInfo 目标依赖 git/VERSION，PoC 可禁用 -->
```

同时删除/注释 `ILSpyUpdateAssemblyInfo` 与 `CleanUpdateAssemblyInfo` 两个 Target，以及 `DecompilerVersionInfo` 的生成逻辑（PoC 用固定版本号）。

**0.3** 仅构建引擎项目：

```bash
dotnet build Ext/ILSpy11/ICSharpCode.Decompiler/ICSharpCode.Decompiler.csproj -c Release -f netstandard2.0
```

**验证（必须）**
- 产物名为 `ILSpyModern.Decompiler.dll`
- 用 `dotnet` 反射或 `sigcheck` 确认 AssemblyName 已变、未强命名
- **在 net48 下写一个 10 行控制台程序，`new CSharpDecompiler(path, new DecompilerSettings())` 并打印一段反编译文本** —— 这一步验证 netstandard2.0 引擎在 .NET Framework 上真能跑（不只是引用成功）

**回滚**：`git submodule deinit -f Ext/ILSpy11 && git rm -f Ext/ILSpy11`

---

### Phase 1 — 适配器能注册（1 天）

**1.1** 新建项目 `Extensions/Decompiler.ILSpyModern/dnSpy.Decompiler.ILSpyModern.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\..\DnSpyCommon.props" />
  <PropertyGroup>
    <AssemblyName>dnSpy.Decompiler.ILSpyModern.x</AssemblyName>
    <SignAssembly>True</SignAssembly>
    <AssemblyOriginatorKeyFile>..\..\dnSpy.snk</AssemblyOriginatorKeyFile>
    <OutputPath>..\..\dnSpy\dnSpy\bin\$(Configuration)\</OutputPath>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\dnSpy\dnSpy.Contracts.Logic\dnSpy.Contracts.Logic.csproj" />
    <!-- 注意：绝不引用 dnSpy.Decompiler.ILSpy.Core，否则会连带引入旧引擎，产生同名类型歧义 -->
    <ProjectReference Include="..\..\Ext\ILSpy11\ICSharpCode.Decompiler\ICSharpCode.Decompiler.csproj" />
  </ItemGroup>
</Project>
```

> 若现代引擎以 dll 形式引用，改为 `<Reference Include="ILSpyModern.Decompiler"><HintPath>…</HintPath></Reference>`。

同时把项目加入 `dnSpy.sln`（否则 build.ps1 不会编译它）。

**1.2** `ModernSettings.cs` —— 6 个抽象成员的最小实现：

```csharp
sealed class ModernSettings : DecompilerSettingsBase {
    public override DecompilerSettingsBase Clone() => new ModernSettings();
    public override int Version => 0;
    public override event EventHandler? VersionChanged;
    public override IEnumerable<IDecompilerOption> Options => Array.Empty<IDecompilerOption>();
    public override bool Equals(object? obj) => obj is ModernSettings;
    public override int GetHashCode() => 0;
    void Raise() => VersionChanged?.Invoke(this, EventArgs.Empty);
}
```

**1.3** `ModernCSharpDecompiler.cs` —— 27 成员骨架（关键部分）：

```csharp
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using dnSpy.Contracts.Decompiler;          // IDecompiler / IDecompilerOutput / BoxedTextColor
using dnSpy.Contracts.Text;
using ICSharpCode.Decompiler;              // 现代引擎的 DecompilerSettings
using ICSharpCode.Decompiler.CSharp;
using ModernCSharpDecompilerEngine = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;

sealed class ModernCSharpDecompiler : IDecompiler {
    public DecompilerSettingsBase Settings { get; } = new ModernSettings();
    public MetadataTextColorProvider MetadataTextColorProvider => throw new NotSupportedException();
    public string ContentTypeString => "C# ILSpy";   // 复用旧 C# 引擎的 content type，白得语法高亮
    public string GenericNameUI => "C#";
    public string UniqueNameUI => "C# (ILSpy 11 PoC)";
    public double OrderUI => DecompilerConstants.CSHARP_ILSPY_ORDERUI + 0.5;
    public Guid GenericGuid => DecompilerConstants.LANGUAGE_CSHARP;
    public Guid UniqueGuid => new Guid("B4E8F0A1-5C7D-4A2E-9F31-6D0C1E2A7B45"); // 自定，勿与旧引擎重复
    public string FileExtension => ".cs";
    public string? ProjectFileExtension => null;

    public void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
        // 见 Phase 2
    }

    // 其余成员 PoC 阶段一律 NotSupportedException
    public void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx)
        => throw new NotSupportedException();
    // …PropertyDef/FieldDef/EventDef/AssemblyDef/ModuleDef/DecompileNamespace
    // …WriteName/WriteType/WriteToolTip/WriteNamespaceToolTip/Write/WriteCommentBegin/End/ShowMember
}
```

**1.4** `Creator.cs`：

```csharp
[Export(typeof(IDecompilerCreator))]
sealed class ModernDecompilerCreator : IDecompilerCreator {
    public IEnumerable<IDecompiler> Create() => new IDecompiler[] { new ModernCSharpDecompiler() };
}
```

**验证**
- `dotnet build` 通过
- 启动 dnSpy，语言下拉出现 `C# (ILSpy 11 PoC)`
- 选中它打开任意方法 → 抛出可捕获的 `NotSupportedException` 或显示空内容（不崩溃）

**回滚**：从 sln 移除项目，删除目录；无其它副作用。

---

### Phase 2 — 单方法真反编译（2 天）

**2.1** 模块路径解析 + 缓存：

```csharp
sealed class EngineCache {
    readonly Dictionary<string, (ModernCSharpDecompilerEngine D, DateTime Stamp)> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();

    public ModernCSharpDecompilerEngine Get(string path) {
        lock (gate) {                                   // CSharpDecompiler 非线程安全
            var stamp = File.GetLastWriteTimeUtc(path);
            if (cache.TryGetValue(path, out var e) && e.Stamp == stamp) return e.D;
            var d = new ModernCSharpDecompilerEngine(path, new DecompilerSettings());
            cache[path] = (d, stamp);
            return d;
        }
    }
    public T Run<T>(string path, Func<ModernCSharpDecompilerEngine, T> f) {
        lock (gate) return f(Get(path));                // 反编译本身串行化，PoC 可接受
    }
}
```

**2.2** `Decompile(MethodDef, …)` 实现：

```csharp
public void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
    // 1) 已编辑的方法体 → 回退提示（不尝试映射，避免显示陈旧内容）
    if (ctx.IsBodyModified?.Invoke(method) == true) { WriteNote(output, "// 该方法体已被修改，PoC 引擎暂不显示。"); return; }

    // 2) 必须是磁盘上的 ModuleDefMD
    if (method.Module is not ModuleDefMD md || string.IsNullOrEmpty(md.Location) || !File.Exists(md.Location)) {
        WriteNote(output, "// 模块无磁盘路径（内存模块），PoC 引擎暂不显示。"); return;
    }

    ctx.CancellationToken.ThrowIfCancellationRequested();
    var handle = MetadataTokens.MethodDefinitionHandle((int)method.MDToken.Rid);

    string text;
    try {
        text = cache.Run(md.Location, d => d.DecompileAsString(handle));
    }
    catch (Exception ex) { WriteNote(output, "// ILSpy 11 反编译失败: " + ex.GetType().Name + ": " + ex.Message); return; }

    output.Write(text, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
}

static void WriteNote(IDecompilerOutput o, string s) =>
    o.Write(s, null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
```

**2.3** 关掉"未实现成员"的崩溃面：确认 dnSpy 在哪种场景会调用 `Decompile(TypeDef)`。PoC 若只测方法视图，先实现 `Decompile(TypeDef)` 为"遍历 `type.Methods` 逐个调用"，成本很低，建议一并做（否则点开类型节点会抛异常）。

**验证**
- 打开任意 DLL → 选择新引擎 → 单方法能看到 C# 代码
- 用 #529 的 `ConsoleApp1.zip` 复现工程：**两个引擎并排对比 `Module1.OpenWithRetry`**
- 编辑方法体后切到新引擎 → 出现回退提示而非陈旧内容

---

### Phase 3 — 类型级与稳定性（1 天，可选）

- `Decompile(TypeDef)`：先写类型签名 + 成员逐个反编译
- 长方法性能测量（首次构造 `CSharpDecompiler` 会加载整个 PE，注意首次延迟）
- `MetadataTextColorProvider` 若被 UI 强制调用，补一个最小实现

---

### Phase 4 — 回归与验收（0.5 天）

```powershell
$env:DNSPY_BUILD_ARGS = '-NoMsbuild net';          & 'Build\build-log.ps1' -Script 'build.ps1'
$env:DNSPY_BUILD_ARGS = '-NoMsbuild netframework'; & 'Build\build-log.ps1' -Script 'build.ps1'
$env:DNSPY_BUILD_ARGS = '-NoMsbuild net-x64';      & 'Build\build-log.ps1' -Script 'build.ps1'
```

逐项核对第 1 节验收标准，并在 `build_history.log` 记录结果。

---

## 4. 风险与对策

| 风险 | 概率 | 影响 | 对策 |
|---|---|---|---|
| **net48 下现代引擎无法运行**（并非只引用成功） | 中 | 阻塞 | Phase 0.3 的 10 行控制台验证必须做；失败则退为"net10 版专属功能" |
| 程序集冲突未彻底解决 | 中 | 启动即 `TypeLoadException` | 改名必须在 Phase 0 完成并单独验证；绝不引用 `dnSpy.Decompiler.ILSpy.Core` |
| `System.Reflection.Metadata` / `Immutable` 版本与 dnSpy 已有版本冲突 | 中 | 运行时绑定失败 | 统一版本 + `bindingRedirect`；Phase 0 验证时先测 |
| 内存模块占比高（从进程打开的程序集） | 高 | 功能覆盖低 | PoC 明确回退；后续用 `module.Write(stream)` 补 |
| `CSharpDecompiler` 构造昂贵（每次读整个 PE） | 高 | 首次打开卡顿 | 按路径+时间戳缓存，见 2.1 |
| 反编译输出无颜色/无跳转，体验差 | 确定 | 观感 | PoC 接受；后续接 `MetadataTextColorProvider` 与 reference 写入 |
| 现代引擎要求 C# 14 / SDK 10 构建 | 低 | 构建失败 | 本机已有 SDK 10.0.401（build.ps1 已默认 `-NoMsbuild`） |

---

## 5. 工作量与判据

| 阶段 | 人日 |
|---|---|
| Phase 0 引擎改名与 net48 可行性 | 0.5 |
| Phase 1 适配器注册 | 1 |
| Phase 2 单方法反编译 | 2 |
| Phase 3（可选）类型级 | 1 |
| Phase 4 回归验收 | 0.5 |
| **合计** | **约 5 人日（不含 Phase 3）** |

### 继续 / 放弃判据
- **Phase 0 后**：net48 控制台无法运行现代引擎 → 若不能接受"net10 专属"，**立即停止**
- **Phase 1 后**：语言下拉不出现 → 检查 `*.x.dll` 输出路径与 MEF 导出，半天内不能解决则停
- **Phase 2 后**：单方法反编译成功率 < 80%（token 映射或 resolver 大面积失败）→ 停止

> 说明：本表是 **PoC** 的工作量。做到"与旧引擎特性对等"（含调试映射、着色、跳转、设置、编辑往返）仍是 2–4 人月量级，二者不要混淆。

---

## 6. PoC 通过后的路线

1. 补 `MethodDebugInfo`：把 ILSpy 的 sequence point 映射到文本位置 → 恢复调试步进高亮
2. 补语义写入：`output.Write(text, reference, flags, color)` → 恢复 Ctrl+Click 跳转
3. 设置映射：`DecompilerSettings` ↔ dnSpy 选项页
4. 内存模块支持：`module.Write(stream)` → `PEReader`，并处理"编辑后内容一致性"
5. 稳定后评估：是否把旧引擎的 C# 路径下线（VB/IL/编辑仍留在旧引擎）

---

## 7. 一句话总结

**PoC 的本质是验证一件事：能不能让现代引擎以"第二个反编译器"的身份，在一个未修改的磁盘程序集上，输出正确的 C# 文本。** 能，就说明双引擎路线成立，后续只是补体验；不能，损失 5 人日。
