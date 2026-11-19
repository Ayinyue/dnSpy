using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

class Probe {
	static string? ilspy23DirOverride;

	static int Main(string[] args) {
		ilspy23DirOverride = Environment.GetEnvironmentVariable("PROBE_ILSPY23_DIR");
		if (args.Length > 0 && args[0] == "make")
			return MakeReproAssembly(args[1]);
		if (args.Length > 0 && args[0] == "decompile")
			return DecompileRepro(args[1], args[2], args[3]);
		if (args.Length > 0 && args[0] == "count")
			return CountReferences(args[1], args[2], args[3]);
		if (args.Length > 0 && args[0] == "type")
			return DecompileNamedType(args[1], args[2], args[3], args[4]);
		if (args.Length > 0 && args[0] == "export")
			return TestExportPaths(args[1], args[2], args[3]);
		if (args.Length > 0 && args[0] == "spdump") {
			InstallBinResolver(new[] { args[2] });
			return SpDumpLang(args);
		}
		if (args.Length > 0 && args[0] == "engine-tests") {
			InstallBinResolver(new[] { args[2] });
			return EngineTests(args[1], args[2]);
		}
		if (args.Length > 0 && args[0] == "dbg-map") {
			InstallBinResolver(new[] { args[2] });
			return DbgMap(args[1], args[2]);
		}
		if (args.Length > 0 && args[0] == "dbg-map2") {
			InstallBinResolver(new[] { args[2] });
			return DbgMap2(args[1], args[2], args[3], args[4], int.Parse(args[5]));
		}
		if (args.Length > 0 && args[0] == "options-wiring") {
			InstallBinResolver(new[] { args[2] });
			return OptionsWiring(args[1], args[2], args[3]);
		}
		if (args.Length > 0 && args[0] == "text-query") {
			InstallBinResolver(new[] { args[2] });
			return TextDumpWithOptions(args[1], args[2], args[3], args[4], args[5], queryOn: true);
		}
		if (args.Length > 0 && args[0] == "text-nooptions") {
			InstallBinResolver(new[] { args[2] });
			return TextDumpWithOptions(args[1], args[2], args[3], args[4], args[5], applyOptions: true);
		}
		{
			var dnspyBin = "D:/Codebase/dnSpyEx/dnSpyEx/dnSpy/dnSpy/bin/Release/net10.0-windows";
			System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) => {
				var pp = System.IO.Path.Combine(dnspyBin, name.Name + ".dll");
				return System.IO.File.Exists(pp) ? ctx.LoadFromAssemblyPath(pp) : null;
			};
		}
		if (args.Length > 0 && args[0] == "state-diag") {
			var dnspyBin = "D:/Codebase/dnSpyEx/dnSpyEx/dnSpy/dnSpy/bin/Release/net10.0-windows";
			System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) => {
				var pp = System.IO.Path.Combine(dnspyBin, name.Name + ".dll");
				return System.IO.File.Exists(pp) ? ctx.LoadFromAssemblyPath(pp) : null;
			};
			return StateMachineDiagRun(args[1], dnspyBin, int.Parse(args[2]));
		}
		if (args.Length > 0 && args[0] == "ildump")
			return IlDumpRun(args[1]);
		if (args.Length > 0 && args[0] == "settings-tests") {
			var dnspyBin = "D:/Codebase/dnSpyEx/dnSpyEx/dnSpy/dnSpy/bin/Release/net10.0-windows";
			System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) => {
				var p = System.IO.Path.Combine(dnspyBin, name.Name + ".dll");
				return System.IO.File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
			};
			return SettingsMigrationTests.Run();
		}
		if (args.Length > 0 && args[0] == "text") {
			InstallBinResolver(new[] { args[2] });
			return TextDump(args[1], args[2], args[3], args[4], args[5]);
		}
		if (args.Length > 0 && args[0] == "asyncdiff") {
			InstallBinResolver(new[] { args[2] });
			return AsyncDiff(args[1], args[2], int.Parse(args[3]));
		}
		if (args.Length > 0 && args[0] == "bench") {
			InstallBinResolver(new[] { args[2] });
			return Bench(args[1], args[2], int.Parse(args[3]));
		}
		if (args.Length > 0 && args[0] == "ilprobe") {
			InstallBinResolver(new[] { args[2] });
			return ILProbe(args[1], args[2], args[3]);
		}
		if (args.Length > 0 && args[0] == "asyncprobe") {
			InstallBinResolver(new[] { args[2] });
			return AsyncProbe(args[1], args[2], args[3]);
		}
		if (args.Length > 0 && args[0] == "mixed-verify") {
			InstallBinResolver(new[] { args[3] });
			return MixedVerify(args[1], args[2], args[3]);
		}
		if (args.Length > 0 && args[0] == "lang-diff-all") {
			return LangDiffAll(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
		}
		if (args.Length > 0 && args[0] == "lang-diff") {
			return LangDiff(args[1], args[2], args[3], args[4], args.Length >= 7 ? args[5] : null, args.Length >= 7 ? args[6] : null, args.Length >= 8 ? args[7] : null);
		}
		if (args.Length > 0 && args[0] == "settings-wiring") {
			InstallBinResolver(new[] { args[1] });
			return SettingsWiring(args[1]);
		}
		if (args.Length > 0 && args[0] == "ilast-verify") {
			InstallBinResolver(new[] { args[3] });
			return ILAstVerify(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
		}
		if (args.Length > 0 && args[0] == "ilprint-diff") {
			InstallBinResolver(new[] { args[2] });
			return ILPrintDiff(args[1], args[2], args[3], args[4]);
		}
		if (args.Length > 0 && args[0] == "colordump") {
			// Install the resolver BEFORE dispatching: the JIT of ColorDump needs dnlib and the
			// contracts, and with Private=false references those come only from this handler.
			InstallBinResolver(new[] { args[2] });
			return ColorDump(args[1], args[2], args[3], int.Parse(args[4]), args.Length > 5 ? args[5] : "", args.Length > 6 ? args[6] : ".");
		}
		Console.WriteLine("usage: make <path> | decompile <engineDll> <reproDll> <dnspyBin> | colordump <targetDll> <dnspyBin> <cs|vb> <maxTypes> [typeFilter] <outDir> | ilprint-diff <targetDll> <dnspyBin> <out23.txt> <out32.txt>");
		return 1;
	}

	static string[] binResolverDirs = Array.Empty<string>();

	static void InstallBinResolver(string[] dirs) {
		binResolverDirs = dirs;
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
			foreach (var dir in binResolverDirs) {
				var p = Path.Combine(dir, name);
				if (File.Exists(p)) return System.Reflection.Assembly.LoadFrom(p);
			}
			return null;
		};
	}

	// 2.3 引擎是用 Assembly.LoadFrom 从 path23 加载的：它的依赖（dnSpy.Contracts.Logic /
	// DnSpy，强名称 6.6.0.0）在 LoadFrom 上下文里解析，CI 上曾直接抛
	// FileNotFoundException（本地靠历史遗留的 dnSpy bin 才偶然通过）。提前 LoadFrom
	// 契约程序集，让 2.3 引擎的引用命中已加载的程序集。文件缺失时静默跳过。
	static void PreloadContracts(string dnspyBin) {
		foreach (var n in new[] { "dnSpy.Contracts.Logic", "dnSpy.Contracts.DnSpy" }) {
			var p = Path.Combine(dnspyBin, n + ".dll");
			if (!File.Exists(p))
				continue;
			try { System.Reflection.Assembly.LoadFrom(p); } catch { }
		}
	}

	// ==== settings-wiring: walk the MEF composition path (DecompilerCreator with the persisted
	// settings singletons) and assert the languages got the persisted instances, that the Mixed
	// view shares the IL settings with the IL view (as in 2.3), and that the 2.3 IL options were
	// carried over into the persisted 3.2 IL settings.
	static int SettingsWiring(string dnspyBin) {
		const string legacy23ILGuid = "DD6752B1-5336-4601-A9B2-0879E18AE9F3";
		var extAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		var csImplType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.Settings.DecompilerSettings32Impl", true);
		var vbImplType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.Settings.VbDecompilerSettings32Impl", true);
		var ilImplType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.Settings.ILDecompilerSettings32Impl", true);
		var ilastImplType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.Settings.ILAstDecompilerSettings32Impl", true);
		var creatorType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", true);

		var fake = new FakeSettingsService();
		// Pretend the user customized the 2.3 IL options: ShowILBytes=false, ShowXmlDocumentation=false.
		fake.Seed(Guid.Parse(legacy23ILGuid), new Dictionary<string, string> {
			{ "ShowILBytes", "False" },
			{ "ShowXmlDocumentation", "False" },
			{ "ShowPdbInfo", "True" },
		});

		var csImpl = NewInstance(csImplType, fake);
		var vbImpl = NewInstance(vbImplType, fake);
		var ilImpl = NewInstance(ilImplType, fake);
		var ilastImpl = NewInstance(ilastImplType, fake);
		var creator = creatorType.GetConstructors().First(c => c.GetParameters().Length == 4)
			.Invoke(new object[] { csImpl, vbImpl, ilImpl, ilastImpl });
		var langs = ((System.Collections.IEnumerable)creatorType.GetMethod("Create").Invoke(creator, null)).Cast<object>().ToList();

		var il = langs.First(l => l.GetType().Name == "ILDecompiler32");
		var ilast = langs.First(l => l.GetType().Name == "ILAstDecompiler32");
		var mixed = langs.First(l => l.GetType().Name == "ILWithCSharpDecompiler32");
		var csharp = langs.First(l => l.GetType().Name == "CSharpDecompiler32");

		var ilSettingsField = il.GetType().GetField("settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		var ilastSettingsField = ilast.GetType().GetField("settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		var mixedCsharpField = mixed.GetType().GetField("csharpLangSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		var mixedIlField = mixed.GetType().GetField("ilLangSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		var csharpField = csharp.GetType().GetField("settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

		var ilSettings = ilSettingsField.GetValue(il);
		var mixedIlSettings = mixedIlField.GetValue(mixed);
		var ilType = ilImpl.GetType().BaseType!;
		bool ok = true;
		void Check(string name, bool value) {
			Console.WriteLine(name + "=" + value);
			ok &= value;
		}

		Check("ilViewUsesPersistedInstance", ReferenceEquals(ilSettings, ilImpl));
		Check("ilastViewUsesPersistedInstance", ReferenceEquals(ilastSettingsField.GetValue(ilast), ilastImpl));
		Check("mixedSharesILSettingsWithILView", ReferenceEquals(mixedIlSettings, ilImpl));
		Check("mixedCsharpSettingsIsPersistedInstance", ReferenceEquals(mixedCsharpField.GetValue(mixed), csImpl));
		Check("csharpViewUsesSameInstance", ReferenceEquals(csharpField.GetValue(csharp), csImpl));
		Check("migrated23ShowILBytes", Equals(ilType.GetProperty("ShowILBytes")!.GetValue(ilSettings), false));
		Check("migrated23ShowXmlDocumentation", Equals(ilType.GetProperty("ShowXmlDocumentation")!.GetValue(ilSettings), false));
		Check("migrated23ShowPdbInfo", Equals(ilType.GetProperty("ShowPdbInfo")!.GetValue(ilSettings), true));
		// The carried-over values were persisted under the 3.2 section (created on migration).
		Console.WriteLine("persistedSections=" + fake.Sections.Length);
		return ok ? 0 : 1;
	}

	static object NewInstance(Type type, object arg) {
		var ctor = type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
			.First(c => c.GetParameters().Length == 1);
		return ctor.Invoke(new object[] { arg });
	}

	sealed class FakeSection : dnSpy.Contracts.Settings.ISettingsSection {
		public string Name { get; set; } = string.Empty;
		public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
		public (string key, string value)[] Attributes => Values.Select(kv => (kv.Key, kv.Value)).ToArray();
		public dnSpy.Contracts.Settings.ISettingsSection[] Sections => Array.Empty<dnSpy.Contracts.Settings.ISettingsSection>();
		public dnSpy.Contracts.Settings.ISettingsSection? GetSection(Guid guid) => null;
		public dnSpy.Contracts.Settings.ISettingsSection CreateSection(string name) => new FakeSection { Name = name };
		public dnSpy.Contracts.Settings.ISettingsSection GetOrCreateSection(string name) => new FakeSection { Name = name };
		public void RemoveSection(string name) { }
		public void RemoveSection(dnSpy.Contracts.Settings.ISettingsSection section) { }
		public dnSpy.Contracts.Settings.ISettingsSection[] SectionsWithName(string name) => Array.Empty<dnSpy.Contracts.Settings.ISettingsSection>();
		public dnSpy.Contracts.Settings.ISettingsSection? TryGetSection(string name) => null;
		public void Attribute<T>(string name, T value) => Values[name] = value?.ToString() ?? string.Empty;
		public T? Attribute<T>(string name) {
			if (Values.TryGetValue(name, out var s))
				return (T?)Convert.ChangeType(s, typeof(T));
			return default;
		}
		public void RemoveAttribute(string name) => Values.Remove(name);
		public void CopyFrom(dnSpy.Contracts.Settings.ISettingsSection section) {
			Name = section.Name;
			Values = section.Attributes.ToDictionary(a => a.key, a => a.value, StringComparer.Ordinal);
		}
	}

	sealed class FakeSettingsService : dnSpy.Contracts.Settings.ISettingsService {
		readonly Dictionary<Guid, FakeSection> sections = new Dictionary<Guid, FakeSection>();
		public void Seed(Guid guid, Dictionary<string, string> attributes) {
			sections[guid] = new FakeSection { Name = guid.ToString(), Values = new Dictionary<string, string>(attributes, StringComparer.Ordinal) };
		}
		public dnSpy.Contracts.Settings.ISettingsSection[] Sections => sections.Values.Cast<dnSpy.Contracts.Settings.ISettingsSection>().ToArray();
		public dnSpy.Contracts.Settings.ISettingsSection GetOrCreateSection(Guid guid) {
			if (!sections.TryGetValue(guid, out var sect)) {
				sect = new FakeSection { Name = guid.ToString() };
				sections.Add(guid, sect);
			}
			return sect;
		}
		public void RemoveSection(Guid guid) => sections.Remove(guid);
		public void RemoveSection(dnSpy.Contracts.Settings.ISettingsSection section) { }
		public dnSpy.Contracts.Settings.ISettingsSection RecreateSection(Guid guid) {
			var sect = new FakeSection { Name = guid.ToString() };
			sections[guid] = sect;
			return sect;
		}
	}

	// ==== lang-diff-all: the same comparison over every type of an assembly, so the residual
	// difference rate is not a 3-sample artefact.
	//   lang-diff-all <targetDll> <dnspyBin> <path23> [langFilter]
	static int LangDiffAll(string targetDll, string dnspyBin, string path23, string? langFilter = null) {
		InstallBinResolver(new[] { dnspyBin, path23 });
		PreloadContracts(dnspyBin);
		var module = ModuleDefMD.Load(targetDll);
		var languages23 = LoadProviders(Path.Combine(path23, "dnSpy.Decompiler.ILSpy.Core.dll"));
		var languages32 = LoadProviders(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		foreach (var kv32 in languages32) {
			var suffix = kv32.Key.Replace(" (ILSpy 3.2)", string.Empty);
			if (!languages23.TryGetValue(suffix, out var l23))
				continue;
			if (!string.IsNullOrEmpty(langFilter) && !kv32.Key.StartsWith(langFilter, StringComparison.Ordinal))
				continue;
			int types = 0, identical = 0, diffLines = 0, errors = 0;
			var samples = new List<string>();
			foreach (var type in module.Types) {
				if (type.Name!.StartsWith("<"))
					continue;
				types++;
				if (System.Environment.GetEnvironmentVariable("PROBE_TRACE_TYPES") == "1")
					Console.Error.WriteLine("[T] " + type.FullName);
				string? a = null, b = null;
				try { a = DecompileLanguage(l23.Item2, module, type.FullName); }
				catch { errors++; }
				try { b = DecompileLanguage(kv32.Value.Item2, module, type.FullName); }
				catch { errors++; }
				if (a is null || b is null)
					continue;
				if (a == b)
					identical++;
				else {
					diffLines += DiffLineCount(a, b);
					if (samples.Count < 25)
						samples.Add(type.FullName + " (" + DiffLineCount(a, b) + ")");
				}
			}
			Console.WriteLine($"{kv32.Key,-24} types={types} identical={identical} diffLines={diffLines} errors={errors}");
			foreach (var s in samples)
				Console.WriteLine("    " + s);
		}
		return 0;
	}

	// ==== lang-diff: M4 removed the 2.3 extension from the build, so the console can no longer
	// load it. This mode loads the archived/rebuilt 2.3 core assembly from an explicit path and
	// diffs each language's output against the 3.2 one. Usage:
	//   lang-diff <targetDll> <0xToken|TypeName> <dnspyBin> <path23Core>
	static int LangDiff(string targetDll, string tokenOrTypeName, string dnspyBin, string path23, string? out23 = null, string? out32 = null, string? langFilter = null) {
		InstallBinResolver(new[] { dnspyBin, path23 });
		PreloadContracts(dnspyBin);
		var module = ModuleDefMD.Load(targetDll);
		var languages23 = LoadProviders(Path.Combine(path23, "dnSpy.Decompiler.ILSpy.Core.dll"));
		var languages32 = LoadProviders(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		Console.WriteLine("2.3 languages: " + string.Join(", ", languages23.Values.Select(l => l.Item1)));
		Console.WriteLine("3.2 languages: " + string.Join(", ", languages32.Values.Select(l => l.Item1)));
		int rc = 0;
		foreach (var kv32 in languages32) {
			var suffix = kv32.Key.Replace(" (ILSpy 3.2)", string.Empty);
			if (!languages23.TryGetValue(suffix, out var l23)) {
				Console.WriteLine($"SKIP {kv32.Key} (no 2.3 counterpart)");
				continue;
			}
			var text23 = DecompileLanguage(l23.Item2, module, tokenOrTypeName);
			var text32 = DecompileLanguage(kv32.Value.Item2, module, tokenOrTypeName);
			if (text23 is null || text32 is null) {
				Console.WriteLine($"FAIL {kv32.Key}: decompile returned nothing (23={text23 is not null}, 32={text32 is not null})");
				rc = 1;
				continue;
			}
			var diff = DiffLineCount(text23, text32);
			// Optional: lang-diff <dll> <token|type> <dnspyBin> <path23> <out23> <out32>
			if (out23 is not null && out32 is not null && !string.IsNullOrEmpty(langFilter) && kv32.Key.StartsWith(langFilter, StringComparison.Ordinal)) {
				File.WriteAllText(out23, text23);
				File.WriteAllText(out32, text32);
			}
			Console.WriteLine($"{kv32.Key,-24} chars 23={text23.Length} 32={text32.Length} diffLines={diff}");
			if (diff != 0)
				rc = 1;
		}
		return rc;
	}

	static Dictionary<string, (string, dnSpy.Contracts.Decompiler.IDecompiler)> LoadProviders(string asmPath) {
		var result = new Dictionary<string, (string, dnSpy.Contracts.Decompiler.IDecompiler)>(StringComparer.Ordinal);
		if (!File.Exists(asmPath))
			return result;
		var asm = System.Reflection.Assembly.LoadFrom(asmPath);
		foreach (var type in asm.GetTypes()) {
			var isProvider = typeof(dnSpy.Contracts.Decompiler.IDecompilerProvider).IsAssignableFrom(type);
			var isCreator = typeof(dnSpy.Contracts.Decompiler.IDecompilerCreator).IsAssignableFrom(type);
			if ((!isProvider && !isCreator) || type.IsAbstract)
				continue;
			if (type.GetConstructor(Type.EmptyTypes) is null)
				continue;
			var langs = isProvider
				? ((dnSpy.Contracts.Decompiler.IDecompilerProvider)System.Activator.CreateInstance(type)).Create()
				: ((dnSpy.Contracts.Decompiler.IDecompilerCreator)System.Activator.CreateInstance(type)).Create();
			foreach (var lang in langs) {
				var name = lang.UniqueNameUI ?? lang.GenericNameUI ?? type.Name;
				result[name] = (name, lang);
			}
		}
		return result;
	}

	static string? DecompileLanguage(dnSpy.Contracts.Decompiler.IDecompiler lang, ModuleDefMD module, string tokenOrTypeName) {
		var output = new RecordingOutput();
		var ctx = new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true };
		try {
			if (tokenOrTypeName.StartsWith("0x", StringComparison.Ordinal)) {
				var member = module.ResolveToken(Convert.ToInt32(tokenOrTypeName, 16)) as MethodDef;
				if (member is null)
					return null;
				lang.Decompile(member, output, ctx);
			}
			else {
				var type = module.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, tokenOrTypeName, StringComparison.OrdinalIgnoreCase));
				if (type is null)
					return null;
				lang.Decompile(type, output, ctx);
			}
		}
		catch (Exception ex) {
			return "// EX: " + ex.GetType().Name + ": " + ex.Message;
		}
		return output.Text;
	}

	static int DiffLineCount(string a, string b) {
		var la = a.Replace("\r\n", "\n").Split('\n');
		var lb = b.Replace("\r\n", "\n").Split('\n');
		int diff = 0, i = 0, j = 0;
		while (i < la.Length || j < lb.Length) {
			if (i < la.Length && j < lb.Length && la[i] == lb[j]) { i++; j++; continue; }
			diff++;
			// crude LCS-free advance: prefer moving the side that matches next
			if (i + 1 < la.Length && j < lb.Length && la[i + 1] == lb[j]) i++;
			else if (j + 1 < lb.Length && i < la.Length && la[i] == lb[j + 1]) j++;
			else { i++; j++; }
		}
		return diff;
	}

	// ==== ilast-verify: the console's output discards custom data, so the only way to see the
	// ILAst writer's brace pairs and debug info is a recording IDecompilerOutput. The language
	// classes are internal to the extension, so they are created through reflection.
	static int ILAstVerify(string targetDll, string tokenHex, string dnspyBin, string dumpArg = null) {
		var module = ModuleDefMD.Load(targetDll);
		var token = int.Parse(tokenHex, System.Globalization.NumberStyles.HexNumber);
		var method = module.ResolveToken(token) as MethodDef;
		if (method is null) {
			Console.WriteLine("no method for token " + tokenHex);
			return 1;
		}
		var extAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		var langType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.ILAst.ILAstDecompiler32", true);
		var lang = (dnSpy.Contracts.Decompiler.IDecompiler)Activator.CreateInstance(langType, new object[] { "ILAst (ILSpy 3.2)" });
		var output = new RecordingOutput();
		lang.Decompile(method, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
		Console.WriteLine("method=" + method.FullName);
		Console.WriteLine("text=" + output.Text.Length + " chars, lines=" + output.Text.Split('\n').Length);
		Console.WriteLine("braces=" + output.Braces.Count);
		foreach (var b in output.Braces.Take(6))
			Console.WriteLine("  " + b);
		Console.WriteLine("debugInfo=" + (output.DebugInfo is null ? "none" : "statements=" + output.DebugInfo.Statements.Length));
		if (dumpArg == "dump")
			Console.WriteLine(output.Text);
		return output.Braces.Count > 0 && output.DebugInfo is not null ? 0 : 1;
	}

	// ==== mixed-verify: same idea as ilast-verify, for the Mixed language. Also dumps the
	// first colored writes so the XML doc comment coloring can be checked.
	static int MixedVerify(string targetDll, string tokenHex, string dnspyBin) {
		var module = ModuleDefMD.Load(targetDll);
		var token = int.Parse(tokenHex, System.Globalization.NumberStyles.HexNumber);
		var method = module.ResolveToken(token) as MethodDef;
		if (method is null) {
			Console.WriteLine("no method for token " + tokenHex);
			return 1;
		}
		var extAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		var langType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.Mixed.ILWithCSharpDecompiler32", true);
		var lang = (dnSpy.Contracts.Decompiler.IDecompiler)Activator.CreateInstance(langType, new object[] { "IL with C# (ILSpy 3.2)" });
		var output = new RecordingOutput();
		lang.Decompile(method, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
		Console.WriteLine("method=" + method.FullName);
		Console.WriteLine("text=" + output.Text.Length + " chars");
		Console.WriteLine("xmldoc lines=" + output.Text.Split('\n').Count(l => l.StartsWith("///")));
		Console.WriteLine("first colored writes:");
		foreach (var (text, color) in output.Colors.Take(14))
			Console.WriteLine("  [" + color + "] " + text.Replace("\n", "\\n"));
		return output.Text.Split('\n').Any(l => l.StartsWith("///")) ? 0 : 1;
	}

	sealed class RecordingOutput : dnSpy.Contracts.Decompiler.IDecompilerOutput {
		public readonly System.Text.StringBuilder Sb = new System.Text.StringBuilder();
		public readonly System.Collections.Generic.List<string> Braces = new System.Collections.Generic.List<string>();
		public readonly System.Collections.Generic.List<(string text, object color)> Colors = new System.Collections.Generic.List<(string, object)>();
		public dnSpy.Contracts.Decompiler.MethodDebugInfo DebugInfo;

		public string Text => Sb.ToString();
		public int Length => Sb.Length;
		public int NextPosition => Sb.Length;
		public bool UsesCustomData => true;

		public void IncreaseIndent() { }
		public void DecreaseIndent() { }
		public void WriteLine() => Sb.AppendLine();
		public void Write(string text, object color) { Sb.Append(text); Colors.Add((text, color)); }
		public void Write(string text, int index, int length, object color) { Sb.Append(text, index, length); Colors.Add((text.Substring(index, length), color)); }
		public void Write(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) => Sb.Append(text);
		public void Write(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) => Sb.Append(text, index, length);

		public void AddCustomData<TData>(string id, TData data) {
			if (id == dnSpy.Contracts.Decompiler.PredefinedCustomDataIds.CodeBracesRange && data is dnSpy.Contracts.Decompiler.CodeBracesRange range)
				Braces.Add(range.Left.Start + ".." + range.Right.Start + " " + range.Flags);
			else if (id == dnSpy.Contracts.Decompiler.PredefinedCustomDataIds.DebugInfo && data is dnSpy.Contracts.Decompiler.MethodDebugInfo debugInfo)
				DebugInfo = debugInfo;
		}
	}

	// ==== text dump honoring the IDecompilerOption contract: flip ShowTokenAndRvaComments
	// and UsingDeclarations off through Settings.Options (exactly what the dnSpy settings
	// page does) to prove the wiring reaches the output.
	static int IlDumpRun(string targetDll) {
		var module2 = ModuleDefMD.Load(targetDll);
		void Walk(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) {
				foreach (var m in t.Methods) {
					if (!m.HasBody || m.DeclaringType.Name != "<SaveMefStateAsync>d__23")
						continue;
					foreach (var inst in m.Body.Instructions)
						Console.WriteLine($"{inst.Offset:X4} {inst.OpCode} {inst.Operand}");
				}
				Walk(t.NestedTypes);
			}
		}
		Walk(module2.Types);
		return 0;
	}

	static int StateMachineDiagRun(string targetDll, string dnspyBin, int maxTypes) {
		var histogram = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
		int total = 0, rewritten = 0;
		var allReasons = new System.Collections.Generic.List<string>();
		var sink = (Action<string>)(reason => {
			allReasons.Add(reason);
			var key = reason.Substring(0, reason.IndexOf(' ') > 0 ? reason.IndexOf(' ') : reason.Length);
			histogram[key] = histogram.TryGetValue(key, out var n) ? n + 1 : 1;
		});
		var engineAsm = System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(dnspyBin, "ILSpy32.Decompiler.dll"));
		var sinkField = engineAsm.GetType("ICSharpCode.Decompiler.IL.ControlFlow.StateMachineDiagnostics", true)!
			.GetField("Sink", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		sinkField.SetValue(null, sink);
		var module = ModuleDefMD.Load(targetDll);
		var csharp = Create32CSharp(dnspyBin);
		var allT = new System.Collections.Generic.List<TypeDef>();
		void CollectAll(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) {
				allT.Add(t);
				CollectAll(t.NestedTypes);
			}
		}
		CollectAll(module.Types);
		var types = maxTypes < 0
			? allT.Where(t => t.FullName.Contains("__") || t.Methods.Any(mm => mm.HasBody && mm.Body.Instructions.Any(i => i.Operand is dnlib.DotNet.MemberRef mr2 && mr2.Name == "Start")))
			.Take(-maxTypes).ToList()
			: allT.Where(t => !t.Name.StartsWith("<")).Take(maxTypes).ToList();
		foreach (var type in types) {
			var output = new StringDecompilerOutput();
			try {
				csharp.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext());
				total++;
			}
			catch {
			}
		}
		sinkField.SetValue(null, null);
		Console.WriteLine($"types={types.Count} decompiled={total}");
		foreach (var kv in histogram.OrderByDescending(a => a.Value).Take(15))
			Console.WriteLine($"  {kv.Value,5} {kv.Key}");
		Console.WriteLine("  --- match-step histogram ---");
		foreach (var kv in histogram.Where(a => a.Key.Contains(":m") || a.Key.Contains(":match")).OrderBy(a => a.Key, StringComparer.Ordinal))
			Console.WriteLine($"  {kv.Value,5} {kv.Key}");
		Console.WriteLine($"  total rejections logged: {histogram.Values.Sum()} (rewrites succeeded for the rest)");
		foreach (var r in allReasons)
			if (r.Contains("yield:e") || r.Contains("move:b") || r.Contains("async:m4") || r.Contains("async:m5") || r.Contains("async:m28"))
				Console.WriteLine("    DETAIL " + r);
		return 0;
	}

	static int TextDumpWithOptions(string targetDll, string dnspyBin, string lang, string typeName, string outDir, bool applyOptions = true, bool queryOn = false) {
		Directory.CreateDirectory(outDir);
		bool wantCs = lang == "cs";
		var engines = new (string tag, dnSpy.Contracts.Decompiler.IDecompiler decompiler)[] {
			("32", wantCs ? Create32CSharp(dnspyBin) : Create32VB(dnspyBin)),
		};
		var module = ModuleDefMD.Load(targetDll);
		var types = module.Types
			.Where(t => !t.Name.StartsWith("<") && t.FullName.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
			.Take(2)
			.ToList();
		foreach (var type in types) {
			foreach (var (tag, dec) in engines) {
				var output = new TextCaptureOutput();
				try {
					// The settings page mutates the option values through IDecompilerOption.
					foreach (var opt in dec.Settings.Options) {
						if (opt.Guid == dnSpy.Contracts.Decompiler.DecompilerOptionConstants.ShowTokenAndRvaComments_GUID && !queryOn)
							opt.Value = false;
						if (opt.Guid == dnSpy.Contracts.Decompiler.DecompilerOptionConstants.UsingDeclarations_GUID && !queryOn)
							opt.Value = false;
						if (opt.Guid == dnSpy.Contracts.Decompiler.DecompilerOptionConstants.QueryExpressions_GUID && queryOn)
							opt.Value = true;
					}
					dec.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext());
				}
				catch (Exception ex) {
					Console.WriteLine($"  [{tag}] {type.FullName}: THREW {ex.GetType().Name}: {ex.Message}");
					continue;
				}
				var safe = type.FullName.Replace('`', '_').Replace('.', '_').Replace('<', '_').Replace('>', '_').Replace('/', '_');
				File.WriteAllText(Path.Combine(outDir, $"{tag}_{safe}.{(wantCs ? "cs" : "vb")}"), output.ToString());
				Console.WriteLine($"  [{tag}] {type.FullName}: {output.Length} chars");
			}
		}
		return 0;
	}

	// ==== text: decompile matching types with BOTH engines and write the full rendered
	// text (indentation and newlines included) to one file per engine, for direct diffing.
	static int TextDump(string targetDll, string dnspyBin, string lang, string typeName, string outDir) {
		Directory.CreateDirectory(outDir);
		bool wantCs = lang == "cs";
		var engines = new (string tag, dnSpy.Contracts.Decompiler.IDecompiler decompiler)[] {
			("32", wantCs ? Create32CSharp(dnspyBin) : Create32VB(dnspyBin)),
			("23", wantCs ? Create23(dnspyBin, "CSharp") : Create23(dnspyBin, "VisualBasic")),
		};
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) {
				allTypes.Add(t);
				Collect(t.NestedTypes);
			}
		}
		Collect(module.Types);
		var types = allTypes
			.Where(t => t.FullName.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)
			.Take(3)
			.ToList();
		foreach (var type in types) {
			foreach (var (tag, dec) in engines) {
				var output = new TextCaptureOutput();
				try {
					dec.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext());
				}
				catch (Exception ex) {
					Console.WriteLine($"  [{tag}] {type.FullName}: THREW {ex.GetType().Name}: {ex.Message}");
					continue;
				}
				var safe = type.FullName.Replace('`', '_').Replace('.', '_').Replace('<', '_').Replace('>', '_').Replace('/', '_');
				File.WriteAllText(Path.Combine(outDir, $"{tag}_{safe}.{(wantCs ? "cs" : "vb")}"), output.ToString());
				Console.WriteLine($"  [{tag}] {type.FullName}: {output.Length} chars");
			}
		}
		return 0;
	}

	sealed class TextCaptureOutput : dnSpy.Contracts.Decompiler.IDecompilerOutput {
		readonly System.Text.StringBuilder sb = new System.Text.StringBuilder();
		int indent;
		bool atLineStart = true;
		void WriteIndent() { if (atLineStart) { for (int i = 0; i < indent; i++) sb.Append('	'); atLineStart = false; } }
		public int Length => sb.Length;
		public int NextPosition => sb.Length;
		public bool UsesCustomData => false;
		public void IncreaseIndent() => indent++;
		public void DecreaseIndent() => indent--;
		public void WriteLine() { sb.AppendLine(); atLineStart = true; }
		public void Write(string text, object color) { WriteIndent(); sb.Append(text); }
		public void Write(string text, int index, int length, object color) { WriteIndent(); sb.Append(text, index, length); }
		public void Write(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { WriteIndent(); sb.Append(text); }
		public void WriteLine(string text, object color) { Write(text, color); WriteLine(); }
		public void WriteLine(string text, int index, int length, object color) { Write(text, index, length, color); WriteLine(); }
		public void WriteLine(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, reference, flags, color); WriteLine(); }
		public void Write(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, index, length, color); }
		public void WriteLine(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, index, length, reference, flags, color); WriteLine(); }
		public void AddCustomData<TData>(string id, TData data) { }
		public override string ToString() => sb.ToString();
	}

	// ==== colordump: decompile the same types with the 2.3 and 3.2 engines in one process
	// and dump every (text, color, hasReference) token to TSV for offline comparison.
	// The two engines live in differently-named assemblies (ICSharpCode.Decompiler vs
	// ILSpy32.Decompiler), so reflection-only loading has no identity conflicts; the color
	// singletons come from the shared dnSpy.Contracts.Logic, so field-name mapping is valid
	// for both.
	static int ColorDump(string targetDll, string dnspyBin, string lang, int maxTypes, string typeFilter, string outDir) {
		Directory.CreateDirectory(outDir);
		var resolveDirs = new[] { dnspyBin };
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
			foreach (var dir in resolveDirs) {
				var p = Path.Combine(dir, name);
				if (File.Exists(p)) return System.Reflection.Assembly.LoadFrom(p);
			}
			return null;
		};

		bool wantCs = lang == "cs";
		var engines = new (string tag, dnSpy.Contracts.Decompiler.IDecompiler decompiler)[] {
			("32", wantCs ? Create32CSharp(dnspyBin) : Create32VB(dnspyBin)),
			("23", wantCs ? Create23(dnspyBin, "CSharp") : Create23(dnspyBin, "VisualBasic")),
		};

		// Hook the 3.2 token writer's color-path diagnostic (reflection: internal static
		// Action<string> DnSpyTokenWriter.ColorPathSink).
		var engineAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		var sinkField = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DnSpyTokenWriter", throwOnError: false)?
			.GetField("ColorPathSink", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		sinkField?.SetValue(null, (Action<string>)(path => capturedColorPath = path));
		var vbSinkField = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.VisualBasic.VBTextOutputFormatter32", throwOnError: false)?
			.GetField("ColorPathSink", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		vbSinkField?.SetValue(null, (Action<string>)(path => capturedColorPath = path));
		if (Environment.GetEnvironmentVariable("M3PROBE_INVSINK") == "1") {
			var invSinkField = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DnSpyTokenWriter", throwOnError: false)?
				.GetField("InvProbeSink", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			invSinkField?.SetValue(null, (Action<string>)(line => Console.WriteLine("INVSINK " + line)));
			var mapperSinkField = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.Common.DnlibReferenceMapper", throwOnError: false)?
				.GetField("MapperSink", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			mapperSinkField?.SetValue(null, (Action<string>)(line => Console.WriteLine("MAPSINK " + line)));
		}

		var module = ModuleDefMD.Load(targetDll);
		var types = module.Types
			.Where(t => !t.Name.StartsWith("<"))
			.Where(t => t.FullName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
			.Take(maxTypes)
			.ToList();
		Console.WriteLine($"colordump {lang} {Path.GetFileName(targetDll)}: {types.Count} types -> {outDir}");

		var files = new System.Collections.Generic.Dictionary<string, StreamWriter>();
		var tokenCounts = new System.Collections.Generic.Dictionary<string, int>();
		try {
			foreach (var (tag, dec) in engines)
				files[tag] = new StreamWriter(Path.Combine(outDir, tag + ".tsv")) { AutoFlush = false };
			foreach (var type in types) {
				foreach (var (tag, dec) in engines) {
					var output = new ColorCaptureOutput();
					capturedColorPath = "";
					try {
						dec.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext());
					}
					catch (Exception ex) {
						Console.WriteLine($"  [{tag}] {type.FullName}: THREW {ex.GetType().Name}: {ex.Message}");
						tokenCounts[tag] = (tokenCounts.TryGetValue(tag, out var c0) ? c0 : 0) - 1;
						continue;
					}
					var w = files[tag];
					foreach (var e in output.Entries)
						w.WriteLine($"{Escape(type.FullName)}\t{Escape(e.Text)}\t{e.ColorName}\t{(e.Reference is not null ? 1 : 0)}\t{e.PathName}");
					tokenCounts[tag] = (tokenCounts.TryGetValue(tag, out var c1) ? c1 : 0) + 1;
				}
			}
		}
		finally {
			foreach (var f in files.Values) f.Dispose();
		}
		foreach (var (tag, count) in tokenCounts)
			Console.WriteLine($"  [{tag}] decompiled OK: {count} types");
		return 0;
	}

	static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

	static string capturedColorPath = "";

	static System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler> CreateVia(Type providerOrCreatorType) {
		object instance = Activator.CreateInstance(providerOrCreatorType, nonPublic: true)!;
		if (providerOrCreatorType.GetInterfaces().Any(i => i.Name == "IDecompilerProvider")) {
			return (System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler>)providerOrCreatorType.GetMethod("Create")!.Invoke(instance, null)!;
		}
		// IDecompilerCreator
		return (System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler>)providerOrCreatorType.GetMethod("Create")!.Invoke(instance, null)!;
	}

	static dnSpy.Contracts.Decompiler.IDecompiler Create23(string dnspyBin, string kindNs) {
		// M4 之后 2.3 不再进 dnSpy bin；可用 PROBE_ILSPY23_DIR 指向重建的 2.3 产物目录。
		var dir = ilspy23DirOverride ?? dnspyBin;
		var corePath = Path.Combine(dir, "dnSpy.Decompiler.ILSpy.Core.dll");
		if (!File.Exists(corePath)) {
			Console.WriteLine($"SKIP: {corePath} not found (the 2.3 extension is no longer built, M4).");
			Environment.Exit(2);
		}
		var coreAsm = System.Reflection.Assembly.LoadFrom(corePath);
		var providerType = coreAsm.GetType($"dnSpy.Decompiler.ILSpy.Core.{kindNs}.DecompilerProvider", throwOnError: true)!;
		var list = CreateVia(providerType).ToList();
		return Pick(list, wantCs: kindNs == "CSharp");
	}

	static dnSpy.Contracts.Decompiler.IDecompiler Create32(string dnspyBin, string kindNs) {
		var asm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		Console.WriteLine($"  [diag] engine asm: {asm.Location} ({new System.IO.FileInfo(asm.Location).Length} bytes)");
		// Only one creator class exists in the extension (CSharp ns); it yields every language.
		var creatorType = asm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
		var list = CreateVia(creatorType).ToList();
		if (kindNs == "IL") {
			foreach (var d in list) {
				var name = d.GenericNameUI ?? d.UniqueNameUI ?? "";
				if (name.StartsWith("IL") && !name.Contains("C#"))
					return d;
			}
			throw new InvalidOperationException($"no IL decompiler found in [{string.Join(", ", list.Select(d => d.GenericNameUI))}]");
		}
		return Pick(list, wantCs: kindNs == "CSharp");
	}

	static dnSpy.Contracts.Decompiler.IDecompiler Create32CSharp(string dnspyBin) => Create32(dnspyBin, "CSharp");
	static dnSpy.Contracts.Decompiler.IDecompiler Create32VB(string dnspyBin) => Create32(dnspyBin, "VisualBasic");
	static dnSpy.Contracts.Decompiler.IDecompiler Create32IL(string dnspyBin) => Create32(dnspyBin, "IL");

	// ==== spdump: decompile with the 3.2 C# engine and dump the statement-level debug
	// info (SourceStatement = ILSpan + TextSpan) that the engine attaches, with a text
	// excerpt per statement to eyeball the IL<->text alignment.
	sealed class SpCaptureOutput : dnSpy.Contracts.Decompiler.IDecompilerOutput {
		readonly System.Text.StringBuilder sb = new System.Text.StringBuilder();
		int indent;
		bool atLineStart = true;
		public readonly System.Collections.Generic.List<dnSpy.Contracts.Decompiler.MethodDebugInfo> DebugInfos = new();
		public int Length => sb.Length;
		public int NextPosition => sb.Length;
		public bool UsesCustomData => true;
		void WriteIndent() { if (atLineStart) { for (int i = 0; i < indent; i++) sb.Append('\t'); atLineStart = false; } }
		public void IncreaseIndent() => indent++;
		public void DecreaseIndent() => indent--;
		public void WriteLine() { sb.AppendLine(); atLineStart = true; }
		public void Write(string text, object color) { WriteIndent(); sb.Append(text); }
		public void Write(string text, int index, int length, object color) { WriteIndent(); sb.Append(text, index, length); }
		public void Write(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { WriteIndent(); sb.Append(text); }
		public void WriteLine(string text, object color) { Write(text, color); WriteLine(); }
		public void WriteLine(string text, int index, int length, object color) { Write(text, index, length, color); WriteLine(); }
		public void WriteLine(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, reference, flags, color); WriteLine(); }
		public void Write(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, index, length, color); }
		public void WriteLine(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, index, length, reference, flags, color); WriteLine(); }
		public void AddCustomData<TData>(string id, TData data) {
			if (data is dnSpy.Contracts.Decompiler.MethodDebugInfo mdi) DebugInfos.Add(mdi);
		}
		public override string ToString() => sb.ToString();
	}

	// ==== options-wiring: flip every exposed IDecompilerOption and assert the decompiled
	// output actually responds - the same path the dnSpy settings page drives.
	static int OptionsWiring(string targetDll, string dnspyBin, string typeFilter) {
		var dec = Create32CSharp(dnspyBin);
		var settings = dec.Settings;
		var options = settings.Options.ToList();
		Console.WriteLine($"exposed options: {options.Count}");

		var module = ModuleDefMD.Load(targetDll);
		var type = module.Types.First(t => t.FullName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0);
		dnSpy.Contracts.Decompiler.IDecompilerOutput Baseline() {
			var o = new StringDecompilerOutput();
			dec.Decompile(type, o, new dnSpy.Contracts.Decompiler.DecompilationContext());
			return o;
		}
		string Hash(dnSpy.Contracts.Decompiler.IDecompilerOutput o) {
			var sb = new System.Text.StringBuilder();
			for (var i = 0; i < o.Length; i++) {
				var ch = o.ToString().Length > i ? o.ToString()[i] : '\0';
				sb.Append(((int)ch % 251).ToString("X2"));
			}
			return sb.ToString();
		}
		int changes = 0, same = 0;
		foreach (var opt in options) {
			var original = opt.Value;
			bool flipped;
			if (opt.Type == typeof(bool))
				flipped = !(bool)original;
			else if (opt.Type == typeof(int))
				flipped = false;
			else
				flipped = false;
			if (opt.Type != typeof(bool)) {
				Console.WriteLine($"  SKIP  {opt.Guid:B} type={opt.Type.Name}");
				continue;
			}
			try {
				opt.Value = flipped;
				if (Environment.GetEnvironmentVariable("DNSPY_CONSOLE_DIAG") == "1")
					Console.Error.WriteLine($"WIRESINK {opt.Guid:B} flip->{flipped} readback={opt.Value}");
				var before = Baseline();
				string h1, h2;
				{
					var tmp = new StringDecompilerOutput();
					dec.Decompile(type, tmp, new dnSpy.Contracts.Decompiler.DecompilationContext());
					h1 = tmp.ToString();
				}
				opt.Value = original;
				{
					var tmp = new StringDecompilerOutput();
					dec.Decompile(type, tmp, new dnSpy.Contracts.Decompiler.DecompilationContext());
					h2 = tmp.ToString();
				}
				var changed = h1 != h2;
				if (changed) changes++;
				else same++;
				Console.WriteLine($"  {(changed ? "CHANGED" : "SAME   ")}  {(opt.Name ?? opt.Guid.ToString("B"))} -> {flipped}" + (changed ? "" : $" (len {h1.Length} vs {h2.Length})"));
			}
			catch (Exception ex) {
				opt.Value = original;
				Console.WriteLine($"  ERROR  {opt.Guid:B}: {ex.GetType().Name}: {ex.Message}");
			}
		}
		Console.WriteLine($"SUMMARY changed={changes} same={same}");
		return 0;
	}

	// ==== dbg-map: exercise the two dnSpy consumer APIs (MethodDebugInfo
	// GetSourceStatementByCodeOffset / GetSourceStatementByTextOffset) against real 3.2
	// output, the same calls the debugger highlight and the bookmark/locate paths make.
	static int DbgMap(string spTestDll, string dnspyBin) {
		var failures = 0;
		void Check(bool ok, string what) {
			Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
			if (!ok)
				failures++;
		}
		var dec32 = Create32CSharp(dnspyBin);
		var dec23 = Create23(dnspyBin, "CSharp");
		var decVB = Create32VB(dnspyBin);
		var module = ModuleDefMD.Load(spTestDll);
		var statementVariety = module.Types.First(t => t.FullName == "SpTestNamespace.StatementVariety");

		foreach (var (tag, dec) in new[] { ("3.2", dec32), ("2.3", dec23), ("3.2VB", decVB) }) {
			var output = new SpCaptureOutput();
			dec.Decompile(statementVariety, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
			var text = output.ToString();
			Console.WriteLine($"[{tag}] debugInfos={output.DebugInfos.Count}");
			foreach (var mdi in output.DebugInfos) {
				var method = mdi.Method;
				if (method?.Body is null)
					continue;
				// 1) IL coverage: every instruction offset must land in some statement.
				int ilHits = 0, ilTotal = 0;
				foreach (var ins in method.Body.Instructions) {
					ilTotal++;
					if (mdi.GetSourceStatementByCodeOffset((uint)ins.Offset) is not null)
						ilHits++;
				}
				var ilRatio = ilTotal == 0 ? 1.0 : (double)ilHits / ilTotal;
				Console.WriteLine($"  IL coverage {method.Name}: {ilHits}/{ilTotal} ({ilRatio:P0})");

				// 2) Text coverage: the midpoint of every statement's TextSpan must resolve
				//    through GetSourceStatementByTextOffset (the bookmark/locate path).
				int textHits = 0, textTotal = 0;
				foreach (var st in mdi.Statements) {
					var mid = st.TextSpan.Start + (st.TextSpan.Length / 2);
					var lineStart = text.LastIndexOf('\n', Math.Max(0, mid - 1));
					if (lineStart < 0)
						lineStart = 0;
					var nl = text.IndexOf('\n', mid);
					var lineEnd = nl < 0 ? text.Length : nl + 1;
					textTotal++;
					if (mdi.GetSourceStatementByTextOffset(lineStart, lineEnd, mid) is not null)
						textHits++;
				}
				var textRatio = textTotal == 0 ? 1.0 : (double)textHits / textTotal;
				Check(textRatio >= 0.9, $"Text coverage {method.Name}: {textHits}/{textTotal} ({textRatio:P0})");
			}
		}
		return 0;

		static string[] textLines(string s) => s.Split('\n');
	}

	// ==== engine-tests: regression assertions for the 3.2 coverage work, run against the
	// statement-variety sample assembly. Every test from the coverage sessions is asserted
	// automatically (statement SPs, locals, gating, nested types, state machines).
	static int EngineTests(string spTestDll, string dnspyBin) {
		var failures = 0;
		void Check(bool ok, string what) {
			Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
			if (!ok)
				failures++;
		}

		// sample assembly must be the Debug build (richer locals)
		var dec32cs = Create32CSharp(dnspyBin);
		var dec32vb = Create32VB(dnspyBin);
		var module = ModuleDefMD.Load(spTestDll);
		var statementVariety = module.Types.First(t => t.FullName == "SpTestNamespace.StatementVariety");
		var iteratorVariety = module.Types.First(t => t.FullName == "SpTestNamespace.IteratorVariety");
		var outerWithNested = module.Types.First(t => t.FullName == "SpTestNamespace.OuterWithNested");
		var innerType = outerWithNested.NestedTypes.First(t => t.Name == "Inner");
		var nestedMachine = iteratorVariety.NestedTypes.First(t => t.Name.Contains("NestedLoops"));

		dnSpy.Contracts.Decompiler.IDecompilerOutput Decompile(dnSpy.Contracts.Decompiler.IDecompiler dec, TypeDef type, bool calcSp) {
			var o = new StringDecompilerOutput();
			dec.Decompile(type, o, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = calcSp });
			return o;
		}
		void DumpStatements(dnSpy.Contracts.Decompiler.IDecompiler dec, TypeDef type, string tag, System.Action<dnSpy.Contracts.Decompiler.MethodDebugInfo> assert) {
			var o = new SpCaptureOutput();
			dec.Decompile(type, o, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
			var mdi = o.DebugInfos.FirstOrDefault(d => d.Method.Name.Contains(tag));
			assert(mdi!);
		}

		// 1. C# statement-level SPs: SimpleStatements has one SP per statement with aligned text.
		DumpStatements(dec32cs, statementVariety, "SimpleStatements", mdi => {
			Check(mdi!.Statements.Length >= 4, $"1a C# SimpleStatements statement SPs ({mdi.Statements.Length})");
			var first = mdi.Statements.FirstOrDefault();
			var text = mdi.Statements.Length > 0 ? Decompile(dec32cs, statementVariety, true).ToString() : "";
			var span = first!.TextSpan;
			var excerpt = text.Substring(span.Start, Math.Min(span.Length, 60));
			// The bridge reads the PDB when it can (to match the 2.3 engine's local names), so the
			// local is 'sum' when symbols are available and the generated 'num' when they are not.
			Check(excerpt.Contains("num") || excerpt.Contains("sum"), $"1b C# first SP text aligned ('{excerpt.Trim()}' contains 'num'/'sum')");
		});

		// 2. VB statement-level SPs.
		DumpStatements(dec32vb, statementVariety, "SimpleStatements", mdi => {
			Check(mdi!.Statements.Length >= 4, $"2a VB SimpleStatements statement SPs ({mdi.Statements.Length})");
		});

		// 3. C# debug locals: SimpleStatements maps 'num' by slot.
		DumpStatements(dec32cs, statementVariety, "SimpleStatements", mdi => {
			var locals = mdi!.Scope.Locals;
			// See 1b: 'sum' with symbols, generated 'num' without.
			Check(locals!.Any(l => l.Name == "num" || l.Name == "sum"), $"3a C# SimpleStatements locals contain 'num'/'sum' ({string.Join(",", locals!.Select(l => l.Name))})");
		});

		// 4. VB debug locals: LinqAndLock has >= 3 (stringReader/enumerator/item).
		DumpStatements(dec32vb, statementVariety, "LinqAndLock", mdi => {
			var locals = mdi!.Scope.Locals;
			Check(locals!.Length >= 3, $"4a VB LinqAndLock locals >= 3 ({locals!.Length})");
		});

		// 5. CalculateILSpans gating: false gives zero debug info on both engines.
		foreach (var (name, dec) in new[] { ("5a C#", dec32cs), ("5b VB", dec32vb) }) {
			var o = Decompile(dec, statementVariety, calcSp: false);
			var sp = new SpCaptureOutput();
			dec.Decompile(statementVariety, sp, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = false });
			Check(sp.DebugInfos.Count == 0, $"{name} CalculateILSpans=false -> 0 debug infos ({sp.DebugInfos.Count})");
		}

		// 5c/5d. IL coverage after CompactILSpans must be 100% on both 3.2 backends,
		// and the 2.3 engine stays at its own (>=99%) level as the comparison baseline.
		double ILCoverage(dnSpy.Contracts.Decompiler.IDecompiler dec, TypeDef type) {
			var o = new SpCaptureOutput();
			dec.Decompile(type, o, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
			int hits = 0, total = 0;
			foreach (var mdi in o.DebugInfos) {
				if (mdi.Method?.Body is null)
					continue;
				foreach (var ins in mdi.Method.Body.Instructions) {
					total++;
					if (mdi.GetSourceStatementByCodeOffset((uint)ins.Offset) is not null)
						hits++;
				}
			}
			return total == 0 ? 100.0 : hits * 100.0 / total;
		}
		Check(ILCoverage(dec32cs, statementVariety) >= 100.0, "5c C# IL coverage = 100% after CompactILSpans");
		Check(ILCoverage(dec32vb, statementVariety) >= 100.0, "5d VB IL coverage = 100% after CompactILSpans");
		{
			var dec23 = Create23(dnspyBin, "CSharp");
			Check(ILCoverage(dec23, statementVariety) >= 97.0, "5e 2.3 IL coverage >= 97% (comparison baseline; 97.5 measured on the Debug sample)");
		}

		// 6. Nested types: decompiling Inner renders Inner itself, not the whole declaring type.
		{
			var o = Decompile(dec32cs, innerType, calcSp: false);
			var text = o.ToString();
			Check(text.Contains("class Inner"), "6a C# Inner renders its own class");
			Check(!text.Contains("OuterMethod"), "6b C# Inner does not leak OuterMethod");
			Check(text.Contains("// Token: 0x"), "6c C# Inner has Token comments (knownType binding)");
		}

		// 7. State machine types: nested MoveNext renders with full members.
		{
			var o = Decompile(dec32cs, nestedMachine, calcSp: false);
			var text = o.ToString();
			Check(text.Contains("MoveNext"), "7a C# nested state machine renders MoveNext");
			Check(text.Contains("private sealed class") || text.Contains("sealed class"), "7b C# state machine renders as full class");
			Check(!text.Contains("decompilation failed"), "7c C# state machine has no failure note");
		}

		// 8. VB backend renders the state machine type with statement-level detail.
		{
			var o = new SpCaptureOutput();
			dec32vb.Decompile(nestedMachine, o, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
			var stmtLevel = o.DebugInfos.Count(d => d.Statements.Length > 1);
			Check(o.DebugInfos.Count > 0 && stmtLevel > 0, $"8a VB state machine has statement SPs ({o.DebugInfos.Count} infos, {stmtLevel} statement-level)");
		}

		// 10. Member sorting (SortMembers option): with the option on, the 2.3 grouping
		// (methods, properties, events, fields, nested types) applies and each group is
		// sorted with the 2.3 comparers; with it off, the metadata order is kept.
		{
			var settings = dec32cs.Settings;
			var sortOption = settings.Options.First(o => o.Guid == dnSpy.Contracts.Decompiler.DecompilerOptionConstants.SortMembers_GUID);
			var original = (bool)sortOption.Value;

			sortOption.Value = true;
			var sortedText = Decompile(dec32cs, statementVariety, calcSp: false).ToString();
			if (Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1") {
				foreach (var line2 in sortedText.Replace("\r\n", "\n").Split('\n'))
					if (line2.Contains("AsyncWork") || line2.Contains("ControlFlow") || line2.Contains("SimpleStatements") || line2.Contains("CountTo") || line2.Contains("LinqAndLock") || line2.Contains("private ") || line2.Contains("Async") || line2.Contains("Task"))
						Console.Error.WriteLine("SORTED-MEMBER " + line2.Trim().Substring(0, System.Math.Min(line2.Trim().Length, 60)));
			}
			sortOption.Value = original;
			var unsortedText = Decompile(dec32cs, statementVariety, calcSp: false).ToString();

			// Methods group comes first and is alphabetical; the fields group follows.
			var firstMethod = sortedText.IndexOf("AsyncWork(", StringComparison.Ordinal);
			var secondMethod = sortedText.IndexOf("ControlFlow(", StringComparison.Ordinal);
			var fieldsIdx = sortedText.IndexOf("private int counter", StringComparison.Ordinal);
			Check(firstMethod >= 0 && secondMethod > firstMethod, $"10a methods sorted alphabetically (AsyncWork@{firstMethod} < ControlFlow@{secondMethod})");
			Check(fieldsIdx > secondMethod, $"10b fields group after methods group (counter@{fieldsIdx})");
			Check(unsortedText.IndexOf("public int SimpleStatements", StringComparison.Ordinal) >= 0, "10c unsorted output keeps original members");
		}

		// 9. Reference highlighting: every call site of the same external method must produce
		// the same reference instance (or one dnSpy's comparer accepts), or highlight and
		// go-to-both-sites break. WriteLine is called from four statements in the sample.
		{
			var o = new ColorCaptureOutput();
			dec32cs.Decompile(statementVariety, o, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = false });
			var writeLineRefs = o.Entries.Where(e => e.Reference is dnlib.DotNet.IMemberRef mr && mr.Name == "WriteLine")
				.Select(e => (dnlib.DotNet.IMemberRef)e.Reference)
				.ToList();
			Check(writeLineRefs.Count >= 2, $"9a multiple WriteLine call sites captured ({writeLineRefs.Count} sites)");
			Check(writeLineRefs.Distinct().Count() == 1, $"9b all WriteLine sites share one reference instance ({writeLineRefs.Distinct().Count()} distinct)");

			// The synthesized path (table miss): two structurally identical MemberRefUsers
			// must compare equal through dnlib's SigComparer - the same comparer dnSpy's
			// SpanDataReferenceInfoExtensions.CompareReferences falls back to after its own
			// name/declaring-type fast checks.
			var corlibRef = new dnlib.DotNet.AssemblyRefUser("System.Runtime");
			var listTypeRef = new dnlib.DotNet.TypeRefUser(module, "System.Collections.Generic", "List`1", corlibRef);
			var sig = dnlib.DotNet.MethodSig.CreateInstance(module.CorLibTypes.Void, new dnlib.DotNet.GenericInstSig(new dnlib.DotNet.ClassSig(listTypeRef), module.CorLibTypes.String));
			var siteA = new dnlib.DotNet.MemberRefUser(module, "Add", sig, listTypeRef);
			var siteB = new dnlib.DotNet.MemberRefUser(module, "Add", sig, listTypeRef);
			var comparer = new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable);
			Check(comparer.Equals(siteA, siteB), "9c dnlib SigComparer matches two structurally identical synthesized MemberRefs");
			var siteC = new dnlib.DotNet.MemberRefUser(module, "Add", dnlib.DotNet.MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Object), listTypeRef);
			Check(!comparer.Equals(siteA, siteC), "9d SigComparer distinguishes synthesized overloads");
		}

		// 11. Query-lowered lambda 'this' (audit §8.63 末「复发与定位」): IntroduceQueryExpressions moves
		// the lambda body into the query clause, so the lambda's IL instructions are visited
		// while the outer statement's sequence point is being built. The builder must skip those
		// cross-function instructions; otherwise the statement's ILSpan starts inside the
		// previous statement's range (the lambda's 'this' load offset leaks into the outer SP).
		DumpStatements(dec32cs, statementVariety, "LambdaThis", mdi => {
			var stmts = mdi!.Statements;
			Check(stmts.Length == 4, $"11a C# LambdaThis statement count ({stmts.Length})");
			var overlap = false;
			for (int i = 1; i < stmts.Length; i++) {
				if (stmts[i].ILSpan.Start < stmts[i - 1].ILSpan.End)
					overlap = true;
			}
			Check(!overlap, "11b C# LambdaThis statement ILSpans are non-overlapping");
			var text = Decompile(dec32cs, statementVariety, true).ToString();
			var rStmt = stmts.Length > 1 ? stmts[1] : default;
			var excerpt = rStmt.TextSpan.Length > 0 ? text.Substring(rStmt.TextSpan.Start, Math.Min(rStmt.TextSpan.Length, 40)) : "";
			Check(excerpt.Contains("from a in columns"), $"11c C# LambdaThis second statement is the query ('{excerpt.Replace("\r", "").Replace("\n", " ")}')");
		});

		Console.WriteLine(failures == 0 ? "ENGINE-TESTS: ALL PASS" : $"ENGINE-TESTS: {failures} FAILURES");
		return failures == 0 ? 0 : 1;
	}

	// ==== dbg-map2: IL/text coverage report over an arbitrary target assembly with a
	// chosen engine (32cs/32vb/23cs) - the general-purpose shape of dbg-map for real
	// libraries with large methods and deep control flow.
	static int DbgMap2(string targetDll, string dnspyBin, string engine, string typeFilter, int maxTypes) {
		var dec = engine switch {
			"32vb" => Create32VB(dnspyBin),
			"23cs" => Create23(dnspyBin, "CSharp"),
			_ => Create32CSharp(dnspyBin),
		};
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) {
				allTypes.Add(t);
				Collect(t.NestedTypes);
			}
		}
		Collect(module.Types);
		var types = allTypes
			.Where(t => !t.Name.StartsWith("<") || typeFilter.Length > 0)
			.Where(t => t.FullName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
			.Take(maxTypes)
			.ToList();
		int worstIL = 100, worstText = 100;
		string worstILName = "-", worstTextName = "-";
		foreach (var type in types) {
			var output = new SpCaptureOutput();
			try {
				dec.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
			}
			catch (Exception ex) {
				Console.WriteLine($"TYPE {type.FullName}: THREW {ex.GetType().Name}: {ex.Message}");
				continue;
			}
			var text = output.ToString();
			foreach (var mdi in output.DebugInfos) {
				var method = mdi.Method;
				if (method?.Body is null || method.Body.Instructions.Count == 0)
					continue;
				int ilHits = 0, ilTotal = 0;
				foreach (var ins in method.Body.Instructions) {
					ilTotal++;
					if (mdi.GetSourceStatementByCodeOffset((uint)ins.Offset) is not null)
						ilHits++;
				}
				var ilRatio = (double)ilHits * 100 / ilTotal;
				if (ilTotal >= 8 && ilRatio < worstIL) {
					worstIL = (int)ilRatio;
					worstILName = $"{type.Name}.{method.Name}";
				}
				int textHits = 0;
				foreach (var st in mdi.Statements) {
					var mid = st.TextSpan.Start + (st.TextSpan.Length / 2);
					var lineStart = text.LastIndexOf('\n', Math.Max(0, mid - 1));
					if (lineStart < 0)
						lineStart = 0;
					var nl = text.IndexOf('\n', mid);
					var lineEnd = nl < 0 ? text.Length : nl + 1;
					if (mdi.GetSourceStatementByTextOffset(lineStart, lineEnd, mid) is not null)
						textHits++;
				}
				var textRatio = mdi.Statements.Length == 0 ? 100.0 : (double)textHits * 100 / mdi.Statements.Length;
				if (mdi.Statements.Length >= 3 && textRatio < worstText) {
					worstText = (int)textRatio;
					worstTextName = $"{type.Name}.{method.Name}";
				}
				if (ilRatio < 100 || textRatio < 100)
					Console.WriteLine($"  {type.Name}.{method.Name}: IL {ilHits}/{ilTotal} ({ilRatio:F0}%) text {textHits}/{mdi.Statements.Length} ({textRatio:F0}%)");
			}
		}
		Console.WriteLine($"WORST IL coverage: {worstIL}% ({worstILName}); WORST text coverage: {worstText}% ({worstTextName})");
		return 0;
	}

	static int SpDumpLang(string[] args) {
		var dnspyBin = args[2];
		// Engine selectors: vb (3.2 VB), 23 (2.3 C#), 23vb (2.3 VB), default 3.2 C# - the
		// hoistcheck comparison needs both engines on the same sample.
		var dec = args.Length > 4 && args[4] == "vb" ? Create32VB(dnspyBin)
			: args.Length > 4 && args[4] == "23" ? Create23(dnspyBin, "CSharp")
			: args.Length > 4 && args[4] == "23vb" ? Create23(dnspyBin, "VisualBasic")
			: Create32CSharp(dnspyBin);
		spDumpCalculateILSpans = Environment.GetEnvironmentVariable("M3PROBE_CALC_SP") != "0";
		return SpDump(args[1], dnspyBin, args[3], dec);
	}

	static bool spDumpCalculateILSpans = true;

	static int SpDump(string targetDll, string dnspyBin, string typeFilter, dnSpy.Contracts.Decompiler.IDecompiler dec) {
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) {
				allTypes.Add(t);
				Collect(t.NestedTypes);
			}
		}
		Collect(module.Types);
		int totalMethods = 0, stmtMethods = 0, totalStmts = 0;
		foreach (var type in allTypes.Where(t => t.FullName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0).Take(5)) {
			var output = new SpCaptureOutput();
			try {
				dec.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = spDumpCalculateILSpans });
			}
			catch (Exception ex) {
				Console.WriteLine($"TYPE {type.FullName}: THREW {ex.GetType().Name}: {ex.Message}");
				continue;
			}
			Console.WriteLine($"TYPE {type.FullName}: text={output.Length} chars, debugInfos={output.DebugInfos.Count}");
			foreach (var mdi in output.DebugInfos) {
				totalMethods++;
				var stmts = mdi.Statements;
				totalStmts += stmts.Length;
				var locals = mdi.Scope?.Locals?.Length ?? 0;
				var shape = stmts.Length == 0 ? "EMPTY" : (stmts.Length == 1 ? "WHOLE " : "STMT  ");
				if (stmts.Length == 0) {
					Console.WriteLine($"  {shape} {mdi.Method.Name} locals={locals}");
					continue;
				}
				if (stmts.Length > 1) stmtMethods++;
				Console.WriteLine($"  {shape} {mdi.Method.Name}: {stmts.Length} statements, locals={locals}");
				var text = output.ToString();
				int shown = 0;
				foreach (var st in stmts) {
					if (shown++ >= 6) { Console.WriteLine($"      ... +{stmts.Length - shown + 1} more"); break; }
					string excerpt;
					var s = st.TextSpan.Start;
					var e = Math.Min(st.TextSpan.End, text.Length);
					if (s >= 0 && s < text.Length && e > s) {
						excerpt = text.Substring(s, Math.Min(e - s, 70)).Replace("\r", "\\r").Replace("\n", "\\n");
						if (e - s > 70) excerpt += "...";
					}
					else
						excerpt = "<out of range>";
					Console.WriteLine($"      IL[{st.ILSpan.Start}..{st.ILSpan.End}) text[{st.TextSpan.Start}..{st.TextSpan.End}) '{excerpt}'");
				}
			}
		}
		Console.WriteLine($"SUMMARY methods-with-debuginfo={totalMethods} methods-with-statements={stmtMethods} total-statements={totalStmts}");
		return 0;
	}

	static dnSpy.Contracts.Decompiler.IDecompiler Pick(System.Collections.Generic.List<dnSpy.Contracts.Decompiler.IDecompiler> list, bool wantCs) {
		foreach (var d in list) {
			var name = d.GenericNameUI ?? d.UniqueNameUI ?? "";
			if (wantCs && name.StartsWith("C#")) return d;
			if (!wantCs && name.StartsWith("Visual Basic")) return d;
		}
		throw new InvalidOperationException($"no {(wantCs ? "C#" : "VB")} decompiler found in [{string.Join(", ", list.Select(d => d.GenericNameUI))}]");
	}

	sealed class ColorCaptureOutput : dnSpy.Contracts.Decompiler.IDecompilerOutput {
		readonly System.Text.StringBuilder sb = new System.Text.StringBuilder();
		int indent;
		bool atLineStart = true;

		public sealed class Entry {
			public string Text = "";
			public string ColorName = "";
			public object? Reference;
			public string PathName = "";
		}
		public readonly System.Collections.Generic.List<Entry> Entries = new();

		// Maps a color instance to its BoxedTextColor field name; both engines share the
		// same Contracts assembly instance, so reference identity is reliable. BoxedTextColor's
		// fields are `object` holding boxed TextColor enum values.
		static System.Collections.Generic.Dictionary<object, string>? colorNameMap;
		static string ColorNameOf(object? color) {
			if (color is null) return "(null)";
			if (colorNameMap is null) {
				colorNameMap = new System.Collections.Generic.Dictionary<object, string>();
				var boxedType = color.GetType().Assembly.GetType("dnSpy.Contracts.Text.BoxedTextColor", throwOnError: false);
				if (boxedType is not null) {
					foreach (var f in boxedType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)) {
						if (f.FieldType != typeof(object)) continue;
						var v = f.GetValue(null);
						if (v is not null && !colorNameMap.ContainsKey(v))
							colorNameMap[v] = f.Name;
					}
				}
			}
			return colorNameMap.TryGetValue(color, out var name) ? name : color.GetType().Name + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(color).ToString("x");
		}

		public int Length => sb.Length;
		public int NextPosition => sb.Length;
		public bool UsesCustomData => false;
		void WriteIndent() { if (atLineStart) { for (int i = 0; i < indent; i++) sb.Append('\t'); atLineStart = false; } }
		public void IncreaseIndent() => indent++;
		public void DecreaseIndent() => indent--;
		public void WriteLine() { sb.AppendLine(); atLineStart = true; }
		public void Write(string text, object color) {
			WriteIndent(); sb.Append(text);
			Entries.Add(new Entry { Text = text, ColorName = ColorNameOf(color), PathName = capturedColorPath });
		}
		public void Write(string text, int index, int length, object color) {
			WriteIndent(); var s = text.Substring(index, length); sb.Append(s);
			Entries.Add(new Entry { Text = s, ColorName = ColorNameOf(color), PathName = capturedColorPath });
		}
		public void Write(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			WriteIndent(); sb.Append(text);
			Entries.Add(new Entry { Text = text, ColorName = ColorNameOf(color), Reference = reference, PathName = capturedColorPath + (reference is null ? "" : "/r:" + reference.GetType().Name) });
		}
		public void WriteLine(string text, object color) { Write(text, color); WriteLine(); }
		public void WriteLine(string text, int index, int length, object color) { Write(text, index, length, color); WriteLine(); }
		public void WriteLine(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) { Write(text, reference, flags, color); WriteLine(); }
		public void Write(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			WriteIndent(); var s = text.Substring(index, length); sb.Append(s);
			Entries.Add(new Entry { Text = s, ColorName = ColorNameOf(color), Reference = reference, PathName = capturedColorPath + (reference is null ? "" : "/r:" + reference.GetType().Name) });
		}
		public void WriteLine(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			Write(text, index, length, reference, flags, color); WriteLine();
		}
		public void AddCustomData<TData>(string id, TData data) { }
		public override string ToString() => sb.ToString();
	}

	static int MakeReproAssembly(string path) {
		var module = new ModuleDefUser("ReproAssembly", Guid.NewGuid());
		module.Kind = ModuleKind.Dll;
		var assembly = new AssemblyDefUser("ReproAssembly", new Version(1, 0, 0, 0));
		assembly.Modules.Add(module);

		var smRef = new AssemblyRefUser("System.Management") {
			Version = new Version(4, 0, 0, 0),
			PublicKeyOrToken = new PublicKeyToken(new byte[] { 0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a }),
		};
		var corlib = new AssemblyRefUser("mscorlib") { Version = new Version(4, 0, 0, 0) };

		var moType = new TypeRefUser(module, "System.Management", "ManagementObject", smRef);
		var objectType = new TypeRefUser(module, "System", "Object", corlib);

		var testType = new TypeDefUser("Repro", "Test", objectType);
		module.Types.Add(testType);
		var field = new FieldDefUser("managementObject", new FieldSig(new ClassSig(moType)), dnlib.DotNet.FieldAttributes.Public);
		testType.Fields.Add(field);

		// A method with a real body, so method-level debug spans have something to map.
		var int32Type = new TypeRefUser(module, "System", "Int32", corlib);
		var method = new MethodDefUser("Compute", MethodSig.CreateStatic(new ClassSig(int32Type)),
			dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static);
		method.Body = new CilBody();
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		testType.Methods.Add(method);

		module.Write(path, new ModuleWriterOptions(module));
		Console.WriteLine("repro written: " + path);
		return 0;
	}

	static int CountReferences(string engineDllPath, string targetDll, string dnspyBinDir) {
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
			foreach (var dir in new[] { Path.GetDirectoryName(engineDllPath)!, dnspyBinDir }) {
				var p = Path.Combine(dir, name);
				if (File.Exists(p))
					return System.Reflection.Assembly.LoadFrom(p);
			}
			return null;
		};
		var engineAsm = System.Reflection.Assembly.LoadFrom(engineDllPath);
		var creatorType = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
		var decompilers = ((System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler>)creatorType.GetMethod("Create")!.Invoke(Activator.CreateInstance(creatorType), null)!).ToList();
		var csharp = decompilers.First(d => d.GenericGuid == new Guid("F5A318D4-4B2A-48D2-AE33-F4D2B1EFF4B0"));
		var vb = decompilers.First(d => d.GenericGuid == new Guid("B6849618-8239-4FBB-8DFF-D45EB023C193"));

		var module = ModuleDefMD.Load(targetDll);
		int csRefs = 0, vbRefs = 0, types = 0;
		foreach (var type in module.Types.Where(t => !t.Name.StartsWith("<")).Take(8)) {
			var csOut = new StringDecompilerOutput();
			csharp.Decompile(type, csOut, new dnSpy.Contracts.Decompiler.DecompilationContext());
			var vbOut = new StringDecompilerOutput();
			vb.Decompile(type, vbOut, new dnSpy.Contracts.Decompiler.DecompilationContext());
			Console.WriteLine($"{type.Name}: csRefs={csOut.referenceCount} vbRefs={vbOut.referenceCount}");
			csRefs += csOut.referenceCount;
			vbRefs += vbOut.referenceCount;
			types++;
		}
		Console.WriteLine($"TOTAL ({types} types): csRefs={csRefs} vbRefs={vbRefs}");
		return 0;
	}

	static int DecompileNamedType(string engineDllPath, string targetDll, string dnspyBinDir, string typeName) {
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
			foreach (var dir in new[] { Path.GetDirectoryName(engineDllPath)!, dnspyBinDir }) {
				var p = Path.Combine(dir, name);
				if (File.Exists(p))
					return System.Reflection.Assembly.LoadFrom(p);
			}
			return null;
		};
		var engineAsm = System.Reflection.Assembly.LoadFrom(engineDllPath);
		var creatorType = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
		var decompilers = ((System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler>)creatorType.GetMethod("Create")!.Invoke(Activator.CreateInstance(creatorType), null)!).ToList();
		var csharp = decompilers.First(d => d.GenericGuid == new Guid("F5A318D4-4B2A-48D2-AE33-F4D2B1EFF4B0"));

		var module = ModuleDefMD.Load(targetDll);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		foreach (var type in module.Types) {
			if (type.FullName.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			var output = new StringDecompilerOutput();
			var sw2 = System.Diagnostics.Stopwatch.StartNew();
			try {
				csharp.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext());
			}
			catch (Exception ex) {
				Console.WriteLine($"TYPE {type.FullName}: THREW {ex.GetType().Name}: {ex.Message}");
				continue;
			}
			sw2.Stop();
			var text = output.ToString();
			Console.WriteLine($"TYPE {type.FullName}: {text.Length} chars, {sw2.ElapsedMilliseconds} ms, refs={output.referenceCount}, failed-note={text.Contains("decompilation failed")}");
			var preview = text.Length > 200 ? text.Substring(0, 200) : text;
			Console.WriteLine("---- preview ----");
			Console.WriteLine(preview);
		}
		sw.Stop();
		Console.WriteLine($"done in {sw.ElapsedMilliseconds} ms");
		return 0;
	}

	static int TestExportPaths(string engineDllPath, string reproAssemblyPath, string dnspyBinDir) {
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
			foreach (var dir in new[] { Path.GetDirectoryName(engineDllPath)!, dnspyBinDir }) {
				var p = Path.Combine(dir, name);
				if (File.Exists(p))
					return System.Reflection.Assembly.LoadFrom(p);
			}
			return null;
		};
		var engineAsm = System.Reflection.Assembly.LoadFrom(engineDllPath);
		var creatorType = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
		var decompilers = ((System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler>)creatorType.GetMethod("Create")!.Invoke(Activator.CreateInstance(creatorType), null)!).ToList();
		var csharp = decompilers.First(d => d.GenericGuid == new Guid("F5A318D4-4B2A-48D2-AE33-F4D2B1EFF4B0"));
		var vb = decompilers.First(d => d.GenericGuid == new Guid("B6849618-8239-4FBB-8DFF-D45EB023C193"));
		Console.WriteLine("cs ProjectFileExtension: " + csharp.ProjectFileExtension);
		Console.WriteLine("vb ProjectFileExtension: " + vb.ProjectFileExtension);
		Console.WriteLine("cs CanDecompile PartialType/AssemblyInfo/TypeMethods: " +
			csharp.CanDecompile(dnSpy.Contracts.Decompiler.DecompilationType.PartialType) + "/" +
			csharp.CanDecompile(dnSpy.Contracts.Decompiler.DecompilationType.AssemblyInfo) + "/" +
			csharp.CanDecompile(dnSpy.Contracts.Decompiler.DecompilationType.TypeMethods));

		var module = ModuleDefMD.Load(reproAssemblyPath);
		var type = module.Types.First(t => t.Name == "Test");

		foreach (var (name, decompiler) in new[] { ("C#", csharp), ("VB", vb) }) {
			var partialOut = new StringDecompilerOutput();
			decompiler.Decompile(dnSpy.Contracts.Decompiler.DecompilationType.PartialType,
				new dnSpy.Contracts.Decompiler.DecompilePartialType(partialOut, new dnSpy.Contracts.Decompiler.DecompilationContext(), type));
			var asmOut = new StringDecompilerOutput();
			decompiler.Decompile(dnSpy.Contracts.Decompiler.DecompilationType.AssemblyInfo,
				new dnSpy.Contracts.Decompiler.DecompileAssemblyInfo(asmOut, new dnSpy.Contracts.Decompiler.DecompilationContext(), module));
			Console.WriteLine($"[{name}] PartialType: {partialOut.ToString().Length} chars, failed={partialOut.ToString().Contains("failed")}");
			Console.WriteLine($"[{name}] AssemblyInfo: {asmOut.ToString().Length} chars, failed={asmOut.ToString().Contains("failed")}");
		}
		return 0;
	}

	sealed class StringDecompilerOutput : dnSpy.Contracts.Decompiler.IDecompilerOutput {
		readonly System.Text.StringBuilder sb = new System.Text.StringBuilder();
		int indentLevel;
		bool atLineStart = true;

		public int Length => sb.Length;
		public int NextPosition => sb.Length;
		public bool UsesCustomData => false;

		void WriteIndent() {
			if (atLineStart) {
				for (int i = 0; i < indentLevel; i++)
					sb.Append('\t');
				atLineStart = false;
			}
		}

		public void IncreaseIndent() => indentLevel++;
		public void DecreaseIndent() => indentLevel--;
		public void WriteLine() {
			sb.AppendLine();
			atLineStart = true;
		}
		public void Write(string text, object color) {
			WriteIndent();
			sb.Append(text);
		}
		public void Write(string text, int index, int length, object color) {
			WriteIndent();
			sb.Append(text, index, length);
		}
		public void Write(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			Write(text, color);
			// surface the reference so the test can assert navigation works
			lastReference = reference;
			if (reference is not null)
				referenceCount++;
		}
		public void WriteLine(string text, object color) {
			Write(text, color);
			WriteLine();
		}
		public void WriteLine(string text, int index, int length, object color) {
			Write(text, index, length, color);
			WriteLine();
		}
		public void WriteLine(string text, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			Write(text, reference, flags, color);
			WriteLine();
		}
		public void Write(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			Write(text, index, length, color);
			lastReference = reference;
		}
		public void WriteLine(string text, int index, int length, object reference, dnSpy.Contracts.Decompiler.DecompilerReferenceFlags flags, object color) {
			Write(text, index, length, reference, flags, color);
			WriteLine();
		}
		public System.Collections.Generic.List<object> customData = new System.Collections.Generic.List<object>();
		public void AddCustomData<TData>(string id, TData data) {
			if (data is dnSpy.Contracts.Decompiler.MethodDebugInfo mdi)
				customData.Add(mdi);
		}
		public override string ToString() => sb.ToString();
		public object? lastReference;
		public int referenceCount;
	}

	static int DecompileRepro(string engineDllPath, string reproAssemblyPath, string dnspyBinDir) {
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
			var name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
			foreach (var dir in new[] { Path.GetDirectoryName(engineDllPath)!, dnspyBinDir }) {
				var p = Path.Combine(dir, name);
				if (File.Exists(p))
					return System.Reflection.Assembly.LoadFrom(p);
			}
			return null;
		};
		var engineAsm = System.Reflection.Assembly.LoadFrom(engineDllPath);
		var creatorType = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
		var creator = Activator.CreateInstance(creatorType)!;
		var decompilers = ((System.Collections.Generic.IEnumerable<dnSpy.Contracts.Decompiler.IDecompiler>)creatorType.GetMethod("Create")!.Invoke(creator, null)!).ToList();
		var csharp = decompilers.First(d => d.GenericGuid == new Guid("F5A318D4-4B2A-48D2-AE33-F4D2B1EFF4B0"));

		var module = ModuleDefMD.Load(reproAssemblyPath);
		var type = module.Types.First(t => t.Name == "Test");
		var output = new StringDecompilerOutput();
		csharp.Decompile(type, output, new dnSpy.Contracts.Decompiler.DecompilationContext());
		var text = output.ToString();
		Console.WriteLine(text);

		// Single-member decompile: does the member's XML doc comment appear?
		var member = type.Fields.First(f => f.Name == "managementObject");
		var methodOutput = new StringDecompilerOutput();
		csharp.Decompile(member, methodOutput, new dnSpy.Contracts.Decompiler.DecompilationContext());
		Console.WriteLine("=== single method ===");
		Console.WriteLine(methodOutput.ToString());
		Console.WriteLine("=== method has doc: " + methodOutput.ToString().Contains("///") + " ===");

		// VB side: decompile the type and count non-null references
		var vb = decompilers.First(d => d.GenericGuid == new Guid("B6849618-8239-4FBB-8DFF-D45EB023C193"));
		var vbOutput = new StringDecompilerOutput();
		vb.Decompile(type, vbOutput, new dnSpy.Contracts.Decompiler.DecompilationContext());
		Console.WriteLine("=== VB output ===");
		Console.WriteLine(vbOutput.ToString());
		Console.WriteLine("=== VB last reference: " + (vbOutput.lastReference?.ToString() ?? "NULL") + " ===");
		if (text.Contains("decompilation failed") || text.Contains("decompilation degraded")) {
			Console.WriteLine("FAIL: degraded output");
			return 1;
		}
		var hasDoc = text.Contains("///");
		var hasRef = output.lastReference is not null;
		Console.WriteLine(hasDoc ? "PASS: XML doc comments present" : "NOTE: no XML doc comments");
		Console.WriteLine($"CS reference count: {output.referenceCount}");
		Console.WriteLine($"CS debug infos: {output.customData.Count}");
		Console.WriteLine($"VB reference count: {vbOutput.referenceCount} (last: {vbOutput.lastReference?.ToString() ?? "NULL"})");
		Console.WriteLine($"VB debug infos: {vbOutput.customData.Count}");
		return 0;
	}

	static int AsyncProbe(string targetDll, string dnspyBin, string methodName) {
		var dec = Create32CSharp(dnspyBin);
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) { allTypes.Add(t); Collect(t.NestedTypes); }
		}
		Collect(module.Types);
		MethodDef? target = null;
		foreach (var t in allTypes) {
			foreach (var m in t.Methods) {
				if (m.Name == methodName && m.HasBody) { target = m; break; }
			}
			if (target is not null) break;
		}
		if (target is null) { Console.WriteLine("method not found: " + methodName); return 1; }
		Console.WriteLine("kickoff: " + target.FullName + " body=" + target.Body.Instructions.Count + " ins, ilrange=[0x" + target.Body.Instructions.First().Offset.ToString("X") + "-0x" + target.Body.Instructions.Last().Offset.ToString("X") + "]");
		var smType = target.DeclaringType.NestedTypes.FirstOrDefault(t => t.Name?.String?.StartsWith("<" + target.Name + ">") == true);
		MethodDef? moveNext = smType?.FindMethod("MoveNext");
		if (moveNext is not null)
			Console.WriteLine("moveNext: " + smType!.FullName + " body=" + moveNext.Body!.Instructions.Count + " ins, ilrange=[0x" + moveNext.Body.Instructions.First().Offset.ToString("X") + "-0x" + moveNext.Body.Instructions.Last().Offset.ToString("X") + "]");
		var output = new SpCaptureOutput();
		dec.Decompile(target, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
		Console.WriteLine("member-level: debugInfos=" + output.DebugInfos.Count + " textLen=" + output.Length);
		var engineAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
		var twType = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DnSpyTokenWriter");
		Console.WriteLine("  GetCecil calls=" + twType?.GetField("GetCecilCalls", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null) + " failures=" + twType?.GetField("GetCecilFailures", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null));
		var typeOutput = new SpCaptureOutput();
		dec.Decompile(target.DeclaringType, typeOutput, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
		Console.WriteLine("type-level:   debugInfos=" + typeOutput.DebugInfos.Count + " textLen=" + typeOutput.Length);
		var twType2 = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DnSpyTokenWriter");
		var twT = engineAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DnSpyTokenWriter");
		var mapperT = engineAsm.GetTypes().FirstOrDefault(t => t.Name == "DnlibReferenceMapper");
		if (mapperT is null)
			Console.WriteLine("  mapper type not found");
		var fields = new[] {
			("resolveRef", twT, "ResolveReferenceTicks"),
			("resolveToken", mapperT, "ResolveTokenTicks"),
			("profAnn", twT, "ProfAnnTicks"),
			("profTrr", twT, "ProfTrrTicks"),
			("profIType", twT, "ProfITypeTicks"),
			("profInvoc", twT, "ProfInvocTicks"),
			("profSymbol", twT, "ProfSymbolTicks"),
			("profFallback", twT, "ProfFallbackTicks"),
		};
		foreach (var (label, type, field) in fields) {
			var f = type?.GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			var ticks = f?.GetValue(null);
			Console.WriteLine($"  PROF[{label}] ticks=" + ticks + " ms~=" + (ticks is long t ? t / 10000.0 : -1));
		}
		var head = output.ToString();
		foreach (var l in head.Split('\n').Where(l => l.Contains("failed") || l.Contains("Exception")).Take(3))
			Console.WriteLine("  !! " + l.TrimEnd());
		Console.WriteLine("  | tail3: " + string.Join(" || ", head.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(3).Select(l => l.Trim())));
		foreach (var mdi in output.DebugInfos) {
			Console.WriteLine("  mdi.Method=" + mdi.Method.FullName + " stmts=" + mdi.Statements.Length + " kind=" + mdi.StateMachineKind + " kickoff=" + (mdi.KickoffMethod?.FullName ?? "null") + " asyncInfo=" + (mdi.AsyncInfo is not null));
			if (mdi.Statements.Length > 0 && Environment.GetEnvironmentVariable("DNSPY_ILSPY32_DIAG") == "1") {
				var mod = ModuleDefMD.Load(targetDll);
				var smt = target.DeclaringType.NestedTypes.FirstOrDefault(t => t.Name?.String?.StartsWith("<" + target.Name + ">") == true);
				var mn = smt?.FindMethod("MoveNext");
				if (mn?.Body is not null) {
					var ins2 = mn.Body.Instructions;
					for (int i = 0; i < ins2.Count; i++) {
						if (ins2[i].Offset >= 0x88 && ins2[i].Offset <= 0xB5)
							Console.WriteLine("    IL " + ins2[i].Offset.ToString("X4") + " " + ins2[i].OpCode + " " + (ins2[i].Operand?.ToString() ?? ""));
					}
				}
			}
		if (mdi.Statements.Length > 0) {
				var first = mdi.Statements[0];
				var last = mdi.Statements[mdi.Statements.Length - 1];
				Console.WriteLine("    stmt IL span: 0x" + first.ILSpan.Start.ToString("X") + " .. 0x" + last.ILSpan.End.ToString("X"));
			}
		}
		return 0;
	}

	/// <summary>
	/// Decompile-time comparison between the 2.3 and 3.2 engines over the same sampled types:
	/// total and median wall clock per engine, type-level decompiles with CalculateILSpans off
	/// (the display path).
	/// </summary>
	static int Bench(string targetDll, string dnspyBin, int sampleCount) {
		// bench 的一半是 2.3 对照；M4 后 2.3 不再进 bin，可用 PROBE_ILSPY23_DIR 指定。
		if (!File.Exists(Path.Combine(ilspy23DirOverride ?? dnspyBin, "dnSpy.Decompiler.ILSpy.Core.dll"))) {
			Console.WriteLine("SKIP: dnSpy.Decompiler.ILSpy.Core.dll not found (the 2.3 extension is no longer built, M4).");
			return 2;
		}
		InstallBinResolver(ilspy23DirOverride is null ? new[] { dnspyBin } : new[] { dnspyBin, ilspy23DirOverride });
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) { allTypes.Add(t); Collect(t.NestedTypes); }
		}
		Collect(module.Types);
		// Skip the compiler-generated noise types; deterministic stride sample.
		var types = allTypes.Where(t => !t.Name!.String!.StartsWith("<") && !t.FullName.Contains("PrivateImplementationDetails"))
			.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
		var stride = Math.Max(1, types.Count / sampleCount);
		var sample = new System.Collections.Generic.List<TypeDef>();
		for (int i = 0; i < types.Count && sample.Count < sampleCount; i += stride)
			sample.Add(types[i]);

		var engines = new (string tag, dnSpy.Contracts.Decompiler.IDecompiler dec)[] {
			("3.2-CSharp", Create32CSharp(dnspyBin)),
			("2.3-CSharp", Create23(dnspyBin, "CSharp")),
			("3.2-VB", Create32VB(dnspyBin)),
			("2.3-VB", Create23(dnspyBin, "VisualBasic")),
		};
		foreach (var (tag, dec) in engines) {
			// Warm-up pass (engine init, assembly load) then a timed pass.
			foreach (var t in sample.Take(10)) {
				var w = new SpCaptureOutput();
				try { dec.Decompile(t, w, new dnSpy.Contracts.Decompiler.DecompilationContext()); } catch { }
			}
			var times = new System.Collections.Generic.List<long>();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var perType = new System.Collections.Generic.List<(string Name, long Ms, int Calls, int Fails)>();
			var engineAsmB = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll"));
			var twTypeB = engineAsmB.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DnSpyTokenWriter");
			var fCalls = twTypeB?.GetField("GetCecilCalls", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			var fFail = twTypeB?.GetField("GetCecilFailures", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			foreach (var t in sample) {
				var c0 = fCalls is null ? 0 : (int)fCalls.GetValue(null)!;
				var f0 = fFail is null ? 0 : (int)fFail.GetValue(null)!;
				var one = System.Diagnostics.Stopwatch.StartNew();
				var w = new SpCaptureOutput();
				try { dec.Decompile(t, w, new dnSpy.Contracts.Decompiler.DecompilationContext()); } catch { }
				one.Stop();
				times.Add(one.ElapsedMilliseconds);
				var c1 = fCalls is null ? 0 : (int)fCalls.GetValue(null)!;
				var f1 = fFail is null ? 0 : (int)fFail.GetValue(null)!;
				perType.Add((t.FullName!, one.ElapsedMilliseconds, c1 - c0, f1 - f0));
			}
			sw.Stop();
			times.Sort();
			var median = times.Count > 0 ? times[times.Count / 2] : 0;
			Console.WriteLine($"{tag}: total {sw.ElapsedMilliseconds} ms for {times.Count} types, median {median} ms/type, max {times.LastOrDefault()} ms");
			foreach (var (name, ms, calls, fails) in perType.OrderByDescending(p => p.Ms).Take(6))
				Console.WriteLine($"  slow {ms,6} ms  {name}  cecilCalls={calls} fails={fails}");
		}
		return 0;
	}

	/// <summary>
	/// Verifies the IL view's debug-info chain: decompile one method with the 3.2 IL language
	/// and dump the statement-level mapping (one statement per instruction, the 2.3 shape).
	/// </summary>
	static int ILProbe(string targetDll, string dnspyBin, string methodName) {
		var dec = Create32IL(dnspyBin);
		Console.WriteLine("engine: " + (dec.UniqueNameUI ?? "?"));
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) { allTypes.Add(t); Collect(t.NestedTypes); }
		}
		Collect(module.Types);
		MethodDef? target = null;
		foreach (var t in allTypes) {
			foreach (var m in t.Methods) {
				if (m.Name == methodName && m.HasBody) { target = m; break; }
			}
			if (target is not null) break;
		}
		if (target is null) { Console.WriteLine("method not found: " + methodName); return 1; }
		var output = new SpCaptureOutput();
		dec.Decompile(target, output, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true });
		Console.WriteLine("debugInfos=" + output.DebugInfos.Count + " textLen=" + output.Length);
		foreach (var mdi in output.DebugInfos) {
			Console.WriteLine("  mdi.Method=" + mdi.Method.FullName + " stmts=" + mdi.Statements.Length);
			var first = mdi.Statements[0];
			var last = mdi.Statements[mdi.Statements.Length - 1];
			Console.WriteLine("    IL span: 0x" + first.ILSpan.Start.ToString("X") + " .. 0x" + last.ILSpan.End.ToString("X") + " (body " + mdi.Method.Body!.Instructions.Count + " ins, 0x" + mdi.Method.Body.Instructions.Last().Offset.ToString("X") + ")");
		}
		return 0;
	}

	/// <summary>
	/// AsyncInfo numeric comparison between the 2.3 and 3.2 engines over every async kickoff
	/// in the assembly: step pairs (yield offset, resume offset), SetResultOffset and the
	/// builder field must agree, since both map the same MoveNext IL and dnSpy's async
	/// stepper steps wrong on any disagreement.
	/// </summary>
	static int AsyncDiff(string targetDll, string dnspyBin, int max) {
		var dec32 = Create32CSharp(dnspyBin);
		var dec23 = Create23(dnspyBin, "CSharp");
		var module = ModuleDefMD.Load(targetDll);
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> list) {
			foreach (var t in list) { allTypes.Add(t); Collect(t.NestedTypes); }
		}
		Collect(module.Types);

		int compared = 0, agree = 0, only32 = 0, only23 = 0, both = 0;
		foreach (var t in allTypes) {
			foreach (var m in t.Methods) {
				if (!m.HasBody)
					continue;
				var smType = t.NestedTypes.FirstOrDefault(n => n.Name?.String?.StartsWith("<" + m.Name + ">") == true &&
					n.Interfaces.Any(i => i.Interface?.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine"));
				if (smType is null)
					continue;
				if (compared >= max)
					return 0;
				compared++;
				var out32 = new SpCaptureOutput();
				var out23 = new SpCaptureOutput();
				try { dec32.Decompile(m, out32, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true }); } catch { }
				try { dec23.Decompile(m, out23, new dnSpy.Contracts.Decompiler.DecompilationContext { CalculateILSpans = true }); } catch { }
				var a32 = out32.DebugInfos.FirstOrDefault(d => d.AsyncInfo is not null)?.AsyncInfo;
				var a23 = out23.DebugInfos.FirstOrDefault(d => d.AsyncInfo is not null)?.AsyncInfo;
				if (a32 is null && a23 is null)
					continue;
				if (a32 is not null && a23 is null) {
					only32++;
					Console.WriteLine($"ONLY-32 {t.FullName}::{m.Name} steps={a32!.StepInfos.Length}");
					continue;
				}
				if (a32 is null && a23 is not null) {
					only23++;
					Console.WriteLine($"ONLY-23 {t.FullName}::{m.Name} steps={a23!.StepInfos.Length}");
					continue;
				}
				both++;
				var s32 = a32!.StepInfos.Select(si => (si.YieldOffset, si.ResumeMethod.Name + "@" + (si.ResumeMethod.MDToken.Raw).ToString("X"), si.ResumeOffset)).OrderBy(p => p.Item1).ToList();
				var s23 = a23!.StepInfos.Select(si => (si.YieldOffset, si.ResumeMethod.Name + "@" + (si.ResumeMethod.MDToken.Raw).ToString("X"), si.ResumeOffset)).OrderBy(p => p.Item1).ToList();
				var setResultOk = a32.SetResultOffset == a23.SetResultOffset;
				if (s32.Count == s23.Count && s32.Zip(s23, (x, y) => x.Item1 == y.Item1 && x.Item3 == y.Item3).All(ok => ok) && setResultOk) {
					agree++;
					continue;
				}
				Console.WriteLine($"DIFF {t.FullName}::{m.Name}: 32={s32.Count} steps (setResult 0x{a32.SetResultOffset:X}) 23={s23.Count} steps (setResult 0x{a23.SetResultOffset:X}) setResultOk={setResultOk}");
				foreach (var pair in s32.Zip(s23, (x, y) => (x, y)).Where(p => p.x.Item1 != p.y.Item1 || p.x.Item3 != p.y.Item3).Take(4))
					Console.WriteLine($"    32 yield=0x{pair.x.Item1:X} resume=0x{pair.x.Item3:X} | 23 yield=0x{pair.y.Item1:X} resume=0x{pair.y.Item3:X}");
			}
		}
		Console.WriteLine($"SUMMARY compared={compared} agree={agree} only32={only32} only23={only23} both={both}");
		return 0;
	}

	// ==== ilprint-diff: 用反射分别实例化 2.3 的 dnSpy.Decompiler.ILSpy.IL.SimpleILPrinter 与
	// 3.2 的 dnSpy.Decompiler.ILSpy32.IL.SimpleILPrinter32，对同一批 dnlib 对象（方法/字段/
	// 属性/事件/类型/各种 TypeSig/MethodSig）输出 IL 风格文本并逐字 diff，验证
	// DnlibILText32 与 2.3 的 DisassemblerHelpers 行为一致。
	static int ILPrintDiff(string targetDll, string dnspyBin, string out23Path, string out32Path) {
		// M4 removed the 2.3 extension from the build; the comparison is only possible while a
		// copy of dnSpy.Decompiler.ILSpy.x.dll is still next to the 3.2 one.
		if (!File.Exists(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy.x.dll"))) {
			Console.WriteLine("SKIP: dnSpy.Decompiler.ILSpy.x.dll not found (the 2.3 extension is no longer built, M4).");
			return 2;
		}
		var printer23 = CreateSimpleILPrinter(dnspyBin, "dnSpy.Decompiler.ILSpy.x.dll", "dnSpy.Decompiler.ILSpy.IL.SimpleILPrinter");
		var printer32 = CreateSimpleILPrinter(dnspyBin, "dnSpy.Decompiler.ILSpy32.x.dll", "dnSpy.Decompiler.ILSpy32.IL.SimpleILPrinter32");
		var module = ModuleDefMD.Load(targetDll);
		var samples = CollectILSamples(module);
		var texts23 = RenderILSamples(printer23, samples);
		var texts32 = RenderILSamples(printer32, samples);
		File.WriteAllText(out23Path, RenderILSamplesText(samples, texts23));
		File.WriteAllText(out32Path, RenderILSamplesText(samples, texts32));
		// 逐样本比较：属性/事件是多行文本，逐行比较会因结构差异错位，掩盖真正的写入器差异。
		int diffSamples = 0;
		for (int i = 0; i < samples.Count; i++) {
			if (texts23[i] != texts32[i]) {
				diffSamples++;
				if (diffSamples <= 30) {
					Console.WriteLine($"DIFF sample {i} [{samples[i].Label}]");
					Console.WriteLine("  23: " + texts23[i].Replace("\n", "\\n"));
					Console.WriteLine("  32: " + texts32[i].Replace("\n", "\\n"));
				}
			}
		}
		var byKind = new System.Collections.Generic.SortedDictionary<string, int>();
		var totalByKind = new System.Collections.Generic.SortedDictionary<string, int>();
		for (int i = 0; i < samples.Count; i++) {
			var kind = samples[i].Label.Split(' ')[0];
			totalByKind[kind] = totalByKind.TryGetValue(kind, out var c) ? c + 1 : 1;
			if (texts23[i] != texts32[i])
				byKind[kind] = byKind.TryGetValue(kind, out var c2) ? c2 + 1 : 1;
		}
		Console.WriteLine($"samples={samples.Count} diffSamples={diffSamples} text23={RenderILSamplesText(samples, texts23).Length} text32={RenderILSamplesText(samples, texts32).Length}");
		foreach (var kv in totalByKind)
			Console.WriteLine($"  {kv.Key}: {kv.Value} samples, {(byKind.TryGetValue(kv.Key, out var d) ? d : 0)} diff");
		return diffSamples == 0 ? 0 : 1;
	}

	static dnSpy.Contracts.Decompiler.ISimpleILPrinter CreateSimpleILPrinter(string dnspyBin, string assemblyFileName, string typeName) {
		var asm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, assemblyFileName));
		var type = asm.GetType(typeName, throwOnError: true)!;
		return (dnSpy.Contracts.Decompiler.ISimpleILPrinter)Activator.CreateInstance(type, nonPublic: true)!;
	}

	static System.Collections.Generic.List<string> RenderILSamples(dnSpy.Contracts.Decompiler.ISimpleILPrinter printer, System.Collections.Generic.List<ILSample> samples) {
		var list = new System.Collections.Generic.List<string>(samples.Count);
		foreach (var s in samples) {
			var output = new StringDecompilerOutput();
			s.Write(printer, output);
			list.Add(output.ToString());
		}
		return list;
	}

	static string RenderILSamplesText(System.Collections.Generic.List<ILSample> samples, System.Collections.Generic.List<string> texts) {
		var sb = new System.Text.StringBuilder();
		for (int i = 0; i < samples.Count; i++) {
			sb.AppendLine("### " + samples[i].Label);
			sb.Append(texts[i]);
			sb.AppendLine();
		}
		return sb.ToString();
	}

	sealed class ILSample {
		public readonly string Label;
		public readonly Action<dnSpy.Contracts.Decompiler.ISimpleILPrinter, dnSpy.Contracts.Decompiler.IDecompilerOutput> Write;
		public ILSample(string label, Action<dnSpy.Contracts.Decompiler.ISimpleILPrinter, dnSpy.Contracts.Decompiler.IDecompilerOutput> write) {
			Label = label;
			Write = write;
		}
	}

	static void AddILSampleTypeSig(System.Collections.Generic.List<ILSample> list, string label, TypeSig type) {
		if (type == null)
			return;
		list.Add(new ILSample(label, (p, o) => p.Write(o, type)));
	}

	static System.Collections.Generic.List<ILSample> CollectILSamples(ModuleDefMD module) {
		var list = new System.Collections.Generic.List<ILSample>();
		var allTypes = new System.Collections.Generic.List<TypeDef>();
		void Collect(System.Collections.Generic.IEnumerable<TypeDef> types) {
			foreach (var t in types) { allTypes.Add(t); Collect(t.NestedTypes); }
		}
		Collect(module.Types);

		foreach (var t in allTypes) {
			list.Add(new ILSample("type " + t.FullName, (p, o) => p.Write(o, t)));
			if (t.BaseType != null)
				list.Add(new ILSample("base " + t.FullName, (p, o) => p.Write(o, t.BaseType)));
			foreach (var i in t.Interfaces)
				if (i.Interface != null)
					list.Add(new ILSample("iface " + t.FullName, (p, o) => p.Write(o, i.Interface)));
			foreach (var m in t.Methods) {
				list.Add(new ILSample("method " + m.FullName, (p, o) => p.Write(o, m)));
				if (m.MethodSig != null) {
					list.Add(new ILSample("methodsig " + m.FullName, (p, o) => p.Write(o, m.MethodSig)));
					AddILSampleTypeSig(list, "ret " + m.FullName, m.MethodSig.RetType);
					foreach (var pt in m.MethodSig.Params)
						AddILSampleTypeSig(list, "param " + m.FullName, pt);
				}
			}
			foreach (var f in t.Fields) {
				list.Add(new ILSample("field " + f.FullName, (p, o) => p.Write(o, f)));
				if (f.FieldSig != null)
					AddILSampleTypeSig(list, "fieldtype " + f.FullName, f.FieldSig.Type);
			}
			foreach (var prop in t.Properties)
				list.Add(new ILSample("prop " + prop.FullName, (p, o) => p.Write(o, prop)));
			foreach (var ev in t.Events)
				list.Add(new ILSample("event " + ev.FullName, (p, o) => p.Write(o, ev)));
		}

		// 合成 TypeSig，覆盖指针/byref/数组(含 sizes)/泛型实例/modopt/modreq/fnptr/sentinel/
		// pinned/泛型参数等分支。
		var corlib = module.CorLibTypes;
		var int32 = corlib.Int32;
		var obj = corlib.Object;
		var genericClass = allTypes.FirstOrDefault(t => t.GenericParameters.Count > 0 && !t.IsValueType);
		var genericStruct = allTypes.FirstOrDefault(t => t.GenericParameters.Count > 0 && t.IsValueType);
		AddILSampleTypeSig(list, "ptr", new PtrSig(int32));
		AddILSampleTypeSig(list, "byref", new ByRefSig(int32));
		AddILSampleTypeSig(list, "szarray", new SZArraySig(int32));
		AddILSampleTypeSig(list, "array1", new ArraySig(int32, 1));
		AddILSampleTypeSig(list, "array2", new ArraySig(int32, 2));
		AddILSampleTypeSig(list, "array-sizes", new ArraySig(int32, 2, new uint[] { 3, 4 }, new int[] { 1, 0 }));
		AddILSampleTypeSig(list, "pinned", new PinnedSig(int32));
		AddILSampleTypeSig(list, "sentinel", new SentinelSig());
		AddILSampleTypeSig(list, "modopt", new CModOptSig(obj.ToTypeDefOrRef(), int32));
		AddILSampleTypeSig(list, "modreq", new CModReqdSig(obj.ToTypeDefOrRef(), int32));
		AddILSampleTypeSig(list, "fnptr", new FnPtrSig(new MethodSig(CallingConvention.Default, 0, corlib.Void, new TypeSig[] { int32 })));
		AddILSampleTypeSig(list, "genericvar", new GenericVar(0));
		AddILSampleTypeSig(list, "genericmvar", new GenericMVar(0));
		if (genericClass != null)
			AddILSampleTypeSig(list, "genericinst-class", new GenericInstSig(new ClassSig(genericClass), new TypeSig[] { int32, obj }));
		if (genericStruct != null)
			AddILSampleTypeSig(list, "genericinst-struct", new GenericInstSig(new ValueTypeSig(genericStruct), new TypeSig[] { int32 }));

		// 泛型方法实例（MethodSpec），覆盖 WriteTo(IMethod) 的 <...> 分支。
		var genericMethod = allTypes.SelectMany(t => t.Methods).FirstOrDefault(m => m.GenericParameters.Count > 0);
		if (genericMethod != null) {
			var spec = new MethodSpecUser(genericMethod, new GenericInstMethodSig(new TypeSig[] { int32 }));
			list.Add(new ILSample("methodspec " + genericMethod.FullName, (p, o) => p.Write(o, spec)));
		}
		return list;
	}
}
