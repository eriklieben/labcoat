using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Labcoat.Aspire.Hosting.Tests;

[TestClass]
public class LabcoatOptionsTests
{
    [TestMethod]
    public void Ui_settings_serialize_to_compact_camel_case_json()
    {
        var ui = new LabcoatUiSettings
        {
            Theme = LabcoatTheme.GitHub,
            Mode = LabcoatThemeMode.Dark,
            Motion = LabcoatMotion.Reduced,
            Editor = LabcoatEditor.VisualStudio,
            CoverageTint = LabcoatCoverageTint.Gutter,
            Confetti = true,
            PartialRunsInHistory = false,
            OpenOutputOnError = true,
            SingleKeyShortcuts = false,
        };
        ui.Lock(s => s.Editor).Lock(LabcoatUiSetting.SingleKeyShortcuts, LabcoatUiSetting.Theme);

        Assert.AreEqual(
            """{"theme":"github","mode":"dark","motion":"reduced","editor":"visualstudio","coverageTint":"gutter","confetti":true,"partialRunsInHistory":false,"openOutputOnError":true,"singleKeyShortcuts":false,"locked":["theme","editor","singleKeyShortcuts"]}""",
            ui.ToJson().ToJsonString());
    }

    [TestMethod]
    public void Unset_settings_are_left_out()
    {
        Assert.AreEqual("{}", new LabcoatUiSettings().ToJson().ToJsonString());
        Assert.IsTrue(new LabcoatUiSettings().IsEmpty);
        Assert.AreEqual("""{"editor":"vscode"}""", new LabcoatUiSettings { Editor = LabcoatEditor.VSCode }.ToJson().ToJsonString());
    }

    [TestMethod]
    public void Every_enum_value_maps_to_a_value_the_explorer_accepts()
    {
        // must match UiSettings.All in src/Labcoat/LabcoatConfig.cs and the UI (ui-defaults.ts)
        CollectionAssert.AreEqual(new[] { "catppuccin", "aspire", "github", "one", "dracula", "solarized", "gruvbox" },
            Enum.GetValues<LabcoatTheme>().Select(v => new LabcoatUiSettings { Theme = v }.ToJson()["theme"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "vscode", "rider", "visualstudio" },
            Enum.GetValues<LabcoatEditor>().Select(v => new LabcoatUiSettings { Editor = v }.ToJson()["editor"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "theme", "mode", "motion", "editor", "coverageTint", "confetti", "partialRunsInHistory", "openOutputOnError", "singleKeyShortcuts" },
            new LabcoatUiSettings().LockAll().ToJson()["locked"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public void Lock_by_expression_accepts_only_settings_properties()
    {
        var ui = new LabcoatUiSettings().Lock(s => s.Confetti).Lock(s => s.Theme);
        CollectionAssert.AreEquivalent(new[] { LabcoatUiSetting.Confetti, LabcoatUiSetting.Theme }, ui.Locked.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => ui.Lock(s => s.Locked));
        Assert.ThrowsExactly<ArgumentException>(() => ui.Lock(s => 42));
    }

    [TestMethod]
    public void Options_bind_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Labcoat:Watch"] = "true",
            ["Labcoat:HostPort"] = "4300",
            ["Labcoat:TestProjects:0"] = "../A.Tests",
            ["Labcoat:TestProjects:1"] = "../B.Tests",
            ["Labcoat:Ui:Theme"] = "dracula",
            ["Labcoat:Ui:Mode"] = "Dark",
            ["Labcoat:Ui:Editor"] = "rider",
            ["Labcoat:Ui:Confetti"] = "true",
            ["Labcoat:Ui:Locked:0"] = "Editor",
            ["Labcoat:Ui:Locked:1"] = "confetti",
            ["Labcoat:Resources:tests:Coverage"] = "true",
            ["Labcoat:CoverageSettings"] = " ci.xml ",
            ["Labcoat:CoverageInclude:0"] = "Labcoat.*",
            ["Labcoat:Resources:tests:CoverageExclude"] = "Tools; **/Migrations/**",
            ["Labcoat:Resources:tests:Ui:Theme"] = "Aspire",
        }).Build();

        var o = new LabcoatOptions().Bind(config.GetSection("Labcoat")).Bind(config.GetSection("Labcoat:Resources:tests"));

        Assert.IsTrue(o.Watch);
        Assert.IsTrue(o.Coverage);
        Assert.AreEqual(4300, o.HostPort);
        CollectionAssert.AreEqual(new[] { "../A.Tests", "../B.Tests" }, o.TestProjects.ToArray());
        Assert.AreEqual("ci.xml", o.CoverageSettings);
        CollectionAssert.AreEqual(new[] { "Labcoat.*" }, o.CoverageInclude.ToArray());
        CollectionAssert.AreEqual(new[] { "Tools", "**/Migrations/**" }, o.CoverageExclude.ToArray());
        Assert.AreEqual("""{"theme":"aspire","mode":"dark","editor":"rider","confetti":true,"locked":["editor","confetti"]}""", o.Ui.ToJson().ToJsonString());
    }

    [TestMethod]
    public void Coverage_lists_skip_blanks_and_duplicates()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Labcoat:CoverageInclude:0"] = " A ",
            ["Labcoat:CoverageInclude:1"] = " ",
            ["Labcoat:CoverageInclude"] = "A;B",
            ["Labcoat:CoverageSettings"] = "",
        }).Build();
        var o = new LabcoatOptions().Bind(config.GetSection("Labcoat"));
        CollectionAssert.AreEqual(new[] { "A", "B" }, o.CoverageInclude.ToArray());
        Assert.IsEmpty(o.CoverageExclude);
        Assert.IsNull(o.CoverageSettings);
    }

    [TestMethod]
    public void Invalid_configuration_values_throw_naming_the_key()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:Ui:Theme"] = "neon" }).Build();
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(config.GetSection("Labcoat")));
        Assert.Contains("'Labcoat:Ui:Theme' is 'neon'; expected Catppuccin, Aspire, GitHub, One, Dracula, Solarized, Gruvbox", ex.Message);

        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:Ui:Mode"] = "2" }).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(config.GetSection("Labcoat")));
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:Ui:Locked"] = "Theme,Colour" }).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(config.GetSection("Labcoat")));
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:Watch"] = "yes" }).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(config.GetSection("Labcoat")));
    }

    [TestMethod]
    public void AddLabcoat_applies_options_configuration_then_code_and_fluent_calls_win()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        builder.Configuration["Labcoat:Ui:Theme"] = "Gruvbox";
        builder.Configuration["Labcoat:Ui:Motion"] = "Off";

        var rb = builder.AddLabcoat("tests", o =>
        {
            o.Watch = true;
            o.TestTracing = true;
            o.Ui.Theme = LabcoatTheme.Dracula;   // code wins over configuration
            o.Ui.Editor = LabcoatEditor.Rider;
            o.Ui.Lock(s => s.Editor);
        }).WithUiSettings(ui => ui.Confetti = true).WithWatch(false);

        Assert.IsFalse(rb.Resource.WatchOnStart);  // the fluent call after AddLabcoat
        Assert.IsTrue(rb.Resource.TracingOnStart);
        Assert.AreEqual("""{"theme":"dracula","motion":"off","editor":"rider","confetti":true,"locked":["editor"]}""", rb.Resource.UiSettings.ToJson().ToJsonString());
    }

    [TestMethod]
    public void The_explorer_gets_the_ui_settings_as_an_environment_variable()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        var rb = builder.AddLabcoat("tests").WithUiSettings(ui => { ui.Theme = LabcoatTheme.One; ui.LockAll(); });

        var env = new Dictionary<string, object>();
        var ctx = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), rb.Resource, env);
        foreach (var a in rb.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) a.Callback(ctx).GetAwaiter().GetResult();

        Assert.AreEqual("""{"theme":"one","locked":["theme","mode","motion","editor","coverageTint","confetti","partialRunsInHistory","openOutputOnError","singleKeyShortcuts"]}""",
            env[LabcoatResourceBuilderExtensions.UiSettingsEnvironmentVariable]);
        Assert.AreEqual("tests", env["LABCOAT_NAME"]);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [TestMethod]
    public void Test_projects_and_pass_through_variables_skip_blanks_and_duplicates()
    {
        var config = Config(
            ("Labcoat:TestProjects:0", "../A.Tests"),
            ("Labcoat:TestProjects:1", " "),
            ("Labcoat:TestProjects:2", "../A.Tests"),
            ("Labcoat:PassThroughEnvironment:0", " OTEL_* "),
            ("Labcoat:PassThroughEnvironment:1", ""),
            ("Labcoat:PassThroughEnvironment", "OTEL_*; DOCKER_HOST ,,MY_VAR"),
            ("Labcoat:Resources:tests:TestProjects", "../A.Tests"),        // one value, already listed
            ("Labcoat:Resources:other:TestProjects", "../B.Tests"));
        var o = new LabcoatOptions().Bind(config.GetSection("Labcoat")).Bind(config.GetSection("Labcoat:Resources:tests"));
        CollectionAssert.AreEqual(new[] { "../A.Tests" }, o.TestProjects.ToArray());
        CollectionAssert.AreEqual(new[] { "OTEL_*", "DOCKER_HOST", "MY_VAR" }, o.PassThroughEnvironment.ToArray());

        o.Bind(config.GetSection("Labcoat:Resources:other"));
        CollectionAssert.AreEqual(new[] { "../A.Tests", "../B.Tests" }, o.TestProjects.ToArray(), "a single value is added");
    }

    [TestMethod]
    public void A_later_section_only_overrides_what_it_sets()
    {
        var config = Config(
            ("Labcoat:TestTracing", "true"),
            ("Labcoat:SuiteResultState", "False"),
            ("Labcoat:AffectedBase", " origin/develop "),
            ("Labcoat:HostPort", "65535"),
            ("Labcoat:Ui:Motion", "reduced"),
            ("Labcoat:Ui:CoverageTint", "GUTTER"),
            ("Labcoat:Ui:PartialRunsInHistory", "true"),
            ("Labcoat:Ui:OpenOutputOnError", "false"),
            ("Labcoat:Ui:SingleKeyShortcuts", "false"),
            ("Labcoat:Resources:tests:Watch", "true"),
            ("Labcoat:Resources:tests:Ui:Confetti", "true"),
            ("Labcoat:Resources:tests:AffectedBase", ""));
        var o = new LabcoatOptions().Bind(config.GetSection("Labcoat")).Bind(config.GetSection("Labcoat:Resources:tests"));
        Assert.AreEqual((true, false, true, false), (o.TestTracing, o.SuiteResultState, o.Watch, o.Coverage));
        Assert.AreEqual(("origin/develop", (int?)65535), (o.AffectedBase, o.HostPort), "an empty value keeps the earlier one");
        Assert.AreEqual("""{"motion":"reduced","coverageTint":"gutter","confetti":true,"partialRunsInHistory":true,"openOutputOnError":false,"singleKeyShortcuts":false}""",
            o.Ui.ToJson().ToJsonString());

        // a section without any of the keys changes nothing
        o.Bind(config.GetSection("Labcoat:Resources:nothing"));
        Assert.AreEqual((true, false, true), (o.TestTracing, o.SuiteResultState, o.Watch));
        Assert.AreEqual(6, o.Ui.ToJson().Count);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("0")]
    [DataRow("65536")]
    [DataRow("-1")]
    public void An_invalid_host_port_throws_naming_the_key(string port)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(Config(("Labcoat:Resources:tests:HostPort", port)).GetSection("Labcoat:Resources:tests")));
        Assert.AreEqual($"Labcoat configuration: 'Labcoat:Resources:tests:HostPort' is '{port}'; expected a port number (1-65535).", ex.Message);
    }

    [TestMethod]
    public void Binding_the_configuration_root_names_the_key_without_a_prefix()
    {
        // e.g. a configuration built for Labcoat alone (no "Labcoat" section): the root is not an IConfigurationSection
        var o = new LabcoatOptions().Bind(Config(("Watch", "true"), ("Ui:Theme", "One")));
        Assert.IsTrue(o.Watch);
        Assert.AreEqual(LabcoatTheme.One, o.Ui.Theme);
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(Config(("Coverage", "1"))));
        Assert.AreEqual("Labcoat configuration: 'Coverage' is '1'; expected true or false.", ex.Message);
        Assert.ThrowsExactly<ArgumentNullException>(() => new LabcoatOptions().Bind(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LabcoatUiSettings().Bind(null!));
    }

    [TestMethod]
    public void Locked_accepts_all_star_names_and_skips_blank_entries()
    {
        var ui = new LabcoatUiSettings().Bind(Config(("Locked:0", "theme"), ("Locked:1", " "), ("Locked:2", "SINGLEKEYSHORTCUTS")));
        CollectionAssert.AreEqual(new[] { LabcoatUiSetting.Theme, LabcoatUiSetting.SingleKeyShortcuts }, ui.Locked.ToArray());
        Assert.HasCount(Enum.GetValues<LabcoatUiSetting>().Length, new LabcoatUiSettings().Bind(Config(("Locked", "*"))).Locked);
        Assert.HasCount(Enum.GetValues<LabcoatUiSetting>().Length, new LabcoatUiSettings().Bind(Config(("Locked:0", "ALL"))).Locked);
        Assert.HasCount(2, new LabcoatUiSettings().Bind(Config(("Locked", "Mode, Motion,"))).Locked);
        // a number is not a setting name, even one that is a valid enum value
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatUiSettings().Bind(Config(("Locked", "3"))));
        Assert.Contains("'Locked' is '3'; expected setting names (Theme, Mode, Motion, Editor, CoverageTint, Confetti, PartialRunsInHistory, OpenOutputOnError, SingleKeyShortcuts) or All", ex.Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatUiSettings().Bind(Config(("Locked", "99"))));
    }

    [TestMethod]
    public void Lock_by_expression_also_takes_a_boxed_property()
    {
        // Lock<object>: the compiler wraps the nullable enum / bool in a Convert
        var ui = new LabcoatUiSettings().Lock<object?>(s => s.Mode).Lock<object?>(s => s.OpenOutputOnError);
        CollectionAssert.AreEqual(new[] { LabcoatUiSetting.Mode, LabcoatUiSetting.OpenOutputOnError }, ui.Locked.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => ui.Lock<object>(s => s.ToString()!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ui.Lock<object>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ui.Lock((LabcoatUiSetting[])null!));
    }

    [TestMethod]
    public void Apply_copies_only_what_is_set()
    {
        var all = new LabcoatUiSettings
        {
            Theme = LabcoatTheme.Solarized, Mode = LabcoatThemeMode.Light, Motion = LabcoatMotion.Off, Editor = LabcoatEditor.Rider,
            CoverageTint = LabcoatCoverageTint.Line, Confetti = false, PartialRunsInHistory = true, OpenOutputOnError = true, SingleKeyShortcuts = true,
        }.Lock(LabcoatUiSetting.Motion);
        var target = new LabcoatUiSettings { Theme = LabcoatTheme.One, Confetti = true }.Lock(LabcoatUiSetting.Theme);

        Assert.AreSame(target, target.Apply(new LabcoatUiSettings()));
        Assert.AreEqual("""{"theme":"one","confetti":true,"locked":["theme"]}""", target.ToJson().ToJsonString(), "nothing set: nothing changes");

        target.Apply(all);
        Assert.AreEqual(all.ToJson().Without("locked"), target.ToJson().Without("locked"));
        CollectionAssert.AreEqual(new[] { LabcoatUiSetting.Theme, LabcoatUiSetting.Motion }, target.Locked.ToArray(), "locks add up");
        Assert.IsFalse(target.IsEmpty);
        Assert.ThrowsExactly<ArgumentNullException>(() => target.Apply(null!));
    }
}

internal static class JsonTestExtensions
{
    public static string Without(this System.Text.Json.Nodes.JsonObject o, string key)
    {
        o.Remove(key);
        return o.ToJsonString();
    }
}
