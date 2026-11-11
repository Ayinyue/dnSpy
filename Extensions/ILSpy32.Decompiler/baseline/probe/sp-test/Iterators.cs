using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SpTestNamespace
{
	public class IteratorVariety
	{
		readonly List<string> source = new List<string> { "a", "bb", "ccc" };

		public IEnumerable<int> SimpleYield(int max) {
			for (int i = 0; i < max; i++)
				yield return i;
		}

		public IEnumerable<string> YieldWithTryFinally(string path) {
			var reader = new StreamReader(path);
			try {
				string line;
				while ((line = reader.ReadLine()) != null)
					yield return line;
			}
			finally {
				reader.Dispose();
			}
		}

		public IEnumerable<int> NestedLoops(int n) {
			for (int i = 0; i < n; i++) {
				for (int j = i; j < n; j++) {
					if ((i + j) % 3 == 0)
						continue;
					yield return i * n + j;
				}
			}
			yield return -1;
		}

		public IEnumerable<string> YieldBreakAndMultiple(int mode) {
			if (mode == 0)
				yield break;
			foreach (var s in source) {
				if (mode == 1 && s.Length > 2)
					continue;
				yield return s.ToUpperInvariant();
			}
			if (mode == 2)
				yield break;
			for (int i = 0; i < 2; i++)
				yield return "extra" + i;
		}

		public IEnumerator NonGeneric() {
			yield return 1;
			yield return 2;
		}

		public async IAsyncEnumerable<int> AsyncStream(int max) {
			for (int i = 0; i < max; i++)
				await Task.Delay(1);
			for (int i = 0; i < max; i++)
				yield return i;
		}
	}
}
