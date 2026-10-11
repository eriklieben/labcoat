using System.Net;
using System.Net.Sockets;
using Labcoat;

namespace Labcoat.Tests;

[TestClass]
public class CliTests
{
    private const string TestProject = """<Project Sdk="MSTest.Sdk/4.0.2" />""";
    private const string AppProject = """<Project Sdk="Microsoft.NET.Sdk" />""";

    [TestMethod]
    public void Default_path_is_the_test_project_in_the_current_directory()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-cli-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var tests = Directory.CreateDirectory(Path.Combine(root.FullName, "Shop.Tests")).FullName;
            File.WriteAllText(Path.Combine(tests, "Shop.Tests.csproj"), TestProject);

            Assert.AreEqual(tests, Cli.ResolveDefaultPath(tests));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Default_path_walks_up_to_the_nearest_solution_preferring_slnx()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-cli-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            File.WriteAllText(Path.Combine(root.FullName, "Shop.sln"), "");
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var api = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Shop.Api")).FullName;
            File.WriteAllText(Path.Combine(api, "Shop.Api.csproj"), AppProject); // not a test project: keep walking

            var warnings = new List<string>();
            Assert.AreEqual(Path.Combine(root.FullName, "Shop.slnx"), Cli.ResolveDefaultPath(api, warnings.Add));
            Assert.IsEmpty(warnings, "Shop.sln next to Shop.slnx is not ambiguous");

            File.WriteAllText(Path.Combine(root.FullName, "Other.slnx"), "<Solution />");
            Assert.AreEqual(Path.Combine(root.FullName, "Other.slnx"), Cli.ResolveDefaultPath(api, warnings.Add));
            Assert.HasCount(1, warnings);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Default_path_stops_at_the_repository_root()
    {
        var outer = Directory.CreateTempSubdirectory("labcoat-cli-");
        try
        {
            File.WriteAllText(Path.Combine(outer.FullName, "Outer.slnx"), "<Solution />"); // above the repo: never used
            var repo = Directory.CreateDirectory(Path.Combine(outer.FullName, "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var docs = Directory.CreateDirectory(Path.Combine(repo, "docs")).FullName;

            Assert.AreEqual(docs, Cli.ResolveDefaultPath(docs));
        }
        finally { outer.Delete(recursive: true); }
    }

    [TestMethod]
    public void Busy_port_is_skipped()
    {
        using var busy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        busy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        busy.Listen();
        var port = ((IPEndPoint)busy.LocalEndPoint!).Port;

        Assert.IsFalse(Cli.IsPortFree(port));
        var free = Cli.FindFreePort(port);
        Assert.IsNotNull(free);
        Assert.IsGreaterThan(port, free.Value);
    }

    [TestMethod]
    public void Version_has_no_build_metadata() => Assert.DoesNotContain("+", Cli.Version);
}
