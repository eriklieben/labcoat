using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>OutputWatch.cs: assemblies rewritten in place under a warm test host.</summary>
[TestClass]
public class OutputWatchTests
{
    public TestContext TestContext { get; set; }

    private static DirectoryInfo s_dir = null!;

    [ClassInitialize]
    public static void Init(TestContext _) => s_dir = Directory.CreateTempSubdirectory("labcoat-outputwatch-");

    [ClassCleanup]
    public static void Cleanup()
    {
        try { s_dir.Delete(recursive: true); } catch { }
    }

    private static long Hash(string path) => BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)), 0);

    /// <summary>A folder with Lib.dll (and the hashes taken "when the host started").</summary>
    private static (string Dir, string Lib, Dictionary<string, long> Hashes) Folder()
    {
        var dir = s_dir.CreateSubdirectory(Guid.NewGuid().ToString("N")).FullName;
        var lib = Path.Combine(dir, "Lib.dll");
        File.WriteAllBytes(lib, [1, 2, 3, 4]);
        return (dir, lib, new Dictionary<string, long> { ["Lib.dll"] = Hash(lib) });
    }

    [TestMethod]
    public void Identical_rewrites_and_new_files_keep_the_host_different_bytes_count()
    {
        var (dir, lib, hashes) = Folder();
        using var watch = new OutputWatch(dir, hashes, Hash, TimeSpan.FromMilliseconds(20));

        watch.OnWrite("Lib.dll"); // same bytes (a deterministic rebuild)
        Assert.IsNull(watch.Check());
        watch.OnWrite("New.dll"); // not loaded when the host started
        Assert.IsNull(watch.Check());
        watch.OnWrite(null);
        Assert.IsNull(watch.Check());

        File.WriteAllBytes(lib, [9, 9]);
        watch.OnWrite("Lib.dll");
        Assert.AreEqual("Lib.dll", watch.Check());
        File.WriteAllBytes(lib, [1, 2, 3, 4]); // put back: still counts, the host may have read the other bytes
        Assert.AreEqual("Lib.dll", watch.Check());
        Assert.AreEqual("Lib.dll", watch.Rewritten);
    }

    [TestMethod]
    public void A_deleted_assembly_counts_as_rewritten()
    {
        var (dir, lib, hashes) = Folder();
        using var watch = new OutputWatch(dir, hashes, Hash);
        File.Delete(lib);
        watch.OnWrite("Lib.dll");
        Assert.AreEqual("Lib.dll", watch.Check());
    }

    [TestMethod]
    public void In_strict_mode_any_in_place_write_counts_even_when_the_bytes_are_restored()
    {
        var (dir, _, hashes) = Folder();
        using var watch = new OutputWatch(dir, hashes, Hash, TimeSpan.FromHours(1)); // only strict mode reports here
        var reported = new TaskCompletionSource<string>();
        watch.Modified = name => reported.TrySetResult(name);

        using (watch.BeginStrict())
        {
            watch.OnWrite("Other.dll"); // unknown file: ignored even in strict mode
            Assert.IsNull(watch.Rewritten);
            watch.OnWrite("Lib.dll"); // same bytes on disk by now (coverage restored them)
            Assert.AreEqual("Lib.dll", watch.Rewritten);
        }
        Assert.IsTrue(reported.Task.Wait(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
        Assert.AreEqual("Lib.dll", reported.Task.Result);
    }

    [TestMethod]
    public void Strict_mode_ends_with_its_scope()
    {
        var (dir, _, hashes) = Folder();
        using var watch = new OutputWatch(dir, hashes, Hash, TimeSpan.FromMilliseconds(20));
        var scope = watch.BeginStrict();
        scope.Dispose();
        scope.Dispose(); // twice: once
        watch.OnWrite("Lib.dll"); // same bytes, not strict any more
        Assert.IsNull(watch.Check());
    }

    [TestMethod]
    public async Task The_file_system_watcher_reports_an_in_place_write_once()
    {
        var (dir, lib, hashes) = Folder();
        using var watch = new OutputWatch(dir, hashes, Hash, TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(watch.IsWatching);
        var calls = 0;
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Modified = name =>
        {
            Interlocked.Increment(ref calls);
            reported.TrySetResult(name);
        };

        await using (var stream = new FileStream(lib, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) // same file, new bytes
            await stream.WriteAsync(new byte[] { 7, 7, 7, 7, 7 }, TestContext.CancellationToken);
        Assert.AreEqual("Lib.dll", await reported.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken));

        File.WriteAllBytes(lib, [5, 5]);
        await Task.Delay(300, TestContext.CancellationToken);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void A_missing_folder_is_not_watched_and_a_disposed_watch_ignores_writes()
    {
        var watch = new OutputWatch(Path.Combine(s_dir.FullName, "missing"), new Dictionary<string, long> { ["Lib.dll"] = 1 }, _ => 2);
        Assert.IsFalse(watch.IsWatching);
        watch.Dispose();
        watch.OnWrite("Lib.dll");
        Assert.IsNull(watch.Check());
    }
}
