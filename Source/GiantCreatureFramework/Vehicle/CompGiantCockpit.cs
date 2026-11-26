using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace GiantCreatureFramework
{
    public enum GiantCockpitControlMode
    {
        Dormant = 0,
        Autonomous = 1,
        Piloted = 2
    }

    public enum GiantCockpitRole
    {
        PrimaryPilot = 0,
        Gunner = 1,
        Commander = 2,
        Passenger = 3
    }

    public class GiantCockpitSeatDef
    {
        public string seatId = "MainCockpit";
        public GiantCockpitRole role = GiantCockpitRole.PrimaryPilot;
        public string labelKey;
        public string partLabelKey;
        public BodyPartDef linkedBodyPart;
        public string linkedBodyPartCustomLabel;
        public int linkedBodyPartIndex = -1;
        public float bleedthroughFactor = 0.08f;
        public int violentEjectStunTicks = 180;
        public float violentEjectBluntDamage = 6f;
        public float comfortLevel = 0.72f;

        /// <summary>
        /// Whether this seat's boarded occupant should be visually rendered on the rig when
        /// CompProperties_GiantCockpit.renderCrewOnRig is true.
        /// </summary>
        public bool renderOccupant = true;

        /// <summary>
        /// Optional explicit BoneNodeDef &lt;crewSeats&gt; seatId to bind to; defaults to seatId when omitted.
        /// </summary>
        public string rigSeatId;

        public string EffectiveRigSeatId => !string.IsNullOrEmpty(rigSeatId) ? rigSeatId : seatId;

        public string GetLocalizedRoleLabel()
        {
            if (!string.IsNullOrEmpty(labelKey) && labelKey.CanTranslate())
            {
                return labelKey.Translate().ToString();
            }

            string roleKey = role switch
            {
                GiantCockpitRole.PrimaryPilot => "GCF_CockpitRole_PrimaryPilot",
                GiantCockpitRole.Gunner => "GCF_CockpitRole_Gunner",
                GiantCockpitRole.Commander => "GCF_CockpitRole_Commander",
                _ => "GCF_CockpitRole_Passenger"
            };

            if (roleKey.CanTranslate())
            {
                return roleKey.Translate().ToString();
            }

            return role switch
            {
                GiantCockpitRole.PrimaryPilot => "Pilot",
                GiantCockpitRole.Gunner => "Gunner",
                GiantCockpitRole.Commander => "Commander",
                _ => "Passenger"
            };
        }
    }

    public class CompProperties_GiantCockpit : CompProperties
    {
        public bool autonomousWhenEmpty = true;
        public bool allowHostileAutonomousFallback = true;
        public bool allowPlayerControlWithoutOverseer = true;
        public bool requireOverseerWhenBiotechActive = false;
        public bool requireDedicatedGunner = false;
        public bool requireDedicatedCommanderForAbilities = false;
        public BodyPartDef autonomousCoreBodyPart;

        /// <summary>
        /// When true (default for CompProperties_GiantCockpit on vehicles/mechs), classifies the host Pawn as a
        /// mechanical vehicle in GiantVehicleBehaviorGuard (suppressing biological Food/Rest needs, wild wander, etc.).
        /// Set false (default on CompProperties_GiantCrew) for biological saddled mounts so natural creature needs,
        /// ambient calls, and creature ThinkTrees remain completely intact.
        /// </summary>
        public bool treatHostAsVehicle = true;

        /// <summary>
        /// Feature toggle enabling full unoccluded PawnRenderTree rendering of boarded crew members directly above
        /// their corresponding BoneNodeDef &lt;crewSeats&gt; mounts, tracking 100% of bone FK offsets, gait, turns, and flight.
        /// </summary>
        public bool renderCrewOnRig = false;

        public float autonomousMoveSpeedFactor = 0.85f;
        public float autonomousAccuracyFactor = 0.88f;
        public float autonomousReloadFactor = 0.88f;

        public float pilotedMinMoveSpeedFactor = 0.85f;
        public float pilotedMaxMoveSpeedFactor = 1.30f;
        public float pilotedMinAccuracyFactor = 0.85f;
        public float pilotedMaxAccuracyFactor = 1.36f;
        public float pilotedMinReloadFactor = 0.85f;
        public float pilotedMaxReloadFactor = 1.34f;
        public float commanderFireControlBonus = 0.08f;

        public int enterTicks = 55;
        public List<GiantCockpitSeatDef> seats = new List<GiantCockpitSeatDef>();

        public CompProperties_GiantCockpit()
        {
            compClass = typeof(CompGiantCockpit);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string err in base.ConfigErrors(parentDef))
            {
                yield return err;
            }

            if (seats == null || seats.Count == 0)
            {
                yield return $"CompProperties_GiantCockpit on '{parentDef?.defName}' defines zero seats.";
                yield break;
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BodyDef body = parentDef?.race?.body;
            for (int i = 0; i < seats.Count; i++)
            {
                GiantCockpitSeatDef seat = seats[i];
                if (seat == null)
                {
                    yield return $"CompProperties_GiantCockpit on '{parentDef?.defName}' has null seat at index {i}.";
                    continue;
                }
                if (string.IsNullOrWhiteSpace(seat.seatId))
                {
                    yield return $"CompProperties_GiantCockpit on '{parentDef?.defName}' has empty seatId at index {i}.";
                }
                else if (!seenIds.Add(seat.seatId))
                {
                    yield return $"CompProperties_GiantCockpit on '{parentDef?.defName}' has duplicate seatId '{seat.seatId}'.";
                }

                if (seat.linkedBodyPart != null && body != null)
                {
                    BodyPartRecord matched = GiantRigDef.ResolveLinkedBodyPartRecord(
                        body,
                        seat.linkedBodyPart,
                        seat.linkedBodyPartCustomLabel,
                        seat.linkedBodyPartIndex);
                    if (matched == null)
                    {
                        yield return $"CompProperties_GiantCockpit seat '{seat.seatId}' on '{parentDef?.defName}' could not resolve linkedBodyPart '{seat.linkedBodyPart.defName}'.";
                    }
                }
            }

            if (renderCrewOnRig && parentDef != null)
            {
                GiantRigDef rigDef = parentDef.GetCompProperties<CompProperties_GiantRig>()?.rigDef;
                if (rigDef == null)
                {
                    yield return $"CompProperties_GiantCockpit on '{parentDef.defName}' enables <renderCrewOnRig>true</renderCrewOnRig>, which requires CompProperties_GiantRig with a valid rigDef.";
                }
                else
                {
                    rigDef.EnsureTopologyBuilt();
                    for (int i = 0; i < seats.Count; i++)
                    {
                        GiantCockpitSeatDef seat = seats[i];
                        if (seat == null || !seat.renderOccupant)
                        {
                            continue;
                        }
                        string mountId = seat.EffectiveRigSeatId;
                        if (!rigDef.TryGetCrewSeatMount(mountId, out _, out _))
                        {
                            yield return $"CompProperties_GiantCockpit seat '{seat.seatId}' on '{parentDef.defName}' enables occupant rendering (<renderCrewOnRig>true</renderCrewOnRig>), but GiantRigDef '{rigDef.defName}' has no matching <crewSeats> entry with seatId='{mountId}'.";
                        }
                    }
                }
            }
        }
    }

    public class CompProperties_GiantCrew : CompProperties_GiantCockpit
    {
        public CompProperties_GiantCrew()
        {
            compClass = typeof(CompGiantCrew);
            treatHostAsVehicle = false;
            renderCrewOnRig = true;
            autonomousMoveSpeedFactor = 1.0f;
            autonomousAccuracyFactor = 1.0f;
            autonomousReloadFactor = 1.0f;
        }
    }

    [StaticConstructorOnStartup]
    internal static class GiantCockpitGizmoAssets
    {
        public static readonly Texture2D BoardIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", reportFailure: false)
            ?? ContentFinder<Texture2D>.Get("UI/Icons/GCF_DebugGizmo", reportFailure: false)
            ?? BaseContent.BadTex;

        public static readonly Texture2D DisembarkIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/PodEject", reportFailure: false)
            ?? ContentFinder<Texture2D>.Get("UI/Icons/GCF_DebugGizmo", reportFailure: false)
            ?? BaseContent.BadTex;

        public static readonly Texture2D AutonomousIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/Draft", reportFailure: false)
            ?? ContentFinder<Texture2D>.Get("UI/Icons/GCF_DebugGizmo", reportFailure: false)
            ?? BaseContent.BadTex;

        public static readonly Texture2D BarBgTex =
            SolidColorMaterials.NewSolidColorTexture(new Color(0.11f, 0.13f, 0.16f, 0.95f));

        public static readonly Texture2D BarHpFillTex =
            SolidColorMaterials.NewSolidColorTexture(new Color(0.24f, 0.76f, 0.88f, 0.92f));

        public static readonly Texture2D BarDamagedFillTex =
            SolidColorMaterials.NewSolidColorTexture(new Color(0.92f, 0.56f, 0.18f, 0.92f));

        public static readonly Texture2D BarSeveredFillTex =
            SolidColorMaterials.NewSolidColorTexture(new Color(0.72f, 0.18f, 0.20f, 0.92f));

        public static readonly Texture2D CardBgTex =
            SolidColorMaterials.NewSolidColorTexture(new Color(0.15f, 0.17f, 0.21f, 0.92f));
    }

    public static class GiantCockpitPercentCache
    {
        private static readonly string[] CachedPercentStrings = BuildCache();

        private static string[] BuildCache()
        {
            var arr = new string[251];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = i.ToString() + "%";
            }
            return arr;
        }

        public static string FormatMultiplierPct(float mult)
        {
            int pct = Mathf.Clamp(Mathf.RoundToInt(mult * 100f), 0, 250);
            return CachedPercentStrings[pct];
        }

        public static string FormatRatio01Pct(float ratio01)
        {
            int pct = Mathf.Clamp(Mathf.RoundToInt(ratio01 * 100f), 0, 100);
            return CachedPercentStrings[pct];
        }
    }

    public sealed class ThinkNode_GiantDormantNull : ThinkNode
    {
        public static readonly ThinkNode_GiantDormantNull Instance = new ThinkNode_GiantDormantNull();

        public ThinkNode_GiantDormantNull()
        {
            subNodes = new List<ThinkNode>(0);
        }

        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            return ThinkResult.NoJob;
        }
    }

    public class CompGiantCockpit : ThingComp, IThingHolder
    {
        public CompProperties_GiantCockpit Props => (CompProperties_GiantCockpit)props;
        public Pawn HostPawn => parent as Pawn;

        private ThingOwner<Thing> innerContainer;
        private ThingOwner<Pawn> legacyInnerCrew;
        private List<string> scribeSeatIds;
        private List<Pawn> scribeSeatPawns;
        private Pawn[] seatOccupants = Array.Empty<Pawn>();
        private BodyPartRecord[] linkedSeatParts = Array.Empty<BodyPartRecord>();
        private BodyPartRecord autonomousCorePart;
        private bool bodyPartsResolved;

        private bool parkedWhenEmpty;
        private bool suppressEjectReentry;

        [ThreadStatic]
        internal static Pawn CurrentlyBoardingPawn;

        private bool statsDirty = true;
        private int lastStatsRecacheTick = -999;
        private float cachedMoveMult = 1f;
        private float cachedAccuracyMult = 1f;
        private float cachedReloadMult = 1f;
        internal int cachedRequiredBandwidth = -1;
        internal int lastOverseerCheckTick = -999;
        internal bool cachedOverseerValid = true;

        public CompGiantCockpit()
        {
            innerContainer = new ThingOwner<Thing>(this, oneStackOnly: false, LookMode.Deep);
        }

        public int SeatCount => Props.seats?.Count ?? 0;
        public int CrewCount
        {
            get
            {
                if (innerContainer == null || innerContainer.Count == 0)
                {
                    return 0;
                }
                int count = 0;
                for (int i = 0; i < innerContainer.Count; i++)
                {
                    if (innerContainer[i] is Pawn p && !p.Dead && !p.Destroyed)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        public bool ParkedWhenEmpty => parkedWhenEmpty;

        public bool HasFreeSeat
        {
            get
            {
                EnsureSeatArrays();
                return FindFirstOpenIntactSeatIndex() >= 0;
            }
        }

        public IReadOnlyList<Pawn> BoardedCrew
        {
            get
            {
                if (innerContainer == null || innerContainer.Count == 0)
                {
                    return Array.Empty<Pawn>();
                }
                var list = new List<Pawn>(innerContainer.Count);
                for (int i = 0; i < innerContainer.Count; i++)
                {
                    if (innerContainer[i] is Pawn p && !p.Dead && !p.Destroyed)
                    {
                        list.Add(p);
                    }
                }
                return list;
            }
        }

        public bool TryAcceptCrewImmediate(Pawn boardingPawn)
        {
            EnsureSeatArrays();
            Pawn host = HostPawn;
            if (boardingPawn == null || boardingPawn.Dead || boardingPawn.Destroyed || !boardingPawn.RaceProps.Humanlike
                || innerContainer == null || innerContainer.Contains(boardingPawn))
            {
                return false;
            }
            if (host == null || host.Dead || host.Downed || boardingPawn == host || GiantFastLookup.TryGetRig(boardingPawn) != null)
            {
                return false;
            }
            int openIdx = FindFirstOpenIntactSeatIndex();
            if (openIdx < 0)
            {
                return false;
            }
            if (boardingPawn.Spawned)
            {
                boardingPawn.DeSpawn(DestroyMode.Vanish);
            }
            bool accepted = boardingPawn.holdingOwner != null
                ? boardingPawn.holdingOwner.TryTransferToContainer(boardingPawn, innerContainer, canMergeWithExistingStacks: false)
                : innerContainer.TryAdd(boardingPawn, canMergeWithExistingStacks: false);
            if (!accepted)
            {
                return false;
            }
            seatOccupants[openIdx] = boardingPawn;
            statsDirty = true;
            SyncMapOccupancy();
            return true;
        }

        public GiantCockpitControlMode CurrentMode
        {
            get
            {
                Pawn host = HostPawn;
                if (host != null && GiantDormantLockRegistry.IsLocked(host))
                {
                    return GiantCockpitControlMode.Dormant;
                }

                if (host != null && GiantVehicleBehaviorGuard.IsPlayerFaction(host.Faction) &&
                    !GiantPlayerControlUtility.HasRequiredBiotechOverseer(host, this))
                {
                    return GiantCockpitControlMode.Dormant;
                }

                if (HasControllingOccupant())
                {
                    return GiantCockpitControlMode.Piloted;
                }

                Faction playerFac = GiantVehicleBehaviorGuard.GetSafePlayerFaction();
                if (host != null && host.Faction != null && playerFac != null &&
                    host.Faction.HostileTo(playerFac) && Props.allowHostileAutonomousFallback)
                {
                    return IsAutonomousCoreIntact()
                        ? GiantCockpitControlMode.Autonomous
                        : GiantCockpitControlMode.Dormant;
                }

                if (Props.autonomousWhenEmpty && !parkedWhenEmpty && IsAutonomousCoreIntact())
                {
                    return GiantCockpitControlMode.Autonomous;
                }

                return GiantCockpitControlMode.Dormant;
            }
        }

        public bool IsDormantUnmanned => CurrentMode == GiantCockpitControlMode.Dormant;
        public bool IsAutonomousActive => CurrentMode == GiantCockpitControlMode.Autonomous;
        public bool IsPiloted => CurrentMode == GiantCockpitControlMode.Piloted;

        public bool ShouldNullifyThinkTree
        {
            get
            {
                Pawn host = HostPawn;
                return host != null && !host.Dead && !host.Downed && IsDormantUnmanned;
            }
        }

        public bool ShouldHoldPositionWhenPilotedUndrafted
        {
            get
            {
                Pawn host = HostPawn;
                if (host == null || (Current.Game != null && !host.Spawned) || host.Dead || host.Downed || !IsPiloted)
                {
                    return false;
                }
                if (!GiantVehicleBehaviorGuard.IsPlayerFaction(host.Faction) || host.Drafted)
                {
                    return false;
                }
                if (host.jobs?.jobQueue != null && host.jobs.jobQueue.Count > 0)
                {
                    return false;
                }
                if (host.mindState != null && host.mindState.forcedGotoPosition.IsValid)
                {
                    return false;
                }
                return true;
            }
        }

        public bool IsAutonomousPlayerControlledVehicle
        {
            get
            {
                Pawn host = HostPawn;
                return host != null
                    && Props.treatHostAsVehicle
                    && host.Faction == Faction.OfPlayer
                    && Props.allowPlayerControlWithoutOverseer
                    && !GiantDormantLockRegistry.IsLocked(host)
                    && GiantPlayerControlUtility.HasRequiredBiotechOverseer(host, this)
                    && !IsDormantUnmanned;
            }
        }

        public bool CanFirePrimaryWeapons
        {
            get
            {
                GiantCockpitControlMode mode = CurrentMode;
                if (mode == GiantCockpitControlMode.Dormant)
                {
                    return false;
                }
                if (mode == GiantCockpitControlMode.Autonomous)
                {
                    return true;
                }
                if (Props.requireDedicatedGunner)
                {
                    return GetActiveOccupantForRole(GiantCockpitRole.Gunner) != null;
                }
                return true;
            }
        }

        public bool CanActivateAbilities
        {
            get
            {
                GiantCockpitControlMode mode = CurrentMode;
                if (mode == GiantCockpitControlMode.Dormant)
                {
                    return false;
                }
                if (mode == GiantCockpitControlMode.Autonomous)
                {
                    return true;
                }
                if (Props.requireDedicatedCommanderForAbilities)
                {
                    return GetActiveOccupantForRole(GiantCockpitRole.Commander) != null;
                }
                return true;
            }
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public ThingOwner GetDirectlyHeldThings()
        {
            return innerContainer;
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            EnsureSeatArrays();
            GiantFastLookup.RegisterCockpit(HostPawn, this);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureSeatArrays();
            ResolveSeatBodyPartLinks();
            ReconcileContainerAndSeats();
            statsDirty = true;
            GiantFastLookup.RegisterCockpit(HostPawn, this);
            SyncMapOccupancy();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            if (mode == DestroyMode.KillFinalize || mode == DestroyMode.Deconstruct)
            {
                EjectAllInternal(violent: true, mapOverride: map, bypassFlightBlock: true);
            }
            GiantFootprintMapComponent.GetFast(map)?.UnregisterCockpit(this);
            base.PostDeSpawn(map, mode);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            if (innerContainer != null && innerContainer.Count > 0)
            {
                EjectAllInternal(violent: true, mapOverride: previousMap, bypassFlightBlock: true);
            }
            GiantFootprintMapComponent.GetFast(previousMap)?.UnregisterCockpit(this);
            if (HostPawn == null || HostPawn.Corpse == null || HostPawn.Discarded)
            {
                GiantFastLookup.UnregisterCockpit(HostPawn);
            }
            base.PostDestroy(mode, previousMap);
        }

        public override void PrePreTraded(TradeAction action, Pawn playerNegotiator, ITrader trader)
        {
            base.PrePreTraded(action, playerNegotiator, trader);
            if (innerContainer != null && innerContainer.Count > 0)
            {
                Map map = HostPawn?.MapHeld ?? playerNegotiator?.MapHeld;
                EjectAllInternal(violent: false, mapOverride: map, bypassFlightBlock: true);
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (parent != null && parent.Spawned && innerContainer != null && innerContainer.Count > 0)
            {
                innerContainer.DoTick();
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref parkedWhenEmpty, "gcf_parkedWhenEmpty", defaultValue: false);

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (innerContainer == null)
                {
                    innerContainer = new ThingOwner<Thing>(this, oneStackOnly: false, LookMode.Deep);
                }
                Scribe_Deep.Look(ref innerContainer, "gcf_innerCrewThings", this);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (Scribe.loader?.curXmlParent?["gcf_innerCrewThings"] != null)
                {
                    Scribe_Deep.Look(ref innerContainer, "gcf_innerCrewThings", this);
                }
                else if (Scribe.loader?.curXmlParent?["gcf_innerCrew"] != null)
                {
                    Scribe_Deep.Look(ref legacyInnerCrew, "gcf_innerCrew", this);
                }
            }

            if (innerContainer == null)
            {
                innerContainer = new ThingOwner<Thing>(this, oneStackOnly: false, LookMode.Deep);
            }

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                EnsureSeatArrays();
                scribeSeatIds = new List<string>();
                scribeSeatPawns = new List<Pawn>();
                for (int i = 0; i < seatOccupants.Length && i < Props.seats.Count; i++)
                {
                    Pawn occ = seatOccupants[i];
                    if (occ != null && !occ.Dead && !occ.Destroyed && innerContainer.Contains(occ))
                    {
                        scribeSeatIds.Add(Props.seats[i].seatId);
                        scribeSeatPawns.Add(occ);
                    }
                }
            }

            Scribe_Collections.Look(ref scribeSeatIds, "gcf_seatIds", LookMode.Value);
            Scribe_Collections.Look(ref scribeSeatPawns, "gcf_seatPawns", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                scribeSeatIds = null;
                scribeSeatPawns = null;
            }
            else if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (legacyInnerCrew != null)
                {
                    legacyInnerCrew.TryTransferAllToContainer(innerContainer);
                    legacyInnerCrew = null;
                }

                EnsureSeatArrays();
                Array.Clear(seatOccupants, 0, seatOccupants.Length);
                if (scribeSeatIds != null && scribeSeatPawns != null)
                {
                    int n = Mathf.Min(scribeSeatIds.Count, scribeSeatPawns.Count);
                    for (int i = 0; i < n; i++)
                    {
                        int sIdx = FindSeatIndexById(scribeSeatIds[i]);
                        Pawn p = scribeSeatPawns[i];
                        if (sIdx >= 0 && p != null && !p.Dead && !p.Destroyed && innerContainer.Contains(p))
                        {
                            seatOccupants[sIdx] = p;
                        }
                    }
                }
                scribeSeatIds = null;
                scribeSeatPawns = null;
                ReconcileContainerAndSeats(ejectCorpsesOnMap: false);
                statsDirty = true;
            }
        }

        private void EnsureSeatArrays()
        {
            int count = SeatCount;
            if (seatOccupants.Length != count)
            {
                var resized = new Pawn[count];
                Array.Copy(seatOccupants, resized, Mathf.Min(seatOccupants.Length, count));
                seatOccupants = resized;
            }
            if (linkedSeatParts.Length != count)
            {
                linkedSeatParts = new BodyPartRecord[count];
                bodyPartsResolved = false;
            }
        }

        public void ResolveSeatBodyPartLinks()
        {
            EnsureSeatArrays();
            BodyDef body = HostPawn?.RaceProps?.body;
            if (body == null)
            {
                return;
            }

            autonomousCorePart = Props.autonomousCoreBodyPart != null
                ? GiantRigDef.ResolveLinkedBodyPartRecord(body, Props.autonomousCoreBodyPart)
                : null;

            for (int i = 0; i < SeatCount; i++)
            {
                GiantCockpitSeatDef seatDef = Props.seats[i];
                linkedSeatParts[i] = seatDef?.linkedBodyPart != null
                    ? GiantRigDef.ResolveLinkedBodyPartRecord(
                        body,
                        seatDef.linkedBodyPart,
                        seatDef.linkedBodyPartCustomLabel,
                        seatDef.linkedBodyPartIndex)
                    : null;
            }

            bodyPartsResolved = true;
        }

        private bool ReconcileContainerAndSeats(bool ejectCorpsesOnMap = true)
        {
            EnsureSeatArrays();
            bool changed = false;
            for (int i = 0; i < seatOccupants.Length; i++)
            {
                Pawn occ = seatOccupants[i];
                if (occ != null && (occ.Dead || occ.Destroyed || innerContainer == null || !innerContainer.Contains(occ)))
                {
                    seatOccupants[i] = null;
                    changed = true;
                }
            }

            if (innerContainer == null)
            {
                return changed;
            }

            if (ejectCorpsesOnMap && HostPawn != null && HostPawn.Spawned && HostPawn.Map != null)
            {
                Map map = HostPawn.Map;
                for (int c = innerContainer.Count - 1; c >= 0; c--)
                {
                    if (innerContainer[c] is Corpse corpse)
                    {
                        if (TryDropThingSafelyOnMap(corpse, HostPawn, map, allowWideSearch: true, out _))
                        {
                            changed = true;
                        }
                    }
                }
            }

            for (int c = 0; c < innerContainer.Count; c++)
            {
                if (!(innerContainer[c] is Pawn p) || p.Dead || p.Destroyed || IndexOfOccupant(p) >= 0)
                {
                    continue;
                }
                int open = FindFirstOpenIntactSeatIndex();
                if (open >= 0)
                {
                    seatOccupants[open] = p;
                    changed = true;
                }
            }
            return changed;
        }

        public bool IsAutonomousCoreIntact()
        {
            if (Props.autonomousCoreBodyPart == null)
            {
                return true;
            }
            if (!bodyPartsResolved)
            {
                ResolveSeatBodyPartLinks();
            }
            Pawn host = HostPawn;
            if (host?.health?.hediffSet == null || autonomousCorePart == null)
            {
                return true;
            }
            return !host.health.hediffSet.PartIsMissing(autonomousCorePart);
        }

        public bool IsSeatPartIntact(int seatIndex)
        {
            if ((uint)seatIndex >= (uint)SeatCount)
            {
                return false;
            }
            if (!bodyPartsResolved)
            {
                ResolveSeatBodyPartLinks();
            }
            BodyPartRecord rec = linkedSeatParts[seatIndex];
            Pawn host = HostPawn;
            if (rec == null || host?.health?.hediffSet == null)
            {
                return true;
            }
            return !host.health.hediffSet.PartIsMissing(rec);
        }

        public BodyPartRecord GetSeatBodyPartRecord(int seatIndex)
        {
            if (!bodyPartsResolved)
            {
                ResolveSeatBodyPartLinks();
            }
            return (uint)seatIndex < (uint)linkedSeatParts.Length ? linkedSeatParts[seatIndex] : null;
        }

        public float GetSeatPartHealthPct(int seatIndex)
        {
            BodyPartRecord rec = GetSeatBodyPartRecord(seatIndex);
            Pawn host = HostPawn;
            if (rec == null || host?.health?.hediffSet == null)
            {
                return 1f;
            }
            if (host.health.hediffSet.PartIsMissing(rec))
            {
                return 0f;
            }
            float maxHp = Mathf.Max(1f, rec.def.GetMaxHealth(host));
            return Mathf.Clamp01(host.health.hediffSet.GetPartHealth(rec) / maxHp);
        }

        public Pawn GetOccupantAt(int seatIndex)
        {
            EnsureSeatArrays();
            return (uint)seatIndex < (uint)seatOccupants.Length ? seatOccupants[seatIndex] : null;
        }

        public int IndexOfOccupant(Pawn pawn)
        {
            if (pawn == null)
            {
                return -1;
            }
            EnsureSeatArrays();
            for (int i = 0; i < seatOccupants.Length; i++)
            {
                if (seatOccupants[i] == pawn)
                {
                    return i;
                }
            }
            return -1;
        }

        public int FindSeatIndexById(string seatId)
        {
            if (string.IsNullOrEmpty(seatId) || Props.seats == null)
            {
                return -1;
            }
            for (int i = 0; i < Props.seats.Count; i++)
            {
                if (Props.seats[i] != null && string.Equals(Props.seats[i].seatId, seatId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        public int FindFirstOpenIntactSeatIndex()
        {
            EnsureSeatArrays();
            // Priority order: PrimaryPilot -> Gunner -> Commander -> Passenger
            GiantCockpitRole[] priority =
            {
                GiantCockpitRole.PrimaryPilot,
                GiantCockpitRole.Gunner,
                GiantCockpitRole.Commander,
                GiantCockpitRole.Passenger
            };
            for (int r = 0; r < priority.Length; r++)
            {
                for (int i = 0; i < SeatCount; i++)
                {
                    if (Props.seats[i].role == priority[r] && seatOccupants[i] == null && IsSeatPartIntact(i))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        public bool HasAvailableIntactSeat => FindFirstOpenIntactSeatIndex() >= 0;

        private bool HasControllingOccupant()
        {
            EnsureSeatArrays();
            bool anyPrimaryDefined = false;
            for (int i = 0; i < SeatCount; i++)
            {
                if (Props.seats[i].role == GiantCockpitRole.PrimaryPilot)
                {
                    anyPrimaryDefined = true;
                    Pawn occ = seatOccupants[i];
                    if (occ != null && !occ.Dead && !occ.Downed)
                    {
                        return true;
                    }
                }
            }

            if (!anyPrimaryDefined)
            {
                for (int i = 0; i < SeatCount; i++)
                {
                    Pawn occ = seatOccupants[i];
                    if (occ != null && !occ.Dead && !occ.Downed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public Pawn GetActiveOccupantForRole(GiantCockpitRole role)
        {
            EnsureSeatArrays();
            for (int i = 0; i < SeatCount; i++)
            {
                if (Props.seats[i].role == role)
                {
                    Pawn occ = seatOccupants[i];
                    if (occ != null && !occ.Dead && !occ.Downed)
                    {
                        return occ;
                    }
                }
            }
            return null;
        }

        public void SetParkedWhenEmpty(bool parked)
        {
            parkedWhenEmpty = parked;
            statsDirty = true;
            Pawn host = HostPawn;
            if (IsDormantUnmanned && host != null)
            {
                if (host.drafter != null && host.drafter.Drafted)
                {
                    host.drafter.Drafted = false;
                }
                StopAutonomousMotionAndJobs(host);
            }
        }

        public static void StopAutonomousMotionAndJobs(Pawn host)
        {
            if (host == null || !host.Spawned || host.Dead)
            {
                return;
            }
            if (host.pather != null && host.pather.Moving)
            {
                host.pather.StopDead();
            }
            if (host.jobs?.curJob != null &&
                host.jobs.curJob.def != JobDefOf.Wait_MaintainPosture &&
                host.jobs.curJob.def != JobDefOf.Wait)
            {
                host.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
        }

        public bool IsFlightStateBlockingBoardingOrDisembark(out string failReason)
        {
            failReason = null;
            Pawn host = HostPawn;
            CompGiantRig rig = host != null ? GiantFastLookup.TryGetRig(host) : null;
            GiantFlightRuntime flight = rig?.FlightLocomotion;
            if (flight == null || !flight.Def.IsActive)
            {
                return false;
            }

            // HighAirborne: never allow boarding or manual disembark while airborne, taking off, or landing
            if (flight.Def.altitudeTier == FlightAltitudeTier.HighAirborne)
            {
                if (flight.IsAirborneOrTakingOff || flight.FlightState == GiantFlightState.Landing)
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_HostAirborne",
                        "Host is airborne (must land first)");
                    return true;
                }
                return false;
            }

            // LowSkim + TakeoffAndLanding: must be grounded to board or disembark
            if (flight.Def.flightMode == GiantFlightMode.TakeoffAndLanding)
            {
                if (flight.IsAirborneOrTakingOff || flight.FlightState == GiantFlightState.Landing)
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_HostAirborne",
                        "Host is airborne (must land first)");
                    return true;
                }
                return false;
            }

            // AlwaysAirborne + LowSkim (e.g. hover-skimming heavy mechs): allow boarding/disembarking when over standable dry ground
            if (host != null && host.Spawned && host.Map != null)
            {
                IntVec3 pos = host.Position;
                if (!pos.InBounds(host.Map) || !pos.Standable(host.Map))
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_UnsafeHoverTerrain",
                        "Host must hover over standable dry ground");
                    return true;
                }
                TerrainDef terrain = host.Map.terrainGrid?.TerrainAt(pos);
                if (terrain != null && terrain.passability == Traversability.Impassable)
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_UnsafeHoverTerrain",
                        "Host must hover over standable dry ground");
                    return true;
                }
                Building edifice = pos.GetEdifice(host.Map);
                if (edifice != null && edifice.def.passability == Traversability.Impassable)
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_UnsafeHoverTerrain",
                        "Host must hover over standable dry ground");
                    return true;
                }
            }

            return false;
        }

        public bool CanPawnBoard(Pawn candidate, out string failReason, int preferredSeatIndex = -1)
        {
            failReason = null;
            Pawn host = HostPawn;
            if (host == null || host.Dead || host.Downed || !host.Spawned)
            {
                failReason = GiantFlightUtility.SafeTranslate(
                    "GCF_CockpitFail_HostUnavailable",
                    "Host unit unavailable");
                return false;
            }
            if (candidate == null || candidate.Dead || candidate.Destroyed || candidate.Downed || !candidate.RaceProps.Humanlike)
            {
                failReason = GiantFlightUtility.SafeTranslate(
                    "GCF_CockpitFail_InvalidCrew",
                    "Cannot board");
                return false;
            }
            if (candidate == host || GiantFastLookup.TryGetRig(candidate) != null)
            {
                return false;
            }
            if (innerContainer != null && innerContainer.Contains(candidate))
            {
                return false;
            }
            if (host.Faction != Faction.OfPlayer || candidate.Faction != Faction.OfPlayer)
            {
                failReason = GiantFlightUtility.SafeTranslate(
                    "GCF_CockpitFail_WrongFaction",
                    "Not player controlled");
                return false;
            }
            if (IsFlightStateBlockingBoardingOrDisembark(out string flightBlockReason))
            {
                failReason = flightBlockReason;
                return false;
            }

            if (preferredSeatIndex >= 0)
            {
                if ((uint)preferredSeatIndex >= (uint)SeatCount)
                {
                    return false;
                }
                if (!IsSeatPartIntact(preferredSeatIndex))
                {
                    string partLabel = ResolveSeatPartDisplayLabel(preferredSeatIndex);
                    failReason = LanguageDatabase.activeLanguage != null && "GCF_CockpitFail_SeatDestroyed".CanTranslate()
                        ? string.Format("GCF_CockpitFail_SeatDestroyed".Translate(), partLabel)
                        : $"Cockpit destroyed ({partLabel})";
                    return false;
                }
                if (seatOccupants[preferredSeatIndex] != null)
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_SeatOccupied",
                        "Seat occupied");
                    return false;
                }
                return true;
            }

            int openIdx = FindFirstOpenIntactSeatIndex();
            if (openIdx < 0)
            {
                bool anyIntact = false;
                for (int i = 0; i < SeatCount; i++)
                {
                    if (IsSeatPartIntact(i))
                    {
                        anyIntact = true;
                        break;
                    }
                }

                if (!anyIntact)
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_AllSeatsDestroyed",
                        "All cockpit modules destroyed");
                }
                else
                {
                    failReason = GiantFlightUtility.SafeTranslate(
                        "GCF_CockpitFail_Full",
                        "Cockpit full");
                }
                return false;
            }

            return true;
        }

        public bool TryAcceptPawn(Pawn boardingPawn, int preferredSeatIndex = -1)
        {
            EnsureSeatArrays();
            Pawn host = HostPawn;
            if (boardingPawn == null || boardingPawn.Dead || boardingPawn.Destroyed || innerContainer == null || innerContainer.Contains(boardingPawn))
            {
                return false;
            }
            if (host == null || host.Dead || host.Downed || boardingPawn == host || GiantFastLookup.TryGetRig(boardingPawn) != null)
            {
                return false;
            }

            // Full player/map validation for spawned pawns or player hosts; allow non-player unspawned arena crew seeding for same-faction non-player hosts.
            bool isPlayerOrSpawnedBoarding = boardingPawn.Spawned || host.Faction == Faction.OfPlayerSilentFail;
            if (isPlayerOrSpawnedBoarding)
            {
                if (!CanPawnBoard(boardingPawn, out _, preferredSeatIndex) &&
                    (preferredSeatIndex < 0 || !CanPawnBoard(boardingPawn, out _, -1)))
                {
                    return false;
                }
            }
            else
            {
                if (boardingPawn.Downed || !boardingPawn.RaceProps.Humanlike || boardingPawn.Faction != host.Faction)
                {
                    return false;
                }
            }

            int seatIdx = (preferredSeatIndex >= 0 && preferredSeatIndex < SeatCount && seatOccupants[preferredSeatIndex] == null && IsSeatPartIntact(preferredSeatIndex))
                ? preferredSeatIndex
                : FindFirstOpenIntactSeatIndex();

            if (seatIdx < 0)
            {
                return false;
            }

            bool wasSpawned = boardingPawn.Spawned;
            Map prevMap = boardingPawn.Map;
            IntVec3 prevPos = boardingPawn.Position;
            Rot4 prevRot = boardingPawn.Rotation;

            if (wasSpawned && prevMap != null && boardingPawn.carryTracker?.CarriedThing != null)
            {
                boardingPawn.carryTracker.TryDropCarriedThing(prevPos, ThingPlaceMode.Near, out _);
            }

            bool wasSelected = Find.Selector != null && Find.Selector.IsSelected(boardingPawn);
            CurrentlyBoardingPawn = boardingPawn;
            bool addedToContainer = false;
            try
            {
                if (wasSpawned)
                {
                    boardingPawn.DeSpawn(DestroyMode.Vanish);
                }

                addedToContainer = innerContainer.TryAddOrTransfer(boardingPawn, canMergeWithExistingStacks: false);
                if (!addedToContainer)
                {
                    if (wasSpawned && prevMap != null && !boardingPawn.Spawned && !boardingPawn.Destroyed)
                    {
                        GenSpawn.Spawn(boardingPawn, prevPos, prevMap, prevRot, WipeMode.Vanish);
                    }
                    return false;
                }
            }
            catch
            {
                if (!addedToContainer && wasSpawned && prevMap != null && !boardingPawn.Spawned && !boardingPawn.Destroyed)
                {
                    GenSpawn.Spawn(boardingPawn, prevPos, prevMap, prevRot, WipeMode.Vanish);
                }
                throw;
            }
            finally
            {
                CurrentlyBoardingPawn = null;
            }

            seatOccupants[seatIdx] = boardingPawn;
            lastOverseerCheckTick = -999;
            statsDirty = true;
            SyncMapOccupancy();
            Find.ColonistBar?.MarkColonistsDirty();

            if (host.Spawned)
            {
                if (!host.Drafted && ShouldHoldPositionWhenPilotedUndrafted)
                {
                    StopAutonomousMotionAndJobs(host);
                }
                SoundDefOf.CryptosleepCasket_Accept?.PlayOneShot(new TargetInfo(host.Position, host.Map));
                if (wasSelected && Find.Selector != null && !Find.Selector.IsSelected(host))
                {
                    Find.Selector.Select(host, playSound: false, forceDesignatorDeselect: false);
                }
            }

            return true;
        }

        public bool EjectSeat(int seatIndex, bool violent = false, Map mapOverride = null)
        {
            return EjectSeatInternal(seatIndex, violent, mapOverride, bypassFlightBlock: false);
        }

        private bool EjectSeatInternal(int seatIndex, bool violent, Map mapOverride, bool bypassFlightBlock)
        {
            if (suppressEjectReentry || (uint)seatIndex >= (uint)SeatCount)
            {
                return false;
            }

            EnsureSeatArrays();
            Pawn occupant = seatOccupants[seatIndex];
            if (occupant == null)
            {
                return false;
            }

            Pawn host = HostPawn;
            Map map = mapOverride ?? host?.MapHeld;
            GiantCockpitSeatDef seatDef = Props.seats[seatIndex];
            bool forceEject = violent || bypassFlightBlock || host == null || host.Dead || host.Destroyed || mapOverride != null;

            if (!forceEject && map != null && IsFlightStateBlockingBoardingOrDisembark(out string flightBlockReason))
            {
                if (!string.IsNullOrEmpty(flightBlockReason))
                {
                    Messages.Message(flightBlockReason, host, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }

            suppressEjectReentry = true;
            try
            {
                if (innerContainer == null || !innerContainer.Contains(occupant))
                {
                    seatOccupants[seatIndex] = null;
                    statsDirty = true;
                    SyncMapOccupancy();
                    Find.ColonistBar?.MarkColonistsDirty();
                    return false;
                }

                seatOccupants[seatIndex] = null;
                bool ejected = false;

                if (map != null && host != null)
                {
                    if (TryDropThingSafelyOnMap(occupant, host, map, allowWideSearch: forceEject, out Thing dropped) && dropped != null)
                    {
                        ejected = true;
                        if (violent)
                        {
                            GiantVfxSystem.SpawnVolumetricSmokeCluster(map, dropped.DrawPos, 1.15f, 24, new Color(0.22f, 0.21f, 0.20f, 1f), new Color(0.56f, 0.53f, 0.49f, 1f), alpha: 0.45f);
                            GiantVfxSystem.SpawnImpactSparkFan(map, dropped.DrawPos, 0f, 0.85f, 12, new Color(0.72f, 0.62f, 0.46f, 0.72f), deflected: false);
                            SoundDefOf.CryptosleepCasket_Eject?.PlayOneShot(new TargetInfo(dropped.Position, map));

                            if (!occupant.Dead)
                            {
                                if (seatDef.violentEjectStunTicks > 0)
                                {
                                    occupant.stances?.stunner?.StunFor(seatDef.violentEjectStunTicks, host, addBattleLog: false);
                                }
                                if (seatDef.violentEjectBluntDamage > 0.5f)
                                {
                                    float safeDmg = Mathf.Min(seatDef.violentEjectBluntDamage, 10f);
                                    occupant.TakeDamage(new DamageInfo(DamageDefOf.Blunt, safeDmg, 0f, -1f, host));
                                }
                            }
                        }
                        else
                        {
                            SoundDefOf.CryptosleepCasket_Eject?.PlayOneShot(new TargetInfo(dropped.Position, map));
                        }
                    }
                    else if (forceEject)
                    {
                        // Extreme map-full fallback: rescue to world rather than deleting colonist into the void.
                        ejected = TryRescueHeldThingWithoutMap(occupant, host);
                    }
                    else
                    {
                        // Voluntary manual disembark failed (e.g. surrounded by deep water/walls): keep occupant safely seated.
                        seatOccupants[seatIndex] = occupant;
                        string noCellMsg = GiantFlightUtility.SafeTranslate(
                            "GCF_CockpitFail_NoSafeDisembark",
                            "No safe standable ground nearby to disembark.");
                        if (Current.Game != null)
                        {
                            Messages.Message(noCellMsg, host, MessageTypeDefOf.RejectInput, historical: false);
                        }
                        return false;
                    }
                }
                else
                {
                    ejected = TryRescueHeldThingWithoutMap(occupant, host);
                    if (!ejected && !forceEject)
                    {
                        seatOccupants[seatIndex] = occupant;
                        return false;
                    }
                }

                if (!ejected && innerContainer.Contains(occupant))
                {
                    // Never delete into the void: restore seat binding if still held inside container.
                    seatOccupants[seatIndex] = occupant;
                    return false;
                }

                statsDirty = true;
                SyncMapOccupancy();
                if (IsDormantUnmanned && host != null)
                {
                    if (host.drafter != null && host.drafter.Drafted)
                    {
                        host.drafter.Drafted = false;
                    }
                    StopAutonomousMotionAndJobs(host);
                }
                Find.ColonistBar?.MarkColonistsDirty();
                return ejected;
            }
            finally
            {
                suppressEjectReentry = false;
            }
        }

        public int EjectAll(bool violent = false, Map mapOverride = null)
        {
            return EjectAllInternal(violent, mapOverride, bypassFlightBlock: false);
        }

        private int EjectAllInternal(bool violent, Map mapOverride, bool bypassFlightBlock)
        {
            EnsureSeatArrays();
            Pawn host = HostPawn;
            Map map = mapOverride ?? host?.MapHeld;
            bool forceEject = violent || bypassFlightBlock || host == null || host.Dead || host.Destroyed || mapOverride != null;

            if (!forceEject && map != null && IsFlightStateBlockingBoardingOrDisembark(out string flightBlockReason))
            {
                if (!string.IsNullOrEmpty(flightBlockReason))
                {
                    Messages.Message(flightBlockReason, host, MessageTypeDefOf.RejectInput, historical: false);
                }
                return 0;
            }

            int count = 0;
            for (int i = 0; i < SeatCount; i++)
            {
                if (seatOccupants[i] != null && EjectSeatInternal(i, violent, mapOverride, bypassFlightBlock))
                {
                    count++;
                }
            }

            if (innerContainer != null)
            {
                for (int c = innerContainer.Count - 1; c >= 0; c--)
                {
                    Thing leftover = innerContainer[c];
                    if (leftover == null)
                    {
                        continue;
                    }
                    if (map != null && host != null)
                    {
                        if (TryDropThingSafelyOnMap(leftover, host, map, allowWideSearch: forceEject, out _))
                        {
                            count++;
                        }
                        else if (forceEject && TryRescueHeldThingWithoutMap(leftover, host))
                        {
                            count++;
                        }
                    }
                    else if (TryRescueHeldThingWithoutMap(leftover, host))
                    {
                        count++;
                    }
                }
            }

            statsDirty = true;
            SyncMapOccupancy();
            Find.ColonistBar?.MarkColonistsDirty();
            return count;
        }

        private bool TryDropThingSafelyOnMap(Thing item, Pawn host, Map map, bool allowWideSearch, out Thing dropped)
        {
            dropped = null;
            if (item == null || map == null || host == null || innerContainer == null || !innerContainer.Contains(item))
            {
                return false;
            }

            IntVec3 ejectCell = FindSafeEjectionCell(host, map);
            if (ejectCell.IsValid && ejectCell.InBounds(map) && ejectCell.Standable(map) &&
                innerContainer.TryDrop(item, ejectCell, map, ThingPlaceMode.Near, out dropped) && dropped != null)
            {
                return true;
            }

            if (!allowWideSearch)
            {
                return false;
            }

            IntVec3 origin = host.Position.IsValid && host.Position.InBounds(map)
                ? host.Position
                : (host.Corpse != null && host.Corpse.Position.InBounds(map) ? host.Corpse.Position : map.Center);

            if (CellFinder.TryFindRandomCellNear(origin, map, 32, c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map), out IntVec3 wideCell) &&
                innerContainer.TryDrop(item, wideCell, map, ThingPlaceMode.Near, out dropped) && dropped != null)
            {
                return true;
            }

            // Full map-wide standable scan as last resort for violent/death ejection over huge lakes/lava
            IntVec3 bestCell = IntVec3.Invalid;
            int bestDistSq = int.MaxValue;
            bool bestUnfogged = false;
            foreach (IntVec3 c in map.AllCells)
            {
                if (!c.Standable(map))
                {
                    continue;
                }
                bool unfogged = !c.Fogged(map);
                int dSq = (c - origin).LengthHorizontalSquared;
                if ((unfogged && !bestUnfogged) || (unfogged == bestUnfogged && dSq < bestDistSq))
                {
                    bestUnfogged = unfogged;
                    bestDistSq = dSq;
                    bestCell = c;
                }
            }

            if (bestCell.IsValid && innerContainer.TryDrop(item, bestCell, map, ThingPlaceMode.Near, out dropped) && dropped != null)
            {
                return true;
            }

            return false;
        }

        private bool TryRescueHeldThingWithoutMap(Thing item, Pawn host)
        {
            if (item == null || innerContainer == null || !innerContainer.Contains(item))
            {
                return false;
            }

            Caravan hostCaravan = null;
            WorldObject_Giant hostWorldGiant = null;
            IThingHolder holder = host?.ParentHolder;
            for (int depth = 0; holder != null && depth < 8; depth++, holder = holder.ParentHolder)
            {
                if (holder is Caravan car && !car.Destroyed)
                {
                    hostCaravan = car;
                    break;
                }
                if (holder is WorldObject_Giant wg && !wg.Destroyed)
                {
                    hostWorldGiant = wg;
                    break;
                }
            }
            if (hostCaravan == null && hostWorldGiant == null && host != null && Find.WorldObjects != null)
            {
                List<WorldObject> allWorldObjs = Find.WorldObjects.AllWorldObjects;
                for (int i = 0; i < allWorldObjs.Count; i++)
                {
                    if (allWorldObjs[i] is WorldObject_Giant wg && !wg.Destroyed && wg.Pawn == host)
                    {
                        hostWorldGiant = wg;
                        break;
                    }
                }
            }

            if (hostCaravan != null)
            {
                innerContainer.Remove(item);
                hostCaravan.AddPawnOrItem(item, addCarriedPawnToWorldPawnsIfAny: true);
                if (item is Pawn cp && !cp.Dead && !cp.IsWorldPawn())
                {
                    Find.WorldPawns?.PassToWorld(cp, PawnDiscardDecideMode.KeepForever);
                }
                return true;
            }

            if (item is Pawn pawn)
            {
                if (pawn.Dead)
                {
                    innerContainer.Remove(pawn);
                    if (Find.WorldPawns != null && !Find.WorldPawns.Contains(pawn))
                    {
                        Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Decide);
                    }
                    return true;
                }

                if (hostWorldGiant != null && hostWorldGiant.Tile.Valid && Find.World != null && Find.WorldObjects != null)
                {
                    PlanetTile targetTile = ResolveNearestPassableCaravanTile(hostWorldGiant.Tile);
                    if (targetTile.Valid)
                    {
                        Faction targetFaction = pawn.Faction ?? host?.Faction ?? Faction.OfPlayerSilentFail;
                        Caravan existingCaravan = targetFaction == Faction.OfPlayerSilentFail
                            ? Find.WorldObjects.PlayerControlledCaravanAt(targetTile)
                            : null;
                        innerContainer.Remove(pawn);
                        if (existingCaravan != null && !existingCaravan.Destroyed)
                        {
                            existingCaravan.AddPawn(pawn, addCarriedPawnToWorldPawnsIfAny: true);
                            if (!pawn.IsWorldPawn())
                            {
                                Find.WorldPawns?.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                            }
                        }
                        else
                        {
                            CaravanMaker.MakeCaravan(new[] { pawn }, targetFaction, targetTile, addToWorldPawnsIfNotAlready: true);
                        }
                        return true;
                    }
                }

                if (Find.WorldPawns != null)
                {
                    innerContainer.Remove(pawn);
                    if (!Find.WorldPawns.Contains(pawn))
                    {
                        Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                    }
                    return true;
                }
            }
            else if (item is Corpse corpse && hostWorldGiant != null && hostWorldGiant.Tile.Valid && Find.WorldObjects != null)
            {
                PlanetTile targetTile = ResolveNearestPassableCaravanTile(hostWorldGiant.Tile);
                Caravan existingCaravan = targetTile.Valid ? Find.WorldObjects.PlayerControlledCaravanAt(targetTile) : null;
                if (existingCaravan != null && !existingCaravan.Destroyed)
                {
                    innerContainer.Remove(corpse);
                    existingCaravan.AddPawnOrItem(corpse, addCarriedPawnToWorldPawnsIfAny: true);
                    return true;
                }
            }

            return false;
        }

        private static PlanetTile ResolveNearestPassableCaravanTile(PlanetTile originTile)
        {
            if (!originTile.Valid || originTile.LayerDef == null || !originTile.LayerDef.canFormCaravans)
            {
                return PlanetTile.Invalid;
            }
            if (Find.WorldPathGrid != null && Find.WorldPathGrid.Passable(originTile))
            {
                return originTile;
            }
            if (TileFinder.TryFindPassableTileWithTraversalDistance(
                    originTile,
                    minDist: 1,
                    maxDist: 20,
                    out PlanetTile safeTile,
                    validator: t => t.Valid && t.LayerDef != null && t.LayerDef.canFormCaravans && Find.WorldPathGrid != null && Find.WorldPathGrid.Passable(t),
                    ignoreFirstTilePassability: true,
                    tileFinderMode: TileFinderMode.Near,
                    canTraverseImpassable: true,
                    exitOnFirstTileFound: true))
            {
                return safeTile;
            }
            return PlanetTile.Invalid;
        }

        public static IntVec3 FindSafeEjectionCell(Pawn host, Map map)
        {
            if (host == null || map == null)
            {
                return IntVec3.Invalid;
            }

            IntVec3 origin = host.Position.IsValid && host.Position.InBounds(map)
                ? host.Position
                : (host.Corpse != null && host.Corpse.Position.InBounds(map) ? host.Corpse.Position : map.Center);

            CompGiantRig rig = GiantFastLookup.TryGetRig(host);
            HashSet<IntVec3> hullCols = rig?.Footprint?.collisionCellSet;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);

            float hitExtent = rig != null && !rig.Footprint.tightHitBounds.IsEmpty
                ? Mathf.Max(rig.Footprint.tightHitBounds.Width, rig.Footprint.tightHitBounds.Height) * 0.5f
                : 0f;
            float searchRadius = Mathf.Clamp(hitExtent + 4f, 8f, 16f);
            int maxRadial = GenRadial.NumCellsInRadius(searchRadius);
            for (int i = 1; i < maxRadial; i++)
            {
                IntVec3 cand = origin + GenRadial.RadialPattern[i];
                if (!cand.InBounds(map) || !cand.Standable(map) || !cand.Walkable(map))
                {
                    continue;
                }
                if (hullCols != null && hullCols.Contains(cand) && !host.Dead)
                {
                    continue;
                }
                if (mapComp != null && !host.Dead && mapComp.GetCollisionOwnerAt(cand) != null)
                {
                    continue;
                }
                return cand;
            }

            if (CellFinder.TryFindRandomCellNear(origin, map, Mathf.CeilToInt(searchRadius) + 1, c => c.InBounds(map) && c.Standable(map), out IntVec3 fallback))
            {
                return fallback;
            }
            return origin.InBounds(map) && origin.Standable(map) ? origin : IntVec3.Invalid;
        }

        public void CheckMissingSeatPartsAndEject(Map mapOverride = null)
        {
            if (innerContainer.Count == 0 || suppressEjectReentry)
            {
                return;
            }

            EnsureSeatArrays();
            for (int i = 0; i < SeatCount; i++)
            {
                if (seatOccupants[i] != null && !IsSeatPartIntact(i))
                {
                    EjectSeatInternal(i, violent: true, mapOverride: mapOverride, bypassFlightBlock: true);
                }
            }
        }

        public void Notify_HostDamageApplied(DamageInfo dinfo, float totalDamageDealt)
        {
            if (innerContainer.Count == 0 || suppressEjectReentry)
            {
                return;
            }

            CheckMissingSeatPartsAndEject();
            if (innerContainer.Count == 0 || dinfo.HitPart == null || totalDamageDealt <= 0.01f)
            {
                return;
            }

            bool anyCrewStatusChanged = false;
            for (int i = 0; i < SeatCount; i++)
            {
                Pawn occ = seatOccupants[i];
                if (occ == null || occ.Dead)
                {
                    continue;
                }
                BodyPartRecord seatPart = GetSeatBodyPartRecord(i);
                float bleedFactor = Props.seats[i].bleedthroughFactor;
                if (seatPart != null && dinfo.HitPart == seatPart && bleedFactor > 0.001f)
                {
                    float bleedDmg = totalDamageDealt * bleedFactor;
                    if (bleedDmg >= 1f)
                    {
                        occ.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Mathf.Min(bleedDmg, 14f), 0f, -1f, dinfo.Instigator));
                        statsDirty = true;
                        if (occ.Dead || occ.Destroyed || !innerContainer.Contains(occ))
                        {
                            anyCrewStatusChanged = true;
                        }
                    }
                }
            }

            if (anyCrewStatusChanged)
            {
                ReconcileContainerAndSeats(ejectCorpsesOnMap: true);
                statsDirty = true;
                SyncMapOccupancy();
                Find.ColonistBar?.MarkColonistsDirty();
            }
        }

        public override void PostPostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.PostPostApplyDamage(dinfo, totalDamageDealt);
            Notify_HostDamageApplied(dinfo, totalDamageDealt);
        }

        public override void Notify_Killed(Map prevMap, DamageInfo? dinfo = null)
        {
            EjectAllInternal(violent: true, mapOverride: prevMap, bypassFlightBlock: true);
            base.Notify_Killed(prevMap, dinfo);
        }

        public static bool IsHeldInsideWorldGiant(Pawn host)
        {
            if (host == null || (Current.Game != null && host.Spawned))
            {
                return false;
            }
            IThingHolder holder = host.ParentHolder;
            for (int depth = 0; holder != null && depth < 8; depth++, holder = holder.ParentHolder)
            {
                if (holder is WorldObject_Giant || holder is Caravan)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Sustains boarded crew Food, Rest, and Comfort during multi-day WorldObject_Giant or Caravan travel
        /// (preventing mid-ocean pilot malnutrition unconsciousness and world-map soft-locks).
        /// </summary>
        public void StabilizeBoardedCrewInWorldTransit()
        {
            if (innerContainer == null || innerContainer.Count == 0 || !IsHeldInsideWorldGiant(HostPawn))
            {
                return;
            }

            EnsureSeatArrays();
            for (int i = 0; i < innerContainer.Count; i++)
            {
                if (!(innerContainer[i] is Pawn occ) || occ.Dead)
                {
                    continue;
                }
                if (occ.needs != null)
                {
                    if (occ.needs.food != null && occ.needs.food.CurLevelPercentage < 0.75f)
                    {
                        occ.needs.food.CurLevelPercentage = 0.75f;
                    }
                    if (occ.needs.rest != null && occ.needs.rest.CurLevelPercentage < 0.75f)
                    {
                        occ.needs.rest.CurLevelPercentage = 0.75f;
                    }
                    if (occ.needs.comfort != null)
                    {
                        int seatIdx = IndexOfOccupant(occ);
                        float comfort = (uint)seatIdx < (uint)SeatCount ? Mathf.Clamp01(Props.seats[seatIdx].comfortLevel) : 0.65f;
                        occ.needs.comfort.ComfortUsed(comfort);
                    }
                }
                Hediff malnutrition = occ.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                if (malnutrition != null)
                {
                    occ.health.RemoveHediff(malnutrition);
                }
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            bool reconciledChange = ReconcileContainerAndSeats(ejectCorpsesOnMap: true);
            if (reconciledChange)
            {
                statsDirty = true;
                SyncMapOccupancy();
                Find.ColonistBar?.MarkColonistsDirty();
            }

            if (innerContainer == null || innerContainer.Count == 0)
            {
                return;
            }

            CheckMissingSeatPartsAndEject();
            StabilizeBoardedCrewInWorldTransit();
            for (int i = 0; i < SeatCount; i++)
            {
                Pawn occ = seatOccupants[i];
                if (occ == null || occ.Dead || occ.needs == null)
                {
                    continue;
                }

                GiantCockpitSeatDef sDef = Props.seats[i];
                if (occ.needs.comfort != null)
                {
                    occ.needs.comfort.ComfortUsed(Mathf.Clamp01(sDef.comfortLevel));
                }
                if (sDef.role == GiantCockpitRole.Passenger && occ.needs.rest != null)
                {
                    occ.needs.rest.TickResting(0.45f);
                }
            }
            statsDirty = true;
            SyncMapOccupancy();
        }

        private void SyncMapOccupancy()
        {
            Pawn host = HostPawn;
            Map map = host?.Map;
            if (map == null)
            {
                return;
            }
            GiantFootprintMapComponent.GetFast(map)?.SyncCockpitOccupancy(this);
        }

        public static float EvaluatePilotMoveMultiplier(
            float skill01,
            float consciousness,
            float manipulation,
            float minFactor,
            float maxFactor)
        {
            float capFactor = Mathf.Clamp(consciousness * 0.65f + manipulation * 0.35f, 0.35f, 1.35f);
            float t = Mathf.Clamp01(skill01 * 0.75f + (capFactor - 0.5f) * 0.35f);
            return Mathf.Clamp(Mathf.Lerp(minFactor, maxFactor, t) * Mathf.Lerp(0.75f, 1.0f, Mathf.Clamp01(consciousness)), 0.35f, 1.85f);
        }

        public static float EvaluateGunnerCombatMultiplier(
            float shootingSkill01,
            float consciousness,
            float sight,
            float commanderBonus,
            float minFactor,
            float maxFactor)
        {
            float sensorFactor = Mathf.Clamp(consciousness * 0.55f + sight * 0.45f, 0.35f, 1.35f);
            float t = Mathf.Clamp01(shootingSkill01 * 0.78f + (sensorFactor - 0.5f) * 0.30f);
            float raw = Mathf.Lerp(minFactor, maxFactor, t) + commanderBonus;
            return Mathf.Clamp(raw * Mathf.Lerp(0.75f, 1.0f, Mathf.Clamp01(consciousness)), 0.35f, 1.95f);
        }

        private void EnsureStatsCached()
        {
            int nowTick = Current.Game?.tickManager?.TicksGame ?? 0;
            if (!statsDirty && nowTick - lastStatsRecacheTick < 120)
            {
                return;
            }

            lastStatsRecacheTick = nowTick;
            statsDirty = false;

            GiantCockpitControlMode mode = CurrentMode;
            if (mode == GiantCockpitControlMode.Dormant)
            {
                cachedMoveMult = 0f;
                cachedAccuracyMult = 0.05f;
                cachedReloadMult = 0.05f;
                return;
            }

            if (mode == GiantCockpitControlMode.Autonomous)
            {
                cachedMoveMult = Mathf.Max(0.1f, Props.autonomousMoveSpeedFactor);
                cachedAccuracyMult = Mathf.Max(0.1f, Props.autonomousAccuracyFactor);
                cachedReloadMult = Mathf.Max(0.1f, Props.autonomousReloadFactor);
                return;
            }

            Pawn pilot = GetActiveOccupantForRole(GiantCockpitRole.PrimaryPilot) ?? GetFirstAliveOccupant();
            Pawn gunner = GetActiveOccupantForRole(GiantCockpitRole.Gunner) ?? pilot;
            Pawn commander = GetActiveOccupantForRole(GiantCockpitRole.Commander);

            if (pilot != null)
            {
                float melee01 = (pilot.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 8) / 20f;
                float shoot01 = (pilot.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 8) / 20f;
                float pilotSkill01 = Mathf.Max(melee01, shoot01);
                float consc = pilot.health?.capacities?.GetLevel(PawnCapacityDefOf.Consciousness) ?? 1f;
                float manip = pilot.health?.capacities?.GetLevel(PawnCapacityDefOf.Manipulation) ?? 1f;
                cachedMoveMult = EvaluatePilotMoveMultiplier(
                    pilotSkill01,
                    consc,
                    manip,
                    Props.pilotedMinMoveSpeedFactor,
                    Props.pilotedMaxMoveSpeedFactor);
            }
            else
            {
                cachedMoveMult = 1f;
            }

            if (gunner != null)
            {
                float shoot01 = (gunner.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 8) / 20f;
                float consc = gunner.health?.capacities?.GetLevel(PawnCapacityDefOf.Consciousness) ?? 1f;
                float sight = gunner.health?.capacities?.GetLevel(PawnCapacityDefOf.Sight) ?? 1f;
                float cmdBonus = commander != null ? Props.commanderFireControlBonus : 0f;

                cachedAccuracyMult = EvaluateGunnerCombatMultiplier(
                    shoot01,
                    consc,
                    sight,
                    cmdBonus,
                    Props.pilotedMinAccuracyFactor,
                    Props.pilotedMaxAccuracyFactor);

                cachedReloadMult = EvaluateGunnerCombatMultiplier(
                    shoot01,
                    consc,
                    sight,
                    cmdBonus,
                    Props.pilotedMinReloadFactor,
                    Props.pilotedMaxReloadFactor);
            }
            else
            {
                cachedAccuracyMult = 1f;
                cachedReloadMult = 1f;
            }
        }

        private Pawn GetFirstAliveOccupant()
        {
            EnsureSeatArrays();
            for (int i = 0; i < seatOccupants.Length; i++)
            {
                if (seatOccupants[i] != null && !seatOccupants[i].Dead && !seatOccupants[i].Downed)
                {
                    return seatOccupants[i];
                }
            }
            return null;
        }

        public float ComputeMoveSpeedMultiplier()
        {
            EnsureStatsCached();
            return cachedMoveMult;
        }

        public float ComputeWeaponAccuracyMultiplier()
        {
            EnsureStatsCached();
            return cachedAccuracyMult;
        }

        public float ComputeReloadSpeedMultiplier()
        {
            EnsureStatsCached();
            return cachedReloadMult;
        }

        public string ResolveSeatPartDisplayLabel(int seatIndex)
        {
            if ((uint)seatIndex < (uint)SeatCount)
            {
                GiantCockpitSeatDef seatDef = Props.seats[seatIndex];
                if (seatDef != null && !string.IsNullOrEmpty(seatDef.partLabelKey) && seatDef.partLabelKey.CanTranslate())
                {
                    return seatDef.partLabelKey.Translate().ToString();
                }
            }

            BodyPartRecord rec = GetSeatBodyPartRecord(seatIndex);
            if (rec != null)
            {
                // Prefer localized BodyPartDef.LabelCap when BodyPartRecord.customLabel is an untranslated duplicate of the English def label
                if (!string.IsNullOrEmpty(rec.customLabel)
                    && !string.IsNullOrEmpty(rec.untranslatedCustomLabel)
                    && string.Equals(rec.customLabel, rec.untranslatedCustomLabel, StringComparison.OrdinalIgnoreCase)
                    && rec.def != null
                    && !string.IsNullOrEmpty(rec.def.label)
                    && !string.Equals(rec.def.label, rec.untranslatedCustomLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return rec.def.LabelCap;
                }
                return rec.LabelCap;
            }
            if ((uint)seatIndex < (uint)SeatCount && Props.seats[seatIndex]?.linkedBodyPart != null)
            {
                return Props.seats[seatIndex].linkedBodyPart.LabelCap;
            }
            return Props.seats[seatIndex]?.GetLocalizedRoleLabel() ?? "Cockpit";
        }

        public string GetLocalizedControlModeLabel()
        {
            if (!Props.treatHostAsVehicle)
            {
                string mountKey = CurrentMode switch
                {
                    GiantCockpitControlMode.Piloted => "GCF_CrewMode_Mounted",
                    GiantCockpitControlMode.Autonomous => "GCF_CrewMode_Unmounted",
                    _ => "GCF_CockpitMode_Dormant"
                };
                if (mountKey.CanTranslate())
                {
                    return mountKey.Translate().ToString();
                }
                return CurrentMode == GiantCockpitControlMode.Piloted ? "Mounted" : "Unmounted";
            }

            string key = CurrentMode switch
            {
                GiantCockpitControlMode.Piloted => "GCF_CockpitMode_Piloted",
                GiantCockpitControlMode.Autonomous => "GCF_CockpitMode_Autonomous",
                _ => "GCF_CockpitMode_Dormant"
            };
            if (key.CanTranslate())
            {
                return key.Translate().ToString();
            }
            return CurrentMode.ToString();
        }

        public bool HasAnyBoardedFreeColonist()
        {
            if (innerContainer == null || innerContainer.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < innerContainer.Count; i++)
            {
                if (innerContainer[i] is Pawn p && !p.Dead && p.IsFreeColonist)
                {
                    return true;
                }
            }
            return false;
        }

        public int CountBoardedFreeColonists()
        {
            if (innerContainer == null || innerContainer.Count == 0)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < innerContainer.Count; i++)
            {
                if (innerContainer[i] is Pawn p && !p.Dead && p.IsFreeColonist)
                {
                    count++;
                }
            }
            return count;
        }

        public bool HasAnyValidColonyPawnBlockingRemoval()
        {
            if (innerContainer == null || innerContainer.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < innerContainer.Count; i++)
            {
                if (innerContainer[i] is Pawn p && !p.Dead && !p.Downed && p.IsColonist)
                {
                    return true;
                }
            }
            return false;
        }

        public override string CompInspectStringExtra()
        {
            Pawn host = HostPawn;
            if (host == null || host.Dead)
            {
                return null;
            }

            if (!Props.treatHostAsVehicle && CrewCount == 0 && host.Faction != Faction.OfPlayer)
            {
                return null;
            }

            string modeStr = GetLocalizedControlModeLabel();
            string crewRatio = $"{CrewCount}/{SeatCount}";
            string movePct = GiantCockpitPercentCache.FormatMultiplierPct(ComputeMoveSpeedMultiplier());
            string firePct = GiantCockpitPercentCache.FormatMultiplierPct(ComputeWeaponAccuracyMultiplier());

            Pawn primary = GetActiveOccupantForRole(GiantCockpitRole.PrimaryPilot) ?? GetFirstAliveOccupant();
            if (primary != null)
            {
                return "GCF_CockpitInspect_Manned".CanTranslate()
                    ? string.Format("GCF_CockpitInspect_Manned".Translate(), modeStr, primary.LabelShort, crewRatio, movePct, firePct)
                    : $"Control: {modeStr} ({primary.LabelShort}) | Crew: {crewRatio} | Move {movePct} · Fire {firePct}";
            }

            return "GCF_CockpitInspect_Unmanned".CanTranslate()
                ? string.Format("GCF_CockpitInspect_Unmanned".Translate(), modeStr, crewRatio, movePct, firePct)
                : $"Control: {modeStr} | Crew: {crewRatio} | Move {movePct} · Fire {firePct}";
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            Pawn host = HostPawn;
            if (host == null || !host.Spawned || host.Dead || host.Faction != Faction.OfPlayer)
            {
                yield break;
            }

            if (!host.Drafted && Props.treatHostAsVehicle && Props.autonomousWhenEmpty && CrewCount == 0)
            {
                bool coreOk = IsAutonomousCoreIntact();
                var autoToggle = new Command_Toggle
                {
                    defaultLabel = "GCF_Gizmo_AutonomousMode_Label".CanTranslate()
                        ? "GCF_Gizmo_AutonomousMode_Label".Translate().ToString()
                        : "Autonomous AI",
                    defaultDesc = "GCF_Gizmo_AutonomousMode_Desc".CanTranslate()
                        ? "GCF_Gizmo_AutonomousMode_Desc".Translate().ToString()
                        : "Toggle onboard autonomous AI patrol and combat when no human pilot is aboard. Turn off to park in standby.",
                    icon = GiantCockpitGizmoAssets.AutonomousIcon,
                    isActive = () => !parkedWhenEmpty && coreOk,
                    toggleAction = () => SetParkedWhenEmpty(!parkedWhenEmpty)
                };
                if (!coreOk)
                {
                    autoToggle.Disable("GCF_CockpitFail_AiCoreDestroyed".CanTranslate()
                        ? "GCF_CockpitFail_AiCoreDestroyed".Translate().ToString()
                        : "Autonomous AI core destroyed");
                }
                yield return autoToggle;
            }

            if (CrewCount > 0)
            {
                string ratio = $"{CrewCount}/{SeatCount}";
                var ejectCmd = new Command_Action
                {
                    defaultLabel = "GCF_Gizmo_DisembarkAll_Label".CanTranslate()
                        ? string.Format("GCF_Gizmo_DisembarkAll_Label".Translate(), ratio)
                        : $"Disembark All ({ratio})",
                    defaultDesc = "GCF_Gizmo_DisembarkAll_Desc".CanTranslate()
                        ? "GCF_Gizmo_DisembarkAll_Desc".Translate().ToString()
                        : "Order all boarded crew members to disembark onto safe perimeter cells.",
                    icon = GiantCockpitGizmoAssets.DisembarkIcon,
                    action = () => EjectAll(violent: false)
                };
                if (IsFlightStateBlockingBoardingOrDisembark(out string flightBlockReason))
                {
                    ejectCmd.Disable(flightBlockReason);
                }
                yield return ejectCmd;
            }
        }

        private struct MountedOccupantDrawItem
        {
            public Pawn occupant;
            public CrewSeatWorldPose pose;
        }

        private MountedOccupantDrawItem[] mountedDrawScratch;

        public override void PostDraw()
        {
            base.PostDraw();
            if (!Props.renderCrewOnRig || innerContainer == null || innerContainer.Count == 0 || !UnityData.IsInMainThread)
            {
                return;
            }

            Pawn host = HostPawn;
            if (host == null || !host.Spawned || host.Dead)
            {
                return;
            }

            CompGiantRig rigComp = GiantFastLookup.TryGetRig(host);
            if (rigComp?.RigDef == null)
            {
                return;
            }

            bool airborneAboveFog = rigComp.IsCurrentlyAirborne || rigComp.IsFlyingEntry;
            if ((Current.Game?.World?.factionManager != null && host.health?.hediffSet?.hediffs != null && host.IsHiddenFromPlayer()) ||
                (!airborneAboveFog && host.Map != null && host.Position.Fogged(host.Map)))
            {
                return;
            }

            EnsureSeatArrays();
            int seatLen = Mathf.Min(seatOccupants.Length, Props.seats?.Count ?? 0);
            if (seatLen <= 0)
            {
                return;
            }

            Vector3 hostDrawPos = CompGiantRig.GetSafeDrawPos(host);
            if (rigComp.VehicleLocomotion != null && rigComp.VehicleLocomotion.TryGetExactWorldPos(host, out Vector3 exactVehPos))
            {
                hostDrawPos = new Vector3(exactVehPos.x, hostDrawPos.y, exactVehPos.z);
            }
            else if (rigComp.FlightVehicleLocomotion != null && rigComp.FlightVehicleLocomotion.TryGetExactWorldPos(host, out Vector3 exactFvehPos))
            {
                hostDrawPos = new Vector3(exactFvehPos.x, hostDrawPos.y, exactFvehPos.z);
            }

            if (mountedDrawScratch == null || mountedDrawScratch.Length < seatLen)
            {
                mountedDrawScratch = new MountedOccupantDrawItem[Math.Max(8, seatLen)];
            }

            int drawCount = 0;
            for (int i = 0; i < seatLen; i++)
            {
                Pawn occ = seatOccupants[i];
                GiantCockpitSeatDef seatDef = Props.seats[i];
                if (occ == null || occ.Dead || seatDef == null || !seatDef.renderOccupant)
                {
                    continue;
                }
                if (!rigComp.TryEvaluateCrewSeatWorldPose(seatDef.EffectiveRigSeatId, hostDrawPos, out CrewSeatWorldPose seatPose) ||
                    !seatPose.visible)
                {
                    continue;
                }

                mountedDrawScratch[drawCount++] = new MountedOccupantDrawItem
                {
                    occupant = occ,
                    pose = seatPose
                };
            }

            if (drawCount <= 0)
            {
                return;
            }

            // Insertion-sort North-to-South (descending worldZ) so northern riders draw first and southern riders draw in front
            for (int i = 1; i < drawCount; i++)
            {
                MountedOccupantDrawItem key = mountedDrawScratch[i];
                int j = i - 1;
                while (j >= 0 && mountedDrawScratch[j].pose.worldXZ.y < key.pose.worldXZ.y)
                {
                    mountedDrawScratch[j + 1] = mountedDrawScratch[j];
                    j--;
                }
                mountedDrawScratch[j + 1] = key;
            }

            for (int i = 0; i < drawCount; i++)
            {
                DrawMountedOccupantPawn(mountedDrawScratch[i].occupant, in mountedDrawScratch[i].pose);
            }
        }

        private static void DrawMountedOccupantPawn(Pawn occupant, in CrewSeatWorldPose seatPose)
        {
            PawnRenderer occRenderer = occupant?.Drawer?.renderer;
            PawnRenderTree tree = occRenderer?.renderTree;
            if (tree == null)
            {
                return;
            }

            PawnRenderFlags flags = PawnRenderFlags.Headgear | PawnRenderFlags.Clothes;
            tree.EnsureInitialized(flags);

            Vector3 drawLoc = new Vector3(seatPose.worldXZ.x, seatPose.worldY, seatPose.worldXZ.y);
            Quaternion rot = Mathf.Abs(seatPose.spriteAngleDeg) > 1e-3f
                ? Quaternion.AngleAxis(seatPose.spriteAngleDeg, Vector3.up)
                : Quaternion.identity;
            float s = seatPose.drawScale;

            PawnDrawParms parms = PawnDrawParms.Default;
            parms.pawn = occupant;
            parms.matrix = Matrix4x4.TRS(drawLoc, rot, new Vector3(s, 1f, s));
            parms.facing = seatPose.facingRot4;
            parms.rotDrawMode = occRenderer.CurRotDrawMode;
            parms.posture = PawnPosture.Standing;
            parms.flags = flags;
            parms.tint = occRenderer.StatueColor ?? Color.white;
            parms.statueColor = occRenderer.StatueColor;

            tree.ParallelPreDraw(parms);
            tree.Draw(parms);

            if (seatPose.renderWeapon && occupant.equipment?.Primary != null)
            {
                PawnRenderUtility.DrawEquipmentAndApparelExtras(occupant, drawLoc, seatPose.facingRot4, flags);
            }
        }

        public void OpenQuickBoardFloatMenu(Pawn host, int targetSeatIndex = -1)
        {
            Map map = host?.Map;
            if (map == null)
            {
                return;
            }

            var options = new List<FloatMenuOption>();
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn col = colonists[i];
                if (col == null || col.Dead || col.Downed || !GiantHullInteractionUtility.CanPawnReachGiantHull(col, host))
                {
                    continue;
                }
                if (!CanPawnBoard(col, out _, targetSeatIndex))
                {
                    continue;
                }

                int shootLvl = col.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
                int meleeLvl = col.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
                string optLabel = "GCF_FloatMenu_AssignColonistItem".CanTranslate()
                    ? string.Format("GCF_FloatMenu_AssignColonistItem".Translate(), col.LabelShort, shootLvl, meleeLvl)
                    : $"{col.LabelShort} (Shoot {shootLvl} / Melee {meleeLvl})";

                Pawn capturedCol = col;
                int capturedSeat = targetSeatIndex;
                options.Add(new FloatMenuOption(optLabel, delegate
                {
                    JobDriver_EnterGiantCockpit.TryGiveBoardJob(capturedCol, host, capturedSeat);
                }));
            }

            if (options.Count == 0)
            {
                string emptyLabel = "GCF_FloatMenu_NoEligibleColonists".CanTranslate()
                    ? "GCF_FloatMenu_NoEligibleColonists".Translate().ToString()
                    : "No available colonists can reach cockpit";
                options.Add(new FloatMenuOption(emptyLabel, null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    public class CompGiantCrew : CompGiantCockpit
    {
    }
}
