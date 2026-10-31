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

  baseline dump --asm <assembly.dll> --impl core|ilspy32 --lang cs|vb --out <dir>
                [--all] [--max N] [--dnspy-dir <dir>]

      core     = existing 2.3 engine (dnSpy.Decompiler.ILSpy.Core.dll)
      ilspy32  = new 3.2 engine    (dnSpy.Decompiler.ILSpy32.x.dll)
      --all    = include internal / compiler-generated types
      --max N  = stop after N types (smoke runs)

  baseline compare --a <dirA> --b <dirB> --out <report.md> [--top N]

      Writes a markdown report: identical / different / failed categories.");
		}

		// ---------------------------------------------------------------- dump

		static int RunDump(string[] args) {
			string asmPath = null, impl = null, lang = null, outDir = null, dnspyDir = null;
			bool all = false;
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
			var ext = lang == "vb" ? ".vb" : ".cs";
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
				.OrderBy(t => t.FullName, StringComparer.Ordinal)
				.Take(max)
				.ToList();
			Console.WriteLine("[..] " + candidates.Count + " types (all=" + all + ")");

			var entries = new List<DumpEntry>(candidates.Count);
			int index = 0;
			foreach (var t in candidates) {
				index++;
				var entry = new DumpEntry { FullName = t.FullName, FileName = EntryFileName(index, t.FullName) };
				string text;
				try {
					var o = new StringDecompilerOutput();
					decompiler.Decompile(t, o, new DecompilationContext());
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

		static string Truncate(string s, int max) =>
			s.Length <= max ? s : s.Substring(0, max) + "…";
	}
}
