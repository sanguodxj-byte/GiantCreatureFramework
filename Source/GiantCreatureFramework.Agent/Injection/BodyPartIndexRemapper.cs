namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// Pure helper for validating BodyDef part-count invariance and computing old-index -> new-part
/// mappings when hot-reloading a BodyDef that already has spawned/world pawns.
/// </summary>
public static class BodyPartIndexRemapper
{
    public static bool TryBuildIndexRemap<TPart>(
        IReadOnlyList<TPart> oldParts,
        IReadOnlyList<TPart> newParts,
        string bodyDefName,
        out TPart[] remappedByOldIndex,
        out string? errorMessage)
    {
        if (oldParts.Count != newParts.Count)
        {
            remappedByOldIndex = Array.Empty<TPart>();
            errorMessage = AgentProtocol.Format("Authoring_BodyPartCountChanged", bodyDefName, oldParts.Count, newParts.Count);
            return false;
        }

        remappedByOldIndex = new TPart[oldParts.Count];
        for (var i = 0; i < oldParts.Count; i++)
        {
            remappedByOldIndex[i] = newParts[i];
        }
        errorMessage = null;
        return true;
    }
}
