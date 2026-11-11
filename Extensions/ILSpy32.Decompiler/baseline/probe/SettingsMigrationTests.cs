using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Settings;

// Minimal in-memory ISettingsService mirroring dnSpy's section persistence, so the
// settings impls' load/save/migrate paths can be exercised without launching dnSpy.
// The impl types are internal to the 3.2 extension, so reflection drives them.
sealed class MemorySettingsSection : ISettingsSection {
	readonly Dictionary<string, string> attrs = new Dictionary<string, string>(StringComparer.Ordinal);
	public string Name { get; }
	public MemorySettingsSection(string name) => Name = name;
	public (string key, string value)[] Attributes => attrs.Select(a => (a.Key, a.Value)).ToArray();
	public void Attribute<T>(string name, T value) => attrs[name] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
	public T Attribute<T>(string name) {
		if (!attrs.TryGetValue(name, out var s))
			return default;
		if (typeof(T) == typeof(bool) && bool.TryParse(s, out var b))
			return (T)(object)b;
		if (typeof(T) == typeof(bool?) && bool.TryParse(s, out var b2))
			return (T)(object)(bool?)b2;
		return default;
	}
	public void RemoveAttribute(string name) => attrs.Remove(name);
	public void CopyFrom(ISettingsSection section) {
		foreach (var (k, v) in section.Attributes)
			attrs[k] = v;
	}
	public ISettingsSection CreateSection(string name) => new MemorySettingsSection(name);
	public ISettingsSection GetOrCreateSection(string name) => new MemorySettingsSection(name);
	public void RemoveSection(string name) { }
	public void RemoveSection(ISettingsSection section) { }
	public ISettingsSection? TryGetSection(string name) => null;
	public ISettingsSection[] Sections => Array.Empty<ISettingsSection>();
	public ISettingsSection[] SectionsWithName(string name) => Array.Empty<ISettingsSection>();
}

sealed class MemorySettingsService : ISettingsService {
	readonly Dictionary<string, ISettingsSection> sections = new Dictionary<string, ISettingsSection>(StringComparer.Ordinal);
	static string Key(Guid g) => g.ToString();
	public ISettingsSection GetOrCreateSection(Guid guid) {
		var k = Key(guid);
		if (!sections.TryGetValue(k, out var s))
			sections[k] = s = new MemorySettingsSection(k);
		return s;
	}
	public ISettingsSection RecreateSection(Guid guid) {
		sections.Remove(Key(guid));
		return GetOrCreateSection(guid);
	}
	public ISettingsSection[] Sections => new List<ISettingsSection>(sections.Values).ToArray();
	public void RemoveSection(Guid guid) => sections.Remove(Key(guid));
	public void RemoveSection(ISettingsSection section) => sections.Remove(section.Name);
}

static class SettingsMigrationTests {
	static readonly Type ImplType = Type.GetType("dnSpy.Decompiler.ILSpy32.Settings.DecompilerSettings32Impl, dnSpy.Decompiler.ILSpy32.x");
	static readonly Type VbImplType = Type.GetType("dnSpy.Decompiler.ILSpy32.Settings.VbDecompilerSettings32Impl, dnSpy.Decompiler.ILSpy32.x");

	static object CreateImpl(ISettingsService svc, Type type) {
		var ctor = type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
			.First(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(ISettingsService));
		return ctor.Invoke(new object[] { svc });
	}

	static object GetEngineSettings(object impl) => impl.GetType().GetProperty("EngineSettings")!.GetValue(impl)!;
	static object GetProp(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;
	static void SetProp(object o, string name, object value) => o.GetType().GetProperty(name)!.SetValue(o, value);

	public static int Run() {
		int failures = 0;
		void Check(bool ok, string what) {
			Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
			if (!ok) failures++;
		}

		var legacy = new Guid("6745457F-254B-4B7B-90F1-F948F0721C3B");
		var csharp = new Guid("7E19E9D1-4B7A-4E12-9A03-6D7D9F52C331");

		// Scenario 1: user customized options under 2.3; migration must carry them over.
		var svc = new MemorySettingsService();
		GetSection(svc, legacy).Attribute("ShowTokenAndRvaComments", false);
		GetSection(svc, legacy).Attribute("ShowXmlDocumentation", false);
		GetSection(svc, legacy).Attribute("AlwaysGenerateExceptionVariableForCatchBlocksUnlessTypeIsObject", true);
		GetSection(svc, legacy).Attribute("YieldReturn", false);

		var csharpImpl = CreateImpl(svc, ImplType);
		var vbImpl = CreateImpl(svc, VbImplType);

		Check(!(bool)GetProp(csharpImpl, "ShowTokenAndRvaComments"), "C# ShowTokenAndRvaComments migrated false");
		Check(!(bool)GetProp(GetEngineSettings(csharpImpl), "ShowXmlDocumentation"), "C# ShowXmlDocumentation migrated false");
		Check((bool)GetProp(GetEngineSettings(csharpImpl), "AlwaysGenerateExceptionVariableForCatchBlocks"), "C# catch-variable (2.3 long key) migrated true");
		Check(!(bool)GetProp(GetEngineSettings(csharpImpl), "YieldReturn"), "C# YieldReturn migrated false");
		Check(!(bool)GetProp(vbImpl, "ShowTokenAndRvaComments"), "VB ShowTokenAndRvaComments migrated false");
		Check(!(bool)GetProp(GetEngineSettings(vbImpl), "QueryExpressions"), "VB QueryExpressions pinned false");

		// Persistence: mutating through the option surface writes to the 3.2 section.
		SetProp(csharpImpl, "ShowTokenAndRvaComments", true);
		Check(GetSection(svc, csharp).Attribute<bool>("ShowTokenAndRvaComments"), "C# save writes section");

		// Scenario 2: no 2.3 section (fresh install) -> defaults, no exception.
		var svc2 = new MemorySettingsService();
		var csharp2 = CreateImpl(svc2, ImplType);
		Check((bool)GetProp(csharp2, "ShowTokenAndRvaComments"), "fresh install keeps default true");

		// Scenario 3: migration must not re-run after the 3.2 section has content.
		var svc3 = new MemorySettingsService();
		GetSection(svc3, legacy).Attribute("ShowTokenAndRvaComments", false);
		var impl3 = CreateImpl(svc3, ImplType);
		SetProp(impl3, "ShowTokenAndRvaComments", true); // user flips back; section has content
		var impl3b = CreateImpl(svc3, ImplType);
		Check((bool)GetProp(impl3b, "ShowTokenAndRvaComments"), "second start keeps the user's new value");

		return failures;
	}

	static MemorySettingsSection GetSection(MemorySettingsService svc, Guid g) => (MemorySettingsSection)svc.GetOrCreateSection(g);
}
