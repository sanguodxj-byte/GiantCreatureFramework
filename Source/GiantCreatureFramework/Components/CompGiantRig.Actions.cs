using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Attack/ability triggers, warmup handoff, forced morphs, and preview clips.
    public partial class CompGiantRig
    {
        public GiantAnimDef ResolveMeleeAttackAnim(string toolTagOrLabel)
        {
            return RigDef?.ResolveMeleeAttackAnim(toolTagOrLabel);
        }

        public GiantAnimDef ResolveRangedAttackAnim()
        {
            GiantAnimDef clip = RigDef?.rangedAttackAnim;
            return clip != null && !clip.loop ? clip : null;
        }

        public GiantAnimDef ResolveAbilityAnim(string tagOrDefName)
        {
            return RigDef?.ResolveAbilityAnim(tagOrDefName);
        }

        public GiantAnimDef ResolveAbilityAnim(Ability ability)
        {
            if (ability == null || RigDef == null)
            {
                return null;
            }
            if (ability.comps != null)
            {
                for (int i = 0; i < ability.comps.Count; i++)
                {
                    if (ability.comps[i] is IGiantDynamicAbilityAnimComp dynComp)
                    {
                        GiantAnimDef dynAnim = ResolveAbilityAnim(dynComp.ResolveEffectiveAnimDefName(this));
                        if (dynAnim != null)
                        {
                            return dynAnim;
                        }
                    }
                }
            }
            return ResolveAbilityAnim(ability.def?.defName);
        }

        private bool HasAnyIntactRearMeleeTool()
        {
            List<Tool> tools = Pawn?.def?.tools;
            if (tools == null || tools.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < tools.Count; i++)
            {
                Tool tool = tools[i];
                if (tool == null || !IsMeleeToolAnatomicallyIntact(tool))
                {
                    continue;
                }
                string lower = GiantRigDef.BuildToolMatchTag(tool).ToLowerInvariant();
                if ((lower.Contains("tail") || lower.Contains("fluke") || lower.Contains("stinger") ||
                     lower.Contains("rear") || lower.Contains("crucible") || lower.Contains("thruster") ||
                     lower.Contains("propulsion")) &&
                    ResolveMeleeAttackAnim(GiantRigDef.BuildToolMatchTag(tool)) != null)
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsMeleeToolAnatomicallyIntact(Tool tool)
        {
            if (tool == null || tool.linkedBodyPartsGroup == null || Pawn?.health?.hediffSet == null)
            {
                return true;
            }

            foreach (BodyPartRecord part in Pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part == null || !part.IsInGroup(tool.linkedBodyPartsGroup))
                {
                    continue;
                }

                bool blockedBySeveredNode = false;
                if (RigDef?.SortedNodes != null)
                {
                    BoneNodeDef[] nodes = RigDef.SortedNodes;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        if (GetLinkedBodyPartRaw(i) == part && !IsBoneIntact(nodes[i].nodeId))
                        {
                            blockedBySeveredNode = true;
                            break;
                        }
                    }
                }

                if (!blockedBySeveredNode)
                {
                    return true;
                }
            }

            return false;
        }

        private GiantAnimDef ResolveWarmupAnim(Stance_Warmup warmup, out Vector2? targetWorldXZ)
        {
            targetWorldXZ = null;
            if (warmup?.verb == null || RigDef == null)
            {
                return null;
            }

            if (warmup.focusTarg.IsValid)
            {
                Vector3 wp = warmup.focusTarg.HasThing
                    ? (warmup.focusTarg.Thing is Pawn tp ? GetSimTickPos(tp) : warmup.focusTarg.Thing.DrawPos)
                    : warmup.focusTarg.Cell.ToVector3Shifted();
                targetWorldXZ = new Vector2(wp.x, wp.z);
            }

            if (warmup.verb is IAbilityVerb abVerb && abVerb.Ability?.def != null)
            {
                return ResolveAbilityAnim(abVerb.Ability);
            }
            if (warmup.verb is Verb_LaunchProjectile)
            {
                return ResolveRangedAttackAnim();
            }
            if (warmup.verb is Verb_MeleeAttack meleeVerb)
            {
                string tag = meleeVerb.tool != null
                    ? GiantRigDef.BuildToolMatchTag(meleeVerb.tool)
                    : warmup.verb.verbProps?.label;
                return ResolveMeleeAttackAnim(tag);
            }
            return null;
        }

        public bool TriggerAttackAnimation(string toolTagOrLabel, bool isRanged = false, Vector2? targetWorldXZ = null)
        {
            if (RigDef == null)
            {
                return false;
            }

            GiantAnimDef chosen = isRanged
                ? ResolveRangedAttackAnim()
                : ResolveMeleeAttackAnim(toolTagOrLabel);

            if (chosen == null)
            {
                return false;
            }

            ActivateAttackOneShot(chosen, targetWorldXZ);
            return true;
        }

        public bool TriggerAbilityAnimation(Ability ability, Vector2? targetWorldXZ = null)
        {
            if (RigDef == null || ability == null)
            {
                return false;
            }

            GiantAnimDef chosen = ResolveAbilityAnim(ability);
            if (chosen == null)
            {
                return false;
            }

            ActivateAttackOneShot(chosen, targetWorldXZ);
            return true;
        }

        public bool TriggerAbilityAnimation(string tagOrDefName, Vector2? targetWorldXZ = null)
        {
            if (RigDef == null)
            {
                return false;
            }

            GiantAnimDef chosen = ResolveAbilityAnim(tagOrDefName);
            if (chosen == null)
            {
                return false;
            }

            ActivateAttackOneShot(chosen, targetWorldXZ);
            return true;
        }

        public void PlayOneShotFlightTransition(GiantAnimDef clip)
        {
            if (clip == null || RigDef == null)
            {
                return;
            }
            if (Pawn != null && GiantMotionOverrideRegistry.HasActiveWeaponSuppressingSequence(Pawn))
            {
                return;
            }
            ActivateAttackOneShot(clip, null);
        }

        public bool IsSoftWhipStrikeActive =>
            animState == GiantAnimState.AttackOnce
            && !isWarmupWindup
            && activeOneShotAnim != null
            && IsSoftWhipStrikeClip(activeOneShotAnim);

        private bool IsSoftWhipStrikeClip(GiantAnimDef clip)
        {
            if (clip == null || string.IsNullOrEmpty(clip.defName) || RigDef?.softWhips == null)
            {
                return false;
            }
            for (int i = 0; i < RigDef.softWhips.Count; i++)
            {
                SoftWhipDef sw = RigDef.softWhips[i];
                if (sw != null && sw.IsStrikeAnim(clip.defName))
                {
                    return true;
                }
            }
            return false;
        }

        private float ResolveWarmupCapTick(GiantAnimDef clip)
        {
            if (clip == null)
            {
                return 0f;
            }
            if (GiantMotionOverrideRegistry.TryResolveWarmupCapTick(clip, out float customWarmupCap))
            {
                return customWarmupCap;
            }
            if (RigDef?.softWhips != null)
            {
                float minWindupNorm = float.MaxValue;
                for (int i = 0; i < RigDef.softWhips.Count; i++)
                {
                    SoftWhipDef sw = RigDef.softWhips[i];
                    if (sw != null && sw.IsStrikeAnim(clip.defName))
                    {
                        minWindupNorm = Mathf.Min(minWindupNorm, Mathf.Clamp(sw.windupEndNorm - 0.01f, 0.05f, 0.30f));
                    }
                }
                if (minWindupNorm < 1f)
                {
                    return Mathf.Clamp(clip.durationTicks * minWindupNorm, 1f, clip.ResolveImpactTick());
                }
            }
            return clip.ResolveImpactTick();
        }

        private void ActivateAttackOneShot(GiantAnimDef chosen, Vector2? targetWorldXZ)
        {
            int curTicksGame = Current.Game?.tickManager?.TicksGame ?? -1;
            bool continuingFromWarmup = isWarmupWindup && animState == GiantAnimState.AttackOnce && activeOneShotAnim == chosen;
            isWarmupWindup = false;

            // Guard against duplicate same-tick activation (e.g. Ability_Activate_Prefix + CompAbilityEffect.Apply)
            // so a just-handed-off ability clip is never rewound back to tick 0.
            if (!continuingFromWarmup
                && curTicksGame >= 0
                && lastOneShotActivateTick == curTicksGame
                && animState == GiantAnimState.AttackOnce
                && activeOneShotAnim == chosen)
            {
                if (targetWorldXZ.HasValue)
                {
                    hasAttackTargetWorldXZ = true;
                    attackTargetWorldXZ = targetWorldXZ.Value;
                }
                return;
            }
            lastOneShotActivateTick = curTicksGame;

            hasAttackTargetWorldXZ = targetWorldXZ.HasValue;
            if (targetWorldXZ.HasValue)
            {
                attackTargetWorldXZ = targetWorldXZ.Value;
            }

            if (continuingFromWarmup)
            {
                float handoffMax = Mathf.Max(0f, chosen.durationTicks - 4f);
                float prevWarmupTick = oneShotElapsedTicks;
                if (GiantMotionOverrideRegistry.ShouldRewindOnWarmupHandoff(chosen))
                {
                    oneShotElapsedTicks = 0f;
                }
                else if (IsSoftWhipStrikeClip(chosen))
                {
                    // Soft-whip crossing clips held inside windup during Stance_Warmup must play the full 1.0x whip-crack snap phase [windup..impact..end]!
                    oneShotElapsedTicks = Mathf.Clamp(prevWarmupTick, 0f, handoffMax);
                }
                else
                {
                    float impactHandoff = Mathf.Min(chosen.ResolveImpactTick(), handoffMax);
                    oneShotElapsedTicks = Mathf.Clamp(Mathf.Max(prevWarmupTick, impactHandoff), 0f, handoffMax);
                }
                activeOneShotAnim = chosen;
                TransitionToClip(chosen, GiantAnimState.AttackOnce);
                if (oneShotElapsedTicks > prevWarmupTick)
                {
                    EvaluateClipEvents(chosen, prevWarmupTick, oneShotElapsedTicks, isLoop: false);
                }
            }
            else
            {
                activeOneShotAnim = chosen;
                oneShotElapsedTicks = 0f;
                TransitionToClip(chosen, GiantAnimState.AttackOnce);
            }

            // Immediately evaluate skeletal pose so synchronous first-shot projectile launches read exact impact-frame muzzles.
            if (Pawn != null && Pawn.Spawned)
            {
                EvaluateCurrentPoseAndPublish(0f);
            }
        }

        public bool IsMorphClipFinished =>
            animState != GiantAnimState.MorphLocked
            || activeOneShotAnim == null
            || oneShotElapsedTicks >= activeOneShotAnim.durationTicks;

        public float MorphClipNormalizedTime =>
            (animState == GiantAnimState.MorphLocked && activeOneShotAnim != null && activeOneShotAnim.durationTicks > 0.01f)
                ? Mathf.Clamp01(oneShotElapsedTicks / activeOneShotAnim.durationTicks)
                : 0f;

        public void PlayForcedMorphClip(GiantAnimDef clip, float blendTicksOverride = -1f)
        {
            PlayForcedMorphClipAtTick(clip, blendTicksOverride, startElapsedTicks: 0f);
        }

        internal void PlayForcedMorphClipAtTick(GiantAnimDef clip, float blendTicksOverride, float startElapsedTicks)
        {
            hasAttackTargetWorldXZ = false;
            if (clip == null)
            {
                activeOneShotAnim = null;
                TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
                return;
            }
            EnsureBuffersInitialized();
            activeOneShotAnim = clip;
            oneShotElapsedTicks = Mathf.Clamp(startElapsedTicks, 0f, Mathf.Max(0f, clip.durationTicks));
            animState = GiantAnimState.MorphLocked;
            CaptureBlendFromPoses(finalLocalPoses);
            currentClip = clip;
            blendElapsedTicks = 0f;
            blendDurationTicks = blendTicksOverride >= 0f ? blendTicksOverride : Mathf.Max(0, clip.blendInTicks);
            EvaluateCurrentPoseAndPublish(0f);
        }

        public void TriggerPreviewClip(GiantAnimDef clip)
        {
            hasAttackTargetWorldXZ = false;
            if (clip == null)
            {
                animState = GiantAnimState.Idle;
                activeOneShotAnim = null;
                TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
                return;
            }
            activeOneShotAnim = clip;
            oneShotElapsedTicks = 0f;
            TransitionToClip(clip, GiantAnimState.ManualPreview);
        }

    }
}
