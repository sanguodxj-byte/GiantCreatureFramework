using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public class CompProperties_AbilityRequiresGiantBone : CompProperties_AbilityEffect
    {
        public List<string> requiredBoneIds = new List<string>();
        public bool requireAll = false;
        public string organLabel;
        public string organLabelKey;
        public string disabledReasonKey = "GCF_AbilityDisabledMissingOrgan";

        public CompProperties_AbilityRequiresGiantBone()
        {
            compClass = typeof(CompAbilityEffect_RequiresGiantBone);
        }
    }

    public class CompAbilityEffect_RequiresGiantBone : CompAbilityEffect
    {
        public new CompProperties_AbilityRequiresGiantBone Props => (CompProperties_AbilityRequiresGiantBone)props;

        public bool AreBonesIntact()
        {
            Pawn pawn = parent?.pawn;
            if (pawn == null || Props.requiredBoneIds == null || Props.requiredBoneIds.Count == 0)
            {
                return true;
            }
            CompGiantRig rig = pawn.TryGetComp<CompGiantRig>();
            if (rig == null)
            {
                return true;
            }
            return rig.AreRequiredBonesIntact(Props.requiredBoneIds, Props.requireAll);
        }

        public string GetOrganDisplayLabel()
        {
            if (!string.IsNullOrEmpty(Props.organLabelKey) && Props.organLabelKey.CanTranslate())
            {
                return Props.organLabelKey.Translate();
            }
            if (!string.IsNullOrEmpty(Props.organLabel))
            {
                return Props.organLabel.CanTranslate()
                    ? Props.organLabel.Translate().ToString()
                    : Props.organLabel;
            }
            Pawn pawn = parent?.pawn;
            CompGiantRig rig = pawn?.TryGetComp<CompGiantRig>();
            if (rig?.RigDef != null && Props.requiredBoneIds != null && Props.requiredBoneIds.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < Props.requiredBoneIds.Count; i++)
                {
                    if (rig.RigDef.TryGetNodeIndex(Props.requiredBoneIds[i], out int idx))
                    {
                        BodyPartRecord part = rig.GetLinkedBodyPartRaw(idx);
                        if (part != null)
                        {
                            names.Add(part.LabelCap);
                        }
                        else
                        {
                            BoneNodeDef node = rig.RigDef.SortedNodes[idx];
                            names.Add(!string.IsNullOrEmpty(node.label) ? node.label : node.nodeId);
                        }
                    }
                    else
                    {
                        names.Add(Props.requiredBoneIds[i]);
                    }
                }
                if (names.Count > 0)
                {
                    return string.Join(" / ", names);
                }
            }
            return "organ";
        }

        public override bool GizmoDisabled(out string reason)
        {
            if (!AreBonesIntact())
            {
                string key = string.IsNullOrEmpty(Props.disabledReasonKey)
                    ? "GCF_AbilityDisabledMissingOrgan"
                    : Props.disabledReasonKey;
                string organ = GetOrganDisplayLabel();
                reason = key.CanTranslate() ? string.Format(key.Translate(), organ) : $"Disabled: missing {organ}";
                return true;
            }
            return base.GizmoDisabled(out reason);
        }

        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            return AreBonesIntact();
        }
    }

    public class CompProperties_AbilityAbyssalQuake : CompProperties_AbilityEffect
    {
        public float radius = 4.4f;
        public int damageAmount = 38;
        public float armorPenetration = 0.55f;
        public int stunTicks = 150;
        public DamageDef damageDef;
        public SoundDef explosionSound;

        // Declarative multi-unit reuse fields (soft-whip crossing detonation, turret recoil salvo, or track blitz)
        public bool triggerAtDualWhipCrossing = false;
        public string dualWhipRequiredClipDefName;
        public string muzzleBoneId;
        public Vector2 muzzlePixel = new Vector2(256f, 24f);
        public bool triggerTurretRecoil = false;
        public float turretRecoilScale = 1.15f;
        public string trackLeftBoneId;
        public string trackRightBoneId;
        public Color vfxColor = new Color(1.00f, 0.68f, 0.22f, 0.98f);

        public CompProperties_AbilityAbyssalQuake()
        {
            compClass = typeof(CompAbilityEffect_AbyssalQuake);
        }
    }

    public class CompAbilityEffect_AbyssalQuake : CompAbilityEffect
    {
        private struct PendingDualWhipCrossing
        {
            public Pawn Caster;
            public CompGiantRig Rig;
            public Map Map;
            public CompProperties_AbilityAbyssalQuake Props;
            public int TicksRemaining;
        }

        private static readonly List<PendingDualWhipCrossing> PendingCrossings = new List<PendingDualWhipCrossing>(8);

        public new CompProperties_AbilityAbyssalQuake Props => (CompProperties_AbilityAbyssalQuake)props;

        public static bool HasPendingCrossingStrike(Pawn caster)
        {
            if (caster == null || PendingCrossings.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < PendingCrossings.Count; i++)
            {
                if (PendingCrossings[i].Caster == caster)
                {
                    return true;
                }
            }
            return false;
        }

        public static void ClearAllPendingCrossings()
        {
            PendingCrossings.Clear();
        }

        public static void ClearPendingCrossingsForMap(Map map)
        {
            if (map == null || PendingCrossings.Count == 0)
            {
                return;
            }
            for (int i = PendingCrossings.Count - 1; i >= 0; i--)
            {
                if (PendingCrossings[i].Map == map)
                {
                    PendingCrossings.RemoveAt(i);
                }
            }
        }

        public static void TickPendingCrossingDetonations(Map map)
        {
            if (map == null || PendingCrossings.Count == 0)
            {
                return;
            }

            for (int i = PendingCrossings.Count - 1; i >= 0; i--)
            {
                PendingDualWhipCrossing entry = PendingCrossings[i];
                if (entry.Map != map)
                {
                    continue;
                }

                Pawn caster = entry.Caster;
                CompGiantRig rig = entry.Rig;
                string requiredClip = entry.Props?.dualWhipRequiredClipDefName;
                if (caster == null || caster.Dead || caster.Downed || !caster.Spawned || caster.Map != map
                    || rig == null || (!string.IsNullOrEmpty(requiredClip) && rig.ActiveOneShotClipDefName != requiredClip))
                {
                    PendingCrossings.RemoveAt(i);
                    continue;
                }

                entry.TicksRemaining--;
                if (entry.TicksRemaining <= 0)
                {
                    PendingCrossings.RemoveAt(i);
                    continue;
                }

                // Wait for forward whip snap phase and trigger strictly once at the exact crossing instant & coordinate.
                if (rig.ActiveOneShotElapsedTicks >= 16f
                    && GiantVfxSystem.TryDetectDualWhipStrikeCrossing(rig, out Vector3 crossWorldPos, out _))
                {
                    PendingCrossings.RemoveAt(i);
                    ExecuteDualWhipCrossingDetonation(caster, rig, map, crossWorldPos, entry.Props);
                    continue;
                }

                PendingCrossings[i] = entry;
            }
        }

        private static void ExecuteDualWhipCrossingDetonation(
            Pawn caster,
            CompGiantRig rig,
            Map map,
            Vector3 crossWorldPos,
            CompProperties_AbilityAbyssalQuake quakeProps)
        {
            if (caster == null || map == null || quakeProps == null)
            {
                return;
            }

            IntVec3 crossCell = crossWorldPos.ToIntVec3();
            if (!crossCell.InBounds(map))
            {
                return;
            }

            if (GiantVfxSystem.IsRigVfxEnabled(caster, rig))
            {
                GiantVfxSystem.SpawnDualWhipConvergenceExplosion(
                    caster,
                    rig,
                    scale: Mathf.Clamp(quakeProps.radius / 3.5f, 0.95f, 1.35f),
                    verifiedCrossingPos: crossWorldPos);
            }

            ApplyQuakeExplosionAndStagger(caster, rig, map, crossCell, quakeProps);
        }

        private static void ApplyQuakeExplosionAndStagger(
            Pawn caster,
            CompGiantRig rig,
            Map map,
            IntVec3 center,
            CompProperties_AbilityAbyssalQuake quakeProps)
        {
            DamageDef resolvedDamDef = quakeProps.damageDef ?? DamageDefOf.Blunt;
            SoundDef resolvedExplosionSound = quakeProps.explosionSound
                ?? resolvedDamDef?.soundExplosion
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Thump")
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Bomb");

            GenExplosion.DoExplosion(
                center: center,
                map: map,
                radius: quakeProps.radius,
                damType: resolvedDamDef,
                instigator: caster,
                damAmount: quakeProps.damageAmount,
                armorPenetration: quakeProps.armorPenetration,
                explosionSound: resolvedExplosionSound,
                ignoredThings: new List<Thing> { caster },
                doVisualEffects: !GiantVfxSystem.IsRigVfxEnabled(caster, rig),
                doSoundEffects: resolvedExplosionSound != null,
                screenShakeFactor: 1.25f);

            foreach (Thing t in GenRadial.RadialDistinctThingsAround(center, map, quakeProps.radius, useCenter: true))
            {
                if (t is Pawn victim && victim != caster && !victim.Dead && victim.TryGetComp<CompGiantRig>() == null)
                {
                    if (quakeProps.stunTicks > 0)
                    {
                        victim.stances?.stunner?.StunFor(quakeProps.stunTicks, caster, addBattleLog: false);
                    }
                    victim.stances?.stagger?.StaggerFor(180);
                }
            }
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent?.pawn;
            Map map = caster?.Map;
            if (map == null || !target.IsValid)
            {
                return;
            }

            IntVec3 center = target.Cell;
            if (!center.InBounds(map))
            {
                return;
            }

            Vector3 targetPos = center.ToVector3Shifted();
            CompGiantRig rig = caster.TryGetComp<CompGiantRig>();
            string abilityDefName = parent?.def?.defName ?? string.Empty;
            if (rig != null && !string.IsNullOrEmpty(abilityDefName))
            {
                rig.TriggerAbilityAnimation(abilityDefName, new Vector2(targetPos.x, targetPos.z));
            }

            // Dual-whip crossing strike (reusable via <triggerAtDualWhipCrossing>true</triggerAtDualWhipCrossing>):
            bool isWhipCrossing = Props.triggerAtDualWhipCrossing;
            if (isWhipCrossing)
            {
                for (int i = PendingCrossings.Count - 1; i >= 0; i--)
                {
                    if (PendingCrossings[i].Caster == caster)
                    {
                        PendingCrossings.RemoveAt(i);
                    }
                }

                string expectedClip = !string.IsNullOrEmpty(Props.dualWhipRequiredClipDefName)
                    ? Props.dualWhipRequiredClipDefName
                    : rig?.ActiveOneShotClipDefName;
                if (rig != null && (string.IsNullOrEmpty(expectedClip) || rig.ActiveOneShotClipDefName == expectedClip))
                {
                    if (rig.ActiveOneShotElapsedTicks >= 16f
                        && GiantVfxSystem.TryDetectDualWhipStrikeCrossing(rig, out Vector3 immediateCrossPos, out _))
                    {
                        ExecuteDualWhipCrossingDetonation(caster, rig, map, immediateCrossPos, Props);
                    }
                    else
                    {
                        PendingCrossings.Add(new PendingDualWhipCrossing
                        {
                            Caster = caster,
                            Rig = rig,
                            Map = map,
                            Props = Props,
                            TicksRemaining = 50
                        });
                    }
                }
                return;
            }

            Vector3 casterPos = CompGiantRig.GetSafeDrawPos(caster);
            Vector3 toTarget = targetPos - casterPos;
            toTarget.y = 0f;
            float bearingDeg = toTarget.sqrMagnitude > 0.01f
                ? Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg
                : (rig?.CurrentHeadingDeg ?? caster.Rotation.AsAngle);

            string resolvedMuzzleBone = !string.IsNullOrEmpty(Props.muzzleBoneId)
                ? Props.muzzleBoneId
                : (abilityDefName == "GCF_Ability_PanzerSprgrSalvo" ? "main_gun" : null);
            bool doRecoil = Props.triggerTurretRecoil || abilityDefName == "GCF_Ability_PanzerSprgrSalvo";
            if (doRecoil && !string.IsNullOrEmpty(resolvedMuzzleBone))
            {
                float recoilAng = bearingDeg;
                if (rig != null && rig.TryGetBonePixelWorldPos(resolvedMuzzleBone, Props.muzzlePixel, out Vector2 rMuzzleXZ, out float rMuzzleAng))
                {
                    recoilAng = rMuzzleAng;
                    Vector3 rToTarget = targetPos - new Vector3(rMuzzleXZ.x, casterPos.y, rMuzzleXZ.y);
                    rToTarget.y = 0f;
                    if (rToTarget.sqrMagnitude > 0.04f)
                    {
                        float rTargetDeg = Mathf.Atan2(rToTarget.x, rToTarget.z) * Mathf.Rad2Deg;
                        if (Mathf.Abs(Mathf.DeltaAngle(rMuzzleAng, rTargetDeg)) <= 55f)
                        {
                            recoilAng = rTargetDeg;
                        }
                    }
                }
                rig?.TriggerTurretRecoilByBoneId(resolvedMuzzleBone, recoilAng, Props.turretRecoilScale, publishImmediate: true);
            }

            string resolvedTrackLeft = Props.trackLeftBoneId;
            string resolvedTrackRight = Props.trackRightBoneId;

            if (GiantVfxSystem.IsRigVfxEnabled(caster, rig))
            {
                if (!string.IsNullOrEmpty(resolvedMuzzleBone))
                {
                    Color heAmber = Props.vfxColor;
                    Vector3 muzzlePos = casterPos;
                    if (rig != null && rig.TryGetBonePixelWorldPos(resolvedMuzzleBone, Props.muzzlePixel, out Vector2 mXZ, out float mAng))
                    {
                        muzzlePos = new Vector3(mXZ.x, casterPos.y, mXZ.y);
                        GiantVfxSystem.SpawnMuzzleBlastVfx(caster, rig, muzzlePos, mAng, 1.35f, heAmber, GiantAnimVfxPreset.MuzzleBlast);
                    }
                    GiantVfxSystem.SpawnOrdnanceTrailBeam(map, muzzlePos, targetPos, 1.10f, durationTicks: 13, baseColor: heAmber);
                    GiantVfxSystem.SpawnTectonicRift(map, targetPos, Props.radius * 0.95f, heAmber, durationTicks: 38, spokes: 11, includeShockwave: true);
                    GiantVfxSystem.SpawnDetonationNova(map, targetPos, Props.radius * 0.78f, durationTicks: 26, baseColor: heAmber);
                    GiantVfxSystem.SpawnVolumetricSmokeCluster(map, targetPos, Props.radius * 0.62f, durationTicks: 42, new Color(0.15f, 0.14f, 0.13f, 1f), new Color(0.76f, 0.65f, 0.48f, 1f), alpha: 0.84f, delayTicks: 3);
                }
                else if (!string.IsNullOrEmpty(resolvedTrackLeft) || !string.IsNullOrEmpty(resolvedTrackRight))
                {
                    Color blitzCol = Props.vfxColor;
                    Vector3 trackOrigin = casterPos;
                    if (rig != null)
                    {
                        Vector2 lXZ = Vector2.zero;
                        Vector2 rXZ = Vector2.zero;
                        bool hasLeft = !string.IsNullOrEmpty(resolvedTrackLeft) && rig.TryGetBonePixelWorldPos(resolvedTrackLeft, new Vector2(256f, 140f), out lXZ, out _);
                        bool hasRight = !string.IsNullOrEmpty(resolvedTrackRight) && rig.TryGetBonePixelWorldPos(resolvedTrackRight, new Vector2(256f, 140f), out rXZ, out _);
                        if (hasLeft && hasRight)
                        {
                            trackOrigin = new Vector3((lXZ.x + rXZ.x) * 0.5f, casterPos.y, (lXZ.y + rXZ.y) * 0.5f);
                        }
                        else if (hasLeft)
                        {
                            trackOrigin = new Vector3(lXZ.x, casterPos.y, lXZ.y);
                        }
                        else if (hasRight)
                        {
                            trackOrigin = new Vector3(rXZ.x, casterPos.y, rXZ.y);
                        }
                    }
                    GiantVfxSystem.SpawnOrdnanceTrailBeam(map, trackOrigin, targetPos, 1.65f, durationTicks: 16, baseColor: blitzCol);
                    GiantVfxSystem.SpawnTectonicRift(map, targetPos, Props.radius * 0.95f, blitzCol, durationTicks: 34, spokes: 10, includeShockwave: true);
                    GiantVfxSystem.SpawnImpactSparkFan(map, targetPos, bearingDeg, 1.45f, durationTicks: 16, baseColor: blitzCol, deflected: false);
                    GiantVfxSystem.SpawnVolumetricSmokeCluster(map, targetPos, Props.radius * 0.58f, durationTicks: 34, new Color(0.24f, 0.21f, 0.18f, 1f), new Color(0.74f, 0.66f, 0.54f, 1f), alpha: 0.80f);
                }
            }

            ApplyQuakeExplosionAndStagger(caster, rig, map, center, Props);
        }

        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            if (target.IsValid)
            {
                GenDraw.DrawFieldEdges(GenRadial.RadialCellsAround(target.Cell, Props.radius, useCenter: true).ToList());
            }
        }

        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            Pawn caster = parent?.pawn;
            if (caster == null || !target.HasThing || !(target.Thing is Pawn victim))
            {
                return false;
            }
            return !victim.Dead && !victim.Downed && victim.HostileTo(caster);
        }
    }

    public class CompProperties_GiantBioferriteCore : CompProperties
    {
        public string requiredBoneId;
        public List<string> requiredBoneIds = new List<string>();
        public float healPerInterval = 16f;
        public int intervalTicks = 60;

        public CompProperties_GiantBioferriteCore()
        {
            compClass = typeof(CompGiantBioferriteCore);
        }
    }

    public class CompGiantBioferriteCore : ThingComp
    {
        public CompProperties_GiantBioferriteCore Props => (CompProperties_GiantBioferriteCore)props;
        public Pawn Pawn => parent as Pawn;

        public float GetCorePowerFactor()
        {
            Pawn pawn = Pawn;
            if (pawn == null)
            {
                return 0f;
            }
            CompGiantRig rig = pawn.TryGetComp<CompGiantRig>();
            if (rig == null)
            {
                return 1f;
            }

            if (Props.requiredBoneIds != null && Props.requiredBoneIds.Count > 0)
            {
                int intact = 0;
                for (int i = 0; i < Props.requiredBoneIds.Count; i++)
                {
                    if (rig.IsBoneIntact(Props.requiredBoneIds[i]))
                    {
                        intact++;
                    }
                }
                return intact / (float)Props.requiredBoneIds.Count;
            }

            if (string.IsNullOrEmpty(Props.requiredBoneId))
            {
                return 1f;
            }
            return rig.IsBoneIntact(Props.requiredBoneId) ? 1f : 0f;
        }

        public bool IsCoreIntact() => GetCorePowerFactor() > 0f;

        public override void CompTick()
        {
            base.CompTick();
            Pawn pawn = Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }

            int interval = Mathf.Max(10, Props.intervalTicks);
            if (!pawn.IsHashIntervalTick(interval))
            {
                return;
            }

            float factor = GetCorePowerFactor();
            if (factor <= 0f)
            {
                return;
            }

            List<Hediff> hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs == null || hediffs.Count == 0)
            {
                return;
            }

            float budget = Props.healPerInterval * factor;
            for (int i = hediffs.Count - 1; i >= 0 && budget > 0.01f; i--)
            {
                if (hediffs[i] is Hediff_Injury injury && !injury.IsPermanent())
                {
                    float amount = Mathf.Min(budget, injury.Severity);
                    injury.Heal(amount);
                    budget -= amount;
                }
            }
        }

        public override string CompInspectStringExtra()
        {
            Pawn pawn = Pawn;
            if (pawn == null || pawn.Dead)
            {
                return null;
            }

            var sb = new StringBuilder();
            float factor = GetCorePowerFactor();
            float perSec = Props.healPerInterval * factor * (60f / Mathf.Max(1, Props.intervalTicks));
            if (factor > 0f)
            {
                string line = "GCF_BioferriteCoreActive".CanTranslate()
                    ? string.Format("GCF_BioferriteCoreActive".Translate(), perSec.ToString("F0"))
                    : $"Bioferrite core: Regenerating (+{perSec:F0} HP/s)";
                sb.Append(line);
            }
            else
            {
                string line = "GCF_BioferriteCoreSevered".CanTranslate()
                    ? "GCF_BioferriteCoreSevered".Translate().ToString()
                    : "Bioferrite core: Offline (organ severed)";
                sb.Append(line);
            }

            if (pawn.abilities?.abilities != null && pawn.abilities.abilities.Count > 0)
            {
                var offlineSkills = new List<string>();
                for (int i = 0; i < pawn.abilities.abilities.Count; i++)
                {
                    Ability ab = pawn.abilities.abilities[i];
                    var req = ab?.CompOfType<CompAbilityEffect_RequiresGiantBone>();
                    if (req != null && !req.AreBonesIntact())
                    {
                        offlineSkills.Add(ab.def.LabelCap);
                    }
                }
                if (offlineSkills.Count > 0)
                {
                    sb.AppendLine();
                    string joined = string.Join(", ", offlineSkills);
                    string disabledLine = "GCF_DisabledSkillsLine".CanTranslate()
                        ? string.Format("GCF_DisabledSkillsLine".Translate(), joined)
                        : $"Disarmed skills: {joined}";
                    sb.Append(disabledLine);
                }
            }

            return sb.ToString();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

        }
    }

    public class JobGiver_GiantAbilityFight : ThinkNode_JobGiver
    {
        public List<AbilityDef> abilityDefs;
        public float maxAcquireRadius = 42f;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            var copy = (JobGiver_GiantAbilityFight)base.DeepCopy(resolve);
            copy.maxAcquireRadius = maxAcquireRadius;
            if (abilityDefs != null)
            {
                copy.abilityDefs = new List<AbilityDef>(abilityDefs);
            }
            return copy;
        }

        public static float GetMaxOffensiveAbilityRange(Pawn pawn)
        {
            if (pawn?.abilities?.abilities == null)
            {
                return 0f;
            }
            float maxR = 0f;
            for (int i = 0; i < pawn.abilities.abilities.Count; i++)
            {
                Ability ab = pawn.abilities.abilities[i];
                if (ab?.def == null || !ab.def.aiCanUse || (!ab.def.hostile && !ab.def.ai_IsOffensive))
                {
                    continue;
                }
                float r = ab.def.verbProperties?.range ?? 0f;
                if (r > maxR)
                {
                    maxR = r;
                }
            }
            return maxR;
        }

        public static Job TryGiveAutonomousCombatAbilityJob(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Downed || pawn.Dead || pawn.abilities?.abilities == null || pawn.abilities.abilities.Count == 0)
            {
                return null;
            }
            if (pawn.Faction == Faction.OfPlayerSilentFail && pawn.Drafted)
            {
                return null;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig spinRig &&
                spinRig.PartCombat != null &&
                spinRig.PartCombat.HasActiveFullHullSpinClip())
            {
                return null;
            }

            Job customJob = GiantAutonomousAbilityRegistry.TryGiveJob(pawn);
            if (customJob != null)
            {
                return customJob;
            }

            float acquireRadius = Mathf.Max(42f, GetMaxOffensiveAbilityRange(pawn) + 4f);
            Thing target = ResolveAbilityTarget(pawn, acquireRadius);
            if (target == null || !target.Spawned || target.Map != pawn.Map)
            {
                return null;
            }

            LocalTargetInfo lti = new LocalTargetInfo(target);
            float dist = pawn.Position.DistanceTo(target.Position);
            for (int i = 0; i < pawn.abilities.abilities.Count; i++)
            {
                Ability ab = pawn.abilities.abilities[i];
                if (ab?.def == null || !ab.def.aiCanUse || (!ab.def.hostile && !ab.def.ai_IsOffensive))
                {
                    continue;
                }
                Job job = TryBuildCastJob(pawn, ab, lti, dist);
                if (job != null)
                {
                    return job;
                }
            }

            return null;
        }

        private static Thing ResolveAbilityTarget(Pawn pawn, float radius)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null)
            {
                return null;
            }
            Thing target = pawn.mindState?.enemyTarget;
            if (target == null || target.Destroyed || !target.Spawned || target.Map != pawn.Map ||
                (target is Pawn tp && (tp.Dead || tp.Downed)) ||
                !GiantPartCombatRuntime.IsValidHostileThing(pawn, target) ||
                pawn.Position.DistanceTo(target.Position) > radius)
            {
                IAttackTarget found = GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                    pawn,
                    TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable,
                    validator: null,
                    minDist: 0f,
                    maxDist: radius,
                    locus: IntVec3.Invalid,
                    maxTravelRadiusFromLocus: float.MaxValue);
                target = found?.Thing;
            }
            if (target != null && (target.Destroyed || !target.Spawned || target.Map != pawn.Map ||
                !GiantPartCombatRuntime.IsValidHostileThing(pawn, target)))
            {
                return null;
            }
            return target;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || pawn.Downed || pawn.Dead || pawn.abilities?.abilities == null || pawn.abilities.abilities.Count == 0)
            {
                return null;
            }
            if (pawn.Faction == Faction.OfPlayerSilentFail && pawn.Drafted)
            {
                return null;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig spinRig &&
                spinRig.PartCombat != null &&
                spinRig.PartCombat.HasActiveFullHullSpinClip())
            {
                return null;
            }

            float effectiveAcquire = Mathf.Max(maxAcquireRadius, GetMaxOffensiveAbilityRange(pawn) + 4f);
            Thing target = ResolveAbilityTarget(pawn, effectiveAcquire);
            if (target == null || !target.Spawned || target.Map != pawn.Map)
            {
                return null;
            }

            LocalTargetInfo lti = new LocalTargetInfo(target);
            float dist = pawn.Position.DistanceTo(target.Position);

            if (abilityDefs != null && abilityDefs.Count > 0)
            {
                for (int i = 0; i < abilityDefs.Count; i++)
                {
                    Ability ab = pawn.abilities.GetAbility(abilityDefs[i]);
                    Job job = TryBuildCastJob(pawn, ab, lti, dist);
                    if (job != null)
                    {
                        return job;
                    }
                }
            }
            else
            {
                for (int i = 0; i < pawn.abilities.abilities.Count; i++)
                {
                    Ability ab = pawn.abilities.abilities[i];
                    Job job = TryBuildCastJob(pawn, ab, lti, dist);
                    if (job != null)
                    {
                        return job;
                    }
                }
            }

            return null;
        }

        public static bool IsDisabledByEffectComps(Ability ab)
        {
            if (ab?.EffectComps == null)
            {
                return false;
            }
            for (int i = 0; i < ab.EffectComps.Count; i++)
            {
                if (ab.EffectComps[i] != null && ab.EffectComps[i].GizmoDisabled(out _))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsDualWhipCrossingAbility(Ability ab)
        {
            if (ab?.def == null)
            {
                return false;
            }
            if (ab.def.comps != null)
            {
                for (int i = 0; i < ab.def.comps.Count; i++)
                {
                    if (ab.def.comps[i] is CompProperties_AbilityAbyssalQuake qp && qp.triggerAtDualWhipCrossing)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static Job TryBuildCastJob(Pawn pawn, Ability ab, LocalTargetInfo target, float dist)
        {
            if (ab == null || ab.verb == null || !ab.CanCast || IsDisabledByEffectComps(ab))
            {
                return null;
            }

            if (ab.def != null && !ab.def.targetRequired)
            {
                target = new LocalTargetInfo(pawn);
                dist = 0f;
            }
            else
            {
                // Ground-targeted skills (e.g. Rathalos FireballBreath / FireballLiftoff) must lock onto the
                // target's map cell rather than tracking a moving Pawn/Thing during cast warmup.
                TargetingParameters tpParams = ab.verb.verbProps?.targetParams;
                if (tpParams != null
                    && tpParams.canTargetLocations
                    && !tpParams.canTargetPawns
                    && !tpParams.canTargetBuildings
                    && target.HasThing)
                {
                    target = new LocalTargetInfo(target.Cell);
                }
            }

            if (!ab.AICanTargetNow(target))
            {
                return null;
            }

            if (!ab.CanApplyOn(target))
            {
                return null;
            }
            if (ab.EffectComps != null)
            {
                for (int i = 0; i < ab.EffectComps.Count; i++)
                {
                    CompAbilityEffect ec = ab.EffectComps[i];
                    if (ec != null && !ec.Valid(target, throwMessages: false))
                    {
                        return null;
                    }
                }
            }

            if (pawn.TryGetComp<CompGiantRig>() is CompGiantRig rigComp && rigComp.RigDef != null)
            {
                if (!(pawn.Faction == Faction.OfPlayerSilentFail && pawn.Drafted) &&
                    rigComp.PartCombat != null &&
                    rigComp.PartCombat.HasActiveFullHullSpinClip())
                {
                    return null;
                }
                if (target.IsValid && target.Cell != pawn.Position)
                {
                    Vector3 simPos = CompGiantRig.GetSimTickPos(pawn);
                    Vector3 targPos = target.HasThing
                        ? (target.Thing is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : target.Thing.DrawPos)
                        : target.Cell.ToVector3Shifted();
                    Vector3 dir = targPos - simPos;
                    dir.y = 0f;
                    if (dir.x * dir.x + dir.z * dir.z > 0.25f)
                    {
                        float bearingDeg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                        float maxAzimuthDeg = IsDualWhipCrossingAbility(ab) ? 24f : 95f;
                        if (Mathf.Abs(Mathf.DeltaAngle(rigComp.CurrentHeadingDeg, bearingDeg)) > maxAzimuthDeg)
                        {
                            return null;
                        }
                    }
                }
            }

            VerbProperties vp = ab.verb.verbProps;
            if (vp != null)
            {
                if (dist < vp.minRange || dist > vp.range)
                {
                    return null;
                }
            }

            if (!ab.verb.CanHitTargetFrom(pawn.Position, target))
            {
                return null;
            }

            return ab.GetJob(target, target);
        }
    }

    public class Verb_CastGiantAbility : Verb_CastAbility
    {
        public override void DrawHighlight(LocalTargetInfo target)
        {
            base.DrawHighlight(target);
            if (caster != null && caster.Spawned && caster.Map != null && verbProps != null && !verbProps.IsMeleeAttack)
            {
                float r = EffectiveRange;
                if (r >= GenRadial.MaxRadialPatternRadius - 0.1f)
                {
                    DrawLargeRadiusRing(caster.Position, caster.Map, r, new Color(1f, 1f, 1f, 0.78f));
                }
            }
        }

        public static void DrawLargeRadiusRing(IntVec3 center, Map map, float radius, Color color, int segments = 80)
        {
            if (map == null || radius <= 0.5f)
            {
                return;
            }
            if (radius < GenRadial.MaxRadialPatternRadius - 0.1f)
            {
                GenDraw.DrawRadiusRing(center, radius, color);
                return;
            }

            Vector3 cPos = center.ToVector3Shifted();
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            float minX = 0.5f;
            float minZ = 0.5f;
            float maxX = map.Size.x - 0.5f;
            float maxZ = map.Size.z - 0.5f;
            int segCount = Mathf.Clamp(segments, 36, 128);
            float stepRad = (Mathf.PI * 2f) / segCount;

            Vector3 prev = new Vector3(cPos.x + radius, y, cPos.z);
            bool prevIn = prev.x >= minX && prev.x <= maxX && prev.z >= minZ && prev.z <= maxZ;

            for (int i = 1; i <= segCount; i++)
            {
                float ang = i * stepRad;
                Vector3 cur = new Vector3(
                    cPos.x + Mathf.Cos(ang) * radius,
                    y,
                    cPos.z + Mathf.Sin(ang) * radius);
                bool curIn = cur.x >= minX && cur.x <= maxX && cur.z >= minZ && cur.z <= maxZ;
                if (prevIn && curIn)
                {
                    GenDraw.DrawLineBetween(prev, cur, SimpleColor.White, 0.16f);
                }
                prev = cur;
                prevIn = curIn;
            }
        }
    }

    public class CompProperties_AbilityMechPhaseAdvance : CompProperties_AbilityEffect
    {
        public CompProperties_AbilityMechPhaseAdvance()
        {
            compClass = typeof(CompAbilityEffect_MechPhaseAdvance);
        }
    }

    public class CompAbilityEffect_MechPhaseAdvance : CompAbilityEffect
    {
        public new CompProperties_AbilityMechPhaseAdvance Props => (CompProperties_AbilityMechPhaseAdvance)props;

        private CompGiantPhaseTransform ResolvePhaseTransform()
        {
            Pawn caster = parent?.pawn;
            if (caster == null)
            {
                return null;
            }
            return GiantFastLookup.TryGetPhaseTransform(caster) ?? caster.TryGetComp<CompGiantPhaseTransform>();
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            CompGiantPhaseTransform phaseComp = ResolvePhaseTransform();
            phaseComp?.TryAdvanceToNextStageManually();
        }

        private static string TranslateOrFallback(string key, string fallback)
        {
            return LanguageDatabase.activeLanguage != null && key.CanTranslate()
                ? key.Translate().ToString()
                : fallback;
        }

        public override bool GizmoDisabled(out string reason)
        {
            CompGiantPhaseTransform phaseComp = ResolvePhaseTransform();
            if (phaseComp == null)
            {
                reason = TranslateOrFallback("GCF_PhaseAdvanceDisabledUnavailable", "Phase transform system unavailable.");
                return true;
            }
            if (!phaseComp.CanManuallyAdvanceToNextStage(out string reasonKey, out string fallback))
            {
                reason = TranslateOrFallback(reasonKey, fallback);
                return true;
            }
            return base.GizmoDisabled(out reason);
        }

        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            return false;
        }
    }

    public class CompProperties_AbilityMechPhaseRevert : CompProperties_AbilityEffect
    {
        public CompProperties_AbilityMechPhaseRevert()
        {
            compClass = typeof(CompAbilityEffect_MechPhaseRevert);
        }
    }

    public class CompAbilityEffect_MechPhaseRevert : CompAbilityEffect
    {
        public new CompProperties_AbilityMechPhaseRevert Props => (CompProperties_AbilityMechPhaseRevert)props;

        private CompGiantPhaseTransform ResolvePhaseTransform()
        {
            Pawn caster = parent?.pawn;
            if (caster == null)
            {
                return null;
            }
            return GiantFastLookup.TryGetPhaseTransform(caster) ?? caster.TryGetComp<CompGiantPhaseTransform>();
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            CompGiantPhaseTransform phaseComp = ResolvePhaseTransform();
            phaseComp?.TryRevertToPreviousStageManually();
        }

        private static string TranslateOrFallback(string key, string fallback)
        {
            return LanguageDatabase.activeLanguage != null && key.CanTranslate()
                ? key.Translate().ToString()
                : fallback;
        }

        public override bool GizmoDisabled(out string reason)
        {
            CompGiantPhaseTransform phaseComp = ResolvePhaseTransform();
            if (phaseComp == null)
            {
                reason = TranslateOrFallback("GCF_PhaseAdvanceDisabledUnavailable", "Phase transform system unavailable.");
                return true;
            }
            if (!phaseComp.CanManuallyRevertToPreviousStage(out string reasonKey, out string fallback))
            {
                reason = TranslateOrFallback(reasonKey, fallback);
                return true;
            }
            return base.GizmoDisabled(out reason);
        }

        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            return false;
        }
    }
}
