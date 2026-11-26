using System.Collections.Concurrent;
using System.Diagnostics;

namespace GiantCreatureFramework.Agent;

/// <summary>Bounded main-thread dispatch. Cancellation completes even while the game is loading.</summary>
public static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> PendingActions = new();
    private static int _mainThreadId = -1;
    public static void MarkMainThread() => Interlocked.CompareExchange(ref _mainThreadId, Environment.CurrentManagedThreadId, -1);
    public static bool IsMainThread => Environment.CurrentManagedThreadId == Volatile.Read(ref _mainThreadId);

    public static void Pump()
    {
        MarkMainThread();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 8 && clock.ElapsedMilliseconds < 4 && PendingActions.TryDequeue(out var action); i++) action();
    }

    public static async Task<T> RunOnMainThreadAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMainThread) return func();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        PendingActions.Enqueue(() =>
        {
            if (completion.Task.IsCompleted) return;
            try { cancellationToken.ThrowIfCancellationRequested(); completion.TrySetResult(func()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task.ConfigureAwait(false);
    }
}
