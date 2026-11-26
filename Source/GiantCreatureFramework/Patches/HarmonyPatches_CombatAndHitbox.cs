using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public static class PendingBoneHitContext
    {
        public struct ScopeState
        {
            public bool DidPush;
            public Pawn PrevPawn;
            public CompGiantRig PrevComp;
            public int PrevBoneIndex;
            public BodyPartRecord PrevBodyPart;
            public Vector3? PrevHitWorldPos;
        }

        [ThreadStatic]
        public static Pawn TargetPawn;

        [ThreadStatic]
        public static CompGiantRig TargetComp;

        [ThreadStatic]
        public static int TargetBoneIndex = -1;

        [ThreadStatic]
        public static BodyPartRecord TargetBodyPart;

        [ThreadStatic]
        public static Vector3? TargetHitWorldPos;

        public static ScopeState Push(Pawn pawn, CompGiantRig comp, int boneIdx, BodyPartRecord part, Vector3? hitWorldPos = null)
        {
            var prev = new ScopeState
            {
                DidPush = true,
                PrevPawn = TargetPawn,
                PrevComp = TargetComp,
                PrevBoneIndex = TargetBoneIndex,
                PrevBodyPart = TargetBodyPart,
                PrevHitWorldPos = TargetHitWorldPos
            };
            TargetPawn = pawn;
            TargetComp = comp;
            TargetBoneIndex = boneIdx;
            TargetBodyPart = part;
            TargetHitWorldPos = hitWorldPos;
            return prev;
        }

        public static void Pop(in ScopeState prev)
        {
            if (!prev.DidPush)
            {
                return;
            }
            TargetPawn = prev.PrevPawn;
            TargetComp = prev.PrevComp;
            TargetBoneIndex = prev.PrevBoneIndex;
            TargetBodyPart = prev.PrevBodyPart;
            TargetHitWorldPos = prev.PrevHitWorldPos;
        }

        public static void Set(Pawn pawn, CompGiantRig comp, int boneIdx, BodyPartRecord part, Vector3? hitWorldPos = null)
        {
            TargetPawn = pawn;
            TargetComp = comp;
            TargetBoneIndex = boneIdx;
            TargetBodyPart = part;
            TargetHitWorldPos = hitWorldPos;
        }

        public static void Clear()
        {
            TargetPawn = null;
            TargetComp = null;
            TargetBoneIndex = -1;
            TargetBodyPart = null;
            TargetHitWorldPos = null;
        }
    }

    [HarmonyPatch]
    public static class HarmonyPatches_CombatAndHitbox
    {
        private static readonly MethodInfo ProjectileCanHitMethod =
            AccessTools.Method(typeof(Projectile), "CanHit", new[] { typeof(Thing) });

        private static readonly Func<Projectile, Thing, bool> ProjectileCanHitFast =
            ProjectileCanHitMethod != null
                ? (Func<Projectile, Thing, bool>)Delegate.CreateDelegate(typeof(Func<Projectile, Thing, bool>), ProjectileCanHitMethod)
                : null;

        private static readonly MethodInfo ProjectileImpactMethod =
            AccessTools.Method(typeof(Projectile), "Impact", new[] { typeof(Thing), typeof(bool) });

        private static readonly Action<Projectile, Thing, bool> ProjectileImpactFast =
            CreateProjectileImpactDelegate();

        private static Action<Projectile, Thing, bool> CreateProjectileImpactDelegate()
        {
            if (ProjectileImpactMethod == null)
            {
                return null;
            }
            try
            {
                return (Action<Projectile, Thing, bool>)Delegate.CreateDelegate(
                    typeof(Action<Projectile, Thing, bool>),
                    ProjectileImpactMethod);
            }
            catch
            {
                return null;
            }
        }

        private static readonly FieldInfo ProjectileOriginField =
            AccessTools.Field(typeof(Projectile), "origin");

        private static readonly AccessTools.FieldRef<Projectile, Vector3> ProjectileOriginRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("origin");

        private static readonly Func<JobDriver, Toil> JobDriverCurToilFast = CreateJobDriverCurToilDelegate();

        private static Func<JobDriver, Toil> CreateJobDriverCurToilDelegate()
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(JobDriver), "CurToil");
            if (getter == null)
            {
                return null;
            }
            try
            {
                return (Func<JobDriver, Toil>)Delegate.CreateDelegate(typeof(Func<JobDriver, Toil>), getter);
            }
            catch
            {
                return driver => getter.Invoke(driver, null) as Toil;
            }
        }

        private static readonly FieldInfo ProjectileLauncherField =
            AccessTools.Field(typeof(Projectile), "launcher");

        private static readonly AccessTools.FieldRef<Projectile, Thing> ProjectileLauncherRef =
            AccessTools.FieldRefAccess<Projectile, Thing>("launcher");

        private static readonly FieldInfo ProjectilePreventFriendlyFireField =
            AccessTools.Field(typeof(Projectile), "preventFriendlyFire");

        private static readonly AccessTools.FieldRef<Projectile, bool> ProjectilePreventFriendlyFireRef =
            AccessTools.FieldRefAccess<Projectile, bool>("preventFriendlyFire");

        private static readonly FieldInfo TargeterCasterField =
            AccessTools.Field(typeof(Targeter), "caster");

        private static readonly AccessTools.FieldRef<Pawn_MechanitorTracker, Pawn> MechanitorPawnRef =
            AccessTools.FieldRefAccess<Pawn_MechanitorTracker, Pawn>("pawn");

        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo> TurretForcedTargetRef =
            AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>("forcedTarget");

        private static readonly AccessTools.FieldRef<Building_TurretGun, int> TurretCooldownRef =
            AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");

        private static readonly AccessTools.FieldRef<Building_TurretGun, bool> TurretHoldFireRef =
            AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire");

        private static readonly MethodInfo LocalTargetInfoGetCellMethod =
            AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Cell));

        private static readonly MethodInfo CellRectClosestCellToMethod =
            AccessTools.Method(typeof(CellRect), nameof(CellRect.ClosestCellTo), new[] { typeof(IntVec3) });

        private static readonly FieldInfo VerbCurrentTargetField =
            AccessTools.Field(typeof(Verb), "currentTarget");

        private static readonly FieldInfo VerbPropertiesRequireLineOfSightField =
            AccessTools.Field(typeof(VerbProperties), nameof(VerbProperties.requireLineOfSight));

        private static readonly MethodInfo ProjectileLaunchMethod =
            AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[]
            {
                typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
                typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
            });

        private static readonly MethodInfo ResolveEffectiveTargetCellRefMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveEffectiveTargetCellRef));

        private static readonly MethodInfo ResolveCoverTargetCellRefMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveCoverTargetCellRef));

        private static readonly MethodInfo ResolveShootLineTargetCellRefMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveShootLineTargetCellRef));

        private static readonly MethodInfo ResolveLeanDestCellMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveLeanDestCell));

        private static readonly MethodInfo ResolveLaunchUsedTargetMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveLaunchUsedTarget));

        private static readonly MethodInfo ResolveVerbOrderTargetCellRefMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveVerbOrderTargetCellRef));

        private static readonly MethodInfo ResolveAttackStaticTargetCellRefMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(ResolveAttackStaticTargetCellRef));

        public struct VerbResolutionState
        {
            public bool Pushed;
            public Thing PrevShooter;
            public Verb PrevVerb;
            public bool PrevIgnoreRange;
        }

        public struct TargeterInputState
        {
            public bool PushedHover;
            public bool PushedClick;
            public IntVec3 ClickCell;
            public Vector3 ClickWorldPos;
        }

        /// <summary>
        /// Intercepts projectiles along [lastExactPos -> newExactPos] against any Giant Creature's
        /// texture-mapped HitCells and exact 512x512 alpha bitmasks, impacting at the struck limb's world cell
        /// and routing damage to that bone's linked BodyPartRecord.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), "CheckForFreeInterceptBetween")]
        public static bool Projectile_CheckForFreeInterceptBetween_Prefix(
            Projectile __instance,
            Vector3 lastExactPos,
            Vector3 newExactPos,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            Map map = __instance.Map;
            if (map == null || lastExactPos == newExactPos || __instance.def.projectile.flyOverhead)
            {
                return true;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || !mapComp.HasAnyGiants || !mapComp.MightSegmentIntersectAnyGiantHitbox(lastExactPos, newExactPos))
            {
                return true;
            }

            Vector3 origin = ProjectileOriginRef(__instance);
            Thing launcher = ProjectileLauncherRef(__instance);
            bool preventFF = ProjectilePreventFriendlyFireRef(__instance);

            Vector3 seg = newExactPos - lastExactPos;
            float dist = seg.MagnitudeHorizontal();
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / 0.35f));

            var testedGiants = SimpleGiantSetPool.Get();
            try
            {
                for (int s = 0; s <= steps; s++)
                {
                    Vector3 sample = Vector3.LerpUnclamped(lastExactPos, newExactPos, s / (float)steps);
                    IntVec3 cell = sample.ToIntVec3();
                    if (!cell.InBounds(map))
                    {
                        continue;
                    }

                    List<CompGiantRig> candidates = mapComp.GetHitGiantsAt(cell);
                    if (candidates == null || candidates.Count == 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        CompGiantRig comp = candidates[i];
                        Pawn giant = comp?.Pawn;
                        if (giant == null || !giant.Spawned || giant == launcher || !testedGiants.Add(comp))
                        {
                            continue;
                        }

                        bool canHit = ProjectileCanHitFast != null
                            ? ProjectileCanHitFast(__instance, giant)
                            : (bool)ProjectileCanHitMethod.Invoke(__instance, new object[] { giant });
                        if (!canHit)
                        {
                            continue;
                        }

                        bool isIntended = __instance.intendedTarget.Thing == giant;
                        if (!isIntended)
                        {
                            float distFactor = VerbUtility.InterceptChanceFactorFromDistance(origin, cell);
                            if (distFactor <= 0f)
                            {
                                continue;
                            }
                            if (launcher != null && giant.Faction != null && launcher.Faction != null && !giant.Faction.HostileTo(launcher.Faction))
                            {
                                if (preventFF)
                                {
                                    continue;
                                }
                                distFactor *= Find.Storyteller.difficulty.friendlyFireChanceFactor;
                            }

                            float coverage = comp.Footprint.hitEntryByCell.TryGetValue(cell, out HitCellEntry entry)
                                ? entry.hitCoverage
                                : 0.5f;
                            float interceptChance = Mathf.Clamp01(0.75f * coverage * distFactor);
                            if (!Rand.Chance(interceptChance))
                            {
                                continue;
                            }
                        }

                        // Perform exact alpha-mask segment test (including continuum soft-whip masks) to find precise bone & world impact coordinate
                        int hitBoneIdx;
                        Vector3 hitWorld;
                        if (!comp.Footprint.TryHitTestSegment(
                                comp.RigDef,
                                comp.AlphaMasksSpan,
                                lastExactPos,
                                newExactPos,
                                out hitBoneIdx,
                                out hitWorld,
                                comp.SoftWhipAlphaMasksSpan))
                        {
                            if (isIntended && (!__instance.usedTarget.IsValid || newExactPos.ToIntVec3() != __instance.usedTarget.Cell))
                            {
                                // Let projectile continue slightly further inward until it touches an opaque pixel or reaches usedTarget.Cell
                                continue;
                            }
                            hitBoneIdx = comp.Footprint.hitEntryByCell.TryGetValue(cell, out HitCellEntry ce)
                                ? ce.dominantBoneIndex
                                : comp.Footprint.FindClosestHitBoneToCell(cell);
                            hitWorld = sample;
                        }

                        IntVec3 impactCell = hitWorld.ToIntVec3();
                        if (impactCell.InBounds(map))
                        {
                            __instance.Position = impactCell;
                        }

                        BodyPartRecord hitPart = comp.GetBodyPartForBone(hitBoneIdx);
                        PendingBoneHitContext.ScopeState hitScope = PendingBoneHitContext.Push(giant, comp, hitBoneIdx, hitPart, hitWorld);
                        try
                        {
                            if (ProjectileImpactFast != null)
                            {
                                ProjectileImpactFast(__instance, giant, false);
                            }
                            else
                            {
                                ProjectileImpactMethod.Invoke(__instance, new object[] { giant, false });
                            }
                        }
                        finally
                        {
                            PendingBoneHitContext.Pop(in hitScope);
                        }

                        __result = true;
                        return false;
                    }
                }
            }
            finally
            {
                SimpleGiantSetPool.Return(testedGiants);
            }

            return true;
        }

        /// <summary>
        /// Handles projectiles that land on a map cell inside a Giant Creature's HitCells (e.g. aimed at an exposed
        /// pincer/tail cell or wild/forced-miss shots landing on an outer limb cell).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), "ImpactSomething")]
        public static bool Projectile_ImpactSomething_Prefix(Projectile __instance)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            Map map = __instance.Map;
            if (map == null)
            {
                return true;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return true;
            }

            IntVec3 pos = __instance.Position;
            List<CompGiantRig> giantsAtDest = mapComp.GetHitGiantsAt(pos);
            if (giantsAtDest == null || giantsAtDest.Count == 0)
            {
                return true;
            }

            Thing usedThing = __instance.usedTarget.HasThing ? __instance.usedTarget.Thing : null;
            if (usedThing != null)
            {
                bool usedThingIsGiantAtDest = false;
                for (int i = 0; i < giantsAtDest.Count; i++)
                {
                    if (giantsAtDest[i]?.Pawn == usedThing)
                    {
                        usedThingIsGiantAtDest = true;
                        break;
                    }
                }
                if (!usedThingIsGiantAtDest)
                {
                    return true;
                }
            }

            Vector3 origin = ProjectileOriginRef(__instance);
            Thing launcher = ProjectileLauncherRef(__instance);
            bool preventFF = ProjectilePreventFriendlyFireRef(__instance);
            for (int i = 0; i < giantsAtDest.Count; i++)
            {
                CompGiantRig comp = giantsAtDest[i];
                Pawn giant = comp?.Pawn;
                if (giant == null || !giant.Spawned || giant == launcher)
                {
                    continue;
                }
                if (usedThing != null && giant != usedThing)
                {
                    continue;
                }

                bool canHit = ProjectileCanHitFast != null
                    ? ProjectileCanHitFast(__instance, giant)
                    : (bool)ProjectileCanHitMethod.Invoke(__instance, new object[] { giant });
                if (!canHit)
                {
                    continue;
                }

                bool isIntended = __instance.usedTarget.Thing == giant || __instance.intendedTarget.Thing == giant;
                float hitChance = 1f;
                int boneIdx = -1;
                if (comp.Footprint.hitEntryByCell.TryGetValue(pos, out HitCellEntry entry))
                {
                    boneIdx = entry.dominantBoneIndex;
                    if (!isIntended)
                    {
                        hitChance = Mathf.Clamp01(0.85f * entry.hitCoverage);
                    }
                }
                else if (!isIntended)
                {
                    hitChance = 0.5f;
                }

                if (!isIntended)
                {
                    float distFactor = VerbUtility.InterceptChanceFactorFromDistance(origin, pos);
                    if (distFactor <= 0f)
                    {
                        continue;
                    }
                    if (launcher != null && giant.Faction != null && launcher.Faction != null && !giant.Faction.HostileTo(launcher.Faction))
                    {
                        if (preventFF)
                        {
                            continue;
                        }
                        distFactor *= Find.Storyteller?.difficulty?.friendlyFireChanceFactor ?? 1f;
                        if (distFactor <= 0f)
                        {
                            continue;
                        }
                    }
                    hitChance = Mathf.Clamp01(hitChance * distFactor);
                    if (hitChance <= 0f)
                    {
                        continue;
                    }
                }

                if (comp.Footprint.TryHitTestPoint(comp.RigDef, comp.AlphaMasksSpan, __instance.ExactPosition, out int exactBone, comp.SoftWhipAlphaMasksSpan))
                {
                    boneIdx = exactBone;
                }

                if (isIntended || Rand.Chance(hitChance))
                {
                    BodyPartRecord part = comp.GetBodyPartForBone(boneIdx);
                    PendingBoneHitContext.ScopeState hitScope = PendingBoneHitContext.Push(giant, comp, boneIdx, part, __instance.ExactPosition);
                    try
                    {
                        if (ProjectileImpactFast != null)
                        {
                            ProjectileImpactFast(__instance, giant, false);
                        }
                        else
                        {
                            ProjectileImpactMethod.Invoke(__instance, new object[] { giant, false });
                        }
                    }
                    finally
                    {
                        PendingBoneHitContext.Pop(in hitScope);
                    }
                    return false;
                }
            }

            return true;
        }

        private static int ResolveBoneIndexForBodyPart(CompGiantRig comp, int preferredBoneIdx, BodyPartRecord resolvedPart)
        {
            if (comp?.RigDef?.SortedNodes == null || resolvedPart == null)
            {
                return preferredBoneIdx;
            }
            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            if ((uint)preferredBoneIdx < (uint)nodes.Length && comp.GetLinkedBodyPartRaw(preferredBoneIdx) == resolvedPart)
            {
                return preferredBoneIdx;
            }
            int cur = preferredBoneIdx;
            int guard = nodes.Length;
            while ((uint)cur < (uint)nodes.Length && guard-- > 0)
            {
                if (comp.GetLinkedBodyPartRaw(cur) == resolvedPart)
                {
                    return cur;
                }
                cur = nodes[cur].parentRuntimeIndex;
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                if (comp.GetLinkedBodyPartRaw(i) == resolvedPart)
                {
                    return i;
                }
            }
            return preferredBoneIdx;
        }

        public static CompGiantRig TryGetEnabledCorpseRig(Thing thing)
        {
            if (thing is Corpse corpse && corpse.InnerPawn != null)
            {
                CompGiantRig comp = GiantFastLookup.TryGetRig(corpse.InnerPawn);
                if (comp != null && (comp.RigDef == null || comp.RigDef.enableCorpseFootprint))
                {
                    return comp;
                }
            }
            return null;
        }

        /// <summary>
        /// Routes part-specific projectile or melee hits directly to the struck bone's anatomical BodyPartRecord.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
        public static bool Thing_TakeDamage_Prefix(
            Thing __instance,
            ref DamageInfo dinfo,
            ref DamageWorker.DamageResult __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            if (__instance is Pawn victimPawn && dinfo.Amount > 0f && dinfo.Instigator is Pawn instPawn &&
                GiantFlightUtility.IsBlockedGroundMeleeDamage(instPawn, victimPawn, in dinfo))
            {
                dinfo.SetAmount(0f);
                __result = new DamageWorker.DamageResult();
                return false;
            }

            if (!(__instance is Pawn pawn) || dinfo.HitPart != null || dinfo.Amount <= 0f)
            {
                return true;
            }

            if (PendingBoneHitContext.TargetPawn == pawn)
            {
                CompGiantRig pendingComp = PendingBoneHitContext.TargetComp ?? GiantFastLookup.TryGetRig(pawn);
                if (pendingComp?.RigDef != null && !pendingComp.RigDef.enableLimbPartDamageRouting)
                {
                    return true;
                }
                int pendingBone = PendingBoneHitContext.TargetBoneIndex;
                BodyPartRecord resolved = pendingComp != null
                    ? pendingComp.ResolveHitBodyPartForBone(pendingBone, dinfo)
                    : PendingBoneHitContext.TargetBodyPart;
                if (resolved != null && resolved.coverageAbs > 0f)
                {
                    dinfo.SetHitPart(resolved);
                    if (resolved.depth == BodyPartDepth.Inside && dinfo.Depth == BodyPartDepth.Undefined)
                    {
                        dinfo.SetBodyRegion(dinfo.Height, BodyPartDepth.Inside);
                    }
                }
                int effectiveBone = pendingComp != null && resolved != null
                    ? ResolveBoneIndexForBodyPart(pendingComp, pendingBone, resolved)
                    : pendingBone;
                if (pendingComp != null && effectiveBone >= 0)
                {
                    pendingComp.Notify_PreRoutedBoneHit(effectiveBone);
                    GiantCombatMechanicsUtility.ApplyBoneArmorAndWeakpointModifiers(
                        pawn,
                        pendingComp,
                        effectiveBone,
                        ref dinfo,
                        PendingBoneHitContext.TargetHitWorldPos,
                        out _);
                }
                return true;
            }

            // For melee, hitscan beams, abilities, or fallback ranged attacks without explicit HitPart,
            // route to the physical bone along the incoming line of fire or closest to Instigator.
            if (dinfo.Instigator != null && dinfo.Instigator.Spawned && dinfo.Instigator.Map == pawn.Map &&
                GiantFastLookup.TryGetRig(pawn) is CompGiantRig comp &&
                (comp.RigDef == null || comp.RigDef.enableLimbPartDamageRouting))
            {
                IntVec3 instPos = dinfo.Instigator.Position;
                bool nearFootprint = instPos.DistanceToSquared(pawn.Position) <= 64 ||
                    (!comp.Footprint.tightHitBounds.IsEmpty && comp.Footprint.tightHitBounds.ExpandedBy(2).Contains(instPos));

                int targetBone = -1;
                Vector3? hitWorld = null;
                if (nearFootprint)
                {
                    targetBone = comp.Footprint.FindClosestHitBoneToCell(instPos);
                }
                else
                {
                    Vector3 rayStart = dinfo.Instigator.DrawPos;
                    Vector3 rayEnd = CompGiantRig.GetSafeDrawPos(pawn);
                    if (comp.Footprint.TryHitTestSegment(
                            comp.RigDef,
                            comp.AlphaMasksSpan,
                            rayStart,
                            rayEnd,
                            out int segBone,
                            out Vector3 segWorld,
                            comp.SoftWhipAlphaMasksSpan))
                    {
                        targetBone = segBone;
                        hitWorld = segWorld;
                    }
                    else
                    {
                        targetBone = comp.Footprint.FindClosestHitBoneToCell(instPos);
                    }
                }

                if (targetBone >= 0)
                {
                    BodyPartRecord part = comp.ResolveHitBodyPartForBone(targetBone, dinfo);
                    if (part != null && part.coverageAbs > 0f)
                    {
                        dinfo.SetHitPart(part);
                        if (part.depth == BodyPartDepth.Inside && dinfo.Depth == BodyPartDepth.Undefined)
                        {
                            dinfo.SetBodyRegion(dinfo.Height, BodyPartDepth.Inside);
                        }
                    }
                    int effectiveBone = part != null
                        ? ResolveBoneIndexForBodyPart(comp, targetBone, part)
                        : targetBone;
                    comp.Notify_PreRoutedBoneHit(effectiveBone);
                    GiantCombatMechanicsUtility.ApplyBoneArmorAndWeakpointModifiers(
                        pawn,
                        comp,
                        effectiveBone,
                        ref dinfo,
                        hitWorld,
                        out _);
                }
            }
            return true;
        }

        /// <summary>
        /// Prevents vanilla secondary injury workers (<see cref="DamageWorker_Cut"/> cleave,
        /// <see cref="DamageWorker_Stab"/> splash, <see cref="DamageWorker_Blunt"/> inner-hit) from bypassing
        /// an intact <see cref="BodyPartDepth.Outside"/> armor shell and dealing direct <c>FinalizeAndAddInjury</c>
        /// hits to enclosed <see cref="BodyPartDepth.Inside"/> vital organs (<c>Brain</c>, <c>Heart</c>, <c>Reactor</c>),
        /// while preserving explicit internal self-burst damage.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageWorker_AddInjury), "FinalizeAndAddInjury",
            new[] { typeof(Pawn), typeof(Hediff_Injury), typeof(DamageInfo), typeof(DamageWorker.DamageResult) })]
        public static void DamageWorker_AddInjury_FinalizeAndAddInjury_Prefix(
            Pawn pawn,
            Hediff_Injury injury,
            ref DamageInfo dinfo)
        {
            if (GiantFastLookup.IsEmpty || pawn == null || injury?.Part == null || injury.Part.depth != BodyPartDepth.Inside)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig comp)
            {
                GiantCombatMechanicsUtility.TryRedirectGiantInternalInjuryToOuterShell(
                    pawn,
                    comp,
                    injury,
                    ref dinfo,
                    out _);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HediffSet), "CalculateBleedRate")]
        public static void HediffSet_CalculateBleedRate_Postfix(Pawn ___pawn, ref float __result)
        {
            if (__result <= 0f || ___pawn == null || !(GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp) || comp.RigDef == null || !comp.RigDef.enableLimbPartDamageRouting)
            {
                return;
            }
            __result = GiantCombatMechanicsUtility.NormalizeGiantBleedRate(___pawn, comp, __result);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), "DropBloodFilth")]
        public static bool Pawn_HealthTracker_DropBloodFilth_Prefix(Pawn ___pawn)
        {
            if (___pawn == null || !(GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp))
            {
                return true;
            }
            GiantBloodFilthUtility.TryDropAnatomicalBleedingFilth(___pawn, comp);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.CheckForStateChange))]
        public static void Pawn_HealthTracker_CheckForStateChange_Prefix(Pawn_HealthTracker __instance, Pawn ___pawn)
        {
            if (__instance != null &&
                __instance.overrideDeathOnDownedChance < 0f &&
                ___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp)
            {
                GiantCombatMechanicsUtility.EnsureGiantDeathOnDownedSuppressed(___pawn, comp);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HediffSet), "CalculatePain")]
        public static bool HediffSet_CalculatePain_Prefix(HediffSet __instance, Pawn ___pawn, ref float __result)
        {
            if (___pawn == null || !(GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp) || comp.RigDef == null)
            {
                return true;
            }
            if (___pawn.Dead)
            {
                __result = 0f;
                return false;
            }
            __result = GiantCombatMechanicsUtility.CalculateGiantPain(___pawn, __instance, comp);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.InPainShock), MethodType.Getter)]
        public static void Pawn_HealthTracker_InPainShock_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (___pawn == null || !(GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp) || comp.RigDef == null)
            {
                return;
            }
            if (GiantCombatMechanicsUtility.IsPainImmuneGiant(___pawn, comp))
            {
                __result = false;
                return;
            }
            if (comp.RigDef.painShockThresholdOverride > 0f && ___pawn.health?.hediffSet != null)
            {
                float thresh = GiantCombatMechanicsUtility.ResolveEffectivePainShockThreshold(___pawn, comp.RigDef);
                __result = ___pawn.health.hediffSet.PainTotal >= thresh;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.CanBleed), MethodType.Getter)]
        public static void Pawn_HealthTracker_CanBleed_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result &&
                ___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                comp.RigDef != null &&
                comp.RigDef.onlyDieWhenCoreDestroyed &&
                GiantCombatMechanicsUtility.IsPainImmuneGiant(___pawn, comp))
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDead))]
        public static void Pawn_HealthTracker_ShouldBeDead_LethalCore_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (!__result &&
                ___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                GiantCombatMechanicsUtility.HasDestroyedLethalCoreBone(___pawn, comp))
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDowned))]
        public static void Pawn_HealthTracker_ShouldBeDowned_CoreOnly_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result &&
                ___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                GiantCombatMechanicsUtility.ShouldSuppressNonCoreDowned(___pawn, comp))
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.LethalDamageThreshold), MethodType.Getter)]
        public static void Pawn_HealthTracker_LethalDamageThreshold_Postfix(Pawn ___pawn, ref float __result)
        {
            if (___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                (comp.RigDef == null || comp.RigDef.enableLimbPartDamageRouting))
            {
                __result *= GiantCombatMechanicsUtility.ComputeGiantLethalThresholdMultiplier(comp);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDeadFromLethalDamageThreshold))]
        public static void Pawn_HealthTracker_ShouldBeDeadFromLethalDamageThreshold_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result &&
                ___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                GiantCombatMechanicsUtility.ShouldPreventPrematureLethalThresholdDeath(___pawn, comp))
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SummaryHealthHandler), nameof(SummaryHealthHandler.SummaryHealthPercent), MethodType.Getter)]
        public static void SummaryHealthHandler_SummaryHealthPercent_Postfix(Pawn ___pawn, ref float __result)
        {
            if (___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                (comp.RigDef == null || comp.RigDef.enableLimbPartDamageRouting))
            {
                __result = GiantBossHealthBarHUD.ComputeSynchronizedGiantHealthPercent(___pawn, comp);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
        public static void Verb_LaunchProjectile_TryCastShot_Prefix(
            Verb_LaunchProjectile __instance,
            out ActiveGiantVerbShotContext.ScopeState __state)
        {
            __state = default;
            if (__instance == null || __instance is Verb_CarrierDispatchInterceptors)
            {
                return;
            }

            if (__instance.CasterPawn is Pawn giant && giant.Spawned &&
                GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.TryResolveVerbMuzzleShot(__instance, advanceCursor: true, out GiantMuzzleShotInfo shotInfo))
            {
                __state = ActiveGiantVerbShotContext.Push(giant, comp, __instance, in shotInfo);
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
        public static void Verb_LaunchProjectile_TryCastShot_Finalizer(ActiveGiantVerbShotContext.ScopeState __state)
        {
            if (__state.DidPush)
            {
                ActiveGiantVerbShotContext.Pop(in __state);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch),
            new[]
            {
                typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
                typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
            })]
        public static void Projectile_Launch_Prefix(
            Projectile __instance,
            Thing launcher,
            ref Vector3 origin,
            LocalTargetInfo intendedTarget)
        {
            if (!ActiveGiantVerbShotContext.TryConsumeForLauncher(
                    launcher,
                    out CompGiantRig comp,
                    out Verb activeVerb,
                    out GiantMuzzleShotInfo shotInfo))
            {
                return;
            }

            Pawn giant = comp?.Pawn;
            origin = shotInfo.WorldPos;

            if (__instance != null && __instance.Map != null &&
                shotInfo.MapCell.IsValid && shotInfo.MapCell.InBounds(__instance.Map))
            {
                __instance.Position = shotInfo.MapCell;
            }

            if (giant == null || !giant.Spawned)
            {
                return;
            }

            float vfxAngle = shotInfo.WorldAngleDeg;
            if (intendedTarget.IsValid)
            {
                Vector3 targPos = intendedTarget.HasThing
                    ? intendedTarget.Thing.DrawPos
                    : intendedTarget.Cell.ToVector3Shifted();
                Vector3 toTarget = targPos - shotInfo.WorldPos;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.04f)
                {
                    float aimBearing = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
                    if (Mathf.Abs(Mathf.DeltaAngle(shotInfo.WorldAngleDeg, aimBearing)) <= 55f)
                    {
                        vfxAngle = aimBearing;
                    }
                }
            }

            GiantWeaponEmitterDef emitter = comp.ResolveEmitterForVerb(activeVerb);
            if (emitter != null && emitter.fireMode == GiantEmitterFireMode.Salvo && !string.IsNullOrEmpty(emitter.emitterId))
            {
                var salvoShots = new List<GiantMuzzleShotInfo>(4);
                if (comp.CollectEmitterMuzzles(emitter.emitterId, salvoShots) > 1)
                {
                    for (int i = 0; i < salvoShots.Count; i++)
                    {
                        GiantMuzzleShotInfo s = salvoShots[i];
                        if (shotInfo.SpawnMuzzleVfx)
                        {
                            GiantVfxSystem.SpawnMuzzleBlastVfx(giant, comp, s.WorldPos, vfxAngle, s.VfxScale, s.VfxColor, s.VfxPreset);
                            GiantVfxSystem.TrySpawnGiantProjectileLaunchWake(__instance, giant, s.WorldPos, intendedTarget, vfxAngle);
                        }
                        comp.TriggerTurretRecoil(s.BoneIndex, vfxAngle, 1f, publishImmediate: i == salvoShots.Count - 1);
                    }
                    return;
                }
            }

            if (shotInfo.SpawnMuzzleVfx)
            {
                GiantVfxSystem.SpawnMuzzleBlastVfx(
                    giant,
                    comp,
                    shotInfo.WorldPos,
                    vfxAngle,
                    shotInfo.VfxScale,
                    shotInfo.VfxColor,
                    shotInfo.VfxPreset);
                GiantVfxSystem.TrySpawnGiantProjectileLaunchWake(__instance, giant, shotInfo.WorldPos, intendedTarget, vfxAngle);
            }
            comp.TriggerTurretRecoil(shotInfo.BoneIndex, vfxAngle, 1f, publishImmediate: true);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), "Impact", new[] { typeof(Thing), typeof(bool) })]
        public static void Projectile_Impact_Prefix(Projectile __instance, Thing hitThing)
        {
            GiantVfxSystem.TrySpawnGiantProjectileImpactVfx(__instance, hitThing);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageWorker), "ExplosionDamageThing",
            new[] { typeof(Explosion), typeof(Thing), typeof(List<Thing>), typeof(List<Thing>), typeof(IntVec3) })]
        public static void DamageWorker_ExplosionDamageThing_Prefix(
            Explosion explosion,
            Thing t,
            IntVec3 cell,
            out PendingBoneHitContext.ScopeState __state)
        {
            __state = default;
            if (t is Pawn giant && giant.Spawned &&
                PendingBoneHitContext.TargetPawn != giant &&
                GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                (comp.RigDef == null || comp.RigDef.enableMultiCellExplosionRouting))
            {
                int boneIdx = comp.Footprint.hitEntryByCell.TryGetValue(cell, out HitCellEntry entry)
                    ? entry.dominantBoneIndex
                    : (explosion != null ? comp.Footprint.FindClosestHitBoneToCell(explosion.Position) : 0);
                BodyPartRecord part = comp.GetBodyPartForBone(boneIdx);
                __state = PendingBoneHitContext.Push(giant, comp, boneIdx, part, cell.ToVector3Shifted());
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(DamageWorker), "ExplosionDamageThing",
            new[] { typeof(Explosion), typeof(Thing), typeof(List<Thing>), typeof(List<Thing>), typeof(IntVec3) })]
        public static void DamageWorker_ExplosionDamageThing_Finalizer(PendingBoneHitContext.ScopeState __state)
        {
            PendingBoneHitContext.Pop(in __state);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DamageWorker), nameof(DamageWorker.ExplosionAffectCell),
            new[] { typeof(Explosion), typeof(IntVec3), typeof(List<Thing>), typeof(List<Thing>), typeof(bool) })]
        public static void DamageWorker_ExplosionAffectCell_Postfix(
            DamageWorker __instance,
            Explosion explosion,
            IntVec3 c,
            List<Thing> damagedThings,
            List<Thing> ignoredThings)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            GiantCombatMechanicsUtility.AffectGiantsInExplosionCell(__instance, explosion, c, damagedThings, ignoredThings);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
        public static void Pawn_DeSpawn_Postfix(Pawn __instance)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            GiantTargetAimTracker.ClearForAttacker(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterOnGUI))]
        public static void Targeter_TargeterOnGUI_Prefix(out bool __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = false;
                return;
            }
            Map map = Find.CurrentMap;
            if (map != null)
            {
                IntVec3 cell = UI.MouseCell();
                if (cell.InBounds(map))
                {
                    GiantTargetAimTracker.PushHoverQuery(cell);
                    __state = true;
                    return;
                }
            }
            __state = false;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterOnGUI))]
        public static void Targeter_TargeterOnGUI_Finalizer(bool __state)
        {
            if (__state)
            {
                GiantTargetAimTracker.PopHoverQuery();
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterUpdate))]
        public static void Targeter_TargeterUpdate_Prefix(out bool __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = false;
                return;
            }
            Map map = Find.CurrentMap;
            if (map != null)
            {
                IntVec3 cell = UI.MouseCell();
                if (cell.InBounds(map))
                {
                    GiantTargetAimTracker.PushHoverQuery(cell);
                    __state = true;
                    return;
                }
            }
            __state = false;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterUpdate))]
        public static void Targeter_TargeterUpdate_Finalizer(bool __state)
        {
            if (__state)
            {
                GiantTargetAimTracker.PopHoverQuery();
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.ProcessInputEvents))]
        public static void Targeter_ProcessInputEvents_Prefix(Targeter __instance, out TargeterInputState __state)
        {
            __state = default;
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (map == null || !__instance.IsTargeting)
            {
                return;
            }

            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                return;
            }

            GiantTargetAimTracker.PushHoverQuery(cell);
            __state.PushedHover = true;

            if (Event.current != null && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                Vector3 worldPos = UI.MouseMapPosition();
                GiantTargetAimTracker.PushClickCapture(cell, worldPos);
                __state.PushedClick = true;
                __state.ClickCell = cell;
                __state.ClickWorldPos = worldPos;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.ProcessInputEvents))]
        public static void Targeter_ProcessInputEvents_Postfix(Targeter __instance, TargeterInputState __state)
        {
            if (!__state.PushedClick || Event.current == null || Event.current.type != EventType.Used)
            {
                return;
            }

            Map map = Find.CurrentMap;
            if (map == null || __instance.targetingSource != null)
            {
                return;
            }

            if (!(TargeterCasterField?.GetValue(__instance) is Pawn casterPawn))
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            List<CompGiantRig> giants = mapComp?.GetHitGiantsAt(__state.ClickCell);
            if (giants != null && giants.Count == 1 && giants[0]?.Pawn != null)
            {
                GiantTargetAimTracker.RecordHitCellClick(casterPawn, giants[0].Pawn, __state.ClickCell, __state.ClickWorldPos);
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Targeter), nameof(Targeter.ProcessInputEvents))]
        public static void Targeter_ProcessInputEvents_Finalizer(TargeterInputState __state)
        {
            if (__state.PushedClick)
            {
                GiantTargetAimTracker.PopClickCapture();
            }
            if (__state.PushedHover)
            {
                GiantTargetAimTracker.PopHoverQuery();
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
        public static void FloatMenuMakerMap_GetOptions_Prefix(Vector3 clickPos, out bool __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = false;
                return;
            }
            Map map = Find.CurrentMap;
            if (map != null && clickPos.InBounds(map))
            {
                GiantTargetAimTracker.PushHoverQuery(IntVec3.FromVector3(clickPos));
                __state = true;
                return;
            }
            __state = false;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
        public static void FloatMenuMakerMap_GetOptions_Finalizer(bool __state)
        {
            if (__state)
            {
                GiantTargetAimTracker.PopHoverQuery();
            }
        }

        public static bool IsValidRightClickPartFocusTarget(Thing clickedThing, out Pawn targetPawn)
        {
            if (!(clickedThing is Pawn p) || p.Destroyed || !p.Spawned || p.Dead)
            {
                targetPawn = null;
                return false;
            }

            Faction playerFaction = Faction.OfPlayerSilentFail;
            bool hostile = playerFaction != null && p.HostileTo(playerFaction);
            if (p.def != null && p.def.noRightClickDraftAttack && hostile)
            {
                targetPawn = null;
                return false;
            }
            if (playerFaction != null && p.health?.hediffSet?.hediffs != null && InvisibilityUtility.IsHiddenFromPlayer(p))
            {
                targetPawn = null;
                return false;
            }

            bool nonHumanlikeOrWild = p.RaceProps != null && WildManUtility.NonHumanlikeOrWildMan(p);
            if (!hostile && (playerFaction == null || p.Faction == playerFaction || !nonHumanlikeOrWild))
            {
                targetPawn = null;
                return false;
            }

            targetPawn = p;
            return true;
        }

        public static bool CanGiantIssueDraftedPartFocusOrder(Pawn sp, Pawn targetPawn, out CompGiantRig rig)
        {
            rig = null;
            if (sp == null || sp == targetPawn || sp.Destroyed || !sp.Spawned || sp.Dead || sp.Downed || !sp.Drafted)
            {
                return false;
            }

            rig = GiantFastLookup.TryGetRig(sp);
            if (rig?.PartCombat == null)
            {
                return false;
            }

            if (GiantFastLookup.TryGetCockpit(sp) is CompGiantCockpit cockpit &&
                (cockpit.IsDormantUnmanned || !cockpit.CanFirePrimaryWeapons))
            {
                return false;
            }

            bool hasOperationalHangar = GiantFastLookup.TryGetCarrierHangar(sp) is CompCarrierHangar ch
                && ch.MaxAllowedInterceptors > 0
                && ch.IntactBayCount > 0;
            return rig.PartCombat.CountIntactWeaponGroups() > 0 || hasOperationalHangar;
        }

        public static bool CanGiantIssueDraftedAttackAction(
            Pawn sp,
            Thing targetThing,
            out CompGiantRig rig,
            out bool hasRangedOrCarrier,
            out bool hasMelee)
        {
            rig = null;
            hasRangedOrCarrier = false;
            hasMelee = false;
            if (sp == null || sp == targetThing || sp.Destroyed || !sp.Spawned || sp.Dead || sp.Downed || !sp.Drafted)
            {
                return false;
            }

            rig = GiantFastLookup.TryGetRig(sp);
            if (rig?.PartCombat == null)
            {
                return false;
            }

            if (GiantFastLookup.TryGetCockpit(sp) is CompGiantCockpit cockpit &&
                (cockpit.IsDormantUnmanned || !cockpit.CanFirePrimaryWeapons))
            {
                return false;
            }

            bool hasOperationalHangar = GiantFastLookup.TryGetCarrierHangar(sp) is CompCarrierHangar ch
                && ch.MaxAllowedInterceptors > 0
                && ch.IntactBayCount > 0;
            rig.PartCombat.GetTacticalRangeCapabilities(out bool hasRanged, out _, out _, out hasMelee, out _);
            hasRangedOrCarrier = hasRanged || hasOperationalHangar;
            return hasRangedOrCarrier || hasMelee;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedAttack), nameof(FloatMenuOptionProvider_DraftedAttack.GetOptionsFor), new[] { typeof(Thing), typeof(FloatMenuContext) })]
        public static void FloatMenuOptionProvider_DraftedAttack_GetOptionsFor_Postfix(
            Thing clickedThing,
            FloatMenuContext context,
            ref IEnumerable<FloatMenuOption> __result)
        {
            if (context == null || clickedThing == null)
            {
                return;
            }

            if (__result != null &&
                clickedThing is Pawn giant &&
                GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                (comp.Footprint.hitCellSet.Contains(context.ClickedCell) || comp.IsImmuneToGroundMeleeNow))
            {
                __result = WrapDraftedAttackOptions(__result, giant, context);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.UseRangedAttack))]
        public static bool FloatMenuUtility_UseRangedAttack_Prefix(Pawn pawn, ref bool __result)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) is CompGiantRig rig && rig.PartCombat != null)
            {
                bool hasOperationalHangar = GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar ch
                    && ch.MaxAllowedInterceptors > 0
                    && ch.IntactBayCount > 0;
                rig.PartCombat.GetTacticalRangeCapabilities(out bool hasRanged, out _, out _, out _, out _);
                __result = hasRanged || hasOperationalHangar;
                return false;
            }
            // Pure NRE guard: vanilla FloatMenuUtility.UseRangedAttack dereferences pawn.equipment without null check.
            // When pawn.equipment != null (even if Primary == null), pass through to vanilla and third-party mods.
            if (pawn == null || pawn.equipment == null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetRangedAttackAction))]
        public static bool FloatMenuUtility_GetRangedAttackAction_Prefix(
            Pawn pawn,
            LocalTargetInfo target,
            ref string failStr,
            ref Action __result)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) is CompGiantRig rig && rig.PartCombat != null)
            {
                if (target.IsValid && target.HasThing &&
                    CanGiantIssueDraftedAttackAction(pawn, target.Thing, out _, out bool hasRangedOrCarrier, out _) &&
                    hasRangedOrCarrier)
                {
                    LocalTargetInfo capturedTarget = target;
                    Pawn capturedPawn = pawn;
                    CompGiantRig capturedRig = rig;
                    failStr = "";
                    __result = delegate
                    {
                        if (capturedTarget.HasThing && (capturedTarget.Thing.Destroyed || !capturedTarget.Thing.Spawned))
                        {
                            return;
                        }
                        if (capturedTarget.Thing is Pawn tp && tp.Dead)
                        {
                            return;
                        }
                        GiantPartCombatUtility.OrderGiantFocusAttack(capturedPawn, capturedRig, capturedTarget, allowApproachIfOutOfRange: true);
                    };
                    return false;
                }
                failStr = "";
                __result = null;
                return false;
            }
            if (pawn == null || pawn.equipment == null)
            {
                failStr = "";
                __result = null;
                return false;
            }
            return true;
        }

        private static IEnumerable<FloatMenuOption> WrapDraftedAttackOptions(
            IEnumerable<FloatMenuOption> source,
            Pawn giant,
            FloatMenuContext context)
        {
            IntVec3 clickedCell = context.ClickedCell;
            Vector3 clickPos = context.clickPosition;
            Vector3 fleckPos = clickedCell.ToVector3Shifted();
            List<Pawn> selectedPawns = context.allSelectedPawns != null ? new List<Pawn>(context.allSelectedPawns) : null;
            bool targetAirborneImmune = GiantFastLookup.TryGetRig(giant)?.IsImmuneToGroundMeleeNow ?? false;
            bool allSelectedMeleeOnlyGround = targetAirborneImmune
                && selectedPawns != null
                && selectedPawns.Count > 0
                && selectedPawns.TrueForAll(sp => sp != null && !GiantFlightUtility.CanVanillaPawnEngageAirborneTargets(sp));

            if (allSelectedMeleeOnlyGround)
            {
                string blockLabel = "GCF_FloatMenu_CannotMeleeAirborne".CanTranslate()
                    ? "GCF_FloatMenu_CannotMeleeAirborne".Translate(giant.LabelShort).ToString()
                    : $"Cannot melee airborne target {giant.LabelShort}";
                yield return new FloatMenuOption(blockLabel, null);
                yield break;
            }

            foreach (FloatMenuOption opt in source)
            {
                if (opt != null && opt.action != null)
                {
                    Action origAction = opt.action;
                    opt.action = delegate
                    {
                        if (selectedPawns != null)
                        {
                            for (int i = 0; i < selectedPawns.Count; i++)
                            {
                                GiantTargetAimTracker.RecordHitCellClick(selectedPawns[i], giant, clickedCell, clickPos);
                            }
                        }

                        GiantTargetAimTracker.PushOverrideFleckPos(fleckPos);
                        try
                        {
                            origAction();
                        }
                        finally
                        {
                            GiantTargetAimTracker.PopOverrideFleckPos();
                        }
                    };
                }
                yield return opt;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FleckMaker), nameof(FleckMaker.Static), new[] { typeof(Vector3), typeof(Map), typeof(FleckDef), typeof(float) })]
        public static bool FleckMaker_Static_Prefix(ref Vector3 loc, FleckDef fleckDef, ref float scale)
        {
            if (GiantTargetAimTracker.TryGetOverrideFleckPos(out Vector3 overridePos) &&
                (fleckDef == FleckDefOf.FeedbackShoot || fleckDef == FleckDefOf.FeedbackMelee))
            {
                loc = overridePos;
                return true;
            }

            if (fleckDef == FleckDefOf.ShotFlash &&
                ActiveGiantVerbShotContext.TryGetActiveFlashRedirect(out GiantMuzzleShotInfo shotInfo))
            {
                if (shotInfo.ReplaceVanillaMuzzleFlash && GiantVfxSystem.IsRigVfxEnabled(null, ActiveGiantVerbShotContext.ActiveComp))
                {
                    return false;
                }
                loc = shotInfo.WorldPos;
                if (shotInfo.ReplaceVanillaMuzzleFlash)
                {
                    scale = Mathf.Min(scale, 3.2f * Mathf.Max(0.5f, shotInfo.VfxScale));
                }
            }

            return true;
        }

        /// <summary>
        /// Allows shooters to see and aim at exposed limbs/claws/tail cells sticking out from behind walls,
        /// preferring hovered or previously clicked HitCells first.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ShootLeanUtility), nameof(ShootLeanUtility.CalcShootableCellsOf))]
        public static void ShootLeanUtility_CalcShootableCellsOf_Postfix(
            List<IntVec3> outCells,
            Thing target,
            IntVec3 shooterPos)
        {
            if (target is Pawn pawn && GiantFastLookup.TryGetRig(pawn) is CompGiantRig comp && comp.Footprint.hitCells.Count > 0)
            {
                GiantTargetAimTracker.AppendAndOrderShootableHitCells(
                    outCells,
                    pawn,
                    comp,
                    shooterPos,
                    GiantTargetAimTracker.CurrentResolutionShooter,
                    GiantTargetAimTracker.CurrentResolutionVerb,
                    GiantTargetAimTracker.CurrentIgnoreRange);
            }
        }

        /// <summary>
        /// Measures weapon range against the Giant Creature's actual HitCells rather than its center cell or bounding box.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb), nameof(Verb.OutOfRange))]
        public static bool Verb_OutOfRange_Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (targ.HasThing && targ.Thing is Pawn pawn && GiantFastLookup.TryGetRig(pawn) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0)
            {
                bool anyInRange = GiantTargetAimTracker.AnyHitCellInVerbRange(comp, root, pawn, __instance);
                __result = !anyInRange;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetRangedAttackAction))]
        public static void FloatMenuUtility_GetRangedAttackAction_Postfix(
            Pawn pawn,
            LocalTargetInfo target,
            ref string failStr,
            Action __result)
        {
            if (__result != null || !target.HasThing || !(target.Thing is Pawn giant) ||
                !(GiantFastLookup.TryGetRig(giant) is CompGiantRig comp) || comp.Footprint.hitCells.Count == 0)
            {
                return;
            }

            Verb primaryVerb = pawn?.equipment?.PrimaryEq?.PrimaryVerb;
            if (primaryVerb == null || primaryVerb.verbProps.IsMeleeAttack)
            {
                return;
            }

            string outOfRangeText = "OutOfRange".Translate().CapitalizeFirst();
            string tooCloseText = "TooClose".Translate().CapitalizeFirst();
            string cannotHitText = "CannotHitTarget".Translate().CapitalizeFirst();
            if (failStr != outOfRangeText && failStr != tooCloseText && failStr != cannotHitText)
            {
                return;
            }

            bool anyWithinMax = GiantTargetAimTracker.AnyHitCellSatisfies(comp, c =>
                pawn.Position.InHorDistOf(c, primaryVerb.EffectiveRange));
            if (!anyWithinMax)
            {
                failStr = outOfRangeText;
                return;
            }

            float minR = primaryVerb.verbProps.EffectiveMinRange(target, pawn);
            bool anyValid = GiantTargetAimTracker.AnyHitCellSatisfies(comp, c =>
                pawn.Position.InHorDistOf(c, primaryVerb.EffectiveRange) &&
                (float)pawn.Position.DistanceToSquared(c) >= minR * minR);
            failStr = anyValid ? cannotHitText : tooCloseText;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.CanCommandTo))]
        public static void Pawn_MechanitorTracker_CanCommandTo_Postfix(
            Pawn_MechanitorTracker __instance,
            LocalTargetInfo target,
            ref bool __result)
        {
            if (__result || !target.HasThing || !(target.Thing is Pawn giant) ||
                !(GiantFastLookup.TryGetRig(giant) is CompGiantRig comp) || comp.Footprint.hitCells.Count == 0)
            {
                return;
            }

            Pawn overseer = MechanitorPawnRef(__instance);
            if (overseer == null || overseer.MapHeld == null || giant.MapHeld != overseer.MapHeld)
            {
                return;
            }

            if (GiantTargetAimTracker.AnyHitCellSatisfies(comp, c =>
                c.InBounds(overseer.MapHeld) && (float)overseer.Position.DistanceToSquared(c) < 620.01f))
            {
                __result = true;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.OrderAttack))]
        public static bool Building_TurretGun_OrderAttack_Prefix(Building_TurretGun __instance, LocalTargetInfo targ)
        {
            if (!targ.IsValid || !targ.HasThing || !(targ.Thing is Pawn giant) ||
                !(GiantFastLookup.TryGetRig(giant) is CompGiantRig comp) || comp.Footprint.hitCells.Count == 0)
            {
                return true;
            }

            if (GiantTargetAimTracker.TryGetActiveClick(out IntVec3 clickCell, out Vector3 clickWorldPos))
            {
                GiantTargetAimTracker.RecordHitCellClick(__instance, giant, clickCell, clickWorldPos);
            }

            Verb attackVerb = __instance.AttackVerb;
            if (attackVerb == null)
            {
                return true;
            }

            float minR = attackVerb.verbProps.EffectiveMinRange(targ, __instance);
            float maxR = attackVerb.EffectiveRange;

            bool anyWithinMax = GiantTargetAimTracker.AnyHitCellSatisfies(comp, c =>
                (c - __instance.Position).LengthHorizontal <= maxR);
            if (!anyWithinMax)
            {
                Messages.Message("MessageTargetBeyondMaximumRange".Translate(), __instance, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }

            bool anyValid = GiantTargetAimTracker.AnyHitCellSatisfies(comp, c =>
            {
                float d = (c - __instance.Position).LengthHorizontal;
                return d >= minR && d <= maxR;
            });
            if (!anyValid)
            {
                Messages.Message("MessageTargetBelowMinimumRange".Translate(), __instance, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }

            ref LocalTargetInfo forcedTarget = ref TurretForcedTargetRef(__instance);
            if (forcedTarget != targ)
            {
                forcedTarget = targ;
                if (TurretCooldownRef(__instance) <= 0)
                {
                    __instance.TryStartShootSomething(canBeginBurstImmediately: false);
                }
            }
            if (TurretHoldFireRef(__instance))
            {
                Messages.Message("MessageTurretWontFireBecauseHoldFire".Translate(__instance.def.label), __instance, MessageTypeDefOf.RejectInput, historical: false);
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb), nameof(Verb.OrderForceTarget))]
        public static void Verb_OrderForceTarget_Prefix(Verb __instance, LocalTargetInfo target)
        {
            if (target.HasThing && target.Thing is Pawn giant &&
                GiantTargetAimTracker.TryGetActiveClick(out IntVec3 clickCell, out Vector3 clickWorldPos))
            {
                GiantTargetAimTracker.RecordHitCellClick(__instance.CasterPawn ?? __instance.caster, giant, clickCell, clickWorldPos);
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Verb), nameof(Verb.OrderForceTarget))]
        public static IEnumerable<CodeInstruction> Verb_OrderForceTarget_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.Calls(LocalTargetInfoGetCellMethod))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, ResolveVerbOrderTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
        public static void Verb_TryFindShootLineFromTo_Prefix(
            Verb __instance,
            bool ignoreRange,
            out VerbResolutionState __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = default;
                return;
            }
            __state = new VerbResolutionState
            {
                Pushed = true,
                PrevShooter = GiantTargetAimTracker.CurrentResolutionShooter,
                PrevVerb = GiantTargetAimTracker.CurrentResolutionVerb,
                PrevIgnoreRange = GiantTargetAimTracker.CurrentIgnoreRange
            };
            GiantTargetAimTracker.PushVerbResolution(__instance.caster, __instance, ignoreRange);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
        public static void Verb_TryFindShootLineFromTo_Finalizer(VerbResolutionState __state)
        {
            if (__state.Pushed)
            {
                GiantTargetAimTracker.PopVerbResolution(__state.PrevShooter, __state.PrevVerb, __state.PrevIgnoreRange);
            }
        }

        /// <summary>
        /// When an airborne giant fires a ranged or ability verb from aloft, bypasses ground-wall occlusion unless
        /// blocked by an overhead thick mountain roof; when grounded, allows elevated/lateral weapon muzzles to
        /// establish line-of-sight from their actual muzzle cell.
        /// </summary>
        public static bool HasClearAerialSightline(Map map, IntVec3 sourceCell, IntVec3 destCell, bool blockedByThickRoof)
        {
            if (map == null || !sourceCell.InBounds(map) || !destCell.InBounds(map))
            {
                return false;
            }
            if (!blockedByThickRoof)
            {
                return true;
            }
            if (GiantDestructionUtility.HasThickRoofAt(map, sourceCell) || GiantDestructionUtility.HasThickRoofAt(map, destCell))
            {
                return false;
            }
            foreach (IntVec3 cell in GenSight.PointsOnLineOfSight(sourceCell, destCell))
            {
                if (GiantDestructionUtility.HasThickRoofAt(map, cell))
                {
                    return false;
                }
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
        public static void Verb_TryFindShootLineFromTo_Postfix(
            Verb __instance,
            IntVec3 root,
            LocalTargetInfo targ,
            ref ShootLine resultingLine,
            bool ignoreRange,
            ref bool __result)
        {
            if (__result || !targ.IsValid || __instance == null || __instance is Verb_CarrierDispatchInterceptors)
            {
                return;
            }

            Pawn caster = __instance.CasterPawn;
            Map map = caster?.Map;
            if (map == null || !caster.Spawned || root != caster.Position ||
                !(GiantFastLookup.TryGetRig(caster) is CompGiantRig comp))
            {
                return;
            }

            bool isAirborneCaster = comp.IsCurrentlyAirborne ||
                (comp.FlightLocomotion != null && comp.FlightLocomotion.FlightState == GiantFlightState.TakingOff);
            if (isAirborneCaster && __instance.verbProps != null && !__instance.IsMeleeAttack && __instance.EffectiveRange > 1.42f)
            {
                if (targ.HasThing && targ.Thing.Map != map)
                {
                    return;
                }

                IntVec3 airDestCell = targ.Cell;
                if (targ.HasThing && targ.Thing is Pawn airTargetGiant &&
                    GiantFastLookup.TryGetRig(airTargetGiant) is CompGiantRig airTargetComp &&
                    airTargetComp.Footprint.hitCells.Count > 0)
                {
                    airDestCell = GiantTargetAimTracker.ResolvePreferredHitCell(airTargetGiant, airTargetComp, root, caster, __instance, ignoreRange);
                }
                if (!airDestCell.InBounds(map))
                {
                    return;
                }

                CellRect occupiedRect = targ.HasThing && !(targ.Thing is Pawn g && GiantFastLookup.TryGetRig(g) != null)
                    ? targ.Thing.OccupiedRect()
                    : CellRect.FromCell(airDestCell);
                if (!ignoreRange && __instance.OutOfRange(root, targ, occupiedRect))
                {
                    return;
                }
                if (__instance.verbProps.mustCastOnOpenGround &&
                    (!airDestCell.Standable(map) || map.thingGrid.CellContains(airDestCell, ThingCategory.Pawn)))
                {
                    return;
                }

                bool blockedByThickRoof = comp.FlightLocomotion?.Def?.blockedByThickRoof
                    ?? comp.RigDef?.ActiveFlightLocomotion?.blockedByThickRoof
                    ?? true;
                if (!__instance.verbProps.requireLineOfSight || HasClearAerialSightline(map, root, airDestCell, blockedByThickRoof))
                {
                    resultingLine = new ShootLine(root, airDestCell);
                    __result = true;
                    return;
                }
            }

            if (!(__instance is Verb_LaunchProjectile) ||
                !comp.TryResolveVerbMuzzleShot(__instance, advanceCursor: false, out GiantMuzzleShotInfo shotInfo))
            {
                return;
            }

            IntVec3 muzzleCell = shotInfo.MapCell;
            if (!muzzleCell.IsValid || muzzleCell == root || !muzzleCell.InBounds(map))
            {
                return;
            }

            IntVec3 destCell = targ.Cell;
            if (targ.HasThing && targ.Thing is Pawn targetGiant && GiantFastLookup.TryGetRig(targetGiant) is CompGiantRig targetComp &&
                targetComp.Footprint.hitCells.Count > 0)
            {
                destCell = GiantTargetAimTracker.ResolvePreferredHitCell(targetGiant, targetComp, muzzleCell, caster, __instance, ignoreRange);
            }

            if (!destCell.InBounds(map))
            {
                return;
            }

            if (!ignoreRange)
            {
                if (__instance.OutOfRange(root, targ, CellRect.FromCell(destCell)))
                {
                    return;
                }
                float minR = __instance.verbProps.EffectiveMinRange(targ, caster);
                if (root.DistanceToSquared(destCell) < minR * minR)
                {
                    return;
                }
            }

            if (!__instance.verbProps.requireLineOfSight || GenSight.LineOfSight(muzzleCell, destCell, map, skipFirstCell: true))
            {
                resultingLine = new ShootLine(muzzleCell, destCell);
                __result = true;
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
        public static IEnumerable<CodeInstruction> Verb_TryFindShootLineFromTo_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            bool seenRequireLineOfSight = false;
            bool patchedNoLosBranch = false;

            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction inst = list[i];
                if (VerbPropertiesRequireLineOfSightField != null && inst.LoadsField(VerbPropertiesRequireLineOfSightField))
                {
                    seenRequireLineOfSight = true;
                }

                if (inst.Calls(CellRectClosestCellToMethod))
                {
                    var ldarg2 = new CodeInstruction(OpCodes.Ldarg_2);
                    ldarg2.MoveLabelsFrom(inst);
                    ldarg2.MoveBlocksFrom(inst);
                    yield return ldarg2;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
                    yield return new CodeInstruction(OpCodes.Call, ResolveLeanDestCellMethod);
                }
                else if (seenRequireLineOfSight && !patchedNoLosBranch && inst.Calls(LocalTargetInfoGetCellMethod))
                {
                    // Match the LocalTargetInfo.get_Cell call immediately following Ldfld VerbProperties.requireLineOfSight
                    // (the !requireLineOfSight TRUE branch building the returned ShootLine).
                    patchedNoLosBranch = true;
                    var ldarg0 = new CodeInstruction(OpCodes.Ldarg_0);
                    ldarg0.MoveLabelsFrom(inst);
                    ldarg0.MoveBlocksFrom(inst);
                    yield return ldarg0;
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
                    yield return new CodeInstruction(OpCodes.Call, ResolveShootLineTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
        public static IEnumerable<CodeInstruction> Verb_LaunchProjectile_TryCastShot_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            bool patched = false;

            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction inst = list[i];
                if (!patched &&
                    inst.LoadsField(VerbCurrentTargetField) &&
                    i + 2 < list.Count &&
                    list[i + 1].opcode == OpCodes.Ldarg_0 &&
                    list[i + 2].LoadsField(VerbCurrentTargetField))
                {
                    // Confirm this pair belongs to the final Projectile.Launch(usedTarget, intendedTarget, ...) call.
                    bool nearLaunch = false;
                    for (int k = i + 3; k < list.Count && k <= i + 12; k++)
                    {
                        if (list[k].Calls(ProjectileLaunchMethod))
                        {
                            nearLaunch = true;
                            break;
                        }
                    }

                    if (nearLaunch)
                    {
                        yield return inst; // pushes currentTarget
                        yield return new CodeInstruction(OpCodes.Ldarg_0); // Verb
                        yield return new CodeInstruction(OpCodes.Ldloc_2); // ShootLine resultingLine
                        yield return new CodeInstruction(OpCodes.Call, ResolveLaunchUsedTargetMethod);
                        patched = true;
                        continue;
                    }
                }

                yield return inst;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
        public static void ShotReport_HitReportFor_Prefix(Thing caster, Verb verb, out VerbResolutionState __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = default;
                return;
            }
            __state = new VerbResolutionState
            {
                Pushed = true,
                PrevShooter = GiantTargetAimTracker.CurrentResolutionShooter,
                PrevVerb = GiantTargetAimTracker.CurrentResolutionVerb,
                PrevIgnoreRange = GiantTargetAimTracker.CurrentIgnoreRange
            };
            GiantTargetAimTracker.PushVerbResolution(caster, verb, ignoreRange: false);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
        public static void ShotReport_HitReportFor_Finalizer(VerbResolutionState __state)
        {
            if (__state.Pushed)
            {
                GiantTargetAimTracker.PopVerbResolution(__state.PrevShooter, __state.PrevVerb, __state.PrevIgnoreRange);
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
        public static IEnumerable<CodeInstruction> ShotReport_HitReportFor_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.Calls(LocalTargetInfoGetCellMethod))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, ResolveEffectiveTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CoverUtility), nameof(CoverUtility.CalculateCoverGiverSet))]
        public static IEnumerable<CodeInstruction> CoverUtility_CalculateCoverGiverSet_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.Calls(LocalTargetInfoGetCellMethod))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1); // shooterLoc
                    yield return new CodeInstruction(OpCodes.Call, ResolveCoverTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CoverUtility), nameof(CoverUtility.CalculateOverallBlockChance))]
        public static IEnumerable<CodeInstruction> CoverUtility_CalculateOverallBlockChance_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.Calls(LocalTargetInfoGetCellMethod))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1); // shooterLoc
                    yield return new CodeInstruction(OpCodes.Call, ResolveCoverTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CoverUtility), "TryFindAdjustedCoverInCell")]
        public static IEnumerable<CodeInstruction> CoverUtility_TryFindAdjustedCoverInCell_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.Calls(LocalTargetInfoGetCellMethod))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0); // shooterLoc
                    yield return new CodeInstruction(OpCodes.Call, ResolveCoverTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }

        private static IntVec3 ResolveEffectiveTargetCellRef(ref LocalTargetInfo target, Thing caster, Verb verb)
        {
            if (target.HasThing && target.Thing is Pawn giant && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0 && caster != null)
            {
                return GiantTargetAimTracker.ResolvePreferredHitCell(giant, comp, caster.Position, caster, verb, ignoreRange: false);
            }
            return target.Cell;
        }

        private static IntVec3 ResolveCoverTargetCellRef(ref LocalTargetInfo target, IntVec3 shooterLoc)
        {
            if (target.HasThing && target.Thing is Pawn giant && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0)
            {
                return GiantTargetAimTracker.ResolvePreferredHitCell(
                    giant,
                    comp,
                    shooterLoc,
                    GiantTargetAimTracker.CurrentResolutionShooter,
                    GiantTargetAimTracker.CurrentResolutionVerb,
                    GiantTargetAimTracker.CurrentIgnoreRange);
            }
            return target.Cell;
        }

        private static IntVec3 ResolveLeanDestCell(
            ref CellRect occupiedRect,
            IntVec3 root,
            LocalTargetInfo targ,
            Verb verb,
            bool ignoreRange)
        {
            if (targ.HasThing && targ.Thing is Pawn giant && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0)
            {
                return GiantTargetAimTracker.ResolvePreferredHitCell(giant, comp, root, verb?.caster, verb, ignoreRange);
            }
            return occupiedRect.ClosestCellTo(root);
        }

        private static IntVec3 ResolveShootLineTargetCellRef(
            ref LocalTargetInfo targ,
            Verb verb,
            IntVec3 root,
            bool ignoreRange)
        {
            if (targ.HasThing && targ.Thing is Pawn giant && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0)
            {
                return GiantTargetAimTracker.ResolvePreferredHitCell(giant, comp, root, verb?.caster, verb, ignoreRange);
            }
            return targ.Cell;
        }

        private static LocalTargetInfo ResolveLaunchUsedTarget(
            LocalTargetInfo currentTarget,
            Verb verb,
            ShootLine resultingLine)
        {
            if (currentTarget.HasThing && currentTarget.Thing is Pawn giant &&
                GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                resultingLine.Dest.IsValid &&
                comp.Footprint.hitCellSet.Contains(resultingLine.Dest))
            {
                return new LocalTargetInfo(resultingLine.Dest);
            }
            return currentTarget;
        }

        private static IntVec3 ResolveVerbOrderTargetCellRef(ref LocalTargetInfo target, Verb verb)
        {
            if (target.HasThing && target.Thing is Pawn giant && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0 && verb?.CasterPawn != null)
            {
                return GiantTargetAimTracker.ResolvePreferredHitCell(
                    giant,
                    comp,
                    verb.CasterPawn.Position,
                    verb.CasterPawn,
                    verb,
                    ignoreRange: false);
            }
            return target.Cell;
        }

        internal static IntVec3 ResolveAttackStaticTargetCellRef(ref LocalTargetInfo target, Verb verb, Pawn shooter)
        {
            if (target.HasThing && target.Thing is Pawn giant && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp &&
                comp.Footprint.hitCells.Count > 0 && shooter != null)
            {
                return GiantTargetAimTracker.ResolvePreferredHitCell(giant, comp, shooter.Position, shooter, verb, ignoreRange: false);
            }
            return target.Cell;
        }

        /// <summary>
        /// Allows melee attackers & touch interactions to succeed symmetrically both when attacking a giant's HitCells
        /// and when a giant attacker touches a smaller target adjacent to its own hull / within meleeTouchRadius.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetMeleeAttackAction))]
        public static bool FloatMenuUtility_GetMeleeAttackAction_Prefix(
            Pawn pawn,
            LocalTargetInfo target,
            ref string failStr,
            ref Action __result)
        {
            if (pawn != null && target.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(pawn, target.Thing))
            {
                failStr = "GCF_FloatMenu_CannotMeleeAirborne".CanTranslate()
                    ? "GCF_FloatMenu_CannotMeleeAirborne".Translate(target.Thing.LabelShort).ToString()
                    : $"Cannot melee airborne target {target.Thing.LabelShort}";
                __result = null;
                return false;
            }
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) is CompGiantRig rig && rig.PartCombat != null)
            {
                if (target.IsValid && target.HasThing &&
                    CanGiantIssueDraftedAttackAction(pawn, target.Thing, out _, out _, out bool hasMelee) &&
                    hasMelee)
                {
                    LocalTargetInfo capturedTarget = target;
                    Pawn capturedPawn = pawn;
                    CompGiantRig capturedRig = rig;
                    failStr = "";
                    __result = delegate
                    {
                        if (capturedTarget.HasThing && (capturedTarget.Thing.Destroyed || !capturedTarget.Thing.Spawned))
                        {
                            return;
                        }
                        if (capturedTarget.Thing is Pawn tp && tp.Dead)
                        {
                            return;
                        }
                        GiantPartCombatUtility.OrderGiantFocusAttack(capturedPawn, capturedRig, capturedTarget, allowApproachIfOutOfRange: true);
                    };
                    return false;
                }
                failStr = "";
                __result = null;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(JobDriver_AttackMelee), nameof(JobDriver_AttackMelee.TryMakePreToilReservations))]
        public static bool JobDriver_AttackMelee_TryMakePreToilReservations_Prefix(
            JobDriver_AttackMelee __instance,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            Pawn caster = __instance?.pawn;
            Thing targetThing = __instance?.job?.targetA.Thing;
            if (caster != null && targetThing != null &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, targetThing))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ReachabilityImmediate), nameof(ReachabilityImmediate.CanReachImmediate),
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(Map), typeof(PathEndMode), typeof(Pawn) })]
        public static bool ReachabilityImmediate_CanReachImmediate_Prefix(
            LocalTargetInfo target,
            PathEndMode peMode,
            Pawn pawn,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            if (peMode == PathEndMode.Touch && pawn != null && target.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(pawn, target.Thing))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ReachabilityImmediate), nameof(ReachabilityImmediate.CanReachImmediate),
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(Map), typeof(PathEndMode), typeof(Pawn) })]
        public static void ReachabilityImmediate_CanReachImmediate_Postfix(
            IntVec3 start,
            LocalTargetInfo target,
            Map map,
            PathEndMode peMode,
            Pawn pawn,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty || !GiantHullInteractionUtility.IsGiantTouchCompatiblePathEndMode(peMode) || !target.IsValid)
            {
                return;
            }
            if (pawn != null && target.HasThing && GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(pawn, target.Thing))
            {
                return;
            }
            if (target.HasThing && target.Thing.Spawned && target.Thing.Map == map)
            {
                CompGiantRig comp = GiantFastLookup.TryGetRig(target.Thing as Pawn)
                    ?? TryGetEnabledCorpseRig(target.Thing);
                if (comp != null && (pawn == null || GiantFastLookup.TryGetRig(pawn) == null))
                {
                    bool validSmallTouch = GiantHullInteractionUtility.IsCellValidSmallPawnTouch(map, pawn, target.Thing, start);
                    if (__result && !validSmallTouch && GiantHullInteractionUtility.IsLiveStandingGroundedGiant(target.Thing, comp))
                    {
                        __result = false;
                        return;
                    }
                    if (!__result && validSmallTouch)
                    {
                        __result = true;
                        return;
                    }
                }
            }

            if (__result)
            {
                return;
            }

            if (pawn != null && pawn.Spawned && pawn.Map == map &&
                GiantFastLookup.TryGetRig(pawn) is CompGiantRig attackerComp)
            {
                bool hasTargetGiantHitCells = target.HasThing && target.Thing.Spawned
                    && (GiantFastLookup.TryGetRig(target.Thing as Pawn) ?? TryGetEnabledCorpseRig(target.Thing)) is CompGiantRig targetRig
                    && targetRig.Footprint.hitCells.Count > 0;
                bool isIngestTarget = !hasTargetGiantHitCells && IsGiantIngestJobTarget(pawn, target);

                if (!ShouldApplyGiantAttackerHullTouch(peMode, target.HasThing, hasTargetGiantHitCells, isIngestTarget))
                {
                    return;
                }

                bool isStationaryNonGoto = IsGiantInStationaryTouchToil(pawn, target);

                if (isIngestTarget)
                {
                    CellRect targetRect = target.HasThing && target.Thing.Spawned
                        ? target.Thing.OccupiedRect()
                        : CellRect.FromCell(target.Cell);
                    bool isMovingAtFinalNode = pawn.pather != null
                        && pawn.pather.Moving
                        && pawn.pather.curPath != null
                        && pawn.pather.curPath.NodesLeftCount <= 1;

                    if (EvaluateGiantSmallIngestTouchImmediate(
                        start,
                        pawn.Position,
                        isStationaryNonGoto,
                        isMovingAtFinalNode,
                        attackerComp.Footprint.localCollisionOffsets,
                        attackerComp.Footprint.maxCollisionHorizontalExtent,
                        attackerComp.GetEffectiveMeleeTouchRadius(),
                        targetRect,
                        attackerComp.Footprint.hitCells))
                    {
                        __result = true;
                    }
                    return;
                }

                float extraSlack = 0f;
                if (isStationaryNonGoto)
                {
                    float baseMelee = attackerComp.GetEffectiveMeleeTouchRadius();
                    float stationaryRadius = ComputeGiantStationaryTouchRadius(
                        baseMelee,
                        attackerComp.Footprint.localCollisionOffsets,
                        attackerComp.Footprint.maxCollisionHorizontalExtent);
                    extraSlack = Mathf.Max(0f, stationaryRadius - baseMelee);
                }

                if (IsGiantAttackerInTouchRange(attackerComp, start, target, extraRadiusSlack: extraSlack))
                {
                    __result = true;
                }
            }
        }

        public static bool ShouldApplyGiantAttackerHullTouch(
            PathEndMode peMode,
            bool targetHasThing,
            bool targetHasGiantHitCells,
            bool isGiantIngestTarget)
        {
            if (peMode == PathEndMode.Touch)
            {
                return true;
            }
            if (targetHasGiantHitCells)
            {
                return GiantHullInteractionUtility.IsGiantTouchCompatiblePathEndMode(peMode);
            }
            if (targetHasThing && isGiantIngestTarget)
            {
                return GiantHullInteractionUtility.IsGiantTouchCompatiblePathEndMode(peMode);
            }
            return false;
        }

        public static float ComputeMaxLocalOffsetDistance(
            IReadOnlyList<IntVec3> localOffsets,
            int fallbackHorizontalExtent)
        {
            float maxDistSq = 0f;
            if (localOffsets != null && localOffsets.Count > 0)
            {
                for (int i = 0; i < localOffsets.Count; i++)
                {
                    IntVec3 off = localOffsets[i];
                    float dSq = off.x * off.x + off.z * off.z;
                    if (dSq > maxDistSq)
                    {
                        maxDistSq = dSq;
                    }
                }
                return Mathf.Sqrt(maxDistSq);
            }
            return fallbackHorizontalExtent > 0 ? fallbackHorizontalExtent * 1.4142135f : 0f;
        }

        public static float ComputeGiantStationaryTouchRadius(
            float baseMeleeTouchRadius,
            IReadOnlyList<IntVec3> localOffsets,
            int maxCollisionHorizontalExtent)
        {
            float baseWithSlack = baseMeleeTouchRadius + 2.75f;
            float maxOffsetDist = ComputeMaxLocalOffsetDistance(localOffsets, maxCollisionHorizontalExtent);
            float hullRotationSafeRadius = maxOffsetDist > 0.01f ? maxOffsetDist + 2.5f : 0f;
            return Mathf.Max(baseWithSlack, hullRotationSafeRadius);
        }

        public static float ComputeSnugIngestWalkRadius(
            float baseMeleeTouchRadius,
            int maxCollisionHorizontalExtent)
        {
            float scaledExtent = maxCollisionHorizontalExtent > 0 ? maxCollisionHorizontalExtent * 0.55f : 0f;
            float desired = Mathf.Max(baseMeleeTouchRadius, scaledExtent);
            return Mathf.Clamp(desired, 1.5f, 3.2f);
        }

        public static bool EvaluateGiantSmallIngestTouchImmediate(
            IntVec3 start,
            IntVec3 pawnPosition,
            bool isStationaryNonGotoToil,
            bool isMovingAtFinalPathNode,
            IReadOnlyList<IntVec3> attackerLocalOffsets,
            int maxCollisionHorizontalExtent,
            float baseMeleeTouchRadius,
            CellRect targetRect,
            IReadOnlyList<HitCellEntry> attackerHitCells = null)
        {
            if (isStationaryNonGotoToil)
            {
                float stationaryRadius = ComputeGiantStationaryTouchRadius(
                    baseMeleeTouchRadius,
                    attackerLocalOffsets,
                    maxCollisionHorizontalExtent);
                if (IsGiantHullInTouchRangeOfCells(start, attackerLocalOffsets, stationaryRadius, null, targetRect))
                {
                    return true;
                }
                if (attackerHitCells != null)
                {
                    for (int i = 0; i < attackerHitCells.Count; i++)
                    {
                        IntVec3 hc = attackerHitCells[i].cell;
                        int dx = hc.x < targetRect.minX ? targetRect.minX - hc.x : (hc.x > targetRect.maxX ? hc.x - targetRect.maxX : 0);
                        int dz = hc.z < targetRect.minZ ? targetRect.minZ - hc.z : (hc.z > targetRect.maxZ ? hc.z - targetRect.maxZ : 0);
                        if (dx <= 1 && dz <= 1)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            float snugRadius = ComputeSnugIngestWalkRadius(baseMeleeTouchRadius, maxCollisionHorizontalExtent);
            if (IsGiantHullInTouchRangeOfCells(start, null, snugRadius, null, targetRect))
            {
                return true;
            }

            if (start != pawnPosition || isMovingAtFinalPathNode)
            {
                return IsGiantHullInTouchRangeOfCells(start, attackerLocalOffsets, baseMeleeTouchRadius, null, targetRect);
            }

            return false;
        }

        internal static bool IsGiantIngestJobTarget(Pawn pawn, LocalTargetInfo target)
        {
            Job curJob = pawn?.CurJob;
            JobDef def = curJob?.def;
            if (def == null || !target.IsValid)
            {
                return false;
            }
            if (curJob.targetA != target && curJob.targetB != target)
            {
                return false;
            }
            if (def == JobDefOf.Ingest
                || string.Equals(def.defName, "Ingest", StringComparison.Ordinal)
                || (def.driverClass != null && typeof(IEatingDriver).IsAssignableFrom(def.driverClass)))
            {
                return true;
            }
            if ((def == JobDefOf.PredatorHunt || string.Equals(def.defName, "PredatorHunt", StringComparison.Ordinal))
                && target.HasThing && target.Thing is Corpse)
            {
                return true;
            }
            return false;
        }

        internal static bool IsGiantInStationaryTouchToil(Pawn pawn, LocalTargetInfo target)
        {
            if (pawn == null || pawn.pather == null || pawn.pather.Moving || pawn.CurJob == null)
            {
                return false;
            }
            if (pawn.CurJob.targetA != target && pawn.CurJob.targetB != target)
            {
                return false;
            }
            JobDriver curDriver = pawn.jobs?.curDriver;
            Toil curToil = curDriver != null ? JobDriverCurToilFast?.Invoke(curDriver) : null;
            if (curToil != null && curToil.defaultCompleteMode == ToilCompleteMode.PatherArrival)
            {
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTargetFrom))]
        public static bool Verb_CanHitTargetFrom_Prefix(
            Verb __instance,
            LocalTargetInfo targ,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            if (__instance != null &&
                (__instance is Verb_MeleeAttack || (__instance.verbProps != null && __instance.verbProps.IsMeleeAttack)) &&
                targ.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(__instance.CasterPawn, targ.Thing))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTargetFrom))]
        public static void Verb_CanHitTargetFrom_Postfix(
            Verb __instance,
            IntVec3 root,
            LocalTargetInfo targ,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty || __result || !(__instance is Verb_MeleeAttack) || !targ.IsValid)
            {
                return;
            }
            if (targ.HasThing && GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(__instance.CasterPawn, targ.Thing))
            {
                return;
            }
            if (__instance.CasterPawn is Pawn caster && caster.Spawned &&
                GiantFastLookup.TryGetRig(caster) is CompGiantRig attackerComp &&
                IsGiantAttackerInTouchRange(attackerComp, root, targ))
            {
                __result = true;
            }
        }

        public static bool CanGiantPerformMelee(Pawn p)
        {
            // Base-Pawn melee is strictly forbidden on all GCF giants; all attacks execute via BoneNodeDef partAttacks.
            if (p == null || p.kindDef?.canMeleeAttack == false || GiantFastLookup.TryGetRig(p) != null)
            {
                return false;
            }
            return p.def?.tools != null && p.def.tools.Count > 0;
        }

        public static bool HasAnyPositiveDirectionalMeleeTool(Pawn caster, CompGiantRig comp, float relativeBearingDeg)
        {
            List<Tool> tools = caster?.def?.tools;
            if (tools == null || comp == null)
            {
                return false;
            }
            for (int i = 0; i < tools.Count; i++)
            {
                Tool t = tools[i];
                if (t == null || !comp.IsMeleeToolAnatomicallyIntact(t))
                {
                    continue;
                }
                string tTag = $"{GiantRigDef.BuildToolMatchTag(t)}|{t.label}";
                if (ComputeDirectionalToolMultiplier(tTag, relativeBearingDeg) > 0f)
                {
                    return true;
                }
            }
            return false;
        }

        public static float ComputeDirectionalToolMultiplier(string toolTag, float relativeBearingDeg)
        {
            if (string.IsNullOrEmpty(toolTag))
            {
                return 1f;
            }

            string lower = toolTag.ToLowerInvariant();
            float absBearing = Mathf.Abs(Mathf.DeltaAngle(0f, relativeBearingDeg));
            float signed = Mathf.DeltaAngle(0f, relativeBearingDeg);

            if (lower.Contains("left") || lower.Contains("port") || lower.Contains("whip_l") || lower.Contains("sponson_l"))
            {
                if (signed <= -15f && signed >= -145f) return 2.45f;
                if (signed < 12f && absBearing <= 45f) return 0.95f;
                return 0f;
            }

            if (lower.Contains("right") || lower.Contains("starboard") || lower.Contains("whip_r") || lower.Contains("sponson_r"))
            {
                if (signed >= 15f && signed <= 145f) return 2.45f;
                if (signed > -12f && absBearing <= 45f) return 0.95f;
                return 0f;
            }

            if (lower.Contains("tail") || lower.Contains("halberd") || lower.Contains("stinger") ||
                lower.Contains("rear") || lower.Contains("crucible") || lower.Contains("abdomen") ||
                lower.Contains("thruster") || lower.Contains("propulsion") ||
                lower.Contains("stern") || lower.Contains("aft") || lower.Contains("exhaust"))
            {
                if (absBearing >= 120f) return 2.65f;
                if (absBearing >= 92f) return 1.35f;
                return 0f;
            }

            if (lower.Contains("maw") || lower.Contains("bite") || lower.Contains("jaw") ||
                lower.Contains("head") || lower.Contains("teeth") || lower.Contains("mouth") ||
                lower.Contains("mandible") || lower.Contains("front") || lower.Contains("horn") ||
                lower.Contains("carapace") || lower.Contains("turret") || lower.Contains("cranial") ||
                lower.Contains("prow") || lower.Contains("chassis") || lower.Contains("bow") ||
                lower.Contains("hull") || lower.Contains("dozer") || lower.Contains("bumper") ||
                lower.Contains("ram") || lower.Contains("claw"))
            {
                if (absBearing <= 62f) return 2.15f;
                if (absBearing <= 92f) return 0.85f;
                return 0f;
            }

            return 1f;
        }

        /// <summary>
        /// Evaluates whether a giant attacker's hull (anchor + translated CollisionCells offsets)
        /// can melee-touch either a giant target's HitCells or a fallback occupied rect.
        /// </summary>
        public static bool IsGiantHullInTouchRangeOfCells(
            IntVec3 attackerAnchor,
            IReadOnlyList<IntVec3> attackerLocalOffsets,
            float attackerMeleeTouchRadius,
            IReadOnlyList<HitCellEntry> targetHitCells,
            CellRect targetFallbackRect,
            HashSet<IntVec3> targetHitCellSet = null)
        {
            if (targetHitCells != null && targetHitCells.Count > 0)
            {
                float touchRad = attackerMeleeTouchRadius > 0.01f ? attackerMeleeTouchRadius + 1.15f : 1.5f;
                float touchRadSq = touchRad * touchRad;

                for (int i = 0; i < targetHitCells.Count; i++)
                {
                    IntVec3 hc = targetHitCells[i].cell;
                    int rdx = attackerAnchor.x - hc.x;
                    int rdz = attackerAnchor.z - hc.z;
                    if (rdx * rdx + rdz * rdz <= touchRadSq)
                    {
                        return true;
                    }
                }

                if (attackerLocalOffsets != null && attackerLocalOffsets.Count > 0)
                {
                    if (targetHitCellSet != null && targetHitCellSet.Count > 0)
                    {
                        for (int o = 0; o < attackerLocalOffsets.Count; o++)
                        {
                            IntVec3 cc = attackerAnchor + attackerLocalOffsets[o];
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (targetHitCellSet.Contains(new IntVec3(cc.x + dx, cc.y, cc.z + dz)))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int o = 0; o < attackerLocalOffsets.Count; o++)
                        {
                            IntVec3 cc = attackerAnchor + attackerLocalOffsets[o];
                            for (int i = 0; i < targetHitCells.Count; i++)
                            {
                                IntVec3 hc = targetHitCells[i].cell;
                                int dx = cc.x - hc.x;
                                int dz = cc.z - hc.z;
                                if (dx >= -1 && dx <= 1 && dz >= -1 && dz <= 1)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }

            if (attackerLocalOffsets != null && attackerLocalOffsets.Count > 0)
            {
                for (int i = 0; i < attackerLocalOffsets.Count; i++)
                {
                    IntVec3 cc = attackerAnchor + attackerLocalOffsets[i];
                    int dx = cc.x < targetFallbackRect.minX ? targetFallbackRect.minX - cc.x : (cc.x > targetFallbackRect.maxX ? cc.x - targetFallbackRect.maxX : 0);
                    int dz = cc.z < targetFallbackRect.minZ ? targetFallbackRect.minZ - cc.z : (cc.z > targetFallbackRect.maxZ ? cc.z - targetFallbackRect.maxZ : 0);
                    if (dx <= 1 && dz <= 1)
                    {
                        return true;
                    }
                }
            }

            if (attackerMeleeTouchRadius > 0.01f)
            {
                float rdx = attackerAnchor.x < targetFallbackRect.minX ? targetFallbackRect.minX - attackerAnchor.x : (attackerAnchor.x > targetFallbackRect.maxX ? attackerAnchor.x - targetFallbackRect.maxX : 0);
                float rdz = attackerAnchor.z < targetFallbackRect.minZ ? targetFallbackRect.minZ - attackerAnchor.z : (attackerAnchor.z > targetFallbackRect.maxZ ? attackerAnchor.z - targetFallbackRect.maxZ : 0);
                float limit = attackerMeleeTouchRadius + 1.15f;
                if (rdx * rdx + rdz * rdz <= limit * limit)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsGiantAttackerInTouchRange(
            CompGiantRig attackerComp,
            IntVec3 attackerRoot,
            LocalTargetInfo target,
            float extraRadiusSlack = 0f)
        {
            if (attackerComp?.Pawn == null || !target.IsValid)
            {
                return false;
            }

            CompGiantRig targetComp = target.HasThing && target.Thing.Spawned
                ? (GiantFastLookup.TryGetRig(target.Thing as Pawn) ?? TryGetEnabledCorpseRig(target.Thing))
                : null;

            CellRect targetRect = target.HasThing && target.Thing.Spawned
                ? target.Thing.OccupiedRect()
                : CellRect.FromCell(target.Cell);
            IReadOnlyList<HitCellEntry> targetHits = targetComp != null && targetComp.Footprint.hitCells.Count > 0
                ? targetComp.Footprint.hitCells
                : null;

            bool useCanopyStandoff = targetComp == null
                && target.HasThing
                && target.Thing is Pawn smallPawn
                && !smallPawn.Dead
                && GiantPartCombatRuntime.ShouldUseVisualCanopyPerimeterForTarget(attackerComp, target.Thing, null);
            if (useCanopyStandoff)
            {
                EvaluateGiantSmallPawnCanopyStandoff(
                    attackerRoot,
                    attackerComp.Footprint.localPawnExclusionOffsets,
                    attackerComp.GetEffectiveMeleeTouchRadius() + extraRadiusSlack,
                    targetRect,
                    out bool canopyTouch,
                    out bool canopyOverlaps);
                return canopyTouch && (!canopyOverlaps || extraRadiusSlack > 0.05f);
            }

            return IsGiantHullInTouchRangeOfCells(
                attackerRoot,
                attackerComp.Footprint.localCollisionOffsets,
                attackerComp.GetEffectiveMeleeTouchRadius() + extraRadiusSlack,
                targetHits,
                targetRect,
                targetComp?.Footprint.hitCellSet);
        }

        public static void EvaluateGiantSmallPawnCanopyStandoff(
            IntVec3 candidateAnchor,
            IReadOnlyList<IntVec3> canopyOffsets,
            float touchRadius,
            CellRect smallPawnRect,
            out bool inTouchRange,
            out bool overlapsCanopy)
        {
            overlapsCanopy = GiantPartCombatUtility.ComputeMinTranslatedHullGapToRectSq(
                candidateAnchor,
                canopyOffsets,
                smallPawnRect) <= 0.01f;
            inTouchRange = IsGiantHullInTouchRangeOfCells(
                candidateAnchor,
                canopyOffsets,
                touchRadius,
                null,
                smallPawnRect);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.IsAdjacentOrInsideAndAllowedToTouch))]
        public static void TouchPathEndModeUtility_IsAdjacent_Postfix(
            IntVec3 root,
            LocalTargetInfo target,
            ref bool __result)
        {
            if (!target.HasThing || !target.Thing.Spawned)
            {
                return;
            }
            CompGiantRig comp = GiantFastLookup.TryGetRig(target.Thing as Pawn)
                ?? TryGetEnabledCorpseRig(target.Thing);
            if (comp == null)
            {
                return;
            }
            Pawn activePawn = GiantPathingContext.ActiveParameterizePawn ?? GiantPathingContext.ActiveGiant;
            if (activePawn != null && GiantFastLookup.TryGetRig(activePawn) != null)
            {
                if (!__result && IsCellTouchingFootprint(root, comp.Footprint))
                {
                    __result = true;
                }
                return;
            }
            bool validSmallTouch = GiantHullInteractionUtility.IsCellValidSmallPawnTouch(target.Thing.Map, activePawn, target.Thing, root);
            if (__result && !validSmallTouch && GiantHullInteractionUtility.IsLiveStandingGroundedGiant(target.Thing, comp))
            {
                __result = false;
                return;
            }
            if (!__result && validSmallTouch)
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.AddAllowedAdjacentRegions))]
        public static void TouchPathEndModeUtility_AddAllowedAdjacentRegions_Postfix(
            LocalTargetInfo dest,
            TraverseParms traverseParams,
            Map map,
            List<Region> regions)
        {
            if (!dest.HasThing || !dest.Thing.Spawned || dest.Thing.Map != map)
            {
                return;
            }
            CompGiantRig comp = GiantFastLookup.TryGetRig(dest.Thing as Pawn)
                ?? TryGetEnabledCorpseRig(dest.Thing);
            if (comp == null)
            {
                return;
            }

            List<HitCellEntry> hitCells = comp.Footprint.hitCells;
            for (int i = 0; i < hitCells.Count; i++)
            {
                IntVec3 hc = hitCells[i].cell;
                for (int d = 0; d < 8; d++)
                {
                    IntVec3 adj = hc + GenAdj.AdjacentCells[d];
                    if (!adj.InBounds(map))
                    {
                        continue;
                    }
                    Region reg = adj.GetRegion(map);
                    if (reg != null && reg.Allows(traverseParams, isDestination: true) && !regions.Contains(reg))
                    {
                        regions.Add(reg);
                    }
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PathFinder), "ParameterizePathJob")]
        public static void PathFinder_ParameterizePathJob_Prefix(
            PathRequest request,
            out GiantPathingContext.ParameterizeScopeState __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = default;
                return;
            }
            Pawn requester = request != null ? (request.pawn ?? request.TraverseParms.pawn) : null;
            IntVec3 startCell = request != null ? request.Start : IntVec3.Invalid;
            __state = GiantPathingContext.PushParameterizePawn(requester, startCell);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(PathFinder), "ParameterizePathJob")]
        public static void PathFinder_ParameterizePathJob_Finalizer(
            GiantPathingContext.ParameterizeScopeState __state)
        {
            GiantPathingContext.PopParameterizePawn(__state);
        }

        private static class SmallDestScratchPool
        {
            [ThreadStatic]
            private static List<(int idx, byte tier, bool walkable)> cached;

            public static List<(int idx, byte tier, bool walkable)> Get()
            {
                var list = cached ??= new List<(int idx, byte tier, bool walkable)>(196);
                list.Clear();
                return list;
            }
        }

        /// <summary>
        /// Shapes the A* PathEndMode.Touch destination set in RimWorld 1.7's Burst PathFinderJob to:
        /// 1) the walkable outer perimeter ring touching a Giant Creature target's HitCells, and
        /// 2) crowd-aware, non-overlapping giant hull anchors when the attacker itself is a giant.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PathFinder), "MakeDestination")]
        public static void PathFinder_MakeDestination_Postfix(
            LocalTargetInfo target,
            PathEndMode peMode,
            PathingContext context,
            ref CellRect rect,
            ref NativeList<int> excluded)
        {
            if (GiantFastLookup.IsEmpty || !GiantHullInteractionUtility.IsGiantTouchCompatiblePathEndMode(peMode) || !target.HasThing || !target.Thing.Spawned)
            {
                return;
            }

            Map map = context.map;
            if (map == null)
            {
                return;
            }

            Pawn attacker = GiantPathingContext.ActiveParameterizePawn;
            IntVec3 startCell = GiantPathingContext.ActiveParameterizeStartCell;
            CompGiantRig attackerComp = GiantFastLookup.TryGetRig(attacker);
            if (attackerComp != null && attackerComp.RigDef == null)
            {
                attackerComp = null;
            }

            CompGiantRig targetComp = GiantFastLookup.TryGetRig(target.Thing as Pawn)
                ?? TryGetEnabledCorpseRig(target.Thing);
            bool hasTargetHitCells = targetComp != null && targetComp.Footprint.hitCells.Count > 0;
            if (attackerComp == null && !hasTargetHitCells)
            {
                return;
            }

            CellIndices indices = map.cellIndices;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            bool isMovingPawnTarget = attacker != null
                && startCell.IsValid
                && startCell == attacker.Position
                && target.Thing is Pawn tpMove
                && tpMove.pather != null
                && tpMove.pather.Moving;

            if (attackerComp == null)
            {
                rect = targetComp.Footprint.tightHitBounds.ExpandedBy(1).ClipInsideMap(map);
                excluded.Clear();

                bool isLiveGroundedGiant = GiantHullInteractionUtility.IsLiveStandingGroundedGiant(target.Thing, targetComp);
                bool attackerIgnoresCollision = GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(attacker);

                if (!isLiveGroundedGiant || attackerIgnoresCollision)
                {
                    bool isLiveGiant = target.Thing is Pawn p && !p.Dead && !p.Downed;
                    int startIdx = (isMovingPawnTarget && rect.Contains(startCell))
                        ? indices.CellToIndex(startCell)
                        : -1;
                    bool startWasValid = false;
                    int otherWalkableValidCount = 0;

                    foreach (IntVec3 c in rect)
                    {
                        int idx = indices.CellToIndex(c);
                        bool blockedInterior = isLiveGiant && !attackerIgnoresCollision && targetComp.Footprint.collisionCellSet.Contains(c);
                        bool blockedByOtherGiant = mapComp != null && mapComp.GetBlockingGiantForSmallPawnAt(c, attacker) != null;
                        bool touchesAnyHitCell = !blockedInterior && !blockedByOtherGiant && IsCellTouchingFootprint(c, targetComp.Footprint);
                        if (!touchesAnyHitCell)
                        {
                            excluded.Add(idx);
                        }
                        else if (idx == startIdx)
                        {
                            startWasValid = true;
                        }
                        else if (c.Walkable(map))
                        {
                            otherWalkableValidCount++;
                        }
                    }

                    if (startWasValid && otherWalkableValidCount > 0)
                    {
                        excluded.Add(startIdx);
                    }
                    return;
                }

                var smallDestScratch = SmallDestScratchPool.Get();

                int liveStartIdx = (startCell.IsValid && rect.Contains(startCell))
                    ? indices.CellToIndex(startCell)
                    : -1;

                int anyWalkableA = 0, nonStartWalkableA = 0;
                int anyWalkableB = 0, nonStartWalkableB = 0;

                foreach (IntVec3 c in rect)
                {
                    int idx = indices.CellToIndex(c);
                    bool blockedCore = targetComp.Footprint.collisionCellSet.Contains(c)
                        || (mapComp != null && mapComp.GetBlockingGiantForSmallPawnAt(c, attacker) != null);
                    if (blockedCore)
                    {
                        smallDestScratch.Add((idx, 0, false));
                        continue;
                    }

                    bool blockedCanopy = targetComp.Footprint.pawnExclusionCellSet.Contains(c)
                        || (mapComp != null && mapComp.GetPawnExclusionGiantForSmallPawnAt(c, attacker) != null);
                    bool walkable = attacker != null ? c.WalkableBy(map, attacker) : c.Walkable(map);
                    byte tier = 0;

                    if (!blockedCanopy && targetComp.Footprint.IsCellStrictExteriorTouch(c))
                    {
                        tier = 1;
                        if (walkable)
                        {
                            anyWalkableA++;
                            if (idx != liveStartIdx) nonStartWalkableA++;
                        }
                    }
                    else if (!blockedCanopy && targetComp.Footprint.IsCellCanopySafeTouch(c))
                    {
                        tier = 2;
                        if (walkable)
                        {
                            anyWalkableB++;
                            if (idx != liveStartIdx) nonStartWalkableB++;
                        }
                    }
                    else if (IsCellTouchingFootprint(c, targetComp.Footprint))
                    {
                        tier = 3;
                    }

                    smallDestScratch.Add((idx, tier, walkable));
                }

                byte maxAcceptedTier;
                if (nonStartWalkableA > 0 || (!isMovingPawnTarget && anyWalkableA > 0))
                {
                    maxAcceptedTier = 1;
                }
                else if (nonStartWalkableB > 0 || (!isMovingPawnTarget && anyWalkableB > 0))
                {
                    maxAcceptedTier = 2;
                }
                else
                {
                    maxAcceptedTier = 3;
                }

                bool startAccepted = false;
                int otherAcceptedWalkable = 0;
                for (int i = 0; i < smallDestScratch.Count; i++)
                {
                    (int idx, byte tier, bool walkable) entry = smallDestScratch[i];
                    bool accepted = entry.tier > 0 && entry.tier <= maxAcceptedTier;
                    if (!accepted)
                    {
                        excluded.Add(entry.idx);
                    }
                    else if (entry.idx == liveStartIdx)
                    {
                        startAccepted = true;
                    }
                    else if (entry.walkable)
                    {
                        otherAcceptedWalkable++;
                    }
                }

                if (isMovingPawnTarget && startAccepted && otherAcceptedWalkable > 0)
                {
                    excluded.Add(liveStartIdx);
                }
                return;
            }

            bool isLiveGiantTarget = hasTargetHitCells && target.Thing is Pawn tp && !tp.Dead && !tp.Downed;
            IReadOnlyList<HitCellEntry> targetHits = hasTargetHitCells ? targetComp.Footprint.hitCells : null;
            CellRect targetBaseRect = hasTargetHitCells
                ? targetComp.Footprint.tightHitBounds
                : target.Thing.OccupiedRect();

            bool isSmallIngestTarget = !(target.Thing is Pawn) && !hasTargetHitCells && IsGiantIngestJobTarget(attacker, target);
            bool useSmallPawnCanopyStandoff = !hasTargetHitCells
                && target.Thing is Pawn smallTargetPawn
                && !smallTargetPawn.Dead
                && GiantPartCombatRuntime.ShouldUseVisualCanopyPerimeterForTarget(attackerComp, target.Thing, null);
            if (!ShouldApplyGiantAttackerHullTouch(peMode, target.HasThing, hasTargetHitCells, isSmallIngestTarget))
            {
                return;
            }
            float fullMeleeRadius = attackerComp.GetEffectiveMeleeTouchRadius();
            float snugWalkRadius = isSmallIngestTarget
                ? ComputeSnugIngestWalkRadius(fullMeleeRadius, attackerComp.Footprint.maxCollisionHorizontalExtent)
                : fullMeleeRadius;
            int attackerExtent = useSmallPawnCanopyStandoff
                ? Mathf.Max(attackerComp.Footprint.maxCollisionHorizontalExtent, attackerComp.Footprint.maxPawnExclusionHorizontalExtent)
                : attackerComp.Footprint.maxCollisionHorizontalExtent;
            int expand = Mathf.Max(
                1,
                Mathf.CeilToInt(Mathf.Max(fullMeleeRadius, snugWalkRadius) + 1.15f) + attackerExtent);
            rect = targetBaseRect.ExpandedBy(expand).ClipInsideMap(map);
            excluded.Clear();

            IReadOnlyList<IntVec3> attackerOffsets = useSmallPawnCanopyStandoff
                ? attackerComp.Footprint.localPawnExclusionOffsets
                : attackerComp.Footprint.localCollisionOffsets;
            if (useSmallPawnCanopyStandoff)
            {
                GiantPartCombatUtility.PopulateRotatedHullBuckets(attackerOffsets);
            }
            float curAttackerHeading = attackerComp.CurrentHeadingDeg;
            Vector2 smallTargetCenterXZ = useSmallPawnCanopyStandoff
                ? new Vector2(targetBaseRect.minX + targetBaseRect.Width * 0.5f, targetBaseRect.minZ + targetBaseRect.Height * 0.5f)
                : Vector2.zero;
            bool checkOtherGiants = (attackerComp.RigDef == null || attackerComp.RigDef.enableGiantToGiantCollision) && mapComp != null;

            bool useTieredZeroCells = isSmallIngestTarget || useSmallPawnCanopyStandoff;
            List<int> tier1ZeroCells = useTieredZeroCells ? new List<int>(64) : null;
            List<int> tier2ZeroCells = useTieredZeroCells ? new List<int>(64) : null;
            List<int> zeroOverlapCells = new List<int>(rect.Area);
            List<int> fallbackValidCells = new List<int>(64);
            List<int> fallbackOverlapCounts = new List<int>(64);
            int minFallbackOverlap = int.MaxValue;
            bool hasWalkableTier1Zero = false;

            bool shouldExcludeStart = attacker != null
                && startCell.IsValid
                && startCell == attacker.Position
                && rect.Contains(startCell)
                && (isMovingPawnTarget
                    || !ReachabilityImmediate.CanReachImmediate(startCell, target, map, peMode, attacker));
            int giantStartIdx = shouldExcludeStart
                ? indices.CellToIndex(startCell)
                : -1;

            foreach (IntVec3 c in rect)
            {
                int idx = indices.CellToIndex(c);
                if (GiantDestructionUtility.HasThickRoofAt(map, c) && !c.Walkable(map))
                {
                    excluded.Add(idx);
                    continue;
                }

                if (checkOtherGiants
                    && isLiveGiantTarget
                    && GiantHullCollisionUtility.CountTranslatedHullOverlap(c, attackerOffsets, targetComp.Footprint.collisionCellSet) > 0)
                {
                    excluded.Add(idx);
                    continue;
                }

                bool inPreferredTier1 = false;
                bool inTouchRange;
                if (useSmallPawnCanopyStandoff)
                {
                    Vector2 candXZ = new Vector2(c.x + 0.5f, c.z + 0.5f);
                    Vector2 toSmall = smallTargetCenterXZ - candXZ;
                    float candBearing = toSmall.sqrMagnitude > 0.01f
                        ? SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(toSmall.x, toSmall.y) * Mathf.Rad2Deg)
                        : curAttackerHeading;
                    int octant = ((Mathf.RoundToInt(Mathf.DeltaAngle(curAttackerHeading, candBearing) / 45f) % 8) + 8) % 8;
                    IReadOnlyList<IntVec3> alignedCanopy = GiantPartCombatUtility.GetRotatedHullOffsetsBucket(attackerOffsets, octant);
                    EvaluateGiantSmallPawnCanopyStandoff(
                        c,
                        alignedCanopy,
                        fullMeleeRadius,
                        targetBaseRect,
                        out inTouchRange,
                        out bool overlapsAligned);
                    bool overlapsCurrent = GiantPartCombatUtility.ComputeMinTranslatedHullGapToRectSq(
                        c,
                        attackerOffsets,
                        targetBaseRect) <= 0.01f;
                    inPreferredTier1 = inTouchRange && !overlapsAligned && !overlapsCurrent;
                }
                else
                {
                    inPreferredTier1 = isSmallIngestTarget
                        && IsGiantHullInTouchRangeOfCells(c, null, snugWalkRadius, null, targetBaseRect);
                    inTouchRange = inPreferredTier1
                        || IsGiantHullInTouchRangeOfCells(c, attackerOffsets, fullMeleeRadius, targetHits, targetBaseRect, targetComp?.Footprint.hitCellSet);
                }
                if (!inTouchRange)
                {
                    excluded.Add(idx);
                    continue;
                }

                int otherOverlap = checkOtherGiants
                    ? mapComp.CountGiantHullOverlapAt(attackerComp, c, ignoreComp: targetComp)
                    : 0;
                if (otherOverlap == 0)
                {
                    zeroOverlapCells.Add(idx);
                    if (useTieredZeroCells)
                    {
                        if (inPreferredTier1)
                        {
                            tier1ZeroCells.Add(idx);
                            if (!hasWalkableTier1Zero && IsCellTraversableForGiantGoal(map, attacker, c))
                            {
                                hasWalkableTier1Zero = true;
                            }
                        }
                        else
                        {
                            tier2ZeroCells.Add(idx);
                        }
                    }
                }
                else
                {
                    fallbackValidCells.Add(idx);
                    fallbackOverlapCounts.Add(otherOverlap);
                    if (otherOverlap < minFallbackOverlap)
                    {
                        minFallbackOverlap = otherOverlap;
                    }
                }
            }

            if (hasWalkableTier1Zero)
            {
                for (int i = 0; i < fallbackValidCells.Count; i++)
                {
                    excluded.Add(fallbackValidCells[i]);
                }

                for (int i = 0; i < tier2ZeroCells.Count; i++)
                {
                    excluded.Add(tier2ZeroCells[i]);
                }

                if (giantStartIdx >= 0 && tier1ZeroCells.Contains(giantStartIdx))
                {
                    bool hasOtherWalkableTier1 = false;
                    for (int i = 0; i < tier1ZeroCells.Count; i++)
                    {
                        int otherIdx = tier1ZeroCells[i];
                        if (otherIdx != giantStartIdx
                            && IsCellTraversableForGiantGoal(map, attacker, indices.IndexToCell(otherIdx)))
                        {
                            hasOtherWalkableTier1 = true;
                            break;
                        }
                    }

                    if (hasOtherWalkableTier1)
                    {
                        excluded.Add(giantStartIdx);
                    }
                }
            }
            else if (zeroOverlapCells.Count > 0)
            {
                for (int i = 0; i < fallbackValidCells.Count; i++)
                {
                    excluded.Add(fallbackValidCells[i]);
                }

                if (giantStartIdx >= 0 && zeroOverlapCells.Contains(giantStartIdx))
                {
                    bool hasOtherWalkableZero = false;
                    for (int i = 0; i < zeroOverlapCells.Count; i++)
                    {
                        int otherIdx = zeroOverlapCells[i];
                        if (otherIdx != giantStartIdx
                            && IsCellTraversableForGiantGoal(map, attacker, indices.IndexToCell(otherIdx)))
                        {
                            hasOtherWalkableZero = true;
                            break;
                        }
                    }

                    if (hasOtherWalkableZero)
                    {
                        excluded.Add(giantStartIdx);
                    }
                }
            }
            else if (fallbackValidCells.Count > 0)
            {
                bool startInMinFallback = false;
                bool hasOtherWalkableMinFallback = false;
                for (int i = 0; i < fallbackValidCells.Count; i++)
                {
                    int idx = fallbackValidCells[i];
                    if (fallbackOverlapCounts[i] > minFallbackOverlap)
                    {
                        excluded.Add(idx);
                    }
                    else if (idx == giantStartIdx)
                    {
                        startInMinFallback = true;
                    }
                    else if (giantStartIdx >= 0
                        && IsCellTraversableForGiantGoal(map, attacker, indices.IndexToCell(idx)))
                    {
                        hasOtherWalkableMinFallback = true;
                    }
                }

                if (startInMinFallback && hasOtherWalkableMinFallback)
                {
                    excluded.Add(giantStartIdx);
                }
            }
        }

        private static bool IsCellTraversableForGiantGoal(Map map, Pawn attacker, IntVec3 cell)
        {
            if (map == null || attacker == null || !cell.InBounds(map))
            {
                return false;
            }
            return cell.WalkableBy(map, attacker)
                || GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, attacker, cell);
        }

        private static Vector2? TryExtractTargetWorldXZ(LocalTargetInfo targ)
        {
            if (!targ.IsValid)
            {
                return null;
            }
            Vector3 pos = targ.HasThing
                ? (targ.Thing is Pawn tp ? CompGiantRig.GetSafeDrawPos(tp) : targ.Thing.DrawPos)
                : targ.Cell.ToVector3Shifted();
            return new Vector2(pos.x, pos.z);
        }

        /// <summary>
        /// Suppresses vanilla "has no available melee attack" red console errors and melee verb selection
        /// on pure standoff vehicles (canMeleeAttack == false or rigs with zero melee tools/animations).
        /// </summary>
        /// <summary>
        /// Hard-locks all root Pawn native attack verbs on GCF giants so the base Pawn never shoots,
        /// never melees, and never enters vanilla FullBodyBusy combat stances.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.TryGetAttackVerb))]
        public static bool Pawn_TryGetAttackVerb_Prefix(Pawn __instance, ref Verb __result)
        {
            if (__instance != null && GiantFastLookup.TryGetRig(__instance) != null)
            {
                __result = null;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.TryStartAttack))]
        public static bool Pawn_TryStartAttack_Prefix(Pawn __instance, ref bool __result)
        {
            if (__instance != null && GiantFastLookup.TryGetRig(__instance) != null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryGetMeleeVerb))]
        public static bool Pawn_MeleeVerbs_TryGetMeleeVerb_Prefix(Pawn_MeleeVerbs __instance, ref Verb __result)
        {
            Pawn p = __instance?.Pawn;
            if (p != null && (GiantFastLookup.TryGetRig(p) != null || (GiantFastLookup.MightBeGiant(p) && p.kindDef?.canMeleeAttack == false)))
            {
                __result = null;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PawnAttackGizmoUtility), "ShouldUseMeleeAttackGizmo")]
        public static bool PawnAttackGizmoUtility_ShouldUseMeleeAttackGizmo_Prefix(Pawn pawn, ref bool __result)
        {
            if (pawn != null && (GiantFastLookup.TryGetRig(pawn) != null || (GiantFastLookup.MightBeGiant(pawn) && pawn.kindDef?.canMeleeAttack == false)))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget))]
        public static bool AttackTargetFinder_BestAttackTarget_Prefix(
            IAttackTargetSearcher searcher,
            TargetScanFlags flags,
            ref Predicate<Thing> validator,
            float minDist,
            float maxDist,
            IntVec3 locus,
            float maxTravelRadiusFromLocus,
            ref IAttackTarget __result)
        {
            if (searcher is Pawn sPawn && GiantFastLookup.TryGetRig(sPawn) != null)
            {
                __result = GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                    sPawn,
                    flags,
                    validator,
                    minDist,
                    maxDist,
                    locus,
                    maxTravelRadiusFromLocus);
                return false;
            }

            if (searcher is Pawn groundPawn && !GiantFlightUtility.CanVanillaPawnEngageAirborneTargets(groundPawn))
            {
                Predicate<Thing> innerValidator = validator;
                validator = t =>
                    !GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(groundPawn, t)
                    && (innerValidator == null || innerValidator(t));
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestShootTargetFromCurrentPosition))]
        public static bool AttackTargetFinder_BestShootTargetFromCurrentPosition_Prefix(
            IAttackTargetSearcher searcher,
            TargetScanFlags flags,
            Predicate<Thing> validator,
            float minDistance,
            float maxDistance,
            ref IAttackTarget __result)
        {
            if (searcher is Pawn sPawn && GiantFastLookup.TryGetRig(sPawn) is CompGiantRig rig)
            {
                float cap = Mathf.Min(maxDistance, Mathf.Max(4f, rig.PartCombat?.GetMaxOperationalRange() ?? 32f));
                __result = GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                    sPawn,
                    flags,
                    validator,
                    minDistance,
                    cap,
                    IntVec3.Invalid,
                    float.MaxValue);
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FoodUtility), "BestPawnToHuntForPredator")]
        public static bool FoodUtility_BestPawnToHuntForPredator_Prefix(
            Pawn predator,
            bool forceScanWholeMap,
            ref Pawn __result)
        {
            if (predator == null ||
                !(GiantFastLookup.TryGetRig(predator) is CompGiantRig rig) ||
                GiantVehicleBehaviorGuard.IsVehiclePawn(predator, out _, out _))
            {
                return true;
            }

            Map map = predator.Map;
            if (map == null ||
                !predator.Spawned ||
                rig.PartCombat == null ||
                !rig.PartCombat.GetTacticalRangeCapabilities(out bool hasRanged, out _, out _, out bool hasMelee, out _) ||
                (!hasMelee && !hasRanged))
            {
                __result = null;
                return false;
            }

            bool woundedOnlyDowned = (predator.health?.summaryHealth?.SummaryHealthPercent ?? 1f) < 0.25f;
            bool tutorialMode = TutorSystem.TutorialMode;
            IReadOnlyList<Pawn> allPawns = map.mapPawns.AllPawnsSpawned;

            Pawn bestPrey = null;
            float bestScore = 0f;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Pawn cand = allPawns[i];
                if (cand == null || cand == predator || !cand.Spawned || cand.Dead)
                {
                    continue;
                }
                if (woundedOnlyDowned && !cand.Downed)
                {
                    continue;
                }
                if (!FoodUtility.IsAcceptablePreyFor(predator, cand))
                {
                    continue;
                }
                if (cand.IsForbidden(predator) || (tutorialMode && cand.Faction == Faction.OfPlayerSilentFail))
                {
                    continue;
                }
                if (!predator.CanReach(cand, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }

                float score = FoodUtility.GetPreyScoreFor(predator, cand);
                if (bestPrey == null || score > bestScore)
                {
                    bestScore = score;
                    bestPrey = cand;
                }
            }

            __result = bestPrey;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "TryGiveJob")]
        public static bool JobGiver_AIFightEnemy_TryGiveJob_Prefix(
            JobGiver_AIFightEnemy __instance,
            Pawn pawn,
            ref Job __result)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) != null)
            {
                Job abilityJob = JobGiver_GiantAbilityFight.TryGiveAutonomousCombatAbilityJob(pawn);
                if (abilityJob != null)
                {
                    __result = abilityJob;
                    return false;
                }

                IntVec3 defendLocus = IntVec3.Invalid;
                float defendRadius = float.MaxValue;
                if (__instance is JobGiver_AIDefendPoint && pawn.mindState?.duty != null && pawn.mindState.duty.focus.IsValid)
                {
                    defendLocus = pawn.mindState.duty.focus.Cell;
                    defendRadius = pawn.mindState.duty.radius > 0f ? pawn.mindState.duty.radius : 28f;
                }
                else if (__instance is JobGiver_AIDefendMaster && pawn.playerSettings?.Master is Pawn master && (master.Spawned || master.CarriedBy != null))
                {
                    defendLocus = master.PositionHeld;
                    bool released = master.playerSettings != null
                        && master.playerSettings.animalsReleased
                        && (pawn.training?.HasLearned(TrainableDefOf.Release) ?? false);
                    defendRadius = released ? 55f : 18f;
                    if (pawn.mindState != null && pawn.mindState.enemyTarget == null &&
                        master.Spawned && !master.InMentalState &&
                        master.mindState?.meleeThreat is Thing mThreat &&
                        mThreat != pawn && !mThreat.Destroyed && mThreat.Spawned &&
                        master.mindState.MeleeThreatStillThreat)
                    {
                        pawn.mindState.enemyTarget = mThreat;
                    }
                }
                __result = GiantPartCombatUtility.TryGivePartCombatPositioningJob(pawn, 60f, 72f, defendLocus, defendRadius);
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(JobGiver_Manhunter), "TryGiveJob")]
        public static bool JobGiver_Manhunter_TryGiveJob_Prefix(Pawn pawn, ref Job __result)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) != null)
            {
                Job abilityJob = JobGiver_GiantAbilityFight.TryGiveAutonomousCombatAbilityJob(pawn);
                if (abilityJob != null)
                {
                    __result = abilityJob;
                    return false;
                }

                __result = GiantPartCombatUtility.TryGivePartCombatPositioningJob(pawn, 9999f, 9999f);
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(JobGiver_Berserk), "TryGiveJob")]
        public static bool JobGiver_Berserk_TryGiveJob_Prefix(Pawn pawn, ref Job __result)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) != null)
            {
                Job abilityJob = JobGiver_GiantAbilityFight.TryGiveAutonomousCombatAbilityJob(pawn);
                if (abilityJob != null)
                {
                    __result = abilityJob;
                    return false;
                }

                __result = GiantPartCombatUtility.TryGivePartCombatPositioningJob(pawn, 90f, 110f);
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(JobGiver_ReactToCloseMeleeThreat), "TryGiveJob")]
        public static bool JobGiver_ReactToCloseMeleeThreat_TryGiveJob_Prefix(Pawn pawn, ref Job __result)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) is CompGiantRig rig)
            {
                if (pawn.Faction != null && pawn.Faction.IsPlayer && pawn.Drafted)
                {
                    __result = null;
                    return false;
                }
                Thing threat = pawn.mindState?.meleeThreat ?? rig.TryGetActiveProvokedTarget();
                if (threat != null && !threat.Destroyed && threat.Spawned && threat.Map == pawn.Map)
                {
                    if (pawn.mindState != null && pawn.mindState.enemyTarget == null)
                    {
                        pawn.mindState.enemyTarget = threat;
                    }
                    __result = JobGiver_GiantAbilityFight.TryGiveAutonomousCombatAbilityJob(pawn)
                        ?? GiantPartCombatUtility.TryGivePartCombatPositioningJob(pawn, 52f, 68f);
                    return false;
                }
                __result = null;
                return false;
            }
            return true;
        }
        /// <summary>
        /// Triggers the Giant Creature's own skeletal melee attack animation matching the active Tool!
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb_MeleeAttack), "TryCastShot")]
        public static bool Verb_MeleeAttack_TryCastShot_Prefix(Verb_MeleeAttack __instance, ref bool __result)
        {
            if (GiantFastLookup.IsEmpty || __instance == null)
            {
                return true;
            }
            Pawn caster = __instance.CasterPawn;
            if (__instance.CurrentTarget.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, __instance.CurrentTarget.Thing))
            {
                __result = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Triggers configured skeletal ranged/ability animation when firing projectiles while allowing unmapped verbs to proceed normally.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verb_LaunchProjectile), nameof(Verb_LaunchProjectile.WarmupComplete))]
        public static bool Verb_LaunchProjectile_WarmupComplete_Prefix(Verb_LaunchProjectile __instance)
        {
            if (GiantFastLookup.TryGetRig(__instance?.CasterPawn) is CompGiantRig casterComp && casterComp.RigDef != null)
            {
                if (!(__instance is IAbilityVerb) && !casterComp.IsRangedVerbOperational(__instance))
                {
                    return false;
                }
                Vector2? targetXZ = TryExtractTargetWorldXZ(__instance.CurrentTarget);
                if (__instance is IAbilityVerb abilityVerb && abilityVerb.Ability?.def != null)
                {
                    casterComp.TriggerAbilityAnimation(abilityVerb.Ability, targetXZ);
                    return true;
                }
                casterComp.TriggerAttackAnimation(__instance.verbProps?.label, isRanged: true, targetXZ);
            }
            return true;
        }

        /// <summary>
        /// Triggers configured skeletal ability animation when activating abilities while allowing unmapped/pure-VFX abilities to proceed normally.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        [HarmonyPatch(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) })]
        public static bool Ability_Activate_Prefix(Ability __instance, [HarmonyArgument(0)] LocalTargetInfo target)
        {
            if (__instance == null)
            {
                return true;
            }
            var reqComp = __instance.CompOfType<CompAbilityEffect_RequiresGiantBone>();
            if (reqComp != null && !reqComp.AreBonesIntact())
            {
                return false;
            }
            if (GiantFastLookup.TryGetRig(__instance.pawn) is CompGiantRig casterComp && casterComp.RigDef != null)
            {
                if (!(__instance.verb is Verb_LaunchProjectile))
                {
                    casterComp.TriggerAbilityAnimation(__instance, TryExtractTargetWorldXZ(target));
                }
            }
            return true;
        }

        /// <summary>
        /// Disarms ranged projectile verbs whose rigged weapon bone (e.g. tail_stinger) has been destroyed,
        /// and disarms ability verbs whose required anatomical organ bones have been severed.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Normal)]
        [HarmonyPatch(typeof(Verb), nameof(Verb.Available))]
        public static void Verb_Available_Postfix(Verb __instance, ref bool __result)
        {
            if (!__result || GiantFastLookup.IsEmpty || __instance == null)
            {
                return;
            }
            Pawn caster = __instance.CasterPawn;
            if (__instance.IsMeleeAttack && __instance.CurrentTarget.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, __instance.CurrentTarget.Thing))
            {
                __result = false;
                return;
            }
            if (caster != null && GiantFastLookup.TryGetCockpit(caster) is CompGiantCockpit cockpit)
            {
                if (cockpit.IsDormantUnmanned ||
                    (__instance is IAbilityVerb ? !cockpit.CanActivateAbilities : !cockpit.CanFirePrimaryWeapons))
                {
                    __result = false;
                    return;
                }
            }
            if (__instance is IAbilityVerb abilityVerb && abilityVerb.Ability != null)
            {
                var reqComp = abilityVerb.Ability.CompOfType<CompAbilityEffect_RequiresGiantBone>();
                if (reqComp != null && !reqComp.AreBonesIntact())
                {
                    __result = false;
                }
                return;
            }
            if (__instance is Verb_LaunchProjectile &&
                GiantFastLookup.TryGetRig(caster) is CompGiantRig comp &&
                !comp.IsRangedVerbOperational(__instance))
            {
                __result = false;
            }
        }

        public static bool IsCellTouchingFootprint(IntVec3 cell, GiantFootprintData fp)
        {
            if (fp.hitCellSet.Contains(cell))
            {
                return true;
            }
            for (int i = 0; i < 8; i++)
            {
                if (fp.hitCellSet.Contains(cell + GenAdj.AdjacentCells[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static class SimpleGiantSetPool
        {
            [ThreadStatic]
            private static HashSet<CompGiantRig> cached;

            public static HashSet<CompGiantRig> Get()
            {
                var set = cached ??= new HashSet<CompGiantRig>();
                set.Clear();
                return set;
            }

            public static void Return(HashSet<CompGiantRig> set)
            {
                set?.Clear();
            }
        }
    }

    [HarmonyPatch]
    public static class HarmonyPatch_JobDriver_AttackStatic_Tick
    {
        private static readonly MethodInfo LocalTargetInfoGetCellMethod =
            AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Cell));

        private static readonly FieldInfo JobDriverPawnField =
            AccessTools.Field(typeof(JobDriver), nameof(JobDriver.pawn));

        private static readonly MethodInfo ResolveAttackStaticTargetCellRefMethod =
            AccessTools.Method(typeof(HarmonyPatches_CombatAndHitbox), nameof(HarmonyPatches_CombatAndHitbox.ResolveAttackStaticTargetCellRef));

        private static MethodBase cachedTarget;
        private static FieldInfo cachedThisField;

        public static bool Prepare()
        {
            cachedTarget = null;
            cachedThisField = null;

            Type[] nested = typeof(JobDriver_AttackStatic).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < nested.Length; i++)
            {
                Type t = nested[i];
                FieldInfo thisField = AccessTools.Field(t, "<>4__this");
                if (thisField == null)
                {
                    continue;
                }

                MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (!method.Name.StartsWith("<MakeNewToils>b__", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    ParameterInfo[] ps = method.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(int))
                    {
                        continue;
                    }
                    cachedTarget = method;
                    cachedThisField = thisField;
                    return true;
                }
            }

            return false;
        }

        public static MethodBase TargetMethod()
        {
            return cachedTarget;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.Calls(LocalTargetInfoGetCellMethod) && cachedThisField != null)
                {
                    yield return new CodeInstruction(OpCodes.Ldloc_2);
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, cachedThisField);
                    yield return new CodeInstruction(OpCodes.Ldfld, JobDriverPawnField);
                    yield return new CodeInstruction(OpCodes.Call, ResolveAttackStaticTargetCellRefMethod);
                }
                else
                {
                    yield return inst;
                }
            }
        }
    }
}
