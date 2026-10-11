using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace Labcoat.TestingPlatform.Tests;

[TestClass]
public class CommandLineTests
{
    private static readonly LabcoatCommandLineProvider Provider = new();
    private static readonly CommandLineOption Option = Provider.GetCommandLineOptions().Single();

    [TestMethod]
    public void Provider_declares_one_visible_labcoat_option_with_optional_settings()
    {
        Assert.AreEqual("labcoat", Option.Name);
        Assert.AreEqual(ArgumentArity.ZeroOrMore, Option.Arity);
        Assert.IsFalse(Option.IsHidden);
        Assert.Contains("watch, coverage, traces, no-open, port=<n>, root=<dir>", Option.Description);
        Assert.AreEqual("Labcoat.TestingPlatform", Provider.Uid);
        Assert.AreEqual("Labcoat", Provider.DisplayName);
        Assert.IsFalse(string.IsNullOrEmpty(Provider.Version));
        Assert.DoesNotContain("+", Provider.Version);
        Assert.IsFalse(string.IsNullOrEmpty(Provider.Description));
    }

    [TestMethod]
    public async Task Provider_is_always_enabled() => Assert.IsTrue(await Provider.IsEnabledAsync());

    [TestMethod]
    [DataRow(new string[0], DisplayName = "no settings")]
    [DataRow(new[] { "watch" })]
    [DataRow(new[] { "coverage", "traces", "no-open" })]
    [DataRow(new[] { "port=5000" })]
    [DataRow(new[] { "port=1", "port=65535" })]
    [DataRow(new[] { "PORT=4300", "Watch" }, DisplayName = "names are case-insensitive")]
    [DataRow(new[] { "--watch", "--port=5000" }, DisplayName = "leading dashes are tolerated")]
    [DataRow(new[] { "root=/src", "root=C:\\src" })]
    public async Task Valid_settings_are_accepted(string[] settings)
    {
        var result = await Provider.ValidateOptionArgumentsAsync(Option, settings);
        Assert.IsTrue(result.IsValid, result.ErrorMessage);
    }

    [TestMethod]
    [DataRow("port=0")]
    [DataRow("port=65536")]
    [DataRow("port=x")]
    [DataRow("port")]
    [DataRow("root=")]
    [DataRow("root")]
    [DataRow("watch=1")]
    [DataRow("no-open=yes")]
    [DataRow("w", DisplayName = "short forms are not settings")]
    [DataRow("browser")]
    public async Task Invalid_settings_are_rejected_with_the_list_of_settings(string setting)
    {
        var result = await Provider.ValidateOptionArgumentsAsync(Option, ["watch", setting]);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual($"Unknown --labcoat setting '{setting}'. Use: watch, coverage, traces, no-open, port=<n>, root=<dir>.", result.ErrorMessage);
    }

    [TestMethod]
    public async Task Labcoat_is_refused_under_dotnet_test()
    {
        var result = await Provider.ValidateCommandLineOptionsAsync(new FakeOptions(("labcoat", []), ("server", ["dotnettestcli"])));
        Assert.IsFalse(result.IsValid);
        Assert.Contains("isn't supported under 'dotnet test'", result.ErrorMessage);
        Assert.Contains($"'{LabcoatInfo.DnxCommand}'", result.ErrorMessage);

        Assert.IsTrue((await Provider.ValidateCommandLineOptionsAsync(new FakeOptions(("labcoat", []), ("server", [])))).IsValid,
            "plain --server (an IDE, Labcoat itself): the launcher is inert there, no error");
        Assert.IsTrue((await Provider.ValidateCommandLineOptionsAsync(new FakeOptions(("labcoat", []), ("server", ["jsonrpc"])))).IsValid);
        Assert.IsTrue((await Provider.ValidateCommandLineOptionsAsync(new FakeOptions(("server", ["dotnettestcli"])))).IsValid,
            "dotnet test without --labcoat");
        Assert.IsTrue((await Provider.ValidateCommandLineOptionsAsync(new FakeOptions())).IsValid);
    }

    [TestMethod]
    public void The_dnx_hint_has_prerelease_only_for_a_prerelease()
    {
        Assert.AreEqual("dnx Labcoat --prerelease", LabcoatInfo.DnxCommandFor("1.1.0-beta.1"));
        Assert.AreEqual("dnx Labcoat", LabcoatInfo.DnxCommandFor("1.2.0"));
    }

    [TestMethod]
    public void Settings_split_into_name_and_value()
    {
        Assert.AreEqual(("watch", (string?)null), LabcoatCommandLineProvider.Split("watch"));
        Assert.AreEqual(("port", (string?)"5000"), LabcoatCommandLineProvider.Split("--Port=5000"));
        Assert.AreEqual(("root", (string?)"C:\\A=B"), LabcoatCommandLineProvider.Split("root=C:\\A=B"), "only the first '=' separates");
    }

    [TestMethod]
    public void Settings_become_labcoat_options()
    {
        CollectionAssert.AreEqual(
            new[] { "--watch", "--port", "5000", "--root", "/src", "--no-open" },
            LabcoatCommandLineProvider.ToLabcoatArgs(["watch", "port=5000", "--root=/src", "no-open"]).ToArray());
        Assert.IsEmpty(LabcoatCommandLineProvider.ToLabcoatArgs([]).ToList());
    }

    [TestMethod]
    public void Builder_hook_registers_the_option_and_the_launcher()
    {
        var builder = new FakeBuilder();
        TestingPlatformBuilderHook.AddExtensions(builder, []);

        Assert.HasCount(1, builder.Providers);
        Assert.IsInstanceOfType<LabcoatCommandLineProvider>(builder.Providers[0]());
        Assert.HasCount(1, builder.Lifetimes);
        var launcher = builder.Lifetimes[0](new Services(new FakeOptions(("labcoat", []))));
        Assert.IsInstanceOfType<LabcoatLauncher>(launcher);
        Assert.AreEqual("Labcoat.TestingPlatform.Launcher", launcher.Uid);
        Assert.AreEqual("Labcoat", launcher.DisplayName);
        Assert.AreEqual(Provider.Version, launcher.Version);
        Assert.IsFalse(string.IsNullOrEmpty(launcher.Description));
    }
}
