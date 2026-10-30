using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace VbVerify {
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

	static class Program {
		static int Main(string[] args) {
			Console.OutputEncoding = Encoding.UTF8;
			try {
				// 1. Locate the ILSpy32 extension assembly.
				var asmDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
				var extPath = Path.Combine(asmDir, "dnSpy.Decompiler.ILSpy32.x.dll");
				if (!File.Exists(extPath)) {
					Console.WriteLine("[FAIL] dnSpy.Decompiler.ILSpy32.x.dll not found next to host: " + extPath);
					return 1;
				}
				var extAsm = Assembly.LoadFrom(extPath);
				Console.WriteLine("[OK] Loaded extension assembly: " + extAsm.FullName);

				// 2. Instantiate DecompilerCreator via reflection.
				var creatorType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
				var creator = Activator.CreateInstance(creatorType)!;
				var createMethod = creatorType.GetMethod("Create")!;
				var decompilers = ((IEnumerable<IDecompiler>)createMethod.Invoke(creator, null)!).ToList();
				Console.WriteLine("[OK] DecompilerCreator returned " + decompilers.Count + " decompilers:");
				foreach (var d in decompilers)
					Console.WriteLine("      - " + d.UniqueNameUI + "  (guid=" + d.UniqueGuid + ", ext=" + d.FileExtension + ")");

				var vb = decompilers.FirstOrDefault(d => d.FileExtension == ".vb");
				if (vb is null) {
					Console.WriteLine("[FAIL] No VB decompiler found!");
					return 1;
				}
				Console.WriteLine("[OK] VB decompiler found: " + vb.UniqueNameUI);

				var targetDecompiler = vb;
				bool useCs = args.Any(a => a == "--cs");
				if (useCs) {
					var cs = decompilers.FirstOrDefault(d => d.FileExtension == ".cs");
					if (cs is null) {
						Console.WriteLine("[FAIL] No C# decompiler found!");
						return 1;
					}
					targetDecompiler = cs;
					Console.WriteLine("[OK] Using C# decompiler: " + cs.UniqueNameUI);
				}

				// 3. Load a real assembly and decompile a type with the VB decompiler.
				string targetPath;
				if (args.Length > 0 && File.Exists(args[0]))
					targetPath = args[0];
				else
					targetPath = Path.Combine(asmDir, "dnSpy.Contracts.Logic.dll");
				Console.WriteLine("[..] Loading module: " + targetPath);

				var mod = ModuleDefMD.Load(targetPath);
				Console.WriteLine("[OK] Module loaded. Total types: " + mod.GetTypes().Count());

				// Batch-decompile all types (public or not, when --all is passed) with the selected decompiler.
				bool allTypes = args.Any(a => a == "--all");
				var candidates = (allTypes
						? mod.GetTypes().ToList()
						: mod.GetTypes().Where(t => t.IsPublic && !t.IsEnum && !t.IsInterface).ToList());
				Console.WriteLine("[..] " + targetDecompiler.UniqueNameUI + "-decompiling " + candidates.Count + " types...");
				int ok = 0, failed = 0;
				var failures = new List<string>();
				foreach (var t in candidates) {
					var o = new StringDecompilerOutput();
					try {
						targetDecompiler.Decompile(t, o, new DecompilationContext());
						ok++;
					}
					catch (Exception ex) {
						failed++;
						failures.Add(t.FullName + " :: " + ex.GetType().Name + ": " + ex.Message);
					}
				}
				Console.WriteLine("[RESULT] " + targetDecompiler.UniqueNameUI + " batch: ok=" + ok + " failed=" + failed);
				foreach (var f in failures.Take(10))
					Console.WriteLine("  FAIL " + f);

				// Show one representative output (first non-empty).
				var first = candidates.FirstOrDefault(t => t.Methods.Any(m => m.HasBody));
				if (first is not null) {
					var output = new StringDecompilerOutput();
					targetDecompiler.Decompile(first, output, new DecompilationContext());
					var text = output.ToString();
					Console.WriteLine("----- " + targetDecompiler.UniqueNameUI + " OUTPUT FOR " + first.FullName + " (" + text.Length + " chars) -----");
					Console.WriteLine(text);
					Console.WriteLine("----- END -----");
				}

				bool looksLikeVb = ok > 0;
				Console.WriteLine("[RESULT] looksLikeVb=" + looksLikeVb);
				return (ok > 0 && failed == 0) ? 0 : 2;
			}
			catch (Exception ex) {
				Console.WriteLine("[EXCEPTION] " + ex);
				return 1;
			}
		}
	}
}