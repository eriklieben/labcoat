using System.Text.Json.Nodes;
using Labcoat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Labcoat.Tests.Unit;

/// <summary>Edge and error paths of the workspace data folder, the host log and the diagnostics report.</summary>
[TestClass]
public class DiagnosticsEdgeTests
{
    public TestContext TestContext { get; set; }

    private static string TempDir(string prefix) => ExplorerTests.ScratchWorkspace.CreateRealTempDirectory(prefix);

    private static TestNode Node(string project, string id, Outcome outcome, string? at = "2026-10-09T08:00:00Z") => new()
    {
        Id = id, ProjectId = project, DisplayName = id, Outcome = outcome, LastRunAt = at,
    };

    // ------------------------------------------------------------------ workspace identity and lock

    [TestMethod]
    public void Paths_outside_the_root_and_odd_names()
    {
        var root = TempDir("labcoat-edge-");
        try
        {
            var name = new string('n', 50) + " x";
            var project = Path.Combine(root, name, "A.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            var id = WorkspaceIdentity.Resolve([project]);
            Assert.AreEqual(new string('n', 40), id.FolderName[..id.FolderName.LastIndexOf('-')], "40 safe characters");
            var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "B.csproj");
            // outside the root: the full path, symlinks resolved like the workspace root (macOS: /var → /private/var)
            Assert.AreEqual(PathUtil.CanonicalizeOrSelf(outside).Replace('\\', '/'), id.RelativeProjectPath(outside));
            var none = WorkspaceIdentity.Resolve([]);
            Assert.AreEqual(Path.GetFullPath(project).Replace('\\', '/'), none.RelativeProjectPath(project));
            Assert.AreEqual("none", none.Describe()["kind"]!.GetValue<string>());
            File.WriteAllText(Path.Combine(root, name, "Odd Name!.sln"), "");
            // case-folded on Windows, where paths are case-insensitive
            StringAssert.StartsWith(WorkspaceIdentity.Resolve([project]).FolderName, "Odd_Name_-", StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }

        // outside the home and temp directories, without git or a solution up to the file system root
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Assert.AreEqual("directory", WorkspaceIdentity.Resolve([Path.Combine(runtimeDir, "X.csproj")]).Kind);
    }

    [TestMethod]
    public void Leases_upgrade_dispose_once_and_survive_odd_owner_files()
    {
        var dir = Path.Combine(TempDir("labcoat-lease-"), "data");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, WorkspaceLock.OwnerFileName)); // can't be written: only informational
            var a = WorkspaceLock.Acquire(dir);
            var b = WorkspaceLock.Acquire(dir);
            Assert.IsTrue(a.Owner && b.Owner);
            Assert.IsTrue(a.TryUpgrade(), "an owner stays one");
            a.Dispose();
            a.Dispose();
            Assert.IsFalse(a.TryUpgrade(), "disposed");
            Assert.IsTrue(WorkspaceLock.CanWrite(dir));
            b.Dispose();
            Assert.IsNull(WorkspaceLock.ReadOwnerPid(dir), "a directory, not a file");

            Directory.Delete(Path.Combine(dir, WorkspaceLock.OwnerFileName));
            foreach (var text in new[] { "{ not json", "null", """{"pid":"x"}""", "{}" })
            {
                File.WriteAllText(Path.Combine(dir, WorkspaceLock.OwnerFileName), text);
                Assert.IsNull(WorkspaceLock.ReadOwnerPid(dir), text);
            }

            // held elsewhere and the owner unknown: "unknown pid"; a non-owner lease's dispose releases nothing
            File.Delete(Path.Combine(dir, WorkspaceLock.OwnerFileName));
            using var other = new FileStream(Path.Combine(dir, WorkspaceLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var denied = WorkspaceLock.Acquire(dir);
            Assert.IsFalse(denied.Owner);
            Assert.IsNull(denied.OtherPid);
            denied.Dispose();
            Assert.IsFalse(WorkspaceLock.CanWrite(dir));
        }
        finally
        {
            var lease = WorkspaceLock.Acquire(dir); // after the "other" handle is gone: back to writable
            lease.Dispose();
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [TestMethod]
    public void A_read_only_data_folder_is_not_a_lock_conflict()
    {
        if (OperatingSystem.IsWindows()) return; // Unix file modes
        var dir = Path.Combine(TempDir("labcoat-ro-"), "data");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var lease = WorkspaceLock.Acquire(dir);
            Assert.IsTrue(lease.Owner);
            Assert.IsTrue(WorkspaceLock.CanWrite(dir));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [TestMethod]
    public void Unknown_owner_and_no_log_while_another_instance_holds_the_folder()
    {
        var root = TempDir("labcoat-nolog-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var project = Path.Combine(root, "A", "A.csproj");
            var dataDir = WorkspaceIdentity.Resolve([project]).DataDirectory;
            Directory.CreateDirectory(dataDir);
            var other = new FileStream(Path.Combine(dataDir, WorkspaceLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var log = new List<string>();
            using (var logged = new HistoryStore([project], (_, m) => log.Add(m)))
                StringAssert.Contains(log.Single(), "unknown pid");
            using var store = new HistoryStore([project]); // no log
            store.Record(new RunSummary { RunId = "r", StartedAt = "2026-10-09T08:00:00Z" }, [Node("A", "t", Outcome.Passed)]);
            other.Dispose();
            store.Save();
            Assert.IsFalse(store.ReadOnly);
            Assert.IsTrue(File.Exists(store.FilePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ merging and migration

    [TestMethod]
    public void Merging_caps_history_and_runs_and_keeps_the_newer_result()
    {
        var root = TempDir("labcoat-merge-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var project = Path.Combine(root, "A", "A.csproj");
            var dataDir = WorkspaceIdentity.Resolve([project]).DataDirectory;
            using (var first = new HistoryStore([project]))
            {
                for (var i = 0; i < HistoryStore.MaxStoredRuns; i++)
                    first.Record(new RunSummary { RunId = $"m{i}", StartedAt = $"2026-10-01T08:{i:00}:00Z" }, [Node("A", "t", Outcome.Passed, $"2026-10-01T08:{i:00}:00Z")]);
                first.Record(new RunSummary { RunId = "m-null", StartedAt = "2026-10-01T09:00:00Z" }, [Node("A", "no-time", Outcome.Failed, null)]);
            }

            var other = new FileStream(Path.Combine(dataDir, WorkspaceLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using (var second = new HistoryStore([project]))
            {
                Assert.IsTrue(second.ReadOnly);
                second.Record(new RunSummary { RunId = "late", StartedAt = "2026-10-02T08:00:00Z" }, [Node("A", "t", Outcome.Failed, "2026-10-02T08:00:00Z")]);
                other.Dispose();
                second.Save(); // takes over: merges the file (the same runs again, capped) and writes
            }

            using var again = new HistoryStore([project]);
            var t = Node("A", "t", Outcome.NotRun);
            again.Restore(t);
            Assert.HasCount(HistoryStore.MaxRuns, t.History!);
            Assert.AreEqual(Outcome.Failed, t.Outcome, "the newer result");
            Assert.HasCount(HistoryStore.MaxStoredRuns, again.Runs());
            Assert.AreEqual("late", again.Runs()[0].RunId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Projects_spread_over_the_temp_directory_are_a_broad_workspace()
    {
        var a = TempDir("labcoat-broad-a-");
        var b = TempDir("labcoat-broad-b-");
        try
        {
            var id = WorkspaceIdentity.Resolve([Path.Combine(a, "A.csproj"), Path.Combine(b, "B.csproj")]);
            Assert.AreEqual("directory", id.Kind);
            Assert.IsTrue(id.IsBroad);
            Assert.IsFalse(WorkspaceIdentity.Resolve([Path.Combine(a, "A.csproj")]).IsBroad);
            var log = new List<string>();
            using (new HistoryStore([Path.Combine(a, "A.csproj"), Path.Combine(b, "B.csproj")], (_, m) => log.Add(m)))
                Assert.IsFalse(log.Any(l => l.StartsWith("Merged")), "never claims other workspaces' folders");
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [TestMethod]
    public void Migration_reads_big_and_broken_old_files_and_survives_unwritable_targets()
    {
        var root = TempDir("labcoat-mig-");
        var baseDir = WorkspaceIdentity.BaseDirectory;
        var folders = new List<string>();
        string Legacy(string content)
        {
            var dir = Path.Combine(baseDir, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..16]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "history.json"), content);
            folders.Add(dir);
            return dir;
        }
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var project = Path.Combine(root, "A", "A.csproj");
            var projects = new JsonArray(JsonValue.Create(project));
            // "projects" after 300 KB: read in full
            var big = new JsonObject { ["version"] = 1, ["padding"] = new string('x', 300 * 1024), ["projects"] = projects.DeepClone(), ["tests"] = new JsonObject() };
            var bigDir = Legacy(big.ToJsonString());
            // the projects match but the rest is broken: a warning, nothing merged
            var broken = Legacy($$"""{"version":2,"projects":{{projects.ToJsonString()}},"projectIds":"nope"}""");
            var unreadable = Legacy("{ not json at all");
            File.WriteAllText(Path.Combine(bigDir, "coverage.json"), "{}");

            var dataDir = WorkspaceIdentity.Resolve([project]).DataDirectory;
            Directory.CreateDirectory(Path.Combine(dataDir, "coverage.json")); // can't be copied over
            File.WriteAllText(Path.Combine(dataDir, "settings.json"), "{}");     // already there: kept
            Directory.CreateDirectory(Path.Combine(dataDir, HistoryStore.WorkspaceFileName)); // can't be written

            var log = new List<string>();
            using (new HistoryStore([project], (level, m) => log.Add($"{level}: {m}")))
            {
                var all = string.Join("\n", log);
                StringAssert.Contains(all, "Merged the test history of 2 earlier folder(s)");
                StringAssert.Contains(all, "warning: Could not read test history");
                StringAssert.Contains(all, "warning: Could not copy");
                StringAssert.Contains(all, "warning: Could not write");
            }
            Assert.AreEqual("{}", File.ReadAllText(Path.Combine(dataDir, "settings.json")));
            Assert.IsTrue(File.Exists(Path.Combine(bigDir, HistoryStore.MigratedFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(unreadable, HistoryStore.MigratedFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(broken, HistoryStore.MigratedFileName)));

            // ReadProjects: a number in the list, a cut file
            var f = Path.Combine(root, "p.json");
            File.WriteAllText(f, """{"projects":[1]}""");
            Assert.IsNull(HistoryStore.ReadProjects(f));
            File.WriteAllText(f, """{"a":""");
            Assert.IsNull(HistoryStore.ReadProjects(f));
            File.WriteAllText(f, """{"a":{"b":""");
            Assert.IsNull(HistoryStore.ReadProjects(f));
        }
        finally
        {
            foreach (var d in folders) Directory.Delete(d, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ host log

    [TestMethod]
    public void The_log_keeps_the_last_lines_logs_every_level_and_tails_big_files()
    {
        var dir = TempDir("labcoat-log2-");
        try
        {
            using var log = new HostLog(LogLevel.Trace);
            for (var i = 0; i < HostLog.RecentCapacity + 10; i++) log.Write("info", $"l{i}");
            Assert.HasCount(HostLog.RecentCapacity, log.Recent(int.MaxValue));
            Assert.EndsWith("l10", log.Recent(int.MaxValue)[0]);
            var logger = log.CreateLogger("C");
            logger.LogTrace("t");
            logger.LogDebug("d");
            CollectionAssert.AreEqual(new[] { "trace", "debug" }, log.Recent(2).Select(l => l.Split(' ')[1]).ToArray());
            Assert.IsNull(HostLog.ParseLevel("3"));
            Assert.IsNull(HostLog.ParseLevel(""));
            Assert.AreEqual(LogLevel.Warning, HostLog.ParseLevel("Warning"));

            var big = Path.Combine(dir, "big.log");
            File.WriteAllLines(big, Enumerable.Range(0, 20_000).Select(i => $"line {i:00000} " + new string('y', 20)));
            var tail = HostLog.Tail(big, 3);
            CollectionAssert.AreEqual(new[] { "line 19997", "line 19998", "line 19999" }, tail.Select(l => l[..10]).ToArray());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(big, UnixFileMode.None);
                Assert.IsEmpty(HostLog.Tail(big, 3), "unreadable");
                File.SetUnixFileMode(big, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public async Task A_verbose_host_writes_its_log_file_and_names_it_in_the_report()
    {
        var dir = TempDir("labcoat-verbose-");
        try
        {
            var logFile = Path.Combine(dir, "logs", "labcoat.log");
            var app = LabcoatApp.Build(new LabcoatAppOptions
            {
                Paths = [dir], Projects = [], Guard = new RequestGuard([]), WebRoot = Directory.CreateDirectory(Path.Combine(dir, "www")).FullName,
                LogLevel = LogLevel.Debug, LogFile = logFile, SettingsFiles = [".labcoat.json"],
            }, b => b.WebHost.UseTestServer());
            await app.StartAsync(TestContext.CancellationToken);
            try
            {
                using var client = app.GetTestServer().CreateClient();
                var json = JsonNode.Parse(await client.GetStringAsync("/api/diagnostics", TestContext.CancellationToken))!;
                Assert.AreEqual("Debug", (string?)json["report"]!["settings"]!["logLevel"]);
                Assert.AreEqual(".labcoat.json", (string?)json["report"]!["settings"]!["settingsFiles"]);
                StringAssert.Contains((string?)json["report"]!["settings"]!["logFile"], "labcoat.log");
            }
            finally
            {
                await app.StopAsync(TestContext.CancellationToken);
                await app.DisposeAsync();
            }
            var text = File.ReadAllText(logFile);
            StringAssert.Contains(text, "debug");
            StringAssert.Contains(text, "Microsoft.");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ the report

    [TestMethod]
    public void Project_files_and_assets_that_are_odd_or_broken()
    {
        var dir = TempDir("labcoat-assets-");
        try
        {
            var workspace = WorkspaceIdentity.Resolve([Path.Combine(dir, "x.csproj")]);
            Diagnostics.ProjectInfo Info(string name, string csproj, string? assets)
            {
                var pdir = Directory.CreateDirectory(Path.Combine(dir, name)).FullName;
                var path = Path.Combine(pdir, name + ".csproj");
                File.WriteAllText(path, csproj);
                if (assets is not null)
                {
                    Directory.CreateDirectory(Path.Combine(pdir, "obj"));
                    File.WriteAllText(Path.Combine(pdir, "obj", "project.assets.json"), assets);
                }
                return Diagnostics.Project(path, workspace, null);
            }

            var broken = Info("Broken", "<Project", "{ not json");
            StringAssert.StartsWith(broken.Note, "obj/project.assets.json could not be read");
            Assert.IsEmpty(broken.TargetFrameworks, "the project file isn't XML either");
            Assert.IsNull(broken.TestFramework);

            var odd = Info("Odd", """<Project Sdk="MSTest.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                """{"libraries":{"noslash":{"type":"package"},"NUnit/4.4.0":{},"xunit.v3/3.1.0":{"type":"package"}},"project":{"frameworks":[]}}""");
            Assert.AreEqual("MSTest (global.json)", odd.TestFramework, "MSTest.Sdk without a version: from global.json");
            CollectionAssert.AreEqual(new[] { "net10.0" }, odd.TargetFrameworks);
            CollectionAssert.AreEquivalent(new[] { "MSTest.Sdk", "NUnit", "xunit.v3" }, odd.Packages.Select(p => p.Name).ToArray());

            var plain = Info("Plain", """<Project Sdk="Microsoft.NET.Sdk" />""", """{"libraries":[]}""");
            Assert.IsEmpty(plain.Packages);
            var noSdk = Info("NoSdk", "<Project />", "{}");
            Assert.IsEmpty(noSdk.Packages);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void The_markdown_of_a_bare_report()
    {
        var report = new Diagnostics.Report
        {
            LabcoatVersion = "1.0.0", StartedAs = "x | y", Os = "os", Runtime = "rt", Sdk = null, WorkspaceKind = "none", Workspace = "",
            DataDirectory = "/d", DataReadOnly = true,
            Projects = [new Diagnostics.ProjectInfo { Name = "P", Path = "p.csproj" }],
            Settings = new SortedDictionary<string, string>(), Environment = new SortedDictionary<string, string>(), Log = [], LogSource = null,
        };
        var md = Diagnostics.ToMarkdown(report);
        StringAssert.Contains(md, "| Labcoat | 1.0.0 (x \\| y) |");
        StringAssert.Contains(md, "| .NET SDK | unknown |");
        StringAssert.Contains(md, "(read-only: another Labcoat uses it)");
        StringAssert.Contains(md, "P: (none found)");
        StringAssert.Contains(md, "(no LABCOAT_* or related variables)");
        StringAssert.Contains(md, "#### Log (last 0 lines)");
        StringAssert.Contains(md, "(empty)");
        StringAssert.Contains(Diagnostics.ToMarkdown(report with { Projects = [] }), "#### Test projects (0)");
    }

    [TestMethod]
    public async Task Redaction_details_and_a_cancelled_sdk_probe()
    {
        Assert.AreEqual("a://***@b c://d e://", Diagnostics.RedactUrlCredentials("a://u@b c://d e://"));
        Assert.AreEqual("x://h;y", Diagnostics.RedactUrlCredentials("x://h;y"));
        var selected = Diagnostics.SelectedEnvironment(new Dictionary<string, string?> { ["LABCOAT_X"] = null, ["LABCOAT_Y"] = "y" });
        CollectionAssert.AreEqual(new[] { "LABCOAT_Y" }, selected.Keys.ToArray());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var report = await Diagnostics.CollectAsync(new Diagnostics.Input
        {
            Projects = [],
            Log = ["k=short", "header part-secret-value and the full value"],
            Environment = new Dictionary<string, string?> { ["LABCOAT_OTLP_HEADERS"] = "a=b,noequals,k=part-secret-value", ["LABCOAT_TOKEN"] = "abc" },
            Home = "/",
        }, cancelled.Token);
        Assert.IsNull(report.Sdk);
        Assert.AreEqual("header *** and the full value", report.Log[1]);
        Assert.AreEqual("k=short", report.Log[0], "short secrets are not replaced in the log (too likely to hit ordinary text)");
    }

    [TestMethod]
    public void Clipboard_tools_are_tried_in_order()
    {
        Assert.IsFalse(Diagnostics.TryCopyToClipboard("x", []));
        Assert.IsFalse(Diagnostics.TryCopyToClipboard("x", [("labcoat-no-such-tool-xyz", [])]));
        _ = Diagnostics.ClipboardTools(); // per OS; never run here (it would change this machine's clipboard)
        if (OperatingSystem.IsWindows()) return;
        var dir = TempDir("labcoat-clip-");
        try
        {
            var target = Path.Combine(dir, "clip.txt");
            Assert.IsTrue(Diagnostics.TryCopyToClipboard("copied", [("sh", ["-c", "exit 3"]), ("sh", ["-c", $"cat > '{target}'"])]));
            Assert.AreEqual("copied", File.ReadAllText(target));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void The_home_directory_is_masked_only_as_a_whole_path()
    {
        var sep = Path.DirectorySeparatorChar;
        var home = Path.Combine(Path.GetTempPath(), "labcoat-home-test");
        Assert.AreEqual("~" + sep + "repo", Diagnostics.MaskHomeForTest(Path.Combine(home, "repo"), home));
        Assert.AreEqual("~", Diagnostics.MaskHomeForTest(home, home));
        Assert.AreEqual("dotnet \"~" + sep + "x.dll\"", Diagnostics.MaskHomeForTest("dotnet \"" + Path.Combine(home, "x.dll") + "\"", home));
        // a path that only contains the home path is left alone (not a symlink alias: on macOS /private/var/… really is
        // /var/…, so that one is masked, correctly)
        var elsewhere = sep + "srv" + Path.Combine(home, "repo");
        Assert.AreEqual(elsewhere, Diagnostics.MaskHomeForTest(elsewhere, home));
        Assert.AreEqual(home + "-other", Diagnostics.MaskHomeForTest(home + "-other", home));
    }
}
