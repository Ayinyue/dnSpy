using System;
using System.Collections.Generic;
using System.Text;

namespace SpTestNamespace
{
	public class GenericMethods<T> where T : IComparable<T>
	{
		public T FindMax(T a, T b) => a.CompareTo(b) >= 0 ? a : b;

		public List<T> FilterAbove(T threshold, IEnumerable<T> source) {
			var result = new List<T>();
			foreach (var item in source)
				if (item.CompareTo(threshold) > 0)
					result.Add(item);
			return result;
		}

		public Dictionary<string, List<T>> GroupByFirst(IEnumerable<T> items) {
			var dict = new Dictionary<string, List<T>>();
			foreach (var item in items) {
				var key = item?.ToString() ?? "null";
				if (!dict.ContainsKey(key))
					dict[key] = new List<T>();
				dict[key].Add(item);
			}
			return dict;
		}
	}

	public class OperatorsAndIndexers
	{
		readonly int[] values = new int[10];

		public int this[int index] {
			get => values[index];
			set => values[index] = value;
		}

		public string this[string key] => key.ToUpperInvariant();

		public static OperatorsAndIndexers operator +(OperatorsAndIndexers a, OperatorsAndIndexers b) {
			var result = new OperatorsAndIndexers();
			for (int i = 0; i < 10; i++)
				result[i] = a[i] + b[i];
			return result;
		}

		public static implicit operator int(OperatorsAndIndexers o) => o.values[0];

		public delegate void SimpleDelegate(string message);

		public event SimpleDelegate CustomEvent;

		public void RaiseEvent(string msg) => CustomEvent?.Invoke(msg);
	}

	public static class Extensions
	{
		public static string Reverse(this string s) {
			var chars = s.ToCharArray();
			Array.Reverse(chars);
			return new string(chars);
		}

		public static int WordCount(this string s) => s.Split(' ').Length;
	}

	public class NestedGenerics
	{
		public Dictionary<string, Dictionary<int, List<string>>> BuildNested() {
			return new Dictionary<string, Dictionary<int, List<string>>>();
		}

		public void ProcessNested(Dictionary<string, Dictionary<int, List<string>>> data) {
			foreach (var kvp in data) {
				foreach (var inner in kvp.Value) {
					foreach (var item in inner.Value)
						Console.WriteLine($"{kvp.Key}[{inner.Key}] = {item}");
				}
			}
		}

		public T? NullableGeneric<T>(T? value) where T : struct {
			return value.HasValue ? value.Value : default(T);
		}
	}
}
