using SampleTests.Lib;

namespace SampleTests.Snapshots;

/// <summary>
/// Snapshot tests with Verify (https://github.com/VerifyTests/Verify): the approved output lives next to this file as
/// <c>ReceiptSnapshotTests.&lt;Test&gt;.verified.txt</c>; a run that produces something else writes a <c>.received.txt</c>
/// and fails. In Labcoat, the failure shows a snapshot diff with Accept / Reject.
/// </summary>
[TestClass]
[UsesVerify]
public sealed partial class ReceiptSnapshotTests
{
    private static readonly (string Item, int Qty, decimal Price)[] Lines = [("Coffee", 2, 3.50m), ("Croissant", 1, 3.50m)];

    private static string Receipt(decimal discountPercent)
    {
        var total = Lines.Sum(l => l.Qty * l.Price);
        var discounted = OrderPricing.ApplyDiscount(total, discountPercent);
        var rows = Lines.Select(l => $"{l.Qty} x {l.Item,-12}{l.Qty * l.Price,6:0.00}");
        return string.Join("\n", ["Order #42", "---------", .. rows, $"{"Discount",-16}{discounted - total,6:0.00}", $"{"Total",-16}{discounted,6:0.00}"]);
    }

    // Passes: the output matches the committed .verified.txt.
    [TestMethod]
    public Task Receipt_MatchesSnapshot() => Verify(Receipt(10));

    // Fails on purpose: the committed snapshot says the order was "sent", the code now says "queued" (accept it in
    // Labcoat to approve the change, or reject it to keep the snapshot).
    [TestMethod]
    public Task Order_MatchesSnapshot() => Verify(new
    {
        Id = 42,
        Status = "queued",
        Items = Lines.Select(l => new { l.Item, l.Qty }),
        Total = OrderPricing.ApplyDiscount(10.50m, 10),
    });

    // Fails on purpose: a new snapshot (no .verified.txt yet). Accepting it in Labcoat creates the file.
    [TestMethod]
    public Task Invoice_NewSnapshot() => Verify(new { Number = "INV-2026-0042", Customer = "Contoso Coffee", Amount = 9.45m, Paid = false });
}
