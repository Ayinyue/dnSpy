using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SpTestNamespace
{
	public class StatementVariety
	{
		int counter;
		readonly List<string> items = new List<string>();

		public int SimpleStatements(int a, int b) {
			int sum = a + b;
			sum = sum * 2;
			counter++;
			Console.WriteLine("sum: " + sum);
			return sum;
		}

		public int ControlFlow(int n) {
			if (n < 0) {
				return -1;
			}
			switch (n % 3) {
				case 0:
					n += 10;
					break;
				case 1:
					n += 20;
					goto case 0;
				default:
					n += 30;
					break;
			}
			for (int i = 0; i < n; i++) {
				if (i % 2 == 0)
					continue;
				n -= i;
			}
			while (n > 100) {
				n -= 7;
			}
			do {
				n++;
			} while (n < 3);
			try {
				n = n / (n - n);
			}
			catch (DivideByZeroException ex) {
				Console.WriteLine(ex.Message);
				throw;
			}
			finally {
				counter = 0;
			}
			return n;
		}

		public async Task<int> AsyncWork(int delay) {
			await Task.Delay(delay);
			var text = await File.ReadAllTextAsync("some.txt");
			int len = text.Length;
			Console.WriteLine("len: " + len);
			return len + counter;
		}

		public IEnumerable<int> CountTo(int max) {
			for (int i = 0; i < max; i++)
				yield return i;
			yield break;
		}

		public void LinqAndLock(object gate) {
			lock (gate) {
				items.Add("locked");
			}
			var evens = items.Where(s => s.Length > 2).Select(s => s.Length).ToList();
			using (var reader = new StringReader(items[0])) {
				var line = reader.ReadLine();
				Console.WriteLine(line);
			}
			foreach (var item in items) {
				Console.WriteLine(item);
			}
		}

		// Regression sample for the sequence-point builder: the lambda body references the
		// implicit 'this' (counter), so IntroduceQueryExpressions moves the lambda's IL
		// instructions into the outer statement's sequence point. See audit §8.63/§8.74.
		public int[] LambdaThis(int[] columns) {
			int x = 1;
			var r = columns.Select(a => counter + a).ToArray();
			x++;
			return r;
		}
	}
}
