namespace Labcoat;

/// <summary>Discovery diagnostics (TestProject.diagnostics): why a project found no tests or its test host did not start.</summary>
internal static class ProjectDiagnostics
{
    /// <summary>
    /// MSBuild properties read when the project is evaluated, for <see cref="Diagnose"/> and
    /// Explorer.AcceptsPlatformAfterBuild (the test platform settings and the restore output).
    /// </summary>
    public static readonly string[] Properties =
        ["OutputType", .. TestPlatform.Properties, Explorer.ProjectAssetsFile];

    /// <summary>
    /// Why a project discovered no tests (<paramref name="failure"/> null) or its test host could not start/answer:
    /// host exit code + stderr tail, OutputType not Exe, no Microsoft.Testing.Platform runner enabled, no tests found.
    /// </summary>
    public static List<ProjectDiagnostic> Diagnose(IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> packageReferences,
        string projectPath, string? targetPath, Exception? failure)
    {
        var list = new List<ProjectDiagnostic>();
        switch (failure)
        {
            case TestHostExitedException th:
                list.Add(new ProjectDiagnostic(LogLevels.Error,
                    $"The test host exited{(th.ExitCode is { } code ? $" with code {code}" : "")} before it answered.",
                    "Run the test app directly (dotnet <test dll> --list-tests) to see why it does not start.",
                    th.StderrTail.Count > 0 ? string.Join('\n', th.StderrTail) : null));
                break;
            case TimeoutException:
                list.Add(new ProjectDiagnostic(LogLevels.Error, "The test host did not connect within 60 s.",
                    "Labcoat starts the test app with --server; only Microsoft.Testing.Platform test apps support that."));
                break;
            case not null:
                list.Add(new ProjectDiagnostic(LogLevels.Error, "Discovery failed: " + FirstLine(failure.Message)));
                break;
        }

        if (properties.Count > 0)
        {
            var outputType = properties.GetValueOrDefault("OutputType");
            if (!string.IsNullOrEmpty(outputType) && !outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                && !outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new ProjectDiagnostic(LogLevels.Error,
                    $"OutputType is '{outputType}', but Microsoft.Testing.Platform test projects are executables.",
                    "Add <OutputType>Exe</OutputType> to the project (MSTest.Sdk and xunit.v3 set it for you)."));
            }
            if (!HasMtpRunner(properties, packageReferences, projectPath))
            {
                list.Add(new ProjectDiagnostic(LogLevels.Warning,
                    "No Microsoft.Testing.Platform runner is enabled for this project.",
                    "Use MSTest.Sdk, or set <EnableMSTestRunner>true</EnableMSTestRunner> (MSTest), " +
                    "<EnableNUnitRunner>true</EnableNUnitRunner> (NUnit), or reference xunit.v3 / TUnit."));
            }
        }

        if (failure is null)
        {
            list.Add(new ProjectDiagnostic(LogLevels.Warning, "No tests discovered.",
                "Check that the test classes are public and marked as tests (MSTest: [TestClass] + [TestMethod]) and that " +
                $"the build output is current{(targetPath is null ? "" : $" ({Path.GetFileName(targetPath)})")}."));
        }
        return list;

        static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
    }

    private static bool HasMtpRunner(IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> packageReferences, string projectPath)
    {
        bool True(string name) => string.Equals(properties.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);
        if (True("IsTestingPlatformApplication") || True("EnableMSTestRunner") || True("EnableNUnitRunner")
            || True("UseMicrosoftTestingPlatformRunner") || True("UsingMSTestSdk"))
            return true;
        if (packageReferences.Any(r => r.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase)
                                         || r.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)
                                         || r.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)))
            return true;
        try { return File.ReadAllText(projectPath).Contains("MSTest.Sdk", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
