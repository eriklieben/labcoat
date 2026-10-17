namespace Labcoat;

/// <summary>
/// "Run in random order": a seeded shuffle of the tests of a project (docs/protocol.md "Test order", research). The
/// same seed gives the same order for the same set of tests, on every machine: the PRNG is SplitMix64 written out here
/// (System.Random's algorithm is not guaranteed to stay the same between .NET versions) and the shuffle starts from the
/// tests sorted by id, so neither the discovery order nor the platform matters.
/// </summary>
public static class RandomOrder
{
    /// <summary>RunSummary.order / the <c>order</c> of a run request.</summary>
    public const string Random = "random";

    /// <summary>Seeds are positive 31-bit numbers: easy to type, to copy from a log and to pass on a command line.</summary>
    public static int NewSeed() => System.Random.Shared.Next(1, int.MaxValue);

    /// <summary>"random" (or null / empty = the test framework's own order); null and an error text for anything else.</summary>
    public static (string? Order, string? Error) ParseOrder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) return (null, null);
        return value.Trim().Equals(Random, StringComparison.OrdinalIgnoreCase)
            ? (Random, null)
            : (null, $"order must be \"random\" (or left out), not \"{value}\".");
    }

    /// <summary>The items of <paramref name="items"/> in the order the seed gives (Fisher-Yates over the id-sorted list).</summary>
    public static List<T> Shuffle<T>(IEnumerable<T> items, int seed, Func<T, string> id)
    {
        var list = items.OrderBy(id, StringComparer.Ordinal).ToList();
        var state = (ulong)(uint)seed;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = (int)(Next(ref state) % (ulong)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>SplitMix64.</summary>
    private static ulong Next(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
