using System.Diagnostics;
using RimWorld;
using Verse;

namespace GiantCreatureFramework.Agent.Probes;

/// <summary>Cooperative probe budget for read-only game state and log inspection.</summary>
public sealed class ProbeExecution : IDisposable
{
    private static readonly AsyncLocal<ProbeExecution?> Slot = new();
    private readonly ProbeExecution? _previous;
    private readonly CancellationToken _token;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _milliseconds;

    public static CancellationToken CurrentCancellationToken => Slot.Value?._token ?? default;
    public static IEnumerable<object> SelectedObjects => Find.Selector?.SelectedObjects?.ToArray() ?? Array.Empty<object>();
    public static Thing? SelectedThing => SelectedObjects.OfType<Thing>().FirstOrDefault();

    public ProbeExecution(CancellationToken token, bool mainThread, int mainThreadBudgetMs = 5_000, int backgroundBudgetMs = 30_000)
    {
        _previous = Slot.Value;
        _token = token;
        var mainBudget = Math.Clamp(mainThreadBudgetMs, 1_000, 30_000);
        var bgBudget = Math.Clamp(backgroundBudgetMs, 5_000, 120_000);
        _milliseconds = mainThread ? mainBudget : bgBudget;
        Slot.Value = this;
    }

    public static void Check()
    {
        var execution = Slot.Value;
        if (execution == null) return;
        execution._token.ThrowIfCancellationRequested();
        if (execution._clock.ElapsedMilliseconds > execution._milliseconds)
            throw new TimeoutException(AgentProtocol.Get("Probe_BudgetTimeout"));
    }

    public void Dispose() => Slot.Value = _previous;
}

public static class ProbeEnumeration
{
    public static IEnumerable<T> Budgeted<T>(this IEnumerable<T> source)
    {
        foreach (var item in source) { ProbeExecution.Check(); yield return item; }
    }
}
