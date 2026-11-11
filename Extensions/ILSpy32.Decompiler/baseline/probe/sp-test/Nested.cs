using System;

namespace SpTestNamespace
{
	public class OuterWithNested
	{
		int outerField;

		public int OuterMethod() {
			outerField++;
			return outerField;
		}

		public class Inner
		{
			int innerField;

			public int InnerMethod() {
				innerField += 5;
				return innerField;
			}

			public class Deeper
			{
				public string DeeperMethod() => "deep";
			}
		}

		protected internal struct InnerStruct
		{
			public double Value;
		}
	}
}
