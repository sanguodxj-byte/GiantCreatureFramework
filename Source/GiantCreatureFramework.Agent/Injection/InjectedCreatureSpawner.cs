using RimWorld;
using Verse;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>Places one pawn of a given kind on the current map, the way the framework's own spawn actions do.</summary>
public static class InjectedCreatureSpawner
{
    /// <returns>The spawned pawn, or null when there is no current map, the kind is unknown, or the cell is outside the map.</returns>
    public static Pawn? Spawn(string kindDefName, IntVec3 cell, Faction? faction = null)
    {
        var map = Find.CurrentMap;
        var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindDefName);
        if (map == null || kind == null || !cell.InBounds(map))
        {
            return null;
        }
        var pawn = PawnGenerator.GeneratePawn(kind, faction, map.Tile);
        GenSpawn.Spawn(pawn, cell, map, Rot4.North, WipeMode.Vanish);
        pawn.TryGetComp<CompGiantRig>()?.RebuildFootprintNow(notifyMap: true);
        return pawn;
    }
}
