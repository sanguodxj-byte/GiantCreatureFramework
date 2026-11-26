using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public interface IGiantDynamicAbilityAnimComp
    {
        string ResolveEffectiveAnimDefName(CompGiantRig rig);
    }

    public interface IGiantShaderSnapshotComp
    {
        void ComputeShaderSnapshot(out Color glowColor, out Vector4 shaderParams);
    }

    public interface IGiantMeleeArmorPenModifier
    {
        float ModifyMeleeArmorPenetration(float baseArmorPenetration);
    }

    public abstract class GiantMotionOverrideProvider
    {
        public virtual bool TryGetActiveWorldPos(Pawn pawn, out Vector3 worldPos)
        {
            worldPos = default;
            return false;
        }

        public virtual bool TryGetActiveDirectionXZ(Pawn pawn, out Vector2 dirXZ)
        {
            dirXZ = default;
            return false;
        }

        public virtual bool TryGetLockedFlightHeading(Pawn pawn, out float headingDeg)
        {
            headingDeg = 0f;
            return false;
        }

        public virtual bool HasActiveDisplacement(Pawn pawn) => false;

        public virtual bool HasActiveWeaponSuppressingSequence(Pawn pawn) => HasActiveDisplacement(pawn);

        public virtual bool IsHiddenInOrbit(Pawn pawn) => false;

        public virtual bool HasActiveHighAltitudeOverride(Pawn pawn) => false;

        public virtual bool TryGetActiveAnimOverride(Pawn pawn, out string animDefName, out float sampleTick)
        {
            animDefName = null;
            sampleTick = 0f;
            return false;
        }

        public virtual bool TryGetSyncedClipSampleTick(Pawn pawn, string activeClipDefName, out float sampleTick)
        {
            sampleTick = 0f;
            return false;
        }

        public virtual bool TryGetArenaCameraAnchorXZ(Pawn pawn, out Vector2 anchorXZ)
        {
            anchorXZ = default;
            return false;
        }

        public virtual bool TryResolveWarmupCapTick(GiantAnimDef clip, out float capTick)
        {
            capTick = 0f;
            return false;
        }

        public virtual bool ShouldRewindOnWarmupHandoff(GiantAnimDef clip) => false;
    }

    public static class GiantMotionOverrideRegistry
    {
        private static readonly object SyncRoot = new object();
        private static GiantMotionOverrideProvider[] providers = Array.Empty<GiantMotionOverrideProvider>();

        public static void Register(GiantMotionOverrideProvider provider)
        {
            if (provider == null)
            {
                return;
            }
            lock (SyncRoot)
            {
                Type providerType = provider.GetType();
                for (int i = 0; i < providers.Length; i++)
                {
                    if (providers[i].GetType() == providerType)
                    {
                        providers[i] = provider;
                        return;
                    }
                }
                var next = new GiantMotionOverrideProvider[providers.Length + 1];
                Array.Copy(providers, next, providers.Length);
                next[providers.Length] = provider;
                providers = next;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetActiveWorldPos(Pawn pawn, out Vector3 worldPos)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                worldPos = default;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryGetActiveWorldPos(pawn, out worldPos))
                {
                    return true;
                }
            }
            worldPos = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetActiveDirectionXZ(Pawn pawn, out Vector2 dirXZ)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                dirXZ = default;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryGetActiveDirectionXZ(pawn, out dirXZ))
                {
                    return true;
                }
            }
            dirXZ = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetLockedFlightHeading(Pawn pawn, out float headingDeg)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                headingDeg = 0f;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryGetLockedFlightHeading(pawn, out headingDeg))
                {
                    return true;
                }
            }
            headingDeg = 0f;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasActiveDisplacement(Pawn pawn)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].HasActiveDisplacement(pawn))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasActiveWeaponSuppressingSequence(Pawn pawn)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].HasActiveWeaponSuppressingSequence(pawn))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHiddenInOrbit(Pawn pawn)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].IsHiddenInOrbit(pawn))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasActiveHighAltitudeOverride(Pawn pawn)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].HasActiveHighAltitudeOverride(pawn))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetActiveAnimOverride(Pawn pawn, out string animDefName, out float sampleTick)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                animDefName = null;
                sampleTick = 0f;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryGetActiveAnimOverride(pawn, out animDefName, out sampleTick))
                {
                    return true;
                }
            }
            animDefName = null;
            sampleTick = 0f;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetSyncedClipSampleTick(Pawn pawn, string activeClipDefName, out float sampleTick)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null || string.IsNullOrEmpty(activeClipDefName))
            {
                sampleTick = 0f;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryGetSyncedClipSampleTick(pawn, activeClipDefName, out sampleTick))
                {
                    return true;
                }
            }
            sampleTick = 0f;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetArenaCameraAnchorXZ(Pawn pawn, out Vector2 anchorXZ)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || pawn == null)
            {
                anchorXZ = default;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryGetArenaCameraAnchorXZ(pawn, out anchorXZ))
                {
                    return true;
                }
            }
            anchorXZ = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryResolveWarmupCapTick(GiantAnimDef clip, out float capTick)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || clip == null)
            {
                capTick = 0f;
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].TryResolveWarmupCapTick(clip, out capTick))
                {
                    return true;
                }
            }
            capTick = 0f;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldRewindOnWarmupHandoff(GiantAnimDef clip)
        {
            GiantMotionOverrideProvider[] list = providers;
            if (list.Length == 0 || clip == null)
            {
                return false;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].ShouldRewindOnWarmupHandoff(clip))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public static class GiantAutonomousAbilityRegistry
    {
        private static readonly object SyncRoot = new object();
        private static Func<Pawn, Job>[] evaluators = Array.Empty<Func<Pawn, Job>>();

        public static void Register(Func<Pawn, Job> evaluator)
        {
            if (evaluator == null)
            {
                return;
            }
            lock (SyncRoot)
            {
                for (int i = 0; i < evaluators.Length; i++)
                {
                    if (evaluators[i] == evaluator)
                    {
                        return;
                    }
                }
                var next = new Func<Pawn, Job>[evaluators.Length + 1];
                Array.Copy(evaluators, next, evaluators.Length);
                next[evaluators.Length] = evaluator;
                evaluators = next;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Job TryGiveJob(Pawn pawn)
        {
            Func<Pawn, Job>[] list = evaluators;
            if (list.Length == 0 || pawn == null)
            {
                return null;
            }
            for (int i = 0; i < list.Length; i++)
            {
                Job job = list[i](pawn);
                if (job != null)
                {
                    return job;
                }
            }
            return null;
        }
    }

    public interface IGiantPhaseTransformVfxListener
    {
        void OnTransformStarted(Pawn pawn, int targetStageIndex, int durationTicks);
        void OnTickPawnEffects(Pawn pawn, CompGiantPhaseTransform phaseComp);
        void OnStageTransitionBurst(Map map, Pawn pawn, Vector3 worldPos, int currentStageIndex, Color tint);
        void OnFinalMeltdownBurst(Map map, Pawn pawn, Vector3 worldPos);
        void ClearForPawn(Pawn pawn);
    }

    public static class GiantPhaseTransformVfxRegistry
    {
        private static readonly object SyncRoot = new object();
        private static IGiantPhaseTransformVfxListener[] listeners = Array.Empty<IGiantPhaseTransformVfxListener>();

        public static void Register(IGiantPhaseTransformVfxListener listener)
        {
            if (listener == null)
            {
                return;
            }
            lock (SyncRoot)
            {
                Type t = listener.GetType();
                for (int i = 0; i < listeners.Length; i++)
                {
                    if (listeners[i].GetType() == t)
                    {
                        listeners[i] = listener;
                        return;
                    }
                }
                var next = new IGiantPhaseTransformVfxListener[listeners.Length + 1];
                Array.Copy(listeners, next, listeners.Length);
                next[listeners.Length] = listener;
                listeners = next;
            }
        }

        public static void NotifyTransformStarted(Pawn pawn, int targetStageIndex, int durationTicks)
        {
            IGiantPhaseTransformVfxListener[] list = listeners;
            for (int i = 0; i < list.Length; i++)
            {
                list[i].OnTransformStarted(pawn, targetStageIndex, durationTicks);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void NotifyTickPawnEffects(Pawn pawn, CompGiantPhaseTransform phaseComp)
        {
            IGiantPhaseTransformVfxListener[] list = listeners;
            for (int i = 0; i < list.Length; i++)
            {
                list[i].OnTickPawnEffects(pawn, phaseComp);
            }
        }

        public static void NotifyStageTransitionBurst(Map map, Pawn pawn, Vector3 worldPos, int currentStageIndex, Color tint)
        {
            IGiantPhaseTransformVfxListener[] list = listeners;
            for (int i = 0; i < list.Length; i++)
            {
                list[i].OnStageTransitionBurst(map, pawn, worldPos, currentStageIndex, tint);
            }
        }

        public static void NotifyFinalMeltdownBurst(Map map, Pawn pawn, Vector3 worldPos)
        {
            IGiantPhaseTransformVfxListener[] list = listeners;
            for (int i = 0; i < list.Length; i++)
            {
                list[i].OnFinalMeltdownBurst(map, pawn, worldPos);
            }
        }

        public static void NotifyClearForPawn(Pawn pawn)
        {
            IGiantPhaseTransformVfxListener[] list = listeners;
            for (int i = 0; i < list.Length; i++)
            {
                list[i].ClearForPawn(pawn);
            }
        }
    }

    public static class GiantDormantLockRegistry
    {
        private static readonly Dictionary<int, Pawn> LockedPawnsById = new Dictionary<int, Pawn>(8);

        public static bool HasAnyLocked
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => LockedPawnsById.Count > 0;
        }

        public static void SetLocked(Pawn pawn, bool locked)
        {
            if (pawn == null)
            {
                return;
            }
            if (locked)
            {
                LockedPawnsById[pawn.thingIDNumber] = pawn;
            }
            else
            {
                LockedPawnsById.Remove(pawn.thingIDNumber);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLocked(Thing thing)
        {
            if (LockedPawnsById.Count == 0 || thing == null)
            {
                return false;
            }
            return LockedPawnsById.TryGetValue(thing.thingIDNumber, out Pawn lockedPawn) && ReferenceEquals(lockedPawn, thing);
        }

        public static void ClearAll()
        {
            LockedPawnsById.Clear();
        }
    }

    public static class GiantSubmodEvents
    {
        public static event Action<Pawn, CompGiantRig, BoneNodeDef> BoneSevered;

        public static void NotifyBoneSevered(Pawn pawn, CompGiantRig rig, BoneNodeDef severedBone)
        {
            BoneSevered?.Invoke(pawn, rig, severedBone);
        }
    }

    public struct GiantBossHudPartBadge
    {
        public string Label;
        public bool IsDestroyed;
        public float HealthPct01;
    }

    public struct GiantBossHudTelemetry
    {
        public bool HasCustomState;
        public float ActiveBlend01;
        public float GlowPulse01;
        public bool IsOverdrive;
        public bool ShowSecondaryBar;
        public float SecondaryFill01;
        public float SecondaryThresholdMarker01;
        public Color SecondaryBarColor;
        public string StatusReadout;
        public Color StatusTextColor;
        public GiantBossHudPartBadge[] PartBadges;
        public int PartBadgeCount;
    }

    public interface IGiantBossHudStateProvider
    {
        void FillBossHudTelemetry(ref GiantBossHudTelemetry telemetry);
    }

    public interface IGiantBossHudOverlayDrawer
    {
        void DrawBossBarOverlay(Rect trackRect, Rect wellRect, in GiantBossHudTelemetry telemetry, float unfold01, float alpha);
    }

    public struct GiantBossBarRenderContext
    {
        public Pawn Pawn;
        public CompGiantRig RigComp;
        public GiantHealthBarDef StyleDef;
        public float CenterX;
        public float StartY;
        public float TotalWidth;
        public float BarHeight;
        public float Unfold01;
        public float FillReveal01;
        public float Alpha;
        public bool CompactMode;
        public bool IsSelected;
        public bool HasExpandButton;
        public bool Expanded;
        public float ExpandAnim01;
        public float MainHealthPct;
        public float MainLagPct;
        public string DisplayTitle;
        public Color? AccentColorOverride;
        public IReadOnlyList<GiantHealthBarPartRowSpec> RowSpecs;
        public float[] RowHealthPct;
        public float[] RowLagPct;
        public Vector2 ScrollPos;
    }

    public struct GiantBossBarRenderResult
    {
        public float UsedHeight;
        public bool ToggleExpandClicked;
        public bool SelectAndJumpClicked;
        public Vector2 UpdatedScrollPos;
    }

    public interface IGiantCustomBossBarRenderer
    {
        bool CanRender(Pawn pawn, CompGiantRig rigComp, GiantHealthBarDef styleDef);
        bool TryDrawBossBar(in GiantBossBarRenderContext context, out GiantBossBarRenderResult result);
    }

    public static class GiantCustomBossBarRegistry
    {
        private static readonly object SyncRoot = new object();
        private static IGiantCustomBossBarRenderer[] renderers = Array.Empty<IGiantCustomBossBarRenderer>();

        public static void Register(IGiantCustomBossBarRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }
            lock (SyncRoot)
            {
                Type t = renderer.GetType();
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i].GetType() == t)
                    {
                        renderers[i] = renderer;
                        return;
                    }
                }
                var next = new IGiantCustomBossBarRenderer[renderers.Length + 1];
                Array.Copy(renderers, next, renderers.Length);
                next[renderers.Length] = renderer;
                renderers = next;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryDraw(in GiantBossBarRenderContext context, out GiantBossBarRenderResult result)
        {
            IGiantCustomBossBarRenderer[] list = renderers;
            for (int i = 0; i < list.Length; i++)
            {
                IGiantCustomBossBarRenderer r = list[i];
                if (r.CanRender(context.Pawn, context.RigComp, context.StyleDef) &&
                    r.TryDrawBossBar(in context, out result))
                {
                    return true;
                }
            }
            result = default;
            return false;
        }
    }
}
