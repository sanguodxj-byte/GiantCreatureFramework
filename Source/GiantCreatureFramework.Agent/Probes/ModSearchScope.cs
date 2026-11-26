namespace GiantCreatureFramework.Agent.Probes;

/// <summary>Resolves an explicit search scope without fuzzy discovery expanding it.</summary>
public static class ModSearchScope
{
    public static IReadOnlyList<T> Resolve<T>(IReadOnlyList<T> mods, string? filter, Func<T, string> packageId, Func<T, string> name)
    {
        var value = filter?.Trim() ?? "";
        if (value.Length == 0) return mods;
        var exact = mods.Where(m => string.Equals(packageId(m), value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name(m), value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0) return exact;
        return mods.Where(m => packageId(m).Contains(value, StringComparison.OrdinalIgnoreCase)
            || name(m).Contains(value, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
