namespace SampleTests.Lib;

public static class OrderPricing
{
    /// <summary>Applies a percentage discount (0-100) and rounds to cents.</summary>
    public static decimal ApplyDiscount(decimal total, decimal percent)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total), "Total can't be negative.");
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent), "Discount must be 0-100%.");
        if (percent == 0) return total;
        return Math.Round(total * (100 - percent) / 100, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Validates that a cart has items before checkout.</summary>
    public static void EnsureNotEmpty(IReadOnlyCollection<string> items)
    {
        if (items.Count == 0) throw new InvalidOperationException("Cart is empty");
    }
}
