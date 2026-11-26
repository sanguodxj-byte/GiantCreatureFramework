using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum WorldGiantArrivalAction
    {
        None = 0,
        EnterExistingMap = 1,
        AttackSettlement = 2,
        VisitSite = 3,
        VisitEscapeShip = 4,
        VisitSettlement = 5,
        TradeWithSettlement = 6,
        OfferGiftsToSettlement = 7,
        VisitPeaceTalks = 8
    }

    internal sealed class WorldGiantSettlementTraderAdapter : ITrader
    {
        private readonly Settlement settlement;
        private readonly WorldObject_Giant worldGiant;

        public WorldGiantSettlementTraderAdapter(Settlement settlement, WorldObject_Giant worldGiant)
        {
            this.settlement = settlement;
            this.worldGiant = worldGiant;
        }

        public Settlement Settlement => settlement;
        public WorldObject_Giant WorldGiant => worldGiant;
        public TraderKindDef TraderKind => settlement?.TraderKind;
        public IEnumerable<Thing> Goods => settlement?.Goods ?? System.Array.Empty<Thing>();
        public int RandomPriceFactorSeed => settlement?.RandomPriceFactorSeed ?? 0;
        public string TraderName => settlement?.TraderName ?? string.Empty;
        public bool CanTradeNow => settlement != null && settlement.CanTradeNow;
        public float TradePriceImprovementOffsetForPlayer => settlement?.TradePriceImprovementOffsetForPlayer ?? 0f;
        public Faction Faction => settlement?.Faction;
        public TradeCurrency TradeCurrency => settlement?.TradeCurrency ?? TradeCurrency.Silver;

        public IEnumerable<Thing> ColonyThingsWillingToBuy(Pawn playerNegotiator)
        {
            if (worldGiant == null || !worldGiant.Spawned)
            {
                yield break;
            }
            var yielded = new HashSet<Thing>();
            Pawn giantPawn = worldGiant.Pawn;
            if (giantPawn != null && giantPawn.inventory?.innerContainer != null)
            {
                ThingOwner giantInv = giantPawn.inventory.innerContainer;
                for (int i = 0; i < giantInv.Count; i++)
                {
                    Thing item = giantInv[i];
                    if (item != null && yielded.Add(item))
                    {
                        yield return item;
                    }
                }
            }
            if (giantPawn != null && GiantFastLookup.TryGetCockpit(giantPawn) is CompGiantCockpit cockpit)
            {
                IReadOnlyList<Pawn> crew = cockpit.BoardedCrew;
                for (int c = 0; c < crew.Count; c++)
                {
                    Pawn member = crew[c];
                    if (member?.inventory?.innerContainer == null)
                    {
                        continue;
                    }
                    ThingOwner inv = member.inventory.innerContainer;
                    for (int i = 0; i < inv.Count; i++)
                    {
                        Thing item = inv[i];
                        if (item != null && yielded.Add(item))
                        {
                            yield return item;
                        }
                    }
                }
            }
            ThingOwner direct = worldGiant.GetDirectlyHeldThings();
            if (direct != null)
            {
                for (int i = 0; i < direct.Count; i++)
                {
                    Thing item = direct[i];
                    if (item == null || item == giantPawn || item is Corpse && ((Corpse)item).InnerPawn == giantPawn)
                    {
                        continue;
                    }
                    if (item is Pawn p && (p.IsFreeColonist || p.IsColonySubhuman))
                    {
                        continue;
                    }
                    if (yielded.Add(item))
                    {
                        yield return item;
                    }
                }
            }
        }

        public void GiveSoldThingToTrader(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            if (toGive == null || countToGive <= 0 || settlement == null)
            {
                return;
            }
            Thing split = toGive.SplitOff(countToGive);
            split.PreTraded(TradeAction.PlayerSells, playerNegotiator, settlement);
            ThingOwner traderStock = settlement.trader?.GetDirectlyHeldThings();
            if (split is Pawn soldPawn)
            {
                if (!soldPawn.RaceProps.Humanlike && traderStock != null && traderStock.TryAdd(soldPawn, canMergeWithExistingStacks: false))
                {
                    return;
                }
                soldPawn.Destroy();
            }
            else if (traderStock == null || !traderStock.TryAdd(split, canMergeWithExistingStacks: false))
            {
                split.Destroy();
            }
        }

        public void GiveSoldThingToPlayer(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            if (toGive == null || countToGive <= 0 || settlement == null)
            {
                return;
            }
            Thing split = toGive.SplitOff(countToGive);
            split.PreTraded(TradeAction.PlayerBuys, playerNegotiator, settlement);
            Pawn giantPawn = worldGiant?.Pawn;
            if (split is Pawn boughtPawn)
            {
                boughtPawn.SetFaction(Faction.OfPlayer);
                if (giantPawn != null && GiantFastLookup.TryGetCockpit(giantPawn) is CompGiantCockpit cockpit
                    && boughtPawn.RaceProps.Humanlike && cockpit.HasFreeSeat)
                {
                    if (cockpit.TryAcceptCrewImmediate(boughtPawn))
                    {
                        return;
                    }
                }
                PlanetTile targetTile = worldGiant != null && worldGiant.Tile.Valid ? worldGiant.Tile : settlement.Tile;
                Caravan existingCaravan = Find.WorldObjects?.PlayerControlledCaravanAt(targetTile);
                if (existingCaravan != null)
                {
                    existingCaravan.AddPawn(boughtPawn, addCarriedPawnToWorldPawnsIfAny: true);
                }
                else
                {
                    CaravanMaker.MakeCaravan(new[] { boughtPawn }, Faction.OfPlayer, targetTile, addToWorldPawnsIfNotAlready: true);
                }
                return;
            }

            if (playerNegotiator?.inventory?.innerContainer != null
                && playerNegotiator.inventory.innerContainer.TryAdd(split))
            {
                return;
            }
            if (giantPawn != null && GiantFastLookup.TryGetCockpit(giantPawn) is CompGiantCockpit cp)
            {
                IReadOnlyList<Pawn> crew = cp.BoardedCrew;
                for (int i = 0; i < crew.Count; i++)
                {
                    Pawn member = crew[i];
                    if (member != null && member != playerNegotiator && member.inventory?.innerContainer != null
                        && member.inventory.innerContainer.TryAdd(split))
                    {
                        return;
                    }
                }
            }
            if (giantPawn?.inventory?.innerContainer != null && giantPawn.inventory.innerContainer.TryAdd(split))
            {
                return;
            }
            if (worldGiant != null && worldGiant.GetDirectlyHeldThings().TryAdd(split))
            {
                return;
            }
            split.Destroy();
        }
    }

    public class WorldObject_Giant : WorldObject, IThingHolderTickable
    {
        private static readonly List<WorldObject_Giant> activeWorldGiants = new List<WorldObject_Giant>(4);
        public static IReadOnlyList<WorldObject_Giant> ActiveWorldGiants => activeWorldGiants;

        public static void ClearStaticState()
        {
            activeWorldGiants.Clear();
        }
        private ThingOwner<Thing> contents;
        private List<PlanetTile> route = new List<PlanetTile>();
        private int routeIndex;
        private float segmentProgress;
        private Vector3 segmentStart;
        private bool hasDirectDestination;
        private Vector3 directStartPosition;
        private Vector3 directTargetPosition;
        private PlanetTile directDestinationTile = PlanetTile.Invalid;
        private float directArcLength;
        private float directProgress;
        private Vector3 position;
        private bool hasPosition;
        private float heading;
        private bool enterOnArrival;
        private WorldGiantArrivalAction arrivalAction;
        private bool arrivalConfirmedByUser;
        private GiantWorldRenderer renderer;
        private readonly List<PlanetTile> tileSyncNeighbors = new List<PlanetTile>(8);
        private static readonly List<SitePartDef> tmpSitePartDefs = new List<SitePartDef>();
        private static readonly HashSet<SitePartDef> tmpUsedSitePartDefs = new HashSet<SitePartDef>();

        public WorldObject_Giant()
        {
            contents = new ThingOwner<Thing>(this, false, LookMode.Deep);
        }

        public Pawn Pawn
        {
            get
            {
                foreach (Thing thing in contents)
                {
                    if (thing is Pawn pawn) return pawn;
                    if (thing is Corpse corpse) return corpse.InnerPawn;
                }
                return null;
            }
        }

        public bool Moving => hasDirectDestination;
        public float Heading => heading;
        public PlanetTile Destination => hasDirectDestination && directDestinationTile.Valid ? directDestinationTile : Tile;
        public float SegmentProgress => hasDirectDestination ? directProgress : 0f;
        public bool ShouldTickContents => true;
        public override string Label => Pawn?.LabelShort ?? base.Label;
        public override bool HasName => true;
        public override bool AppendFactionToInspectString => false;
        public override Vector3 DrawPos => Tile.Layer.Origin + (hasPosition ? position : Find.WorldGrid.GetTileCenter(Tile));
        public override float DrawAltitude => renderer != null ? renderer.CurrentBaseAltitude : GiantWorldRenderer.ComputeGiantBaseAltitude(ID, CanFlyOnWorld(Pawn), 1, 0.00075f);
        public float WorldUnitsPerCell => Tile.Layer.AverageTileSize / 20f;
        public GiantWorldRenderer Renderer => renderer ??= new GiantWorldRenderer(this);

        public ThingOwner GetDirectlyHeldThings() => contents;
        public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, contents);

        public bool TakePawn(Pawn pawn)
        {
            if (pawn == null || pawn.Spawned || contents.Count != 0) return false;
            if (!contents.TryAddOrTransfer(pawn)) return false;
            position = Find.WorldGrid.GetTileCenter(Tile);
            segmentStart = position;
            hasPosition = true;
            heading = GiantFastLookup.TryGetRig(pawn)?.CurrentHeadingDeg ?? pawn.Rotation.AsAngle;
            GiantFastLookup.TryGetRig(pawn)?.BeginWorldSimulation();
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref contents, "gcf_worldContents", this);
            Scribe_Collections.Look(ref route, "gcf_worldRoute", LookMode.Value);
            Scribe_Values.Look(ref routeIndex, "gcf_worldRouteIndex");
            Scribe_Values.Look(ref segmentProgress, "gcf_worldSegmentProgress");
            Scribe_Values.Look(ref segmentStart, "gcf_worldSegmentStart");
            Scribe_Values.Look(ref hasDirectDestination, "gcf_worldHasDirectDest");
            Scribe_Values.Look(ref directStartPosition, "gcf_worldDirectStartPos");
            Scribe_Values.Look(ref directTargetPosition, "gcf_worldDirectTargetPos");
            Scribe_Values.Look(ref directDestinationTile, "gcf_worldDirectDestTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref directArcLength, "gcf_worldDirectArcLen");
            Scribe_Values.Look(ref directProgress, "gcf_worldDirectProgress");
            Scribe_Values.Look(ref position, "gcf_worldPosition");
            Scribe_Values.Look(ref hasPosition, "gcf_worldHasPosition");
            Scribe_Values.Look(ref heading, "gcf_worldHeading");
            Scribe_Values.Look(ref enterOnArrival, "gcf_worldEnterOnArrival");
            Scribe_Values.Look(ref arrivalAction, "gcf_worldArrivalAction", WorldGiantArrivalAction.None);
            Scribe_Values.Look(ref arrivalConfirmedByUser, "gcf_worldArrivalConfirmed", defaultValue: false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                contents ??= new ThingOwner<Thing>(this, false, LookMode.Deep);
                route ??= new List<PlanetTile>();
                routeIndex = Mathf.Clamp(routeIndex, 0, route.Count);
                if (arrivalAction == WorldGiantArrivalAction.None && enterOnArrival && directDestinationTile.Valid)
                {
                    arrivalAction = ResolveArrivalActionForTile(directDestinationTile, out _);
                }
                if (!hasDirectDestination && route.Count > 0 && routeIndex < route.Count)
                {
                    PlanetTile finalTile = route[route.Count - 1];
                    if (finalTile.Valid && finalTile.Layer == Tile.Layer && Find.WorldGrid != null)
                    {
                        float radius = Tile.Layer.Radius;
                        Vector3 startRel = hasPosition ? position : Find.WorldGrid.GetTileCenter(Tile);
                        Vector3 targetRel = Find.WorldGrid.GetTileCenter(finalTile);
                        directStartPosition = startRel.normalized * radius;
                        directTargetPosition = targetRel.normalized * radius;
                        directDestinationTile = finalTile;
                        directArcLength = ComputeGeodesicArcLength(directStartPosition, directTargetPosition, radius);
                        directProgress = 0f;
                        hasDirectDestination = directArcLength > 1e-5f;
                        if (arrivalAction == WorldGiantArrivalAction.None && enterOnArrival)
                        {
                            arrivalAction = ResolveArrivalActionForTile(finalTile, out _);
                        }
                    }
                    route.Clear();
                    routeIndex = 0;
                }
                GiantFastLookup.TryGetRig(Pawn)?.BeginWorldSimulation();
            }
        }

        public static float ComputeGeodesicArcLength(Vector3 startRel, Vector3 endRel, float radius)
        {
            if (radius <= 1e-6f || startRel.sqrMagnitude < 1e-10f || endRel.sqrMagnitude < 1e-10f)
            {
                return 0f;
            }
            float dot = Mathf.Clamp(Vector3.Dot(startRel.normalized, endRel.normalized), -1f, 1f);
            return Mathf.Acos(dot) * radius;
        }

        public static Vector3 EvaluateGeodesicPoint(Vector3 startRel, Vector3 endRel, float t, float radius)
        {
            Vector3 aHat = startRel.sqrMagnitude > 1e-10f ? startRel.normalized : Vector3.forward;
            Vector3 bHat = endRel.sqrMagnitude > 1e-10f ? endRel.normalized : aHat;
            if (t <= 0f) return aHat * radius;
            if (t >= 1f) return bHat * radius;

            float dot = Mathf.Clamp(Vector3.Dot(aHat, bHat), -1f, 1f);
            if (dot > 0.999999f)
            {
                return Vector3.Lerp(aHat, bHat, t).normalized * radius;
            }
            if (dot < -0.999999f)
            {
                Vector3 refAxis = Mathf.Abs(aHat.y) > 0.9f ? Vector3.right : Vector3.up;
                Vector3 perp = Vector3.Cross(aHat, refAxis).normalized;
                float ang = t * Mathf.PI;
                return (aHat * Mathf.Cos(ang) + perp * Mathf.Sin(ang)).normalized * radius;
            }

            float theta = Mathf.Acos(dot);
            float sinTheta = Mathf.Sin(theta);
            float wA = Mathf.Sin((1f - t) * theta) / sinTheta;
            float wB = Mathf.Sin(t * theta) / sinTheta;
            return (aHat * wA + bHat * wB).normalized * radius;
        }

        public static float ComputeGeodesicHeadingDeg(Vector3 currentRel, Vector3 targetRel)
            => ComputeGeodesicHeadingDeg(currentRel, targetRel, 0f);

        public static float ComputeGeodesicHeadingDeg(Vector3 currentRel, Vector3 targetRel, float fallbackHeadingDeg)
        {
            if (currentRel.sqrMagnitude < 1e-10f || targetRel.sqrMagnitude < 1e-10f)
            {
                return fallbackHeadingDeg;
            }
            Vector3 pHat = currentRel.normalized;
            Vector3 tHat = targetRel.normalized;
            Vector3 tangent = tHat - pHat * Vector3.Dot(pHat, tHat);
            if (tangent.sqrMagnitude < 1e-10f)
            {
                if (Vector3.Dot(pHat, tHat) < 0f)
                {
                    Vector3 refAxis = Mathf.Abs(pHat.y) > 0.9f ? Vector3.right : Vector3.up;
                    tangent = Vector3.Cross(pHat, refAxis);
                }
                else
                {
                    return fallbackHeadingDeg;
                }
            }
            GiantWorldRenderer.GetBasis(currentRel, out Vector3 east, out _, out Vector3 north);
            return Mathf.Atan2(Vector3.Dot(tangent, east), Vector3.Dot(tangent, north)) * Mathf.Rad2Deg;
        }

        public bool OrderMove(PlanetTile destination, bool enterMap = false)
        {
            WorldGiantArrivalAction action = enterMap
                ? ResolveArrivalActionForTile(destination, out _)
                : WorldGiantArrivalAction.None;
            return OrderMoveWithArrival(destination, action, confirmedByUser: false);
        }

        public bool OrderMoveWithArrival(PlanetTile destination, WorldGiantArrivalAction desiredArrival, bool confirmedByUser = false)
        {
            Pawn pawn = Pawn;
            if (!Spawned || !GiantPlayerControlUtility.CanMove(pawn)
                || !destination.Valid || destination.Layer != Tile.Layer)
            {
                return false;
            }
            Vector3 targetRel = Find.WorldGrid.GetTileCenter(destination);
            return OrderMoveDirect(targetRel, destination, desiredArrival, confirmedByUser);
        }

        public bool OrderMoveToPoint(Vector3 targetRelativeToLayer, PlanetTile tileAtPoint, bool enterMap = false)
        {
            Pawn pawn = Pawn;
            if (!Spawned || !GiantPlayerControlUtility.CanMove(pawn)
                || !tileAtPoint.Valid || tileAtPoint.Layer != Tile.Layer
                || targetRelativeToLayer.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            WorldGiantArrivalAction action = enterMap
                ? ResolveArrivalActionForTile(tileAtPoint, out _)
                : WorldGiantArrivalAction.None;
            Vector3 projected = targetRelativeToLayer.normalized * Tile.Layer.Radius;
            return OrderMoveDirect(projected, tileAtPoint, action, confirmedByUser: false);
        }

        private bool OrderMoveDirect(Vector3 targetRel, PlanetTile destTile, WorldGiantArrivalAction desiredArrival, bool confirmedByUser)
        {
            PlanetLayer layer = Tile.Layer;
            if (layer == null)
            {
                return false;
            }
            float radius = layer.Radius;
            Vector3 currentRel = hasPosition ? position : Find.WorldGrid.GetTileCenter(Tile);
            if (currentRel.sqrMagnitude < 1e-8f || targetRel.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            currentRel = currentRel.normalized * radius;
            targetRel = targetRel.normalized * radius;

            Pawn pawn = Pawn;
            CompGiantRig rig = pawn != null ? GiantFastLookup.TryGetRig(pawn) : null;
            GiantAquaticDef aqDef = (!CanFlyOnWorld(pawn)) ? rig?.RigDef?.ActiveAquaticLocomotion : null;
            route.Clear();
            routeIndex = 0;
            segmentProgress = 0f;

            if (aqDef != null && aqDef.IsActive && destTile.Valid)
            {
                if (!GiantAquaticUtility.IsWorldTilePassableForAquatic(destTile, aqDef))
                {
                    string rejectMsg = GiantFlightUtility.SafeTranslate(
                        "GCF_AquaticWorldFail_InvalidDestination",
                        "Cannot navigate to inland dry terrain (water or coastal route required).");
                    Messages.Message(rejectMsg, this, MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }

                if (aqDef.IsWaterOnly && !aqDef.allowInlandWorldTravelWhenWaterOnly && Tile.Valid && Tile != destTile)
                {
                    if (!GiantAquaticUtility.TryFindAquaticWorldRouteWaypoints(Tile, destTile, aqDef, route))
                    {
                        string noSeaRouteMsg = GiantFlightUtility.SafeTranslate(
                            "GCF_AquaticWorldFail_NoWaterRoute",
                            "No connected water or coastal route to destination.");
                        Messages.Message(noSeaRouteMsg, this, MessageTypeDefOf.RejectInput, historical: false);
                        route.Clear();
                        return false;
                    }
                }
            }

            PlanetTile firstLegTile = (route.Count > 1) ? route[0] : destTile;
            Vector3 firstLegTargetRel = (route.Count > 1)
                ? Find.WorldGrid.GetTileCenter(firstLegTile).normalized * radius
                : targetRel;

            float arcLen = ComputeGeodesicArcLength(currentRel, firstLegTargetRel, radius);

            if (arcLen <= 1e-4f && route.Count <= 1)
            {
                position = targetRel;
                hasPosition = true;
                if (destTile.Valid && destTile.Layer == layer)
                {
                    SetTileAndNotifyLandmarks(destTile);
                }
                else
                {
                    SyncCurrentTileToPosition();
                }
                Stop();
                if (desiredArrival != WorldGiantArrivalAction.None)
                {
                    ExecuteVanillaArrival(desiredArrival, confirmedByUser);
                }
                return true;
            }

            position = currentRel;
            hasPosition = true;
            directStartPosition = currentRel;
            directTargetPosition = firstLegTargetRel;
            directDestinationTile = destTile;
            directArcLength = Mathf.Max(1e-4f, arcLen);
            directProgress = 0f;
            hasDirectDestination = true;
            arrivalAction = desiredArrival;
            enterOnArrival = desiredArrival != WorldGiantArrivalAction.None;
            arrivalConfirmedByUser = confirmedByUser;
            segmentStart = currentRel;
            return true;
        }

        public static bool CanFlyOnWorld(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed)
            {
                return false;
            }
            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            GiantFlightDef flightDef = rig?.RigDef?.ActiveFlightLocomotion;
            if (flightDef == null || !flightDef.IsActive)
            {
                return false;
            }
            return rig.FlightLocomotion == null || rig.FlightLocomotion.CanMaintainLift;
        }

        /// <summary>
        /// Resolves the default non-destructive vanilla arrival action for a WorldObject/MapParent:
        /// - Friendly/Neutral NPC Settlement without map: VisitSettlement (peaceful visit; never forces war by default).
        /// - Hostile NPC Settlement: AttackSettlement.
        /// - Player Settlement or any other MapParent with HasMap: EnterExistingMap (CaravanArrivalAction_Enter).
        /// - Site without map: VisitSite (CaravanArrivalAction_VisitSite).
        /// - EscapeShip without map: VisitEscapeShip (CaravanArrivalAction_VisitEscapeShip).
        /// - PeaceTalks: VisitPeaceTalks.
        /// - Empty tiles / DestroyedSettlement without map / non-enterable MapParents: None (never fabricates a Camp).
        /// </summary>
        public static WorldGiantArrivalAction ResolveArrivalActionForWorldObject(WorldObject worldObj)
        {
            if (worldObj == null || !worldObj.Spawned || worldObj.Destroyed)
            {
                return WorldGiantArrivalAction.None;
            }
            if (worldObj is PeaceTalks)
            {
                return WorldGiantArrivalAction.VisitPeaceTalks;
            }
            if (worldObj is MapParent mp)
            {
                return ResolveArrivalActionForMapParent(mp);
            }
            return WorldGiantArrivalAction.None;
        }

        public static WorldGiantArrivalAction ResolveArrivalActionForMapParent(MapParent mapParent)
        {
            if (mapParent == null || !mapParent.Spawned || mapParent.Destroyed)
            {
                return WorldGiantArrivalAction.None;
            }
            if (mapParent is Settlement settlement)
            {
                if (settlement.HasMap && !settlement.Attackable)
                {
                    return WorldGiantArrivalAction.EnterExistingMap;
                }
                if (!settlement.HasMap && settlement.Visitable)
                {
                    return WorldGiantArrivalAction.VisitSettlement;
                }
                if (settlement.Attackable)
                {
                    return WorldGiantArrivalAction.AttackSettlement;
                }
                return settlement.HasMap
                    ? WorldGiantArrivalAction.EnterExistingMap
                    : WorldGiantArrivalAction.None;
            }
            if (mapParent is Site site)
            {
                return site.HasMap
                    ? WorldGiantArrivalAction.EnterExistingMap
                    : WorldGiantArrivalAction.VisitSite;
            }
            if (mapParent.GetComponent<EscapeShipComp>() != null)
            {
                return mapParent.HasMap
                    ? WorldGiantArrivalAction.EnterExistingMap
                    : WorldGiantArrivalAction.VisitEscapeShip;
            }
            return mapParent.HasMap
                ? WorldGiantArrivalAction.EnterExistingMap
                : WorldGiantArrivalAction.None;
        }

        public static WorldGiantArrivalAction ResolveArrivalActionForTile(PlanetTile tile, out MapParent mapParent)
        {
            mapParent = null;
            if (!tile.Valid)
            {
                return WorldGiantArrivalAction.None;
            }
            mapParent = Find.WorldObjects?.MapParentAt(tile);
            if (mapParent != null)
            {
                return ResolveArrivalActionForMapParent(mapParent);
            }
            if (Find.WorldObjects != null)
            {
                foreach (WorldObject obj in Find.WorldObjects.ObjectsAt(tile))
                {
                    if (obj is PeaceTalks && obj.Spawned && !obj.Destroyed)
                    {
                        return WorldGiantArrivalAction.VisitPeaceTalks;
                    }
                }
            }
            return WorldGiantArrivalAction.None;
        }

        public static PeaceTalks FindPeaceTalksAt(PlanetTile tile)
        {
            if (!tile.Valid || Find.WorldObjects == null)
            {
                return null;
            }
            foreach (WorldObject obj in Find.WorldObjects.ObjectsAt(tile))
            {
                if (obj is PeaceTalks pt && pt.Spawned && !pt.Destroyed)
                {
                    return pt;
                }
            }
            return null;
        }

        public static bool CanEnterWorldTile(PlanetTile tile)
        {
            WorldGiantArrivalAction action = ResolveArrivalActionForTile(tile, out MapParent mapParent);
            if (action == WorldGiantArrivalAction.VisitPeaceTalks)
            {
                return FindPeaceTalksAt(tile) != null;
            }
            return action != WorldGiantArrivalAction.None && mapParent != null && !mapParent.EnterCooldownBlocksEntering();
        }

        public Pawn FindBestNegotiator(Faction faction = null, TraderKindDef traderKind = null)
        {
            Pawn best = null;
            float bestVal = -1f;

            void Consider(Pawn candidate)
            {
                if (candidate == null || candidate.Dead || candidate.Downed || candidate.InMentalState)
                {
                    return;
                }
                if (candidate.Faction != Faction.OfPlayer || candidate.RaceProps?.Humanlike != true || candidate.IsPrisoner)
                {
                    return;
                }
                if (candidate.skills == null || candidate.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
                {
                    return;
                }
                if (candidate.health?.capacities == null || !candidate.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                {
                    return;
                }
                if (faction != null && traderKind != null && !candidate.CanTradeWith(faction, traderKind).Accepted)
                {
                    return;
                }
                float val = candidate.GetStatValue(StatDefOf.TradePriceImprovement);
                if (best == null || val > bestVal)
                {
                    best = candidate;
                    bestVal = val;
                }
            }

            Pawn giantPawn = Pawn;
            Consider(giantPawn);
            if (giantPawn != null && GiantFastLookup.TryGetCockpit(giantPawn) is CompGiantCockpit cockpit)
            {
                IReadOnlyList<Pawn> crew = cockpit.BoardedCrew;
                for (int i = 0; i < crew.Count; i++)
                {
                    Consider(crew[i]);
                }
            }
            return best;
        }

        public Pawn FindBestDiplomat()
        {
            Pawn best = null;
            float bestVal = -1f;

            void Consider(Pawn candidate)
            {
                if (candidate == null || candidate.Dead || candidate.Downed || candidate.InMentalState)
                {
                    return;
                }
                if (candidate.Faction != Faction.OfPlayer || candidate.RaceProps?.Humanlike != true || candidate.IsPrisoner)
                {
                    return;
                }
                if (candidate.skills == null || candidate.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
                {
                    return;
                }
                if (candidate.health?.capacities == null || !candidate.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                {
                    return;
                }
                float val = candidate.GetStatValue(StatDefOf.NegotiationAbility);
                if (best == null || val > bestVal)
                {
                    best = candidate;
                    bestVal = val;
                }
            }

            Pawn giantPawn = Pawn;
            Consider(giantPawn);
            if (giantPawn != null && GiantFastLookup.TryGetCockpit(giantPawn) is CompGiantCockpit cockpit)
            {
                IReadOnlyList<Pawn> crew = cockpit.BoardedCrew;
                for (int i = 0; i < crew.Count; i++)
                {
                    Consider(crew[i]);
                }
            }
            return best;
        }

        public bool CanTradeWithSettlement(Settlement settlement, out Pawn negotiator, out string failReason)
        {
            negotiator = null;
            failReason = null;
            if (settlement == null || !settlement.Spawned || settlement.HasMap || settlement.Faction == null
                || settlement.Faction == Faction.OfPlayer || settlement.Faction.def.permanentEnemy
                || settlement.Faction.HostileTo(Faction.OfPlayer) || !settlement.CanTradeNow)
            {
                return false;
            }
            negotiator = FindBestNegotiator(settlement.Faction, settlement.TraderKind);
            if (negotiator == null)
            {
                failReason = "CommandTradeFailNoNegotiator".Translate();
                return false;
            }
            return true;
        }

        public bool CanOfferGiftsToSettlement(Settlement settlement, out Pawn negotiator, out string failReason)
        {
            negotiator = null;
            failReason = null;
            if (settlement == null || !settlement.Spawned || settlement.HasMap || settlement.Faction == null
                || settlement.Faction == Faction.OfPlayer || settlement.Faction.def.permanentEnemy
                || !settlement.CanTradeNow)
            {
                return false;
            }
            negotiator = FindBestNegotiator();
            if (negotiator == null)
            {
                failReason = "CommandTradeFailNoNegotiator".Translate();
                return false;
            }
            return true;
        }

        internal static bool TryBuildRoute(PlanetTile start, PlanetTile destination, out List<PlanetTile> route, bool canFly = false)
        {
            route = null;
            if (!start.Valid || !destination.Valid || destination.Layer != start.Layer) return false;
            if (destination == start)
            {
                route = new List<PlanetTile>();
                return true;
            }
            if (canFly)
            {
                return TryBuildAerialRoute(start, destination, out route);
            }
            if (!Find.WorldPathGrid.Passable(destination)) return false;
            using WorldPath path = start.Layer.Pather.FindPath(start, destination, null);
            if (!path.Found || path.NodesReversed.Count < 2) return false;
            route = new List<PlanetTile>(path.NodesReversed.Count - 1);
            for (int i = path.NodesReversed.Count - 2; i >= 0; i--)
                route.Add(path.NodesReversed[i]);
            return true;
        }

        internal static bool TryBuildAerialRoute(PlanetTile start, PlanetTile destination, out List<PlanetTile> route)
        {
            PlanetLayer layer = start.Layer;
            if (layer == null)
            {
                route = null;
                return false;
            }
            int maxSteps = Mathf.Max(64, layer.TilesCount);
            List<PlanetTile> scratchNeighbors = new List<PlanetTile>(8);
            return TryBuildSphericalAerialRoute(
                start,
                destination,
                maxSteps,
                layer.GetTileCenter,
                (tile, list) =>
                {
                    list.Clear();
                    layer.GetTileNeighbors(tile, list);
                },
                out route,
                scratchNeighbors);
        }

        public static bool TryBuildSphericalAerialRoute<TTile>(
            TTile start,
            TTile destination,
            int maxSteps,
            Func<TTile, Vector3> getCenter,
            Action<TTile, List<TTile>> getNeighbors,
            out List<TTile> route,
            List<TTile> neighborBuffer = null) where TTile : IEquatable<TTile>
        {
            route = null;
            if (getCenter == null || getNeighbors == null || maxSteps <= 0)
            {
                return false;
            }
            if (start.Equals(destination))
            {
                route = new List<TTile>();
                return true;
            }

            Vector3 startDir = getCenter(start).normalized;
            Vector3 destDir = getCenter(destination).normalized;
            if (startDir.sqrMagnitude < 1e-8f || destDir.sqrMagnitude < 1e-8f)
            {
                return false;
            }

            Vector3 greatCircleNormal = Vector3.Cross(startDir, destDir);
            float normalMag = greatCircleNormal.magnitude;
            if (normalMag > 1e-6f)
            {
                greatCircleNormal /= normalMag;
            }

            neighborBuffer ??= new List<TTile>(8);
            var visited = new HashSet<TTile> { start };
            var steps = new List<TTile>(32);
            TTile current = start;

            for (int step = 0; step < maxSteps; step++)
            {
                neighborBuffer.Clear();
                getNeighbors(current, neighborBuffer);
                if (neighborBuffer.Count == 0)
                {
                    return false;
                }

                bool foundCandidate = false;
                TTile bestNext = default;
                float bestScore = float.NegativeInfinity;

                for (int i = 0; i < neighborBuffer.Count; i++)
                {
                    TTile cand = neighborBuffer[i];
                    if (cand.Equals(destination))
                    {
                        steps.Add(cand);
                        route = steps;
                        return true;
                    }
                    if (visited.Contains(cand))
                    {
                        continue;
                    }

                    Vector3 candDir = getCenter(cand).normalized;
                    if (candDir.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }

                    float progressDot = Vector3.Dot(candDir, destDir);
                    float crossTrackPenalty = normalMag > 1e-6f
                        ? Mathf.Abs(Vector3.Dot(candDir, greatCircleNormal)) * 0.08f
                        : 0f;
                    float score = progressDot - crossTrackPenalty;
                    if (!foundCandidate || score > bestScore)
                    {
                        foundCandidate = true;
                        bestNext = cand;
                        bestScore = score;
                    }
                }

                if (!foundCandidate)
                {
                    return false;
                }

                visited.Add(bestNext);
                steps.Add(bestNext);
                current = bestNext;
            }

            return false;
        }

        internal void FollowRoute(List<PlanetTile> newRoute, bool enterMap)
        {
            if (newRoute == null || newRoute.Count == 0)
            {
                Stop();
                if (enterMap) QueueEnterMap();
                return;
            }
            PlanetTile dest = newRoute[newRoute.Count - 1];
            OrderMove(dest, enterMap);
        }

        public void Stop()
        {
            route.Clear();
            routeIndex = 0;
            segmentProgress = 0f;
            segmentStart = position;
            hasDirectDestination = false;
            directStartPosition = position;
            directTargetPosition = position;
            directDestinationTile = PlanetTile.Invalid;
            directArcLength = 0f;
            directProgress = 0f;
            enterOnArrival = false;
            arrivalAction = WorldGiantArrivalAction.None;
            arrivalConfirmedByUser = false;
        }

        private void SyncCurrentTileToPosition()
        {
            PlanetLayer layer = Tile.Layer;
            if (layer == null || !hasPosition || Find.WorldGrid == null)
            {
                return;
            }
            PlanetTile best = Tile;
            float bestDistSq = (Find.WorldGrid.GetTileCenter(best) - position).sqrMagnitude;
            for (int iter = 0; iter < 8; iter++)
            {
                tileSyncNeighbors.Clear();
                layer.GetTileNeighbors(best, tileSyncNeighbors);
                PlanetTile nextBest = best;
                for (int i = 0; i < tileSyncNeighbors.Count; i++)
                {
                    PlanetTile cand = tileSyncNeighbors[i];
                    float dSq = (Find.WorldGrid.GetTileCenter(cand) - position).sqrMagnitude;
                    if (dSq + 1e-6f < bestDistSq)
                    {
                        bestDistSq = dSq;
                        nextBest = cand;
                    }
                }
                if (nextBest == best)
                {
                    break;
                }
                best = nextBest;
            }
            if (best != Tile)
            {
                SetTileAndNotifyLandmarks(best);
            }
        }

        private void SetTileAndNotifyLandmarks(PlanetTile newTile)
        {
            PlanetTile prev = Tile;
            if (prev == newTile)
            {
                return;
            }
            Tile = newTile;
            if (ModsConfig.OdysseyActive && Find.World?.landmarks != null)
            {
                ExpandableLandmarksUtility.Notify_WorldObjectsChanged();
            }
        }

        public override void SpawnSetup()
        {
            base.SpawnSetup();
            if (!activeWorldGiants.Contains(this))
            {
                activeWorldGiants.Add(this);
            }
            if (ModsConfig.OdysseyActive && Find.World?.landmarks != null)
            {
                ExpandableLandmarksUtility.Notify_WorldObjectsChanged();
            }
        }

        private static void StabilizeBiologicalGiantInWorldTransit(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed)
            {
                return;
            }
            if (pawn.needs != null)
            {
                if (pawn.needs.food != null && pawn.needs.food.CurLevelPercentage < 0.75f)
                {
                    pawn.needs.food.CurLevelPercentage = 0.75f;
                }
                if (pawn.needs.rest != null && pawn.needs.rest.CurLevelPercentage < 0.75f)
                {
                    pawn.needs.rest.CurLevelPercentage = 0.75f;
                }
            }
            Hediff malnutrition = pawn.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
            if (malnutrition != null)
            {
                pawn.health.RemoveHediff(malnutrition);
            }
        }

        internal static bool SiteConsidersEnteringAsAttack(Site site)
        {
            if (site?.parts == null)
            {
                return false;
            }
            for (int i = 0; i < site.parts.Count; i++)
            {
                if (site.parts[i]?.def != null && site.parts[i].def.considerEnteringAsAttack)
                {
                    return true;
                }
            }
            return false;
        }

        protected override void Tick()
        {
            base.Tick();
            if (contents == null || contents.Count == 0)
            {
                Destroy();
                return;
            }

            Pawn pawn = Pawn;
            if (pawn == null)
            {
                ForceAbandonAndDestroy();
                return;
            }

            if (pawn.Dead)
            {
                if (hasDirectDestination)
                {
                    Stop();
                }
                if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit deadCockpit && deadCockpit.CrewCount > 0)
                {
                    deadCockpit.EjectAll(violent: true, mapOverride: null);
                }
                bool hasIntactCorpse = pawn.Corpse != null && !pawn.Corpse.Destroyed && contents.Contains(pawn.Corpse);
                if (!hasIntactCorpse)
                {
                    ForceAbandonAndDestroy();
                }
                return;
            }

            if (pawn.Destroyed)
            {
                ForceAbandonAndDestroy();
                return;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig == null) return;
            if (!rig.WorldSimulationActive) rig.BeginWorldSimulation();
            if (pawn.IsHashIntervalTick(120))
            {
                StabilizeBiologicalGiantInWorldTransit(pawn);
                GiantFastLookup.TryGetCockpit(pawn)?.StabilizeBoardedCrewInWorldTransit();
            }
            float movedCells = 0f;
            if (hasDirectDestination && GiantPlayerControlUtility.CanMove(pawn))
            {
                PlanetLayer layer = Tile.Layer;
                if (layer == null)
                {
                    Stop();
                }
                else
                {
                    bool flyingWorld = CanFlyOnWorld(pawn);
                    float radius = layer.Radius;
                    if (directArcLength <= 1e-5f)
                    {
                        directArcLength = ComputeGeodesicArcLength(directStartPosition, directTargetPosition, radius);
                    }
                    if (directArcLength <= 1e-5f)
                    {
                        position = directTargetPosition;
                        hasPosition = true;
                        if (directDestinationTile.Valid && directDestinationTile.Layer == layer)
                        {
                            SetTileAndNotifyLandmarks(directDestinationTile);
                        }
                        else
                        {
                            SyncCurrentTileToPosition();
                        }
                        WorldGiantArrivalAction pendingAction = arrivalAction;
                        if (pendingAction == WorldGiantArrivalAction.None && enterOnArrival)
                        {
                            pendingAction = ResolveArrivalActionForTile(Tile, out _);
                        }
                        bool pendingConfirmed = arrivalConfirmedByUser;
                        Stop();
                        if (pendingAction != WorldGiantArrivalAction.None)
                        {
                            ExecuteVanillaArrival(pendingAction, pendingConfirmed);
                        }
                    }
                    else
                    {
                        heading = ComputeGeodesicHeadingDeg(position, directTargetPosition, heading);
                        GiantAquaticDef worldAq = !flyingWorld ? rig.RigDef?.ActiveAquaticLocomotion : null;
                        bool onWaterTile = false;
                        if (!flyingWorld && Tile.Valid && Find.WorldGrid != null)
                        {
                            Tile curTileInfo = Find.WorldGrid[Tile];
                            onWaterTile = curTileInfo != null && curTileInfo.WaterCovered;
                        }

                        float difficulty = 1f;
                        if (!flyingWorld && Find.WorldPathGrid != null && Tile.Valid)
                        {
                            if (onWaterTile && worldAq != null && worldAq.IsActive && worldAq.allowWorldWaterTravel)
                            {
                                difficulty = 1f;
                            }
                            else
                            {
                                difficulty = Find.WorldPathGrid.Passable(Tile)
                                    ? Mathf.Clamp(Find.WorldPathGrid.PerceivedMovementDifficultyAt(Tile), 1f, 4f)
                                    : 3.5f;
                            }
                        }
                        float fMult = 1f;
                        if (flyingWorld)
                        {
                            fMult = rig.FlightLocomotion != null
                                ? rig.FlightLocomotion.WorldFlightSpeedMultiplier
                                : Mathf.Max(0.15f, rig.RigDef?.ActiveFlightLocomotion?.ResolvedWorldFlightSpeedMultiplier ?? 1f);
                        }
                        else if (worldAq != null && worldAq.IsActive)
                        {
                            fMult = onWaterTile
                                ? Mathf.Max(0.1f, worldAq.EffectiveWorldWaterSpeedMultiplier)
                                : (worldAq.IsAmphibious ? Mathf.Max(0.1f, worldAq.landSpeedMultiplier) : 1f);
                        }
                        float ticksPerCell = ComputeEffectiveWorldTicksPerCell(pawn.TicksPerMoveCardinal, flyingWorld || (onWaterTile && worldAq != null && worldAq.IsActive), fMult);
                        float stepWorldUnits = WorldUnitsPerCell / ticksPerCell / difficulty;
                        float previous = directProgress;
                        directProgress = Mathf.Min(1f, directProgress + stepWorldUnits / directArcLength);
                        position = EvaluateGeodesicPoint(directStartPosition, directTargetPosition, directProgress, radius);
                        hasPosition = true;
                        movedCells = (directProgress - previous) * directArcLength / WorldUnitsPerCell;
                        SyncCurrentTileToPosition();
                        if (directProgress >= 1f)
                        {
                            position = directTargetPosition;
                            if (route != null && routeIndex + 1 < route.Count)
                            {
                                PlanetTile reachedWaypoint = route[routeIndex];
                                if (reachedWaypoint.Valid && reachedWaypoint.Layer == layer)
                                {
                                    SetTileAndNotifyLandmarks(reachedWaypoint);
                                }
                                routeIndex++;
                                PlanetTile nextLegTile = route[routeIndex];
                                Vector3 nextLegTargetRel = Find.WorldGrid.GetTileCenter(nextLegTile).normalized * radius;
                                directStartPosition = position;
                                directTargetPosition = nextLegTargetRel;
                                directArcLength = Mathf.Max(1e-4f, ComputeGeodesicArcLength(directStartPosition, directTargetPosition, radius));
                                directProgress = 0f;
                            }
                            else
                            {
                                if (directDestinationTile.Valid && directDestinationTile.Layer == layer)
                                {
                                    SetTileAndNotifyLandmarks(directDestinationTile);
                                }
                                else
                                {
                                    SyncCurrentTileToPosition();
                                }
                                WorldGiantArrivalAction pendingAction = arrivalAction;
                                if (pendingAction == WorldGiantArrivalAction.None && enterOnArrival)
                                {
                                    pendingAction = ResolveArrivalActionForTile(Tile, out _);
                                }
                                bool pendingConfirmed = arrivalConfirmedByUser;
                                Stop();
                                if (pendingAction != WorldGiantArrivalAction.None)
                                {
                                    ExecuteVanillaArrival(pendingAction, pendingConfirmed);
                                }
                            }
                        }
                    }
                }
            }
            rig.TickWorldAnimation(heading, movedCells);
        }

        public static float ComputeEffectiveWorldTicksPerCell(float baseTicksPerMoveCardinal, bool flyingWorld, float worldFlightSpeedMultiplier)
        {
            float ticksPerCell = Mathf.Max(1f, baseTicksPerMoveCardinal);
            if (flyingWorld && worldFlightSpeedMultiplier > 0.01f && !Mathf.Approximately(worldFlightSpeedMultiplier, 1f))
            {
                ticksPerCell = Mathf.Max(0.25f, ticksPerCell / worldFlightSpeedMultiplier);
            }
            return ticksPerCell;
        }

        public override void Draw()
        {
            if (Pawn != null && Tile.Layer == PlanetLayer.Selected && !this.HiddenBehindTerrainNow()) Renderer.Draw();
        }

        public override string GetInspectString() => Pawn?.GetInspectString() ?? string.Empty;

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos()) yield return gizmo;
            if (Faction != Faction.OfPlayer || Pawn == null) yield break;

            if (!Pawn.Dead)
            {
                var move = new Command_Action
                {
                    defaultLabel = "GoHere".Translate(),
                    icon = TexCommand.Draft,
                    action = () => Find.WorldTargeter.BeginTargeting(target =>
                    {
                        bool ok = (!target.HasWorldObject
                            && GiantWorldUtility.TryRaycastMouseWorldPoint(Tile.Layer, out Vector3 exactRel, out PlanetTile hitTile))
                            ? OrderMoveToPoint(exactRel, hitTile)
                            : OrderMove(target.Tile);
                        if (ok) return true;
                        Messages.Message("CannotGoNoPath".Translate(), MessageTypeDefOf.RejectInput, false);
                        return false;
                    }, true, extraLabelGetter: _ => default, showCancelButton: true)
                };
                if (!GiantPlayerControlUtility.CanMove(Pawn)) move.Disable("Incapable".Translate());
                yield return move;
                if (Moving)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "Cancel".Translate(),
                        icon = TexCommand.ClearPrioritizedWork,
                        action = Stop
                    };
                }
            }

            if (!Pawn.Dead)
            {
                WorldGiantArrivalAction tileAction = ResolveArrivalActionForTile(Tile, out MapParent mapParent);
                if (mapParent is Settlement settlementAtTile && !settlementAtTile.HasMap)
                {
                    if (settlementAtTile.Visitable && settlementAtTile.CanTradeNow)
                    {
                        bool canTrade = CanTradeWithSettlement(settlementAtTile, out _, out string tradeFail);
                        var tradeCmd = new Command_Action
                        {
                            defaultLabel = "CommandTrade".Translate(),
                            defaultDesc = "CommandTradeDesc".Translate(),
                            icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade", false) ?? TexCommand.Draft,
                            action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.TradeWithSettlement, confirmedByUser: true)
                        };
                        if (Moving)
                        {
                            tradeCmd.Disable();
                        }
                        else if (!canTrade)
                        {
                            tradeCmd.Disable(tradeFail);
                        }
                        yield return tradeCmd;
                    }

                    if (settlementAtTile.Faction != null
                        && settlementAtTile.Faction != Faction.OfPlayer
                        && !settlementAtTile.Faction.def.permanentEnemy
                        && settlementAtTile.CanTradeNow)
                    {
                        bool canGift = CanOfferGiftsToSettlement(settlementAtTile, out _, out string giftFail);
                        var giftCmd = new Command_Action
                        {
                            defaultLabel = "CommandOfferGifts".Translate(),
                            defaultDesc = "CommandOfferGiftsDesc".Translate(),
                            icon = ContentFinder<Texture2D>.Get("UI/Commands/OfferGifts", false) ?? TexCommand.Draft,
                            action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.OfferGiftsToSettlement, confirmedByUser: true)
                        };
                        if (Moving)
                        {
                            giftCmd.Disable();
                        }
                        else if (!canGift)
                        {
                            giftCmd.Disable(giftFail);
                        }
                        yield return giftCmd;
                    }
                }

                PeaceTalks peaceTalksAtTile = FindPeaceTalksAt(Tile);
                if (peaceTalksAtTile != null)
                {
                    var peaceCmd = new Command_Action
                    {
                        defaultLabel = "VisitPeaceTalks".Translate(peaceTalksAtTile.Label),
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/ShowMap", false) ?? TexCommand.Draft,
                        action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.VisitPeaceTalks, confirmedByUser: true)
                    };
                    if (Moving)
                    {
                        peaceCmd.Disable();
                    }
                    else if (FindBestDiplomat() == null)
                    {
                        peaceCmd.Disable("MessagePeaceTalksNoDiplomat".Translate());
                    }
                    yield return peaceCmd;
                }

                if (tileAction != WorldGiantArrivalAction.None && mapParent != null)
                {
                    Command_Action entryCmd = null;
                    if (mapParent is Settlement settlement && settlement.Attackable)
                    {
                        entryCmd = new Command_Action
                        {
                            defaultLabel = "CommandAttackSettlement".Translate(),
                            defaultDesc = "CommandAttackSettlementDesc".Translate(),
                            icon = Settlement.AttackCommand,
                            action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.AttackSettlement, confirmedByUser: false)
                        };
                    }
                    else if (tileAction == WorldGiantArrivalAction.EnterExistingMap)
                    {
                        entryCmd = new Command_Action
                        {
                            defaultLabel = "EnterMap".Translate(mapParent.Label),
                            icon = ContentFinder<Texture2D>.Get("UI/Commands/ShowMap", false) ?? TexCommand.Draft,
                            action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.EnterExistingMap, confirmedByUser: true)
                        };
                    }
                    else if (tileAction == WorldGiantArrivalAction.VisitSite && mapParent is Site site)
                    {
                        entryCmd = new Command_Action
                        {
                            defaultLabel = site.ApproachOrderString,
                            icon = ContentFinder<Texture2D>.Get("UI/Commands/ShowMap", false) ?? TexCommand.Draft,
                            action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.VisitSite, confirmedByUser: false)
                        };
                    }
                    else if (tileAction == WorldGiantArrivalAction.VisitEscapeShip)
                    {
                        entryCmd = new Command_Action
                        {
                            defaultLabel = "VisitEscapeShip".Translate(mapParent.Label),
                            icon = ContentFinder<Texture2D>.Get("UI/Commands/ShowMap", false) ?? TexCommand.Draft,
                            action = () => ExecuteVanillaArrival(WorldGiantArrivalAction.VisitEscapeShip, confirmedByUser: true)
                        };
                    }

                    if (entryCmd != null)
                    {
                        if (Moving)
                        {
                            entryCmd.Disable();
                        }
                        else if (mapParent.EnterCooldownBlocksEntering())
                        {
                            entryCmd.Disable("MessageEnterCooldownBlocksEntering".Translate(
                                mapParent.EnterCooldownTicksLeft().ToStringTicksToPeriod()));
                        }
                        yield return entryCmd;
                    }
                }
            }

            if (Pawn.Dead)
            {
                string dismissLabel = GiantFlightUtility.SafeTranslate(
                    "GCF_WorldGizmo_AbandonDead_Label",
                    "Abandon Remains");
                string dismissDesc = GiantFlightUtility.SafeTranslate(
                    "GCF_WorldGizmo_AbandonDead_Desc",
                    "Safely rescue any remaining crew and dismiss this deceased giant from the world map.");
                yield return new Command_Action
                {
                    defaultLabel = dismissLabel,
                    defaultDesc = dismissDesc,
                    icon = TexCommand.ClearPrioritizedWork,
                    action = () =>
                    {
                        string confirmMsg = LanguageDatabase.activeLanguage != null && "GCF_WorldGizmo_AbandonDead_Confirm".CanTranslate()
                            ? string.Format("GCF_WorldGizmo_AbandonDead_Confirm".Translate(), Label)
                            : $"Abandon the remains of {Label} on the world map? This cannot be undone.";
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(confirmMsg, ForceAbandonAndDestroy, destructive: true));
                    }
                };
            }
        }

        public void ForceAbandonAndDestroy()
        {
            Pawn pawn = Pawn;
            if (pawn != null && GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.CrewCount > 0)
            {
                cockpit.EjectAll(violent: false, mapOverride: null);
            }
            Stop();
            if (contents != null && contents.Count > 0)
            {
                contents.ClearAndDestroyContents();
            }
            if (!Destroyed)
            {
                Destroy();
            }
        }

        public void QueueEnterMap()
        {
            WorldGiantArrivalAction action = ResolveArrivalActionForTile(Tile, out _);
            if (action == WorldGiantArrivalAction.None)
            {
                Messages.Message("CannotGoNoPath".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            ExecuteVanillaArrival(action, confirmedByUser: false);
        }

        public void ExecuteVanillaArrival(WorldGiantArrivalAction action, bool confirmedByUser)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer)
            {
                return;
            }
            PlanetTile destination = Tile;
            if (action == WorldGiantArrivalAction.VisitPeaceTalks)
            {
                PeaceTalks pt = FindPeaceTalksAt(destination);
                if (pt != null)
                {
                    VisitPeaceTalksNow(pt);
                }
                return;
            }

            WorldGiantArrivalAction resolved = ResolveArrivalActionForTile(destination, out MapParent mapParent);
            if (resolved == WorldGiantArrivalAction.VisitPeaceTalks)
            {
                PeaceTalks pt = FindPeaceTalksAt(destination);
                if (pt != null)
                {
                    VisitPeaceTalksNow(pt);
                }
                return;
            }
            if (mapParent == null || resolved == WorldGiantArrivalAction.None)
            {
                return;
            }
            if (mapParent is Settlement targetSettlement)
            {
                if (action == WorldGiantArrivalAction.VisitSettlement && targetSettlement.Visitable && !targetSettlement.HasMap)
                {
                    VisitSettlementPeacefullyNow(targetSettlement);
                    return;
                }
                if (action == WorldGiantArrivalAction.TradeWithSettlement)
                {
                    OpenSettlementTradeDialog(targetSettlement, giftsOnly: false);
                    return;
                }
                if (action == WorldGiantArrivalAction.OfferGiftsToSettlement)
                {
                    OpenSettlementTradeDialog(targetSettlement, giftsOnly: true);
                    return;
                }
                if (action != WorldGiantArrivalAction.AttackSettlement && resolved == WorldGiantArrivalAction.VisitSettlement && !targetSettlement.HasMap)
                {
                    VisitSettlementPeacefullyNow(targetSettlement);
                    return;
                }
            }

            if (mapParent.EnterCooldownBlocksEntering())
            {
                Messages.Message(
                    "MessageEnterCooldownBlocksEntering".Translate(mapParent.EnterCooldownTicksLeft().ToStringTicksToPeriod()),
                    new GlobalTargetInfo(destination),
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            if (mapParent is Settlement attackSettlement && attackSettlement.Attackable)
            {
                if (!confirmedByUser && attackSettlement.Faction != null && attackSettlement.Faction.AllyOrNeutralTo(Faction.OfPlayer))
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "ConfirmAttackFriendlyFaction".Translate(attackSettlement.LabelCap, attackSettlement.Faction.Name),
                        () => BeginAttackSettlement(attackSettlement)));
                    return;
                }
                BeginAttackSettlement(attackSettlement);
                return;
            }

            if (action == WorldGiantArrivalAction.VisitSite || (resolved == WorldGiantArrivalAction.VisitSite && mapParent is Site))
            {
                if (mapParent is Site site)
                {
                    if (!confirmedByUser && !site.HasMap && site.Faction != null
                        && site.Faction != Faction.OfPlayer && !site.Faction.HostileTo(Faction.OfPlayer)
                        && SiteConsidersEnteringAsAttack(site))
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            "ConfirmAttackFriendlyFaction".Translate(site.LabelCap, site.Faction.Name),
                            () => BeginVisitSite(site)));
                        return;
                    }
                    BeginVisitSite(site);
                }
                return;
            }

            if (action == WorldGiantArrivalAction.VisitEscapeShip || resolved == WorldGiantArrivalAction.VisitEscapeShip)
            {
                if (mapParent.GetComponent<EscapeShipComp>() != null)
                {
                    BeginVisitEscapeShip(mapParent);
                }
                return;
            }

            if (resolved == WorldGiantArrivalAction.EnterExistingMap && mapParent.HasMap)
            {
                EnterExistingMapNow(mapParent);
            }
        }

        private void VisitSettlementPeacefullyNow(Settlement settlement)
        {
            if (settlement == null || !settlement.Spawned || !settlement.Visitable || settlement.Tile != Tile)
            {
                return;
            }
            Find.LetterStack?.ReceiveLetter(
                "LetterLabelCaravanEnteredMap".Translate(settlement),
                "LetterCaravanEnteredMap".Translate(Label, settlement).CapitalizeFirst(),
                LetterDefOf.NeutralEvent,
                new LookTargets(this));
        }

        public bool OpenSettlementTradeDialog(Settlement settlement, bool giftsOnly)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer || settlement == null || settlement.Tile != Tile)
            {
                return false;
            }
            Pawn negotiator;
            string failReason;
            bool allowed = giftsOnly
                ? CanOfferGiftsToSettlement(settlement, out negotiator, out failReason)
                : CanTradeWithSettlement(settlement, out negotiator, out failReason);
            if (!allowed || negotiator == null)
            {
                if (!string.IsNullOrEmpty(failReason))
                {
                    Messages.Message(failReason, this, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }
            CameraJumper.TryJumpAndSelect(this);
            Find.WindowStack?.Add(new Dialog_Trade(negotiator, new WorldGiantSettlementTraderAdapter(settlement, this), giftsOnly));
            return true;
        }

        private static readonly SimpleCurve PeaceTalksBadOutcomeFactorCurve = new SimpleCurve
        {
            new CurvePoint(0f, 4f),
            new CurvePoint(1f, 1f),
            new CurvePoint(1.5f, 0.4f)
        };

        private void VisitPeaceTalksNow(PeaceTalks peaceTalks)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer
                || peaceTalks == null || !peaceTalks.Spawned || peaceTalks.Destroyed || peaceTalks.Tile != Tile)
            {
                return;
            }
            Pawn diplomat = FindBestDiplomat();
            if (diplomat == null)
            {
                Messages.Message("MessagePeaceTalksNoDiplomat".Translate(), this, MessageTypeDefOf.NegativeEvent, historical: false);
                return;
            }

            Faction talkFaction = peaceTalks.Faction;
            float negAbility = diplomat.GetStatValue(StatDefOf.NegotiationAbility);
            float leaderOffset = 0f;
            if (ModsConfig.IdeologyActive && Faction.OfPlayer?.leader != null)
            {
                bool hasLeader = diplomat == Faction.OfPlayer.leader;
                if (!hasLeader && GiantFastLookup.TryGetCockpit(Pawn) is CompGiantCockpit cp)
                {
                    IReadOnlyList<Pawn> crew = cp.BoardedCrew;
                    for (int i = 0; i < crew.Count; i++)
                    {
                        if (crew[i] == Faction.OfPlayer.leader)
                        {
                            hasLeader = true;
                            break;
                        }
                    }
                }
                leaderOffset = hasLeader ? -0.05f : 0.05f;
            }
            float badFactor = PeaceTalksBadOutcomeFactorCurve.Evaluate(negAbility) * (1f + leaderOffset);
            float goodFactor = 1f / Mathf.Max(0.05f, badFactor);

            float wDisaster = 0.05f * badFactor;
            float wBackfire = 0.10f * badFactor;
            float wFlounder = 0.20f;
            float wSuccess = 0.55f * goodFactor;
            float wTriumph = 0.10f * goodFactor;
            float roll = Rand.Value * (wDisaster + wBackfire + wFlounder + wSuccess + wTriumph);

            string BuildTalkLetterText(TaggedString baseText, FactionRelationKind prevRel, int royalFavor)
            {
                TaggedString text = baseText;
                text += "\n\n" + "PeaceTalksSocialXPGain".Translate(diplomat.LabelShort, 6000f.ToString("F0"), diplomat.Named("PAWN"));
                if (royalFavor > 0 && talkFaction != null)
                {
                    text += "\n\n" + "PeaceTalksRoyalFavorGain".Translate(diplomat.LabelShort, royalFavor.ToString(), talkFaction.Named("FACTION"), diplomat.Named("PAWN"));
                }
                talkFaction?.TryAppendRelationKindChangedInfo(ref text, prevRel, talkFaction.PlayerRelationKind);
                return text;
            }

            int GainRoyalFavorIfApplicable()
            {
                if (talkFaction?.def != null && talkFaction.def.HasRoyalTitles && diplomat.royalty != null)
                {
                    int amt = DiplomacyTuning.RoyalFavor_PeaceTalksSuccessRange.RandomInRange;
                    diplomat.royalty.GainFavor(talkFaction, amt);
                    return amt;
                }
                return 0;
            }

            FactionRelationKind prevKind = talkFaction?.PlayerRelationKind ?? FactionRelationKind.Neutral;
            PlanetTile talkTile = peaceTalks.Tile;

            if (roll < wDisaster && talkFaction != null)
            {
                int delta = DiplomacyTuning.Goodwill_PeaceTalksDisasterRange.RandomInRange;
                Faction.OfPlayer.TryAffectGoodwillWith(
                    talkFaction,
                    Mathf.Min(delta, Faction.OfPlayer.GoodwillToMakeHostile(talkFaction)),
                    canSendMessage: false,
                    canSendHostilityLetter: false,
                    HistoryEventDefOf.PeaceTalksDisaster);
                Find.LetterStack?.ReceiveLetter(
                    "LetterLabelPeaceTalks_Disaster".Translate(),
                    BuildTalkLetterText(
                        "LetterPeaceTalks_Disaster".Translate(talkFaction.def.pawnsPlural.CapitalizeFirst(), talkFaction.NameColored, Mathf.RoundToInt(delta)),
                        prevKind,
                        0),
                    LetterDefOf.ThreatBig,
                    this,
                    talkFaction);
            }
            else if (roll < wDisaster + wBackfire && talkFaction != null)
            {
                int delta = DiplomacyTuning.Goodwill_PeaceTalksBackfireRange.RandomInRange;
                Faction.OfPlayer.TryAffectGoodwillWith(talkFaction, delta, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.PeaceTalksBackfire);
                Find.LetterStack?.ReceiveLetter(
                    "LetterLabelPeaceTalks_Backfire".Translate(),
                    BuildTalkLetterText("LetterPeaceTalks_Backfire".Translate(talkFaction.NameColored, delta), prevKind, 0),
                    LetterDefOf.NegativeEvent,
                    this,
                    talkFaction);
            }
            else if (roll < wDisaster + wBackfire + wFlounder && talkFaction != null)
            {
                Find.LetterStack?.ReceiveLetter(
                    "LetterLabelPeaceTalks_TalksFlounder".Translate(),
                    BuildTalkLetterText("LetterPeaceTalks_TalksFlounder".Translate(talkFaction.NameColored), prevKind, 0),
                    LetterDefOf.NeutralEvent,
                    this,
                    talkFaction);
            }
            else if (roll < wDisaster + wBackfire + wFlounder + wSuccess && talkFaction != null)
            {
                int delta = DiplomacyTuning.Goodwill_PeaceTalksSuccessRange.RandomInRange;
                Faction.OfPlayer.TryAffectGoodwillWith(talkFaction, delta, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.PeaceTalksSuccess);
                int favor = GainRoyalFavorIfApplicable();
                Find.LetterStack?.ReceiveLetter(
                    "LetterLabelPeaceTalks_Success".Translate(),
                    BuildTalkLetterText("LetterPeaceTalks_Success".Translate(talkFaction.NameColored, delta), prevKind, favor),
                    LetterDefOf.PositiveEvent,
                    this,
                    talkFaction);
            }
            else if (talkFaction != null)
            {
                int delta = DiplomacyTuning.Goodwill_PeaceTalksTriumphRange.RandomInRange;
                Faction.OfPlayer.TryAffectGoodwillWith(talkFaction, delta, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.PeaceTalksTriumph);
                int favor = GainRoyalFavorIfApplicable();
                ThingSetMakerParams parms = new ThingSetMakerParams
                {
                    makingFaction = talkFaction,
                    techLevel = talkFaction.def.techLevel,
                    maxTotalMass = 20f,
                    totalMarketValueRange = new FloatRange(500f, 1200f),
                    tile = talkTile
                };
                List<Thing> rewards = ThingSetMakerDefOf.Reward_ItemsStandard.root.Generate(parms);
                string rewardsLabel = GenLabel.ThingsLabel(rewards);
                var adapter = new WorldGiantSettlementTraderAdapter(null, this);
                for (int i = 0; i < rewards.Count; i++)
                {
                    Thing reward = rewards[i];
                    if (diplomat.inventory?.innerContainer == null || !diplomat.inventory.innerContainer.TryAdd(reward))
                    {
                        if (Pawn?.inventory?.innerContainer == null || !Pawn.inventory.innerContainer.TryAdd(reward))
                        {
                            contents.TryAdd(reward);
                        }
                    }
                }
                Find.LetterStack?.ReceiveLetter(
                    "LetterLabelPeaceTalks_Triumph".Translate(),
                    BuildTalkLetterText("LetterPeaceTalks_Triumph".Translate(talkFaction.NameColored, delta, rewardsLabel), prevKind, favor),
                    LetterDefOf.PositiveEvent,
                    this,
                    talkFaction);
            }

            diplomat.skills?.Learn(SkillDefOf.Social, 6000f, direct: true);
            QuestUtility.SendQuestTargetSignals(peaceTalks.questTags, "Resolved", peaceTalks.Named("SUBJECT"));
            peaceTalks.Destroy();
        }

        private void BeginAttackSettlement(Settlement settlement)
        {
            if (settlement == null || !settlement.Spawned || !settlement.Attackable || settlement.Tile != Tile)
            {
                return;
            }
            if (!settlement.HasMap)
            {
                LongEventHandler.QueueLongEvent(
                    () => AttackSettlementNow(settlement),
                    "GeneratingMapForNewEncounter",
                    doAsynchronously: false,
                    exceptionHandler: null);
            }
            else
            {
                AttackSettlementNow(settlement);
            }
        }

        /// <summary>
        /// Exact vanilla SettlementUtility.AttackNow path adapted for WorldObject_Giant:
        /// uses suggestedMapParentDef: null, applies AffectRelationsOnAttacked BEFORE spawning,
        /// sends vanilla enemy-base &amp; related-pawns letters, drafts the giant, and recalculates goodwill.
        /// </summary>
        private void AttackSettlementNow(Settlement settlement)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer)
            {
                return;
            }
            if (settlement == null || !settlement.Spawned || !settlement.Attackable || settlement.Tile != Tile)
            {
                return;
            }
            if (settlement.EnterCooldownBlocksEntering())
            {
                Messages.Message(
                    "MessageEnterCooldownBlocksEntering".Translate(settlement.EnterCooldownTicksLeft().ToStringTicksToPeriod()),
                    new GlobalTargetInfo(settlement.Tile),
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            bool isNewMap = !settlement.HasMap;
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(settlement.Tile, null);
            if (map == null)
            {
                return;
            }

            Pawn enteringPawn = Pawn;
            string giantLabel = Label;
            TaggedString letterLabel = "LetterLabelCaravanEnteredEnemyBase".Translate();
            TaggedString letterText = "LetterCaravanEnteredEnemyBase".Translate(
                giantLabel,
                settlement.Label.ApplyTag(TagType.Settlement, settlement.Faction.GetUniqueLoadID())).CapitalizeFirst();
            SettlementUtility.AffectRelationsOnAttacked(settlement, ref letterText);
            if (isNewMap)
            {
                Find.TickManager.Notify_GeneratedPotentiallyHostileMap();
                PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter(
                    map.mapPawns.AllPawns,
                    ref letterLabel,
                    ref letterText,
                    "LetterRelatedPawnsSettlement".Translate(Faction.OfPlayer.def.pawnsPlural),
                    informEvenIfSeenBefore: true);
            }

            bool entered = GiantWorldUtility.TryEnterMap(this, map, draftPawn: true);
            if (!entered)
            {
                if (isNewMap)
                {
                    Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
                }
                return;
            }

            Find.LetterStack.ReceiveLetter(
                letterLabel,
                letterText,
                LetterDefOf.NeutralEvent,
                new LookTargets(enteringPawn),
                settlement.Faction);
            Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: true);
        }

        /// <summary>
        /// Exact vanilla CaravanArrivalAction_Enter.Arrived path: enters an already existing map
        /// (player colony or already-open non-attackable MapParent) without generating any new MapParent.
        /// </summary>
        private void EnterExistingMapNow(MapParent mapParent)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer)
            {
                return;
            }
            if (mapParent == null || !mapParent.Spawned || !mapParent.HasMap || mapParent.Tile != Tile)
            {
                return;
            }
            if (mapParent is Settlement settlement && settlement.Attackable)
            {
                BeginAttackSettlement(settlement);
                return;
            }
            Map map = mapParent.Map;
            if (map == null)
            {
                return;
            }

            Pawn enteringPawn = Pawn;
            string giantLabel = Label;
            bool draft = mapParent.Faction != null && mapParent.Faction.HostileTo(Faction.OfPlayer);
            if (!GiantWorldUtility.TryEnterMap(this, map, draftPawn: draft))
            {
                return;
            }
            Find.LetterStack.ReceiveLetter(
                "LetterLabelCaravanEnteredMap".Translate(mapParent),
                "LetterCaravanEnteredMap".Translate(giantLabel, mapParent).CapitalizeFirst(),
                LetterDefOf.NeutralEvent,
                new LookTargets(enteringPawn));
        }

        private void BeginVisitSite(Site site)
        {
            if (site == null || !site.Spawned || site.Tile != Tile)
            {
                return;
            }
            if (!site.HasMap)
            {
                LongEventHandler.QueueLongEvent(
                    () => VisitSiteNow(site),
                    "GeneratingMapForNewEncounter",
                    doAsynchronously: false,
                    exceptionHandler: null);
            }
            else
            {
                VisitSiteNow(site);
            }
        }

        /// <summary>
        /// Exact vanilla CaravanArrivalAction_VisitSite.DoEnter path adapted for WorldObject_Giant:
        /// uses site.PreferredMapSize and suggestedMapParentDef: null, sends site-part threat &amp; relation letters.
        /// </summary>
        private void VisitSiteNow(Site site)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer)
            {
                return;
            }
            if (site == null || !site.Spawned || site.Tile != Tile)
            {
                return;
            }
            if (site.EnterCooldownBlocksEntering())
            {
                Messages.Message(
                    "MessageEnterCooldownBlocksEntering".Translate(site.EnterCooldownTicksLeft().ToStringTicksToPeriod()),
                    new GlobalTargetInfo(site.Tile),
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            bool isNewMap = !site.HasMap;
            bool draft = site.Faction == null || site.Faction.HostileTo(Faction.OfPlayer);
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, site.PreferredMapSize, null);
            if (map == null)
            {
                return;
            }

            Pawn enteringPawn = Pawn;
            string giantLabel = Label;
            LookTargets lookTargets = new LookTargets(enteringPawn);
            if (!GiantWorldUtility.TryEnterMap(this, map, draftPawn: draft))
            {
                if (isNewMap)
                {
                    Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
                }
                return;
            }

            if (isNewMap)
            {
                Find.TickManager.Notify_GeneratedPotentiallyHostileMap();
                PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter_Send(
                    map.mapPawns.AllPawns,
                    "LetterRelatedPawnsSite".Translate(Faction.OfPlayer.def.pawnsPlural),
                    LetterDefOf.NeutralEvent,
                    informEvenIfSeenBefore: true);
                var sb = new StringBuilder();
                sb.Append("LetterCaravanEnteredMap".Translate(giantLabel, site).CapitalizeFirst());
                AppendSiteThreatInfo(sb, site, map, out LetterDef letterDef, out LookTargets allLookTargets);
                TaggedString relText = null;
                if (SiteConsidersEnteringAsAttack(site))
                {
                    SettlementUtility.AffectRelationsOnAttacked(site, ref relText);
                    Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: true);
                }
                if (!relText.NullOrEmpty())
                {
                    if (sb.Length > 0)
                    {
                        if (sb[sb.Length - 1] != '\n')
                        {
                            sb.AppendLine();
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLineTagged(relText);
                }
                List<HediffDef> hyperlinks = null;
                if (site.parts != null)
                {
                    for (int i = 0; i < site.parts.Count; i++)
                    {
                        List<HediffDef> partLinks = site.parts[i]?.def?.arrivedLetterHediffHyperlinks;
                        if (partLinks.NullOrEmpty()) continue;
                        hyperlinks ??= new List<HediffDef>();
                        for (int j = 0; j < partLinks.Count; j++)
                        {
                            if (!hyperlinks.Contains(partLinks[j]))
                            {
                                hyperlinks.Add(partLinks[j]);
                            }
                        }
                    }
                }
                ChoiceLetter letter = LetterMaker.MakeLetter(
                    "LetterLabelCaravanEnteredMap".Translate(site),
                    sb.ToString(),
                    letterDef ?? LetterDefOf.NeutralEvent,
                    allLookTargets.IsValid() ? allLookTargets : lookTargets);
                letter.hyperlinkHediffDefs = hyperlinks;
                Find.LetterStack.ReceiveLetter(letter);
            }
            else
            {
                Find.LetterStack.ReceiveLetter(
                    "LetterLabelCaravanEnteredMap".Translate(site),
                    "LetterCaravanEnteredMap".Translate(giantLabel, site).CapitalizeFirst(),
                    LetterDefOf.NeutralEvent,
                    lookTargets);
            }
        }

        private static void AppendSiteThreatInfo(StringBuilder sb, Site site, Map map, out LetterDef letterDef, out LookTargets allLookTargets)
        {
            allLookTargets = new LookTargets();
            letterDef = null;
            if (site?.parts == null)
            {
                return;
            }
            tmpUsedSitePartDefs.Clear();
            tmpSitePartDefs.Clear();
            for (int i = 0; i < site.parts.Count; i++)
            {
                if (site.parts[i]?.def != null)
                {
                    tmpSitePartDefs.Add(site.parts[i].def);
                }
            }
            List<GlobalTargetInfo> combinedTargets = null;
            for (int i = 0; i < tmpSitePartDefs.Count; i++)
            {
                SitePartDef partDef = tmpSitePartDefs[i];
                SitePartWorker worker = partDef.Worker;
                if (worker == null)
                {
                    continue;
                }
                string arrivedPart = worker.GetArrivedLetterPart(map, out LetterDef preferredLetterDef, out LookTargets partTargets);
                if (arrivedPart == null)
                {
                    continue;
                }
                if (tmpUsedSitePartDefs.Add(partDef))
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine();
                    }
                    sb.Append(arrivedPart);
                }
                if (letterDef == null)
                {
                    letterDef = preferredLetterDef;
                }
                if (partTargets.IsValid() && partTargets.targets != null)
                {
                    combinedTargets ??= new List<GlobalTargetInfo>();
                    combinedTargets.AddRange(partTargets.targets);
                }
            }
            if (combinedTargets != null && combinedTargets.Count > 0)
            {
                allLookTargets = new LookTargets(combinedTargets);
            }
            tmpUsedSitePartDefs.Clear();
            tmpSitePartDefs.Clear();
        }

        private void BeginVisitEscapeShip(MapParent escapeShip)
        {
            if (escapeShip == null || !escapeShip.Spawned || escapeShip.Tile != Tile)
            {
                return;
            }
            if (!escapeShip.HasMap)
            {
                LongEventHandler.QueueLongEvent(
                    () => VisitEscapeShipNow(escapeShip),
                    "GeneratingMapForNewEncounter",
                    doAsynchronously: false,
                    exceptionHandler: null);
            }
            else
            {
                VisitEscapeShipNow(escapeShip);
            }
        }

        private void VisitEscapeShipNow(MapParent escapeShip)
        {
            if (!Spawned || Moving || Pawn == null || Pawn.Dead || Faction != Faction.OfPlayer)
            {
                return;
            }
            if (escapeShip == null || !escapeShip.Spawned || escapeShip.Tile != Tile)
            {
                return;
            }
            bool isNewMap = !escapeShip.HasMap;
            if (isNewMap)
            {
                escapeShip.SetFaction(Faction.OfPlayer);
            }
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(escapeShip.Tile, null);
            if (map == null)
            {
                return;
            }

            Pawn enteringPawn = Pawn;
            string giantLabel = Label;
            if (!GiantWorldUtility.TryEnterMap(this, map, draftPawn: false))
            {
                if (isNewMap)
                {
                    Current.Game.DeinitAndRemoveMap(map, notifyPlayer: false);
                }
                return;
            }

            if (isNewMap)
            {
                Find.TickManager.Notify_GeneratedPotentiallyHostileMap();
                Find.LetterStack.ReceiveLetter(
                    "EscapeShipFoundLabel".Translate(),
                    (!Find.Storyteller.difficulty.allowBigThreats) ? "EscapeShipFoundPeaceful".Translate() : "EscapeShipFound".Translate(),
                    LetterDefOf.PositiveEvent,
                    new GlobalTargetInfo(map.Center, map));
            }
            else
            {
                Find.LetterStack.ReceiveLetter(
                    "LetterLabelCaravanEnteredMap".Translate(escapeShip),
                    "LetterCaravanEnteredMap".Translate(giantLabel, escapeShip).CapitalizeFirst(),
                    LetterDefOf.NeutralEvent,
                    new LookTargets(enteringPawn));
            }
        }

        public void ReleasePawnToMap(Map map, IntVec3 cell)
        {
            Pawn pawn = Pawn;
            Thing entity = pawn?.Corpse != null && contents.Contains(pawn.Corpse) ? pawn.Corpse : pawn;
            if (entity == null) return;
            contents.Remove(entity);
            GiantFastLookup.TryGetRig(pawn)?.EndWorldSimulation();
            try
            {
                GenSpawn.Spawn(entity, cell, map, Rot4.FromAngleFlat(heading), WipeMode.Vanish);
            }
            catch
            {
                if (!entity.Spawned) contents.TryAddOrTransfer(entity);
                GiantFastLookup.TryGetRig(pawn)?.BeginWorldSimulation();
                throw;
            }
            Stop();
            renderer?.Dispose();
            renderer = null;
            Destroy();
            Current.Game.CurrentMap = map;
            Find.World.renderer.wantedMode = WorldRenderMode.None;
            CameraJumper.TryJumpAndSelect(entity);
        }

        public override void PostRemove()
        {
            activeWorldGiants.Remove(this);
            if (ModsConfig.OdysseyActive && Find.World?.landmarks != null)
            {
                ExpandableLandmarksUtility.Notify_WorldObjectsChanged();
            }
            renderer?.Dispose();
            renderer = null;
            base.PostRemove();
        }

        public override void Destroy()
        {
            if (contents.Count != 0) return;
            renderer?.Dispose();
            renderer = null;
            base.Destroy();
        }
    }
}
