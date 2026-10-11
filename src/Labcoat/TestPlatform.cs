using System.Xml;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>The test platform a project runs on (<see cref="TestPlatform.Classify"/>).</summary>
public enum TestRunner
{
    /// <summary>No test framework or test platform setting: not a test project.</summary>
    None,
    /// <summary>A Microsoft.Testing.Platform test app: Labcoat can run it.</summary>
    Mtp,
    /// <summary>A test project on VSTest (or another runner that is not Microsoft.Testing.Platform): Labcoat can't run it.</summary>
    VsTest,
}

/// <summary>What <see cref="TestPlatform.Classify"/> found.</summary>
/// <param name="Framework">MSTest | NUnit | xUnit v2 | xUnit v3 | TUnit, or null when it isn't known.</param>
/// <param name="Evidence">The packages and settings that decided it, e.g. ["Microsoft.NET.Test.Sdk", "MSTest.TestAdapter"].</param>
public sealed record TestPlatformInfo(TestRunner Runner, string? Framework, IReadOnlyList<string> Evidence)
{
    public bool IsTestProject => Runner != TestRunner.None;
}

/// <summary>The settings <see cref="TestPlatform.Classify"/> looks at: properties, PackageReference ids and SDK names.</summary>
public sealed record ProjectFacts(IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> Packages, IReadOnlyList<string> Sdks)
{
    public static readonly ProjectFacts Empty = new(new Dictionary<string, string>(), [], []);
}

/// <summary>
/// Tells Microsoft.Testing.Platform test projects from VSTest ones, from the project's settings: read statically from the
/// project file and the Directory.* files above it (<see cref="ReadStatic"/>, works before a restore), or from an MSBuild
/// evaluation (which also sees conditions, imports and, once restored, the properties the test packages set). See
/// docs/troubleshooting.md "My project uses VSTest" and docs/migrating-from-vstest.md.
/// </summary>
public static class TestPlatform
{
    public const string MSTest = "MSTest";
    public const string NUnit = "NUnit";
    public const string XUnitV2 = "xUnit v2";
    public const string XUnitV3 = "xUnit v3";
    public const string TUnit = "TUnit";

    private const string TestSdk = "Microsoft.NET.Test.Sdk";
    private const string MSTestSdk = "MSTest.Sdk";
    private const string MSTestAdapter = "MSTest.TestAdapter";
    private const string NUnitAdapter = "NUnit3TestAdapter";
    private const string XUnitVsAdapter = "xunit.runner.visualstudio";
    private const string IsTestingPlatformApplication = "IsTestingPlatformApplication";
    private const string EnableMSTestRunner = "EnableMSTestRunner";
    private const string EnableNUnitRunner = "EnableNUnitRunner";

    /// <summary>Where the docs explain the switch (linked from the project's message).</summary>
    public const string DocsUrl = "https://github.com/eriklieben/labcoat/blob/main/docs/troubleshooting.md#my-project-uses-vstest";

    /// <summary>The MSBuild properties <see cref="Classify"/> reads (evaluations ask for these).</summary>
    public static readonly string[] Properties =
        [IsTestingPlatformApplication, "TestingPlatformDotnetTestSupport", "UseMicrosoftTestingPlatformRunner", EnableMSTestRunner,
         EnableNUnitRunner, "IsTestProject", "UsingMSTestSdk", "UseVSTest"];

    /// <summary>Packages that make a project a test project; the VSTest adapters are listed so the message can name them.</summary>
    private static readonly string[] EvidencePackages = [TestSdk, MSTest, MSTestAdapter, NUnitAdapter, "xunit", XUnitVsAdapter];

    // ------------------------------------------------------------------ the decision

    /// <summary>
    /// The decision, framework first (a framework's runner switch is what counts, whatever else is referenced):
    /// TUnit is always MTP; MSTest.Sdk is MTP unless UseVSTest; xunit.v3* is MTP; MSTest / NUnit need
    /// EnableMSTestRunner / EnableNUnitRunner (their adapters set IsTestingPlatformApplication from it); xUnit v2 is VSTest.
    /// Then the generic signs of MTP (IsTestingPlatformApplication, TestingPlatformDotnetTestSupport,
    /// UseMicrosoftTestingPlatformRunner, a runner switch, a Microsoft.Testing.Platform* package), and of VSTest
    /// (Microsoft.NET.Test.Sdk, IsTestProject).
    /// </summary>
    public static TestPlatformInfo Classify(ProjectFacts facts)
    {
        var packages = new HashSet<string>(facts.Packages, StringComparer.OrdinalIgnoreCase);
        bool True(string name) => facts.Properties.TryGetValue(name, out var v) && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        bool Prefixed(string prefix) => packages.Any(p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

        if (Prefixed(TUnit) || facts.Sdks.Any(s => s.StartsWith(TUnit, StringComparison.OrdinalIgnoreCase))) return Mtp(TUnit);
        if (True("UsingMSTestSdk") || facts.Sdks.Contains(MSTestSdk, StringComparer.OrdinalIgnoreCase))
            return True("UseVSTest") ? new TestPlatformInfo(TestRunner.VsTest, MSTest, [MSTestSdk, "UseVSTest=true"]) : Mtp(MSTest);
        if (Prefixed("xunit.v3")) return Mtp(XUnitV3);
        return Framework(packages, True) ?? Generic(packages, True, Prefixed);
    }

    /// <summary>Classify on evaluated / read properties and PackageReference ids (no SDK names).</summary>
    public static TestPlatformInfo Classify(IReadOnlyDictionary<string, string> properties, IEnumerable<string> packages) =>
        Classify(new ProjectFacts(properties, packages.ToList(), []));

    private static TestPlatformInfo Mtp(string? framework) => new(TestRunner.Mtp, framework, []);

    /// <summary>MSTest / NUnit / xUnit v2 by their packages; null when none of them is referenced.</summary>
    private static TestPlatformInfo? Framework(HashSet<string> packages, Func<string, bool> isTrue)
    {
        string? framework = null;
        var on = false;
        if (packages.Contains(MSTest) || packages.Contains(MSTestAdapter))
            (framework, on) = (MSTest, isTrue(EnableMSTestRunner));
        else if (packages.Contains(NUnitAdapter))
            (framework, on) = (NUnit, isTrue(EnableNUnitRunner));
        else if (packages.Contains(XUnitVsAdapter) || (packages.Contains("xunit") && packages.Contains(TestSdk)))
            // xUnit v2 has no MTP runner of its own; YTest.MTP.XUnit2 adds one
            (framework, on) = (XUnitV2, packages.Contains("YTest.MTP.XUnit2"));
        if (framework is null) return null;
        return on ? Mtp(framework) : new TestPlatformInfo(TestRunner.VsTest, framework, Evidence(packages));
    }

    private static TestPlatformInfo Generic(HashSet<string> packages, Func<string, bool> isTrue, Func<string, bool> prefixed)
    {
        if (isTrue(IsTestingPlatformApplication) || isTrue("TestingPlatformDotnetTestSupport") || isTrue("UseMicrosoftTestingPlatformRunner")
            || isTrue(EnableMSTestRunner) || isTrue(EnableNUnitRunner) || prefixed("Microsoft.Testing.Platform"))
            return Mtp(null);
        if (packages.Contains(TestSdk)) return new TestPlatformInfo(TestRunner.VsTest, null, Evidence(packages));
        if (isTrue("IsTestProject")) return new TestPlatformInfo(TestRunner.VsTest, null, ["IsTestProject=true"]);
        return new TestPlatformInfo(TestRunner.None, null, []);
    }

    private static List<string> Evidence(HashSet<string> packages) => EvidencePackages.Where(packages.Contains).ToList();

    // ------------------------------------------------------------------ reading the files (no restore, no MSBuild)

    /// <summary>Property names collected by <see cref="ReadStatic"/> (and OutputType for the message).</summary>
    private static readonly HashSet<string> StaticProperties = new(Properties, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The settings in the project file and the Directory.Build.props / .targets / Directory.Packages.props files above it,
    /// without MSBuild: properties set to a literal value, PackageReference / GlobalPackageReference ids and SDK names.
    /// Conditions and imports are not evaluated, so a negative answer is confirmed by an MSBuild evaluation.
    /// </summary>
    public static ProjectFacts ReadStatic(string projectPath)
    {
        var files = ProjectDiscovery.DirectoryFiles(projectPath).ToList();
        // MSBuild's order: Directory.Build.props (outer first), the project, Directory.Build.targets
        var props = files.Where(f => !f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)).Reverse();
        var targets = files.Where(f => f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)).Reverse();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<string>();
        var sdks = new List<string>();
        foreach (var file in props.Append(projectPath).Concat(targets))
            if (TryLoad(file) is { } doc) Collect(doc, properties, packages, sdks);
        return new ProjectFacts(properties, packages, sdks);
    }

    /// <summary><see cref="ReadStatic"/> on the text of a project file alone.</summary>
    public static ProjectFacts ReadText(string projectXml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(projectXml); }
        catch (XmlException) { return ProjectFacts.Empty; }
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<string>();
        var sdks = new List<string>();
        Collect(doc, properties, packages, sdks);
        return new ProjectFacts(properties, packages, sdks);
    }

    private static XDocument? TryLoad(string file)
    {
        try { return XDocument.Load(file); }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; /* unreadable: MSBuild evaluation decides */ }
    }

    private static void Collect(XDocument doc, Dictionary<string, string> properties, List<string> packages, List<string> sdks)
    {
        if (doc.Root is not { } root) return;
        AddSdks(sdks, (string?)root.Attribute("Sdk"));
        foreach (var e in root.Descendants())
        {
            var name = e.Name.LocalName;
            if (name is "PackageReference" or "GlobalPackageReference")
                packages.AddRange(Split((string?)e.Attribute("Include")));
            else if (name == "Sdk") AddSdks(sdks, (string?)e.Attribute("Name"));
            else if (name == "Import") AddSdks(sdks, (string?)e.Attribute("Sdk"));
            else if (StaticProperties.Contains(name) && !e.HasElements) properties[name] = e.Value.Trim();
        }
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => !s.Contains("$(", StringComparison.Ordinal));

    private static void AddSdks(List<string> sdks, string? value) => sdks.AddRange(Split(value).Select(s => s.Split('/')[0].Trim()));

    // ------------------------------------------------------------------ what to tell the user

    /// <summary>"Microsoft.NET.Test.Sdk + MSTest.TestAdapter"</summary>
    private static string EvidenceText(TestPlatformInfo info) => info.Evidence.Count > 0 ? $" ({string.Join(" + ", info.Evidence)})" : "";

    /// <summary>The switch to Microsoft.Testing.Platform for the project's framework (one sentence, no trailing period).</summary>
    public static string Fix(TestPlatformInfo info) => info.Framework switch
    {
        MSTest when info.Evidence.Contains(MSTestSdk) => "remove <UseVSTest>true</UseVSTest> (MSTest.Sdk uses Microsoft.Testing.Platform by default)",
        MSTest => "set <EnableMSTestRunner>true</EnableMSTestRunner> and <OutputType>Exe</OutputType> (or use MSTest.Sdk)",
        NUnit => "set <EnableNUnitRunner>true</EnableNUnitRunner> and <OutputType>Exe</OutputType>, with NUnit3TestAdapter 6 or later",
        XUnitV2 => "move to xUnit v3: replace xunit and xunit.runner.visualstudio with xunit.v3.mtp-v2, and set <OutputType>Exe</OutputType>",
        _ => "turn on the Microsoft.Testing.Platform runner of its test framework (MSTest: EnableMSTestRunner, NUnit: EnableNUnitRunner, " +
             "xUnit: xunit.v3.mtp-v2, TUnit: always on) and set <OutputType>Exe</OutputType>",
    };

    /// <summary>
    /// The one message for a project Labcoat doesn't run, e.g. "SampleTests uses VSTest (Microsoft.NET.Test.Sdk +
    /// MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform projects: set … (or use MSTest.Sdk)."
    /// </summary>
    public static string Summary(string projectName, TestPlatformInfo info) => info.Runner == TestRunner.None
        ? $"{projectName} is not a Microsoft.Testing.Platform test project: it references no test framework and IsTestingPlatformApplication is not true."
        : $"{projectName} uses VSTest{EvidenceText(info)}. Labcoat runs Microsoft.Testing.Platform projects.";

    /// <summary>The log line: <see cref="Summary"/>, the switch and the docs link.</summary>
    public static string Message(string projectName, TestPlatformInfo info, bool restored)
    {
        var fix = info.Runner == TestRunner.None ? "" : $": {Fix(info)}";
        var summary = Summary(projectName, info);
        var head = fix.Length > 0 ? summary[..^1] + fix + "." : summary;
        var notRestored = restored ? "" : " (Not restored yet: Labcoat read the project files. Run `dotnet restore` if a package should switch it.)";
        return $"{head}{notRestored} Labcoat doesn't start it. See {DocsUrl}";
    }

    /// <summary>TestProject.diagnostics for the project: the summary with the switch as the hint, and the restore note.</summary>
    public static List<ProjectDiagnostic> Diagnostics(string projectName, TestPlatformInfo info, bool restored)
    {
        var hint = info.Runner == TestRunner.None
            ? "If it is a test project, " + Fix(info) + ", then Rediscover."
            : char.ToUpperInvariant(Fix(info)[0]) + Fix(info)[1..] + ", then Rediscover.";
        var list = new List<ProjectDiagnostic> { new(LogLevels.Warning, Summary(projectName, info), hint, Link: DocsUrl) };
        if (!restored)
            list.Add(new ProjectDiagnostic(LogLevels.Info, $"{projectName} is not restored yet: Labcoat read its project files.",
                "Run `dotnet restore` if the Microsoft.Testing.Platform switch comes from a package, then Rediscover."));
        return list;
    }
}
