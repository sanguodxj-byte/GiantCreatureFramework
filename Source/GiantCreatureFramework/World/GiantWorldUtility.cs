using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace GiantCreatureFramework
{
    public static class GiantWorldUtility
    {
        private static int confirmedImmediateDeparturePawnId = -1;

        public static void MarkDepartureConfirmed(Pawn pawn)
        {
            confirmedImmediateDeparturePawnId = pawn != null ? pawn.thingIDNumber : -1;
        }

        public static bool ConsumeDepartureConfirmed(Pawn pawn)
        {
            if (pawn != null && confirmedImmediateDeparturePawnId == pawn.thingIDNumber)
            {
                confirmedImmediateDeparturePawnId = -1;
                return true;
            }
            return false;
        }

        public static void ShowNonColonyDepartureConfirmation(Pawn pawn, bool wouldDestroyWorldObject, System.Action onConfirm)
        {
            string giantLabel = pawn?.LabelShortCap ?? pawn?.LabelShort ?? "Giant";
            string mapLabel = pawn?.Map?.Parent?.Label ?? string.Empty;
            string confirmMsg;
            if (wouldDestroyWorldObject)
            {
                confirmMsg = LanguageDatabase.activeLanguage != null && "GCF_WorldDeparture_ConfirmDestroySite".CanTranslate()
                    ? string.Format("GCF_WorldDeparture_ConfirmDestroySite".Translate(), giantLabel, mapLabel)
                    : $"{giantLabel} is the last player unit at \"{mapLabel}\". Leaving now will close the map and remove this world site (all items left behind will be lost). Continue?";
            }
            else
            {
                confirmMsg = LanguageDatabase.activeLanguage != null && "GCF_WorldDeparture_ConfirmCloseMap".CanTranslate()
                    ? string.Format("GCF_WorldDeparture_ConfirmCloseMap".Translate(), giantLabel)
                    : $"{giantLabel} is the last player unit on this non-colony map. Leaving now will close the map and abandon anything left behind. Continue?";
            }
            Find.WindowStack?.Add(Dialog_MessageBox.CreateConfirmation(
                confirmMsg,
                () =>
                {
                    MarkDepartureConfirmed(pawn);
                    onConfirm?.Invoke();
                },
                destructive: true));
        }

        public static bool CanLeaveMapLayer(Map map)
        {
            return map != null && map.Tile.Valid && !(map.Tile.LayerDef?.isSpace ?? false);
        }

        public static Command_Action DepartureCommand(Pawn pawn)
        {
            bool isNonColony = pawn?.Map != null && !pawn.Map.IsPlayerHome;
            var command = new Command_Action
            {
                defaultLabel = MainButtonDefOf.World?.LabelCap ?? "World",
                defaultDesc = (isNonColony ? "CommandReformCaravanDesc" : "CommandFormCaravanDesc").Translate(),
                icon = MainButtonDefOf.World?.Icon ?? TexCommand.Draft,
                action = () =>
                {
                    if (pawn?.Map != null && !pawn.Map.IsPlayerHome
                        && WouldLeavingTriggerMapOrWorldObjectRemoval(pawn, out bool wouldDestroyWorldObject))
                    {
                        ShowNonColonyDepartureConfirmation(pawn, wouldDestroyWorldObject, () => ExecuteImmediateDeparture(pawn));
                        return;
                    }
                    ExecuteImmediateDeparture(pawn);
                }
            };
            if (!GiantPlayerControlUtility.CanMove(pawn))
            {
                command.Disable("Incapable".Translate());
            }
            else if (pawn?.Map != null && !CanLeaveMapLayer(pawn.Map))
            {
                string spaceReason = GiantFlightUtility.SafeTranslate(
                    "GCF_WorldFail_SpaceLayer",
                    "Cannot depart onto orbital space layer");
                command.Disable(spaceReason);
            }
            else if (pawn?.Map != null && !pawn.Map.IsPlayerHome
                && GenHostility.AnyHostileActiveThreatToPlayer(pawn.Map, countDormantPawnsAsHostile: true))
            {
                command.Disable("CommandReformCaravanFailHostilePawns".Translate());
            }
            return command;
        }

        public static bool WouldLeavingTriggerMapOrWorldObjectRemoval(Pawn departingGiant, out bool wouldDestroyWorldObject)
        {
            wouldDestroyWorldObject = false;
            Map map = departingGiant?.Map;
            MapParent mapParent = map?.Parent;
            if (map == null || mapParent == null || map.IsPlayerHome)
            {
                return false;
            }
            if (map.AnyBuildingBlockingMapRemoval || TransporterUtility.IncomingTransporterPreventingMapRemoval(map))
            {
                return false;
            }
            if (HasOtherPawnBlockingMapRemoval(map, departingGiant))
            {
                return false;
            }

            if (mapParent.forceRemoveWorldObjectWhenMapRemoved || mapParent is Camp || mapParent is DestroyedSettlement)
            {
                wouldDestroyWorldObject = true;
            }
            else if (mapParent is Site site)
            {
                bool keepSite = false;
                if (site.parts != null)
                {
                    for (int i = 0; i < site.parts.Count; i++)
                    {
                        SitePart part = site.parts[i];
                        SitePartWorker worker = part?.def?.Worker;
                        if (worker is SitePartWorker_ConditionCauser && part.conditionCauser != null && !part.conditionCauser.Destroyed)
                        {
                            keepSite = true;
                            break;
                        }
                        if (worker is SitePartWorker_RaidSource && GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: true))
                        {
                            keepSite = true;
                            break;
                        }
                        if (worker is SitePartWorker_AncientAltar altar && altar.ShouldKeepMapForRelic(part))
                        {
                            keepSite = true;
                            break;
                        }
                    }
                }
                wouldDestroyWorldObject = !keepSite;
            }
            return true;
        }

        private static bool HasOtherPawnBlockingMapRemoval(Map map, Pawn excludedGiant)
        {
            Faction ofPlayer = Faction.OfPlayer;
            IReadOnlyList<Pawn> spawned = map.mapPawns?.AllPawnsSpawned;
            if (spawned != null)
            {
                for (int i = 0; i < spawned.Count; i++)
                {
                    Pawn p = spawned[i];
                    if (p == null || p == excludedGiant)
                    {
                        continue;
                    }
                    if ((!p.Dead || p.HasDeathRefusalOrResurrecting)
                        && (p.IsColonist || (p.IsColonySubhuman && p.mutant?.Def?.canTravelInCaravan == true)))
                    {
                        return true;
                    }
                    if (p.relations?.relativeInvolvedInRescueQuest != null)
                    {
                        return true;
                    }
                    if (p.Faction == ofPlayer || p.HostFaction == ofPlayer)
                    {
                        Job curJob = p.CurJob;
                        if (curJob != null && curJob.exitMapOnArrival)
                        {
                            return true;
                        }
                        if (p.health?.hediffSet != null && p.health.hediffSet.InLabor())
                        {
                            return true;
                        }
                    }
                    if (!p.Downed && CaravanExitMapUtility.FindCaravanToJoinFor(p) != null)
                    {
                        return true;
                    }
                    if (ModsConfig.BiotechActive && p.IsColonyMechPlayerControlled && !p.Downed && p.GetOverseer() != null)
                    {
                        return true;
                    }
                }
            }

            IEnumerable<Map> pocketMaps = map.ChildPocketMaps;
            if (pocketMaps != null)
            {
                foreach (Map childPocket in pocketMaps)
                {
                    if (childPocket?.mapPawns != null && childPocket.mapPawns.AnyPawnBlockingMapRemoval)
                    {
                        return true;
                    }
                }
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp != null)
            {
                IReadOnlyList<CompGiantRig> giants = mapComp.ActiveGiants;
                if (giants != null)
                {
                    for (int i = 0; i < giants.Count; i++)
                    {
                        Pawn gp = giants[i]?.Pawn;
                        if (gp == null || gp == excludedGiant || !gp.Spawned)
                        {
                            continue;
                        }
                        if (GiantPlayerControlUtility.IsPlayerGiant(gp) && !gp.Dead)
                        {
                            return true;
                        }
                        if (GiantFastLookup.TryGetCockpit(gp) is CompGiantCockpit cp && cp.CrewCount > 0)
                        {
                            ThingOwner held = cp.GetDirectlyHeldThings();
                            if (held != null)
                            {
                                for (int c = 0; c < held.Count; c++)
                                {
                                    if (held[c] is Pawn member && !member.Dead && member.Faction == ofPlayer
                                        && (member.IsColonist || member.IsColonySubhuman))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        public static WorldObject_Giant ExecuteImmediateDeparture(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || !GiantPlayerControlUtility.CanMove(pawn) || !CanLeaveMapLayer(pawn.Map))
            {
                return null;
            }
            PlanetTile sourceTile = pawn.Map.Tile;
            WorldObject_Giant giant = TryLeaveMap(pawn);
            if (giant == null)
            {
                return null;
            }
            CameraJumper.TryJump(sourceTile);
            if (Find.WorldSelector != null)
            {
                List<WorldObject> selected = Find.WorldSelector.SelectedObjects;
                bool hasNonGiant = false;
                for (int i = 0; i < selected.Count; i++)
                {
                    if (selected[i] is not WorldObject_Giant)
                    {
                        hasNonGiant = true;
                        break;
                    }
                }
                if (hasNonGiant)
                {
                    Find.WorldSelector.ClearSelection();
                }
                Find.WorldSelector.Select(giant);
            }
            return giant;
        }

        public static bool TryRaycastMouseWorldPoint(PlanetLayer layer, out Vector3 relativePoint, out PlanetTile hitTile)
        {
            relativePoint = Vector3.zero;
            hitTile = PlanetTile.Invalid;
            Camera worldCamera = Find.WorldCamera;
            if (layer == null || worldCamera == null || !worldCamera.gameObject.activeInHierarchy)
            {
                return false;
            }
            Ray ray = worldCamera.ScreenPointToRay(UI.MousePositionOnUI * Prefs.UIScale);
            WorldTerrainColliderManager.EnsureRaycastCollidersUpdated();
            if (!Physics.Raycast(ray, out RaycastHit hitInfo, GenWorld.MaxRayLength, WorldCameraManager.WorldLayerMask))
            {
                return false;
            }
            hitTile = Find.World.renderer.GetTileFromRayHit(hitInfo);
            if (!hitTile.Valid || hitTile.Layer != layer)
            {
                return false;
            }
            Vector3 rel = hitInfo.point - layer.Origin;
            if (rel.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            relativePoint = rel.normalized * layer.Radius;
            return true;
        }

        public static WorldObject_Giant TryLeaveMap(Pawn pawn)
        {
            if (!GiantPlayerControlUtility.CanMove(pawn) || !pawn.Spawned || !CanLeaveMapLayer(pawn.Map)) return null;
            Map map = pawn.Map;
            IntVec3 position = pawn.Position;
            Rot4 rotation = pawn.Rotation;
            bool drafted = pawn.Drafted;
            var giant = (WorldObject_Giant)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("GCF_WorldGiant"));
            giant.Tile = map.Tile;
            giant.SetFaction(pawn.Faction);
            bool transferred = false;
            try
            {
                pawn.jobs?.StopAll();
                if (pawn.drafter != null) pawn.drafter.Drafted = false;
                pawn.pather?.StopDead();
                pawn.stances?.CancelBusyStanceHard();
                pawn.DeSpawn();
                if (!giant.TakePawn(pawn)) return null;
                Find.WorldObjects.Add(giant);
                transferred = true;
                return giant;
            }
            finally
            {
                if (!transferred)
                {
                    if (giant.GetDirectlyHeldThings().Remove(pawn)) GiantFastLookup.TryGetRig(pawn)?.EndWorldSimulation();
                    if (giant.Spawned) giant.Destroy();
                    if (!pawn.Spawned && !pawn.Destroyed) GenSpawn.Spawn(pawn, position, map, rotation);
                    if (drafted && pawn.drafter != null) pawn.drafter.Drafted = true;
                }
            }
        }

        public static bool TryEnterMap(WorldObject_Giant giant, Map map)
            => TryEnterMap(giant, map, draftPawn: false);

        public static bool TryEnterMap(WorldObject_Giant giant, Map map, bool draftPawn)
        {
            if (giant?.Pawn == null || map == null || !giant.Spawned || giant.Moving || map.Tile != giant.Tile) return false;
            CompGiantRig rig = GiantFastLookup.TryGetRig(giant.Pawn);
            if (rig?.RigDef == null) return false;
            GiantFootprintData footprint = rig.BuildPlacementFootprint();
            var occupied = new HashSet<IntVec3>();
            IReadOnlyList<CompGiantRig> others = GiantFootprintMapComponent.GetFast(map)?.ActiveGiants;
            if (others != null)
            {
                foreach (CompGiantRig other in others)
                {
                    if (other?.Pawn == null || !other.Pawn.Spawned || other.Pawn.Dead || other.Pawn.Downed
                        || other.IsCurrentlyAirborne || other.IsFlyingEntry)
                    {
                        continue;
                    }
                    foreach (IntVec3 cell in other.Footprint.collisionCells) occupied.Add(cell);
                }
            }
            bool CanPlaceGround(IntVec3 cell) => cell.InBounds(map) && !cell.Fogged(map) && cell.Standable(map)
                && cell.GetRoof(map) != RoofDefOf.RoofRockThick && !occupied.Contains(cell) && !HasStandingPawnAt(map, cell);

            GiantFlightDef flightDef = rig.RigDef.ActiveFlightLocomotion;
            bool allowAerialEntry = WorldObject_Giant.CanFlyOnWorld(giant.Pawn) && flightDef != null && flightDef.IsActive;
            bool CanPlaceAerial(IntVec3 cell) => cell.InBounds(map) && !cell.Fogged(map)
                && GiantFlightUtility.IsCellPassableForFlight(map, flightDef, cell)
                && !occupied.Contains(cell) && !HasStandingPawnAt(map, cell);

            GiantAquaticDef aqDef = rig.RigDef.ActiveAquaticLocomotion;
            bool hasAquatic = aqDef != null && aqDef.IsActive;
            bool CanPlaceAquatic(IntVec3 cell) => cell.InBounds(map) && !cell.Fogged(map)
                && cell.GetRoof(map) != RoofDefOf.RoofRockThick
                && GiantAquaticUtility.IsCellPassableForAquatic(map, cell, giant.Pawn, rig, aqDef)
                && !occupied.Contains(cell) && !HasStandingPawnAt(map, cell);

            IntVec3 FindBestEntryCell(System.Func<IntVec3, bool> cellPredicate)
            {
                IntVec3 bestCandidate = IntVec3.Invalid;
                float bestCandidateScore = float.MaxValue;
                foreach (IntVec3 candidate in map.AllCells)
                {
                    if (candidate.Fogged(map) || !cellPredicate(candidate)) continue;
                    float score = Mathf.Min(candidate.x, map.Size.x - 1 - candidate.x, candidate.z, map.Size.z - 1 - candidate.z);
                    score = score * score + candidate.DistanceToSquared(map.Center) * 0.001f;
                    if (score >= bestCandidateScore) continue;
                    bool fits = cellPredicate(candidate);
                    foreach (IntVec3 delta in footprint.collisionCells)
                    {
                        if (fits && cellPredicate(candidate + delta)) continue;
                        fits = false;
                        break;
                    }
                    if (fits) { bestCandidate = candidate; bestCandidateScore = score; }
                }
                return bestCandidate;
            }

            bool usedAerialFallback = false;
            IntVec3 best = IntVec3.Invalid;
            if (hasAquatic && aqDef.IsWaterOnly)
            {
                best = FindBestEntryCell(CanPlaceAquatic);
                if (!best.IsValid)
                {
                    GiantAquaticUtility.TryFindValidAquaticSpawnCell(map, rig.RigDef, map.Center, out best, preferEdgeCells: true);
                }
            }
            else if (hasAquatic && aqDef.IsAmphibious)
            {
                best = FindBestEntryCell(CanPlaceAquatic);
                if (!best.IsValid)
                {
                    best = FindBestEntryCell(CanPlaceGround);
                }
            }
            else
            {
                best = FindBestEntryCell(CanPlaceGround);
            }

            if (!best.IsValid && allowAerialEntry)
            {
                best = FindBestEntryCell(CanPlaceAerial);
                usedAerialFallback = best.IsValid;
            }
            if (!best.IsValid)
            {
                string failMsg = (hasAquatic && aqDef.IsWaterOnly)
                    ? GiantFlightUtility.SafeTranslate("GCF_AquaticFail_NoWaterOnMap", "No navigable water on this map for water-only unit.")
                    : "CannotGoNoPath".Translate().ToString();
                Messages.Message(failMsg, MessageTypeDefOf.RejectInput, false);
                return false;
            }
            giant.ReleasePawnToMap(map, best);
            if (usedAerialFallback && rig.FlightLocomotion != null)
            {
                rig.FlightLocomotion.ForceRestoreAirborneAfterMapEntry(rig);
            }
            GiantPlayerControlUtility.EnsureController(rig.Pawn);
            if (draftPawn && rig.Pawn.drafter != null && GiantPlayerControlUtility.CanControl(rig.Pawn) && !rig.Pawn.InMentalState)
            {
                rig.Pawn.drafter.Drafted = true;
            }
            return true;
        }

        private static bool HasStandingPawnAt(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn p && p.Spawned && !GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(p))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch]
    public static class HarmonyPatches_GiantWorld
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenWorldUI), nameof(GenWorldUI.WorldObjectsUnderMouse))]
        public static void WorldObjectsUnderMouse_Postfix(Vector2 mousePos, ref List<WorldObject> __result)
        {
            __result.RemoveAll(obj => obj is WorldObject_Giant);
            foreach (WorldObject obj in Find.WorldObjects.AllWorldObjects)
            {
                if (obj is WorldObject_Giant giant && giant.Tile.Layer == PlanetLayer.Selected
                    && !giant.HiddenBehindTerrainNow() && giant.Renderer.HitTest(mousePos)) __result.Insert(0, giant);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldSelector), "HandleWorldClicks")]
        public static bool HandleWorldClicks_Prefix(WorldSelector __instance)
        {
            Event evt = Event.current;
            if (evt == null || evt.type != EventType.MouseDown || evt.button != 1 || Find.WorldTargeter.IsTargeting) return true;
            List<WorldObject> selected = __instance.SelectedObjects;
            if (selected.Count == 0 || selected.Exists(obj => obj is not WorldObject_Giant)) return true;
            PlanetTile target = GenWorld.MouseTile();
            if (!target.Valid) return true;
            bool hasExactPoint = GiantWorldUtility.TryRaycastMouseWorldPoint(target.Layer, out Vector3 exactRelPoint, out PlanetTile rayTile);
            if (hasExactPoint && rayTile.Valid)
            {
                target = rayTile;
            }
            var commands = new List<WorldObject_Giant>();
            foreach (WorldObject obj in selected)
                if (obj is WorldObject_Giant giant && giant.Faction == Faction.OfPlayer) commands.Add(giant);
            if (commands.Count == 0) return true;

            MapParent mapParent = null;
            PeaceTalks peaceTalks = null;
            List<WorldObject> underMouse = GenWorldUI.WorldObjectsUnderMouse(UI.MousePositionOnUI);
            for (int i = 0; i < underMouse.Count; i++)
            {
                if (underMouse[i] is MapParent mp && mp.Spawned && !mp.Destroyed && mp.Tile.Layer == target.Layer)
                {
                    mapParent = mp;
                    break;
                }
                if (peaceTalks == null && underMouse[i] is PeaceTalks pt && pt.Spawned && !pt.Destroyed && pt.Tile.Layer == target.Layer)
                {
                    peaceTalks = pt;
                }
            }
            mapParent ??= Find.WorldObjects.MapParentAt(target);
            peaceTalks ??= WorldObject_Giant.FindPeaceTalksAt(target);

            void OrderMoveOnly()
            {
                bool anyFailed = false;
                foreach (WorldObject_Giant giant in commands)
                {
                    bool ok = hasExactPoint
                        ? giant.OrderMoveToPoint(exactRelPoint, target, enterMap: false)
                        : giant.OrderMove(target, enterMap: false);
                    if (!ok) anyFailed = true;
                }
                if (anyFailed) Messages.Message("CannotGoNoPath".Translate(), MessageTypeDefOf.RejectInput, false);
            }

            void OrderArrival(PlanetTile destTile, WorldGiantArrivalAction arrival, bool confirmedByUser)
            {
                bool anyFailed = false;
                foreach (WorldObject_Giant giant in commands)
                {
                    if (!giant.OrderMoveWithArrival(destTile, arrival, confirmedByUser))
                    {
                        anyFailed = true;
                    }
                }
                if (anyFailed) Messages.Message("CannotGoNoPath".Translate(), MessageTypeDefOf.RejectInput, false);
            }

            WorldObject_Giant primaryGiant = commands[0];
            if (mapParent is Settlement settlement && settlement.Spawned && !settlement.Destroyed)
            {
                var menuOptions = new List<FloatMenuOption>
                {
                    new FloatMenuOption("GoHere".Translate(), OrderMoveOnly)
                };
                PlanetTile destTile = settlement.Tile;

                if (settlement.HasMap && !settlement.Attackable)
                {
                    menuOptions.Add(new FloatMenuOption(
                        "EnterMap".Translate(settlement.Label),
                        () => OrderArrival(destTile, WorldGiantArrivalAction.EnterExistingMap, confirmedByUser: true),
                        revalidateWorldClickTarget: settlement));
                }
                else
                {
                    if (!settlement.HasMap && settlement.Visitable)
                    {
                        bool anyNotAtTile = commands.Exists(g => g.Tile != destTile || g.Moving);
                        if (anyNotAtTile)
                        {
                            menuOptions.Add(new FloatMenuOption(
                                "VisitSettlement".Translate(settlement.Label),
                                () => OrderArrival(destTile, WorldGiantArrivalAction.VisitSettlement, confirmedByUser: true),
                                revalidateWorldClickTarget: settlement));
                        }
                        if (settlement.CanTradeNow)
                        {
                            bool canTrade = primaryGiant.CanTradeWithSettlement(settlement, out _, out string tradeFail);
                            string tradeLabel = "TradeWith".Translate(settlement.Label);
                            menuOptions.Add(canTrade
                                ? new FloatMenuOption(
                                    tradeLabel,
                                    () => OrderArrival(destTile, WorldGiantArrivalAction.TradeWithSettlement, confirmedByUser: true),
                                    revalidateWorldClickTarget: settlement)
                                : new FloatMenuOption(tradeLabel + " (" + tradeFail + ")", null));
                        }
                    }

                    if (!settlement.HasMap
                        && settlement.Faction != null
                        && settlement.Faction != Faction.OfPlayer
                        && !settlement.Faction.def.permanentEnemy
                        && settlement.CanTradeNow)
                    {
                        bool canGift = primaryGiant.CanOfferGiftsToSettlement(settlement, out _, out string giftFail);
                        string giftLabel = "OfferGifts".Translate(settlement.Label);
                        menuOptions.Add(canGift
                            ? new FloatMenuOption(
                                giftLabel,
                                () => OrderArrival(destTile, WorldGiantArrivalAction.OfferGiftsToSettlement, confirmedByUser: true),
                                revalidateWorldClickTarget: settlement)
                            : new FloatMenuOption(giftLabel + " (" + giftFail + ")", null));
                    }

                    if (settlement.Attackable)
                    {
                        string attackLabel = "AttackSettlement".Translate(settlement.Label);
                        if (settlement.EnterCooldownBlocksEntering())
                        {
                            string reason = "MessageEnterCooldownBlocksEntering".Translate(
                                settlement.EnterCooldownTicksLeft().ToStringTicksToPeriod());
                            menuOptions.Add(new FloatMenuOption(attackLabel + " (" + reason + ")", null));
                        }
                        else
                        {
                            bool needsConfirm = settlement.Faction != null && settlement.Faction.AllyOrNeutralTo(Faction.OfPlayer);
                            System.Action execAttack = () => OrderArrival(destTile, WorldGiantArrivalAction.AttackSettlement, confirmedByUser: true);
                            System.Action clickAttack = needsConfirm
                                ? () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                                    "ConfirmAttackFriendlyFaction".Translate(settlement.LabelCap, settlement.Faction.Name),
                                    execAttack))
                                : execAttack;
                            menuOptions.Add(new FloatMenuOption(attackLabel, clickAttack, revalidateWorldClickTarget: settlement));
                        }
                    }
                }

                Find.WindowStack.Add(new FloatMenu(menuOptions));
            }
            else if (peaceTalks != null && mapParent == null)
            {
                PlanetTile destTile = peaceTalks.Tile;
                string talkLabel = "VisitPeaceTalks".Translate(peaceTalks.Label);
                bool hasDiplomat = primaryGiant.FindBestDiplomat() != null;
                FloatMenuOption talkOption = hasDiplomat
                    ? new FloatMenuOption(
                        talkLabel,
                        () => OrderArrival(destTile, WorldGiantArrivalAction.VisitPeaceTalks, confirmedByUser: true),
                        revalidateWorldClickTarget: peaceTalks)
                    : new FloatMenuOption(talkLabel + " (" + "MessagePeaceTalksNoDiplomat".Translate() + ")", null);
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("GoHere".Translate(), OrderMoveOnly),
                    talkOption
                }));
            }
            else
            {
                WorldGiantArrivalAction vanillaArrival = WorldObject_Giant.ResolveArrivalActionForMapParent(mapParent);
                if (mapParent != null && vanillaArrival != WorldGiantArrivalAction.None)
                {
                    string optionLabel;
                    bool needsFriendlyAttackConfirm = false;
                    if (vanillaArrival == WorldGiantArrivalAction.VisitSite && mapParent is Site site)
                    {
                        optionLabel = site.ApproachOrderString;
                        needsFriendlyAttackConfirm = !site.HasMap && site.Faction != null
                            && site.Faction != Faction.OfPlayer && !site.Faction.HostileTo(Faction.OfPlayer)
                            && WorldObject_Giant.SiteConsidersEnteringAsAttack(site);
                    }
                    else if (vanillaArrival == WorldGiantArrivalAction.VisitEscapeShip)
                    {
                        optionLabel = "VisitEscapeShip".Translate(mapParent.Label);
                    }
                    else
                    {
                        optionLabel = "EnterMap".Translate(mapParent.Label);
                    }

                    FloatMenuOption arrivalOption;
                    if (mapParent.EnterCooldownBlocksEntering())
                    {
                        string reason = "MessageEnterCooldownBlocksEntering".Translate(
                            mapParent.EnterCooldownTicksLeft().ToStringTicksToPeriod());
                        arrivalOption = new FloatMenuOption(optionLabel + " (" + reason + ")", null);
                    }
                    else
                    {
                        PlanetTile destTile = mapParent.Tile;
                        System.Action executeOrder = () => OrderArrival(destTile, vanillaArrival, confirmedByUser: true);
                        System.Action clickedAction = needsFriendlyAttackConfirm
                            ? () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                                "ConfirmAttackFriendlyFaction".Translate(mapParent.LabelCap, mapParent.Faction.Name),
                                executeOrder))
                            : executeOrder;
                        arrivalOption = new FloatMenuOption(optionLabel, clickedAction, revalidateWorldClickTarget: mapParent);
                    }

                    Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                    {
                        new FloatMenuOption("GoHere".Translate(), OrderMoveOnly),
                        arrivalOption
                    }));
                }
                else
                {
                    OrderMoveOnly();
                }
            }
            evt.Use();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldSelectionDrawer), "DrawSelectionBracketOnGUIFor")]
        public static bool SelectionBracket_Prefix(WorldObject obj)
        {
            if (obj is not WorldObject_Giant giant) return true;
            if (giant.Tile.Layer == PlanetLayer.Selected && !giant.HiddenBehindTerrainNow())
            {
                giant.Renderer.DrawSelection();
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
        public static bool ExitMap_Prefix(Pawn __instance, bool allowedToJoinOrCreateCaravan)
        {
            if (!__instance.Spawned
                || __instance.teleporting
                || __instance.IsCaravanMember()
                || ThingOwnerUtility.AnyParentIs<ActiveTransporterInfo>(__instance)
                || ThingOwnerUtility.AnyParentIs<TravellingTransporters>(__instance)
                || !GiantPlayerControlUtility.IsPlayerGiant(__instance))
            {
                return true;
            }
            if (__instance.Map != null
                && !__instance.Map.IsPlayerHome
                && !GiantWorldUtility.ConsumeDepartureConfirmed(__instance)
                && GiantWorldUtility.WouldLeavingTriggerMapOrWorldObjectRemoval(__instance, out bool wouldDestroyWorldObject))
            {
                __instance.jobs?.StopAll();
                __instance.pather?.StopDead();
                Pawn capturedPawn = __instance;
                GiantWorldUtility.ShowNonColonyDepartureConfirmation(
                    capturedPawn,
                    wouldDestroyWorldObject,
                    () => GiantWorldUtility.ExecuteImmediateDeparture(capturedPawn));
                return false;
            }
            Lord prevLord = __instance.GetLord();
            WorldObject_Giant worldGiant = GiantWorldUtility.TryLeaveMap(__instance);
            if (worldGiant != null)
            {
                prevLord?.Notify_PawnLost(__instance, PawnLostCondition.ExitedMap);
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldPawnsUtility), nameof(WorldPawnsUtility.IsWorldPawn))]
        public static void IsWorldPawn_WorldGiantTrade_Postfix(Pawn p, ref bool __result)
        {
            if (!__result
                && p != null
                && TradeSession.Active
                && TradeSession.trader is WorldGiantSettlementTraderAdapter
                && ReferenceEquals(TradeSession.playerNegotiator, p))
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ExpandableLandmarksUtility), "LandmarksToShow", MethodType.Getter)]
        public static void LandmarksToShow_WorldGiant_Postfix(List<PlanetTile> __result)
        {
            if (__result == null || !ModsConfig.OdysseyActive || WorldObject_Giant.ActiveWorldGiants.Count == 0)
            {
                return;
            }
            var landmarks = Find.World?.landmarks?.landmarks;
            WorldObjectsHolder holder = Find.WorldObjects;
            if (landmarks == null || holder == null)
            {
                return;
            }
            IReadOnlyList<WorldObject_Giant> giants = WorldObject_Giant.ActiveWorldGiants;
            for (int i = 0; i < giants.Count; i++)
            {
                WorldObject_Giant g = giants[i];
                if (g == null || !g.Spawned || !g.Tile.Valid || !landmarks.ContainsKey(g.Tile))
                {
                    continue;
                }
                bool hasNonGiantWorldObject = false;
                foreach (WorldObject obj in holder.ObjectsAt(g.Tile))
                {
                    if (obj is not WorldObject_Giant)
                    {
                        hasNonGiantWorldObject = true;
                        break;
                    }
                }
                if (!hasNonGiantWorldObject && !__result.Contains(g.Tile))
                {
                    __result.Add(g.Tile);
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick))]
        public static bool JobTrackerTick_Prefix(Pawn ___pawn) => !IsHeldOnWorld(___pawn);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTickInterval))]
        public static bool JobTrackerTickInterval_Prefix(Pawn ___pawn) => !IsHeldOnWorld(___pawn);

        private static bool IsHeldOnWorld(Pawn pawn)
        {
            if (pawn == null || pawn.Spawned) return false;
            IThingHolder parent = pawn.ParentHolder;
            for (int depth = 0; parent != null && depth < 8; depth++, parent = parent.ParentHolder)
                if (parent is WorldObject_Giant) return true;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.AnyPawnBlockingMapRemoval), MethodType.Getter)]
        public static void AnyPawnBlockingMapRemoval_Postfix(Map ___map, ref bool __result)
        {
            if (__result || ___map == null) return;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            if (mapComp == null) return;
            if (mapComp.HasAnyBoardedColonyPawnBlockingRemoval())
            {
                __result = true;
                return;
            }
            IReadOnlyList<CompGiantRig> giants = mapComp.ActiveGiants;
            if (giants != null)
                foreach (CompGiantRig rig in giants)
                    if (GiantPlayerControlUtility.IsPlayerGiant(rig.Pawn) && !rig.Pawn.Dead) { __result = true; break; }
        }
    }
}
