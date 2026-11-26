using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public static class DebugActions_GiantCreature
    {
        public static void ClearAllStaticDebugRefs()
        {
        }

        public static void RecallAllCarrierInterceptorsDebug()
        {
            Map map = Find.CurrentMap;
            if (map?.mapPawns?.AllPawnsSpawned == null)
            {
                return;
            }

            int totalRecalled = 0;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                CompCarrierHangar hangar = pawns[i]?.TryGetComp<CompCarrierHangar>();
                if (hangar != null)
                {
                    pawns[i].jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    totalRecalled += hangar.RecallAllInterceptors(bonusShieldRestore: 35f, holdAutoLaunch: true);
                }
            }
            Log.Message($"[GCF_CARRIER_RECALL_TRIGGERED] recalled={totalRecalled}");
        }

        public static void SpawnGiantKindAtMouse(PawnKindDef kind, bool usePlayerFaction)
        {
            if (kind == null)
            {
                return;
            }

            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }

            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                return;
            }

            ThingDef race = kind.race;
            CompProperties_GiantRig rigProps = race?.GetCompProperties<CompProperties_GiantRig>();
            GiantAquaticDef aqDef = rigProps?.rigDef?.ActiveAquaticLocomotion;
            bool hasFlight = rigProps?.rigDef?.ActiveFlightLocomotion?.IsActive == true;
            if (aqDef != null && aqDef.IsWaterOnly && !hasFlight && !GiantAquaticUtility.IsCellPassableForAquatic(map, cell, null, null, aqDef))
            {
                if (!GiantAquaticUtility.TryFindValidAquaticSpawnCell(map, rigProps.rigDef, cell, out cell))
                {
                    string noWaterMsg = GiantFlightUtility.SafeTranslate(
                        "GCF_AquaticFail_NoWaterOnMap",
                        "No navigable water on this map for water-only unit.");
                    Messages.Message(noWaterMsg, MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
            }
            CompProperties_GiantCockpit cockpitProps = race?.GetCompProperties<CompProperties_GiantCockpit>();
            bool isVehicleCockpit = race != null && (race.HasComp(typeof(CompGiantCockpit)) || (cockpitProps != null && cockpitProps.treatHostAsVehicle));
            bool isMechOrVehicleOrNonAnimal =
                (race?.race != null && race.race.IsMechanoid)
                || kind.defaultFactionDef == FactionDefOf.Mechanoid
                || isVehicleCockpit
                || race?.race == null
                || !race.race.Animal;

            if (isMechOrVehicleOrNonAnimal)
            {
                Faction faction;
                if (usePlayerFaction)
                {
                    faction = Faction.OfPlayer;
                }
                else
                {
                    Faction defFac = kind.defaultFactionDef != null ? FactionUtility.DefaultFactionFrom(kind.defaultFactionDef) : null;
                    if (defFac != null && !defFac.IsPlayer && (Faction.OfPlayerSilentFail == null || defFac.HostileTo(Faction.OfPlayerSilentFail)))
                    {
                        faction = defFac;
                    }
                    else
                    {
                        faction = Faction.OfMechanoids
                            ?? Find.FactionManager?.FirstFactionOfDef(FactionDefOf.Pirate)
                            ?? Find.FactionManager?.FirstFactionOfDef(FactionDefOf.AncientsHostile);
                    }
                }

                Pawn pawn = PawnGenerator.GeneratePawn(kind, faction, map.Tile);
                GenSpawn.Spawn(pawn, cell, map, Rot4.North, WipeMode.Vanish);
                pawn.Rotation = Rot4.North;

                CompGiantRig comp = pawn.TryGetComp<CompGiantRig>();
                comp?.RebuildFootprintNow(notifyMap: true);
                Log.Message($"[GCF_DEBUG_SPAWN] kind={kind.defName} id={pawn.thingIDNumber} label={pawn.LabelCap} faction={faction?.def?.defName ?? "null"} pos={pawn.Position} hitCells={comp?.Footprint?.hitCells.Count ?? 0} collisionCells={comp?.Footprint?.collisionCells.Count ?? 0}");
                return;
            }

            Faction bioFaction = usePlayerFaction ? Faction.OfPlayer : null;
            Pawn bioPawn = PawnGenerator.GeneratePawn(kind, bioFaction, map.Tile);
            GenSpawn.Spawn(bioPawn, cell, map, WipeMode.Vanish);
            if (!usePlayerFaction && bioPawn.mindState?.mentalStateHandler != null && MentalStateDefOf.ManhunterPermanent != null)
            {
                bioPawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true, forceWake: true);
            }

            CompGiantRig bioComp = bioPawn.TryGetComp<CompGiantRig>();
            bioComp?.RebuildFootprintNow(notifyMap: true);
            Log.Message($"[GCF_DEBUG_SPAWN] kind={kind.defName} id={bioPawn.thingIDNumber} label={bioPawn.LabelCap} faction={bioFaction?.def?.defName ?? "wildlife"} pos={bioPawn.Position} hitCells={bioComp?.Footprint?.hitCells.Count ?? 0} collisionCells={bioComp?.Footprint?.collisionCells.Count ?? 0}");
        }

        public static void SpawnMechScorpion(bool usePlayerFaction)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }

            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed("GCF_MechScorpion", errorOnFail: false);
            if (kind == null)
            {
                Log.Error("[GiantCreatureFramework] PawnKindDef GCF_MechScorpion not found.");
                return;
            }

            Faction faction = usePlayerFaction
                ? Faction.OfPlayer
                : (Faction.OfMechanoids ?? FactionUtility.DefaultFactionFrom(kind.defaultFactionDef));

            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                return;
            }

            Pawn pawn = PawnGenerator.GeneratePawn(kind, faction, map.Tile);
            GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);

            CompGiantRig comp = pawn.TryGetComp<CompGiantRig>();
            comp?.RebuildFootprintNow(notifyMap: true);

            StringBuilder sb = new StringBuilder();
            if (comp != null)
            {
                BoneNodeDef[] nodes = comp.RigDef?.SortedNodes;
                var seenBones = new HashSet<int>();
                for (int i = 0; i < comp.Footprint.hitCells.Count; i++)
                {
                    HitCellEntry entry = comp.Footprint.hitCells[i];
                    if (seenBones.Add(entry.dominantBoneIndex))
                    {
                        string boneId = (nodes != null && entry.dominantBoneIndex >= 0 && entry.dominantBoneIndex < nodes.Length)
                            ? nodes[entry.dominantBoneIndex].nodeId
                            : entry.dominantBoneIndex.ToString();
                        sb.Append($"{boneId}@{entry.cell};");
                    }
                }
            }

            Log.Message($"[GCF_DEBUG_SPAWN] id={pawn.thingIDNumber} label={pawn.LabelCap} faction={faction?.def?.defName} pos={pawn.Position} hitCells={comp?.Footprint?.hitCells.Count ?? 0} collisionCells={comp?.Footprint?.collisionCells.Count ?? 0} tightBounds={comp?.Footprint?.tightHitBounds} boneSamples=[{sb}]");
        }

        public static void LogHitCellAimingInfo()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }

            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                return;
            }

            GiantFootprintMapComponent mapComp = map.GetComponent<GiantFootprintMapComponent>();
            List<CompGiantRig> giantsAtCell = mapComp?.GetHitGiantsAt(cell);
            int hitCount = giantsAtCell?.Count ?? 0;

            Pawn firstGiant = hitCount > 0 ? giantsAtCell[0].Pawn : null;
            string boneLabel = "none";
            if (firstGiant != null && giantsAtCell[0].Footprint.hitEntryByCell.TryGetValue(cell, out HitCellEntry entry))
            {
                BoneNodeDef[] nodes = giantsAtCell[0].RigDef?.SortedNodes;
                string boneId = (nodes != null && entry.dominantBoneIndex >= 0 && entry.dominantBoneIndex < nodes.Length)
                    ? nodes[entry.dominantBoneIndex].nodeId
                    : entry.dominantBoneIndex.ToString();
                boneLabel = $"{entry.dominantBoneIndex}({boneId}) cov={entry.hitCoverage:F2}";
            }

            List<Thing> underMouse = GenUI.ThingsUnderMouse(cell.ToVector3Shifted(), 1f, TargetingParameters.ForAttackAny());
            string topThing = underMouse.Count > 0 ? $"{underMouse[0].def?.defName}#{underMouse[0].thingIDNumber}" : "none";

            string shooterReport = "noSelectedShooter";
            Pawn selectedShooter = Find.Selector.SingleSelectedThing as Pawn;
            if (selectedShooter != null && firstGiant != null)
            {
                Verb verb = selectedShooter.CurrentEffectiveVerb;
                if (verb != null)
                {
                    GiantTargetAimTracker.PushHoverQuery(cell);
                    try
                    {
                        bool canHit = verb.CanHitTarget(firstGiant);
                        bool tryLine = verb.TryFindShootLineFromTo(selectedShooter.Position, firstGiant, out ShootLine line);
                        ShotReport sr = ShotReport.HitReportFor(selectedShooter, verb, firstGiant);
                        shooterReport = $"shooter={selectedShooter.LabelShort}#{selectedShooter.thingIDNumber} verb={verb.GetType().Name} canHit={canHit} tryLine={tryLine} line={line.Source}->{line.Dest} totalHitChance={sr.TotalEstimatedHitChance:F3} passCover={sr.PassCoverChance:F2} aimDist={sr.AimOnTargetChance_StandardTarget:F3}";
                    }
                    finally
                    {
                        GiantTargetAimTracker.PopHoverQuery();
                    }
                }
            }

            Log.Message($"[GCF_DEBUG_CELL] cell={cell} hitGiants={hitCount} bone={boneLabel} topUnderMouse={topThing} {shooterReport}");
        }

        public static void DestroyBoneAtMouse()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }

            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                return;
            }

            GiantFootprintMapComponent mapComp = map.GetComponent<GiantFootprintMapComponent>();
            List<CompGiantRig> giantsAtCell = mapComp?.GetHitGiantsAt(cell);
            if (giantsAtCell == null || giantsAtCell.Count == 0)
            {
                Log.Message($"[GCF_DEBUG_BONE] cell={cell} no giant hitCell.");
                return;
            }

            CompGiantRig comp = giantsAtCell[0];
            if (!comp.Footprint.hitEntryByCell.TryGetValue(cell, out HitCellEntry entry))
            {
                return;
            }

            BodyPartRecord part = comp.GetBodyPartForBone(entry.dominantBoneIndex);
            if (part == null)
            {
                Log.Message($"[GCF_DEBUG_BONE] cell={cell} bone={entry.dominantBoneIndex} has no intact linked BodyPart.");
                return;
            }

            float partHealth = comp.Pawn?.health?.hediffSet != null
                ? comp.Pawn.health.hediffSet.GetPartHealth(part)
                : 0f;
            float dmgAmount = partHealth > 0f ? Mathf.Ceil(partHealth) + 1f : 9999f;
            DamageInfo dinfo = new DamageInfo(DamageDefOf.Cut, dmgAmount, 2f, -1f, null, part);
            comp.Pawn.TakeDamage(dinfo);
            comp.RebuildFootprintNow(notifyMap: true);
            Log.Message($"[GCF_DEBUG_BONE] destroyed part={part.LabelCap} bone={entry.dominantBoneIndex} at cell={cell} remainingHitCells={comp.Footprint.hitCells.Count}");
        }

        public static void StartLiveFpsTelemetryWindow()
        {
            Prefs.AutomaticPauseMode = AutomaticPauseMode.Never;
            if (Find.WindowStack?.Windows != null)
            {
                var toClose = new List<Window>();
                IList<Window> wins = Find.WindowStack.Windows;
                for (int i = 0; i < wins.Count; i++)
                {
                    Window w = wins[i];
                    if (w != null && !(w is ImmediateWindow) && (w.forcePause || w.absorbInputAroundWindow || w is EditWindow_Log))
                    {
                        toClose.Add(w);
                    }
                }
                for (int i = 0; i < toClose.Count; i++)
                {
                    toClose[i].Close(doCloseSound: false);
                }
            }
            GiantFootprintMapComponent.StartLiveFpsTelemetry();
        }

        public static void StopAndLogLiveFpsTelemetryWindow()
        {
            string json = GiantFootprintMapComponent.StopLiveFpsTelemetry(Find.CurrentMap);
            Log.Message($"[GCF_FPS_TELEMETRY] {json}");
        }
    }
}
