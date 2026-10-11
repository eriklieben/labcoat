using System.Diagnostics;
using System.Text;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// A small workspace of real projects, written to a temp directory and built once per test run (shared by every
/// end-to-end test of the explorer): a product library (Calc), an MSTest project that tests it (X.Tests: passing,
/// failing, skipped, flaky, slow, big-output and trace-id tests in three files), an MSTest project without tests
/// (Empty.Tests), a plain console app (NotMtp), which is not a Microsoft.Testing.Platform test app, and a larger product
/// assembly (Big.Lib, a few hundred KB of generated code) with its tests (Big.Tests): a warm host that has used only a
/// little of it has most of its metadata pages still unread, which is what an in-place rewrite of the file corrupts.
/// </summary>
internal sealed class ScratchWorkspace
{
    private static readonly object s_gate = new();
    private static Task<ScratchWorkspace>? s_instance;

    public required string Root { get; init; }
    public string CalcCs => Path.Combine(Root, "Calc", "Ops.cs");
    public string TestsDir => Path.Combine(Root, "X.Tests");
    public string TestsProject => Path.Combine(TestsDir, "X.Tests.csproj");
    public string AddTestsCs => Path.Combine(TestsDir, "AddTests.cs");
    public string MiscTestsCs => Path.Combine(TestsDir, "MiscTests.cs");
    public string EmptyProject => Path.Combine(Root, "Empty.Tests", "Empty.Tests.csproj");
    public string NotMtpProject => Path.Combine(Root, "NotMtp", "NotMtp.csproj");
    public string FlakyCounter => Path.Combine(Root, "flaky.count");
    public string TestsDll => Path.Combine(TestsDir, "bin", "Debug", "net10.0", "X.Tests.dll");
    public string BigTestsProject => Path.Combine(Root, "Big.Tests", "Big.Tests.csproj");
    /// <summary>Big.Lib.dll as copied into Big.Tests' output folder (the file its test host loads).</summary>
    public string BigLibInTestOutput => Path.Combine(Root, "Big.Tests", "bin", "Debug", "net10.0", "Big.Lib.dll");
    public string NotMtpDll => Path.Combine(Root, "NotMtp", "bin", "Debug", "net10.0", "NotMtp.dll");

    public const string TraceId = "0123456789abcdef0123456789abcdef";

    /// <summary>The workspace, built on first use (~10-20 s); later calls return the same one.</summary>
    public static Task<ScratchWorkspace> GetAsync()
    {
        lock (s_gate) return s_instance ??= Task.Run(CreateAsync);
    }

    private static async Task<ScratchWorkspace> CreateAsync()
    {
        // Builds started by these tests (and by the explorers under test) must not leave MSBuild nodes behind.
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        var root = CreateRealTempDirectory("labcoat-explorer-e2e-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, recursive: true); } catch { } };
        var ws = new ScratchWorkspace { Root = root };
        ws.WriteAll();
        var (code, output) = await DotnetAsync(root, "build", Path.Combine(root, "ws.slnx"), "-nologo", "-v:q", "-tl:off");
        if (code != 0) throw new InvalidOperationException("Building the scratch workspace failed:\n" + output);
        return ws;
    }

    /// <summary>
    /// A new temp directory by its real path. On macOS the temp folder (/var/folders/…) is a symlink into /private/var:
    /// MSBuild resolves some paths through it and some not, so one project is restored under two paths at once and NuGet
    /// races itself ("…csproj.nuget.g.props already exists").
    /// </summary>
    internal static string CreateRealTempDirectory(string prefix) => RealPath(Directory.CreateTempSubdirectory(prefix).FullName);

    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current) is { Exists: true, LinkTarget: not null } link)
                current = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteAll()
    {
        Write("ws.slnx", """
            <Solution>
              <Project Path="Calc/Calc.csproj" />
              <Project Path="X.Tests/X.Tests.csproj" />
              <Project Path="Empty.Tests/Empty.Tests.csproj" />
              <Project Path="NotMtp/NotMtp.csproj" />
              <Project Path="Big.Lib/Big.Lib.csproj" />
              <Project Path="Big.Tests/Big.Tests.csproj" />
            </Solution>
            """);
        Write("Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(CalcCs, CalcSource);
        Write("X.Tests/X.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <OutputType>Exe</OutputType>
                <EnableMSTestRunner>true</EnableMSTestRunner>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="MSTest" Version="4.0.2" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../Calc/Calc.csproj" /></ItemGroup>
              <ItemGroup><Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(AddTestsCs, AddTestsSource);
        Write("X.Tests/MulTests.cs", """
            namespace X;
            [TestClass]
            public class MulTests
            {
                [TestMethod] public void Mul_Works() => Assert.AreEqual(6, Calc.Ops.Mul(2, 3));
            }
            """);
        File.WriteAllText(MiscTestsCs, MiscTestsSource(FlakyCounter));
        Write("Empty.Tests/Empty.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <EnableMSTestRunner>true</EnableMSTestRunner>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="MSTest" Version="4.0.2" /></ItemGroup>
            </Project>
            """);
        Write("Empty.Tests/Nothing.cs", "namespace Empty; public static class Nothing { }");
        // A plain console app: Labcoat treats it as a test project when passed explicitly; it can't speak MTP.
        Write("NotMtp/NotMtp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
            </Project>
            """);
        Write("NotMtp/Program.cs", """System.Console.Error.WriteLine("not a test app"); return 2;""");
        Write("Big.Lib/Big.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var big = new StringBuilder("namespace Big;\npublic static class Ops { public static int Add(int a, int b) { return a + b; } }\n");
        for (var c = 0; c < 300; c++)
        {
            big.Append("public static class C").Append(c).Append(" {\n");
            for (var m = 0; m < 10; m++)
                big.Append($"    public static int M{m}(int a, int b) {{ var s = \"c{c}m{m}\" + a; if (a > {m}) return s.Length + b * {c}; return a - b + {m}; }}\n");
            big.Append("}\n");
        }
        Write("Big.Lib/Generated.cs", big.ToString());
        Write("Big.Tests/Big.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <OutputType>Exe</OutputType>
                <EnableMSTestRunner>true</EnableMSTestRunner>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="MSTest" Version="4.0.2" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../Big.Lib/Big.Lib.csproj" /></ItemGroup>
              <ItemGroup><Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" /></ItemGroup>
            </Project>
            """);
        Write("Big.Tests/BigTests.cs", """
            namespace Big.Tests;
            [TestClass]
            public class BigTests
            {
                // Uses one method: the rest of Big.Lib's metadata stays unread.
                [TestMethod] public void First() => Assert.AreEqual(5, Big.Ops.Add(2, 3));

                // Reads all of it.
                [TestMethod]
                public void All()
                {
                    var n = 0;
                    foreach (var type in typeof(Big.Ops).Assembly.GetTypes())
                        foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                            if (method.GetParameters().Length == 2) { method.Invoke(null, [7, 3]); n++; }
                    Assert.IsGreaterThan(3000, n);
                }

                [TestMethod] public void Slow() => Thread.Sleep(1500);
            }
            """);
    }

    public const string CalcSource = """
        namespace Calc;

        public static class Ops
        {
            public static int Add(int a, int b)
            {
                return a + b;
            }

            public static int Mul(int a, int b)
            {
                return a * b;
            }
        }
        """;

    public const string AddTestsSource = """
        namespace X;
        [TestClass]
        public class AddTests
        {
            [TestMethod] public void Add_Works() => Assert.AreEqual(5, Calc.Ops.Add(2, 3));
        }
        """;

    private static string MiscTestsSource(string counter) => $$"""
        namespace X;
        [TestClass]
        public class MiscTests
        {
            [TestMethod, TestCategory("Integration")] public void Passes() { }
            [TestMethod] public void Fails() => Assert.Fail("boom");
            [TestMethod, Ignore("not now")] public void Skipped() { }
            [TestMethod] public void Traced() => Console.WriteLine("labcoat:traceId={{TraceId}}");
            [TestMethod] public void BigOutput() => Console.Write(new string('x', 70_000));
            [TestMethod] public void Slow() => Thread.Sleep(1500);
            [TestMethod]
            public void Flaky()
            {
                // Fails on every second run: 1 pass, 2 fail, 3 pass, ...
                var path = @"{{counter}}";
                var n = File.Exists(path) ? int.Parse(File.ReadAllText(path)) + 1 : 1;
                File.WriteAllText(path, n.ToString());
                Assert.AreEqual(1, n % 2, $"run {n}");
            }
        }
        """;

    public static async Task<(int Code, string Output)> DotnetAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }

    /// <summary>Waits until <paramref name="condition"/> holds (polling), or fails with <paramref name="what"/>.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }

    /// <summary>The explorer's log so far, for assertion messages.</summary>
    public static string Logs(Labcoat.Explorer e) =>
        string.Join("\n", e.Snapshot(includeLogs: true).Logs!.Select(l => $"{l.Timestamp[11..23]} {l.Level} [{l.ProjectId}] {l.Message}"));

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
