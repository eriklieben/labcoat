using System.Text;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>Verify snapshot detection and file operations (Snapshots.cs): message parsing, naming, kinds, containment.</summary>
[TestClass]
public class SnapshotTests
{
    private DirectoryInfo _dir = null!;
    private string Project => Path.Combine(_dir.FullName, "P.Tests");
    private string Snaps => Path.Combine(Project, "Snapshots");

    [TestInitialize]
    public void Init()
    {
        _dir = Directory.CreateTempSubdirectory("labcoat-snap-");
        Directory.CreateDirectory(Snaps);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _dir.Delete(recursive: true); } catch { }
    }

    /// <summary>The message MSTest reports for a Verify failure (as in the samples).</summary>
    public static string Message(string dir, string section = "NotEqual", params string[] names)
    {
        var sb = new StringBuilder($"Test method P.Tests.ReceiptTests.Render threw exception:\nVerifyException: Directory: {dir}\n{section}:\n");
        foreach (var n in names) sb.Append($"  - Received: {n}.received.txt\n    Verified: {n}.verified.txt\n");
        sb.Append("\nFileContent:\n\nNotEqual:\n\nReceived: Other.received.txt\nnew\nVerified: Other.verified.txt\nold\n");
        return sb.ToString();
    }

    private TestNode Node(string? message, string cls = "ReceiptTests", string method = "Render", Outcome outcome = Outcome.Failed) => new()
    {
        Id = "t1", ProjectId = "P.Tests", DisplayName = method, ClassName = cls, MethodName = method,
        FilePath = Path.Combine(Snaps, cls + ".cs"), Outcome = outcome, ErrorMessage = message,
    };

    private string Write(string name, string text = "x")
    {
        var p = Path.Combine(Snaps, name);
        File.WriteAllText(p, text);
        return p;
    }

    [TestMethod]
    public void Mismatch_messages_are_recognized()
    {
        Assert.IsTrue(VerifySnapshots.IsMismatchMessage(Message("/x", "NotEqual", "A.B")));
        Assert.IsTrue(VerifySnapshots.IsMismatchMessage(Message("/x", "New", "A.B").Replace("\n", "\r\n")));
        Assert.IsFalse(VerifySnapshots.IsMismatchMessage(null));
        Assert.IsFalse(VerifySnapshots.IsMismatchMessage("Assert.AreEqual failed. Expected:<1>. Actual:<2>."));
        Assert.IsFalse(VerifySnapshots.IsMismatchMessage("Directory: /x\nsomething else"));
    }

    [TestMethod]
    public void Parse_reads_new_and_changed_entries_and_stops_at_the_file_content()
    {
        var msg = "VerifyException: Directory: /src/T\nNew:\n  - Received: A.N.received.txt\n    Verified: A.N.verified.txt\n" +
                  "NotEqual:\n  - Received: A.C.received.txt\n    Verified: A.C.verified.txt\n  - Received: A.C#img.received.png\n    Verified: A.C#img.verified.png\n" +
                  "Delete:\n  - A.Old.verified.txt\n\nFileContent:\n\nNew:\n\nReceived: A.N.received.txt\nVerified: A.N.verified.txt\n";
        var (dir, entries) = VerifySnapshots.Parse(msg);
        Assert.AreEqual("/src/T", dir);
        CollectionAssert.AreEqual(new[]
        {
            new VerifySnapshots.ParsedEntry("A.N.received.txt", "A.N.verified.txt", true),
            new VerifySnapshots.ParsedEntry("A.C.received.txt", "A.C.verified.txt", false),
            new VerifySnapshots.ParsedEntry("A.C#img.received.png", "A.C#img.verified.png", false),
        }, entries);
        Assert.AreEqual((null, 0), (VerifySnapshots.Parse(null).Directory, VerifySnapshots.Parse(null).Entries.Count));
        Assert.IsEmpty(VerifySnapshots.Parse("plain failure").Entries);
    }

    [TestMethod]
    public void File_names_and_kinds()
    {
        Assert.AreEqual("A.B_n=2.verified.txt", VerifySnapshots.VerifiedNameFor("A.B_n=2.received.txt"));
        Assert.IsNull(VerifySnapshots.VerifiedNameFor("A.B.txt"));
        Assert.IsNull(VerifySnapshots.VerifiedNameFor(".received.txt"));
        Assert.IsNull(VerifySnapshots.VerifiedNameFor("A.received."));
        Assert.IsTrue(VerifySnapshots.IsSnapshotFileName("A.B.verified.png"));
        Assert.IsTrue(VerifySnapshots.IsSnapshotFileName("A.B.received.txt"));
        Assert.IsFalse(VerifySnapshots.IsSnapshotFileName("A.B.cs"));
        Assert.IsFalse(VerifySnapshots.IsSnapshotFileName("A.verified."));

        Assert.AreEqual(SnapshotKind.Image, VerifySnapshots.KindOf("/nowhere/a.received.PNG"));
        Assert.AreEqual(SnapshotKind.Text, VerifySnapshots.KindOf(Write("A.T.received.txt", "hello")));
        var bin = Path.Combine(Snaps, "A.B.received.bin");
        File.WriteAllBytes(bin, [1, 0, 2]);
        Assert.AreEqual(SnapshotKind.Binary, VerifySnapshots.KindOf(bin));
        Assert.AreEqual(SnapshotKind.Text, VerifySnapshots.KindOf("/nowhere/a.received.txt"));
        Assert.AreEqual("image/png", VerifySnapshots.ContentType("a.png"));
        Assert.AreEqual("image/jpeg", VerifySnapshots.ContentType("a.JPEG"));
        Assert.AreEqual("image/webp", VerifySnapshots.ContentType("a.webp"));
        Assert.AreEqual("application/octet-stream", VerifySnapshots.ContentType("a.bin"));
    }

    [TestMethod]
    public void Verify_projects_are_recognized_by_their_packages()
    {
        Assert.IsTrue(Explorer.UsesVerify(["MSTest", "Verify.MSTest"]));
        Assert.IsTrue(Explorer.UsesVerify(["verify"]));
        Assert.IsTrue(Explorer.UsesVerify(["Verify.XunitV3"]));
        Assert.IsFalse(Explorer.UsesVerify(["MSTest", "VerifyCS", "FluentAssertions"]));
        Assert.IsFalse(Explorer.UsesVerify([]));
    }

    [TestMethod]
    public void Test_hosts_never_launch_a_diff_tool()
    {
        var env = new Dictionary<string, string?>();
        VerifySnapshots.ConfigureTestHost(env);
        Assert.AreEqual("true", env["DiffEngine_Disabled"]);
    }

    [TestMethod]
    public void Find_uses_the_message_and_skips_handled_or_foreign_files()
    {
        Write("ReceiptTests.Render.received.txt", "new");
        Write("ReceiptTests.Render.verified.txt", "old");
        Write("ReceiptTests.Fresh.received.txt", "new");
        var msg = Message(Snaps, "NotEqual", "ReceiptTests.Render", "ReceiptTests.Gone", "ReceiptTests.Fresh");
        var (dir, files) = VerifySnapshots.Find(Node(msg), Project);
        Assert.AreEqual(Snaps, dir);
        Assert.HasCount(2, files);
        Assert.AreEqual(("ReceiptTests.Render.verified.txt", false, SnapshotKind.Text), (files[0].Name, files[0].IsNew, files[0].Kind));
        Assert.AreEqual(PathUtil.Canonicalize(Path.Combine(Snaps, "ReceiptTests.Render.received.txt")), files[0].ReceivedPath);
        Assert.AreEqual(PathUtil.Canonicalize(Path.Combine(Snaps, "ReceiptTests.Render.verified.txt")), files[0].VerifiedPath);
        Assert.IsTrue(files[1].IsNew); // no verified file yet

        // "New:" in the message marks it new even when an (empty placeholder) verified file exists
        Write("ReceiptTests.Fresh.verified.txt", "");
        Assert.IsTrue(VerifySnapshots.Find(Node(Message(Snaps, "New", "ReceiptTests.Fresh")), Project).Files.Single().IsNew);

        // a directory outside the project, names with separators or a verified name that doesn't match are ignored
        var outside = Directory.CreateDirectory(Path.Combine(_dir.FullName, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(outside, "X.Y.received.txt"), "x");
        Assert.IsEmpty(VerifySnapshots.Find(Node(Message(outside, "New", "X.Y")), Project).Files);
        var crafted = $"Directory: {Snaps}\nNew:\n  - Received: ../ReceiptTests.Render.received.txt\n    Verified: ReceiptTests.Render.verified.txt\n" +
                      $"  - Received: ReceiptTests.Render.received.txt\n    Verified: Program.cs\n";
        Assert.IsEmpty(VerifySnapshots.Find(Node(crafted), Project).Files);
    }

    [TestMethod]
    public void Find_falls_back_to_the_test_files_directory()
    {
        Write("ReceiptTests.Render.received.txt");
        Write("ReceiptTests.Render_n=2.received.txt");
        Write("ReceiptTests.Render#chart.received.png");
        Write("ReceiptTests.RenderAll.received.txt"); // another test whose name starts the same
        Write("OtherTests.Render.received.txt");
        // a message cut short (no entries) -> scan
        var cut = $"VerifyException: Directory: {Snaps}\nNotEqual:\n  - Rec";
        var (dir, files) = VerifySnapshots.Find(Node(cut), Project);
        Assert.AreEqual(Snaps, dir);
        CollectionAssert.AreEquivalent(new[] { "ReceiptTests.Render#chart.verified.png", "ReceiptTests.Render.verified.txt", "ReceiptTests.Render_n=2.verified.txt" },
            files.Select(f => f.Name).ToArray());
        Assert.AreEqual(SnapshotKind.Image, files.Single(f => f.Name.EndsWith(".png")).Kind);

        // nested class names use the last segment; no class / missing directory -> nothing
        Assert.HasCount(3, VerifySnapshots.Find(Node(null, cls: "Outer+ReceiptTests"), Project).Files);
        Assert.IsEmpty(VerifySnapshots.Find(Node(null) with { ClassName = null }, Project).Files);
        Assert.IsEmpty(VerifySnapshots.Find(Node(null) with { FilePath = Path.Combine(_dir.FullName, "missing", "A.cs") }, Project).Files);
    }

    [TestMethod]
    public void Links_out_of_the_project_are_refused()
    {
        var secret = Path.Combine(_dir.FullName, "secret.txt");
        File.WriteAllText(secret, "secret");
        try { File.CreateSymbolicLink(Path.Combine(Snaps, "ReceiptTests.Render.received.txt"), secret); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            // Windows: creating a symlink takes an elevated process or Developer Mode.
            Assert.Inconclusive("this account can't create symlinks: " + ex.Message);
        }
        Assert.IsEmpty(VerifySnapshots.Find(Node(Message(Snaps, "New", "ReceiptTests.Render")), Project).Files);

        // verified file is a link to outside the project
        Write("ReceiptTests.Two.received.txt");
        File.CreateSymbolicLink(Path.Combine(Snaps, "ReceiptTests.Two.verified.txt"), secret);
        Assert.IsEmpty(VerifySnapshots.Find(Node(Message(Snaps, "NotEqual", "ReceiptTests.Two")), Project).Files);
    }

    [TestMethod]
    public void Validate_accept_and_reject()
    {
        var received = Write("A.B.received.txt", "new");
        var verified = Path.Combine(Snaps, "A.B.verified.txt");
        File.WriteAllText(verified, "old");
        var f = new SnapshotFile("A.B.verified.txt", received, verified, SnapshotKind.Text, false);
        Assert.IsNull(VerifySnapshots.Validate(f, Project));
        Assert.Contains("outside", VerifySnapshots.Validate(f, Path.Combine(_dir.FullName, "Other"))!);
        Assert.Contains("don't match", VerifySnapshots.Validate(f with { VerifiedPath = Path.Combine(Snaps, "Program.cs") }, Project)!);
        Assert.Contains("don't match", VerifySnapshots.Validate(f with { VerifiedPath = Path.Combine(Project, "A.B.verified.txt") }, Project)!);

        VerifySnapshots.Accept(f);
        Assert.IsFalse(File.Exists(received));
        Assert.AreEqual("new", File.ReadAllText(verified));
        Assert.Contains("no longer exists", VerifySnapshots.Validate(f, Project)!);

        File.WriteAllText(received, "again");
        VerifySnapshots.Reject(f);
        Assert.IsFalse(File.Exists(received));
        Assert.AreEqual("new", File.ReadAllText(verified));

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(received, verified);
            Assert.Contains("link", VerifySnapshots.Validate(f, Project)!);
        }
    }

    [TestMethod]
    public void Test_nodes_flag_snapshot_mismatches_until_resolved()
    {
        var t = Node(Message(Snaps, "NotEqual", "A.B"));
        Assert.IsTrue(t.HasSnapshotMismatch);
        Assert.IsNull((t with { Outcome = Outcome.Passed }).HasSnapshotMismatch);
        Assert.IsNull(Node("Assert.AreEqual failed").HasSnapshotMismatch);
        Assert.IsTrue((t with { Outcome = Outcome.TimedOut }).HasSnapshotMismatch);
        t.SnapshotsResolved = true;
        Assert.IsNull(t.HasSnapshotMismatch);
        Assert.IsTrue(t.Copy().SnapshotsResolved);
        t.ClearResult();
        Assert.IsFalse(t.SnapshotsResolved);

        var json = System.Text.Json.JsonSerializer.Serialize(Node(Message(Snaps, "New", "A.B")), Json.Options);
        Assert.Contains("\"hasSnapshotMismatch\":true", json);
        Assert.IsFalse(json.Contains("snapshotsResolved", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("hasSnapshotMismatch", System.Text.Json.JsonSerializer.Serialize(Node(null, outcome: Outcome.Passed), Json.Options));

        // a new result clears the resolved marker
        var u = Node(Message(Snaps, "New", "A.B"));
        u.SnapshotsResolved = true;
        MtpMapping.ApplyResult(u, Node(Message(Snaps, "New", "A.B")), Outcome.Failed, "failed");
        Assert.IsTrue(u.HasSnapshotMismatch);
    }
}
