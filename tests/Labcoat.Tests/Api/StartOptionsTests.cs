using Labcoat;

namespace Labcoat.Tests.Api;

/// <summary>The command line (<see cref="StartOptions.Parse"/>): every option, its value forms and its errors.</summary>
[TestClass]
public class StartOptionsTests
{
    private static StartOptions Ok(params string[] args)
    {
        var o = StartOptions.Parse(args, out var error);
        Assert.IsNull(error, error);
        return o!;
    }

    private static string Error(params string[] args)
    {
        Assert.IsNull(StartOptions.Parse(args, out var error), "expected an error for: " + string.Join(' ', args));
        return error!;
    }

    [TestMethod]
    public void No_arguments_give_the_defaults()
    {
        var o = Ok();
        Assert.IsEmpty(o.Paths);
        Assert.IsNull(o.Port);
        Assert.IsTrue(o.Open);
        Assert.IsFalse(o.Watch || o.Coverage || o.Traces || o.NoUserConfig);
        Assert.IsNull(o.ConfigFile);
        Assert.IsEmpty(o.ExtraRoots);
        Assert.IsTrue(o.CliUi.IsEmpty);
        Assert.IsNull(o.PrintAndExit);
    }

    [TestMethod]
    public void Coverage_settings_and_filters()
    {
        var o = Ok("--coverage-settings", "ci.xml", "--coverage-include", "Labcoat.*, Tools.*", "--coverage-include=X", "--coverage-exclude=**/Migrations/**,Seeder");
        Assert.AreEqual("ci.xml", o.CoverageSettings);
        CollectionAssert.AreEqual(new[] { "Labcoat.*", "Tools.*", "X" }, o.CoverageInclude);
        CollectionAssert.AreEqual(new[] { "**/Migrations/**", "Seeder" }, o.CoverageExclude);
        Assert.AreEqual("b.xml", Ok("--coverage-settings=b.xml").CoverageSettings);
        Assert.Contains("--coverage-settings needs a file", Error("--coverage-settings"));
        Assert.Contains("--coverage-include needs module names or path globs", Error("--coverage-include", " "));
        Assert.Contains("--coverage-exclude needs", Error("--coverage-exclude"));
    }

    [TestMethod]
    public void Flags_and_paths_are_collected_in_any_order()
    {
        var o = Ok("a.slnx", "--watch", "tests/B.csproj", "--no-open", "--coverage", "--traces", "--no-user-config", "-", "-w");
        CollectionAssert.AreEqual(new[] { "a.slnx", "tests/B.csproj", "-" }, o.Paths); // a lone "-" is a path, not an option
        Assert.IsTrue(o.Watch && o.Coverage && o.Traces && o.NoUserConfig);
        Assert.IsFalse(o.Open);
    }

    [TestMethod]
    [DataRow(new[] { "--port", "5000" }, 5000)]
    [DataRow(new[] { "-p", "1" }, 1)]
    [DataRow(new[] { "--port=65535" }, 65535)]
    public void Port_forms(string[] args, int port) => Assert.AreEqual(port, Ok(args).Port);

    [TestMethod]
    [DataRow(new[] { "--port" }, "--port needs a port number (1-65535).")]
    [DataRow(new[] { "--port", "x" }, "--port needs a port number (1-65535).")]
    [DataRow(new[] { "-p", "0" }, "-p needs a port number (1-65535).")]
    [DataRow(new[] { "--port", "65536" }, "--port needs a port number (1-65535).")]
    [DataRow(new[] { "--port=0" }, "--port needs a port number (1-65535).")]
    [DataRow(new[] { "--port=abc" }, "--port needs a port number (1-65535).")]
    public void Invalid_ports_are_errors(string[] args, string message) => Assert.AreEqual(message, Error(args));

    [TestMethod]
    public void Root_directories_are_absolute_and_repeatable()
    {
        var o = Ok("--root", "src", "--root=docs");
        CollectionAssert.AreEqual(new[] { Path.GetFullPath("src"), Path.GetFullPath("docs") }, o.ExtraRoots);
        Assert.AreEqual("Unknown option '--root='.", Error("--root=")); // no value: not a root
    }

    [TestMethod]
    public void Root_needs_a_directory() => Assert.AreEqual("--root needs a directory.", Error("--root"));

    [TestMethod]
    public void Config_file_in_both_forms()
    {
        Assert.AreEqual("team.json", Ok("--config", "team.json").ConfigFile);
        Assert.AreEqual("x=y.json", Ok("--config=x=y.json").ConfigFile);
        Assert.AreEqual("--config needs a file.", Error("--config"));
        Assert.AreEqual("--config needs a file.", Error("--config="));
    }

    [TestMethod]
    public void Ui_flags_are_validated_and_normalized()
    {
        var o = Ok("--theme", "Dracula", "--mode=DARK", "--editor", "rider", "--motion=off", "--lock", "theme,mode");
        Assert.AreEqual("dracula", o.CliUi.Values["theme"].GetValue<string>());
        Assert.AreEqual("dark", o.CliUi.Values["mode"].GetValue<string>());
        Assert.AreEqual("rider", o.CliUi.Values["editor"].GetValue<string>());
        Assert.AreEqual("off", o.CliUi.Values["motion"].GetValue<string>());
        CollectionAssert.AreEquivalent(new[] { "theme", "mode" }, o.CliUi.Locked.ToArray());
        Assert.HasCount(UiSettings.All.Count, Ok("--lock=all").CliUi.Locked);
    }

    [TestMethod]
    public void Ui_flag_errors_name_the_flag_and_the_allowed_values()
    {
        Assert.AreEqual("--theme needs a value: catppuccin, aspire, github, one, dracula, solarized, gruvbox.", Error("--theme"));
        Assert.AreEqual("--mode needs a value: system, light, dark.", Error("--mode="));
        Assert.StartsWith("--editor: 'editor' must be one of vscode, rider, visualstudio (got \"notepad\")", Error("--editor", "notepad"));
        Assert.StartsWith("--motion: 'motion' must be one of system, reduced, off", Error("--motion=fast"));
        Assert.AreEqual("--lock needs setting names (comma-separated) or 'all'.", Error("--lock"));
        Assert.StartsWith("--lock: Unknown UI setting 'fontSize' in \"locked\"", Error("--lock", "theme,fontSize"));
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("-?")]
    public void Help_wins_over_everything_after_it(string flag)
    {
        var o = Ok("--watch", flag, "--port", "nope", "--bogus");
        Assert.StartsWith("Labcoat ", o.PrintAndExit);
        Assert.Contains("Usage:", o.PrintAndExit);
    }

    [TestMethod]
    public void Version_prints_the_version() => Assert.AreEqual(Cli.Version, Ok("--version", "--bogus").PrintAndExit);

    [TestMethod]
    [DataRow("--bogus")]
    [DataRow("-x")]
    [DataRow("--port-number=1")]
    [DataRow("--theme-x=1")]
    public void Unknown_options_are_errors(string option) => Assert.AreEqual($"Unknown option '{option}'.", Error(option));

    [TestMethod]
    public void First_error_wins()
    {
        Assert.AreEqual("Unknown option '--a'.", Error("--a", "--port", "x"));
        Assert.AreEqual("--port needs a port number (1-65535).", Error("--port", "x", "--a"));
    }
}
