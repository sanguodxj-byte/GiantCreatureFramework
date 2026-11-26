using HarmonyLib;
using Verse;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// Collects game log errors and warnings raised while one injection runs. The Def parser and
/// the cross-reference resolver report failures only through the log, so this is the only way
/// to attribute them to the batch that caused them.
/// </summary>
[HarmonyPatch]
public sealed class ScopedLogCapture : IDisposable
{
    private static readonly object Gate = new();
    private static volatile ScopedLogCapture? _active;
    private readonly int _ownerThreadId;
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    private ScopedLogCapture(int ownerThreadId)
    {
        _ownerThreadId = ownerThreadId;
    }

    public static ScopedLogCapture Begin()
    {
        var capture = new ScopedLogCapture(Environment.CurrentManagedThreadId);
        lock (Gate) { _active = capture; }
        return capture;
    }

    public IReadOnlyList<string> Errors { get { lock (Gate) { return _errors.ToArray(); } } }
    public IReadOnlyList<string> Warnings { get { lock (Gate) { return _warnings.ToArray(); } } }

    public void Dispose()
    {
        lock (Gate) { if (ReferenceEquals(_active, this)) _active = null; }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
    [HarmonyPrefix]
    public static void OnError(string text)
    {
        var active = _active;
        if (active == null)
        {
            return;
        }
        var tid = Environment.CurrentManagedThreadId;
        if (active._ownerThreadId != tid)
        {
            return;
        }
        lock (Gate)
        {
            if (_active != null && _active._ownerThreadId == tid)
            {
                _active._errors.Add(text ?? "");
            }
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string))]
    [HarmonyPrefix]
    public static void OnWarning(string text)
    {
        var active = _active;
        if (active == null)
        {
            return;
        }
        var tid = Environment.CurrentManagedThreadId;
        if (active._ownerThreadId != tid)
        {
            return;
        }
        lock (Gate)
        {
            if (_active != null && _active._ownerThreadId == tid)
            {
                _active._warnings.Add(text ?? "");
            }
        }
    }
}
