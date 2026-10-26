using Labcoat;

namespace Labcoat.Tests;

/// <summary><c>--shard</c> (Sharding.cs): the split must cover every test exactly once and be the same on every shard.</summary>
[TestClass]
public class ShardingTests
{
    public TestContext TestContext { get; set; }

    private static TestNode N(string id, string cls, string project = "P") =>
        new() { Id = id, ProjectId = project, DisplayName = id, Namespace = "Shop", ClassName = cls, MethodName = id };

    private static string Name(string id) => id;

    private static List<TestNode> Suite(int classes, int testsPerClass) =>
        [.. Enumerable.Range(0, classes).SelectMany(c => Enumerable.Range(0, testsPerClass).Select(t => N($"c{c:D2}t{t}", $"Class{c:D2}")))];

    [TestMethod]
    public void Options_parse()
    {
        var o = StartOptions.Parse(["--run", "--shard", "2/4", "--shard-by=test", "--shard-timings", "a.xml,b.xml"], out var error)!;
        Assert.IsNull(error, error);
        Assert.AreEqual(new ShardSpec(2, 4), o.Shard);
        Assert.AreEqual(ShardBy.Test, o.ShardBy);
        CollectionAssert.AreEqual(new[] { "a.xml", "b.xml" }, o.ShardTimings);

        foreach (var bad in new[] { "0/4", "5/4", "2", "a/b", "2/0", "-1/3" })
        {
            Assert.IsNull(StartOptions.Parse(["--run", "--shard", bad], out error), bad);
            Assert.Contains("--shard needs", error!);
        }
        Assert.IsNull(StartOptions.Parse(["--run", "--shard", "1/2", "--shard-by", "x"], out error));
        Assert.Contains("need --run", StartOptions.Parse(["--shard", "1/2"], out error) is null ? error! : "");
        Assert.IsNull(StartOptions.Parse(["--run", "--shard-timings", "a.xml"], out error));
        Assert.Contains("needs --shard", error!);
    }

    [TestMethod]
    public void Shards_cover_every_test_once_without_timings()
    {
        var tests = Suite(7, 3);
        var seen = new List<string>();
        foreach (var i in Enumerable.Range(1, 3))
            seen.AddRange(Sharding.Plan(tests, Name, new ShardSpec(i, 3), ShardBy.Class, null).TestIds);
        CollectionAssert.AreEquivalent(tests.Select(t => t.Id).ToList(), seen);
        Assert.HasCount(tests.Count, seen.Distinct());
    }

    [TestMethod]
    public void A_class_is_never_split_and_the_split_is_stable()
    {
        var tests = Suite(6, 4);
        var a = Sharding.Plan(tests, Name, new ShardSpec(2, 3), ShardBy.Class, null);
        var b = Sharding.Plan([.. tests.AsEnumerable().Reverse()], Name, new ShardSpec(2, 3), ShardBy.Class, null);
        CollectionAssert.AreEquivalent(a.TestIds, b.TestIds);
        Assert.AreEqual(a.Fingerprint, b.Fingerprint);
        Assert.AreEqual(a.Fingerprint, Sharding.Plan(tests, Name, new ShardSpec(1, 3), ShardBy.Class, null).Fingerprint);
        foreach (var cls in a.TestIds.GroupBy(id => id[..3])) Assert.HasCount(4, cls);
    }

    [TestMethod]
    public void More_shards_than_classes_leaves_some_empty()
    {
        var tests = Suite(2, 2);
        Assert.IsEmpty(Sharding.Plan(tests, Name, new ShardSpec(3, 4), ShardBy.Class, null).TestIds);
        Assert.HasCount(2, Sharding.Plan(tests, Name, new ShardSpec(1, 4), ShardBy.Class, null).TestIds);
        Assert.IsEmpty(Sharding.Plan([], Name, new ShardSpec(1, 2), ShardBy.Class, null).TestIds);
    }

    private ShardTimings Timings(params (string Class, double Seconds)[] classes)
    {
        var xml = "<testsuites><testsuite name=\"P\">" +
                  string.Concat(classes.Select(c => $"<testcase classname=\"Shop.{c.Class}\" name=\"x\" time=\"{c.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/>")) +
                  "</testsuite></testsuites>";
        var path = Path.Combine(Path.GetTempPath(), $"labcoat-timings-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        try { return ShardTimings.Load([path]); }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Timings_balance_the_shards()
    {
        // One 10 s class and eight 1 s classes: two shards should end near 9 s each, not 14 / 4 as dealing by name would.
        var tests = Suite(9, 1);
        var timings = Timings(("Class00", 10), ("Class01", 1), ("Class02", 1), ("Class03", 1), ("Class04", 1), ("Class05", 1), ("Class06", 1), ("Class07", 1), ("Class08", 1));
        var one = Sharding.Plan(tests, Name, new ShardSpec(1, 2), ShardBy.Class, timings);
        var two = Sharding.Plan(tests, Name, new ShardSpec(2, 2), ShardBy.Class, timings);
        Assert.AreEqual(one.Fingerprint, two.Fingerprint);
        Assert.AreEqual(10_000, Math.Max(one.EstimatedMs!.Value, two.EstimatedMs!.Value));
        Assert.AreEqual(8_000, Math.Min(one.EstimatedMs.Value, two.EstimatedMs.Value));
        Assert.HasCount(9, one.TestIds.Concat(two.TestIds).Distinct());
    }

    [TestMethod]
    public void An_unknown_class_counts_as_the_median()
    {
        var tests = Suite(3, 1);
        var plan = Sharding.Plan(tests, Name, new ShardSpec(1, 1), ShardBy.Class, Timings(("Class00", 1), ("Class01", 3)));
        Assert.AreEqual(1000 + 3000 + 3000, plan.EstimatedMs); // the median of 1 and 3 is the upper middle: 3
    }

    [TestMethod]
    public void Sharding_by_project_keeps_a_project_together()
    {
        var tests = new List<TestNode> { N("a1", "A", "p1"), N("a2", "B", "p1"), N("b1", "A", "p2") };
        var first = Sharding.Plan(tests, Name, new ShardSpec(1, 2), ShardBy.Project, null);
        var second = Sharding.Plan(tests, Name, new ShardSpec(2, 2), ShardBy.Project, null);
        CollectionAssert.AreEquivalent(new[] { "a1", "a2" }, first.TestIds);
        CollectionAssert.AreEquivalent(new[] { "b1" }, second.TestIds);
    }

    [TestMethod]
    public void Timings_resolve_files_and_patterns()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-timings").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.xml"), "<testsuites/>");
            File.WriteAllText(Path.Combine(dir, "b.xml"), "<testsuites/>");
            Assert.HasCount(2, ShardTimings.Resolve([Path.Combine(dir, "*.xml")]));
            Assert.IsEmpty(ShardTimings.Resolve([Path.Combine(dir, "*.trx")]));
            Assert.ThrowsExactly<FileNotFoundException>(() => ShardTimings.Resolve([Path.Combine(dir, "missing.xml")]));
            File.WriteAllText(Path.Combine(dir, "bad.xml"), "not xml");
            Assert.ThrowsExactly<InvalidDataException>(() => ShardTimings.Load([Path.Combine(dir, "bad.xml")]));
        }
        finally { Directory.Delete(dir, true); }
    }
}
