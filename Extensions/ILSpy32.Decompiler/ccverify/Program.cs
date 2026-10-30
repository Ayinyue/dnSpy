using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace CcVerify {
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
		static async Task<int> Main(string[] args) {
			Console.OutputEncoding = Encoding.UTF8;
			try {
				// 1. Locate the ILSpy32 extension assembly (same reflection host as vbverify).
				var asmDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
				var extPath = Path.Combine(asmDir, "dnSpy.Decompiler.ILSpy32.x.dll");
				if (!File.Exists(extPath)) {
					Console.WriteLine("[FAIL] dnSpy.Decompiler.ILSpy32.x.dll not found next to host: " + extPath);
					return 1;
				}
				var extAsm = Assembly.LoadFrom(extPath);
				var creatorType = extAsm.GetType("dnSpy.Decompiler.ILSpy32.CSharp.DecompilerCreator", throwOnError: true)!;
				var creator = Activator.CreateInstance(creatorType)!;
				var createMethod = creatorType.GetMethod("Create")!;
				var decompilers = ((IEnumerable<IDecompiler>)createMethod.Invoke(creator, null)!).ToList();
				var cs = decompilers.FirstOrDefault(d => d.FileExtension == ".cs");
				if (cs is null) {
					Console.WriteLine("[FAIL] No C# decompiler found!");
					return 1;
				}
				Console.WriteLine("[OK] C# decompiler: " + cs.UniqueNameUI);

				// 2. Load target assembly, decompile each type to C# text.
				if (args.Length == 0 || !File.Exists(args[0])) {
					Console.WriteLine("[FAIL] Usage: ccverify path/to/assembly.dll [--all] [--type Full.Name] [--list-types]");
					return 1;
				}
				var targetPath = Path.GetFullPath(args[0]);
				Console.WriteLine("[..] Loading module: " + targetPath);
				var mod = ModuleDefMD.Load(targetPath);
				if (args.Any(a => a == "--list-types")) {
					foreach (var t in mod.GetTypes().Where(t => t.IsPublic && !t.IsEnum && !t.IsInterface))
						Console.WriteLine(t.FullName);
					return 0;
				}
				bool allTypes = args.Any(a => a == "--all");
				var typeFilter = args.SkipWhile(a => a != "--type").Skip(1).FirstOrDefault();
				var maxTypes = args.SkipWhile(a => a != "--max").Skip(1).FirstOrDefault();
				int maxCount = maxTypes != null && int.TryParse(maxTypes, out var mc) ? mc : int.MaxValue;
				var candidates = (allTypes ? mod.GetTypes() : mod.GetTypes().Where(t => t.IsPublic && !t.IsEnum && !t.IsInterface)).ToList();
				if (typeFilter != null)
					candidates = candidates.Where(t => t.FullName == typeFilter).ToList();
				candidates = candidates.Take(maxCount).ToList();
				Console.WriteLine("[..] Decompiling " + candidates.Count + " types to C#...");

				int okCount = 0, failCount = 0;
				foreach (var t in candidates) {
					var o = new StringDecompilerOutput();
					try {
						cs.Decompile(t, o, new DecompilationContext());
					}
					catch (Exception ex) {
						Console.WriteLine("  FAIL decompile " + t.FullName + " :: " + ex.GetType().Name + ": " + ex.Message);
						continue;
					}
					var csText = o.ToString();
					var result = new ICSharpCode.CodeConverter.ConversionResult();
					try {
						result = await ConvertCsToVb(csText);
						if (result.Success) {
							okCount++;
							Console.WriteLine("----- CodeConverter VB OUTPUT FOR " + t.FullName + " (" + csText.Length + " C# chars) -----");
							Console.WriteLine(result.ConvertedCode);
							Console.WriteLine("[OK] converted");
							Console.WriteLine("----- END -----");
						}
						else {
							failCount++;
							Console.WriteLine("----- CodeConverter FAIL FOR " + t.FullName + " -----");
							Console.WriteLine(result.GetExceptionsAsString());
							Console.WriteLine("----- END -----");
						}
					}
					catch (Exception ex) {
						failCount++;
						Console.WriteLine("----- CodeConverter EXCEPTION FOR " + t.FullName + " -----");
						Console.WriteLine("[EXCEPTION] CodeConverter: " + ex);
						Console.WriteLine("----- END -----");
					}
				}
				Console.WriteLine("[RESULT] CodeConverter C#->VB batch: ok=" + okCount + " failed=" + failCount + " (of " + candidates.Count + ")");
				return 0;
			}
			catch (Exception ex) {
				Console.WriteLine("[EXCEPTION] " + ex);
				return 1;
			}
		}

		static async Task<ICSharpCode.CodeConverter.ConversionResult> ConvertCsToVb(string csText) {
			var options = new ICSharpCode.CodeConverter.CodeWithOptions(csText)
				.SetFromLanguage(Microsoft.CodeAnalysis.LanguageNames.CSharp)
				.SetToLanguage(Microsoft.CodeAnalysis.LanguageNames.VisualBasic)
				.WithTypeReferences();
			return await ICSharpCode.CodeConverter.CodeConverter.ConvertAsync(options);
		}
	}
}