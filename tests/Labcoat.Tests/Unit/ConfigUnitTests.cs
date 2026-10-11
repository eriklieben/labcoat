using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>Settings files and UI default layers (LabcoatConfig.cs): the branches ConfigTests does not cover.</summary>
[TestClass]
public class ConfigUnitTests
{
    private static JsonNode Json(string json) => JsonNode.Parse(json)!;

    [TestMethod]
    public void Ui_settings_that_are_not_an_object_or_have_bad_locks_are_warnings()
    {
        var w = new List<string>();
        Assert.IsTrue(UiLayer.Parse(ConfigLayer.Workspace, "f", Json("[1]"), w).IsEmpty);
        Assert.Contains("the UI settings must be a JSON object", w.Single());

        w.Clear();
        var layer = UiLayer.Parse(ConfigLayer.Workspace, "f", Json("""{"$schema":"x","locked":"theme, nope"}"""), w);
        CollectionAssert.AreEqual(new[] { "theme" }, layer.Locked.ToArray());
        Assert.Contains("Unknown UI setting 'nope'", w.Single());

        w.Clear();
        Assert.IsTrue(UiLayer.Parse(ConfigLayer.Workspace, "f", Json("""{"locked":false}"""), w).IsEmpty);
        Assert.IsEmpty(w);
        UiLayer.Parse(ConfigLayer.Workspace, "f", Json("""{"locked":{"theme":true}}"""), w);
        Assert.Contains("\"locked\" must be a list of setting names", w.Single());

        w.Clear();
        UiLayer.Parse(ConfigLayer.Workspace, "f", Json("""{"locked":[null]}"""), w);
        Assert.Contains("(got null)", w.Single());

        Assert.IsTrue(UiLayer.Parse(ConfigLayer.Env, "e", null, w).IsEmpty);
        Assert.IsTrue(UiLayer.ParseJson(ConfigLayer.Env, "e", "  ", w).IsEmpty);
    }

    [TestMethod]
    public void Values_are_validated_per_setting_type()
    {
        var theme = UiSettings.Find("THEME")!;
        var confetti = UiSettings.Find("confetti")!;
        Assert.AreEqual("catppuccin, aspire, github, one, dracula, solarized, gruvbox", theme.Expected);
        Assert.AreEqual("true or false", confetti.Expected);
        Assert.IsTrue(UiSettings.Normalize(confetti, JsonValue.Create(" TRUE "), out _)!.GetValue<bool>());
        Assert.IsNull(UiSettings.Normalize(confetti, JsonValue.Create("maybe"), out var e1));
        Assert.Contains("'confetti' must be true or false (got \"maybe\")", e1!);
        Assert.IsNull(UiSettings.Normalize(theme, null, out var e2));
        Assert.EndsWith("(got null)", e2);
        Assert.IsNull(UiSettings.Normalize(theme, new JsonArray(), out _));
        Assert.AreEqual("dracula", UiSettings.Normalize(theme, JsonValue.Create(" Dracula"), out _)!.GetValue<string>());
        Assert.IsNull(UiSettings.Find("nope"));
        CollectionAssert.AreEqual(new[] { "user", "workspace", "apphost", "env", "cli" },
            Enum.GetValues<ConfigLayer>().Select(UiSettings.LayerName).ToArray());

        var layer = new UiLayer(ConfigLayer.Cli, "cli");
        Assert.IsFalse(layer.TrySet("fontSize", JsonValue.Create(3), out var e3));
        Assert.StartsWith("Unknown UI setting 'fontSize'", e3);
        Assert.IsFalse(layer.TryLock("nope", out _));
        Assert.IsTrue(layer.TryLock(" * ", out _));
        Assert.HasCount(UiSettings.All.Count, layer.Locked);
    }

    [TestMethod]
    public void Describe_lists_values_with_their_source_and_locks()
    {
        var w = new List<string>();
        var user = UiLayer.Parse(ConfigLayer.User, "u", Json("""{"theme":"github","confetti":true}"""), w);
        var cli = new UiLayer(ConfigLayer.Cli, "cli");
        cli.TryLock("confetti", out _);
        var merged = UiLayer.Merge([user, cli])!;
        Assert.AreEqual("theme=github (user), confetti=true (user, locked)", UiLayer.Describe(merged));
        Assert.AreEqual("mode=dark", UiLayer.Describe(new JsonObject { ["mode"] = "dark" }));
    }

    private sealed class Sandbox : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-config-unit-");
        public string Home => Path.Combine(Root.FullName, "home");
        public string Workspace => Path.Combine(Root.FullName, "home", "repo", "tests");

        public Sandbox()
        {
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Path.Combine(Home, "repo", ".git"));
        }

        /// <summary>Writes <paramref name="relative"/> ('/'-separated) and returns its full path with the platform's separators.</summary>
        public string Write(string relative, string text)
        {
            var path = Path.GetFullPath(Path.Combine(Root.FullName, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public LabcoatConfig.Inputs Inputs() => new() { WorkspaceRoot = Workspace, Home = Home };

        public void Dispose() => Root.Delete(recursive: true);
    }

    [TestMethod]
    public void A_settings_file_that_is_not_an_object_is_a_warning_and_null_is_empty()
    {
        using var s = new Sandbox();
        s.Write("home/repo/.labcoat.json", "[1, 2]");
        var config = LabcoatConfig.Load(s.Inputs());
        Assert.Contains("expected a JSON object", config.Warnings.Single());
        CollectionAssert.AreEqual(new[] { Path.Combine("..", ".labcoat.json") }, config.FilesForDisplay);

        s.Write("home/repo/.labcoat.json", "null");
        config = LabcoatConfig.Load(s.Inputs());
        Assert.IsEmpty(config.Warnings);
        Assert.IsNull(config.UiDefaults);

        s.Write("home/repo/.labcoat.json", """{"watch": true, "coverage": true, "traces": "yes", "port": 70000, "ui": {"mode": "dark"}}""");
        config = LabcoatConfig.Load(s.Inputs());
        Assert.IsTrue(config.Watch);
        Assert.IsTrue(config.Coverage);
        Assert.IsFalse(config.Traces);
        Assert.IsNull(config.Port);
        Assert.HasCount(2, config.Warnings);
        Assert.HasCount(1, config.UiLayers);
    }

    [TestMethod]
    public void Coverage_as_an_object_sets_the_settings_file_and_filters_and_lists_add_up_across_files()
    {
        using var s = new Sandbox();
        s.Write("home/.labcoat.json", """{"coverage": {"include": "Labcoat.*", "exclude": ["Seeder"], "settings": "user.xml"}}""");
        s.Write("home/repo/.labcoat.json", """
            {"coverage": {"enabled": true, "settings": " ci/coverage.xml ", "include": ["Tools.*", ""], "exclude": ["**/Migrations/**"]}}
            """);
        var config = LabcoatConfig.Load(s.Inputs());
        Assert.IsEmpty(config.Warnings);
        Assert.IsTrue(config.Coverage);
        var o = config.CoverageOptions;
        Assert.AreEqual("ci/coverage.xml", o.Settings);
        Assert.AreEqual(Path.Combine(s.Root.FullName, "home", "repo"), o.BaseDirectory);
        CollectionAssert.AreEqual(new[] { "Labcoat.*", "Tools.*" }, o.Include.ToArray());
        CollectionAssert.AreEqual(new[] { "Seeder", "**/Migrations/**" }, o.Exclude.ToArray());

        s.Write("home/repo/.labcoat.json", """{"coverage": {"enabled": "yes", "settings": 1, "include": [1], "exclude": {}, "nope": 1}}""");
        config = LabcoatConfig.Load(s.Inputs() with { Home = null });
        Assert.IsFalse(config.Coverage);
        Assert.IsNull(config.CoverageOptions.Settings);
        Assert.HasCount(5, config.Warnings, string.Join("\n", config.Warnings));
        Assert.Contains("unknown setting 'coverage.nope' (known: enabled, settings, include, exclude)", string.Join("\n", config.Warnings));

        s.Write("home/repo/.labcoat.json", """{"coverage": "on"}""");
        config = LabcoatConfig.Load(s.Inputs() with { Home = null });
        Assert.Contains("'coverage' must be true or false (or an object: enabled, settings, include, exclude)", config.Warnings.Single());
    }

    [TestMethod]
    public void Xdg_config_home_holds_the_user_file()
    {
        using var s = new Sandbox();
        var xdg = s.Write("xdg/labcoat/config.json", """{ "ui": { "motion": "off" } }""");
        var config = LabcoatConfig.Load(s.Inputs() with { XdgConfigHome = Path.Combine(s.Root.FullName, "xdg") });
        Assert.AreEqual(xdg, config.UserFile);
        Assert.AreEqual("off", config.UiDefaults!["motion"]!.GetValue<string>());
        Assert.AreEqual(xdg, config.UiDefaults["files"]!["user"]!.GetValue<string>()); // outside home: the full path
    }

    [TestMethod]
    public void User_and_workspace_lookups_handle_missing_inputs()
    {
        using var s = new Sandbox();
        var w = new List<string>();
        Assert.IsNull(LabcoatConfig.FindUserFile(new LabcoatConfig.Inputs(), w));
        Assert.IsNull(LabcoatConfig.FindUserFile(new LabcoatConfig.Inputs { IsWindows = true }, w));
        Assert.IsNull(LabcoatConfig.FindWorkspaceFile(null, s.Home, w));
        // Starting in the home directory: the walk stops right away (home's .labcoat.json is the user file).
        s.Write("home/.labcoat.json", "{}");
        Assert.IsNull(LabcoatConfig.FindWorkspaceFile(s.Home, s.Home, w));
        Assert.AreEqual("/x/y.json", LabcoatConfig.DisplayUserPath("/x/y.json", null));
        Assert.AreEqual("~/a/b.json", LabcoatConfig.DisplayUserPath(Path.Combine(s.Home, "a", "b.json"), s.Home));
        Assert.IsEmpty(w);
    }

    [TestMethod]
    public void An_explicit_config_file_without_a_workspace_root_is_shown_with_its_full_path()
    {
        using var s = new Sandbox();
        var file = s.Write("team.json", """{ "ui": { "editor": "rider" } }""");
        var config = LabcoatConfig.Load(new LabcoatConfig.Inputs { ConfigFile = file, Home = s.Home });
        Assert.AreEqual(file, config.WorkspaceFile);
        CollectionAssert.AreEqual(new[] { file }, config.FilesForDisplay);
    }

    [TestMethod]
    [DoNotParallelize]
    public void Inputs_come_from_the_environment()
    {
        string[] names = ["LABCOAT_NO_USER_CONFIG", "XDG_CONFIG_HOME", LabcoatConfig.AppHostUiEnv, LabcoatConfig.UiEnv];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("LABCOAT_NO_USER_CONFIG", "1");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/xdg");
            Environment.SetEnvironmentVariable(LabcoatConfig.AppHostUiEnv, """{"theme":"aspire"}""");
            Environment.SetEnvironmentVariable(LabcoatConfig.UiEnv, """{"mode":"dark"}""");
            var cli = new UiLayer(ConfigLayer.Cli, "cli");
            var inputs = LabcoatConfig.Inputs.FromEnvironment("/ws", "team.json", noUserConfig: false, cli);
            Assert.AreEqual(("/ws", "team.json", true, "/xdg"), (inputs.WorkspaceRoot, inputs.ConfigFile, inputs.NoUserConfig, inputs.XdgConfigHome));
            Assert.AreEqual(("""{"theme":"aspire"}""", """{"mode":"dark"}"""), (inputs.AppHostUiJson, inputs.EnvUiJson));
            Assert.AreSame(cli, inputs.Cli);
            Assert.AreEqual(OperatingSystem.IsWindows(), inputs.IsWindows);
            Assert.IsFalse(string.IsNullOrEmpty(inputs.Home));

            foreach (var v in new[] { "0", "false", "FALSE", "" })
            {
                Environment.SetEnvironmentVariable("LABCOAT_NO_USER_CONFIG", v);
                Assert.IsFalse(LabcoatConfig.Inputs.FromEnvironment(null, null, false, null).NoUserConfig, v);
            }
            Assert.IsTrue(LabcoatConfig.Inputs.FromEnvironment(null, null, true, null).NoUserConfig);
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
        }
    }

    [TestMethod]
    public void Index_page_is_cached_until_the_file_changes()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-index-unit-");
        try
        {
            var page = new IndexPage(dir.FullName, new JsonObject { ["theme"] = "one" });
            Assert.IsNull(page.Html()); // UI not built
            File.WriteAllText(page.Path, "<html><body>v1</body></html>");
            var v1 = page.Html()!;
            Assert.Contains("data-labcoat-defaults=", v1);
            Assert.AreSame(v1, page.Html());
            File.WriteAllText(page.Path, "<HTML><body>v2</body></HTML>");
            File.SetLastWriteTimeUtc(page.Path, DateTime.UtcNow.AddMinutes(1));
            var v2 = page.Html()!;
            Assert.Contains("v2", v2);
            Assert.StartsWith("<HTML data-labcoat-defaults=", v2);
            Assert.AreEqual("<p>no html tag</p>", IndexPage.Inject("<p>no html tag</p>", " x=\"1\""));
            Assert.AreEqual("<html>", IndexPage.Inject("<html>", null));
        }
        finally { dir.Delete(recursive: true); }
    }
}
