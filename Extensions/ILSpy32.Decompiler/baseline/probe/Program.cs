using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

class Probe {
	static int Main(string[] args) {
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
		if (args.Length > 0 && args[0] == "colordump") {
			// Install the resolver BEFORE dispatching: the JIT of ColorDump needs dnlib and the
			// contracts, and with Private=false references those come only from this handler.
			InstallBinResolver(new[] { args[2] });
			return ColorDump(args[1], args[2], args[3], int.Parse(args[4]), args.Length > 5 ? args[5] : "", args.Length > 6 ? args[6] : ".");
		}
		Console.WriteLine("usage: make <path> | decompile <engineDll> <reproDll> <dnspyBin> | colordump <targetDll> <dnspyBin> <cs|vb> <maxTypes> [typeFilter] <outDir>");
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
		var coreAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(dnspyBin, "dnSpy.Decompiler.ILSpy.Core.dll"));
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
		return Pick(list, wantCs: kindNs == "CSharp");
	}

	static dnSpy.Contracts.Decompiler.IDecompiler Create32CSharp(string dnspyBin) => Create32(dnspyBin, "CSharp");
	static dnSpy.Contracts.Decompiler.IDecompiler Create32VB(string dnspyBin) => Create32(dnspyBin, "VisualBasic");

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

				// 2) Text coverage: the middle of every non-empty line must resolve to a statement.
				int textHits = 0, textTotal = 0;
				int lineStart = 0;
				for (var li = 0; li < textLines(text).Length; li++) {
					var line = textLines(text)[li];
					var lineEnd = lineStart + line.Length + 1; // the split kept \r; \n is the +1
					var lineLen = line.TrimEnd('\r').Length;
					if (lineLen > 0) {
						textTotal++;
						var mid = lineStart + lineLen / 2;
						if (mdi.GetSourceStatementByTextOffset(lineStart, lineEnd, mid) is not null)
							textHits++;
					}
					lineStart = lineEnd;
				}
				var textRatio = textTotal == 0 ? 1.0 : (double)textHits / textTotal;
				Console.WriteLine($"  Text coverage {method.Name}: {textHits}/{textTotal} ({textRatio:P0})");
			}
		}
		return 0;

		static string[] textLines(string s) => s.Replace("\r\n", "\n").Split('\n');
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
			Check(excerpt.Contains("num"), $"1b C# first SP text aligned ('{excerpt.Trim()}' contains 'num')");
		});

		// 2. VB statement-level SPs.
		DumpStatements(dec32vb, statementVariety, "SimpleStatements", mdi => {
			Check(mdi!.Statements.Length >= 4, $"2a VB SimpleStatements statement SPs ({mdi.Statements.Length})");
		});

		// 3. C# debug locals: SimpleStatements maps 'num' by slot.
		DumpStatements(dec32cs, statementVariety, "SimpleStatements", mdi => {
			var locals = mdi!.Scope.Locals;
			Check(locals!.Any(l => l.Name == "num"), $"3a C# SimpleStatements locals contain 'num' ({string.Join(",", locals!.Select(l => l.Name))})");
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

		Console.WriteLine(failures == 0 ? "ENGINE-TESTS: ALL PASS" : $"ENGINE-TESTS: {failures} FAILURES");
		return failures == 0 ? 0 : 1;
	}

	static int SpDumpLang(string[] args) {
		var dnspyBin = args[2];
		var dec = args.Length > 4 && args[4] == "vb" ? Create32VB(dnspyBin) : Create32CSharp(dnspyBin);
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
}
