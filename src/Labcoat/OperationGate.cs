namespace Labcoat;

/// <summary>
/// The operation lock of the explorer: at most one build / discovery / run / host restart at a time (the others wait), the
/// name of the one in progress, and the cancellation tokens. <see cref="BeginCancel"/> replaces the pair of tokens that
/// new operations start with; the tokens of the operation in progress are cancelled by <see cref="EndCancel"/>. The
/// shutdown token is cancelled once, when the explorer is disposed.
/// </summary>
internal sealed class OperationGate : IDisposable
{
    private readonly object _sync;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _cancel = new();
    /// <summary>_cancel linked with _shutdown, one per _cancel (lock sync): CurrentToken() hands out its token, BeginCancel()
    /// replaces both and EndCancel() disposes the old pair, so no per-call linked source keeps a registration on _shutdown.</summary>
    private CancellationTokenSource _operation;
    private CancellationToken _operationToken;
    private volatile string? _active;
    private volatile bool _cancelling;

    /// <summary>_shutdown.Token, read once: background work started before disposal may still pass it on after the source is
    /// disposed (the token itself stays valid and cancelled; reading Token from a disposed source throws).</summary>
    public CancellationToken ShutdownToken { get; }

    public bool IsShuttingDown => ShutdownToken.IsCancellationRequested;

    /// <summary>Name of the operation holding the lock ("run", "discovery", ...), null when idle.</summary>
    public string? Active => _active;

    /// <summary>Cancel was requested and the operation it stops has not ended yet (SummaryInfo.Cancelling).</summary>
    public bool Cancelling => _cancelling;

    /// <summary>Something holds the lock (or is about to): a new request would have to wait.</summary>
    public bool IsBusy => _active is not null || _lock.CurrentCount == 0;

    /// <param name="sync">The workspace lock: guards the current token and is held by the caller of <see cref="BeginCancel"/>.</param>
    public OperationGate(object sync)
    {
        _sync = sync;
        ShutdownToken = _shutdown.Token;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, ShutdownToken);
        _operationToken = _operation.Token;
    }

    /// <summary>The token an operation started now runs with: cancelled by <see cref="EndCancel"/> and by shutdown.</summary>
    public CancellationToken CurrentToken()
    {
        lock (_sync) return _operationToken;
    }

    /// <summary>Waits for the lock; false when <paramref name="ct"/> was cancelled while waiting.</summary>
    public async Task<bool> AcquireAsync(string name, CancellationToken ct)
    {
        try { await _lock.WaitAsync(ct); }
        catch (OperationCanceledException) { return false; }
        _active = name;
        return true;
    }

    /// <summary>Takes the lock only when it is free right now (and not shutting down); never waits.</summary>
    public async Task<bool> TryAcquireAsync(string name)
    {
        if (IsShuttingDown || !await _lock.WaitAsync(TimeSpan.Zero, CancellationToken.None)) return false;
        _active = name;
        return true;
    }

    public void Release()
    {
        _active = null;
        _cancelling = false;
        _lock.Release();
    }

    /// <summary>What <see cref="BeginCancel"/> replaced: cancel it with <see cref="EndCancel"/> once the caller's lock is released.</summary>
    public readonly record struct Cancellation(CancellationTokenSource Old, CancellationTokenSource OldOperation, string? Active);

    /// <summary>The caller holds <c>sync</c>: later operations get fresh tokens; the operation in progress is marked as cancelling.</summary>
    public Cancellation BeginCancel()
    {
        var result = new Cancellation(_cancel, _operation, _active);
        _cancel = new CancellationTokenSource();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, ShutdownToken);
        _operationToken = _operation.Token;
        if (_active is not null) _cancelling = true;
        return result;
    }

    public static void EndCancel(Cancellation c)
    {
        c.Old.Cancel(); // cancels OldOperation (linked) too
        c.OldOperation.Dispose(); // unregisters it from the shutdown source; tokens already handed out stay cancelled
        c.Old.Dispose();
    }

    public Task CancelShutdownAsync() => _shutdown.CancelAsync();

    /// <summary>
    /// Disposes the shutdown source. The current cancel / operation sources stay undisposed: BeginCancel() may still swap them
    /// after this (a late request), and the linked operation's registration on the shutdown source goes with it.
    /// </summary>
    public void Dispose() => _shutdown.Dispose();
}
