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
    public enum InterceptorFlightState
    {
        Docked = 0,
        Launching = 1,
        AttackRun = 2,
        BreakTurn = 3,
        EscortOrbit = 4,
        Returning = 5
    }

    /// <summary>
    /// Pure kinematic state for one Interceptor so both live RimWorld Things and offline C# verification
    /// tests execute the exact same deterministic launch -> strafe attack -> recall -> dock flight loop.
    /// </summary>
    public struct InterceptorKinematicState
    {
        public InterceptorFlightState State;
        public Vector2 WorldXZ;
        public float HeadingDeg;
        public float BankDeg;
        public float SpeedCellsPerSec;
        public int StateTicks;
        public int BurstShotsRemaining;
        public int CooldownTicksLeft;
        public int OrbitDirectionSign;
        public int TotalShotsFired;
        public bool ReadyToDock;

        public void Step(
            Vector2 hangarSocketXZ,
            float carrierHeadingDeg,
            bool hasValidTarget,
            Vector2 targetXZ,
            bool recallOrdered,
            float maxLeashRadius,
            float baseCruiseSpeed,
            float speedMultiplier,
            out bool firedSalvoThisTick)
        {
            firedSalvoThisTick = false;
            ReadyToDock = false;
            StateTicks++;
            if (CooldownTicksLeft > 0)
            {
                CooldownTicksLeft--;
            }
            if (OrbitDirectionSign == 0)
            {
                OrbitDirectionSign = 1;
            }

            const float dt = 1f / 60f;
            float topSpeed = Mathf.Max(6f, baseCruiseSpeed * Mathf.Max(0.5f, speedMultiplier));
            Vector2 toCarrier = hangarSocketXZ - WorldXZ;
            float distToCarrier = toCarrier.magnitude;

            if (recallOrdered && State != InterceptorFlightState.Returning && State != InterceptorFlightState.Docked)
            {
                State = InterceptorFlightState.Returning;
                StateTicks = 0;
                BurstShotsRemaining = 0;
            }
            else if (State != InterceptorFlightState.Returning && State != InterceptorFlightState.Launching && distToCarrier > maxLeashRadius)
            {
                State = InterceptorFlightState.Returning;
                StateTicks = 0;
                BurstShotsRemaining = 0;
            }

            float desiredHeading = HeadingDeg;
            float targetSpeed = topSpeed;
            float maxTurnDegPerTick = 8.5f;

            switch (State)
            {
                case InterceptorFlightState.Launching:
                {
                    targetSpeed = topSpeed * 1.22f;
                    maxTurnDegPerTick = 5.0f;
                    if (StateTicks > 10 && hasValidTarget)
                    {
                        Vector2 toTarg = targetXZ - WorldXZ;
                        if (toTarg.sqrMagnitude > 0.04f)
                        {
                            desiredHeading = Mathf.Atan2(toTarg.x, toTarg.y) * Mathf.Rad2Deg;
                        }
                    }
                    else
                    {
                        desiredHeading = carrierHeadingDeg + OrbitDirectionSign * 14f;
                    }

                    if (StateTicks >= 18)
                    {
                        State = hasValidTarget ? InterceptorFlightState.AttackRun : InterceptorFlightState.EscortOrbit;
                        StateTicks = 0;
                    }
                    break;
                }

                case InterceptorFlightState.AttackRun:
                {
                    if (!hasValidTarget)
                    {
                        State = InterceptorFlightState.EscortOrbit;
                        StateTicks = 0;
                        break;
                    }

                    Vector2 toTarg = targetXZ - WorldXZ;
                    float dist = toTarg.magnitude;
                    if (dist > 0.05f)
                    {
                        desiredHeading = Mathf.Atan2(toTarg.x, toTarg.y) * Mathf.Rad2Deg;
                    }
                    maxTurnDegPerTick = 9.5f;
                    targetSpeed = topSpeed * 1.05f;

                    float noseErr = Mathf.Abs(Mathf.DeltaAngle(HeadingDeg, desiredHeading));
                    if (dist <= 8.5f && dist >= 1.4f && noseErr <= 34f && CooldownTicksLeft <= 0)
                    {
                        firedSalvoThisTick = true;
                        TotalShotsFired += 2;
                        CooldownTicksLeft = Mathf.Max(14, Mathf.RoundToInt(28f / Mathf.Max(0.6f, speedMultiplier)));
                        State = InterceptorFlightState.BreakTurn;
                        StateTicks = 0;
                    }
                    else if (dist < 1.6f)
                    {
                        if (CooldownTicksLeft <= 0)
                        {
                            firedSalvoThisTick = true;
                            TotalShotsFired += 2;
                            CooldownTicksLeft = Mathf.Max(14, Mathf.RoundToInt(28f / Mathf.Max(0.6f, speedMultiplier)));
                        }
                        State = InterceptorFlightState.BreakTurn;
                        StateTicks = 0;
                    }
                    break;
                }

                case InterceptorFlightState.BreakTurn:
                {
                    Vector2 pivotCenter = hasValidTarget ? targetXZ : hangarSocketXZ;
                    Vector2 away = WorldXZ - pivotCenter;
                    if (away.sqrMagnitude < 0.04f)
                    {
                        float rad = HeadingDeg * Mathf.Deg2Rad;
                        away = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
                    }
                    float radialDeg = Mathf.Atan2(away.x, away.y) * Mathf.Rad2Deg;
                    desiredHeading = radialDeg + OrbitDirectionSign * 58f;
                    maxTurnDegPerTick = 10.5f;
                    targetSpeed = topSpeed * 1.08f;

                    float distFromTarget = away.magnitude;
                    if (StateTicks >= 26 && (distFromTarget >= 5.2f || StateTicks >= 44))
                    {
                        OrbitDirectionSign = -OrbitDirectionSign;
                        State = hasValidTarget ? InterceptorFlightState.AttackRun : InterceptorFlightState.EscortOrbit;
                        StateTicks = 0;
                    }
                    break;
                }

                case InterceptorFlightState.EscortOrbit:
                {
                    if (hasValidTarget)
                    {
                        State = InterceptorFlightState.AttackRun;
                        StateTicks = 0;
                        break;
                    }

                    if (StateTicks >= 75)
                    {
                        State = InterceptorFlightState.Returning;
                        StateTicks = 0;
                        break;
                    }

                    float orbitAngleRad = ((StateTicks * 4.5f * OrbitDirectionSign) + carrierHeadingDeg) * Mathf.Deg2Rad;
                    Vector2 orbitPt = hangarSocketXZ + new Vector2(Mathf.Sin(orbitAngleRad), Mathf.Cos(orbitAngleRad)) * 4.6f;
                    Vector2 toOrbit = orbitPt - WorldXZ;
                    if (toOrbit.sqrMagnitude > 0.04f)
                    {
                        desiredHeading = Mathf.Atan2(toOrbit.x, toOrbit.y) * Mathf.Rad2Deg;
                    }
                    maxTurnDegPerTick = 8.0f;
                    targetSpeed = topSpeed * 0.88f;
                    break;
                }

                case InterceptorFlightState.Returning:
                {
                    if (distToCarrier <= 1.25f)
                    {
                        ReadyToDock = true;
                        State = InterceptorFlightState.Docked;
                        SpeedCellsPerSec = 0f;
                        BankDeg = 0f;
                        return;
                    }

                    desiredHeading = Mathf.Atan2(toCarrier.x, toCarrier.y) * Mathf.Rad2Deg;
                    maxTurnDegPerTick = distToCarrier < 4.5f ? 16.0f : 11.5f;
                    float approachFactor = Mathf.Clamp(distToCarrier / 4.0f, 0.52f, 1.30f);
                    targetSpeed = topSpeed * approachFactor;
                    break;
                }

                default:
                    return;
            }

            float deltaYaw = Mathf.Clamp(Mathf.DeltaAngle(HeadingDeg, desiredHeading), -maxTurnDegPerTick, maxTurnDegPerTick);
            HeadingDeg = Mathf.Repeat(HeadingDeg + deltaYaw, 360f);

            float targetBank = Mathf.Clamp(-deltaYaw * 3.4f, -34f, 34f);
            BankDeg = Mathf.Lerp(BankDeg, targetBank, 0.25f);
            SpeedCellsPerSec = Mathf.Lerp(SpeedCellsPerSec, targetSpeed, 0.22f);

            float moveRad = HeadingDeg * Mathf.Deg2Rad;
            Vector2 step = new Vector2(Mathf.Sin(moveRad), Mathf.Cos(moveRad)) * (SpeedCellsPerSec * dt);
            WorldXZ += step;
        }
    }

    public sealed class CarrierHangarSlot : IExposable
    {
        public int slotIndex;
        public bool isBuilt = true;
        public int buildTicksProgress;
        public float storedHullHp = 95f;
        public float storedShieldHp = 60f;
        public CarrierInterceptor activeInterceptor;

        public int BaySide => slotIndex & 1; // 0 = Port (left), 1 = Starboard (right)

        public bool IsAirborne => activeInterceptor != null && !activeInterceptor.Destroyed && activeInterceptor.Spawned;
        public bool IsDockedReady => isBuilt && !IsAirborne;

        public void ExposeData()
        {
            Scribe_Values.Look(ref slotIndex, "slotIndex", 0);
            Scribe_Values.Look(ref isBuilt, "isBuilt", defaultValue: true);
            Scribe_Values.Look(ref buildTicksProgress, "buildTicksProgress", 0);
            Scribe_Values.Look(ref storedHullHp, "storedHullHp", 95f);
            Scribe_Values.Look(ref storedShieldHp, "storedShieldHp", 60f);
            Scribe_References.Look(ref activeInterceptor, "activeInterceptor");
        }
    }

    /// <summary>
    /// Autonomous StarCraft II Protoss Interceptor starfighter launched from GCF_ProtossCarrier.
    /// Flies smoothly in 360-degree continuous world space, executes high-speed strafing runs firing twin
    /// plasma beam bolts, absorbs fire on its regenerating Protoss plasma shield, and returns to its
    /// Carrier's live articulated hangar bay socket upon target elimination, damage threshold, or recall.
    /// </summary>
    public class CarrierInterceptor : Thing
    {
        private const string InterceptorTexPath = "Things/Pawn/GiantCreature/ProtossCarrier/Interceptor";

        public Pawn carrierPawn;
        public int slotIndex;
        public float shieldHp = 60f;
        public float maxShieldHp = 60f;
        public int ticksSinceLastShieldHit = 999;
        public LocalTargetInfo assignedTarget = LocalTargetInfo.Invalid;
        public bool recallRequested;
        public InterceptorKinematicState kin;
        private int bareCellStrafeTicks;

        public override Vector3 DrawPos => new Vector3(
            kin.WorldXZ.x,
            CompGiantRig.AirborneVfxBaseAltitudeY + 0.01f,
            kin.WorldXZ.y);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref carrierPawn, "carrierPawn");
            Scribe_Values.Look(ref slotIndex, "slotIndex", 0);
            Scribe_Values.Look(ref shieldHp, "shieldHp", 60f);
            Scribe_Values.Look(ref maxShieldHp, "maxShieldHp", 60f);
            Scribe_Values.Look(ref ticksSinceLastShieldHit, "ticksSinceLastShieldHit", 999);
            Scribe_TargetInfo.Look(ref assignedTarget, "assignedTarget");
            Scribe_Values.Look(ref recallRequested, "recallRequested", defaultValue: false);
            Scribe_Values.Look(ref bareCellStrafeTicks, "bareCellStrafeTicks", 0);

            Scribe_Values.Look(ref kin.State, "kinState", InterceptorFlightState.Launching);
            Scribe_Values.Look(ref kin.WorldXZ, "kinWorldXZ", Vector2.zero);
            Scribe_Values.Look(ref kin.HeadingDeg, "kinHeadingDeg", 0f);
            Scribe_Values.Look(ref kin.BankDeg, "kinBankDeg", 0f);
            Scribe_Values.Look(ref kin.SpeedCellsPerSec, "kinSpeed", 14f);
            Scribe_Values.Look(ref kin.StateTicks, "kinStateTicks", 0);
            Scribe_Values.Look(ref kin.CooldownTicksLeft, "kinCooldown", 0);
            Scribe_Values.Look(ref kin.OrbitDirectionSign, "kinOrbitSign", 1);
            Scribe_Values.Look(ref kin.TotalShotsFired, "kinTotalShots", 0);
        }

        public void InitializeSortie(
            Pawn ownerCarrier,
            int hangarSlotIndex,
            Vector2 launchWorldXZ,
            float launchHeadingDeg,
            float initialHullHp,
            float initialShieldHp,
            float maxShield,
            LocalTargetInfo initialTarget)
        {
            carrierPawn = ownerCarrier;
            slotIndex = hangarSlotIndex;
            maxShieldHp = Mathf.Max(10f, maxShield);
            shieldHp = Mathf.Clamp(initialShieldHp, 0f, maxShieldHp);
            HitPoints = Mathf.Clamp(Mathf.RoundToInt(initialHullHp), 1, MaxHitPoints);
            assignedTarget = initialTarget;
            recallRequested = false;
            bareCellStrafeTicks = 0;

            kin = new InterceptorKinematicState
            {
                State = InterceptorFlightState.Launching,
                WorldXZ = launchWorldXZ,
                HeadingDeg = launchHeadingDeg,
                BankDeg = 0f,
                SpeedCellsPerSec = 17.5f,
                StateTicks = 0,
                CooldownTicksLeft = 6,
                OrbitDirectionSign = (hangarSlotIndex & 1) == 0 ? -1 : 1
            };
        }

        public void OrderRecall()
        {
            recallRequested = true;
            if (kin.State != InterceptorFlightState.Docked)
            {
                kin.State = InterceptorFlightState.Returning;
                kin.StateTicks = 0;
            }
        }

        public override void PostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.PostApplyDamage(dinfo, totalDamageDealt);
            if (!Destroyed && Spawned && HitPoints <= MaxHitPoints * 0.35f && shieldHp <= 1f)
            {
                // Damaged Interceptor autonomously breaks off to repair & recharge shields inside hangar
                OrderRecall();
            }
        }

        public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            base.PreApplyDamage(ref dinfo, out absorbed);
            if (absorbed)
            {
                return;
            }

            ticksSinceLastShieldHit = 0;
            if (shieldHp > 0.5f && dinfo.Def != DamageDefOf.SurgicalCut)
            {
                float dmg = dinfo.Amount;
                if (dinfo.Def == DamageDefOf.EMP)
                {
                    dmg *= 1.75f;
                }

                if (Map != null && GiantVfxSystem.IsRigVfxEnabled(carrierPawn))
                {
                    GiantVfxSystem.SpawnKhaydarinWarpPulse(
                        Map,
                        DrawPos,
                        radius: 0.92f,
                        durationTicks: 11,
                        primaryColor: new Color(0.26f, 0.90f, 1.00f, 0.92f),
                        isHexShieldWard: true,
                        followThing: this);
                }
                if (shieldHp >= dmg)
                {
                    shieldHp -= dmg;
                    absorbed = true;
                    return;
                }

                float leftover = dmg - shieldHp;
                shieldHp = 0f;
                dinfo.SetAmount(leftover);
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned || Map == null)
            {
                return;
            }

            // StarCraft II rule: if the Carrier mothership dies or leaves the map, airborne Interceptors overload and detonate.
            if (carrierPawn == null || carrierPawn.Destroyed || carrierPawn.Dead || !carrierPawn.Spawned || carrierPawn.Map != Map)
            {
                DetonatePsionicOverload();
                return;
            }

            CompCarrierHangar hangar = GiantFastLookup.TryGetCarrierHangar(carrierPawn);
            if (hangar == null)
            {
                DetonatePsionicOverload();
                return;
            }

            ticksSinceLastShieldHit++;
            if (ticksSinceLastShieldHit > 180 && shieldHp < maxShieldHp)
            {
                shieldHp = Mathf.Min(maxShieldHp, shieldHp + 0.12f);
            }

            if (carrierPawn.Downed)
            {
                recallRequested = true;
            }

            // Validate current target or acquire next hostile threat within Carrier engagement zone
            if (assignedTarget.IsValid && !assignedTarget.HasThing)
            {
                bareCellStrafeTicks++;
            }
            else
            {
                bareCellStrafeTicks = 0;
            }

            bool hasValidTarget = IsTargetValidAndAlive(assignedTarget);
            if (!hasValidTarget && !recallRequested && this.IsHashIntervalTick(15))
            {
                assignedTarget = hangar.ResolveNextInterceptorTarget(kin.WorldXZ);
                bareCellStrafeTicks = 0;
                hasValidTarget = IsTargetValidAndAlive(assignedTarget);
            }

            Vector2 targetXZ = Vector2.zero;
            if (hasValidTarget)
            {
                Vector3 tPos = assignedTarget.HasThing
                    ? (assignedTarget.Thing is Pawn tp ? CompGiantRig.GetSafeDrawPos(tp) : assignedTarget.Thing.DrawPos)
                    : assignedTarget.Cell.ToVector3Shifted();
                targetXZ = new Vector2(tPos.x, tPos.z);
            }

            Vector2 hangarSocketXZ = hangar.GetLiveBayWorldXZ(slotIndex & 1, out float carrierHeadingDeg);
            Vector2 clampedHangarXZ = new Vector2(
                Mathf.Clamp(hangarSocketXZ.x, 1.3f, Map.Size.x - 1.3f),
                Mathf.Clamp(hangarSocketXZ.y, 1.3f, Map.Size.z - 1.3f));
            float speedMult = hangar.IsOverdriveActive ? 1.32f : 1.0f;

            kin.Step(
                clampedHangarXZ,
                carrierHeadingDeg,
                hasValidTarget,
                targetXZ,
                recallRequested,
                hangar.Props.maxLeashRadius,
                hangar.Props.interceptorCruiseSpeed,
                speedMult,
                out bool firedSalvo);

            // Keep underlying map cell synchronized within map bounds
            IntVec3 clampedCell = new Vector3(
                Mathf.Clamp(kin.WorldXZ.x, 1.5f, Map.Size.x - 1.5f),
                0f,
                Mathf.Clamp(kin.WorldXZ.y, 1.5f, Map.Size.z - 1.5f)).ToIntVec3();
            kin.WorldXZ = new Vector2(
                Mathf.Clamp(kin.WorldXZ.x, 1.2f, Map.Size.x - 1.2f),
                Mathf.Clamp(kin.WorldXZ.y, 1.2f, Map.Size.z - 1.2f));
            if (clampedCell.InBounds(Map) && clampedCell != Position)
            {
                Position = clampedCell;
            }

            // Safety fallback for map-edge Carriers: if returning for a while near the clamped Carrier center, dock cleanly.
            if (!kin.ReadyToDock && kin.State == InterceptorFlightState.Returning && kin.StateTicks >= 240)
            {
                Vector3 cPos = CompGiantRig.GetSafeDrawPos(carrierPawn);
                Vector2 clampedCarrierCenterXZ = new Vector2(
                    Mathf.Clamp(cPos.x, 1.3f, Map.Size.x - 1.3f),
                    Mathf.Clamp(cPos.z, 1.3f, Map.Size.z - 1.3f));
                if ((kin.WorldXZ - clampedCarrierCenterXZ).sqrMagnitude <= 6.25f || kin.StateTicks >= 480)
                {
                    kin.ReadyToDock = true;
                }
            }

            if (firedSalvo && hasValidTarget)
            {
                FireTwinPlasmaBeams(hangar, targetXZ);
            }

            // Subtle cyan Khaydarin thruster trail
            if (this.IsHashIntervalTick(7) && GiantVfxSystem.IsRigVfxEnabled(carrierPawn))
            {
                float rad = kin.HeadingDeg * Mathf.Deg2Rad;
                Vector3 exhaustPos = new Vector3(
                    kin.WorldXZ.x - Mathf.Sin(rad) * 0.52f,
                    AltitudeLayer.MoteOverhead.AltitudeFor(),
                    kin.WorldXZ.y - Mathf.Cos(rad) * 0.52f);
                if (exhaustPos.ToIntVec3().InBounds(Map))
                {
                    GiantVfxSystem.SpawnSoftGlowBurst(Map, exhaustPos, 0.42f, new Color(0.28f, 0.90f, 1.00f, 0.72f), solidTime: 0.04f);
                }
            }

            if (kin.ReadyToDock)
            {
                hangar.NotifyInterceptorDocked(this);
            }
        }

        private bool IsTargetValidAndAlive(LocalTargetInfo targ)
        {
            if (!targ.IsValid)
            {
                return false;
            }
            if (targ.HasThing)
            {
                Thing t = targ.Thing;
                if (t == null || t.Destroyed || !t.Spawned || t.Map != Map || t == carrierPawn)
                {
                    return false;
                }
                if (t is CarrierInterceptor ci && ci.carrierPawn == carrierPawn)
                {
                    return false;
                }
                if (t is Pawn p && (p.Dead || p.Downed))
                {
                    return false;
                }
                if (carrierPawn != null && !t.HostileTo(carrierPawn))
                {
                    return false;
                }
                return true;
            }
            if (!targ.Cell.InBounds(Map) || bareCellStrafeTicks >= 90)
            {
                return false;
            }
            return HasHostileNearCell(targ.Cell, 5.5f);
        }

        private bool HasHostileNearCell(IntVec3 cell, float radius)
        {
            if (Map == null || carrierPawn == null || !cell.InBounds(Map))
            {
                return false;
            }
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(cell, Map, radius, useCenter: true))
            {
                if (t == null || t.Destroyed || !t.Spawned || t == carrierPawn)
                {
                    continue;
                }
                if (t is Pawn p && (p.Dead || p.Downed))
                {
                    continue;
                }
                if (t.HostileTo(carrierPawn))
                {
                    return true;
                }
            }
            return false;
        }

        private void FireTwinPlasmaBeams(CompCarrierHangar hangar, Vector2 targetXZ)
        {
            ThingDef projDef = hangar.Props.beamProjectileDef
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("GCF_Bullet_InterceptorBeam")
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("Bullet_ChargeRifle");
            if (projDef == null || Map == null)
            {
                return;
            }

            float rad = kin.HeadingDeg * Mathf.Deg2Rad;
            Vector2 fwd = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
            Vector2 right = new Vector2(fwd.y, -fwd.x);

            LocalTargetInfo usedTarget = assignedTarget;
            if (assignedTarget.Thing is Pawn targetPawn
                && GiantFastLookup.TryGetRig(targetPawn) is CompGiantRig enemyGiant
                && enemyGiant.Footprint.hitCells.Count > 0)
            {
                IntVec3 bestCell = targetPawn.Position;
                float bestDistSq = float.MaxValue;
                IReadOnlyList<HitCellEntry> hitCells = enemyGiant.Footprint.hitCells;
                for (int i = 0; i < hitCells.Count; i++)
                {
                    IntVec3 c = hitCells[i].cell;
                    float dx = (c.x + 0.5f) - kin.WorldXZ.x;
                    float dz = (c.z + 0.5f) - kin.WorldXZ.y;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        bestCell = c;
                    }
                }
                usedTarget = new LocalTargetInfo(bestCell);
            }

            Thing effectiveLauncher = (carrierPawn != null && carrierPawn.Spawned && carrierPawn.Map == Map)
                ? (Thing)carrierPawn
                : this;

            for (int wing = -1; wing <= 1; wing += 2)
            {
                Vector2 muzzleXZ = kin.WorldXZ + fwd * 0.48f + right * (0.26f * wing);
                IntVec3 spawnCell = new Vector3(muzzleXZ.x, 0f, muzzleXZ.y).ToIntVec3();
                if (!spawnCell.InBounds(Map))
                {
                    spawnCell = Position;
                }

                if (GenSpawn.Spawn(projDef, spawnCell, Map) is Projectile proj)
                {
                    Vector3 origin = new Vector3(muzzleXZ.x, AltitudeLayer.Projectile.AltitudeFor(), muzzleXZ.y);
                    if (GiantVfxSystem.IsRigVfxEnabled(carrierPawn))
                    {
                        Vector3 beamEnd = new Vector3(targetXZ.x, origin.y, targetXZ.y);
                        Vector3 dBeam = beamEnd - origin;
                        dBeam.y = 0f;
                        if (dBeam.magnitude > 4.2f)
                        {
                            beamEnd = origin + dBeam.normalized * 4.2f;
                        }
                        GiantVfxSystem.SpawnOrdnanceTrailBeam(
                            Map,
                            origin,
                            beamEnd,
                            width: 0.62f,
                            durationTicks: 8,
                            baseColor: new Color(0.28f, 0.92f, 1.00f, 0.95f));
                        GiantVfxSystem.SpawnSoftGlowBurst(Map, origin, 0.55f, new Color(1.0f, 0.84f, 0.36f, 0.80f), solidTime: 0.04f);
                    }
                    proj.Launch(
                        launcher: effectiveLauncher,
                        origin: origin,
                        usedTarget: usedTarget,
                        intendedTarget: assignedTarget,
                        hitFlags: ProjectileHitFlags.IntendedTarget | ProjectileHitFlags.NonTargetPawns,
                        preventFriendlyFire: true);
                }
            }

            SoundDef shotSound = DefDatabase<SoundDef>.GetNamedSilentFail("Shot_ChargeRifle")
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Shot_ChargeBlaster");
            shotSound?.PlayOneShot(new TargetInfo(Position, Map));
        }

        private void DetonatePsionicOverload()
        {
            if (Map != null && Position.InBounds(Map) && GiantVfxSystem.IsRigVfxEnabled(carrierPawn))
            {
                Color overloadCol = new Color(0.26f, 0.88f, 1.00f, 0.98f);
                GiantVfxSystem.SpawnKhaydarinWarpPulse(Map, DrawPos, 1.45f, durationTicks: 18, primaryColor: overloadCol, isHexShieldWard: false);
                GiantVfxSystem.SpawnDetonationNova(Map, DrawPos, 1.25f, durationTicks: 16, baseColor: new Color(1.0f, 0.78f, 0.28f, 0.95f));
                GiantVfxSystem.BurstDirectionalSparkFlecks(Map, DrawPos, 0f, 180f, 6, 3.8f, 8.8f, 0.48f, overloadCol);
            }
            if (!Destroyed)
            {
                Destroy(DestroyMode.KillFinalize);
            }
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (!Spawned || Map == null || !UnityData.IsInMainThread || WorldRendererUtility.WorldRendered || Find.CurrentMap != Map)
            {
                return;
            }

            string interceptorTex = !string.IsNullOrEmpty(def?.graphicData?.texPath)
                ? def.graphicData.texPath
                : InterceptorTexPath;
            Material mat = GiantMaterialCache.Resolve(interceptorTex, Color.white, ShaderDatabase.Cutout);
            if (mat == null)
            {
                return;
            }

            // Textured aircraft silhouette shadow offset slightly south-east on the Shadows layer
            float bankScaleX = 1.48f * Mathf.Lerp(1f, 0.84f, Mathf.Abs(kin.BankDeg) / 35f);
            Quaternion fighterRot = Quaternion.Euler(0f, kin.HeadingDeg, 0f);
            Material shadowMat = GiantMaterialCache.Resolve(interceptorTex, new Color(0f, 0f, 0f, 0.28f), ShaderDatabase.Transparent);
            if (shadowMat != null)
            {
                Vector3 shadowPos = new Vector3(
                    kin.WorldXZ.x + 0.34f,
                    AltitudeLayer.Shadows.AltitudeFor() + 0.02f,
                    kin.WorldXZ.y - 0.40f);
                Graphics.DrawMesh(
                    MeshPool.plane10,
                    Matrix4x4.TRS(shadowPos, fighterRot, new Vector3(bankScaleX * 0.86f, 1f, 1.52f * 0.86f)),
                    shadowMat,
                    0);
            }

            // Render strictly above HighAirborne Carrier mothership & FogOfWar/Darkness
            Vector3 fighterPos = new Vector3(
                kin.WorldXZ.x,
                CompGiantRig.AirborneVfxBaseAltitudeY + (slotIndex % 4) * 0.008f,
                kin.WorldXZ.y);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(fighterPos, fighterRot, new Vector3(bankScaleX, 1f, 1.52f)), mat, 0);
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!string.IsNullOrWhiteSpace(baseStr))
            {
                sb.AppendLine(baseStr.Trim());
            }
            string stateKey = $"GCF_InterceptorState_{kin.State}";
            string stateLabel = stateKey.CanTranslate() ? stateKey.Translate().ToString() : kin.State.ToString();
            string line = "GCF_InterceptorInspectLine".CanTranslate()
                ? string.Format("GCF_InterceptorInspectLine".Translate(), shieldHp.ToString("F0"), maxShieldHp.ToString("F0"), stateLabel)
                : $"Plasma shield: {shieldHp:F0} / {maxShieldHp:F0} | State: {stateLabel}";
            sb.Append(line);
            return sb.ToString();
        }
    }

    public class CompProperties_CarrierHangar : CompProperties
    {
        public int maxInterceptors = 8;
        public string leftHangarBoneId = "hangar_bay_left";
        public string rightHangarBoneId = "hangar_bay_right";
        public string leftShutterBoneId = "hangar_door_left";
        public string rightShutterBoneId = "hangar_door_right";
        public float leftShutterOpenAngleDeg = 0f;
        public float rightShutterOpenAngleDeg = 0f;
        public Vector2 leftLaunchPixel = new Vector2(180f, 220f);
        public Vector2 rightLaunchPixel = new Vector2(332f, 220f);
        public ThingDef interceptorDef;
        public ThingDef beamProjectileDef;
        public ThingDef catapultWeaponDef;
        public int catapultLaunchIntervalTicks = 8;
        public int rebuildDurationTicks = 360;
        public float maxInterceptorHullHp = 95f;
        public float maxInterceptorShieldHp = 60f;
        public float interceptorCruiseSpeed = 16.5f;
        public float engagementRange = 32f;
        public float maxLeashRadius = 40f;
        public string launchAnimDefName = "GCF_Anim_Carrier_CatapultDispatch";
        public string recallAnimDefName = "GCF_Anim_Carrier_RecallDock";

        public CompProperties_CarrierHangar()
        {
            compClass = typeof(CompCarrierHangar);
        }
    }

    [StaticConstructorOnStartup]
    internal static class CarrierHangarGizmoAssets
    {
        private static Texture2D recallIcon;
        private static Texture2D autoLaunchIcon;
        private static Texture2D autoBuildIcon;

        static CarrierHangarGizmoAssets()
        {
        }

        public static Texture2D RecallIcon => recallIcon ??=
            (UnityData.IsInMainThread ? ContentFinder<Texture2D>.Get("UI/Abilities/CarrierRecallInterceptors", reportFailure: false) : null)
            ?? TexCommand.GatherSpotActive
            ?? BaseContent.BadTex;

        public static Texture2D AutoLaunchIcon => autoLaunchIcon ??=
            (UnityData.IsInMainThread ? ContentFinder<Texture2D>.Get("UI/Abilities/CarrierScrambleInterceptors", reportFailure: false) : null)
            ?? TexCommand.Attack
            ?? BaseContent.BadTex;

        public static Texture2D AutoBuildIcon => autoBuildIcon ??=
            (UnityData.IsInMainThread ? ContentFinder<Texture2D>.Get("Things/Pawn/GiantCreature/ProtossCarrier/Interceptor", reportFailure: false) : null)
            ?? TexCommand.Install
            ?? BaseContent.BadTex;
    }

    public class CompCarrierHangar : ThingComp
    {
        public CompProperties_CarrierHangar Props => (CompProperties_CarrierHangar)props;
        public Pawn CarrierPawn => parent as Pawn;

        private List<CarrierHangarSlot> slots = new List<CarrierHangarSlot>();
        private LocalTargetInfo squadTarget = LocalTargetInfo.Invalid;
        private int pendingScrambleCount;
        private int launchCooldownTicks;
        private int nextLaunchSide;
        private int overdriveTicksRemaining;
        private int recallHoldoffTicks;
        private int leftShutterHoldTicks;
        private int rightShutterHoldTicks;
        private float leftShutterOpen01;
        private float rightShutterOpen01;
        private bool autoLaunchEnabled = true;
        private bool autoRebuildEnabled = true;

        public bool IsOverdriveActive => overdriveTicksRemaining > 0;
        public bool AutoLaunchEnabled
        {
            get => autoLaunchEnabled;
            set => autoLaunchEnabled = value;
        }
        public bool AutoRebuildEnabled => autoRebuildEnabled;

        public IReadOnlyList<CarrierHangarSlot> Slots => slots;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref slots, "gcf_carrierSlots", LookMode.Deep);
            Scribe_TargetInfo.Look(ref squadTarget, "gcf_squadTarget");
            Scribe_Values.Look(ref pendingScrambleCount, "gcf_pendingScramble", 0);
            Scribe_Values.Look(ref launchCooldownTicks, "gcf_launchCooldown", 0);
            Scribe_Values.Look(ref nextLaunchSide, "gcf_nextLaunchSide", 0);
            Scribe_Values.Look(ref overdriveTicksRemaining, "gcf_overdriveTicks", 0);
            Scribe_Values.Look(ref autoLaunchEnabled, "gcf_autoLaunch", defaultValue: true);
            Scribe_Values.Look(ref autoRebuildEnabled, "gcf_autoRebuild", defaultValue: true);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsureSlotsInitialized();
            }
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            GiantFastLookup.RegisterCarrierHangar(CarrierPawn, this);
            EnsureSlotsInitialized();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            GiantFastLookup.RegisterCarrierHangar(CarrierPawn, this);
            EnsureSlotsInitialized();
            EnsureCatapultWeaponEquipped();
        }

        private void EnsureSlotsInitialized()
        {
            if (slots == null)
            {
                slots = new List<CarrierHangarSlot>();
            }
            int cap = Mathf.Max(2, Props.maxInterceptors);
            while (slots.Count < cap)
            {
                slots.Add(new CarrierHangarSlot
                {
                    slotIndex = slots.Count,
                    isBuilt = true,
                    storedHullHp = Props.maxInterceptorHullHp,
                    storedShieldHp = Props.maxInterceptorShieldHp
                });
            }
        }

        private void EnsureCatapultWeaponEquipped()
        {
            // GCF giants forbid local Pawn equipment weapons; Carrier hangar bays operate directly as part modules.
            Pawn pawn = CarrierPawn;
            if (pawn?.equipment != null && pawn.equipment.HasAnything())
            {
                pawn.equipment.DestroyAllEquipment();
            }
        }

        public bool IsBayIntact(int baySide)
        {
            Pawn pawn = CarrierPawn;
            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig == null)
            {
                return true;
            }
            string boneId = baySide == 0 ? Props.leftHangarBoneId : Props.rightHangarBoneId;
            return string.IsNullOrEmpty(boneId) || rig.IsBoneIntact(boneId);
        }

        public int IntactBayCount
        {
            get
            {
                int c = 0;
                if (IsBayIntact(0)) c++;
                if (IsBayIntact(1)) c++;
                return c;
            }
        }

        public int MaxAllowedInterceptors
        {
            get
            {
                EnsureSlotsInitialized();
                int count = 0;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (IsBayIntact(slots[i].BaySide))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public int ActiveAirborneCount
        {
            get
            {
                EnsureSlotsInitialized();
                int count = 0;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].IsAirborne) count++;
                }
                return count;
            }
        }

        public int DockedReadyCount
        {
            get
            {
                EnsureSlotsInitialized();
                int count = 0;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (IsBayIntact(slots[i].BaySide) && slots[i].IsDockedReady)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public Vector2 GetLiveBayWorldXZ(int baySide, out float carrierHeadingDeg)
        {
            Pawn pawn = CarrierPawn;
            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig?.RigDef != null)
            {
                carrierHeadingDeg = rig.CurrentHeadingDeg;
                int actualSide = IsBayIntact(baySide) ? baySide : (1 - baySide);
                string boneId = actualSide == 0 ? Props.leftHangarBoneId : Props.rightHangarBoneId;
                Vector2 px = actualSide == 0 ? Props.leftLaunchPixel : Props.rightLaunchPixel;
                if (rig.TryGetBonePixelWorldPos(boneId, px, out Vector2 liveBayXZ, out _))
                {
                    return liveBayXZ;
                }
            }

            Vector3 fallbackPos = pawn != null ? CompGiantRig.GetSafeDrawPos(pawn) : Vector3.zero;
            carrierHeadingDeg = pawn != null ? pawn.Rotation.AsAngle : 0f;
            return new Vector2(fallbackPos.x, fallbackPos.z);
        }

        public void CommandAttackTarget(LocalTargetInfo target, int overdriveDurationTicks = 0, int immediateBurstCount = 0)
        {
            Pawn pawn = CarrierPawn;
            if (pawn == null || pawn.Dead || !pawn.Spawned || MaxAllowedInterceptors <= 0)
            {
                return;
            }
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit &&
                (cockpit.IsDormantUnmanned || !cockpit.CanFirePrimaryWeapons))
            {
                return;
            }

            squadTarget = target;
            recallHoldoffTicks = 0;
            if (overdriveDurationTicks > overdriveTicksRemaining)
            {
                overdriveTicksRemaining = overdriveDurationTicks;
            }

            // Redirect all currently airborne Interceptors onto the focus target and cancel any pending recall
            EnsureSlotsInitialized();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].IsAirborne)
                {
                    CarrierInterceptor fighter = slots[i].activeInterceptor;
                    fighter.assignedTarget = target;
                    if (fighter.recallRequested)
                    {
                        fighter.recallRequested = false;
                        fighter.kin.State = InterceptorFlightState.AttackRun;
                        fighter.kin.StateTicks = 0;
                    }
                }
            }

            int ready = DockedReadyCount;
            if (ready > 0)
            {
                pendingScrambleCount = ready;
                if (immediateBurstCount > 0)
                {
                    int burst = Mathf.Min(immediateBurstCount, ready);
                    for (int b = 0; b < burst; b++)
                    {
                        if (!TryLaunchSingleDockedInterceptor(target))
                        {
                            break;
                        }
                        if (pendingScrambleCount > 0)
                        {
                            pendingScrambleCount--;
                        }
                    }
                }
            }
        }

        public int RecallAllInterceptors(float bonusShieldRestore = 0f, bool holdAutoLaunch = false)
        {
            pendingScrambleCount = 0;
            recallHoldoffTicks = 220;
            if (holdAutoLaunch)
            {
                autoLaunchEnabled = false;
            }
            squadTarget = LocalTargetInfo.Invalid;
            EnsureSlotsInitialized();

            int recalled = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].IsAirborne)
                {
                    CarrierInterceptor fighter = slots[i].activeInterceptor;
                    if (fighter.recallRequested)
                    {
                        continue;
                    }
                    if (bonusShieldRestore > 0f)
                    {
                        fighter.shieldHp = Mathf.Min(fighter.maxShieldHp, fighter.shieldHp + bonusShieldRestore);
                    }
                    fighter.OrderRecall();
                    recalled++;
                }
            }

            if (recalled > 0 && CarrierPawn != null && CarrierPawn.Spawned)
            {
                if (CarrierPawn.Map != null && GiantVfxSystem.IsRigVfxEnabled(CarrierPawn))
                {
                    Color khaydarinCyan = new Color(0.26f, 0.90f, 1.00f, 0.98f);
                    float yAlt = CompGiantRig.GetSafeDrawPos(CarrierPawn).y;
                    bool emittedFromBay = false;
                    for (int side = 0; side <= 1; side++)
                    {
                        if (IsBayIntact(side))
                        {
                            Vector2 bayXZ = GetLiveBayWorldXZ(side, out _);
                            Vector3 bayPos = new Vector3(bayXZ.x, yAlt, bayXZ.y);
                            GiantVfxSystem.SpawnKhaydarinWarpPulse(CarrierPawn.Map, bayPos, 2.25f, durationTicks: 24, primaryColor: khaydarinCyan, isHexShieldWard: false, followThing: CarrierPawn);
                            GiantVfxSystem.SpawnShockRing(CarrierPawn.Map, bayPos, 0.35f, 2.95f, new Color(1.0f, 0.82f, 0.32f, 0.92f), durationTicks: 20, baseWidth: 0.38f, followThing: CarrierPawn);
                            emittedFromBay = true;
                        }
                    }
                    if (!emittedFromBay)
                    {
                        Vector3 beaconPos = CompGiantRig.GetSafeDrawPos(CarrierPawn);
                        GiantVfxSystem.SpawnKhaydarinWarpPulse(CarrierPawn.Map, beaconPos, 2.45f, durationTicks: 22, primaryColor: khaydarinCyan, isHexShieldWard: false, followThing: CarrierPawn);
                    }
                }
                SoundDef recallSound = DefDatabase<SoundDef>.GetNamedSilentFail("Psycast_Skip_Exit")
                    ?? DefDatabase<SoundDef>.GetNamedSilentFail("Interact_ChargeRifle");
                recallSound?.PlayOneShot(new TargetInfo(CarrierPawn.Position, CarrierPawn.Map));
            }
            return recalled;
        }

        public void NotifyInterceptorDocked(CarrierInterceptor interceptor)
        {
            if (interceptor == null)
            {
                return;
            }

            EnsureSlotsInitialized();
            int dockSide = interceptor.slotIndex & 1;
            int actualDockSide = IsBayIntact(dockSide) ? dockSide : (1 - dockSide);
            if (actualDockSide == 0 && IsBayIntact(0))
            {
                leftShutterHoldTicks = Mathf.Max(leftShutterHoldTicks, 10);
            }
            else if (actualDockSide == 1 && IsBayIntact(1))
            {
                rightShutterHoldTicks = Mathf.Max(rightShutterHoldTicks, 10);
            }

            CarrierHangarSlot matchedSlot = null;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].activeInterceptor == interceptor)
                {
                    matchedSlot = slots[i];
                    break;
                }
            }
            if (matchedSlot == null)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].slotIndex == interceptor.slotIndex && slots[i].activeInterceptor == null)
                    {
                        matchedSlot = slots[i];
                        break;
                    }
                }
            }
            if (matchedSlot != null)
            {
                matchedSlot.storedHullHp = Mathf.Clamp(interceptor.HitPoints, 1f, Props.maxInterceptorHullHp);
                matchedSlot.storedShieldHp = Mathf.Clamp(interceptor.shieldHp, 0f, Props.maxInterceptorShieldHp);
                matchedSlot.activeInterceptor = null;
                matchedSlot.isBuilt = true;
            }

            if (CarrierPawn?.Map != null && interceptor.Spawned && GiantVfxSystem.IsRigVfxEnabled(CarrierPawn))
            {
                GiantVfxSystem.SpawnKhaydarinWarpPulse(
                    CarrierPawn.Map,
                    interceptor.DrawPos,
                    radius: 0.95f,
                    durationTicks: 12,
                    primaryColor: new Color(0.28f, 0.90f, 1.00f, 0.92f),
                    isHexShieldWard: false);
            }

            if (!interceptor.Destroyed)
            {
                interceptor.Destroy(DestroyMode.Vanish);
            }
        }

        public LocalTargetInfo ResolveNextInterceptorTarget(Vector2 referenceXZ)
        {
            Pawn carrier = CarrierPawn;
            if (carrier == null || !carrier.Spawned || carrier.Map == null)
            {
                return LocalTargetInfo.Invalid;
            }

            if (IsValidHostileTarget(squadTarget))
            {
                return squadTarget;
            }

            Thing mindEnemy = carrier.mindState?.enemyTarget;
            if (mindEnemy != null && IsValidHostileTarget(new LocalTargetInfo(mindEnemy)))
            {
                squadTarget = new LocalTargetInfo(mindEnemy);
                return squadTarget;
            }

            IAttackTarget found = GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                carrier,
                TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable,
                validator: null,
                minDist: 0f,
                maxDist: Props.engagementRange,
                locus: IntVec3.Invalid,
                maxTravelRadiusFromLocus: float.MaxValue);
            if (found?.Thing != null)
            {
                squadTarget = new LocalTargetInfo(found.Thing);
                return squadTarget;
            }

            return LocalTargetInfo.Invalid;
        }

        private bool IsValidHostileTarget(LocalTargetInfo targ)
        {
            Pawn carrier = CarrierPawn;
            if (carrier?.Map == null || !targ.IsValid)
            {
                return false;
            }
            if (targ.HasThing)
            {
                Thing t = targ.Thing;
                if (t == null || t.Destroyed || !t.Spawned || t.Map != carrier.Map || t == carrier)
                {
                    return false;
                }
                if (t is Pawn p && (p.Dead || p.Downed))
                {
                    return false;
                }
                if (carrier.Position.DistanceTo(t.Position) > Props.maxLeashRadius)
                {
                    return false;
                }
                return t.HostileTo(carrier);
            }
            return false;
        }

        private bool TryLaunchSingleDockedInterceptor(LocalTargetInfo target)
        {
            Pawn carrier = CarrierPawn;
            if (carrier?.Map == null || !carrier.Spawned || carrier.Dead || carrier.Downed)
            {
                return false;
            }

            ThingDef fighterDef = Props.interceptorDef
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("GCF_CarrierInterceptor");
            if (fighterDef == null)
            {
                return false;
            }

            EnsureSlotsInitialized();
            CarrierHangarSlot chosenSlot = null;
            for (int pass = 0; pass < 2 && chosenSlot == null; pass++)
            {
                int desiredSide = pass == 0 ? (nextLaunchSide & 1) : (1 - (nextLaunchSide & 1));
                if (!IsBayIntact(desiredSide))
                {
                    continue;
                }
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].BaySide == desiredSide && slots[i].IsDockedReady)
                    {
                        chosenSlot = slots[i];
                        break;
                    }
                }
            }

            if (chosenSlot == null)
            {
                return false;
            }

            nextLaunchSide = 1 - chosenSlot.BaySide;
            int actualLaunchSide = IsBayIntact(chosenSlot.BaySide) ? chosenSlot.BaySide : (1 - chosenSlot.BaySide);
            if (actualLaunchSide == 0)
            {
                leftShutterHoldTicks = Mathf.Max(leftShutterHoldTicks, 16);
            }
            else
            {
                rightShutterHoldTicks = Mathf.Max(rightShutterHoldTicks, 16);
            }
            Vector2 launchXZ = GetLiveBayWorldXZ(chosenSlot.BaySide, out float carrierHeadingDeg);
            IntVec3 spawnCell = new Vector3(
                Mathf.Clamp(launchXZ.x, 1.5f, carrier.Map.Size.x - 1.5f),
                0f,
                Mathf.Clamp(launchXZ.y, 1.5f, carrier.Map.Size.z - 1.5f)).ToIntVec3();
            if (!spawnCell.InBounds(carrier.Map))
            {
                spawnCell = carrier.Position;
            }

            if (!(ThingMaker.MakeThing(fighterDef) is CarrierInterceptor fighter))
            {
                return false;
            }

            if (carrier.Faction != null && fighter.def.CanHaveFaction)
            {
                fighter.SetFactionDirect(carrier.Faction);
            }

            float launchAngle = carrierHeadingDeg + (chosenSlot.BaySide == 0 ? -18f : 18f);
            fighter.InitializeSortie(
                carrier,
                chosenSlot.slotIndex,
                launchXZ,
                launchAngle,
                chosenSlot.storedHullHp,
                chosenSlot.storedShieldHp,
                Props.maxInterceptorShieldHp,
                target);

            GenSpawn.Spawn(fighter, spawnCell, carrier.Map, Rot4.North, WipeMode.Vanish);
            chosenSlot.activeInterceptor = fighter;

            if (GiantVfxSystem.IsRigVfxEnabled(carrier))
            {
                Color catapultCyan = new Color(0.28f, 0.92f, 1.00f, 0.96f);
                GiantVfxSystem.SpawnKhaydarinWarpPulse(carrier.Map, fighter.DrawPos, 1.25f, durationTicks: 14, primaryColor: catapultCyan, isHexShieldWard: false);
                GiantVfxSystem.BurstDirectionalSparkFlecks(carrier.Map, fighter.DrawPos, launchAngle, 35f, 4, 4.2f, 9.2f, 0.46f, new Color(1.0f, 0.84f, 0.36f, 1f));
            }
            SoundDef launchSound = DefDatabase<SoundDef>.GetNamedSilentFail("Psycast_Skip_Entry")
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Shot_ChargeBlaster");
            launchSound?.PlayOneShot(new TargetInfo(spawnCell, carrier.Map));
            return true;
        }

        public override void CompTick()
        {
            base.CompTick();
            Pawn carrier = CarrierPawn;
            if (carrier == null || !carrier.Spawned || carrier.Map == null || carrier.Dead)
            {
                return;
            }

            EnsureSlotsInitialized();
            if (overdriveTicksRemaining > 0)
            {
                overdriveTicksRemaining--;
            }
            if (recallHoldoffTicks > 0)
            {
                recallHoldoffTicks--;
            }
            if (launchCooldownTicks > 0)
            {
                launchCooldownTicks--;
            }

            // Clean up destroyed airborne Interceptors, severing-excess Interceptors, and repair/rebuild docked slots
            CarrierHangarSlot rebuildingSlot = null;
            for (int i = 0; i < slots.Count; i++)
            {
                CarrierHangarSlot slot = slots[i];
                bool bayIntact = IsBayIntact(slot.BaySide);

                if (slot.activeInterceptor != null && (slot.activeInterceptor.Destroyed || !slot.activeInterceptor.Spawned))
                {
                    slot.activeInterceptor = null;
                    slot.isBuilt = false;
                    slot.buildTicksProgress = 0;
                    slot.storedHullHp = Props.maxInterceptorHullHp;
                    slot.storedShieldHp = Props.maxInterceptorShieldHp;
                }

                // If this slot's hangar bay was severed while its Interceptor was airborne and both bays are gone, overload it
                if (!bayIntact && IntactBayCount == 0 && slot.IsAirborne)
                {
                    slot.activeInterceptor.Destroy(DestroyMode.KillFinalize);
                    slot.activeInterceptor = null;
                    slot.isBuilt = false;
                }

                // Rapid hangar repair & Protoss plasma shield recharge while docked
                if (slot.IsDockedReady && bayIntact)
                {
                    if (slot.storedShieldHp < Props.maxInterceptorShieldHp)
                    {
                        slot.storedShieldHp = Mathf.Min(Props.maxInterceptorShieldHp, slot.storedShieldHp + 0.55f);
                    }
                    if (slot.storedHullHp < Props.maxInterceptorHullHp)
                    {
                        slot.storedHullHp = Mathf.Min(Props.maxInterceptorHullHp, slot.storedHullHp + 0.35f);
                    }
                }

                if (!slot.isBuilt && bayIntact && rebuildingSlot == null)
                {
                    rebuildingSlot = slot;
                }
            }

            if (autoRebuildEnabled && rebuildingSlot != null && !carrier.Downed)
            {
                rebuildingSlot.buildTicksProgress++;
                if (rebuildingSlot.buildTicksProgress >= Mathf.Max(60, Props.rebuildDurationTicks))
                {
                    rebuildingSlot.isBuilt = true;
                    rebuildingSlot.buildTicksProgress = 0;
                    rebuildingSlot.storedHullHp = Props.maxInterceptorHullHp;
                    rebuildingSlot.storedShieldHp = Props.maxInterceptorShieldHp;
                }
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(carrier);
            bool hangarWeaponsOnline = cockpit == null || (!cockpit.IsDormantUnmanned && cockpit.CanFirePrimaryWeapons);
            if (cockpit != null && cockpit.IsDormantUnmanned && ActiveAirborneCount > 0)
            {
                bool anyNotYetRecalling = false;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].IsAirborne && !slots[i].activeInterceptor.recallRequested)
                    {
                        anyNotYetRecalling = true;
                        break;
                    }
                }
                if (anyNotYetRecalling)
                {
                    RecallAllInterceptors();
                }
            }

            // Autonomous StarCraft II Carrier AI auto-scramble scan (decoupled from whole-hull animations)
            if (hangarWeaponsOnline && autoLaunchEnabled && recallHoldoffTicks <= 0 && !carrier.Downed && MaxAllowedInterceptors > 0 && carrier.IsHashIntervalTick(20))
            {
                LocalTargetInfo threat = ResolveNextInterceptorTarget(
                    new Vector2(carrier.DrawPos.x, carrier.DrawPos.z));
                if (threat.IsValid && DockedReadyCount > 0 && pendingScrambleCount <= 0)
                {
                    CommandAttackTarget(threat);
                }
            }

            // Execute staggered Graviton Catapult launches
            if (hangarWeaponsOnline && pendingScrambleCount > 0 && launchCooldownTicks <= 0 && !carrier.Downed)
            {
                LocalTargetInfo launchTarg = IsValidHostileTarget(squadTarget)
                    ? squadTarget
                    : ResolveNextInterceptorTarget(new Vector2(carrier.DrawPos.x, carrier.DrawPos.z));

                if (launchTarg.IsValid && TryLaunchSingleDockedInterceptor(launchTarg))
                {
                    pendingScrambleCount--;
                    int baseInterval = Mathf.Max(3, Props.catapultLaunchIntervalTicks);
                    launchCooldownTicks = IsOverdriveActive ? Mathf.Max(2, baseInterval / 2) : baseInterval;
                }
                else
                {
                    pendingScrambleCount = 0;
                }
            }

            UpdateHangarShutters(carrier);
        }

        private void UpdateHangarShutters(Pawn carrier)
        {
            if (leftShutterHoldTicks > 0) leftShutterHoldTicks--;
            if (rightShutterHoldTicks > 0) rightShutterHoldTicks--;

            bool inboundLeft = false;
            bool inboundRight = false;
            Vector2 leftBayXZ = GetLiveBayWorldXZ(0, out _);
            Vector2 rightBayXZ = GetLiveBayWorldXZ(1, out _);
            for (int i = 0; i < slots.Count; i++)
            {
                CarrierInterceptor f = slots[i].activeInterceptor;
                if (f != null && !f.Destroyed && f.Spawned && f.kin.State == InterceptorFlightState.Returning)
                {
                    int dockSide = f.slotIndex & 1;
                    int actualSide = IsBayIntact(dockSide) ? dockSide : (1 - dockSide);
                    Vector2 bayXZ = actualSide == 0 ? leftBayXZ : rightBayXZ;
                    if ((f.kin.WorldXZ - bayXZ).sqrMagnitude <= 16f)
                    {
                        if (actualSide == 0) inboundLeft = true;
                        else inboundRight = true;
                    }
                }
            }

            bool wantLeftOpen = IsBayIntact(0) && (leftShutterHoldTicks > 0 || inboundLeft);
            bool wantRightOpen = IsBayIntact(1) && (rightShutterHoldTicks > 0 || inboundRight);
            leftShutterOpen01 = Mathf.MoveTowards(leftShutterOpen01, wantLeftOpen ? 1f : 0f, wantLeftOpen ? 0.24f : 0.10f);
            rightShutterOpen01 = Mathf.MoveTowards(rightShutterOpen01, wantRightOpen ? 1f : 0f, wantRightOpen ? 0.24f : 0.10f);
            float easeLeft = leftShutterOpen01 * leftShutterOpen01 * (3f - 2f * leftShutterOpen01);
            float easeRight = rightShutterOpen01 * rightShutterOpen01 * (3f - 2f * rightShutterOpen01);

            CompGiantRig rig = GiantFastLookup.TryGetRig(carrier);
            if (rig != null)
            {
                if (!string.IsNullOrEmpty(Props.leftShutterBoneId))
                {
                    rig.SetProceduralBoneAngleOffset(Props.leftShutterBoneId, IsBayIntact(0) ? easeLeft * Props.leftShutterOpenAngleDeg : 0f);
                }
                if (!string.IsNullOrEmpty(Props.rightShutterBoneId))
                {
                    rig.SetProceduralBoneAngleOffset(Props.rightShutterBoneId, IsBayIntact(1) ? easeRight * Props.rightShutterOpenAngleDeg : 0f);
                }
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            EnsureSlotsInitialized();
            for (int i = 0; i < slots.Count; i++)
            {
                CarrierHangarSlot slot = slots[i];
                if (slot.IsAirborne)
                {
                    if (carrierPawnSurvived(mode))
                    {
                        slot.storedHullHp = slot.activeInterceptor.HitPoints;
                        slot.storedShieldHp = slot.activeInterceptor.shieldHp;
                        slot.activeInterceptor.Destroy(DestroyMode.Vanish);
                        slot.activeInterceptor = null;
                        slot.isBuilt = true;
                    }
                    else
                    {
                        slot.activeInterceptor.Destroy(DestroyMode.KillFinalize);
                        slot.activeInterceptor = null;
                    }
                }
            }
            squadTarget = LocalTargetInfo.Invalid;
            pendingScrambleCount = 0;
            recallHoldoffTicks = 0;
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            GiantFastLookup.UnregisterCarrierHangar(CarrierPawn);
            base.PostDestroy(mode, previousMap);
        }

        private bool carrierPawnSurvived(DestroyMode mode)
        {
            return CarrierPawn != null && !CarrierPawn.Dead && mode != DestroyMode.KillFinalize;
        }

        public override string CompInspectStringExtra()
        {
            Pawn carrier = CarrierPawn;
            if (carrier == null || carrier.Dead)
            {
                return null;
            }

            int active = ActiveAirborneCount;
            int docked = DockedReadyCount;
            int maxAllowed = MaxAllowedInterceptors;

            var sb = new StringBuilder();
            if (maxAllowed <= 0)
            {
                string offline = "GCF_CarrierHangarDestroyed".CanTranslate()
                    ? "GCF_CarrierHangarDestroyed".Translate().ToString()
                    : "Interceptor hangars: Offline (both hangar bays severed)";
                sb.Append(offline);
                return sb.ToString();
            }

            string statusLine = "GCF_CarrierHangarStatus".CanTranslate()
                ? string.Format("GCF_CarrierHangarStatus".Translate(), active, docked, maxAllowed, Props.maxInterceptors)
                : $"Interceptors: {active} airborne / {docked} docked (Cap {maxAllowed}/{Props.maxInterceptors})";
            sb.Append(statusLine);

            for (int i = 0; i < slots.Count; i++)
            {
                if (!slots[i].isBuilt && IsBayIntact(slots[i].BaySide))
                {
                    int pct = Mathf.Clamp(
                        Mathf.RoundToInt(100f * slots[i].buildTicksProgress / Mathf.Max(1f, Props.rebuildDurationTicks)),
                        0,
                        99);
                    sb.Append(" | ");
                    string buildStr = "GCF_CarrierRebuildingInterceptor".CanTranslate()
                        ? string.Format("GCF_CarrierRebuildingInterceptor".Translate(), pct)
                        : $"Fabricating: {pct}%";
                    sb.Append(buildStr);
                    break;
                }
            }

            if (IsOverdriveActive)
            {
                sb.Append(" | ");
                string odStr = "GCF_CarrierCatapultOverdriveActive".CanTranslate()
                    ? string.Format("GCF_CarrierCatapultOverdriveActive".Translate(), (overdriveTicksRemaining / 60f).ToString("F1"))
                    : $"Graviton Overdrive ({overdriveTicksRemaining / 60f:F1}s)";
                sb.Append(odStr);
            }

            return sb.ToString();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            Pawn pawn = CarrierPawn;
            if (pawn == null || pawn.Dead || !pawn.Spawned)
            {
                yield break;
            }

            bool canControl = pawn.Faction == Faction.OfPlayerSilentFail && GiantPlayerControlUtility.CanControl(pawn);
            if (!canControl)
            {
                yield break;
            }

            int airborne = ActiveAirborneCount;
            if (airborne > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "GCF_Gizmo_RecallInterceptors_Label".CanTranslate()
                        ? string.Format("GCF_Gizmo_RecallInterceptors_Label".Translate(), airborne)
                        : $"Recall Interceptors ({airborne})",
                    defaultDesc = "GCF_Gizmo_RecallInterceptors_Desc".CanTranslate()
                        ? "GCF_Gizmo_RecallInterceptors_Desc".Translate().ToString()
                        : "Immediately order all airborne Interceptors to break off attack runs and dock back into the Carrier's hangar bays for shield recharge.",
                    icon = CarrierHangarGizmoAssets.RecallIcon ?? BaseContent.BadTex,
                    action = () =>
                    {
                        autoLaunchEnabled = false;
                        RecallAllInterceptors(bonusShieldRestore: 0f);
                    }
                };
            }

            if (pawn.Drafted || !autoLaunchEnabled)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "GCF_Gizmo_AutoLaunch_Label".CanTranslate()
                        ? "GCF_Gizmo_AutoLaunch_Label".Translate().ToString()
                        : "Auto-Scramble Squadron",
                    defaultDesc = "GCF_Gizmo_AutoLaunch_Desc".CanTranslate()
                        ? "GCF_Gizmo_AutoLaunch_Desc".Translate().ToString()
                        : "When enabled, the Carrier automatically launches docked Interceptors against hostile targets in range. Disable to keep Interceptors docked until manually ordered.",
                    icon = CarrierHangarGizmoAssets.AutoLaunchIcon ?? BaseContent.BadTex,
                    isActive = () => autoLaunchEnabled,
                    toggleAction = () => autoLaunchEnabled = !autoLaunchEnabled
                };
            }
        }
    }

    /// <summary>
    /// Integrated Graviton Catapult command verb for GCF_ProtossCarrier.
    /// Allows standard RimWorld attack orders and AI standoff combat at 32-cell carrier range without
    /// requiring direct ground line-of-sight, dispatching the Carrier's Interceptor squadron onto the target.
    /// </summary>
    public class Verb_CarrierDispatchInterceptors : Verb_LaunchProjectile
    {
        public override bool Available()
        {
            if (!base.Available())
            {
                return false;
            }

            CompCarrierHangar hangar = CasterPawn?.TryGetComp<CompCarrierHangar>();
            if (hangar == null)
            {
                return false;
            }
            return hangar.MaxAllowedInterceptors > 0
                && (hangar.ActiveAirborneCount > 0 || hangar.DockedReadyCount > 0);
        }

        protected override bool TryCastShot()
        {
            Pawn caster = CasterPawn;
            CompCarrierHangar hangar = caster?.TryGetComp<CompCarrierHangar>();
            if (hangar == null || !currentTarget.IsValid)
            {
                return false;
            }

            hangar.CommandAttackTarget(currentTarget, overdriveDurationTicks: 0, immediateBurstCount: 2);
            if (caster.Map != null && currentTarget.Cell.InBounds(caster.Map) && GiantVfxSystem.IsRigVfxEnabled(caster))
            {
                Vector3 lockPos = currentTarget.Cell.ToVector3Shifted();
                GiantVfxSystem.SpawnKhaydarinWarpPulse(caster.Map, lockPos, 1.65f, durationTicks: 16, primaryColor: new Color(0.28f, 0.90f, 1.00f, 0.92f), isHexShieldWard: true);
            }
            return true;
        }
    }

    public class CompProperties_AbilityCarrierScramble : CompProperties_AbilityEffect
    {
        public int overdriveDurationTicks = 600;
        public int immediateCatapultCount = 4;
        public string animDefName = "GCF_Anim_Carrier_ScrambleOverdrive";

        public CompProperties_AbilityCarrierScramble()
        {
            compClass = typeof(CompAbilityEffect_CarrierScramble);
        }
    }

    public class CompAbilityEffect_CarrierScramble : CompAbilityEffect
    {
        public new CompProperties_AbilityCarrierScramble Props => (CompProperties_AbilityCarrierScramble)props;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent?.pawn;
            if (caster == null || caster.Map == null || !target.IsValid)
            {
                return;
            }

            CompCarrierHangar hangar = caster.TryGetComp<CompCarrierHangar>();
            hangar?.CommandAttackTarget(
                target,
                overdriveDurationTicks: Props.overdriveDurationTicks,
                immediateBurstCount: Props.immediateCatapultCount);

            if (target.Cell.InBounds(caster.Map) && GiantVfxSystem.IsRigVfxEnabled(caster))
            {
                float yAlt = CompGiantRig.GetSafeDrawPos(caster).y;
                Vector3 targPos = target.Cell.ToVector3Shifted();
                Color overdriveCol = new Color(1.00f, 0.80f, 0.28f, 0.98f);
                Color khaydarinCol = new Color(0.26f, 0.92f, 1.00f, 0.98f);
                if (hangar != null)
                {
                    for (int side = 0; side <= 1; side++)
                    {
                        if (hangar.IsBayIntact(side))
                        {
                            Vector2 bayXZ = hangar.GetLiveBayWorldXZ(side, out _);
                            GiantVfxSystem.SpawnKhaydarinWarpPulse(caster.Map, new Vector3(bayXZ.x, yAlt, bayXZ.y), 2.15f, durationTicks: 22, primaryColor: overdriveCol, isHexShieldWard: false, followThing: caster);
                        }
                    }
                }
                GiantVfxSystem.SpawnKhaydarinWarpPulse(caster.Map, targPos, 2.45f, durationTicks: 22, primaryColor: khaydarinCol, isHexShieldWard: true);
                GiantVfxSystem.SpawnShockRing(caster.Map, targPos, 0.35f, 3.35f, overdriveCol, durationTicks: 19, baseWidth: 0.42f);
            }
        }

        public override bool GizmoDisabled(out string reason)
        {
            CompCarrierHangar hangar = parent?.pawn?.TryGetComp<CompCarrierHangar>();
            if (hangar != null && hangar.MaxAllowedInterceptors <= 0)
            {
                reason = "GCF_CarrierHangarDestroyed".CanTranslate()
                    ? "GCF_CarrierHangarDestroyed".Translate().ToString()
                    : "Both hangar bays severed.";
                return true;
            }
            if (hangar != null && hangar.ActiveAirborneCount <= 0 && hangar.DockedReadyCount <= 0)
            {
                reason = "GCF_CarrierNoInterceptorsReady".CanTranslate()
                    ? "GCF_CarrierNoInterceptorsReady".Translate().ToString()
                    : "No Interceptors fabricated.";
                return true;
            }
            return base.GizmoDisabled(out reason);
        }

        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            Pawn caster = parent?.pawn;
            CompCarrierHangar hangar = caster?.TryGetComp<CompCarrierHangar>();
            if (caster == null || hangar == null || hangar.MaxAllowedInterceptors <= 0)
            {
                return false;
            }
            if (hangar.DockedReadyCount <= 0 && hangar.IsOverdriveActive)
            {
                return false;
            }
            return target.Thing is Pawn victim && !victim.Dead && !victim.Downed && victim.HostileTo(caster);
        }
    }

    public class CompProperties_AbilityCarrierRecall : CompProperties_AbilityEffect
    {
        public float bonusShieldRestore = 35f;
        public string animDefName = "GCF_Anim_Carrier_RecallDock";

        public CompProperties_AbilityCarrierRecall()
        {
            compClass = typeof(CompAbilityEffect_CarrierRecall);
        }
    }

    public class CompAbilityEffect_CarrierRecall : CompAbilityEffect
    {
        public new CompProperties_AbilityCarrierRecall Props => (CompProperties_AbilityCarrierRecall)props;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent?.pawn;
            if (caster == null || caster.Map == null)
            {
                return;
            }

            CompCarrierHangar hangar = caster.TryGetComp<CompCarrierHangar>();
            hangar?.RecallAllInterceptors(Props.bonusShieldRestore);
        }

        public override bool GizmoDisabled(out string reason)
        {
            CompCarrierHangar hangar = parent?.pawn?.TryGetComp<CompCarrierHangar>();
            if (hangar == null || hangar.ActiveAirborneCount <= 0)
            {
                reason = "GCF_Gizmo_RecallDisabledNoneAirborne".CanTranslate()
                    ? "GCF_Gizmo_RecallDisabledNoneAirborne".Translate().ToString()
                    : "No airborne Interceptors.";
                return true;
            }
            return base.GizmoDisabled(out reason);
        }

        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            // AI keeps Interceptors attacking until combat concludes; recall ability is reserved for tactical player control.
            return false;
        }
    }
}
