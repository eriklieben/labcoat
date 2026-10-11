using Labcoat;

namespace Labcoat.Tests;

[TestClass]
public class TestUpdateCoalescerTests
{
    private static TestNode Node(string id, Outcome outcome) => new() { Id = id, ProjectId = "P", DisplayName = id, Outcome = outcome };

    [TestMethod]
    public void Merges_per_test_last_write_wins_in_first_seen_order()
    {
        var c = new TestUpdateCoalescer(TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(c.Add([Node("a", Outcome.Running), Node("b", Outcome.Queued)])); // first add schedules a flush
        Assert.IsFalse(c.Add([Node("a", Outcome.Passed), Node("c", Outcome.Running)]));
        Assert.IsFalse(c.Add([Node("b", Outcome.Failed)]));

        var drained = c.Drain();
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, drained.Select(n => n.Id).ToArray());
        CollectionAssert.AreEqual(new[] { Outcome.Passed, Outcome.Failed, Outcome.Running }, drained.Select(n => n.Outcome).ToArray());
        Assert.AreEqual(0, c.Count);
        Assert.IsEmpty(c.Drain());
    }

    [TestMethod]
    public void Same_id_in_different_projects_is_kept_apart()
    {
        var c = new TestUpdateCoalescer(TimeSpan.FromMilliseconds(50));
        c.Add([Node("a", Outcome.Passed), Node("a", Outcome.Failed) with { ProjectId = "Q" }]);
        Assert.AreEqual(2, c.Count);
    }

    [TestMethod]
    public void Clear_drops_pending_and_next_add_schedules_again()
    {
        var c = new TestUpdateCoalescer(TimeSpan.FromMilliseconds(50));
        c.Add([Node("a", Outcome.Passed)]);
        c.Clear();
        Assert.AreEqual(0, c.Count);
        Assert.IsTrue(c.Add([Node("a", Outcome.Failed)]));
    }
}
