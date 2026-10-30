using System;
using System.IO;

public static class CatchShapes {
	public static void NamedCatch() {
		try {
			File.OpenRead("x");
		}
		catch (IOException ex) {
			Console.WriteLine(ex.Message);
		}
	}

	public static void UnnamedCatch() {
		try {
			File.OpenRead("x");
		}
		catch (IOException) {
			Console.WriteLine("io error");
		}
	}

	public static void UnnamedCatchFilter() {
		try {
			File.OpenRead("x");
		}
		catch (IOException) when (DateTime.Now.Day > 1) {
			Console.WriteLine("filtered io error");
		}
	}

	public static void MultipleUnnamedCatches() {
		try {
			File.OpenRead("x");
		}
		catch (IOException) {
			Console.WriteLine("io");
		}
		catch (UnauthorizedAccessException) {
			Console.WriteLine("unauthorized");
		}
		catch (Exception) {
			Console.WriteLine("generic");
		}
	}

	public static void MixedCatches() {
		try {
			File.OpenRead("x");
		}
		catch (IOException ex) {
			Console.WriteLine(ex.Message);
		}
		catch (UnauthorizedAccessException) {
			Console.WriteLine("unauthorized");
		}
		catch (Exception) {
			Console.WriteLine("generic");
		}
	}

	public static void CatchWithoutType() {
		try {
			File.OpenRead("x");
		}
		catch {
			Console.WriteLine("bare catch");
		}
	}
}