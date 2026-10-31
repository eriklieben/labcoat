namespace Labcoat;

/// <summary>Work on several projects at once, at most a given number at a time (maxParallelProjects).</summary>
internal static class BoundedParallel
{
    /// <summary>
    /// Runs <paramref name="work"/> for every item, at most <paramref name="maxParallel"/> at a time, in the order given
    /// (a queued item starts when a running one ends). Returns the items whose work returned true, in the order given.
    /// Cancellation stops the items that have not started; an exception of one item is rethrown once all have ended.
    /// </summary>
    public static async Task<List<T>> ForEachAsync<T>(IReadOnlyList<T> items, int maxParallel, Func<T, Task<bool>> work, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var results = new bool[items.Count];
        if (items.Count == 1 || maxParallel <= 1)
        {
            for (var i = 0; i < items.Count && !ct.IsCancellationRequested; i++) results[i] = await work(items[i]);
            return items.Where((_, i) => results[i]).ToList();
        }
        using var gate = new SemaphoreSlim(maxParallel);
        var tasks = items.Select(async (item, i) =>
        {
            try { await gate.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (!ct.IsCancellationRequested) results[i] = await work(item);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        return items.Where((_, i) => results[i]).ToList();
    }
}
