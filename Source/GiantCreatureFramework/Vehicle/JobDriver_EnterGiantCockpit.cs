using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    [DefOf]
    public static class GCF_CockpitJobDefOf
    {
        public static JobDef GCF_EnterGiantCockpit;

        static GCF_CockpitJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(GCF_CockpitJobDefOf));
        }
    }

    /// <summary>
    /// Hull-perimeter reachability and interaction cell helpers for multi-cell Giant Creatures
    /// whose solid CollisionCells surround their center Position cell.
    /// </summary>
    public static class GiantHullInteractionUtility
    {
        [ThreadStatic]
        private static HashSet<int> candidateIndicesScratch;

        [ThreadStatic]
        private static List<IntVec3> candidateCellsScratch;

        public static bool IsGiantTouchCompatiblePathEndMode(PathEndMode peMode)
        {
            return peMode == PathEndMode.Touch
                || peMode == PathEndMode.ClosestTouch
                || peMode == PathEndMode.InteractionCell
                || peMode == PathEndMode.OnCell;
        }

        public static CompGiantRig TryGetInteractableGiantRig(Thing thing)
        {
            if (thing == null || !thing.Spawned)
            {
                return null;
            }
            return GiantFastLookup.TryGetRig(thing as Pawn)
                ?? HarmonyPatches_CombatAndHitbox.TryGetEnabledCorpseRig(thing);
        }

        public static bool IsCellTouchingGiantHitbox(IntVec3 cell, Thing giantOrCorpse)
        {
            if (giantOrCorpse == null || !giantOrCorpse.Spawned || !cell.IsValid)
            {
                return false;
            }
            if (cell.AdjacentTo8WayOrInside(giantOrCorpse.Position))
            {
                return true;
            }

            CompGiantRig rig = TryGetInteractableGiantRig(giantOrCorpse);
            if (rig == null)
            {
                return false;
            }

            if (HarmonyPatches_CombatAndHitbox.IsCellTouchingFootprint(cell, rig.Footprint))
            {
                return true;
            }

            List<IntVec3> collisionCells = rig.Footprint.collisionCells;
            if (collisionCells != null && collisionCells.Count > 0)
            {
                for (int i = 0; i < collisionCells.Count; i++)
                {
                    IntVec3 cc = collisionCells[i];
                    if (Math.Abs(cell.x - cc.x) <= 1 && Math.Abs(cell.z - cc.z) <= 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsAnyHitCellInAllowedArea(Thing giantOrCorpse, Pawn actor)
        {
            if (giantOrCorpse == null || !giantOrCorpse.Spawned || actor == null)
            {
                return false;
            }
            if (giantOrCorpse.Position.InAllowedArea(actor))
            {
                return true;
            }
            CompGiantRig rig = TryGetInteractableGiantRig(giantOrCorpse);
            if (rig == null)
            {
                return false;
            }
            List<IntVec3> hitCells = rig.Footprint.hitCellPositions;
            if (hitCells != null)
            {
                for (int i = 0; i < hitCells.Count; i++)
                {
                    if (hitCells[i].InAllowedArea(actor))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool IsLiveStandingGroundedGiant(Thing giantOrCorpse, CompGiantRig rig)
        {
            return rig != null
                && giantOrCorpse is Pawn gp
                && !gp.Dead
                && !gp.Downed
                && !rig.IsCurrentlyAirborne
                && !rig.IsFlyingEntry
                && rig.Footprint.pawnExclusionCellSet.Count > 0;
        }

        public static bool HasAnyWalkableCanopySafeTouchCell(Map map, Pawn smallActor, CompGiantRig rig)
        {
            if (map == null || rig == null)
            {
                return false;
            }
            GiantFootprintData fp = rig.Footprint;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            IntVec3 mapSize = map.Size;
            List<IntVec3> hits = fp.hitCellPositions;
            for (int i = 0; i < hits.Count; i++)
            {
                IntVec3 hc = hits[i];
                if (!fp.collisionCellSet.Contains(hc) && !fp.pawnExclusionCellSet.Contains(hc)
                    && (uint)hc.x < (uint)mapSize.x && (uint)hc.z < (uint)mapSize.z
                    && (smallActor != null ? hc.WalkableBy(map, smallActor) : hc.Walkable(map))
                    && (mapComp == null || mapComp.GetBlockingGiantForSmallPawnAt(hc, smallActor) == null))
                {
                    return true;
                }
                for (int d = 0; d < 8; d++)
                {
                    IntVec3 adj = hc + GenAdj.AdjacentCells[d];
                    if ((uint)adj.x >= (uint)mapSize.x || (uint)adj.z >= (uint)mapSize.z)
                    {
                        continue;
                    }
                    if (fp.collisionCellSet.Contains(adj) || fp.pawnExclusionCellSet.Contains(adj))
                    {
                        continue;
                    }
                    if (!(smallActor != null ? adj.WalkableBy(map, smallActor) : adj.Walkable(map)))
                    {
                        continue;
                    }
                    if (mapComp != null && mapComp.GetBlockingGiantForSmallPawnAt(adj, smallActor) != null)
                    {
                        continue;
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool IsCellValidSmallPawnTouch(Map map, Pawn smallActor, Thing giantOrCorpse, IntVec3 cell)
        {
            if (!IsCellTouchingGiantHitbox(cell, giantOrCorpse))
            {
                return false;
            }
            CompGiantRig rig = TryGetInteractableGiantRig(giantOrCorpse);
            if (!IsLiveStandingGroundedGiant(giantOrCorpse, rig))
            {
                return true;
            }
            if (smallActor != null && (GiantFastLookup.TryGetRig(smallActor) != null || GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(smallActor)))
            {
                return true;
            }
            if (rig.Footprint.collisionCellSet.Contains(cell))
            {
                return false;
            }
            if (!rig.Footprint.pawnExclusionCellSet.Contains(cell))
            {
                return true;
            }
            return !HasAnyWalkableCanopySafeTouchCell(map, smallActor, rig);
        }

        public static bool IsCellTouchingGiantHull(IntVec3 cell, Pawn giant)
            => IsCellTouchingGiantHitbox(cell, giant);

        public static bool IsPawnTouchingGiantHull(Pawn actor, Pawn giant)
            => IsPawnTouchingGiantHitbox(actor, giant);

        public static bool IsPawnTouchingGiantHitbox(Pawn actor, Thing giantOrCorpse)
        {
            return actor != null
                && giantOrCorpse != null
                && actor.Spawned
                && giantOrCorpse.Spawned
                && actor.Map != null
                && actor.Map == giantOrCorpse.Map
                && IsCellValidSmallPawnTouch(actor.Map, actor, giantOrCorpse, actor.Position);
        }

        public static bool TryFindBestHullInteractionCell(Pawn actor, Pawn giant, out IntVec3 bestCell)
            => TryFindBestHullInteractionCell(actor, (Thing)giant, out bestCell);

        public static bool TryFindBestHullInteractionCell(Pawn actor, Thing giantOrCorpse, out IntVec3 bestCell)
        {
            bestCell = IntVec3.Invalid;
            if (actor == null || giantOrCorpse == null || !actor.Spawned || !giantOrCorpse.Spawned || actor.Map == null || actor.Map != giantOrCorpse.Map)
            {
                return false;
            }

            Map map = giantOrCorpse.Map;
            CompGiantRig rig = TryGetInteractableGiantRig(giantOrCorpse);
            bool isLiveGroundedGiant = IsLiveStandingGroundedGiant(giantOrCorpse, rig);
            bool actorIgnoresCollision = GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(actor);
            bool isLiveStandingAny = giantOrCorpse is Pawn gp && !gp.Dead && !gp.Downed;
            HashSet<IntVec3> blockedCollision = (isLiveStandingAny && !actorIgnoresCollision) ? rig?.Footprint.collisionCellSet : null;
            HashSet<IntVec3> blockedCanopy = (isLiveGroundedGiant && !actorIgnoresCollision) ? rig?.Footprint.pawnExclusionCellSet : null;

            if (IsCellValidSmallPawnTouch(map, actor, giantOrCorpse, actor.Position)
                && (blockedCollision == null || !blockedCollision.Contains(actor.Position))
                && (blockedCanopy == null || !blockedCanopy.Contains(actor.Position))
                && actor.Position.StandableBy(map, actor))
            {
                bestCell = actor.Position;
                return true;
            }

            candidateIndicesScratch ??= new HashSet<int>(256);
            candidateCellsScratch ??= new List<IntVec3>(256);
            CellIndices indices = map.cellIndices;
            IntVec3 mapSize = map.Size;

            bool CollectPass(bool allowCanopyFallback)
            {
                candidateIndicesScratch.Clear();
                candidateCellsScratch.Clear();

                void TryAddCandidate(IntVec3 cand)
                {
                    if ((uint)cand.x >= (uint)mapSize.x || (uint)cand.z >= (uint)mapSize.z)
                    {
                        return;
                    }
                    int idx = indices.CellToIndex(cand);
                    if (!candidateIndicesScratch.Add(idx))
                    {
                        return;
                    }
                    if (blockedCollision != null && blockedCollision.Contains(cand))
                    {
                        return;
                    }
                    if (!allowCanopyFallback && blockedCanopy != null && blockedCanopy.Contains(cand))
                    {
                        return;
                    }
                    bool passable = allowCanopyFallback ? cand.WalkableBy(map, actor) : cand.StandableBy(map, actor);
                    if (!passable)
                    {
                        return;
                    }
                    candidateCellsScratch.Add(cand);
                }

                void CollectAround(IntVec3 center)
                {
                    TryAddCandidate(center);
                    for (int d = 0; d < 8; d++)
                    {
                        TryAddCandidate(center + GenAdj.AdjacentCells[d]);
                    }
                }

                bool collectedAny = false;
                List<IntVec3> hitCells = rig?.Footprint.hitCellPositions;
                if (hitCells != null && hitCells.Count > 0)
                {
                    collectedAny = true;
                    for (int i = 0; i < hitCells.Count; i++)
                    {
                        CollectAround(hitCells[i]);
                    }
                }
                List<IntVec3> collisionCells = rig?.Footprint.collisionCells;
                if (collisionCells != null && collisionCells.Count > 0)
                {
                    collectedAny = true;
                    for (int i = 0; i < collisionCells.Count; i++)
                    {
                        CollectAround(collisionCells[i]);
                    }
                }
                if (!collectedAny)
                {
                    CollectAround(giantOrCorpse.Position);
                }
                return candidateCellsScratch.Count > 0;
            }

            int GetCandidateTier(IntVec3 c)
            {
                if (!isLiveGroundedGiant || actorIgnoresCollision || rig == null)
                {
                    return 0;
                }
                if (rig.Footprint.IsCellStrictExteriorTouch(c))
                {
                    return 0;
                }
                if (rig.Footprint.IsCellCanopySafeTouch(c))
                {
                    return 1;
                }
                return 2;
            }

            IntVec3 actorPos = actor.Position;
            IntVec3 giantPos = giantOrCorpse.Position;
            for (int pass = 0; pass < 2; pass++)
            {
                bool allowFallback = pass == 1;
                if (allowFallback && blockedCanopy == null)
                {
                    break;
                }
                if (!CollectPass(allowFallback))
                {
                    continue;
                }

                candidateCellsScratch.Sort((a, b) =>
                {
                    int ta = GetCandidateTier(a);
                    int tb = GetCandidateTier(b);
                    if (ta != tb)
                    {
                        return ta.CompareTo(tb);
                    }
                    int da = (a - actorPos).LengthHorizontalSquared;
                    int db = (b - actorPos).LengthHorizontalSquared;
                    if (da != db)
                    {
                        return da.CompareTo(db);
                    }
                    return (a - giantPos).LengthHorizontalSquared.CompareTo((b - giantPos).LengthHorizontalSquared);
                });

                for (int i = 0; i < candidateCellsScratch.Count; i++)
                {
                    IntVec3 cand = candidateCellsScratch[i];
                    if (actor.CanReach(cand, PathEndMode.OnCell, Danger.Deadly))
                    {
                        bestCell = cand;
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool CanPawnReachGiantHull(Pawn actor, Pawn giant)
            => CanPawnReachGiantHitbox(actor, giant);

        public static bool CanPawnReachGiantHitbox(Pawn actor, Thing giantOrCorpse)
        {
            if (actor == null || giantOrCorpse == null || !actor.Spawned || !giantOrCorpse.Spawned || actor.Map == null || actor.Map != giantOrCorpse.Map)
            {
                return false;
            }
            if (IsPawnTouchingGiantHitbox(actor, giantOrCorpse))
            {
                return true;
            }
            if (TryGetInteractableGiantRig(giantOrCorpse) == null)
            {
                return actor.CanReach(giantOrCorpse, PathEndMode.Touch, Danger.Deadly);
            }
            return TryFindBestHullInteractionCell(actor, giantOrCorpse, out _);
        }
    }

    public class JobDriver_EnterGiantCockpit : JobDriver
    {
        private Pawn HostPawn => job.targetA.Thing as Pawn;
        private CompGiantCockpit Cockpit => GiantFastLookup.TryGetCockpit(HostPawn);

        // job.count encodes (preferredSeatIndex + 1); 0 means auto-select best open role seat.
        private int PreferredSeatIndex => job.count > 0 ? job.count - 1 : -1;

        public static bool TryFindReachablePerimeterStandCell(Pawn actor, Pawn giant, out IntVec3 standCell)
            => GiantHullInteractionUtility.TryFindBestHullInteractionCell(actor, giant, out standCell);

        public static bool TryGiveBoardJob(Pawn colonist, Pawn hostGiant, int preferredSeatIndex = -1)
        {
            if (colonist?.jobs == null || hostGiant == null || GCF_CockpitJobDefOf.GCF_EnterGiantCockpit == null)
            {
                return false;
            }

            Job job = JobMaker.MakeJob(GCF_CockpitJobDefOf.GCF_EnterGiantCockpit, hostGiant);
            job.count = preferredSeatIndex >= 0 ? preferredSeatIndex + 1 : 0;
            return colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // Do not exclusively reserve the multi-seat vehicle Pawn so multiple crew members can board concurrently.
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() =>
            {
                Pawn host = HostPawn;
                CompGiantCockpit cockpit = Cockpit;
                if (host == null || host.Dead || host.Downed || cockpit == null)
                {
                    return true;
                }
                return !cockpit.CanPawnBoard(pawn, out _, PreferredSeatIndex)
                    && (PreferredSeatIndex < 0 || !cockpit.CanPawnBoard(pawn, out _, -1));
            });

            Toil approachHull = ToilMaker.MakeToil("GotoGiantCockpitHull");
            approachHull.initAction = delegate
            {
                Pawn host = HostPawn;
                if (host == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (GiantHullInteractionUtility.IsPawnTouchingGiantHull(pawn, host))
                {
                    pawn.pather?.StopDead();
                    ReadyForNextToil();
                    return;
                }
                if (GiantHullInteractionUtility.TryFindBestHullInteractionCell(pawn, host, out IntVec3 hullCell))
                {
                    pawn.pather?.StartPath(hullCell, PathEndMode.OnCell);
                }
                else
                {
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            approachHull.tickAction = delegate
            {
                Pawn host = HostPawn;
                if (host == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (GiantHullInteractionUtility.IsPawnTouchingGiantHull(pawn, host))
                {
                    pawn.pather?.StopDead();
                    ReadyForNextToil();
                    return;
                }
                if (pawn.pather == null || !pawn.pather.Moving ||
                    (pawn.IsHashIntervalTick(20) && !GiantHullInteractionUtility.IsCellTouchingGiantHull(pawn.pather.Destination.Cell, host)))
                {
                    if (GiantHullInteractionUtility.TryFindBestHullInteractionCell(pawn, host, out IntVec3 hullCell))
                    {
                        pawn.pather?.StartPath(hullCell, PathEndMode.OnCell);
                    }
                    else
                    {
                        EndJobWith(JobCondition.Incompletable);
                    }
                }
            };
            approachHull.defaultCompleteMode = ToilCompleteMode.Never;
            yield return approachHull;

            int waitTicks = Cockpit?.Props?.enterTicks ?? 55;
            Toil enterWait = Toils_General.Wait(waitTicks, TargetIndex.A);
            enterWait.tickAction = delegate
            {
                Pawn host = HostPawn;
                if (host == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                pawn.rotationTracker?.FaceCell(host.Position);
                if (!GiantHullInteractionUtility.IsPawnTouchingGiantHull(pawn, host))
                {
                    JumpToToil(approachHull);
                }
            };
            enterWait.WithProgressBarToilDelay(TargetIndex.A);
            yield return enterWait;

            Toil boardToil = ToilMaker.MakeToil("EnterGiantCockpit");
            boardToil.initAction = delegate
            {
                CompGiantCockpit cockpit = Cockpit;
                if (cockpit != null)
                {
                    cockpit.TryAcceptPawn(pawn, PreferredSeatIndex);
                }
            };
            boardToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return boardToil;
        }
    }

    /// <summary>
    /// Auto-discovered RimWorld 1.7 FloatMenuOptionProvider allowing drafted or undrafted player colonists
    /// (single or multi-selected) to right-click any HitCell of an allied Cockpit-equipped Giant/Vehicle
    /// to take cockpit control, board role seats, or toggle remote draft/autonomous control.
    /// </summary>
    public class FloatMenuOptionProvider_EnterGiantCockpit : FloatMenuOptionProvider
    {
        private static readonly GiantCockpitRole[] RoleDisplayOrder =
        {
            GiantCockpitRole.PrimaryPilot,
            GiantCockpitRole.Commander,
            GiantCockpitRole.Gunner,
            GiantCockpitRole.Passenger
        };

        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => true;
        protected override bool MechanoidCanDo => false;

        public override bool SelectedPawnValid(Pawn pawn, FloatMenuContext context)
        {
            if (!base.SelectedPawnValid(pawn, context))
            {
                return false;
            }
            return pawn != null
                && pawn.Faction == Faction.OfPlayerSilentFail
                && pawn.RaceProps != null
                && pawn.RaceProps.Humanlike
                && GiantFastLookup.TryGetRig(pawn) == null;
        }

        public override bool TargetPawnValid(Pawn clickedPawn, FloatMenuContext context)
        {
            if (!base.TargetPawnValid(clickedPawn, context))
            {
                return false;
            }
            return clickedPawn != null
                && clickedPawn.Spawned
                && !clickedPawn.Dead
                && clickedPawn.Faction == Faction.OfPlayerSilentFail
                && !GiantDormantLockRegistry.IsLocked(clickedPawn)
                && GiantFastLookup.TryGetCockpit(clickedPawn) != null;
        }

        public static int FindBestOpenControlSeatIndex(CompGiantCockpit cockpit)
        {
            if (cockpit?.Props?.seats == null)
            {
                return -1;
            }
            GiantCockpitRole[] controlOrder =
            {
                GiantCockpitRole.PrimaryPilot,
                GiantCockpitRole.Commander,
                GiantCockpitRole.Gunner,
                GiantCockpitRole.Passenger
            };
            for (int r = 0; r < controlOrder.Length; r++)
            {
                for (int i = 0; i < cockpit.SeatCount; i++)
                {
                    if (cockpit.Props.seats[i].role == controlOrder[r] &&
                        cockpit.GetOccupantAt(i) == null &&
                        cockpit.IsSeatPartIntact(i))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Pawn clickedPawn, FloatMenuContext context)
        {
            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(clickedPawn);
            if (cockpit == null || context == null)
            {
                yield break;
            }

            if (context.IsMultiselect)
            {
                List<Pawn> validPawns = new List<Pawn>(context.ValidSelectedPawns);
                if (validPawns.Count == 0)
                {
                    yield break;
                }

                if (!cockpit.CanPawnBoard(validPawns[0], out string reason))
                {
                    string disabledText = "GCF_FloatMenu_BoardDisabled".CanTranslate()
                        ? string.Format("GCF_FloatMenu_BoardDisabled".Translate(), clickedPawn.LabelShort, reason)
                        : $"Cannot board {clickedPawn.LabelShort}: {reason}";
                    yield return new FloatMenuOption(disabledText, null);
                }
                else
                {
                    string multiLabel = "GCF_FloatMenu_BoardMulti".CanTranslate()
                        ? string.Format("GCF_FloatMenu_BoardMulti".Translate(), clickedPawn.LabelShort)
                        : $"Board {clickedPawn.LabelShort}";
                    yield return new FloatMenuOption(multiLabel, delegate
                    {
                        int assignedSeatCursor = 0;
                        for (int p = 0; p < validPawns.Count; p++)
                        {
                            Pawn colonist = validPawns[p];
                            if (colonist == null || !GiantHullInteractionUtility.CanPawnReachGiantHull(colonist, clickedPawn))
                            {
                                continue;
                            }
                            int chosenSeat = -1;
                            while (assignedSeatCursor < cockpit.SeatCount)
                            {
                                int candidateSeat = assignedSeatCursor++;
                                if (cockpit.GetOccupantAt(candidateSeat) == null && cockpit.IsSeatPartIntact(candidateSeat))
                                {
                                    chosenSeat = candidateSeat;
                                    break;
                                }
                            }
                            if (chosenSeat < 0)
                            {
                                break;
                            }
                            JobDriver_EnterGiantCockpit.TryGiveBoardJob(colonist, clickedPawn, chosenSeat);
                        }
                    }, MenuOptionPriority.High);
                }

                foreach (FloatMenuOption remoteOpt in BuildRemoteGiantControlOptions(clickedPawn, cockpit))
                {
                    yield return remoteOpt;
                }
                yield break;
            }

            Pawn selPawn = context.FirstSelectedPawn;
            if (selPawn == null)
            {
                yield break;
            }

            bool canReachHull = GiantHullInteractionUtility.CanPawnReachGiantHull(selPawn, clickedPawn);
            if (!canReachHull)
            {
                string noPath = "NoPath".CanTranslate() ? "NoPath".Translate().ToString() : "No path";
                string label = "GCF_FloatMenu_BoardDisabled".CanTranslate()
                    ? string.Format("GCF_FloatMenu_BoardDisabled".Translate(), clickedPawn.LabelShort, noPath)
                    : $"Cannot board {clickedPawn.LabelShort}: {noPath}";
                yield return new FloatMenuOption(label, null);
            }
            else
            {
                int bestControlSeat = FindBestOpenControlSeatIndex(cockpit);
                if (bestControlSeat < 0 || !cockpit.CanPawnBoard(selPawn, out string failReason, bestControlSeat))
                {
                    cockpit.CanPawnBoard(selPawn, out string fallbackReason);
                    string reason = !string.IsNullOrEmpty(fallbackReason) ? fallbackReason : "Full";
                    string disabledLabel = "GCF_FloatMenu_BoardDisabled".CanTranslate()
                        ? string.Format("GCF_FloatMenu_BoardDisabled".Translate(), clickedPawn.LabelShort, reason)
                        : $"Cannot board {clickedPawn.LabelShort}: {reason}";
                    yield return new FloatMenuOption(disabledLabel, null);
                }
                else
                {
                    string controlRoleName = cockpit.Props.seats[bestControlSeat].GetLocalizedRoleLabel();
                    string controlLabel = "GCF_FloatMenu_TakeCockpitControl".CanTranslate()
                        ? string.Format("GCF_FloatMenu_TakeCockpitControl".Translate(), clickedPawn.LabelShort, controlRoleName)
                        : $"Control {clickedPawn.LabelShort} ({controlRoleName})";
                    int primarySeatCaptured = bestControlSeat;
                    yield return new FloatMenuOption(controlLabel, delegate
                    {
                        JobDriver_EnterGiantCockpit.TryGiveBoardJob(selPawn, clickedPawn, primarySeatCaptured);
                    }, MenuOptionPriority.High);

                    if (cockpit.SeatCount > 1)
                    {
                        for (int r = 0; r < RoleDisplayOrder.Length; r++)
                        {
                            GiantCockpitRole role = RoleDisplayOrder[r];
                            int firstOpenForRole = -1;
                            int openForRoleCount = 0;
                            int totalForRole = 0;
                            string firstUnavailableDetail = null;

                            for (int i = 0; i < cockpit.SeatCount; i++)
                            {
                                if (cockpit.Props.seats[i].role != role)
                                {
                                    continue;
                                }
                                totalForRole++;
                                if (cockpit.CanPawnBoard(selPawn, out string seatFail, i))
                                {
                                    if (firstOpenForRole < 0)
                                    {
                                        firstOpenForRole = i;
                                    }
                                    openForRoleCount++;
                                }
                                else if (firstUnavailableDetail == null)
                                {
                                    Pawn existing = cockpit.GetOccupantAt(i);
                                    firstUnavailableDetail = existing != null ? existing.LabelShort : seatFail;
                                }
                            }

                            if (totalForRole == 0)
                            {
                                continue;
                            }

                            if (firstOpenForRole == bestControlSeat && openForRoleCount == 1)
                            {
                                continue;
                            }

                            string roleName = cockpit.Props.seats[firstOpenForRole >= 0 ? firstOpenForRole : 0].GetLocalizedRoleLabel();
                            for (int i = 0; i < cockpit.SeatCount; i++)
                            {
                                if (cockpit.Props.seats[i].role == role)
                                {
                                    roleName = cockpit.Props.seats[i].GetLocalizedRoleLabel();
                                    break;
                                }
                            }

                            if (openForRoleCount <= 0)
                            {
                                string detail = firstUnavailableDetail ?? "Unavailable";
                                string disabledSeat = "GCF_FloatMenu_SeatUnavailable".CanTranslate()
                                    ? string.Format("GCF_FloatMenu_SeatUnavailable".Translate(), clickedPawn.LabelShort, roleName, detail)
                                    : $"{clickedPawn.LabelShort} [{roleName}]: {detail}";
                                yield return new FloatMenuOption(disabledSeat, null);
                                continue;
                            }

                            int capturedSeat = firstOpenForRole;
                            string optText = openForRoleCount > 1
                                ? ("GCF_FloatMenu_BoardRoleOpenCount".CanTranslate()
                                    ? string.Format("GCF_FloatMenu_BoardRoleOpenCount".Translate(), clickedPawn.LabelShort, roleName, openForRoleCount)
                                    : $"Board {clickedPawn.LabelShort} ({roleName}, {openForRoleCount} open)")
                                : ("GCF_FloatMenu_BoardCockpitSeat".CanTranslate()
                                    ? string.Format("GCF_FloatMenu_BoardCockpitSeat".Translate(), clickedPawn.LabelShort, roleName)
                                    : $"Board {clickedPawn.LabelShort} ({roleName})");

                            yield return new FloatMenuOption(optText, delegate
                            {
                                JobDriver_EnterGiantCockpit.TryGiveBoardJob(selPawn, clickedPawn, capturedSeat);
                            });
                        }
                    }
                }
            }

            foreach (FloatMenuOption remoteOpt in BuildRemoteGiantControlOptions(clickedPawn, cockpit))
            {
                yield return remoteOpt;
            }
        }

        internal static IEnumerable<FloatMenuOption> BuildRemoteGiantControlOptions(Pawn giant, CompGiantCockpit cockpit)
        {
            if (giant == null || !giant.Spawned || giant.Dead || giant.Downed || giant.InMentalState || cockpit == null)
            {
                yield break;
            }

            if (cockpit.Props.allowPlayerControlWithoutOverseer
                && !GiantDormantLockRegistry.IsLocked(giant)
                && GiantPlayerControlUtility.HasRequiredBiotechOverseer(giant, cockpit))
            {
                if (giant.Drafted)
                {
                    string undraftLabel = "GCF_FloatMenu_UndraftControl".CanTranslate()
                        ? string.Format("GCF_FloatMenu_UndraftControl".Translate(), giant.LabelShort)
                        : $"Undraft {giant.LabelShort}";
                    yield return new FloatMenuOption(undraftLabel, delegate
                    {
                        GiantPlayerControlUtility.EnsureController(giant);
                        if (giant.drafter != null)
                        {
                            giant.drafter.Drafted = false;
                        }
                    });
                }
                else if (!cockpit.IsDormantUnmanned || (cockpit.ParkedWhenEmpty && cockpit.Props.autonomousWhenEmpty && cockpit.IsAutonomousCoreIntact()))
                {
                    string draftLabel = "GCF_FloatMenu_DraftControl".CanTranslate()
                        ? string.Format("GCF_FloatMenu_DraftControl".Translate(), giant.LabelShort)
                        : $"Draft & Control {giant.LabelShort}";
                    yield return new FloatMenuOption(draftLabel, delegate
                    {
                        if (cockpit.ParkedWhenEmpty)
                        {
                            cockpit.SetParkedWhenEmpty(false);
                        }
                        GiantPlayerControlUtility.EnsureController(giant);
                        if (giant.drafter != null)
                        {
                            giant.drafter.Drafted = true;
                        }
                        Find.Selector?.ClearSelection();
                        Find.Selector?.Select(giant, playSound: true, forceDesignatorDeselect: false);
                    });
                }

                if (cockpit.Props.autonomousWhenEmpty && cockpit.CrewCount == 0 && cockpit.IsAutonomousCoreIntact())
                {
                    bool currentlyParked = cockpit.ParkedWhenEmpty;
                    string modeLabel = currentlyParked
                        ? ("GCF_FloatMenu_SwitchAutonomousOn".CanTranslate()
                            ? "GCF_FloatMenu_SwitchAutonomousOn".Translate().ToString()
                            : "Control Mode: Enable Autonomous AI")
                        : ("GCF_FloatMenu_SwitchStandbyOn".CanTranslate()
                            ? "GCF_FloatMenu_SwitchStandbyOn".Translate().ToString()
                            : "Control Mode: Park in Standby");
                    yield return new FloatMenuOption(modeLabel, delegate
                    {
                        cockpit.SetParkedWhenEmpty(!currentlyParked);
                    });
                }
            }

            if (cockpit.CrewCount > 0)
            {
                string ratio = $"{cockpit.CrewCount}/{cockpit.SeatCount}";
                string ejectLabel = "GCF_Gizmo_DisembarkAll_Label".CanTranslate()
                    ? string.Format("GCF_Gizmo_DisembarkAll_Label".Translate(), ratio)
                    : $"Disembark All ({ratio})";
                if (cockpit.IsFlightStateBlockingBoardingOrDisembark(out string disembarkReason))
                {
                    yield return new FloatMenuOption($"{ejectLabel} ({disembarkReason})", null);
                }
                else
                {
                    yield return new FloatMenuOption(ejectLabel, delegate
                    {
                        cockpit.EjectAll(violent: false);
                    });
                }
            }
        }
    }
}
