using System;

public class IndexerShapes {
	int[] _items = new int[10];

	// Basic indexer with a single int parameter
	public int this[int index] {
		get { return _items[index]; }
		set { _items[index] = value; }
	}

	// Indexer with two parameters
	public int this[string name, int index] {
		get { return name.Length + index; }
	}

	// Indexer with out/ref-like modifier (readonly getter only)
	public int this[long key, bool flag] {
		get { return (int)(key + (flag ? 1 : 0)); }
	}

	// Property that returns a string
	public string Name { get; set; }
}