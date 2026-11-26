using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    [DefOf]
    public static class GCF_VfxDefOf
    {
        public static ShaderTypeDef GCF_RimfxUberVfx;
        public static ShaderTypeDef GCF_GiantDropShadow;

        static GCF_VfxDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(GCF_VfxDefOf));
        }
    }

    [StaticConstructorOnStartup]
    public static class GiantVfxSystem
    {
        public enum RimfxShaderMode
        {
            CleaveSlash = 0,
            CannonShockCone = 1,
            PlasmaRailSpear = 2,
            ErodedShockwave = 3,
            ImpactSparkFan = 4,
            DetonationNova = 5,
            VolumetricSmoke = 6,
            HarmonicAuraOrArc = 7,
            TectonicRiftDecal = 8,
            AeroBeamWake = 9,
            KhaydarinWarpVortex = 10,
            PureBlackGroundShadow = 11
        }

        public enum RimfxFollowKind
        {
            None = 0,
            ThingCenter = 1,
            BoneUV = 2
        }

        private sealed class RimfxLayerInstance
        {
            public Map Map;
            public Vector3 OriginWorldPos;
            public float HeadingDeg;
            public RimfxShaderMode Mode;
            public Vector2 StartSizeXZ;
            public Vector2 EndSizeXZ;
            public float TiltScaleZ;
            public float ScaleEasePower;
            public float ForwardAnchorShift;
            public float AltitudeOffset;
            public bool UseGroundAltitude;
            public Color CoreColor;
            public Color BodyColor;
            public Color EdgeColor;
            public float EmissiveBoost;
            public float SpanParam;
            public float SubParam;
            public float MasterAlpha;
            public float Seed;
            public int DelayTicks;
            public int ElapsedTicks;
            public int DurationTicks;
            public bool HasLinearTrajectory;
            public Vector3 EndWorldPos;
            public int SpawnSeq;

            // Live part/component following state (cleared on Reset/Release to avoid retaining game references)
            public RimfxFollowKind FollowKind;
            public Thing FollowThing;
            public CompGiantRig FollowRig;
            public int FollowBoneIndex;
            public Vector2 FollowBoneUV;
            public float FollowAngleOffset;
            public bool FollowRotateWithSource;

            public void Reset()
            {
                Map = null;
                OriginWorldPos = Vector3.zero;
                HeadingDeg = 0f;
                Mode = RimfxShaderMode.ErodedShockwave;
                StartSizeXZ = Vector2.one;
                EndSizeXZ = Vector2.one;
                TiltScaleZ = 1f;
                ScaleEasePower = 1.8f;
                ForwardAnchorShift = 0f;
                AltitudeOffset = 0.08f;
                UseGroundAltitude = false;
                CoreColor = Color.white;
                BodyColor = Color.white;
                EdgeColor = Color.white;
                EmissiveBoost = 0.85f;
                SpanParam = 2.0f;
                SubParam = 0.35f;
                MasterAlpha = 0.48f;
                Seed = 1f;
                DelayTicks = 0;
                ElapsedTicks = 0;
                DurationTicks = 18;
                HasLinearTrajectory = false;
                EndWorldPos = Vector3.zero;
                SpawnSeq = 0;
                ClearFollow();
            }

            public void ClearFollow()
            {
                FollowKind = RimfxFollowKind.None;
                FollowThing = null;
                FollowRig = null;
                FollowBoneIndex = -1;
                FollowBoneUV = new Vector2(0.5f, 0.5f);
                FollowAngleOffset = 0f;
                FollowRotateWithSource = false;
            }
        }

        private const int MaxActiveProceduralLayers = 384;
        private const float SecondsPerTick = 1f / 60f;

        private static readonly int PropColor = Shader.PropertyToID("_Color");
        private static readonly int PropCoreColor = Shader.PropertyToID("_CoreColor");
        private static readonly int PropBodyColor = Shader.PropertyToID("_BodyColor");
        private static readonly int PropEdgeColor = Shader.PropertyToID("_EdgeColor");
        private static readonly int PropVfxParams1 = Shader.PropertyToID("_VfxParams1");
        private static readonly int PropVfxParams2 = Shader.PropertyToID("_VfxParams2");

        private static readonly List<RimfxLayerInstance> ActiveLayers = new List<RimfxLayerInstance>(192);
        private static readonly Stack<RimfxLayerInstance> LayerPool = new Stack<RimfxLayerInstance>(192);

        private static Material cachedUberMaterial;
        private static MaterialPropertyBlock sharedPropertyBlock;
        private static MaterialPropertyBlock sharedShadowPropertyBlock;
        internal static MaterialPropertyBlock SharedShadowPropertyBlock => sharedShadowPropertyBlock ??= new MaterialPropertyBlock();
        private static uint deterministicSeedCounter = 17u;
        private static int nextSpawnSeq = 1;
        private static int lastDualWhipExplosionPawnId = -1;
        private static int lastDualWhipExplosionTick = -99999;

        public static int TotalTriggeredVfxCount { get; private set; }
        public static int TotalTriggeredAnimEventsCount { get; private set; }
        public static int ActiveProceduralVfxCount => ActiveLayers.Count;

        public static void ResetCountersForTesting()
        {
            TotalTriggeredVfxCount = 0;
            TotalTriggeredAnimEventsCount = 0;
            lastDualWhipExplosionPawnId = -1;
            lastDualWhipExplosionTick = -99999;
        }

        public static void ClearAllProceduralVfx()
        {
            for (int i = 0; i < ActiveLayers.Count; i++)
            {
                ReleaseLayer(ActiveLayers[i]);
            }
            ActiveLayers.Clear();
            nextSpawnSeq = 1;
            cachedUberMaterial = null;
            lastDualWhipExplosionPawnId = -1;
            lastDualWhipExplosionTick = -99999;
            CompAbilityEffect_AbyssalQuake.ClearAllPendingCrossings();
            GiantSeveredDebrisSystem.ClearAll();
        }

        public static void ClearForMap(Map map)
        {
            if (map == null)
            {
                return;
            }

            CompAbilityEffect_AbyssalQuake.ClearPendingCrossingsForMap(map);
            GiantSeveredDebrisSystem.ClearForMap(map);
            for (int i = ActiveLayers.Count - 1; i >= 0; i--)
            {
                if (ActiveLayers[i].Map == map)
                {
                    ReleaseLayer(ActiveLayers[i]);
                    ActiveLayers.RemoveAt(i);
                }
            }
        }

        private static float NextDeterministicSeed()
        {
            unchecked
            {
                deterministicSeedCounter = deterministicSeedCounter * 1664525u + 1013904223u;
                return 1f + (deterministicSeedCounter % 9500u) * 0.01f;
            }
        }

        private static RimfxLayerInstance RentLayer()
        {
            if (LayerPool.Count > 0)
            {
                RimfxLayerInstance inst = LayerPool.Pop();
                inst.Reset();
                return inst;
            }
            RimfxLayerInstance created = new RimfxLayerInstance();
            created.Reset();
            return created;
        }

        private static void ReleaseLayer(RimfxLayerInstance inst)
        {
            if (inst == null)
            {
                return;
            }
            inst.Map = null;
            inst.ClearFollow();
            if (LayerPool.Count < MaxActiveProceduralLayers)
            {
                LayerPool.Push(inst);
            }
        }

        private static void EnqueueLayer(RimfxLayerInstance inst)
        {
            if (inst == null || inst.Map == null || !UnityData.IsInMainThread)
            {
                ReleaseLayer(inst);
                return;
            }

            if (ActiveLayers.Count >= MaxActiveProceduralLayers)
            {
                ReleaseLayer(ActiveLayers[0]);
                ActiveLayers.RemoveAt(0);
            }
            inst.SpawnSeq = nextSpawnSeq++;
            ActiveLayers.Add(inst);
        }

        private static void BindLayersToThing(int fromSpawnSeq, Thing followThing)
        {
            if (followThing == null)
            {
                return;
            }
            for (int i = ActiveLayers.Count - 1; i >= 0; i--)
            {
                RimfxLayerInstance inst = ActiveLayers[i];
                if (inst == null)
                {
                    continue;
                }
                if (inst.SpawnSeq < fromSpawnSeq)
                {
                    break;
                }
                if (!inst.UseGroundAltitude)
                {
                    inst.FollowKind = RimfxFollowKind.ThingCenter;
                    inst.FollowThing = followThing;
                }
            }
        }

        private static void BindLayersToBoneUV(
            int fromSpawnSeq,
            CompGiantRig rig,
            int boneIndex,
            Vector2 boneUV,
            float angleOffsetDeg = 0f,
            bool rotateWithBone = false)
        {
            if (rig == null || boneIndex < 0)
            {
                return;
            }
            for (int i = ActiveLayers.Count - 1; i >= 0; i--)
            {
                RimfxLayerInstance inst = ActiveLayers[i];
                if (inst == null)
                {
                    continue;
                }
                if (inst.SpawnSeq < fromSpawnSeq)
                {
                    break;
                }
                if (!inst.UseGroundAltitude)
                {
                    inst.FollowKind = RimfxFollowKind.BoneUV;
                    inst.FollowRig = rig;
                    inst.FollowBoneIndex = boneIndex;
                    inst.FollowBoneUV = boneUV;
                    inst.FollowAngleOffset = angleOffsetDeg;
                    inst.FollowRotateWithSource = rotateWithBone;
                }
            }
        }

        private static Vector2 ResolveAirborneFlightVisualOffsetXZ(CompGiantRig rig)
        {
            if (rig == null || (rig.Pawn != null && (rig.Pawn.Dead || rig.Pawn.Downed)))
            {
                return Vector2.zero;
            }
            if (rig.IsFlyingEntry)
            {
                GiantRenderSnapshot snap = rig.LatestSnapshot;
                if (snap != null && snap.isFlyingEntry)
                {
                    return snap.flightVisualOffsetXZ;
                }
            }
            if (rig.IsCurrentlyAirborne || rig.IsAirborneOrTakingOff)
            {
                Vector2 liveOff = rig.CurrentFlightVisualOffsetXZ;
                if (liveOff.sqrMagnitude > 1e-6f)
                {
                    return liveOff;
                }
                GiantRenderSnapshot snap = rig.LatestSnapshot;
                if (snap != null && snap.isFlyingEntry)
                {
                    return snap.flightVisualOffsetXZ;
                }
            }
            GiantRenderSnapshot latest = rig.LatestSnapshot;
            if (latest != null && latest.isFlyingEntry)
            {
                return latest.flightVisualOffsetXZ;
            }
            return Vector2.zero;
        }

        private static void UpdateLayerFollowAnchor(RimfxLayerInstance inst, Map map)
        {
            switch (inst.FollowKind)
            {
                case RimfxFollowKind.ThingCenter:
                    {
                        Thing t = inst.FollowThing;
                        if (t is Pawn p && p.Dead && p.Corpse != null && p.Corpse.Spawned && p.Corpse.Map == map)
                        {
                            inst.OriginWorldPos = p.Corpse.DrawPos;
                        }
                        else if (t != null && t.Spawned && t.Map == map)
                        {
                            Vector3 livePos = t is Pawn fp ? CompGiantRig.GetSafeDrawPos(fp) : t.DrawPos;
                            if (!inst.UseGroundAltitude && t is Pawn airPawn && GiantFastLookup.TryGetRig(airPawn) is CompGiantRig airRig
                                && (airRig.IsCurrentlyAirborne || airRig.IsFlyingEntry || airRig.IsAirborneOrTakingOff || (airRig.LatestSnapshot != null && airRig.LatestSnapshot.isFlyingEntry)))
                            {
                                Vector2 visOff = ResolveAirborneFlightVisualOffsetXZ(airRig);
                                livePos.x += visOff.x;
                                livePos.z += visOff.y;
                            }
                            inst.OriginWorldPos = livePos;
                        }
                    }
                    break;

                case RimfxFollowKind.BoneUV:
                    {
                        CompGiantRig rig = inst.FollowRig;
                        Pawn p = rig?.Pawn;
                        bool validOnMap = p != null && ((p.Spawned && p.Map == map) || (p.Corpse != null && p.Corpse.Spawned && p.Corpse.Map == map));
                        if (validOnMap && rig.TryGetBoneUVWorldPos(inst.FollowBoneIndex, inst.FollowBoneUV, out Vector3 livePos, out float liveAngle))
                        {
                            if (livePos.ToIntVec3().InBounds(map))
                            {
                                if (!inst.UseGroundAltitude
                                    && (rig.IsCurrentlyAirborne || rig.IsFlyingEntry || rig.IsAirborneOrTakingOff || (rig.LatestSnapshot != null && rig.LatestSnapshot.isFlyingEntry)))
                                {
                                    Vector2 visOff = ResolveAirborneFlightVisualOffsetXZ(rig);
                                    livePos.x += visOff.x;
                                    livePos.z += visOff.y;
                                }
                                inst.OriginWorldPos = livePos;
                            }
                            if (inst.FollowRotateWithSource)
                            {
                                inst.HeadingDeg = SkeletalKinematics.NormalizeAngle360(liveAngle + inst.FollowAngleOffset);
                            }
                        }
                    }
                    break;
            }
        }

        private static Material EnsureUberMaterial()
        {
            if (cachedUberMaterial != null)
            {
                return cachedUberMaterial;
            }
            if (!UnityData.IsInMainThread)
            {
                return null;
            }

            Shader shader = GCF_VfxDefOf.GCF_RimfxUberVfx?.Shader
                ?? DefDatabase<ShaderTypeDef>.GetNamedSilentFail("GCF_RimfxUberVfx")?.Shader
                ?? ShaderDatabase.MoteGlow;
            if (shader == null)
            {
                return null;
            }

            cachedUberMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex, shader, Color.white);
            return cachedUberMaterial;
        }

        public static bool IsProtossCarrierOrInterceptor(Thing thing)
        {
            if (thing is CarrierInterceptor)
            {
                return true;
            }
            if (thing is Pawn p)
            {
                CompGiantRig rig = GiantFastLookup.TryGetRig(p);
                if (rig?.RigDef != null)
                {
                    return rig.RigDef.vfxPaletteTheme == GiantVfxPaletteTheme.ProtossShieldPlasma;
                }
                return p.def?.HasComp(typeof(CompCarrierHangar)) == true;
            }
            return false;
        }

        public static bool IsRigVfxEnabled(Thing thing, CompGiantRig rig = null)
        {
            CompGiantRig resolvedRig = rig ?? (thing is Pawn p ? GiantFastLookup.TryGetRig(p) : null);
            return resolvedRig?.RigDef == null || resolvedRig.RigDef.enableGiantVfx;
        }

        /// <summary>
        /// Derives a low-saturation, grounded atmospheric 3-tier temperature palette
        /// (parchment/mist core -> muted slate/ochre/plum haze -> smoky charcoal-umber edge)
        /// that integrates naturally with RimWorld's terrain without cartoon neon clipping.
        /// </summary>
        public static void DeriveRimfxTriad(
            Color baseColor,
            out Color coreColor,
            out Color bodyColor,
            out Color edgeColor)
        {
            Color.RGBToHSV(baseColor, out float h, out float s, out float v);
            float lum = Mathf.Clamp(v, 0.35f, 1.0f);

            float sCore = Mathf.Clamp(s * 0.20f, 0.04f, 0.16f);
            float vCore = Mathf.Clamp(0.76f + lum * 0.10f, 0.76f, 0.86f);
            coreColor = Color.HSVToRGB(h, sCore, vCore);
            coreColor.a = 1f;

            float sBody = Mathf.Clamp(s * 0.32f, 0.08f, 0.28f);
            float vBody = Mathf.Clamp(0.52f + lum * 0.12f, 0.52f, 0.65f);
            bodyColor = Color.HSVToRGB(h, sBody, vBody);
            bodyColor.a = 1f;

            float edgeHue = (h > 0.42f && h < 0.75f)
                ? Mathf.Repeat(h + 0.03f, 1f)
                : Mathf.Repeat(h - 0.025f, 1f);
            float sEdge = Mathf.Clamp(s * 0.36f, 0.10f, 0.32f);
            float vEdge = Mathf.Clamp(0.30f + lum * 0.10f, 0.30f, 0.42f);
            edgeColor = Color.HSVToRGB(edgeHue, sEdge, vEdge);
            edgeColor.a = 1f;
        }

        public static void SpawnSoftGlowBurst(Map map, Vector3 pos, float scale, Color color, float solidTime = -1f, Thing followThing = null)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }
            if (!pos.ToIntVec3().InBounds(map))
            {
                return;
            }

            float safeScale = Mathf.Max(0.35f, scale);
            int dur = (solidTime > 0f && solidTime <= 0.08f) ? 6 : 13;
            DeriveRimfxTriad(color, out Color coreCol, out Color bodyCol, out Color edgeCol);

            int startSeq = nextSpawnSeq;
            RimfxLayerInstance glow = RentLayer();
            glow.Map = map;
            glow.OriginWorldPos = pos;
            glow.HeadingDeg = NextDeterministicSeed() * 31f;
            glow.Mode = RimfxShaderMode.HarmonicAuraOrArc;
            glow.StartSizeXZ = new Vector2(safeScale * 1.15f, safeScale * 1.15f);
            glow.EndSizeXZ = new Vector2(safeScale * 1.85f, safeScale * 1.85f);
            glow.ScaleEasePower = 1.6f;
            glow.AltitudeOffset = 0.09f;
            glow.CoreColor = coreCol;
            glow.BodyColor = bodyCol;
            glow.EdgeColor = edgeCol;
            glow.EmissiveBoost = 0.78f;
            glow.SubParam = 0f;
            glow.MasterAlpha = Mathf.Clamp(color.a * 0.46f, 0.18f, 0.44f);
            glow.Seed = NextDeterministicSeed();
            glow.DurationTicks = dur;
            EnqueueLayer(glow);
            BindLayersToThing(startSeq, followThing);
        }

        public static void SpawnGroundScorchDecal(Map map, Vector3 pos, float scale, float alpha = 0.45f)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }
            if (!pos.ToIntVec3().InBounds(map))
            {
                return;
            }

            float span = Mathf.Max(1.4f, scale * 2.15f);
            RimfxLayerInstance scorch = RentLayer();
            scorch.Map = map;
            scorch.OriginWorldPos = pos;
            scorch.HeadingDeg = NextDeterministicSeed() * 37f;
            scorch.Mode = RimfxShaderMode.TectonicRiftDecal;
            scorch.StartSizeXZ = new Vector2(span * 0.86f, span * 0.86f);
            scorch.EndSizeXZ = new Vector2(span * 1.05f, span * 1.05f);
            scorch.TiltScaleZ = 0.92f;
            scorch.ScaleEasePower = 1.5f;
            scorch.UseGroundAltitude = true;
            scorch.AltitudeOffset = 0.02f;
            scorch.CoreColor = new Color(0.42f, 0.39f, 0.36f, 1f);
            scorch.BodyColor = new Color(0.30f, 0.28f, 0.26f, 1f);
            scorch.EdgeColor = new Color(0.20f, 0.19f, 0.18f, 1f);
            scorch.EmissiveBoost = 0.48f;
            scorch.SpanParam = 8f;
            scorch.MasterAlpha = Mathf.Clamp(alpha * 0.55f, 0.16f, 0.42f);
            scorch.Seed = NextDeterministicSeed();
            scorch.DurationTicks = 44;
            EnqueueLayer(scorch);
        }

        public static void BurstDirectionalSparkFlecks(
            Map map,
            Vector3 origin,
            float centerAngleDeg,
            float spreadHalfDeg,
            int count,
            float minSpeed,
            float maxSpeed,
            float scale,
            Color tint,
            Thing followThing = null)
        {
            if (map == null || count <= 0 || !UnityData.IsInMainThread)
            {
                return;
            }
            if (!origin.ToIntVec3().InBounds(map))
            {
                return;
            }

            int startSeq = nextSpawnSeq;
            float puffScale = Mathf.Clamp(scale * 1.45f, 0.65f, 1.85f);
            SpawnImpactSparkFan(
                map,
                origin,
                centerAngleDeg,
                puffScale,
                durationTicks: 12,
                baseColor: new Color(tint.r, tint.g, tint.b, Mathf.Clamp(tint.a * 0.65f, 0.28f, 0.52f)),
                deflected: false);
            BindLayersToThing(startSeq, followThing);
        }

        public static void SpawnSlashArc(
            Map map,
            Vector3 centerWorldPos,
            float headingDeg,
            float radius,
            float arcSweepDeg,
            Color color,
            int durationTicks = 18,
            float thickness = 0.88f,
            float sweepSign = 1f)
        {
            TotalTriggeredVfxCount++;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float safeRadius = Mathf.Max(0.95f, radius);
            float quadSpan = safeRadius * 2.75f;
            float dirSign = sweepSign >= 0f ? 1f : -1f;
            float sweepRad = Mathf.Clamp(arcSweepDeg, 36f, 250f) * Mathf.Deg2Rad * dirSign;
            float normWidth = Mathf.Clamp(thickness / Mathf.Max(0.6f, safeRadius * 0.95f), 0.34f, 0.56f);
            int dur = Mathf.Max(12, durationTicks);

            DeriveRimfxTriad(color, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance main = RentLayer();
            main.Map = map;
            main.OriginWorldPos = centerWorldPos;
            main.HeadingDeg = headingDeg;
            main.Mode = RimfxShaderMode.CleaveSlash;
            main.StartSizeXZ = new Vector2(quadSpan * 0.88f, quadSpan * 0.88f);
            main.EndSizeXZ = new Vector2(quadSpan * 1.12f, quadSpan * 1.12f);
            main.TiltScaleZ = 0.88f;
            main.ScaleEasePower = 1.95f;
            main.AltitudeOffset = 0.12f;
            main.CoreColor = coreCol;
            main.BodyColor = bodyCol;
            main.EdgeColor = edgeCol;
            main.EmissiveBoost = 0.95f;
            main.SpanParam = sweepRad;
            main.SubParam = normWidth;
            main.MasterAlpha = Mathf.Clamp(color.a * 0.60f, 0.32f, 0.62f);
            main.Seed = NextDeterministicSeed();
            main.DurationTicks = dur;
            EnqueueLayer(main);
        }

        public static void SpawnShockRing(
            Map map,
            Vector3 centerWorldPos,
            float startRadius,
            float endRadius,
            Color color,
            int durationTicks = 22,
            float baseWidth = 0.52f,
            Thing followThing = null)
        {
            TotalTriggeredVfxCount++;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float sR = Mathf.Max(0.24f, startRadius);
            float eR = Mathf.Max(sR + 0.50f, endRadius);
            float endQuad = eR * 2.55f;
            float startQuad = Mathf.Max(sR * 2.55f, endQuad * 0.42f);
            float widthFactor = Mathf.Clamp(baseWidth / 0.40f, 0.80f, 1.40f);

            DeriveRimfxTriad(color, out Color coreCol, out Color bodyCol, out Color edgeCol);

            int startSeq = nextSpawnSeq;
            RimfxLayerInstance ring = RentLayer();
            ring.Map = map;
            ring.OriginWorldPos = centerWorldPos;
            ring.HeadingDeg = NextDeterministicSeed() * 36f;
            ring.Mode = RimfxShaderMode.ErodedShockwave;
            ring.StartSizeXZ = new Vector2(startQuad, startQuad);
            ring.EndSizeXZ = new Vector2(endQuad, endQuad);
            ring.ScaleEasePower = 1.95f;
            ring.AltitudeOffset = 0.06f;
            ring.CoreColor = coreCol;
            ring.BodyColor = bodyCol;
            ring.EdgeColor = edgeCol;
            ring.EmissiveBoost = 0.88f;
            ring.SubParam = widthFactor;
            ring.MasterAlpha = Mathf.Clamp(color.a * 0.56f, 0.26f, 0.58f);
            ring.Seed = NextDeterministicSeed();
            ring.DurationTicks = Mathf.Max(10, durationTicks);
            EnqueueLayer(ring);
            BindLayersToThing(startSeq, followThing);
        }

        public static void SpawnTectonicRift(
            Map map,
            Vector3 centerWorldPos,
            float radius,
            Color fissureColor,
            int durationTicks = 36,
            int spokes = 11,
            bool includeShockwave = true,
            int delayTicks = 0,
            bool isDragonFlame = false)
        {
            TotalTriggeredVfxCount++;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float safeRadius = Mathf.Max(1.1f, radius);
            float endQuad = safeRadius * 2.52f;
            DeriveRimfxTriad(fissureColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance rift = RentLayer();
            rift.Map = map;
            rift.OriginWorldPos = centerWorldPos;
            rift.HeadingDeg = NextDeterministicSeed() * 37f;
            rift.Mode = RimfxShaderMode.TectonicRiftDecal;
            rift.StartSizeXZ = new Vector2(endQuad * 0.58f, endQuad * 0.58f);
            rift.EndSizeXZ = new Vector2(endQuad, endQuad);
            rift.TiltScaleZ = 0.92f;
            rift.ScaleEasePower = 2.05f;
            rift.UseGroundAltitude = true;
            rift.AltitudeOffset = 0.03f;
            rift.CoreColor = coreCol;
            rift.BodyColor = bodyCol;
            rift.EdgeColor = edgeCol;
            rift.EmissiveBoost = isDragonFlame ? 0.98f : 0.82f;
            rift.SpanParam = Mathf.Clamp(spokes, 6, 16);
            rift.SubParam = isDragonFlame ? 2.0f : 0.0f;
            rift.MasterAlpha = isDragonFlame ? Mathf.Clamp(fissureColor.a * 0.68f, 0.36f, 0.72f) : Mathf.Clamp(fissureColor.a * 0.52f, 0.26f, 0.54f);
            rift.Seed = NextDeterministicSeed();
            rift.DelayTicks = Mathf.Max(0, delayTicks);
            rift.DurationTicks = Mathf.Max(18, durationTicks);
            EnqueueLayer(rift);

            if (includeShockwave)
            {
                SpawnShockRing(
                    map,
                    centerWorldPos,
                    0.35f,
                    safeRadius * 1.10f,
                    fissureColor,
                    durationTicks: Mathf.Max(14, durationTicks - 10),
                    baseWidth: Mathf.Clamp(safeRadius * 0.16f, 0.38f, 0.65f));
            }
        }

        public static void SpawnOrdnanceTrailBeam(
            Map map,
            Vector3 startWorldPos,
            Vector3 endWorldPos,
            float width,
            int durationTicks,
            Color baseColor,
            int delayTicks = 0,
            bool isDragonFlame = false)
        {
            TotalTriggeredVfxCount++;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            Vector3 delta = endWorldPos - startWorldPos;
            delta.y = 0f;
            float len = delta.magnitude;
            if (len < 0.45f)
            {
                return;
            }

            float headingDeg = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            Vector3 mid = (startWorldPos + endWorldPos) * 0.5f;
            float safeW = Mathf.Max(0.65f, width);
            DeriveRimfxTriad(baseColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance beam = RentLayer();
            beam.Map = map;
            beam.OriginWorldPos = mid;
            beam.HeadingDeg = headingDeg;
            beam.Mode = RimfxShaderMode.AeroBeamWake;
            beam.StartSizeXZ = isDragonFlame ? new Vector2(safeW * 1.45f, len * 1.06f) : new Vector2(safeW * 0.92f, len * 1.04f);
            beam.EndSizeXZ = isDragonFlame ? new Vector2(safeW * 1.75f, len * 1.08f) : new Vector2(safeW * 1.28f, len * 1.06f);
            beam.ScaleEasePower = 1.80f;
            beam.ForwardAnchorShift = 0f;
            beam.AltitudeOffset = 0.135f;
            beam.CoreColor = coreCol;
            beam.BodyColor = bodyCol;
            beam.EdgeColor = edgeCol;
            beam.EmissiveBoost = isDragonFlame ? 1.14f : 0.92f;
            beam.SubParam = isDragonFlame ? 2.0f : Mathf.Clamp(safeW / 1.15f, 0.50f, 1.40f);
            beam.MasterAlpha = isDragonFlame ? 0.90f : Mathf.Clamp(baseColor.a * 0.54f, 0.30f, 0.56f);
            beam.Seed = NextDeterministicSeed();
            beam.DelayTicks = Mathf.Max(0, delayTicks);
            beam.DurationTicks = Mathf.Max(8, durationTicks);
            EnqueueLayer(beam);
        }

        public static void SpawnFlyingDragonFireball(
            Map map,
            Vector3 startWorldPos,
            Vector3 endWorldPos,
            int travelTicks,
            Color baseColor)
        {
            TotalTriggeredVfxCount++;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            int safeDur = Mathf.Max(4, travelTicks);
            Vector3 delta = endWorldPos - startWorldPos;
            delta.y = 0f;
            float headingDeg = delta.sqrMagnitude > 1e-4f
                ? Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg
                : 0f;
            DeriveRimfxTriad(baseColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance cometWake = RentLayer();
            cometWake.Map = map;
            cometWake.OriginWorldPos = startWorldPos;
            cometWake.EndWorldPos = endWorldPos;
            cometWake.HasLinearTrajectory = true;
            cometWake.HeadingDeg = headingDeg;
            cometWake.Mode = RimfxShaderMode.AeroBeamWake;
            cometWake.StartSizeXZ = new Vector2(1.72f, 4.10f);
            cometWake.EndSizeXZ = new Vector2(1.92f, 4.45f);
            cometWake.ScaleEasePower = 1.25f;
            cometWake.ForwardAnchorShift = -0.34f;
            cometWake.AltitudeOffset = 0.145f;
            cometWake.CoreColor = coreCol;
            cometWake.BodyColor = bodyCol;
            cometWake.EdgeColor = edgeCol;
            cometWake.EmissiveBoost = 1.12f;
            cometWake.SubParam = 2.0f;
            cometWake.MasterAlpha = 0.86f;
            cometWake.Seed = NextDeterministicSeed();
            cometWake.DurationTicks = safeDur;
            EnqueueLayer(cometWake);

            RimfxLayerInstance orb = RentLayer();
            orb.Map = map;
            orb.OriginWorldPos = startWorldPos;
            orb.EndWorldPos = endWorldPos;
            orb.HasLinearTrajectory = true;
            orb.HeadingDeg = headingDeg;
            orb.Mode = RimfxShaderMode.DetonationNova;
            orb.StartSizeXZ = new Vector2(2.38f, 2.68f);
            orb.EndSizeXZ = new Vector2(2.58f, 2.88f);
            orb.ScaleEasePower = 1.20f;
            orb.ForwardAnchorShift = 0.04f;
            orb.AltitudeOffset = 0.16f;
            orb.CoreColor = coreCol;
            orb.BodyColor = bodyCol;
            orb.EdgeColor = edgeCol;
            orb.EmissiveBoost = 1.18f;
            orb.SubParam = 2.0f;
            orb.MasterAlpha = 0.94f;
            orb.Seed = NextDeterministicSeed();
            orb.DurationTicks = safeDur;
            EnqueueLayer(orb);
        }

        public static void SpawnKhaydarinWarpPulse(
            Map map,
            Vector3 centerWorldPos,
            float radius,
            int durationTicks,
            Color primaryColor,
            bool isHexShieldWard = false,
            int delayTicks = 0,
            Thing followThing = null)
        {
            TotalTriggeredVfxCount++;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float safeRadius = Mathf.Max(0.80f, radius);
            float endSpan = safeRadius * 2.45f;
            DeriveRimfxTriad(primaryColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            if (!isHexShieldWard)
            {
                edgeCol = Color.Lerp(edgeCol, new Color(0.46f, 0.42f, 0.32f, 1f), 0.30f);
            }

            int startSeq = nextSpawnSeq;
            RimfxLayerInstance warp = RentLayer();
            warp.Map = map;
            warp.OriginWorldPos = centerWorldPos;
            warp.HeadingDeg = NextDeterministicSeed() * 36f;
            warp.Mode = RimfxShaderMode.KhaydarinWarpVortex;
            warp.StartSizeXZ = new Vector2(endSpan * 0.48f, endSpan * 0.48f);
            warp.EndSizeXZ = new Vector2(endSpan, endSpan);
            warp.TiltScaleZ = isHexShieldWard ? 0.90f : 0.85f;
            warp.ScaleEasePower = 1.95f;
            warp.AltitudeOffset = 0.14f;
            warp.CoreColor = coreCol;
            warp.BodyColor = bodyCol;
            warp.EdgeColor = edgeCol;
            warp.EmissiveBoost = isHexShieldWard ? 0.84f : 0.90f;
            warp.SubParam = isHexShieldWard ? 1.0f : 0.0f;
            warp.MasterAlpha = Mathf.Clamp(primaryColor.a * 0.52f, 0.26f, 0.54f);
            warp.Seed = NextDeterministicSeed();
            warp.DelayTicks = Mathf.Max(0, delayTicks);
            warp.DurationTicks = Mathf.Max(10, durationTicks);
            EnqueueLayer(warp);
            BindLayersToThing(startSeq, followThing);
        }

        public static void SpawnMuzzleCone(
            Map map,
            Vector3 muzzleWorldPos,
            float headingDeg,
            float length,
            float width,
            int durationTicks,
            Color baseColor,
            bool isPlasmaRail,
            bool isDragonFlame = false)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float len = Mathf.Max(1.35f, length);
            float wid = Mathf.Max(1.10f, width);
            DeriveRimfxTriad(baseColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance cone = RentLayer();
            cone.Map = map;
            cone.OriginWorldPos = muzzleWorldPos;
            cone.HeadingDeg = headingDeg;
            cone.Mode = isPlasmaRail ? RimfxShaderMode.PlasmaRailSpear : RimfxShaderMode.CannonShockCone;
            cone.StartSizeXZ = new Vector2(wid * 0.76f, len * 0.76f);
            cone.EndSizeXZ = new Vector2(wid * 1.18f, len * 1.16f);
            cone.ScaleEasePower = 2.15f;
            cone.ForwardAnchorShift = 0.32f;
            cone.AltitudeOffset = 0.14f;
            cone.CoreColor = coreCol;
            cone.BodyColor = bodyCol;
            cone.EdgeColor = edgeCol;
            cone.EmissiveBoost = isDragonFlame ? 1.12f : (isPlasmaRail ? 0.94f : 0.88f);
            cone.SubParam = isDragonFlame ? 2.0f : 0.0f;
            cone.MasterAlpha = isDragonFlame ? 0.88f : 0.58f;
            cone.Seed = NextDeterministicSeed();
            cone.DurationTicks = Mathf.Max(10, durationTicks);
            EnqueueLayer(cone);
        }

        public static void SpawnImpactSparkFan(
            Map map,
            Vector3 hitWorldPos,
            float normalDeg,
            float scale,
            int durationTicks,
            Color baseColor,
            bool deflected)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float span = Mathf.Max(1.25f, 2.35f * scale);
            DeriveRimfxTriad(baseColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance fan = RentLayer();
            fan.Map = map;
            fan.OriginWorldPos = hitWorldPos;
            fan.HeadingDeg = normalDeg;
            fan.Mode = RimfxShaderMode.ImpactSparkFan;
            fan.StartSizeXZ = new Vector2(span * 0.68f, span * 0.68f);
            fan.EndSizeXZ = new Vector2(span * 1.18f, span * 1.18f);
            fan.ScaleEasePower = 1.95f;
            fan.ForwardAnchorShift = 0.16f;
            fan.AltitudeOffset = 0.13f;
            fan.CoreColor = coreCol;
            fan.BodyColor = bodyCol;
            fan.EdgeColor = edgeCol;
            fan.EmissiveBoost = deflected ? 0.88f : 0.82f;
            fan.SubParam = deflected ? 1.0f : 0.0f;
            fan.MasterAlpha = Mathf.Clamp(baseColor.a * 0.52f, 0.26f, 0.52f);
            fan.Seed = NextDeterministicSeed();
            fan.DurationTicks = Mathf.Max(10, durationTicks);
            EnqueueLayer(fan);
        }

        public static void SpawnDetonationNova(
            Map map,
            Vector3 center,
            float radius,
            int durationTicks,
            Color baseColor,
            int delayTicks = 0,
            bool isDragonFlame = false)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float endSpan = Mathf.Max(2.3f, radius * 2.48f);
            DeriveRimfxTriad(baseColor, out Color coreCol, out Color bodyCol, out Color edgeCol);

            RimfxLayerInstance nova = RentLayer();
            nova.Map = map;
            nova.OriginWorldPos = center;
            nova.HeadingDeg = NextDeterministicSeed() * 37f;
            nova.Mode = RimfxShaderMode.DetonationNova;
            nova.StartSizeXZ = new Vector2(endSpan * 0.46f, endSpan * 0.46f);
            nova.EndSizeXZ = new Vector2(endSpan, endSpan);
            nova.ScaleEasePower = 1.95f;
            nova.AltitudeOffset = 0.15f;
            nova.CoreColor = coreCol;
            nova.BodyColor = bodyCol;
            nova.EdgeColor = edgeCol;
            nova.EmissiveBoost = isDragonFlame ? 1.14f : 1.12f;
            nova.SubParam = isDragonFlame ? 2.0f : 0.0f;
            nova.MasterAlpha = isDragonFlame ? 0.92f : Mathf.Clamp(baseColor.a * 0.84f, 0.36f, 0.88f);
            nova.Seed = NextDeterministicSeed();
            nova.DelayTicks = Mathf.Max(0, delayTicks);
            nova.DurationTicks = Mathf.Max(15, durationTicks);
            EnqueueLayer(nova);
        }

        public static void SpawnVolumetricSmokeCluster(
            Map map,
            Vector3 center,
            float radius,
            int durationTicks,
            Color smokeDark,
            Color smokeHighlight,
            float alpha = 0.52f,
            int delayTicks = 0,
            bool atomizedMist = false,
            float headingDeg = -999f,
            float aspectZScale = 1.0f)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float endSpan = Mathf.Max(1.6f, radius * (atomizedMist ? 2.75f : 2.35f));
            RimfxLayerInstance smoke = RentLayer();
            smoke.Map = map;
            smoke.OriginWorldPos = center;
            smoke.HeadingDeg = headingDeg > -900f ? headingDeg : NextDeterministicSeed() * 36f;
            smoke.Mode = RimfxShaderMode.VolumetricSmoke;
            smoke.StartSizeXZ = new Vector2(endSpan * 0.52f, endSpan * 0.52f * aspectZScale);
            smoke.EndSizeXZ = new Vector2(endSpan, endSpan * aspectZScale);
            smoke.ScaleEasePower = atomizedMist ? 1.50f : 1.65f;
            smoke.AltitudeOffset = 0.07f;
            smoke.CoreColor = smokeHighlight;
            smoke.BodyColor = Color.Lerp(smokeDark, smokeHighlight, 0.50f);
            smoke.EdgeColor = smokeDark;
            smoke.EmissiveBoost = atomizedMist ? 0.82f : 0.78f;
            smoke.SubParam = atomizedMist ? 2.0f : 0.0f;
            smoke.MasterAlpha = atomizedMist ? Mathf.Clamp(alpha, 0.14f, 0.42f) : Mathf.Clamp(alpha * 0.72f, 0.18f, 0.56f);
            smoke.Seed = NextDeterministicSeed();
            smoke.DelayTicks = Mathf.Max(0, delayTicks);
            smoke.DurationTicks = Mathf.Max(18, durationTicks);
            EnqueueLayer(smoke);
        }

        public static void SpawnFlightLandingVfx(Pawn giant, Vector3 landPos, float scale = 1.45f, float cameraShake = 0.35f)
        {
            Map map = giant?.Map;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            float safeScale = Mathf.Clamp(scale, 0.7f, 2.8f);
            bool isAnomaly = giant?.def?.defName != null &&
                (giant.def.defName.IndexOf("Void", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 giant.def.defName.IndexOf("Abyssal", StringComparison.OrdinalIgnoreCase) >= 0);

            Color riftColor = isAnomaly ? new Color(0.76f, 0.34f, 0.44f, 0.85f) : new Color(0.84f, 0.72f, 0.56f, 0.85f);
            Color windRingColor = isAnomaly ? new Color(0.68f, 0.48f, 0.58f, 0.72f) : new Color(0.80f, 0.76f, 0.68f, 0.72f);
            Color smokeDark = isAnomaly ? new Color(0.18f, 0.14f, 0.16f, 1f) : new Color(0.26f, 0.24f, 0.21f, 1f);
            Color smokeHighlight = isAnomaly ? new Color(0.56f, 0.44f, 0.48f, 1f) : new Color(0.62f, 0.57f, 0.50f, 1f);

            SpawnGroundScorchDecal(map, landPos, 2.20f * safeScale, alpha: 0.36f);
            SpawnTectonicRift(map, landPos, radius: 2.35f * safeScale, fissureColor: riftColor, durationTicks: 34, spokes: 10, includeShockwave: true);
            SpawnShockRing(map, landPos, startRadius: 0.55f, endRadius: 3.15f * safeScale, color: windRingColor, durationTicks: 24, baseWidth: 0.48f * safeScale);
            SpawnVolumetricSmokeCluster(map, landPos, radius: 2.65f * safeScale, durationTicks: 36, smokeDark: smokeDark, smokeHighlight: smokeHighlight, alpha: 0.30f, atomizedMist: true);

            if (cameraShake > 0f && map == Find.CurrentMap)
            {
                Find.CameraDriver?.shaker?.DoShake(cameraShake);
            }
        }

        public static void DrawFlightGroundShadow(Vector3 groundPos, float headingDeg, float scale, float alpha = 0.88f)
        {
            if (!UnityData.IsInMainThread)
            {
                return;
            }
            Material mat = EnsureUberMaterial();
            if (mat == null)
            {
                return;
            }
            sharedPropertyBlock ??= new MaterialPropertyBlock();
            sharedPropertyBlock.Clear();
            Color dark = Color.black;
            sharedPropertyBlock.SetColor(PropColor, Color.white);
            sharedPropertyBlock.SetColor(PropCoreColor, dark);
            sharedPropertyBlock.SetColor(PropBodyColor, dark);
            sharedPropertyBlock.SetColor(PropEdgeColor, dark);
            sharedPropertyBlock.SetVector(PropVfxParams1, new Vector4((float)RimfxShaderMode.PureBlackGroundShadow, 0.5f, 0.0f, 1.0f));
            sharedPropertyBlock.SetVector(PropVfxParams2, new Vector4(1.0f, 1.0f, 0.0f, Mathf.Clamp01(alpha)));

            Vector3 pos = new Vector3(groundPos.x, AltitudeLayer.Shadows.AltitudeFor() + 0.02f, groundPos.z);
            Quaternion rot = Quaternion.Euler(0f, headingDeg, 0f);
            Vector3 sz = new Vector3(7.4f * scale, 1f, 8.8f * scale);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, rot, sz), mat, 0, null, 0, sharedPropertyBlock);
        }

        private static bool TrySegmentIntersect2D(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2, out Vector2 hitXZ)
        {
            hitXZ = Vector2.zero;
            Vector2 r = p2 - p1;
            Vector2 s = q2 - q1;
            float rxs = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(rxs) < 1e-6f)
            {
                return false;
            }

            Vector2 qp = q1 - p1;
            float t = (qp.x * s.y - qp.y * s.x) / rxs;
            if (t < 0f || t > 1f)
            {
                return false;
            }

            float u = (qp.x * r.y - qp.y * r.x) / rxs;
            if (u < 0f || u > 1f)
            {
                return false;
            }

            hitXZ = p1 + r * t;
            return true;
        }

        private static float SegmentToSegmentDistanceSq2D(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out Vector2 midXZ)
        {
            if (TrySegmentIntersect2D(a0, a1, b0, b1, out midXZ))
            {
                return 0f;
            }

            float bestSq = float.MaxValue;
            midXZ = (a0 + b0) * 0.5f;

            Vector2 ab = b1 - b0;
            float abSq = ab.sqrMagnitude;
            for (int k = 0; k < 2; k++)
            {
                Vector2 p = k == 0 ? a0 : a1;
                float t = abSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - b0, ab) / abSq) : 0f;
                Vector2 proj = b0 + ab * t;
                float dSq = (p - proj).sqrMagnitude;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    midXZ = (p + proj) * 0.5f;
                }
            }

            Vector2 aa = a1 - a0;
            float aaSq = aa.sqrMagnitude;
            for (int k = 0; k < 2; k++)
            {
                Vector2 q = k == 0 ? b0 : b1;
                float t = aaSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(q - a0, aa) / aaSq) : 0f;
                Vector2 proj = a0 + aa * t;
                float dSq = (q - proj).sqrMagnitude;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    midXZ = (q + proj) * 0.5f;
                }
            }

            return bestSq;
        }

        /// <summary>
        /// Pure read-only 2D polyline crossing detector: returns true ONLY when the striking distal segments
        /// of two intact soft-whip centerlines physically intersect (or collide within harpoon ribbon thickness
        /// at extreme reach), outputting the exact stationary crossing coordinate without mutating any state.
        /// </summary>
        public static bool TryDetectDualWhipStrikeCrossing(
            ReadOnlySpan<Vector2> ptsA,
            ReadOnlySpan<Vector2> ptsB,
            out Vector2 crossingXZ,
            out float minDistalSegGapCells)
        {
            crossingXZ = Vector2.zero;
            minDistalSegGapCells = float.MaxValue;
            if (ptsA.Length < 6 || ptsB.Length < 6)
            {
                return false;
            }

            int maxSegA = Mathf.Clamp(Mathf.RoundToInt((ptsA.Length - 1) * 0.94f) - 1, 1, ptsA.Length - 2);
            int maxSegB = Mathf.Clamp(Mathf.RoundToInt((ptsB.Length - 1) * 0.94f) - 1, 1, ptsB.Length - 2);
            int crossStartA = Mathf.Clamp(Mathf.RoundToInt((ptsA.Length - 1) * 0.28f), 1, maxSegA);
            int crossStartB = Mathf.Clamp(Mathf.RoundToInt((ptsB.Length - 1) * 0.28f), 1, maxSegB);
            float invA = 1f / Mathf.Max(1, ptsA.Length - 1);
            float invB = 1f / Mathf.Max(1, ptsB.Length - 1);

            bool foundExactIntersect = false;
            float bestIntersectDistalU = -1f;
            Vector2 bestIntersectXZ = Vector2.zero;

            for (int i = crossStartA; i <= maxSegA; i++)
            {
                Vector2 a0 = ptsA[i];
                Vector2 a1 = ptsA[i + 1];
                float uA = (i + 0.5f) * invA;
                for (int j = crossStartB; j <= maxSegB; j++)
                {
                    if (TrySegmentIntersect2D(a0, a1, ptsB[j], ptsB[j + 1], out Vector2 hitXZ))
                    {
                        float uSum = uA + (j + 0.5f) * invB;
                        if (!foundExactIntersect || uSum > bestIntersectDistalU)
                        {
                            foundExactIntersect = true;
                            bestIntersectDistalU = uSum;
                            bestIntersectXZ = hitXZ;
                        }
                    }
                }
            }

            if (foundExactIntersect)
            {
                crossingXZ = bestIntersectXZ;
                minDistalSegGapCells = 0f;
                return true;
            }

            // Tip-collision check for extreme maximum reach where the two ~0.6-cell-wide harpoon tips kiss
            int lastSegA = ptsA.Length - 2;
            int lastSegB = ptsB.Length - 2;
            int tipStartA = Mathf.Clamp(Mathf.RoundToInt((ptsA.Length - 1) * 0.74f), 1, lastSegA);
            int tipStartB = Mathf.Clamp(Mathf.RoundToInt((ptsB.Length - 1) * 0.74f), 1, lastSegB);
            float bestGapSq = float.MaxValue;
            Vector2 bestTipMidXZ = Vector2.zero;

            for (int i = tipStartA; i <= lastSegA; i++)
            {
                Vector2 a0 = ptsA[i];
                Vector2 a1 = ptsA[i + 1];
                for (int j = tipStartB; j <= lastSegB; j++)
                {
                    float dSq = SegmentToSegmentDistanceSq2D(a0, a1, ptsB[j], ptsB[j + 1], out Vector2 midXZ);
                    if (dSq < bestGapSq)
                    {
                        bestGapSq = dSq;
                        bestTipMidXZ = midXZ;
                    }
                }
            }

            minDistalSegGapCells = Mathf.Sqrt(bestGapSq);
            if (minDistalSegGapCells <= 0.42f)
            {
                crossingXZ = bestTipMidXZ;
                return true;
            }

            return false;
        }

        public static bool TryDetectDualWhipStrikeCrossing(
            CompGiantRig comp,
            out Vector3 crossingWorldPos,
            out float minDistalSegGapCells)
        {
            crossingWorldPos = comp?.Pawn != null ? CompGiantRig.GetSafeDrawPos(comp.Pawn) : Vector3.zero;
            minDistalSegGapCells = float.MaxValue;
            if (comp == null || comp.SoftWhipSlotCount < 2)
            {
                return false;
            }

            int firstSlot = -1;
            int secondSlot = -1;
            int slots = comp.SoftWhipSlotCount;
            for (int s = 0; s < slots; s++)
            {
                if (comp.TryGetIntactSoftWhipCenterline(s, out _))
                {
                    if (firstSlot < 0) firstSlot = s;
                    else { secondSlot = s; break; }
                }
            }

            // Strictly require TWO intact soft whips; single whip can never cross ("交错").
            if (firstSlot < 0 || secondSlot < 0)
            {
                return false;
            }

            if (comp.TryGetIntactSoftWhipCenterline(firstSlot, out ReadOnlySpan<Vector2> ptsA)
                && comp.TryGetIntactSoftWhipCenterline(secondSlot, out ReadOnlySpan<Vector2> ptsB)
                && TryDetectDualWhipStrikeCrossing(ptsA, ptsB, out Vector2 crossXZ, out minDistalSegGapCells))
            {
                crossingWorldPos = new Vector3(crossXZ.x, crossingWorldPos.y, crossXZ.y);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Spawns Void Leviathan's exclusive dual-whip crossing explosion strictly at the exact
        /// crossing point where both intact soft whips intersect. Once detonated, all layers remain
        /// stationary at the crossing coordinate (FollowKind.None) so the explosion never slides forward
        /// along the travelling whip wave.
        /// </summary>
        public static void SpawnDualWhipConvergenceExplosion(
            Pawn giant,
            CompGiantRig comp,
            float scale = 1.0f,
            Vector3? verifiedCrossingPos = null)
        {
            Map map = giant?.Map ?? comp?.FootprintMap;
            if (map == null || !IsRigVfxEnabled(giant, comp) || !UnityData.IsInMainThread)
            {
                return;
            }

            // When CompAbilityEffect_AbyssalQuake is actively monitoring the live dual-whip strike,
            // defer to its synchronized crossing detonation so damage and VFX fire on the exact same tick & point.
            if (!verifiedCrossingPos.HasValue && CompAbilityEffect_AbyssalQuake.HasPendingCrossingStrike(giant))
            {
                return;
            }

            Vector3 explosionPos;
            if (verifiedCrossingPos.HasValue)
            {
                explosionPos = verifiedCrossingPos.Value;
            }
            else if (comp != null && TryDetectDualWhipStrikeCrossing(comp, out Vector3 liveCrossPos, out _))
            {
                explosionPos = liveCrossPos;
            }
            else
            {
                return;
            }

            if (!explosionPos.ToIntVec3().InBounds(map))
            {
                return;
            }

            int nowTicks = Find.TickManager?.TicksGame ?? -1;
            int pawnId = giant?.thingIDNumber ?? -1;
            if (pawnId >= 0 && nowTicks >= 0
                && pawnId == lastDualWhipExplosionPawnId
                && Mathf.Abs(nowTicks - lastDualWhipExplosionTick) <= 18)
            {
                return;
            }
            if (pawnId >= 0 && nowTicks >= 0)
            {
                lastDualWhipExplosionPawnId = pawnId;
                lastDualWhipExplosionTick = nowTicks;
            }

            TotalTriggeredVfxCount++;
            float safeScale = Mathf.Clamp(scale, 0.75f, 1.50f);

            // Low-saturation warm abyssal-ash ember & dust palette (DeriveRimfxTriad clamps S <= 0.16..0.32)
            // All layers remain stationary at explosionPos (FollowKind = None) — zero forward whip-wave drift.
            Color detCol = new Color(0.92f, 0.62f, 0.44f, 1.00f);
            Color heartCol = new Color(0.96f, 0.78f, 0.58f, 0.96f);
            Color pressureCol = new Color(0.80f, 0.58f, 0.50f, 0.94f);
            SpawnTectonicRift(map, explosionPos, radius: 4.45f * safeScale, fissureColor: pressureCol, durationTicks: 40, spokes: 10, includeShockwave: false);
            SpawnDetonationNova(map, explosionPos, radius: 4.35f * safeScale, durationTicks: 28, baseColor: detCol);
            SpawnDetonationNova(map, explosionPos, radius: 2.75f * safeScale, durationTicks: 20, baseColor: heartCol);
            SpawnShockRing(map, explosionPos, startRadius: 0.65f, endRadius: 5.05f * safeScale, color: pressureCol, durationTicks: 22, baseWidth: 0.74f * safeScale);
            SpawnVolumetricSmokeCluster(map, explosionPos, radius: 3.30f * safeScale, durationTicks: 40, smokeDark: new Color(0.14f, 0.12f, 0.12f, 1f), smokeHighlight: new Color(0.72f, 0.60f, 0.52f, 1f), alpha: 0.80f, delayTicks: 1);
        }

        public static void TickMapProceduralVfx(Map map)
        {
            if (map == null || !UnityData.IsInMainThread || ActiveLayers.Count == 0)
            {
                return;
            }

            for (int i = ActiveLayers.Count - 1; i >= 0; i--)
            {
                RimfxLayerInstance inst = ActiveLayers[i];
                if (inst == null || inst.Map != map)
                {
                    continue;
                }

                if (inst.DelayTicks > 0)
                {
                    inst.DelayTicks--;
                    continue;
                }

                inst.ElapsedTicks++;
                if (inst.ElapsedTicks >= inst.DurationTicks)
                {
                    ReleaseLayer(inst);
                    ActiveLayers.RemoveAt(i);
                }
            }
        }

        public static void DrawMapProceduralVfx(Map map)
        {
            if (map == null || !UnityData.IsInMainThread || ActiveLayers.Count == 0 || WorldRendererUtility.WorldRendered || Find.CurrentMap != map)
            {
                return;
            }

            Material mat = EnsureUberMaterial();
            Mesh planeMesh = MeshPool.plane10;
            if (mat == null || planeMesh == null)
            {
                return;
            }

            if (sharedPropertyBlock == null)
            {
                sharedPropertyBlock = new MaterialPropertyBlock();
            }

            float baseOverheadAlt = AltitudeLayer.MoteOverhead.AltitudeFor();
            float baseAirborneAlt = CompGiantRig.AirborneVfxBaseAltitudeY;
            float airborneThresholdY = CompGiantRig.LowSkimAirborneBaseAltitudeY - 0.10f;
            float baseGroundAlt = AltitudeLayer.Filth.AltitudeFor();

            CellRect viewRect = Find.CameraDriver != null
                ? Find.CameraDriver.CurrentViewRect.ExpandedBy(16)
                : CellRect.WholeMap(map);

            for (int i = 0; i < ActiveLayers.Count; i++)
            {
                RimfxLayerInstance inst = ActiveLayers[i];
                if (inst == null || inst.Map != map || inst.DelayTicks > 0)
                {
                    continue;
                }

                if (inst.FollowKind != RimfxFollowKind.None)
                {
                    UpdateLayerFollowAnchor(inst, map);
                }

                float prog = Mathf.Clamp01(inst.ElapsedTicks / (float)Mathf.Max(1, inst.DurationTicks));
                Vector3 liveOrigin = inst.HasLinearTrajectory
                    ? Vector3.Lerp(inst.OriginWorldPos, inst.EndWorldPos, prog)
                    : inst.OriginWorldPos;

                // Viewport frustum cull: skip off-screen VFX quads and shader MPB uploads.
                float maxExtent = Mathf.Max(
                    Mathf.Max(inst.StartSizeXZ.x, inst.StartSizeXZ.y),
                    Mathf.Max(inst.EndSizeXZ.x, inst.EndSizeXZ.y)) * 0.75f + Mathf.Abs(inst.ForwardAnchorShift) * 2f;
                if (liveOrigin.x + maxExtent < viewRect.minX || liveOrigin.x - maxExtent > viewRect.maxX ||
                    liveOrigin.z + maxExtent < viewRect.minZ || liveOrigin.z - maxExtent > viewRect.maxZ)
                {
                    continue;
                }

                float shaderProg = inst.HasLinearTrajectory
                    ? (0.24f + 0.08f * Mathf.Sin(inst.ElapsedTicks * 0.52f))
                    : prog;
                float scaleEase = 1f - Mathf.Pow(1f - shaderProg, inst.ScaleEasePower);
                Vector2 curSize = Vector2.Lerp(inst.StartSizeXZ, inst.EndSizeXZ, scaleEase);
                float effSizeZ = curSize.y * Mathf.Clamp(inst.TiltScaleZ, 0.35f, 1.5f);

                float rad = inst.HeadingDeg * Mathf.Deg2Rad;
                Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                bool airborneSource = !inst.UseGroundAltitude && (
                    (inst.FollowRig != null && (inst.FollowRig.IsCurrentlyAirborne || inst.FollowRig.IsFlyingEntry)) ||
                    (inst.FollowThing is Pawn ftp && (ftp.Flying || (GiantFastLookup.TryGetRig(ftp) is CompGiantRig frig && (frig.IsCurrentlyAirborne || frig.IsFlyingEntry)))) ||
                    liveOrigin.y >= airborneThresholdY);
                float alt = (inst.UseGroundAltitude ? baseGroundAlt : (airborneSource ? baseAirborneAlt : baseOverheadAlt)) + inst.AltitudeOffset;
                Vector3 drawCenter = new Vector3(liveOrigin.x, alt, liveOrigin.z)
                                   + forward * (inst.ForwardAnchorShift * effSizeZ);

                Quaternion rot = Quaternion.AngleAxis(inst.HeadingDeg, Vector3.up);
                Matrix4x4 trs = Matrix4x4.TRS(drawCenter, rot, new Vector3(curSize.x, 1f, effSizeZ));

                sharedPropertyBlock.Clear();
                sharedPropertyBlock.SetColor(PropColor, Color.white);
                sharedPropertyBlock.SetColor(PropCoreColor, inst.CoreColor);
                sharedPropertyBlock.SetColor(PropBodyColor, inst.BodyColor);
                sharedPropertyBlock.SetColor(PropEdgeColor, inst.EdgeColor);
                sharedPropertyBlock.SetVector(
                    PropVfxParams1,
                    new Vector4((float)inst.Mode, shaderProg, inst.ElapsedTicks * SecondsPerTick, inst.Seed));
                sharedPropertyBlock.SetVector(
                    PropVfxParams2,
                    new Vector4(inst.EmissiveBoost, inst.SpanParam, inst.SubParam, inst.MasterAlpha));

                Graphics.DrawMesh(planeMesh, trs, mat, 0, null, 0, sharedPropertyBlock);
            }
        }

        public static GiantHitVfxKind ResolveEffectiveHitVfxKind(Pawn giant, BoneNodeDef boneDef)
        {
            if (boneDef != null && boneDef.hitVfxPreset != GiantHitVfxKind.Auto)
            {
                return boneDef.hitVfxPreset;
            }
            if (IsProtossCarrierOrInterceptor(giant))
            {
                return GiantHitVfxKind.PlasmaFlash;
            }
            if (giant?.RaceProps != null)
            {
                if (giant.RaceProps.IsMechanoid)
                {
                    return GiantHitVfxKind.Sparks;
                }
                if (giant.RaceProps.IsAnomalyEntity || (giant is Pawn bp1 && GiantFastLookup.HasBioferriteCore(bp1)))
                {
                    return GiantHitVfxKind.BioferriteDark;
                }
            }
            return GiantHitVfxKind.FleshBlood;
        }

        public static GiantSeverVfxKind ResolveEffectiveSeverVfxKind(Pawn giant, BoneNodeDef boneDef)
        {
            if (boneDef != null && boneDef.severVfxPreset != GiantSeverVfxKind.Auto)
            {
                return boneDef.severVfxPreset;
            }
            if (IsProtossCarrierOrInterceptor(giant))
            {
                return GiantSeverVfxKind.MechExplosion;
            }
            if (giant?.RaceProps != null)
            {
                if (giant.RaceProps.IsMechanoid)
                {
                    return GiantSeverVfxKind.MechExplosion;
                }
                if (giant.RaceProps.IsAnomalyEntity || (giant is Pawn bp2 && GiantFastLookup.HasBioferriteCore(bp2)))
                {
                    return GiantSeverVfxKind.VoidRupture;
                }
            }
            return GiantSeverVfxKind.FleshSever;
        }

        private static float ComputeImpactOutwardHeading(CompGiantRig comp, int boneIndex, Vector3 hitWorldPos, Pawn fallbackPawn)
        {
            if (comp != null && (uint)boneIndex < (uint)comp.Footprint.bonePoses.Length)
            {
                Vector2 centerXZ = comp.Footprint.bonePoses[boneIndex].UVToWorldXZ(0.5f, 0.5f);
                Vector2 delta = new Vector2(hitWorldPos.x - centerXZ.x, hitWorldPos.z - centerXZ.y);
                if (delta.sqrMagnitude > 0.01f)
                {
                    return Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
                }
            }
            if (fallbackPawn != null)
            {
                Vector3 pCenter = CompGiantRig.GetSafeDrawPos(fallbackPawn);
                Vector2 delta = new Vector2(hitWorldPos.x - pCenter.x, hitWorldPos.z - pCenter.z);
                if (delta.sqrMagnitude > 0.01f)
                {
                    return Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
                }
            }
            return NextDeterministicSeed() * 37f;
        }

        public static void SpawnBoneHitVfx(
            Pawn giant,
            CompGiantRig comp,
            int boneIndex,
            Vector3 hitWorldPos,
            bool isWeakpointHit = false,
            bool isDeflected = false)
        {
            TotalTriggeredVfxCount++;
            Map map = giant?.Map ?? giant?.Corpse?.Map ?? comp?.FootprintMap;
            if (map == null || !IsRigVfxEnabled(giant, comp))
            {
                return;
            }

            BoneNodeDef bDef = (comp?.RigDef != null && (uint)boneIndex < (uint)comp.RigDef.SortedNodes.Length)
                ? comp.RigDef.SortedNodes[boneIndex]
                : null;

            float normalDeg = ComputeImpactOutwardHeading(comp, boneIndex, hitWorldPos, giant);
            bool isProtoss = IsProtossCarrierOrInterceptor(giant);
            int startSeq = nextSpawnSeq;

            if (isDeflected)
            {
                Color deflectColor = isProtoss
                    ? new Color(0.55f, 0.72f, 0.80f, 0.88f)
                    : new Color(0.76f, 0.70f, 0.56f, 0.88f);
                SpawnImpactSparkFan(map, hitWorldPos, normalDeg, scale: 1.05f, durationTicks: 14, baseColor: deflectColor, deflected: true);
                if (isProtoss)
                {
                    SpawnKhaydarinWarpPulse(map, hitWorldPos, 1.10f, 13, deflectColor, isHexShieldWard: true);
                }
                else
                {
                    SpawnShockRing(map, hitWorldPos, 0.20f, 1.08f, deflectColor, durationTicks: 12, baseWidth: 0.30f);
                }
                if (comp != null && comp.TryBindWorldPointToIntactBoneUV(hitWorldPos, boneIndex, normalDeg, out int dIdx, out Vector2 dUv, out float dAngOff))
                {
                    BindLayersToBoneUV(startSeq, comp, dIdx, dUv, dAngOff, rotateWithBone: true);
                }
                return;
            }

            GiantHitVfxKind kind = ResolveEffectiveHitVfxKind(giant, bDef);
            if (kind == GiantHitVfxKind.None)
            {
                return;
            }

            float hitScale = isWeakpointHit ? 1.28f : 0.90f;
            int hitDur = isWeakpointHit ? 15 : 12;

            switch (kind)
            {
                case GiantHitVfxKind.Sparks:
                    Color sparkCol = isWeakpointHit
                        ? new Color(0.58f, 0.74f, 0.82f, 0.90f)
                        : new Color(0.78f, 0.68f, 0.50f, 0.88f);
                    SpawnImpactSparkFan(map, hitWorldPos, normalDeg, hitScale, hitDur, sparkCol, deflected: false);
                    if (isWeakpointHit)
                    {
                        SpawnShockRing(map, hitWorldPos, 0.24f, 1.55f, sparkCol, durationTicks: 14, baseWidth: 0.34f);
                    }
                    break;

                case GiantHitVfxKind.PlasmaFlash:
                    Color plasmaCol = new Color(0.54f, 0.72f, 0.80f, 0.88f);
                    SpawnImpactSparkFan(map, hitWorldPos, normalDeg, hitScale * 1.05f, hitDur + 2, plasmaCol, deflected: isProtoss);
                    if (isProtoss)
                    {
                        SpawnKhaydarinWarpPulse(map, hitWorldPos, isWeakpointHit ? 1.50f : 1.08f, 14, plasmaCol, isHexShieldWard: true);
                    }
                    else
                    {
                        SpawnShockRing(map, hitWorldPos, 0.22f, isWeakpointHit ? 1.62f : 1.10f, plasmaCol, durationTicks: 14, baseWidth: 0.32f);
                    }
                    break;

                case GiantHitVfxKind.BioferriteDark:
                    Color bioCol = new Color(0.66f, 0.42f, 0.48f, 0.88f);
                    SpawnImpactSparkFan(map, hitWorldPos, normalDeg, hitScale, hitDur + 2, bioCol, deflected: false);
                    SpawnVolumetricSmokeCluster(map, hitWorldPos, isWeakpointHit ? 1.32f : 0.92f, 22, new Color(0.20f, 0.14f, 0.16f, 1f), new Color(0.58f, 0.42f, 0.46f, 1f), alpha: 0.48f);
                    break;

                case GiantHitVfxKind.FleshBlood:
                default:
                    Color bloodCol = new Color(0.64f, 0.38f, 0.36f, 0.88f);
                    SpawnImpactSparkFan(map, hitWorldPos, normalDeg, hitScale, hitDur, bloodCol, deflected: false);
                    SpawnVolumetricSmokeCluster(map, hitWorldPos, isWeakpointHit ? 1.24f : 0.86f, 20, new Color(0.24f, 0.14f, 0.14f, 1f), new Color(0.56f, 0.36f, 0.35f, 1f), alpha: 0.46f);
                    break;
            }

            if (comp != null && comp.TryBindWorldPointToIntactBoneUV(hitWorldPos, boneIndex, normalDeg, out int boundIdx, out Vector2 boundUv, out float angOff))
            {
                BindLayersToBoneUV(startSeq, comp, boundIdx, boundUv, angOff, rotateWithBone: true);
            }
        }

        public static void SpawnBoneSeverVfx(Pawn giant, CompGiantRig comp, int boneIndex)
        {
            TotalTriggeredVfxCount++;
            Map map = giant?.Map;
            if (map == null || comp?.RigDef == null || !IsRigVfxEnabled(giant, comp))
            {
                return;
            }

            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            if ((uint)boneIndex >= (uint)nodes.Length)
            {
                return;
            }

            BoneNodeDef bDef = nodes[boneIndex];
            GiantSeverVfxKind kind = ResolveEffectiveSeverVfxKind(giant, bDef);
            if (kind == GiantSeverVfxKind.None)
            {
                return;
            }

            Vector3 severPos = CompGiantRig.GetSafeDrawPos(giant);
            if (boneIndex < comp.Footprint.bonePoses.Length)
            {
                Vector2 jointXZ = comp.Footprint.bonePoses[boneIndex].jointWorldXZ;
                if (jointXZ.sqrMagnitude > 0.01f)
                {
                    severPos = new Vector3(jointXZ.x, severPos.y, jointXZ.y);
                }
            }

            int startSeq = nextSpawnSeq;
            SpawnSeverBurstAt(giant, map, severPos, Mathf.Max(0.55f, bDef.severVfxScale), kind);
            if (comp.TryBindWorldPointToIntactBoneUV(severPos, bDef.parentRuntimeIndex, 0f, out int stumpIdx, out Vector2 stumpUv, out _))
            {
                BindLayersToBoneUV(startSeq, comp, stumpIdx, stumpUv, rotateWithBone: false);
            }
            else
            {
                BindLayersToThing(startSeq, giant);
            }
        }

        public static void SpawnSeverBurstAt(
            Pawn giant,
            Map map,
            Vector3 severPos,
            float scale,
            GiantSeverVfxKind kind)
        {
            if (map == null || !IsRigVfxEnabled(giant))
            {
                return;
            }

            bool isProtoss = IsProtossCarrierOrInterceptor(giant);
            SpawnGroundScorchDecal(map, severPos, 1.95f * scale, alpha: 0.48f);

            switch (kind)
            {
                case GiantSeverVfxKind.MechExplosion:
                    if (isProtoss)
                    {
                        Color khaydarinCol = new Color(0.52f, 0.72f, 0.80f, 0.90f);
                        Color khalaiGold = new Color(0.76f, 0.68f, 0.48f, 0.90f);
                        SpawnKhaydarinWarpPulse(map, severPos, 2.20f * scale, durationTicks: 24, primaryColor: khaydarinCol, isHexShieldWard: false);
                        SpawnDetonationNova(map, severPos, 1.90f * scale, durationTicks: 24, baseColor: khalaiGold);
                        SpawnShockRing(map, severPos, 0.32f, 2.75f * scale, khaydarinCol, durationTicks: 21, baseWidth: 0.46f);
                    }
                    else
                    {
                        Color mechDetCol = new Color(0.76f, 0.62f, 0.42f, 0.92f);
                        SpawnDetonationNova(map, severPos, 2.05f * scale, durationTicks: 25, baseColor: mechDetCol);
                        SpawnShockRing(map, severPos, 0.30f, 2.75f * scale, new Color(0.56f, 0.68f, 0.74f, 0.88f), durationTicks: 21, baseWidth: 0.46f);
                        SpawnHarmonicAura(map, severPos, 1.95f * scale, durationTicks: 16, new Color(0.58f, 0.72f, 0.78f, 0.82f), isElectricArc: true);
                        SpawnVolumetricSmokeCluster(map, severPos, 1.85f * scale, durationTicks: 38, new Color(0.18f, 0.18f, 0.19f, 1f), new Color(0.56f, 0.50f, 0.44f, 1f), alpha: 0.54f, delayTicks: 3);
                    }
                    break;

                case GiantSeverVfxKind.VoidRupture:
                    Color voidCol = new Color(0.66f, 0.42f, 0.48f, 0.90f);
                    SpawnTectonicRift(map, severPos, 2.05f * scale, voidCol, durationTicks: 34, spokes: 9, includeShockwave: false);
                    SpawnDetonationNova(map, severPos, 2.15f * scale, durationTicks: 26, baseColor: voidCol);
                    SpawnVolumetricSmokeCluster(map, severPos, 1.95f * scale, durationTicks: 40, new Color(0.20f, 0.15f, 0.17f, 1f), new Color(0.58f, 0.44f, 0.48f, 1f), alpha: 0.52f, delayTicks: 2);
                    break;

                case GiantSeverVfxKind.FleshSever:
                default:
                    Color goreCol = new Color(0.64f, 0.38f, 0.36f, 0.90f);
                    SpawnDetonationNova(map, severPos, 1.75f * scale, durationTicks: 21, baseColor: goreCol);
                    SpawnVolumetricSmokeCluster(map, severPos, 1.65f * scale, durationTicks: 32, new Color(0.24f, 0.15f, 0.15f, 1f), new Color(0.56f, 0.38f, 0.36f, 1f), alpha: 0.50f);
                    break;
            }

            if (map == Find.CurrentMap)
            {
                Find.CameraDriver?.shaker?.DoShake(0.16f * scale);
            }
        }

        /// <summary>
        /// Ground-impact VFX triggered when a falling severed limb, wing, tail, or turret hits the terrain.
        /// </summary>
        public static void SpawnSeverImpactOnGround(
            Map map,
            Vector3 impactPos,
            float scale,
            GiantSeverVfxKind kind,
            bool isProtoss = false,
            bool isSecondaryBounce = false)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }
            float s = Mathf.Clamp(scale, 0.35f, 2.2f);
            float mult = isSecondaryBounce ? 0.55f : 1.0f;

            if (!isSecondaryBounce)
            {
                SpawnGroundScorchDecal(map, impactPos, 1.45f * s, alpha: kind == GiantSeverVfxKind.MechExplosion ? 0.44f : 0.30f);
            }

            switch (kind)
            {
                case GiantSeverVfxKind.MechExplosion:
                    if (isProtoss)
                    {
                        Color khay = new Color(0.54f, 0.74f, 0.84f, 0.85f);
                        SpawnShockRing(map, impactPos, 0.25f, 1.95f * s * mult, khay, durationTicks: isSecondaryBounce ? 12 : 18, baseWidth: 0.34f * s);
                    }
                    else
                    {
                        Color dustWarm = new Color(0.72f, 0.62f, 0.46f, 0.82f);
                        SpawnShockRing(map, impactPos, 0.24f, 2.15f * s * mult, dustWarm, durationTicks: isSecondaryBounce ? 12 : 18, baseWidth: 0.36f * s);
                        SpawnVolumetricSmokeCluster(
                            map,
                            impactPos,
                            1.35f * s * mult,
                            durationTicks: isSecondaryBounce ? 18 : 30,
                            new Color(0.22f, 0.20f, 0.18f, 1f),
                            new Color(0.58f, 0.54f, 0.48f, 1f),
                            alpha: isSecondaryBounce ? 0.34f : 0.48f);
                    }
                    break;

                case GiantSeverVfxKind.VoidRupture:
                    Color voidDust = new Color(0.58f, 0.38f, 0.44f, 0.82f);
                    SpawnShockRing(map, impactPos, 0.22f, 1.85f * s * mult, voidDust, durationTicks: isSecondaryBounce ? 12 : 18, baseWidth: 0.32f * s);
                    SpawnVolumetricSmokeCluster(
                        map,
                        impactPos,
                        1.25f * s * mult,
                        durationTicks: isSecondaryBounce ? 16 : 26,
                        new Color(0.22f, 0.15f, 0.18f, 1f),
                        new Color(0.56f, 0.42f, 0.46f, 1f),
                        alpha: isSecondaryBounce ? 0.30f : 0.44f);
                    break;

                case GiantSeverVfxKind.FleshSever:
                default:
                    Color dustCol = new Color(0.62f, 0.46f, 0.36f, 0.78f);
                    SpawnShockRing(map, impactPos, 0.22f, 1.85f * s * mult, dustCol, durationTicks: isSecondaryBounce ? 11 : 17, baseWidth: 0.32f * s);
                    SpawnVolumetricSmokeCluster(
                        map,
                        impactPos,
                        1.20f * s * mult,
                        durationTicks: isSecondaryBounce ? 14 : 24,
                        new Color(0.32f, 0.18f, 0.16f, 1f),
                        new Color(0.62f, 0.42f, 0.36f, 1f),
                        alpha: isSecondaryBounce ? 0.28f : 0.44f);
                    break;
            }

            if (!isSecondaryBounce && Find.CurrentMap == map)
            {
                Find.CameraDriver?.shaker?.DoShake(Mathf.Clamp(0.08f * s, 0.05f, 0.18f));
            }
        }

        public static void SpawnDebrisSmokeWisp(Map map, Vector3 worldPos, float scale)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }
            SpawnVolumetricSmokeCluster(
                map,
                worldPos,
                Mathf.Clamp(0.85f * scale, 0.45f, 1.35f),
                durationTicks: 24,
                new Color(0.18f, 0.18f, 0.19f, 1f),
                new Color(0.50f, 0.48f, 0.46f, 1f),
                alpha: 0.34f);
        }

        public static void SpawnMuzzleBlastVfx(
            Pawn giant,
            CompGiantRig comp,
            Vector3 muzzleWorldPos,
            float aimAngleDeg,
            float scale = 1.0f,
            Color? colorOverride = null,
            GiantAnimVfxPreset preset = GiantAnimVfxPreset.MuzzleBlast,
            bool isDragonFlame = false)
        {
            TotalTriggeredVfxCount++;
            Map map = giant?.Map;
            if (map == null || !IsRigVfxEnabled(giant, comp) || preset == GiantAnimVfxPreset.None)
            {
                return;
            }

            float safeScale = Mathf.Max(0.45f, scale);
            bool isPlasmaRail = preset == GiantAnimVfxPreset.PlasmaBurst;
            Color blastColor = colorOverride.HasValue && colorOverride.Value.a > 0.001f
                ? colorOverride.Value
                : (isPlasmaRail
                    ? new Color(0.54f, 0.74f, 0.80f, 0.88f)
                    : new Color(0.78f, 0.68f, 0.48f, 0.88f));

            int startSeq = nextSpawnSeq;
            if (isPlasmaRail)
            {
                SpawnMuzzleCone(
                    map,
                    muzzleWorldPos,
                    aimAngleDeg,
                    length: 3.15f * safeScale,
                    width: 2.05f * safeScale,
                    durationTicks: 14,
                    baseColor: blastColor,
                    isPlasmaRail: true);

                SpawnShockRing(
                    map,
                    muzzleWorldPos,
                    0.20f,
                    1.68f * safeScale,
                    blastColor,
                    durationTicks: 13,
                    baseWidth: 0.34f * safeScale);
            }
            else
            {
                SpawnMuzzleCone(
                    map,
                    muzzleWorldPos,
                    aimAngleDeg,
                    length: (isDragonFlame ? 3.85f : 3.35f) * safeScale,
                    width: (isDragonFlame ? 2.55f : 2.45f) * safeScale,
                    durationTicks: isDragonFlame ? 17 : 15,
                    baseColor: blastColor,
                    isPlasmaRail: false,
                    isDragonFlame: isDragonFlame);

                SpawnVolumetricSmokeCluster(
                    map,
                    muzzleWorldPos,
                    radius: 1.45f * safeScale,
                    durationTicks: 28,
                    smokeDark: isDragonFlame ? new Color(0.18f, 0.13f, 0.12f, 1f) : new Color(0.22f, 0.21f, 0.20f, 1f),
                    smokeHighlight: isDragonFlame ? new Color(0.68f, 0.34f, 0.26f, 1f) : new Color(0.64f, 0.60f, 0.54f, 1f),
                    alpha: isDragonFlame ? 0.28f : 0.50f,
                    delayTicks: 2,
                    atomizedMist: isDragonFlame);
            }

            if (comp != null && comp.TryBindWorldPointToIntactBoneUV(muzzleWorldPos, -1, aimAngleDeg, out int muzBoneIdx, out Vector2 muzUv, out float muzAngOff))
            {
                BindLayersToBoneUV(startSeq, comp, muzBoneIdx, muzUv, muzAngOff, rotateWithBone: true);
            }
            else
            {
                BindLayersToThing(startSeq, giant);
            }
        }

        public static void TrySpawnGiantProjectileLaunchWake(
            Projectile proj,
            Pawn giant,
            Vector3 muzzlePos,
            LocalTargetInfo intendedTarget,
            float aimAngleDeg)
        {
            Map map = giant?.Map ?? proj?.Map;
            if (map == null || proj?.def == null || !IsRigVfxEnabled(giant))
            {
                return;
            }

            string defName = proj.def.defName;
            Color wakeCol;
            float wakeWidth = 0.85f;
            float wakeLen = 5.5f;

            switch (defName)
            {
                case "GCF_Bullet_PlasmaStinger":
                    wakeCol = new Color(0.52f, 0.74f, 0.72f, 0.85f);
                    wakeWidth = 0.90f;
                    wakeLen = 5.6f;
                    break;
                case "GCF_Bullet_PanzerPzGrShell":
                    wakeCol = new Color(0.76f, 0.68f, 0.52f, 0.86f);
                    wakeWidth = 1.00f;
                    wakeLen = 7.2f;
                    break;
                case "GCF_Bullet_MechBastionPulse":
                    wakeCol = new Color(0.54f, 0.72f, 0.80f, 0.85f);
                    wakeWidth = 0.88f;
                    wakeLen = 5.6f;
                    break;
                case "GCF_Bullet_MechPlasmaBolt":
                    wakeCol = new Color(0.74f, 0.60f, 0.46f, 0.85f);
                    wakeWidth = 0.92f;
                    wakeLen = 6.0f;
                    break;
                case "GCF_Bullet_MechRailgunSlug":
                    wakeCol = new Color(0.70f, 0.48f, 0.54f, 0.88f);
                    wakeWidth = 1.18f;
                    wakeLen = 8.8f;
                    break;
                default:
                    return;
            }

            float rad = aimAngleDeg * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            if (intendedTarget.IsValid)
            {
                Vector3 tPos = intendedTarget.HasThing ? intendedTarget.Thing.DrawPos : intendedTarget.Cell.ToVector3Shifted();
                Vector3 d = tPos - muzzlePos;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist > 0.8f)
                {
                    wakeLen = Mathf.Min(wakeLen, dist * 0.82f);
                }
            }

            SpawnOrdnanceTrailBeam(map, muzzlePos, muzzlePos + fwd * wakeLen, wakeWidth, durationTicks: 11, baseColor: wakeCol);
        }

        public static void TrySpawnGiantProjectileImpactVfx(Projectile proj, Thing hitThing)
        {
            Map map = proj?.Map;
            if (map == null || proj.def == null || (proj.Launcher is Pawn launcherPawn && !IsRigVfxEnabled(launcherPawn)))
            {
                return;
            }

            string defName = proj.def.defName;
            Vector3 pos = proj.ExactPosition;
            if (!pos.ToIntVec3().InBounds(map))
            {
                return;
            }

            switch (defName)
            {
                case "GCF_Bullet_PlasmaStinger":
                    Color stingCol = new Color(0.52f, 0.74f, 0.72f, 0.86f);
                    SpawnDetonationNova(map, pos, 1.50f, durationTicks: 18, baseColor: stingCol);
                    SpawnShockRing(map, pos, 0.22f, 1.85f, stingCol, durationTicks: 15, baseWidth: 0.34f);
                    break;

                case "GCF_Bullet_MechBastionPulse":
                    Color pulseCol = new Color(0.54f, 0.72f, 0.80f, 0.86f);
                    SpawnImpactSparkFan(map, pos, NextDeterministicSeed() * 37f, 1.10f, durationTicks: 13, baseColor: pulseCol, deflected: false);
                    SpawnShockRing(map, pos, 0.20f, 1.75f, pulseCol, durationTicks: 14, baseWidth: 0.32f);
                    break;

                case "GCF_Bullet_PanzerPzGrShell":
                    Color shellCol = new Color(0.76f, 0.66f, 0.48f, 0.88f);
                    SpawnGroundScorchDecal(map, pos, 1.75f, alpha: 0.44f);
                    SpawnDetonationNova(map, pos, 1.85f, durationTicks: 21, baseColor: shellCol);
                    SpawnVolumetricSmokeCluster(map, pos, 1.35f, durationTicks: 28, new Color(0.20f, 0.19f, 0.18f, 1f), new Color(0.62f, 0.56f, 0.48f, 1f), alpha: 0.50f, delayTicks: 2);
                    break;

                case "GCF_Bullet_InterceptorBeam":
                    Color beamCol = new Color(0.54f, 0.72f, 0.80f, 0.84f);
                    SpawnKhaydarinWarpPulse(map, pos, 0.90f, durationTicks: 11, primaryColor: beamCol, isHexShieldWard: true, followThing: hitThing);
                    break;

                case "GCF_Bullet_MechPlasmaBolt":
                    Color mpCol = new Color(0.74f, 0.60f, 0.46f, 0.86f);
                    SpawnDetonationNova(map, pos, 1.58f, durationTicks: 18, baseColor: mpCol);
                    SpawnShockRing(map, pos, 0.22f, 1.95f, mpCol, durationTicks: 15, baseWidth: 0.34f);
                    break;

                case "GCF_Bullet_MechRailgunSlug":
                    Color railCol = new Color(0.70f, 0.48f, 0.54f, 0.88f);
                    SpawnGroundScorchDecal(map, pos, 2.00f, alpha: 0.46f);
                    SpawnDetonationNova(map, pos, 2.20f, durationTicks: 23, baseColor: railCol);
                    SpawnShockRing(map, pos, 0.28f, 2.75f, new Color(0.56f, 0.70f, 0.76f, 0.86f), durationTicks: 19, baseWidth: 0.42f);
                    break;
            }
        }

        public static void SpawnFootstepVfx(
            Pawn giant,
            Vector3 worldPos,
            float scale = 1.0f,
            float cameraShake = 0.04f)
        {
            TotalTriggeredVfxCount++;
            Map map = giant?.Map;
            CompGiantRig resolvedRig = GiantFastLookup.TryGetRig(giant);
            if (map == null || !IsRigVfxEnabled(giant, resolvedRig))
            {
                return;
            }

            // Units declaring <autoFootstepVfx>false</autoFootstepVfx> (or Protoss hovercraft) omit footstep dust in a purely Def-driven manner
            if (IsProtossCarrierOrInterceptor(giant) || (resolvedRig?.RigDef != null && !resolvedRig.RigDef.autoFootstepVfx))
            {
                return;
            }

            float safeScale = Mathf.Max(0.45f, scale);
            IntVec3 stepCell = worldPos.ToIntVec3();
            TerrainDef stepTerrain = stepCell.InBounds(map) ? map.terrainGrid?.TerrainAt(stepCell) : null;
            bool stepInWater = (stepTerrain != null && stepTerrain.IsWater && !GiantAquaticUtility.IsMarshTerrain(stepTerrain))
                || (resolvedRig != null && resolvedRig.IsCurrentlyInWater
                    && (resolvedRig.AquaticLocomotion?.Def == null || resolvedRig.AquaticLocomotion.Def.suppressGroundDustInWater));
            if (stepInWater)
            {
                if (worldPos.InBounds(map))
                {
                    FleckMaker.WaterSplash(worldPos, map, 1.55f * safeScale, 2.0f);
                }
                if (cameraShake > 0.001f && map == Find.CurrentMap)
                {
                    Find.CameraDriver?.shaker?.DoShake(cameraShake * 0.5f);
                }
                return;
            }

            bool isMech = giant?.RaceProps != null && giant.RaceProps.IsMechanoid;

            if (isMech)
            {
                SpawnVolumetricSmokeCluster(
                    map,
                    worldPos,
                    radius: 0.85f * safeScale,
                    durationTicks: 18,
                    smokeDark: new Color(0.24f, 0.24f, 0.25f, 1f),
                    smokeHighlight: new Color(0.54f, 0.55f, 0.56f, 1f),
                    alpha: 0.34f);
            }
            else
            {
                SpawnVolumetricSmokeCluster(
                    map,
                    worldPos,
                    radius: 1.28f * safeScale,
                    durationTicks: 22,
                    smokeDark: new Color(0.34f, 0.31f, 0.27f, 1f),
                    smokeHighlight: new Color(0.64f, 0.58f, 0.50f, 1f),
                    alpha: 0.26f,
                    atomizedMist: true);
            }

            if (cameraShake > 0.001f && map == Find.CurrentMap)
            {
                Find.CameraDriver?.shaker?.DoShake(cameraShake);
            }
        }

        public static void SpawnDeathCollapseVfx(Pawn giant, CompGiantRig comp)
        {
            TotalTriggeredVfxCount++;
            Map map = giant?.Map ?? comp?.FootprintMap;
            if (map == null || comp == null || !IsRigVfxEnabled(giant, comp))
            {
                return;
            }
            if (comp.RigDef != null && !comp.RigDef.spawnDeathCollapseVfx)
            {
                return;
            }

            Vector3 center = CompGiantRig.GetSafeDrawPos(giant);
            bool isProtoss = IsProtossCarrierOrInterceptor(giant);
            bool isMech = giant?.RaceProps != null && giant.RaceProps.IsMechanoid;
            bool isAnomaly = (giant?.RaceProps != null && giant.RaceProps.IsAnomalyEntity)
                || (giant is Pawn bioPawn && GiantFastLookup.HasBioferriteCore(bioPawn));

            float maxSpan = Mathf.Max(3.2f, Mathf.Max(comp.Footprint.tightHitBounds.Width, comp.Footprint.tightHitBounds.Height) * 0.60f);

            Color primaryNovaCol = isProtoss
                ? new Color(0.74f, 0.68f, 0.50f, 0.90f)
                : (isMech
                    ? new Color(0.74f, 0.60f, 0.44f, 0.90f)
                    : (isAnomaly
                        ? new Color(0.64f, 0.44f, 0.48f, 0.90f)
                        : new Color(0.66f, 0.56f, 0.46f, 0.88f)));

            Color outerWaveCol = isProtoss
                ? new Color(0.54f, 0.70f, 0.78f, 0.86f)
                : (isMech
                    ? new Color(0.56f, 0.68f, 0.74f, 0.86f)
                    : (isAnomaly
                        ? new Color(0.62f, 0.46f, 0.50f, 0.86f)
                        : new Color(0.62f, 0.56f, 0.48f, 0.84f)));

            SpawnTectonicRift(map, center, maxSpan * 0.88f, primaryNovaCol, durationTicks: 44, spokes: 10, includeShockwave: false);
            int startSeq = nextSpawnSeq;
            if (isProtoss)
            {
                SpawnKhaydarinWarpPulse(map, center, maxSpan * 0.80f, durationTicks: 32, primaryColor: outerWaveCol, isHexShieldWard: false);
            }
            SpawnDetonationNova(map, center, maxSpan * 0.72f, durationTicks: 32, baseColor: primaryNovaCol);
            SpawnShockRing(map, center, 0.45f, maxSpan * 1.12f, outerWaveCol, durationTicks: 28, baseWidth: 0.62f);
            BindLayersToThing(startSeq, giant);

            List<IntVec3> colCells = comp.Footprint.collisionCells;
            if (colCells != null && colCells.Count > 0)
            {
                int subCount = Mathf.Min(3, colCells.Count);
                int step = Mathf.Max(1, colCells.Count / subCount);
                int subIdx = 0;
                for (int i = 0; i < colCells.Count && subIdx < 3; i += step)
                {
                    IntVec3 c = colCells[i];
                    if (c.InBounds(map))
                    {
                        Vector3 subPos = c.ToVector3Shifted();
                        SpawnVolumetricSmokeCluster(
                            map,
                            subPos,
                            radius: maxSpan * 0.44f,
                            durationTicks: 46,
                            smokeDark: isAnomaly ? new Color(0.22f, 0.16f, 0.18f, 1f) : new Color(0.20f, 0.19f, 0.18f, 1f),
                            smokeHighlight: isAnomaly ? new Color(0.56f, 0.44f, 0.48f, 1f) : new Color(0.58f, 0.54f, 0.48f, 1f),
                            alpha: 0.52f,
                            delayTicks: 3 + subIdx * 4);
                        subIdx++;
                    }
                }
            }

            if (map == Find.CurrentMap)
            {
                Find.CameraDriver?.shaker?.DoShake(0.32f);
            }
        }

        public static void TriggerAnimEvent(
            Pawn giant,
            CompGiantRig comp,
            GiantAnimEventDef evt,
            Vector3 worldPos,
            float worldAngleDeg)
        {
            if (evt == null)
            {
                return;
            }

            TotalTriggeredAnimEventsCount++;
            TotalTriggeredVfxCount++;

            Map map = giant?.Map ?? giant?.Corpse?.Map ?? comp?.FootprintMap;
            if (map == null || !IsRigVfxEnabled(giant, comp))
            {
                return;
            }

            float scale = Mathf.Max(0.15f, evt.scale);
            IntVec3 cell = worldPos.ToIntVec3();

            if (evt.soundDef != null && cell.InBounds(map))
            {
                evt.soundDef.PlayOneShot(new TargetInfo(cell, map));
            }

            if (evt.cameraShake > 0.001f && map == Find.CurrentMap)
            {
                Find.CameraDriver?.shaker?.DoShake(evt.cameraShake);
            }

            bool isProtoss = IsProtossCarrierOrInterceptor(giant);
            bool isAnomaly = (giant?.RaceProps != null && giant.RaceProps.IsAnomalyEntity)
                || (giant is Pawn bioPawn && GiantFastLookup.HasBioferriteCore(bioPawn));
            bool isMech = giant?.RaceProps != null && giant.RaceProps.IsMechanoid;

            Color resolvedColor = evt.color;
            if (resolvedColor.r > 0.98f && resolvedColor.g > 0.98f && resolvedColor.b > 0.98f)
            {
                resolvedColor = isProtoss
                    ? new Color(0.54f, 0.72f, 0.80f, 0.88f)
                    : (isAnomaly
                        ? new Color(0.64f, 0.46f, 0.52f, 0.88f)
                        : (isMech
                            ? new Color(0.56f, 0.70f, 0.76f, 0.88f)
                            : new Color(0.68f, 0.60f, 0.48f, 0.86f)));
            }

            int eventBoneIdx = -1;
            if (comp?.RigDef != null && !string.IsNullOrEmpty(evt.boneId))
            {
                comp.RigDef.TryGetNodeIndex(evt.boneId, out eventBoneIdx);
            }

            switch (evt.vfxPreset)
            {
                case GiantAnimVfxPreset.FootstepDust:
                    SpawnFootstepVfx(giant, worldPos, scale, evt.cameraShake);
                    break;

                case GiantAnimVfxPreset.HeavyGroundSlam:
                    {
                        int startSeq = nextSpawnSeq;
                        SpawnTectonicRift(map, worldPos, 2.55f * scale, resolvedColor, durationTicks: 34, spokes: 10, includeShockwave: true);
                        SpawnVolumetricSmokeCluster(
                            map,
                            worldPos,
                            radius: 2.00f * scale,
                            durationTicks: 34,
                            smokeDark: isAnomaly ? new Color(0.22f, 0.16f, 0.18f, 1f) : new Color(0.26f, 0.24f, 0.21f, 1f),
                            smokeHighlight: isAnomaly ? new Color(0.56f, 0.44f, 0.48f, 1f) : new Color(0.60f, 0.55f, 0.48f, 1f),
                            alpha: 0.48f);
                        if (comp != null && eventBoneIdx >= 0
                            && comp.TryBindWorldPointToIntactBoneUV(worldPos, eventBoneIdx, worldAngleDeg, out int bIdx, out Vector2 bUv, out _))
                        {
                            BindLayersToBoneUV(startSeq, comp, bIdx, bUv, rotateWithBone: false);
                        }
                    }
                    break;

                case GiantAnimVfxPreset.MuzzleBlast:
                    SpawnMuzzleBlastVfx(giant, comp, worldPos, worldAngleDeg, scale, evt.color, GiantAnimVfxPreset.MuzzleBlast);
                    break;

                case GiantAnimVfxPreset.SlashArc:
                    {
                        int startSeq = nextSpawnSeq;
                        float sweepDir = (evt.boneId != null && evt.boneId.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0) ? -1f : 1f;
                        SpawnSlashArc(map, worldPos, worldAngleDeg, 2.40f * scale, 132f, resolvedColor, durationTicks: 16, thickness: 0.78f * scale, sweepSign: sweepDir);
                        if (comp != null && eventBoneIdx >= 0
                            && comp.TryBindWorldPointToIntactBoneUV(worldPos, eventBoneIdx, worldAngleDeg, out int bIdx, out Vector2 bUv, out float angOff))
                        {
                            BindLayersToBoneUV(startSeq, comp, bIdx, bUv, angOff, rotateWithBone: true);
                        }
                    }
                    break;

                case GiantAnimVfxPreset.WhipCrackSnap:
                    SpawnDualWhipConvergenceExplosion(giant, comp, scale);
                    break;

                case GiantAnimVfxPreset.PlasmaBurst:
                    if (isProtoss)
                    {
                        int startSeq = nextSpawnSeq;
                        SpawnKhaydarinWarpPulse(map, worldPos, 1.95f * scale, durationTicks: 18, primaryColor: resolvedColor, isHexShieldWard: false);
                        if (comp != null && eventBoneIdx >= 0
                            && comp.TryBindWorldPointToIntactBoneUV(worldPos, eventBoneIdx, worldAngleDeg, out int bIdx, out Vector2 bUv, out _))
                        {
                            BindLayersToBoneUV(startSeq, comp, bIdx, bUv, rotateWithBone: false);
                        }
                        else
                        {
                            BindLayersToThing(startSeq, giant);
                        }
                    }
                    else
                    {
                        SpawnMuzzleBlastVfx(giant, comp, worldPos, worldAngleDeg, scale, resolvedColor, GiantAnimVfxPreset.PlasmaBurst);
                    }
                    break;

                case GiantAnimVfxPreset.RoarShockwave:
                    {
                        int startSeq = nextSpawnSeq;
                        SpawnShockRing(map, worldPos, 0.45f, 3.85f * scale, resolvedColor, durationTicks: 24, baseWidth: 0.52f * scale);
                        RimfxLayerInstance secondWave = RentLayer();
                        DeriveRimfxTriad(resolvedColor, out Color cCore, out Color cBody, out Color cEdge);
                        secondWave.Map = map;
                        secondWave.OriginWorldPos = worldPos;
                        secondWave.HeadingDeg = NextDeterministicSeed() * 36f;
                        secondWave.Mode = RimfxShaderMode.ErodedShockwave;
                        secondWave.StartSizeXZ = new Vector2(1.5f * scale, 1.5f * scale);
                        secondWave.EndSizeXZ = new Vector2(7.2f * scale, 7.2f * scale);
                        secondWave.ScaleEasePower = 1.85f;
                        secondWave.AltitudeOffset = 0.05f;
                        secondWave.CoreColor = cCore;
                        secondWave.BodyColor = cBody;
                        secondWave.EdgeColor = cEdge;
                        secondWave.EmissiveBoost = 0.76f;
                        secondWave.SubParam = 1.05f;
                        secondWave.MasterAlpha = 0.38f;
                        secondWave.Seed = NextDeterministicSeed();
                        secondWave.DelayTicks = 5;
                        secondWave.DurationTicks = 22;
                        EnqueueLayer(secondWave);
                        if (comp != null && eventBoneIdx >= 0
                            && comp.TryBindWorldPointToIntactBoneUV(worldPos, eventBoneIdx, worldAngleDeg, out int bIdx, out Vector2 bUv, out _))
                        {
                            BindLayersToBoneUV(startSeq, comp, bIdx, bUv, rotateWithBone: false);
                        }
                        else
                        {
                            BindLayersToThing(startSeq, giant);
                        }
                    }
                    break;
            }
        }

        public static void TickPersistentBoneAura(
            Pawn giant,
            CompGiantRig comp,
            BoneNodeDef bDef,
            in BoneWorldPose pose,
            int ticksGame)
        {
            if (giant?.Map == null || bDef == null || bDef.persistentVfxPreset == GiantBoneAuraVfxKind.None || !pose.visible)
            {
                return;
            }
            if (!IsRigVfxEnabled(giant, comp) || !UnityData.IsInMainThread)
            {
                return;
            }

            int interval = Mathf.Max(8, bDef.persistentVfxIntervalTicks);
            int hashOffset = (giant.thingIDNumber * 17 + bDef.runtimeIndex * 7) & 0x7FFFFFFF;
            if ((ticksGame + hashOffset) % interval != 0)
            {
                return;
            }

            Vector2 uv = bDef.GetResolvedPersistentVfxUV(pose.activeSector);
            Vector2 worldXZ = pose.UVToWorldXZ(uv.x, uv.y);
            Vector3 worldPos = new Vector3(worldXZ.x, AltitudeLayer.MoteOverhead.AltitudeFor(), worldXZ.y);
            if (!worldPos.ToIntVec3().InBounds(giant.Map))
            {
                return;
            }

            float scale = Mathf.Max(0.35f, bDef.persistentVfxScale);
            bool isProtoss = IsProtossCarrierOrInterceptor(giant);
            TotalTriggeredVfxCount++;

            int startSeq = nextSpawnSeq;
            switch (bDef.persistentVfxPreset)
            {
                case GiantBoneAuraVfxKind.ReactorCoreGlow:
                    if (isProtoss)
                    {
                        SpawnKhaydarinWarpPulse(giant.Map, worldPos, 0.92f * scale, durationTicks: interval + 6, new Color(0.54f, 0.72f, 0.80f, 0.72f), isHexShieldWard: false);
                    }
                    else
                    {
                        SpawnHarmonicAura(giant.Map, worldPos, 1.38f * scale, durationTicks: interval + 6, new Color(0.54f, 0.70f, 0.78f, 0.70f), isElectricArc: false);
                    }
                    break;

                case GiantBoneAuraVfxKind.VoidCrucibleMist:
                    SpawnVolumetricSmokeCluster(giant.Map, worldPos, 1.05f * scale, durationTicks: interval + 12, new Color(0.22f, 0.15f, 0.18f, 1f), new Color(0.56f, 0.42f, 0.46f, 1f), alpha: 0.44f);
                    break;

                case GiantBoneAuraVfxKind.BioferritePulse:
                    SpawnHarmonicAura(giant.Map, worldPos, 1.32f * scale, durationTicks: interval + 6, new Color(0.64f, 0.44f, 0.48f, 0.70f), isElectricArc: false);
                    break;

                case GiantBoneAuraVfxKind.PlasmaVent:
                    if (isProtoss)
                    {
                        SpawnHarmonicAura(giant.Map, worldPos, 1.28f * scale, durationTicks: interval + 8, new Color(0.54f, 0.72f, 0.80f, 0.65f), isElectricArc: false);
                    }
                    else
                    {
                        SpawnVolumetricSmokeCluster(giant.Map, worldPos, 0.92f * scale, durationTicks: interval + 10, new Color(0.18f, 0.20f, 0.22f, 1f), new Color(0.52f, 0.64f, 0.70f, 1f), alpha: 0.42f);
                    }
                    break;
            }
            BindLayersToBoneUV(startSeq, comp, bDef.runtimeIndex, uv, rotateWithBone: false);
        }

        private static void SpawnHarmonicAura(
            Map map,
            Vector3 center,
            float diameter,
            int durationTicks,
            Color baseColor,
            bool isElectricArc)
        {
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            DeriveRimfxTriad(baseColor, out Color coreCol, out Color bodyCol, out Color edgeCol);
            RimfxLayerInstance layer = RentLayer();
            layer.Map = map;
            layer.OriginWorldPos = center;
            layer.HeadingDeg = NextDeterministicSeed() * 36f;
            layer.Mode = RimfxShaderMode.HarmonicAuraOrArc;
            layer.StartSizeXZ = new Vector2(diameter * 0.90f, diameter * 0.90f);
            layer.EndSizeXZ = new Vector2(diameter * 1.08f, diameter * 1.08f);
            layer.ScaleEasePower = 1.4f;
            layer.AltitudeOffset = isElectricArc ? 0.14f : 0.04f;
            layer.CoreColor = coreCol;
            layer.BodyColor = bodyCol;
            layer.EdgeColor = edgeCol;
            layer.EmissiveBoost = isElectricArc ? 0.88f : 0.76f;
            layer.SubParam = isElectricArc ? 1.0f : 0.0f;
            layer.MasterAlpha = Mathf.Clamp(baseColor.a * 0.48f, 0.20f, 0.48f);
            layer.Seed = NextDeterministicSeed();
            layer.DurationTicks = Mathf.Max(8, durationTicks);
            EnqueueLayer(layer);
        }

        public static void TickBattleDamagedBoneVfx(
            Pawn giant,
            CompGiantRig comp,
            BoneNodeDef bDef,
            in BoneWorldPose pose,
            float hpPct,
            int ticksGame)
        {
            if (giant?.Map == null || bDef == null || !pose.visible || hpPct <= 0f || hpPct >= 0.65f)
            {
                return;
            }
            if (!IsRigVfxEnabled(giant, comp) || !UnityData.IsInMainThread)
            {
                return;
            }

            bool criticallyDamaged = hpPct < 0.35f;
            int interval = criticallyDamaged ? 28 : 50;
            int hashOffset = (giant.thingIDNumber * 23 + bDef.runtimeIndex * 13) & 0x7FFFFFFF;
            if ((ticksGame + hashOffset) % interval != 0)
            {
                return;
            }

            Vector2 centerUV = new Vector2(0.5f, 0.5f);
            Vector2 centerXZ = pose.UVToWorldXZ(centerUV.x, centerUV.y);
            Vector3 worldPos = new Vector3(centerXZ.x, AltitudeLayer.MoteOverhead.AltitudeFor(), centerXZ.y);
            if (!worldPos.ToIntVec3().InBounds(giant.Map))
            {
                return;
            }

            float boneSpan = Mathf.Clamp(Mathf.Max(pose.worldSize.x, pose.worldSize.y) * 0.28f, 0.65f, 1.65f);
            GiantHitVfxKind hitKind = ResolveEffectiveHitVfxKind(giant, bDef);
            TotalTriggeredVfxCount++;

            int startSeq = nextSpawnSeq;
            if (hitKind == GiantHitVfxKind.Sparks || hitKind == GiantHitVfxKind.PlasmaFlash)
            {
                SpawnVolumetricSmokeCluster(
                    giant.Map,
                    worldPos,
                    radius: boneSpan * (criticallyDamaged ? 1.02f : 0.76f),
                    durationTicks: 32,
                    smokeDark: new Color(0.18f, 0.19f, 0.20f, 1f),
                    smokeHighlight: new Color(0.52f, 0.50f, 0.47f, 1f),
                    alpha: criticallyDamaged ? 0.50f : 0.38f);

                if (criticallyDamaged)
                {
                    SpawnHarmonicAura(
                        giant.Map,
                        worldPos,
                        diameter: boneSpan * 1.48f,
                        durationTicks: 12,
                        baseColor: new Color(0.56f, 0.72f, 0.78f, 0.82f),
                        isElectricArc: true);
                }
            }
            else if (hitKind == GiantHitVfxKind.BioferriteDark)
            {
                SpawnVolumetricSmokeCluster(
                    giant.Map,
                    worldPos,
                    radius: boneSpan * (criticallyDamaged ? 1.00f : 0.75f),
                    durationTicks: 28,
                    smokeDark: new Color(0.22f, 0.15f, 0.18f, 1f),
                    smokeHighlight: new Color(0.56f, 0.42f, 0.46f, 1f),
                    alpha: 0.44f);
            }
            else
            {
                SpawnVolumetricSmokeCluster(
                    giant.Map,
                    worldPos,
                    radius: boneSpan * 0.82f,
                    durationTicks: 24,
                    smokeDark: new Color(0.24f, 0.15f, 0.15f, 1f),
                    smokeHighlight: new Color(0.54f, 0.38f, 0.36f, 1f),
                    alpha: 0.42f);
            }
            BindLayersToBoneUV(startSeq, comp, bDef.runtimeIndex, centerUV, rotateWithBone: false);
        }
    }
}
