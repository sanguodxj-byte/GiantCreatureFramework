namespace GiantCreatureFramework.Agent.Probes;

/// <summary>Exact identifiers may not silently resolve to a fuzzy candidate.</summary>
public static class ExactDefinitionLookup
{
    public static T? Resolve<T>(IEnumerable<T> candidates, string query, Func<T, string?> name, Func<T, string?> label) where T : class
    {
        var byName = candidates.Where(c => string.Equals(name(c), query, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (byName.Length == 1) return byName[0];
        if (byName.Length > 1) throw new ArgumentException(AgentProtocol.Get("Probe_DuplicateDefName"));
        var byLabel = candidates.Where(c => string.Equals(label(c), query, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (byLabel.Length == 1) return byLabel[0];
        if (byLabel.Length > 1) throw new ArgumentException(AgentProtocol.Get("Probe_AmbiguousLabel"));
        return null;
    }
}
