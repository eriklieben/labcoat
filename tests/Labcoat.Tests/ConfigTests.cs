using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests;

/// <summary>UI defaults: validation of every source, precedence, locks, settings files and the index.html injection.</summary>
[TestClass]
public partial class ConfigTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // ------------------------------------------------------------------ validation

    [TestMethod]
    public void Ui_layer_normalizes_values_and_keys()
    {
        var warnings = new List<string>();
        var layer = UiLayer.Parse(ConfigLayer.AppHost, "test", Obj("""{"Theme":"Dracula","mode":"DARK","editor":"visualStudio","confetti":true,"singleKeyShortcuts":"false","locked":["Theme","editor"]}"""), warnings);

        Assert.IsEmpty(warnings);
        Assert.AreEqual("dracula", layer.Values["theme"].GetValue<string>());
        Assert.AreEqual("dark", layer.Values["mode"].GetValue<string>());
        Assert.AreEqual("visualstudio", layer.Values["editor"].GetValue<string>());
        Assert.IsTrue(layer.Values["confetti"].GetValue<bool>());
        Assert.IsFalse(layer.Values["singleKeyShortcuts"].GetValue<bool>()); // "false" from an env var
        CollectionAssert.AreEquivalent(new[] { "theme", "editor" }, layer.Locked.ToArray());
    }

    [TestMethod]
    public void Ui_layer_skips_unknown_keys_and_invalid_values_with_warnings_and_keeps_the_rest()
    {
        var warnings = new List<string>();
        var layer = UiLayer.Parse(ConfigLayer.Workspace, ".labcoat.json",
            Obj("""{"theme":"neon","mode":"dark","confetti":"yes","fontSize":3,"motion":1,"locked":["mode","nope",4]}"""), warnings);

        Assert.HasCount(1, layer.Values);
        Assert.AreEqual("dark", layer.Values["mode"].GetValue<string>());
        CollectionAssert.AreEqual(new[] { "mode" }, layer.Locked.ToArray());
        Assert.HasCount(6, warnings, string.Join("\n", warnings));
        Assert.IsTrue(warnings.Any(w => w.Contains("'theme' must be one of catppuccin, aspire, github, one, dracula, solarized, gruvbox (got \"neon\")")), string.Join("\n", warnings));
        Assert.IsTrue(warnings.Any(w => w.Contains("'confetti' must be true or false")));
        Assert.IsTrue(warnings.Any(w => w.Contains("Unknown UI setting 'fontSize'")));
        Assert.IsTrue(warnings.Any(w => w.Contains("Unknown UI setting 'nope' in \"locked\"")));
        Assert.IsTrue(warnings.All(w => w.StartsWith(".labcoat.json: ", StringComparison.Ordinal) && w.EndsWith("; ignored.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Locked_all_or_true_locks_every_setting()
    {
        var w = new List<string>();
        Assert.HasCount(UiSettings.All.Count, UiLayer.Parse(ConfigLayer.Env, "e", Obj("""{"locked":"all"}"""), w).Locked);
        Assert.HasCount(UiSettings.All.Count, UiLayer.Parse(ConfigLayer.Env, "e", Obj("""{"locked":true}"""), w).Locked);
        Assert.HasCount(UiSettings.All.Count, UiLayer.Parse(ConfigLayer.Env, "e", Obj("""{"locked":["*"]}"""), w).Locked);
        Assert.IsEmpty(w);
    }

    [TestMethod]
    public void Malformed_env_json_is_a_warning_not_a_failure()
    {
        var w = new List<string>();
        var layer = UiLayer.ParseJson(ConfigLayer.Env, "LABCOAT_UI_SETTINGS", """{"theme":""", w);
        Assert.IsTrue(layer.IsEmpty);
        Assert.HasCount(1, w);
        Assert.StartsWith("LABCOAT_UI_SETTINGS is not valid JSON", w[0]);
    }

    // ------------------------------------------------------------------ precedence

    [TestMethod]
    public void Merge_takes_each_setting_from_the_highest_layer_and_unions_locks()
    {
        var w = new List<string>();
        var user = UiLayer.Parse(ConfigLayer.User, "~/.labcoat.json", Obj("""{"theme":"github","editor":"rider","confetti":true,"locked":["confetti"]}"""), w);
        var workspace = UiLayer.Parse(ConfigLayer.Workspace, ".labcoat.json", Obj("""{"theme":"dracula","locked":["mode"]}"""), w);
        var apphost = UiLayer.Parse(ConfigLayer.AppHost, "apphost", Obj("""{"theme":"aspire","mode":"dark","locked":["theme"]}"""), w);
        var env = UiLayer.Parse(ConfigLayer.Env, "env", Obj("""{"mode":"light"}"""), w);
        var cli = new UiLayer(ConfigLayer.Cli, "cli");
        Assert.IsTrue(cli.TrySet("theme", JsonValue.Create("one"), out _));

        var merged = UiLayer.Merge([user, workspace, apphost, env, cli], "~/.labcoat.json", ".labcoat.json")!;

        Assert.AreEqual("one", merged["theme"]!.GetValue<string>());   // cli > apphost > workspace > user
        Assert.AreEqual("light", merged["mode"]!.GetValue<string>());  // env > apphost
        Assert.AreEqual("rider", merged["editor"]!.GetValue<string>()); // only the user file sets it
        Assert.IsTrue(merged["confetti"]!.GetValue<bool>());
        CollectionAssert.AreEqual(new[] { "theme", "mode", "confetti" }, merged["locked"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.AreEqual("cli", merged["sources"]!["theme"]!.GetValue<string>());
        Assert.AreEqual("env", merged["sources"]!["mode"]!.GetValue<string>());
        Assert.AreEqual("user", merged["sources"]!["editor"]!.GetValue<string>());
        Assert.AreEqual("apphost", merged["lockedBy"]!["theme"]!.GetValue<string>());
        Assert.AreEqual("workspace", merged["lockedBy"]!["mode"]!.GetValue<string>());
        Assert.AreEqual("user", merged["lockedBy"]!["confetti"]!.GetValue<string>());
        Assert.AreEqual("~/.labcoat.json", merged["files"]!["user"]!.GetValue<string>());
        Assert.AreEqual(".labcoat.json", merged["files"]!["workspace"]!.GetValue<string>());
        Assert.IsEmpty(w);
    }

    [TestMethod]
    public void Merge_of_empty_layers_is_null()
    {
        Assert.IsNull(UiLayer.Merge([new UiLayer(ConfigLayer.Env, "e")]));
    }

    [TestMethod]
    public void Lock_without_a_value_is_reported_with_the_builtin_default()
    {
        var layer = new UiLayer(ConfigLayer.Cli, "cli");
        Assert.IsTrue(layer.TryLock("editor", out _));
        var merged = UiLayer.Merge([layer])!;
        Assert.IsNull(merged["editor"]);
        Assert.AreEqual("editor=vscode (built-in) (locked)", UiLayer.Describe(merged));
    }

    // ------------------------------------------------------------------ settings files

    private sealed class Sandbox : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-config-");
        public string Home => Path.Combine(Root.FullName, "home");
        public string Repo => Path.Combine(Home, "src", "repo");
        public string Workspace => Path.Combine(Repo, "tests", "Shop.Tests");

        public Sandbox()
        {
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        }

        public string Write(string relative, string json)
        {
            var path = Path.Combine(Root.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return path;
        }

        public LabcoatConfig.Inputs Inputs(string? configFile = null, bool noUser = false, string? appHost = null, string? env = null, UiLayer? cli = null) => new()
        {
            WorkspaceRoot = Workspace,
            ConfigFile = configFile,
            NoUserConfig = noUser,
            Home = Home,
            XdgConfigHome = null,
            AppData = Path.Combine(Home, "AppData", "Roaming"),
            IsWindows = false,
            AppHostUiJson = appHost,
            EnvUiJson = env,
            Cli = cli,
        };

        public void Dispose() => Root.Delete(recursive: true);
    }

    [TestMethod]
    public void Workspace_file_is_found_walking_up_to_the_repository_root()
    {
        using var s = new Sandbox();
        s.Write("home/src/repo/.labcoat.json", """{ "ui": { "theme": "gruvbox" }, "watch": true }""");

        var config = LabcoatConfig.Load(s.Inputs());

        Assert.AreEqual(Path.Combine(s.Repo, ".labcoat.json"), config.WorkspaceFile);
        Assert.IsTrue(config.Watch);
        Assert.AreEqual("gruvbox", config.UiDefaults!["theme"]!.GetValue<string>());
        Assert.AreEqual("workspace", config.UiDefaults["sources"]!["theme"]!.GetValue<string>());
        Assert.AreEqual(Path.Combine("..", "..", ".labcoat.json"), config.UiDefaults["files"]!["workspace"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { Path.Combine("..", "..", ".labcoat.json") }, config.FilesForDisplay);
    }

    [TestMethod]
    public void Walk_up_stops_at_the_repository_root()
    {
        using var s = new Sandbox();
        s.Write("home/src/.labcoat.json", """{ "ui": { "theme": "gruvbox" } }"""); // above the repo (.git): not used
        var config = LabcoatConfig.Load(s.Inputs());
        Assert.IsNull(config.WorkspaceFile);
        Assert.IsNull(config.UiDefaults);
    }

    [TestMethod]
    public void Dot_config_labcoat_json_is_accepted_and_the_dot_file_wins_with_a_warning()
    {
        using var s = new Sandbox();
        s.Write("home/src/repo/tests/.config/labcoat.json", """{ "ui": { "theme": "one" } }""");
        var config = LabcoatConfig.Load(s.Inputs());
        Assert.AreEqual(Path.Combine(s.Repo, "tests", ".config", "labcoat.json"), config.WorkspaceFile);

        s.Write("home/src/repo/tests/.labcoat.json", """{ "ui": { "theme": "github" } }""");
        config = LabcoatConfig.Load(s.Inputs());
        Assert.AreEqual(Path.Combine(s.Repo, "tests", ".labcoat.json"), config.WorkspaceFile);
        Assert.AreEqual("github", config.UiDefaults!["theme"]!.GetValue<string>());
        Assert.IsTrue(config.Warnings.Any(w => w.Contains("Both .labcoat.json and .config/labcoat.json exist")), string.Join("\n", config.Warnings));
    }

    [TestMethod]
    public void The_home_dot_file_is_the_user_layer_never_the_workspace_file()
    {
        using var s = new Sandbox();
        Directory.Delete(Path.Combine(s.Repo, ".git"));  // no repository: the walk goes up to (not into) home
        s.Write("home/.labcoat.json", """{ "ui": { "editor": "rider", "locked": ["editor"] } }""");

        var config = LabcoatConfig.Load(s.Inputs());

        Assert.IsNull(config.WorkspaceFile);
        Assert.AreEqual(Path.Combine(s.Home, ".labcoat.json"), config.UserFile);
        Assert.AreEqual("user", config.UiDefaults!["sources"]!["editor"]!.GetValue<string>());
        Assert.AreEqual("user", config.UiDefaults["lockedBy"]!["editor"]!.GetValue<string>());
        Assert.AreEqual("~/.labcoat.json", config.UiDefaults["files"]!["user"]!.GetValue<string>());
    }

    [TestMethod]
    public void User_file_in_the_config_dir_and_the_home_file_wins_when_both_exist()
    {
        using var s = new Sandbox();
        s.Write("home/.config/labcoat/config.json", """{ "ui": { "theme": "one" } }""");
        var config = LabcoatConfig.Load(s.Inputs());
        Assert.AreEqual("~/.config/labcoat/config.json", config.UiDefaults!["files"]!["user"]!.GetValue<string>());

        s.Write("home/.labcoat.json", """{ "ui": { "theme": "dracula" } }""");
        config = LabcoatConfig.Load(s.Inputs());
        Assert.AreEqual("dracula", config.UiDefaults!["theme"]!.GetValue<string>());
        Assert.IsTrue(config.Warnings.Any(w => w.Contains("Both ~/.labcoat.json and ~/.config/labcoat/config.json exist; using ~/.labcoat.json")), string.Join("\n", config.Warnings));
    }

    [TestMethod]
    public void Windows_user_file_is_under_appdata()
    {
        using var s = new Sandbox();
        s.Write("home/AppData/Roaming/Labcoat/config.json", """{ "ui": { "mode": "dark" } }""");
        var config = LabcoatConfig.Load(s.Inputs() with { IsWindows = true });
        Assert.AreEqual("dark", config.UiDefaults!["mode"]!.GetValue<string>());
    }

    [TestMethod]
    public void No_user_config_skips_the_user_file()
    {
        using var s = new Sandbox();
        s.Write("home/.labcoat.json", """{ "ui": { "theme": "dracula" } }""");
        var config = LabcoatConfig.Load(s.Inputs(noUser: true));
        Assert.IsNull(config.UserFile);
        Assert.IsNull(config.UiDefaults);
    }

    [TestMethod]
    public void Precedence_user_workspace_apphost_env_cli()
    {
        using var s = new Sandbox();
        s.Write("home/.labcoat.json", """{ "ui": { "theme": "github", "mode": "light", "motion": "off", "editor": "rider", "confetti": true } }""");
        s.Write("home/src/repo/.labcoat.json", """{ "ui": { "theme": "dracula", "mode": "dark", "motion": "reduced", "locked": ["motion"] } }""");
        var cli = new UiLayer(ConfigLayer.Cli, "cli");
        cli.TrySet("theme", JsonValue.Create("one"), out _);

        var config = LabcoatConfig.Load(s.Inputs(appHost: """{"theme":"aspire","mode":"system"}""", env: """{"mode":"dark","coverageTint":"gutter"}""", cli: cli));
        var d = config.UiDefaults!;

        Assert.AreEqual("one", d["theme"]!.GetValue<string>());        // cli
        Assert.AreEqual("dark", d["mode"]!.GetValue<string>());        // env over AppHost
        Assert.AreEqual("reduced", d["motion"]!.GetValue<string>());   // workspace over user
        Assert.AreEqual("rider", d["editor"]!.GetValue<string>());     // user
        Assert.AreEqual("gutter", d["coverageTint"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "motion" }, d["locked"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "~/.labcoat.json", Path.Combine("..", "..", ".labcoat.json") }, config.FilesForDisplay);
    }

    [TestMethod]
    public void Explicit_config_file_replaces_the_walk_up_and_must_exist()
    {
        using var s = new Sandbox();
        s.Write("home/src/repo/.labcoat.json", """{ "ui": { "theme": "dracula" } }""");
        var other = s.Write("elsewhere/team.json", """{ "ui": { "theme": "solarized" }, "port": 4321 }""");

        var config = LabcoatConfig.Load(s.Inputs(configFile: other));
        Assert.AreEqual("solarized", config.UiDefaults!["theme"]!.GetValue<string>());
        Assert.AreEqual(4321, config.Port);

        Assert.ThrowsExactly<ConfigException>(() => LabcoatConfig.Load(s.Inputs(configFile: Path.Combine(s.Root.FullName, "missing.json"))));
        var broken = s.Write("broken.json", "{ \"ui\": ");
        Assert.ThrowsExactly<ConfigException>(() => LabcoatConfig.Load(s.Inputs(configFile: broken)));
    }

    [TestMethod]
    public void Settings_files_allow_comments_and_trailing_commas_and_warn_about_unknown_keys()
    {
        using var s = new Sandbox();
        s.Write("home/src/repo/.labcoat.json", """
            {
              // team defaults
              "$schema": "https://raw.githubusercontent.com/eriklieben/labcoat/main/docs/labcoat.schema.json",
              "ui": { "editor": "rider", },
              "theme": "dracula",
              "colour": "blue",
              "port": 0,
              "coverage": "yes",
              "traces": true,
            }
            """);

        var config = LabcoatConfig.Load(s.Inputs());

        Assert.AreEqual("rider", config.UiDefaults!["editor"]!.GetValue<string>());
        Assert.IsTrue(config.Traces);
        Assert.IsFalse(config.Coverage);
        Assert.IsNull(config.Port);
        Assert.HasCount(4, config.Warnings, string.Join("\n", config.Warnings));
        Assert.IsTrue(config.Warnings.Any(w => w.Contains("'theme' is a UI setting; put it under \"ui\"")));
        Assert.IsTrue(config.Warnings.Any(w => w.Contains("unknown setting 'colour'")));
    }

    [TestMethod]
    public void Malformed_workspace_file_is_a_warning()
    {
        using var s = new Sandbox();
        s.Write("home/src/repo/.labcoat.json", "{ nope");
        var config = LabcoatConfig.Load(s.Inputs());
        Assert.IsNull(config.UiDefaults);
        Assert.HasCount(1, config.Warnings);
        Assert.Contains("could not be read", config.Warnings[0]);
    }

    // ------------------------------------------------------------------ index.html

    [TestMethod]
    public void Index_page_gets_the_defaults_as_an_attribute_and_keeps_its_inline_script_hashes()
    {
        // The line endings of a raw string literal are the source file's: CRLF in a Windows (autocrlf) checkout.
        var html = """
            <!doctype html>
            <html lang="en">
              <head><script>var x = "<html>";</script></head>
            </html>
            """.ReplaceLineEndings("\n");
        var defaults = Obj("""{"theme":"dracula","locked":["theme"],"files":{"workspace":"a\"b<c>.json"}}""");
        var dir = Directory.CreateTempSubdirectory("labcoat-index-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "index.html"), html);
            var served = new IndexPage(dir.FullName, defaults).Html()!;

            Assert.StartsWith("<!doctype html>\n<html data-labcoat-defaults=\"{&quot;theme&quot;:&quot;dracula&quot;", served);
            Assert.DoesNotContain("<c>", served); // HTML-encoded
            CollectionAssert.AreEqual(SecurityHeaders.InlineScripts(html).ToArray(), SecurityHeaders.InlineScripts(served).ToArray());
            // decoding the attribute gives the JSON back
            var attr = DefaultsAttribute().Match(served).Groups[1].Value;
            Assert.AreEqual(defaults.ToJsonString(), System.Net.WebUtility.HtmlDecode(attr));

            Assert.AreEqual(html, new IndexPage(dir.FullName, null).Html());
        }
        finally { dir.Delete(recursive: true); }
    }

    [System.Text.RegularExpressions.GeneratedRegex("data-labcoat-defaults=\"([^\"]*)\"")]
    private static partial System.Text.RegularExpressions.Regex DefaultsAttribute();
}
