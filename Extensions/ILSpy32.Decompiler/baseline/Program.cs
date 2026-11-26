using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace EngineBaseline {

	/// <summary>
	/// Minimal <see cref="IDecompilerOutput"/> that captures the decompiler text.
	/// Kept identical to the one in vbverify so both hosts measure the same thing.
	/// </summary>
	sealed class StringDecompilerOutput : IDecompilerOutput {
		readonly StringBuilder sb = new StringBuilder();
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

		public void Write(string text, object reference, DecompilerReferenceFlags flags, object color) {
			WriteIndent();
			sb.Append(text);
		}

		public void Write(string text, int index, int length, object reference, DecompilerReferenceFlags flags, object color) {
			WriteIndent();
			sb.Append(text, index, length);
		}

		public void AddCustomData<TData>(string id, TData data) { }

		public override string ToString() => sb.ToString();
	}

	sealed class DumpEntry {
		public string FullName;
		public string Status;      // ok | fail
		public int Length;
		public string FileName;
		public string Error;
	}

	static class Program {
		const string SummaryFileName = "summary.tsv";
		const string MetaFileName = "_meta.txt";

		// The 3.2 decompilers swallow exceptions and emit a comment instead of
		// failing. Without detecting these markers the "ok" count is meaningless:
		// a type that produced nothing but a failure note still counts as ok.
		static readonly string[] FailureMarkers = {
			"// ILSpy 3.2 decompilation failed:",
			"// ILSpy 3.2 disassembly failed:",
			"' ILSpy 3.2 VB decompilation failed:",
			"' ILSpy 3.2 VB conversion failed:",
			"// ILSpy 3.2: cannot bridge",
			"' ILSpy 3.2: cannot bridge",
		};

		static int Main(string[] args) {
			Console.OutputEncoding = Encoding.UTF8;
			if (args.Length == 0 || args[0] == "-h" || args[0] == "--help") {
				PrintUsage();
				return args.Length == 0 ? 1 : 0;
			}

			try {
				switch (args[0]) {
				case "dump":
					return RunDump(args.Skip(1).ToArray());
				case "compare":
					return RunCompare(args.Skip(1).ToArray());
				case "validate":
					return RunValidate(args.Skip(1).ToArray());
				case "compile":
					return RunCompile(args.Skip(1).ToArray());
				default:
					Console.WriteLine("[FAIL] Unknown command: " + args[0]);
					PrintUsage();
					return 1;
				}
			}
			catch (Exception ex) {
				Console.WriteLine("[EXCEPTION] " + ex);
				return 1;
			}
		}

		static void PrintUsage() {
			Console.WriteLine(@"Engine baseline harness — compares two dnSpy decompiler engines.

  baseline dump --asm <assembly.dll> --impl core|ilspy32 --lang cs|vb|il --out <dir>
                [--all] [--max N] [--dnspy-dir <dir>] [--members] [--assembly] [--module]

      core     = existing 2.3 engine (dnSpy.Decompiler.ILSpy.Core.dll)
      ilspy32  = new 3.2 engine    (dnSpy.Decompiler.ILSpy32.x.dll)
      --all    = include internal / compiler-generated types
      --max N  = stop after N types (smoke runs)

  baseline compare --a <dirA> --b <dirB> --out <report.md> [--top N]

      Writes a markdown report: identical / different / failed categories.

  baseline validate --dir <dir> --lang cs|vb [--out <report.md>] [--top N]

      Parses every 'ok' entry with Roslyn and reports syntax errors. This is a
      syntax-only check: it catches malformed output, not wrong semantics.

  baseline compile --dir <dir> --lang cs|vb --refs <dir> [--out <report.md>] [--top N]

      Semantic compilation of every 'ok' entry against the assemblies in --refs
      (plus the running runtime). Accessibility diagnostics are reported
      separately because a decompiled type always lives in a different assembly
      than the members it uses, so internal access shows up as a false positive.");
		}

		// ---------------------------------------------------------------- dump

		static int RunDump(string[] args) {
			string asmPath = null, impl = null, lang = null, outDir = null, dnspyDir = null, onlyType = null;
			bool all = false, members = false, dumpAssembly = false, dumpModule = false;
			int max = int.MaxValue;

			for (int i = 0; i < args.Length; i++) {
				switch (args[i]) {
				case "--asm": asmPath = Next(args, ref i); break;
				case "--impl": impl = Next(args, ref i); break;
				case "--lang": lang = Next(args, ref i); break;
				case "--out": outDir = Next(args, ref i); break;
				case "--dnspy-dir": dnspyDir = Next(args, ref i); break;
				case "--max": max = int.Parse(Next(args, ref i)); break;
				case "--all": all = true; break;
				case "--type": onlyType = Next(args, ref i); break;
				// Smoke-run the non-type entry points of a language (used by --lang il).
				case "--members": members = true; break;
				case "--assembly": dumpAssembly = true; break;
				case "--module": dumpModule = true; break;
				default: throw new ArgumentException("Unknown option: " + args[i]);
				}
			}
			if (asmPath is null) throw new ArgumentException("--asm is required");
			if (impl is null) throw new ArgumentException("--impl is required");
			if (lang is null) throw new ArgumentException("--lang is required");
			if (outDir is null) throw new ArgumentException("--out is required");
			if (!File.Exists(asmPath)) throw new FileNotFoundException("Assembly not found", asmPath);

			asmPath = Path.GetFullPath(asmPath);
			dnspyDir = ResolveDnspyDir(dnspyDir, asmPath);
			InstallAssemblyResolver(dnspyDir);

			var decompilers = LoadDecompilers(impl, dnspyDir);
			var ext = lang == "vb" ? ".vb" : lang == "il" ? ".il" : ".cs";
			var decompiler = decompilers.FirstOrDefault(d => d.FileExtension == ext);
			if (decompiler is null) {
				Console.WriteLine("[FAIL] No " + ext + " decompiler. Available: " +
					string.Join(", ", decompilers.Select(d => d.UniqueNameUI + "(" + d.FileExtension + ")")));
				return 1;
			}
			Console.WriteLine("[OK] Engine: " + decompiler.UniqueNameUI + " (" + decompiler.UniqueGuid + ")");
			Console.WriteLine("[OK] Assembly: " + asmPath);

			Directory.CreateDirectory(outDir);
			var mod = ModuleDefMD.Load(asmPath);
			var candidates = (all
				? mod.GetTypes()
				: mod.GetTypes().Where(t => t.IsPublic && !t.IsEnum && !t.IsInterface))
				.Where(t => onlyType is null || t.FullName == onlyType || t.FullName.Contains(onlyType))
				.OrderBy(t => t.FullName, StringComparer.Ordinal)
				.Take(max)
				.ToList();
			Console.WriteLine("[..] " + candidates.Count + " types (all=" + all + ")");

			var work = new List<(string FullName, Action<IDecompilerOutput, DecompilationContext> Run)>();
			if (dumpAssembly && mod.Assembly is not null)
				work.Add(("(assembly)", (o, ctx) => decompiler.Decompile(mod.Assembly, o, ctx)));
			if (dumpModule)
				work.Add(("(module)", (o, ctx) => decompiler.Decompile(mod, o, ctx)));
			foreach (var t in candidates) {
				work.Add((t.FullName, (o, ctx) => decompiler.Decompile(t, o, ctx)));
				if (members) {
					foreach (var m in t.Methods)
						work.Add((t.FullName + "::" + m.Name, (o, ctx) => decompiler.Decompile(m, o, ctx)));
					foreach (var f in t.Fields)
						work.Add((t.FullName + "::" + f.Name, (o, ctx) => decompiler.Decompile(f, o, ctx)));
					foreach (var p in t.Properties)
						work.Add((t.FullName + "::" + p.Name, (o, ctx) => decompiler.Decompile(p, o, ctx)));
					foreach (var e in t.Events)
						work.Add((t.FullName + "::" + e.Name, (o, ctx) => decompiler.Decompile(e, o, ctx)));
				}
			}

			var entries = new List<DumpEntry>(work.Count);
			int index = 0;
			foreach (var w in work) {
				index++;
				var entry = new DumpEntry { FullName = w.FullName, FileName = EntryFileName(index, w.FullName) };
				string text;
				try {
					var o = new StringDecompilerOutput();
					w.Run(o, new DecompilationContext());
					text = o.ToString();
					var marker = FailureMarkers.FirstOrDefault(m => text.IndexOf(m, StringComparison.Ordinal) >= 0);
					if (marker is null) {
						entry.Status = "ok";
					}
					else {
						entry.Status = "degraded";
						entry.Error = ExtractFailure(text, marker);
					}
				}
				catch (Exception ex) {
					text = "";
					entry.Status = "fail";
					entry.Error = ex.GetType().Name + ": " + ex.Message;
				}
				entry.Length = text.Length;
				File.WriteAllText(Path.Combine(outDir, entry.FileName), text, new UTF8Encoding(false));
				entries.Add(entry);
			}

			var sb = new StringBuilder();
			foreach (var e in entries)
				sb.Append(e.FullName).Append('\t').Append(e.Status).Append('\t').Append(e.Length)
				  .Append('\t').Append(e.FileName).Append('\t').Append(Clean(e.Error)).Append('\n');
			File.WriteAllText(Path.Combine(outDir, SummaryFileName), sb.ToString(), new UTF8Encoding(false));

			var meta = new StringBuilder();
			meta.AppendLine("impl=" + impl);
			meta.AppendLine("lang=" + lang);
			meta.AppendLine("asm=" + asmPath);
			meta.AppendLine("all=" + all);
			meta.AppendLine("engine=" + decompiler.UniqueNameUI);
			meta.AppendLine("engineGuid=" + decompiler.UniqueGuid);
			meta.AppendLine("types=" + entries.Count);
			meta.AppendLine("ok=" + entries.Count(e => e.Status == "ok"));
			meta.AppendLine("degraded=" + entries.Count(e => e.Status == "degraded"));
			meta.AppendLine("failed=" + entries.Count(e => e.Status == "fail"));
			File.WriteAllText(Path.Combine(outDir, MetaFileName), meta.ToString(), new UTF8Encoding(false));

			int ok = entries.Count(e => e.Status == "ok");
			int degraded = entries.Count(e => e.Status == "degraded");
			int failed = entries.Count(e => e.Status == "fail");
			Console.WriteLine("[RESULT] " + decompiler.UniqueNameUI + " dump: ok=" + ok +
				" degraded=" + degraded + " failed=" + failed + " -> " + outDir);
			foreach (var g in entries.Where(e => e.Status != "ok")
					.GroupBy(e => e.Status + " | " + Classify(e.Error))
					.OrderByDescending(g => g.Count()).Take(12))
				Console.WriteLine("  " + g.Count() + "× " + g.Key);
			return failed == 0 ? 0 : 2;
		}

		static string EntryFileName(int index, string fullName) =>
			index.ToString("D5") + "_" + Sanitize(fullName) + ".txt";

		static string Sanitize(string s) {
			if (string.IsNullOrEmpty(s)) return "_";
			var sb = new StringBuilder(s.Length);
			foreach (var c in s) {
				if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '+') sb.Append(c);
				else sb.Append('_');
			}
			if (sb.Length > 120) sb.Length = 120;
			return sb.ToString();
		}

		static string Clean(string s) {
			if (string.IsNullOrEmpty(s)) return "";
			return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
		}

		static string ExtractFailure(string text, string marker) {
			int i = text.IndexOf(marker, StringComparison.Ordinal);
			if (i < 0) return "";
			int end = text.IndexOf('\n', i);
			var line = (end < 0 ? text.Substring(i) : text.Substring(i, end - i)).Trim();
			if (line.StartsWith("' ", StringComparison.Ordinal)) line = line.Substring(2);
			else if (line.StartsWith("// ", StringComparison.Ordinal)) line = line.Substring(3);
			return line;
		}

		static string Classify(string error) {
			if (string.IsNullOrEmpty(error)) return "(no message)";
			int idx = error.IndexOf("VB output for ", StringComparison.Ordinal);
			if (idx >= 0) return error.Substring(idx).Trim();
			if (error.Contains("Could not find type definition")) return "Could not find type definition";
			if (error.Contains("cannot bridge")) return "cannot bridge";
			return Truncate(error, 100);
		}

		static string Next(string[] args, ref int i) {
			if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[i]);
			return args[++i];
		}

		static string ResolveDnspyDir(string explicitDir, string asmPath) {
			if (explicitDir is not null) {
				var d = Path.GetFullPath(explicitDir);
				if (!Directory.Exists(d)) throw new DirectoryNotFoundException(d);
				return d;
			}
			var asmDir = Path.GetDirectoryName(asmPath);
			if (asmDir is not null && File.Exists(Path.Combine(asmDir, "dnSpy.Decompiler.ILSpy.Core.dll")))
				return asmDir;
			var hostDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
			if (hostDir is not null && File.Exists(Path.Combine(hostDir, "dnSpy.Decompiler.ILSpy.Core.dll")))
				return hostDir;
			throw new ArgumentException("Cannot locate dnSpy output directory; pass --dnspy-dir");
		}

		static void InstallAssemblyResolver(string dnspyDir) {
			AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
				try {
					var simple = new AssemblyName(e.Name).Name;
					var p = Path.Combine(dnspyDir, simple + ".dll");
					if (File.Exists(p)) return Assembly.LoadFrom(p);
				}
				catch {
					// fall through: let the runtime report the original failure
				}
				return null;
			};
		}

		static List<IDecompiler> LoadDecompilers(string impl, string dnspyDir) {
			if (impl == "core") {
				// The 2.3 providers keep a parameterless ctor explicitly for dnSpy.Console.exe.
				var asm = Assembly.LoadFrom(Path.Combine(dnspyDir, "dnSpy.Decompiler.ILSpy.Core.dll"));
				var result = new List<IDecompiler>();
				foreach (var t in asm.GetTypes()) {
					if (t.IsAbstract || t.IsInterface) continue;
					if (!typeof(IDecompilerProvider).IsAssignableFrom(t)) continue;
					var provider = (IDecompilerProvider)Activator.CreateInstance(t);
					result.AddRange(provider.Create());
				}
				return result;
			}
			if (impl == "ilspy32") {
				var asm = Assembly.LoadFrom(Path.Combine(dnspyDir, "dnSpy.Decompiler.ILSpy32.x.dll"));
				var creatorType = asm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true);
				var creator = Activator.CreateInstance(creatorType);
				var create = creatorType.GetMethod("Create");
				return ((IEnumerable<IDecompiler>)create.Invoke(creator, null)).ToList();
			}
			throw new ArgumentException("--impl must be core or ilspy32");
		}

		// ------------------------------------------------------------- compare

		static int RunCompare(string[] args) {
			string aDir = null, bDir = null, outPath = null;
			int top = 20;
			for (int i = 0; i < args.Length; i++) {
				switch (args[i]) {
				case "--a": aDir = Next(args, ref i); break;
				case "--b": bDir = Next(args, ref i); break;
				case "--out": outPath = Next(args, ref i); break;
				case "--top": top = int.Parse(Next(args, ref i)); break;
				default: throw new ArgumentException("Unknown option: " + args[i]);
				}
			}
			if (aDir is null || bDir is null || outPath is null)
				throw new ArgumentException("--a, --b and --out are required");

			var a = ReadSummary(aDir);
			var b = ReadSummary(bDir);
			var names = a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();

			int identical = 0, different = 0, aOkBdegraded = 0, aDegradedBok = 0;
			int aFailBok = 0, aOkBfail = 0, bothNotOk = 0, onlyA = 0, onlyB = 0;
			var diffSamples = new List<(string Name, string ADetail, string BDetail, int AChars, int BChars)>();

			foreach (var name in names) {
				bool hasA = a.TryGetValue(name, out var ea);
				bool hasB = b.TryGetValue(name, out var eb);
				if (hasA && !hasB) { onlyA++; continue; }
				if (!hasA && hasB) { onlyB++; continue; }

				bool aOk = ea.Status == "ok";
				bool bOk = eb.Status == "ok";
				if (!aOk && !bOk) { bothNotOk++; continue; }
				if (aOk && !bOk) {
					if (eb.Status == "degraded") aOkBdegraded++; else aOkBfail++;
					continue;
				}
				if (!aOk && bOk) {
					if (ea.Status == "degraded") aDegradedBok++; else aFailBok++;
					continue;
				}

				var ta = Normalize(ReadEntry(aDir, ea));
				var tb = Normalize(ReadEntry(bDir, eb));
				if (ta == tb) { identical++; continue; }
				different++;
				if (diffSamples.Count < top)
					diffSamples.Add((name, FirstDiff(ta, tb, "A"), FirstDiff(tb, ta, "B"), ea.Length, eb.Length));
			}

			var metaA = ReadMeta(aDir);
			var metaB = ReadMeta(bDir);
			var report = new StringBuilder();
			report.AppendLine("# Engine baseline report");
			report.AppendLine();
			report.AppendLine("| | A | B |");
			report.AppendLine("|---|---|---|");
			report.AppendLine("| dir | `" + aDir + "` | `" + bDir + "` |");
			report.AppendLine("| engine | " + metaA.GetValueOrDefault("engine", "?") + " | " + metaB.GetValueOrDefault("engine", "?") + " |");
			report.AppendLine("| impl | " + metaA.GetValueOrDefault("impl", "?") + " | " + metaB.GetValueOrDefault("impl", "?") + " |");
			report.AppendLine("| lang | " + metaA.GetValueOrDefault("lang", "?") + " | " + metaB.GetValueOrDefault("lang", "?") + " |");
			report.AppendLine("| assembly | `" + metaA.GetValueOrDefault("asm", "?") + "` | `" + metaB.GetValueOrDefault("asm", "?") + "` |");
			report.AppendLine();
			report.AppendLine("## Summary");
			report.AppendLine();
			report.AppendLine("| Category | Count |");
			report.AppendLine("|---|---|");
			report.AppendLine("| identical | " + identical + " |");
			report.AppendLine("| different (both ok) | " + different + " |");
			report.AppendLine("| A ok / B degraded | " + aOkBdegraded + " |");
			report.AppendLine("| A degraded / B ok | " + aDegradedBok + " |");
			report.AppendLine("| A failed / B ok | " + aFailBok + " |");
			report.AppendLine("| A ok / B failed | " + aOkBfail + " |");
			report.AppendLine("| both degraded/failed | " + bothNotOk + " |");
			report.AppendLine("| only in A | " + onlyA + " |");
			report.AppendLine("| only in B | " + onlyB + " |");
			report.AppendLine("| **total** | **" + names.Count + "** |");
			report.AppendLine();
			AppendFailureDistribution(report, "A", a);
			AppendFailureDistribution(report, "B", b);
			if (diffSamples.Count > 0) {
				report.AppendLine("## First differing line (up to " + top + " types)");
				report.AppendLine();
				foreach (var s in diffSamples) {
					report.AppendLine("### " + s.Name);
					report.AppendLine();
					report.AppendLine("- A (" + s.AChars + " chars): `" + Truncate(s.ADetail, 300) + "`");
					report.AppendLine("- B (" + s.BChars + " chars): `" + Truncate(s.BDetail, 300) + "`");
					report.AppendLine();
				}
			}
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
			File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
			Console.WriteLine("[RESULT] identical=" + identical + " different=" + different +
				" aOkBdegraded=" + aOkBdegraded + " aDegradedBok=" + aDegradedBok +
				" aFailBok=" + aFailBok + " aOkBfail=" + aOkBfail + " bothNotOk=" + bothNotOk +
				" onlyA=" + onlyA + " onlyB=" + onlyB);
			Console.WriteLine("[OK] Report: " + Path.GetFullPath(outPath));
			return 0;
		}

		static void AppendFailureDistribution(StringBuilder report, string label, Dictionary<string, DumpEntry> entries) {
			var bad = entries.Values.Where(e => e.Status != "ok").ToList();
			if (bad.Count == 0) return;
			report.AppendLine("### " + label + " failure reasons");
			report.AppendLine();
			report.AppendLine("| Count | Status | Reason |");
			report.AppendLine("|---|---|---|");
			foreach (var g in bad.GroupBy(e => e.Status + "\t" + Classify(e.Error)).OrderByDescending(g => g.Count())) {
				var parts = g.Key.Split('\t');
				report.AppendLine("| " + g.Count() + " | " + parts[0] + " | `" + Truncate(parts[1], 160) + "` |");
			}
			report.AppendLine();
		}

		static Dictionary<string, DumpEntry> ReadSummary(string dir) {
			var path = Path.Combine(dir, SummaryFileName);
			if (!File.Exists(path)) throw new FileNotFoundException("Missing summary", path);
			var result = new Dictionary<string, DumpEntry>(StringComparer.Ordinal);
			foreach (var line in File.ReadAllLines(path)) {
				if (line.Length == 0) continue;
				var parts = line.Split('\t');
				if (parts.Length < 5) continue;
				result[parts[0]] = new DumpEntry {
					FullName = parts[0],
					Status = parts[1],
					Length = int.TryParse(parts[2], out var n) ? n : 0,
					FileName = parts[3],
					Error = parts[4]
				};
			}
			return result;
		}

		static Dictionary<string, string> ReadMeta(string dir) {
			var path = Path.Combine(dir, MetaFileName);
			var result = new Dictionary<string, string>(StringComparer.Ordinal);
			if (!File.Exists(path)) return result;
			foreach (var line in File.ReadAllLines(path)) {
				var idx = line.IndexOf('=');
				if (idx > 0) result[line.Substring(0, idx)] = line.Substring(idx + 1);
			}
			return result;
		}

		static string ReadEntry(string dir, DumpEntry e) {
			var path = Path.Combine(dir, e.FileName);
			return File.Exists(path) ? File.ReadAllText(path) : "";
		}

		static string Normalize(string text) {
			if (string.IsNullOrEmpty(text)) return "";
			var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			for (int i = 0; i < lines.Length; i++)
				lines[i] = lines[i].TrimEnd();
			return string.Join("\n", lines).TrimEnd('\n');
		}

		static string FirstDiff(string text, string other, string label) {
			var a = text.Split('\n');
			var b = other.Split('\n');
			int n = Math.Min(a.Length, b.Length);
			for (int i = 0; i < n; i++) {
				if (a[i] != b[i])
					return label + " line " + (i + 1) + ": " + a[i].Trim();
			}
			if (a.Length != b.Length)
				return label + " line " + (n + 1) + ": <end>";
			return label + ": <no line diff>";
		}

		// ----------------------------------------------------------- validate

		static int RunValidate(string[] args) {
			string dir = null, lang = null, outPath = null, onlyFile = null;
			int top = 10;
			for (int i = 0; i < args.Length; i++) {
				switch (args[i]) {
				case "--dir": dir = Next(args, ref i); break;
				case "--lang": lang = Next(args, ref i); break;
				case "--out": outPath = Next(args, ref i); break;
				case "--top": top = int.Parse(Next(args, ref i)); break;
				case "--file": onlyFile = Next(args, ref i); break;
				default: throw new ArgumentException("Unknown option: " + args[i]);
				}
			}
			if (dir is null || lang is null) throw new ArgumentException("--dir and --lang are required");
			bool vb = lang == "vb";

			var entries = ReadSummary(dir).Values.Where(e => e.Status == "ok")
				.OrderBy(e => e.FileName, StringComparer.Ordinal).ToList();
			if (onlyFile is not null)
				entries = entries.Where(e => e.FullName == onlyFile || e.FileName == onlyFile).ToList();
			int badFiles = 0, badDiagnostics = 0;
			var byId = new Dictionary<string, int>(StringComparer.Ordinal);
			var sampleMessage = new Dictionary<string, string>(StringComparer.Ordinal);
			var samples = new List<string>();

			foreach (var e in entries) {
				var text = ReadEntry(dir, e);
				var tree = vb
					? Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree.ParseText(text)
					: Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
				var errors = tree.GetDiagnostics()
					.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
					.ToList();
				if (errors.Count == 0) continue;
				badFiles++;
				badDiagnostics += errors.Count;
				if (onlyFile is not null) {
					foreach (var d in errors)
						Console.WriteLine("  line " + (d.Location.GetLineSpan().StartLinePosition.Line + 1) +
							": " + d.Id + " " + d.GetMessage());
				}
				foreach (var g in errors.GroupBy(d => d.Id)) {
					byId[g.Key] = (byId.TryGetValue(g.Key, out var n) ? n : 0) + 1;
					if (!sampleMessage.ContainsKey(g.Key))
						sampleMessage[g.Key] = g.First().GetMessage();
				}
				if (samples.Count < top) {
					var first = errors[0];
					int line = first.Location.GetLineSpan().StartLinePosition.Line + 1;
					samples.Add(e.FullName + " :: " + first.Id + " " + first.GetMessage() + " (line " + line + ")");
				}
			}

			var report = new StringBuilder();
			report.AppendLine("# Syntax validation");
			report.AppendLine();
			report.AppendLine("- dir: `" + dir + "`");
			report.AppendLine("- language: " + lang);
			report.AppendLine("- files checked: " + entries.Count);
			report.AppendLine("- files with syntax errors: " + badFiles);
			report.AppendLine("- error diagnostics: " + badDiagnostics);
			report.AppendLine();
			if (byId.Count > 0) {
				report.AppendLine("| Count | Diagnostic | Message |");
				report.AppendLine("|---|---|---|");
				foreach (var kv in byId.OrderByDescending(k => k.Value))
					report.AppendLine("| " + kv.Value + " | `" + kv.Key + "` | " +
						sampleMessage.GetValueOrDefault(kv.Key, "").Replace("|", "\\|") + " |");
				report.AppendLine();
				report.AppendLine("## Samples");
				report.AppendLine();
				foreach (var s in samples)
					report.AppendLine("- " + s);
			}

			if (outPath is not null) {
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
				File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
			}
			Console.WriteLine("[RESULT] validate lang=" + lang + " checked=" + entries.Count +
				" badFiles=" + badFiles + " diagnostics=" + badDiagnostics);
			foreach (var kv in byId.OrderByDescending(k => k.Value).Take(10))
				Console.WriteLine("  " + kv.Value + "x " + kv.Key + " " + sampleMessage.GetValueOrDefault(kv.Key, ""));
			foreach (var s in samples.Take(5))
				Console.WriteLine("  " + s);
			if (outPath is not null) Console.WriteLine("[OK] Report: " + Path.GetFullPath(outPath));
			return badFiles == 0 ? 0 : 2;
		}

		// ------------------------------------------------------------ compile

		// A decompiled type always ends up in a different assembly than the members it
		// references, so internal members look inaccessible. Report those separately.
		static readonly HashSet<string> accessibilityNoise = new HashSet<string>(StringComparer.Ordinal) {
			"CS0122", "CS0050", "CS0051", "BC30390", "BC30389", "BC30500", "BC30508"
		};
		static bool IsAccessibilityNoise(string id) => accessibilityNoise.Contains(id);

		// dnSpy 2.3-style output prints the compiler-reserved nullable attributes verbatim;
		// Roslyn rejects their manual use (CS8335/CS8623) and misplacement of them
		// (CS0592). This is a deliberate fidelity choice, not a decompiler defect.
		static bool IsNullableAttributeNoise(Microsoft.CodeAnalysis.Diagnostic d)
		{
			if (d.Id == "CS8335" || d.Id == "CS8623")
				return true;
			return d.Id == "CS0592" && d.GetMessage().IndexOf("Nullable", StringComparison.Ordinal) >= 0;
		}

		// Compiler-generated names such as "<GetCustomAttributes>d__9" are rewritten to
		// "_GetCustomAttributes_d__9" because VB cannot name them. The state-machine type itself
		// is not part of the dump (it is a non-public nested type), so the reference cannot
		// resolve. That is a property of the dump selection, not of the generated code.
		// In per-file mode the dumped type is visible twice: once from the file, once from the
		// reference assembly (the very assembly being dumped). Its internal members are missing
		// from the latter, so a qualified call to one reports "is not a member".
		static bool IsShadowedMemberNoise(Microsoft.CodeAnalysis.Diagnostic diagnostic, DumpEntry current, List<DumpEntry> all) {
			// A type declared in another file of the dump cannot resolve when a single file is
			// compiled, and a member of such a type is missing from the reference assembly.
			if (diagnostic.Id == "BC30002" || diagnostic.Id == "CS0246") {
				var typeName0 = ExtractQuoted(diagnostic.GetMessage(), "Type '");
				return typeName0 != null && IsDefinedElsewhere(typeName0, current, all);
			}
			if (diagnostic.Id != "BC30456" && diagnostic.Id != "CS0117")
				return false;
			var message = diagnostic.GetMessage();
			const string marker = "is not a member of '";
			int start = message.IndexOf(marker, StringComparison.Ordinal);
			if (start < 0)
				return false;
			start += marker.Length;
			int end = message.IndexOf('\'', start);
			if (end < 0)
				return false;
			return IsDefinedElsewhere(message.Substring(start, end - start), current, all);
		}

		static string ExtractQuoted(string message, string marker) {
			int start = message.IndexOf(marker, StringComparison.Ordinal);
			if (start < 0)
				return null;
			start += marker.Length;
			int end = message.IndexOf('\'', start);
			return end < 0 ? null : message.Substring(start, end - start);
		}

		static string ExtractFirstQuoted(string message) {
			int start = message.IndexOf('\'');
			if (start < 0)
				return null;
			int end = message.IndexOf('\'', start + 1);
			return end < 0 ? null : message.Substring(start + 1, end - start - 1);
		}

		// When a base class or interface is inaccessible ('Friend' in the reference assembly), VB
		// cannot see its members, so every Overrides/Implements diagnostic on the type is a
		// cascade of that single accessibility error, not a conversion defect. Only treat it as
		// noise when the type's Inherits/Implements clause really names the inaccessible type.
		static readonly HashSet<string> cascadeDiagnostics = new HashSet<string>(StringComparer.Ordinal) {
			"BC30284", "BC30401", "BC30149", "BC30583", "BC30610", "BC30794", "BC30517",
			"CS0115", "CS0535", "CS0534"
		};

		static HashSet<string> CollectInaccessibleBases(List<Microsoft.CodeAnalysis.Diagnostic> errors, string text) {
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var diagnostic in errors) {
				if (diagnostic.Id != "BC30389")
					continue;
				var quoted = ExtractFirstQuoted(diagnostic.GetMessage());
				if (quoted is null)
					continue;
				var simple = quoted.Substring(quoted.LastIndexOf('.') + 1);
				if (simple.Length == 0)
					continue;
				if (MentionsInInheritanceClause(text, simple))
					names.Add(simple);
			}
			return names;
		}

		/// <summary>
		/// Simple names reported as inaccessible (Friend/internal) anywhere in the file. An
		/// internal type used as a field or local type makes the whole member's type resolve
		/// to an error type, and the member-access/extension-method diagnostics that follow
		/// (BC30456 'X' is not a member of 'Y()') are cascades, not converter defects.
		/// </summary>
		static HashSet<string> CollectInaccessibleFriendNames(List<Microsoft.CodeAnalysis.Diagnostic> errors) {
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var diagnostic in errors) {
				if (diagnostic.Id != "BC30389")
					continue;
				var quoted = ExtractFirstQuoted(diagnostic.GetMessage());
				if (quoted is null)
					continue;
				var simple = quoted.Substring(quoted.LastIndexOf('.') + 1);
				if (simple.Length > 0)
					names.Add(simple);
			}
			return names;
		}

		/// <summary>True when a diagnostic is a cascade of an inaccessible (Friend) type.</summary>
		static bool IsInaccessibleFriendCascade(Microsoft.CodeAnalysis.Diagnostic d, HashSet<string> inaccessibleFriendNames) {
			if (inaccessibleFriendNames.Count == 0)
				return false;
			if (d.Id != "BC30456" && d.Id != "BC30451" && d.Id != "BC30002" && d.Id != "BC30452")
				return false;
			foreach (var quoted in ExtractAllQuoted(d.GetMessage())) {
				var simple = quoted.Substring(quoted.LastIndexOf('.') + 1);
				while (simple.EndsWith("()", StringComparison.Ordinal))
					simple = simple.Substring(0, simple.Length - 2);
				if (inaccessibleFriendNames.Contains(simple))
					return true;
			}
			return false;
		}

		static System.Collections.Generic.IEnumerable<string> ExtractAllQuoted(string message) {
			var search = 0;
			while (search < message.Length) {
				var start = message.IndexOf('\'', search);
				if (start < 0)
					yield break;
				var end = message.IndexOf('\'', start + 1);
				if (end < 0)
					yield break;
				if (end > start + 1)
					yield return message.Substring(start + 1, end - start - 1);
				search = end + 1;
			}
		}

		static bool MentionsInInheritanceClause(string text, string name) {
			foreach (var raw in text.Split('\n')) {
				var line = raw.Trim();
				if (!line.StartsWith("Inherits ", StringComparison.Ordinal) &&
					!line.StartsWith("Implements ", StringComparison.Ordinal))
					continue;
				if (line.IndexOf(name, StringComparison.Ordinal) >= 0)
					return true;
			}
			return false;
		}

		static bool IsDefinedElsewhere(string typeName, DumpEntry current, List<DumpEntry> all) {
			foreach (var other in all) {
				if (ReferenceEquals(other, current))
					continue;
				if (other.FullName == typeName || other.FullName.EndsWith("." + typeName, StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		static bool IsRenamedStateMachineReference(Microsoft.CodeAnalysis.Diagnostic diagnostic) {
			if (diagnostic.Id != "BC30002" && diagnostic.Id != "CS0246")
				return false;
			// "Type '_GetCustomAttributes_d__9' is not defined." — the sanitized state-machine
			// name always contains "_d__".
			return diagnostic.GetMessage().Contains("_d__", StringComparison.Ordinal);
		}

		static int RunCompile(string[] args) {
			string dir = null, lang = null, outPath = null;
			var refsDirs = new List<string>();
			int top = 10, max = int.MaxValue;
			bool whole = false;
			string onlyDiagnostic = null, onlyType = null;
			for (int i = 0; i < args.Length; i++) {
				switch (args[i]) {
				case "--dir": dir = Next(args, ref i); break;
				case "--lang": lang = Next(args, ref i); break;
				case "--out": outPath = Next(args, ref i); break;
				// Several directories may be needed: the output directory of the dumped assembly
				// plus a shared framework (WPF lives in Microsoft.WindowsDesktop.App).
				case "--refs": refsDirs.AddRange(Next(args, ref i).Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
				case "--top": top = int.Parse(Next(args, ref i)); break;
				case "--max": max = int.Parse(Next(args, ref i)); break;
				case "--whole": whole = true; break;
				case "--id": onlyDiagnostic = Next(args, ref i); break;
				// Restrict the run to types whose full name contains this substring. Combined with
				// the per-file diagnostic dump below this is the fastest way to iterate on one type.
				case "--type": onlyType = Next(args, ref i); break;
				default: throw new ArgumentException("Unknown option: " + args[i]);
				}
			}
			if (dir is null || lang is null || refsDirs.Count == 0)
				throw new ArgumentException("--dir, --lang and --refs are required");
			bool vb = lang == "vb";

			var refs = new List<Microsoft.CodeAnalysis.MetadataReference>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa) {
				foreach (var path in tpa.Split(Path.PathSeparator)) {
					if (!File.Exists(path) || !seen.Add(path)) continue;
					try { refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path)); } catch { }
				}
			}
			foreach (var refsDir in refsDirs) {
				if (!Directory.Exists(refsDir))
					throw new ArgumentException("Reference directory not found: " + refsDir);
				foreach (var path in Directory.EnumerateFiles(refsDir, "*.dll", SearchOption.TopDirectoryOnly)) {
					if (!seen.Add(path)) continue;
					try { refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path)); } catch { }
				}
			}

			var entries = ReadSummary(dir).Values.Where(e => e.Status == "ok")
				.OrderBy(e => e.FileName, StringComparer.Ordinal).Take(max).ToList();
			if (onlyType is not null)
				entries = entries.Where(e => e.FullName.IndexOf(onlyType, StringComparison.OrdinalIgnoreCase) >= 0 ||
					e.FileName.IndexOf(onlyType, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
			if (whole)
				return CompileWhole(vb, entries, refs, dir, top, outPath, onlyDiagnostic);
			int badFiles = 0, noiseOnlyFiles = 0, realBadFiles = 0;
			var byId = new Dictionary<string, int>(StringComparer.Ordinal);
			// Several kinds of noise are decided per diagnostic (cross-file types, cascades), so
			// the raw count alone overstates the problem; keep a noise count per id as well.
			var byIdNoise = new Dictionary<string, int>(StringComparer.Ordinal);
			var msg = new Dictionary<string, string>(StringComparer.Ordinal);
			var samples = new List<string>();

			foreach (var e in entries) {
				var text = ReadEntry(dir, e);
				var tree = vb
					? Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree.ParseText(text)
					: Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
				Microsoft.CodeAnalysis.Compilation compilation = vb
					? Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation.Create(
						"dump_" + e.FileName, new[] { tree }, refs,
						CreateVBOptions())
					: Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
						"dump_" + e.FileName, new[] { tree }, refs,
						new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

				var errors = compilation.GetDiagnostics()
					.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
					.ToList();
				if (errors.Count == 0) continue;
				badFiles++;
				// A single-type run is a debugging session, so show every diagnostic verbatim.
				if (onlyType is not null) {
					Console.WriteLine("[FILE] " + e.FullName + " (" + e.FileName + ") errors=" + errors.Count);
					foreach (var d in errors.OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)) {
						var pos = d.Location.GetLineSpan().StartLinePosition;
						Console.WriteLine("    " + d.Id + " (line " + (pos.Line + 1) + ", col " + (pos.Character + 1) + ") " +
							d.GetMessage());
					}
				}
				var inaccessibleBases = CollectInaccessibleBases(errors, text);
				var inaccessibleFriendNames = CollectInaccessibleFriendNames(errors);
				bool IsNoise(Microsoft.CodeAnalysis.Diagnostic d) =>
					IsAccessibilityNoise(d.Id) || IsNullableAttributeNoise(d) || IsRenamedStateMachineReference(d) || IsShadowedMemberNoise(d, e, entries) ||
					IsInaccessibleFriendCascade(d, inaccessibleFriendNames) ||
					(inaccessibleBases.Count > 0 && cascadeDiagnostics.Contains(d.Id));
				bool noiseOnly = errors.All(IsNoise);
				if (noiseOnly)
					noiseOnlyFiles++;
				else
					realBadFiles++;
				foreach (var g in errors.GroupBy(d => d.Id)) {
					byId[g.Key] = (byId.TryGetValue(g.Key, out var n) ? n : 0) + 1;
					// Same unit as byId (files, not diagnostics): a file whose diagnostics of this
					// id were all judged noise.
					if (g.All(IsNoise))
						byIdNoise[g.Key] = (byIdNoise.TryGetValue(g.Key, out var nz) ? nz : 0) + 1;
					if (!msg.ContainsKey(g.Key)) msg[g.Key] = g.First().GetMessage();
				}
				if (samples.Count < top) {
					var first = errors.FirstOrDefault(d => !IsNoise(d)) ?? errors[0];
					int line = first.Location.GetLineSpan().StartLinePosition.Line + 1;
					samples.Add(e.FullName + " :: " + first.Id + " " + first.GetMessage() + " (line " + line + ")");
				}
			}

			var report = new StringBuilder();
			report.AppendLine("# Semantic compile validation");
			report.AppendLine();
			report.AppendLine("- dir: `" + dir + "`");
			report.AppendLine("- language: " + lang);
			report.AppendLine("- files compiled: " + entries.Count);
			report.AppendLine("- references: " + refs.Count);
			report.AppendLine("- files with errors: " + badFiles);
			report.AppendLine("- files with real errors: " + realBadFiles);
			report.AppendLine("- files whose errors are all measurement noise: " + noiseOnlyFiles);
			report.AppendLine();
			if (byId.Count > 0) {
				report.AppendLine("| Count | Noise | Diagnostic | Message |");
				report.AppendLine("|---|---|---|---|");
				foreach (var kv in byId.OrderByDescending(k => k.Value))
					report.AppendLine("| " + kv.Value + " | " + (byIdNoise.TryGetValue(kv.Key, out var noise) ? noise : 0) +
						" | `" + kv.Key + "`" +
						(IsAccessibilityNoise(kv.Key) ? " (noise)" : "") + " | " +
						msg.GetValueOrDefault(kv.Key, "").Replace("|", "\\|") + " |");
				report.AppendLine();
				report.AppendLine("## Samples");
				report.AppendLine();
				foreach (var s in samples)
					report.AppendLine("- " + s);
			}
			if (outPath is not null) {
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
				File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
			}
			Console.WriteLine("[RESULT] compile lang=" + lang + " files=" + entries.Count +
				" badFiles=" + badFiles + " realBadFiles=" + realBadFiles +
				" noiseOnly=" + noiseOnlyFiles);
			foreach (var kv in byId.OrderByDescending(k => k.Value).Take(10))
				Console.WriteLine("  " + kv.Value + "x " + kv.Key +
					(IsAccessibilityNoise(kv.Key) ? " (noise)" : "") + " " + msg.GetValueOrDefault(kv.Key, ""));
			foreach (var s in samples.Take(5))
				Console.WriteLine("  " + s);
			if (outPath is not null) Console.WriteLine("[OK] Report: " + Path.GetFullPath(outPath));
			return badFiles == 0 ? 0 : 2;
		}

		// Compiles the whole dump as one assembly. Cross-file references and Friend members that
		// are part of the dump then resolve, which is closer to how the output would be used.
		static int CompileWhole(bool vb, List<DumpEntry> entries, List<Microsoft.CodeAnalysis.MetadataReference> refs,
				string dir, int top, string outPath, string onlyDiagnostic) {
			var trees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
			var files = new Dictionary<Microsoft.CodeAnalysis.SyntaxTree, DumpEntry>();
			foreach (var entry in entries) {
				var text = ReadEntry(dir, entry);
				var tree = vb
					? Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree.ParseText(text)
					: Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
				trees.Add(tree);
				files[tree] = entry;
			}
			Microsoft.CodeAnalysis.Compilation compilation = vb
				? Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation.Create("dump", trees, refs,
					CreateVBOptions())
				: Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("dump", trees, refs,
					new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

			var errors = compilation.GetDiagnostics()
				.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
				.ToList();
			var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
			var byId = new Dictionary<string, int>(StringComparer.Ordinal);
			var msg = new Dictionary<string, string>(StringComparer.Ordinal);
			var samples = new List<string>();
			int orphan = 0;
			foreach (var diagnostic in errors) {
				byId[diagnostic.Id] = (byId.TryGetValue(diagnostic.Id, out var n) ? n : 0) + 1;
				if (!msg.ContainsKey(diagnostic.Id))
					msg[diagnostic.Id] = diagnostic.GetMessage();
				var tree = diagnostic.Location.SourceTree;
				if (tree != null && files.TryGetValue(tree, out var entry)) {
					perFile[entry.FullName] = (perFile.TryGetValue(entry.FullName, out var c) ? c : 0) + 1;
					if (samples.Count < top && !IsAccessibilityNoise(diagnostic.Id) && !IsRenamedStateMachineReference(diagnostic))
						samples.Add(entry.FullName + " :: " + diagnostic.Id + " " + diagnostic.GetMessage() +
							" (line " + (diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1) + ")");
				}
				else
					orphan++;
			}

			var report = new StringBuilder();
			report.AppendLine("# Semantic compile validation (whole dump)");
			report.AppendLine();
			report.AppendLine("- dir: `" + dir + "`");
			report.AppendLine("- language: " + (vb ? "vb" : "cs"));
			report.AppendLine("- files compiled: " + entries.Count);
			report.AppendLine("- files with errors: " + perFile.Count);
			report.AppendLine("- diagnostics: " + errors.Count + " (unattributed: " + orphan + ")");
			report.AppendLine();
			report.AppendLine("| Count | Diagnostic | Message |");
			report.AppendLine("|---|---|---|");
			foreach (var kv in byId.OrderByDescending(k => k.Value))
				report.AppendLine("| " + kv.Value + " | `" + kv.Key + "`" +
					(IsAccessibilityNoise(kv.Key) ? " (noise)" : "") + " | " +
					msg.GetValueOrDefault(kv.Key, "").Replace("|", "\\|") + " |");
			report.AppendLine();
			report.AppendLine("## Samples");
			report.AppendLine();
			foreach (var sample in samples)
				report.AppendLine("- " + sample);

			if (outPath is not null) {
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
				File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
			}
			Console.WriteLine("[RESULT] compile(whole) lang=" + (vb ? "vb" : "cs") +
				" files=" + entries.Count + " badFiles=" + perFile.Count + " diagnostics=" + errors.Count);
			foreach (var kv in byId.OrderByDescending(k => k.Value).Take(10))
				Console.WriteLine("  " + kv.Value + "x " + kv.Key +
					(IsAccessibilityNoise(kv.Key) ? " (noise)" : "") + " " + msg.GetValueOrDefault(kv.Key, ""));
			if (onlyDiagnostic is not null) {
				foreach (var diagnostic in errors.Where(d => d.Id == onlyDiagnostic).Take(20)) {
					var tree = diagnostic.Location.SourceTree;
					var name = tree != null && files.TryGetValue(tree, out var entry) ? entry.FullName : "<unknown>";
					Console.WriteLine("    " + name + ":" +
						(diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1) + " " + diagnostic.GetMessage());
				}
			}
			foreach (var sample in samples.Take(3))
				Console.WriteLine("  " + sample);
			if (outPath is not null)
				Console.WriteLine("[OK] Report: " + Path.GetFullPath(outPath));
			return perFile.Count == 0 ? 0 : 2;
		}

		// A real VB project imports these implicitly; a bare Compilation does not, which would
		// otherwise report vbTab/vbCrLf and friends as undeclared.
		static Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions CreateVBOptions() {
			return new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(
				Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
				globalImports: new[] {
					Microsoft.CodeAnalysis.VisualBasic.GlobalImport.Parse("System"),
					Microsoft.CodeAnalysis.VisualBasic.GlobalImport.Parse("System.Collections.Generic"),
					Microsoft.CodeAnalysis.VisualBasic.GlobalImport.Parse("System.Linq"),
					Microsoft.CodeAnalysis.VisualBasic.GlobalImport.Parse("Microsoft.VisualBasic"),
				});
		}

		static string Truncate(string s, int max) =>
			s.Length <= max ? s : s.Substring(0, max) + "…";
	}
}
