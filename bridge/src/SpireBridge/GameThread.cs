using System.Threading;

namespace SpireBridge;

/// <summary>
/// Marshals work onto the game (main) thread.
///
/// The game's SynchronizationContext is captured in ModEntry.Initialize(),
/// which the mod loader calls on the game thread. HTTP handler threads never
/// touch game APIs directly: they Post the delegate to the captured context
/// and wait (bounded) for completion. If no context was captured (e.g. an
/// out-of-game harness) or the wait times out, the call reports failure
/// instead of throwing — callers must then return an honest fallback and
/// must NOT fall back to touching game APIs on the worker thread.
/// </summary>
public static class GameThread
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static SynchronizationContext? _context;

    public static bool IsCaptured => _context != null;

    /// <summary>Must be called on the game thread (ModEntry.Initialize).</summary>
    public static void Capture()
    {
        try
        {
            _context = SynchronizationContext.Current;
        }
        catch
        {
            _context = null;
        }

        BridgeLog.Info(_context != null
            ? "[SpireBridge] GameThread: SynchronizationContext captured"
            : "[SpireBridge] GameThread: WARNING no SynchronizationContext on init thread");
    }

    public sealed class Result<T>
    {
        public bool Success { get; init; }
        public T? Value { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the captured game thread and waits up
    /// to 5 seconds. Timeout / missing context / exception all come back as
    /// Success=false (never thrown to the caller).
    /// </summary>
    public static Result<T> Invoke<T>(Func<T> func)
    {
        var ctx = _context;
        if (ctx == null)
            return new Result<T> { Success = false, Error = "game_thread_unavailable" };

        // Already on the game thread: run inline (Posting to ourselves and
        // waiting would deadlock).
        if (SynchronizationContext.Current == ctx)
        {
            try { return new Result<T> { Success = true, Value = func() }; }
            catch (Exception ex) { return new Result<T> { Success = false, Error = ex.Message }; }
        }

        T? value = default;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        try
        {
            ctx.Post(_ =>
            {
                try { value = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            }, null);
        }
        catch (Exception ex)
        {
            return new Result<T> { Success = false, Error = ex.Message };
        }

        if (!done.Wait(Timeout))
            return new Result<T> { Success = false, Error = "game_thread_timeout" };
        if (error != null)
            return new Result<T> { Success = false, Error = error.Message };
        return new Result<T> { Success = true, Value = value };
    }

    /// <summary>Action convenience overload of <see cref="Invoke{T}"/>.</summary>
    public static Result<bool> Invoke(Action action) =>
        Invoke<bool>(() => { action(); return true; });
}
