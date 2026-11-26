using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum GiantWoundVisualKind : byte
    {
        OrganicBloodSlash = 0,
        OrganicBloodSoak = 1,
        OrganicBurnScorch = 2,
        OrganicBandaged = 3,
        MechanoidBattleScar = 4
    }

    public struct GiantWoundDecalSpec
    {
        public int boneIndex;
        public GiantWoundVisualKind kind;
        public byte variantIndex;
        public Vector2 uvDorsal;
        public Vector2 uvEast;
        public Vector2 uvWest;
        public float localAngleDeg;
        public Vector2 baseWorldSize;
        public float surfaceLayerDelta;
        public Color tintColor;
        public float priorityScore;

        public Vector2 GetSectorUV(GiantBoneViewSector sector)
        {
            return sector switch
            {
                GiantBoneViewSector.East => uvEast,
                GiantBoneViewSector.West => uvWest,
                _ => uvDorsal
            };
        }
    }

    /// <summary>
    /// 360-degree FK-tracked wound, bloodstain, bandage, and mechanoid battle-scar overlay system
    /// plus anatomical per-limb ground blood splatter &amp; bleeding trail helpers for GCF giants.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GiantWoundBloodSystem
    {
        public const int MaxDecalsPerGiant = 48;
        public const int MaxDecalsPerBone = 6;
        public const float BloodSoakLayerDelta = 0.07f;
        public const float WoundCutLayerDelta = 0.11f;

        public static readonly string[] OrganicSlashTexPaths =
        {
            "Things/Pawn/GiantCreature/Wounds/BloodSlashA",
            "Things/Pawn/GiantCreature/Wounds/BloodSlashB",
            "Things/Pawn/GiantCreature/Wounds/BloodSlashC"
        };

        public static readonly string[] OrganicStainTexPaths =
        {
            "Things/Pawn/GiantCreature/Wounds/BloodStainA",
            "Things/Pawn/GiantCreature/Wounds/BloodStainB",
            "Things/Pawn/GiantCreature/Wounds/BloodStainC"
        };

        public static readonly string[] MechanoidScarTexPaths =
        {
            "Things/Pawn/GiantCreature/Wounds/MechScarA",
            "Things/Pawn/GiantCreature/Wounds/MechScarB",
            "Things/Pawn/GiantCreature/Wounds/MechScarC"
        };

        public static readonly string[] BandageTexPaths =
        {
            "Things/Pawn/GiantCreature/Wounds/BandageWrapA",
            "Things/Pawn/GiantCreature/Wounds/BandageWrapB"
        };

        private static readonly string[] VanillaFallbackFleshWounds =
        {
            "Things/Pawn/Wounds/WoundFleshA",
            "Things/Pawn/Wounds/WoundFleshB",
            "Things/Pawn/Wounds/WoundFleshC"
        };

        private static readonly string[] VanillaFallbackMechWounds =
        {
            "Things/Pawn/Wounds/WoundMechA",
            "Things/Pawn/Wounds/WoundMechB",
            "Things/Pawn/Wounds/WoundMechC"
        };

        private static readonly string[] VanillaFallbackBandages =
        {
            "Things/Pawn/Wounds/BandagedA",
            "Things/Pawn/Wounds/BandagedB",
            "Things/Pawn/Wounds/BandagedC"
        };

        private static MaterialPropertyBlock sharedDecalPropertyBlock;
        internal static MaterialPropertyBlock SharedDecalPropertyBlock =>
            sharedDecalPropertyBlock ??= new MaterialPropertyBlock();

        private static bool materialsPrewarmed;

        public static void PrewarmMaterialsOnMainThread()
        {
            if (materialsPrewarmed || !UnityData.IsInMainThread)
            {
                return;
            }
            Shader shader = ShaderDatabase.Transparent;
            if (shader == null)
            {
                return;
            }
            WarmArray(OrganicSlashTexPaths, shader);
            WarmArray(OrganicStainTexPaths, shader);
            WarmArray(MechanoidScarTexPaths, shader);
            WarmArray(BandageTexPaths, shader);
            WarmArray(VanillaFallbackFleshWounds, shader);
            WarmArray(VanillaFallbackMechWounds, shader);
            WarmArray(VanillaFallbackBandages, shader);
            materialsPrewarmed = true;
        }

        private static void WarmArray(string[] paths, Shader shader)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                GiantMaterialCache.WarmOnMainThread(paths[i], Color.white, shader);
            }
        }

        public static Material ResolveDecalMaterialOnMainThread(GiantWoundVisualKind kind, int variantIndex)
        {
            if (!UnityData.IsInMainThread)
            {
                return null;
            }
            PrewarmMaterialsOnMainThread();
            Shader shader = ShaderDatabase.Transparent;
            string primaryPath;
            string fallbackPath;

            switch (kind)
            {
                case GiantWoundVisualKind.OrganicBloodSoak:
                    primaryPath = OrganicStainTexPaths[PositiveMod(variantIndex, OrganicStainTexPaths.Length)];
                    fallbackPath = VanillaFallbackFleshWounds[PositiveMod(variantIndex, VanillaFallbackFleshWounds.Length)];
                    break;
                case GiantWoundVisualKind.OrganicBurnScorch:
                    primaryPath = MechanoidScarTexPaths[PositiveMod(variantIndex, MechanoidScarTexPaths.Length)];
                    fallbackPath = VanillaFallbackMechWounds[PositiveMod(variantIndex, VanillaFallbackMechWounds.Length)];
                    break;
                case GiantWoundVisualKind.OrganicBandaged:
                    primaryPath = BandageTexPaths[PositiveMod(variantIndex, BandageTexPaths.Length)];
                    fallbackPath = VanillaFallbackBandages[PositiveMod(variantIndex, VanillaFallbackBandages.Length)];
                    break;
                case GiantWoundVisualKind.MechanoidBattleScar:
                    primaryPath = MechanoidScarTexPaths[PositiveMod(variantIndex, MechanoidScarTexPaths.Length)];
                    fallbackPath = VanillaFallbackMechWounds[PositiveMod(variantIndex, VanillaFallbackMechWounds.Length)];
                    break;
                default:
                    primaryPath = OrganicSlashTexPaths[PositiveMod(variantIndex, OrganicSlashTexPaths.Length)];
                    fallbackPath = VanillaFallbackFleshWounds[PositiveMod(variantIndex, VanillaFallbackFleshWounds.Length)];
                    break;
            }

            return GiantMaterialCache.Resolve(primaryPath, Color.white, shader)
                ?? GiantMaterialCache.Resolve(fallbackPath, Color.white, shader);
        }

        public static bool IsOrganicFleshGiant(Pawn pawn)
        {
            if (pawn?.RaceProps == null)
            {
                return false;
            }
            if (pawn.RaceProps.IsMechanoid || pawn.RaceProps.IsDrone)
            {
                return false;
            }
            FleshTypeDef flesh = pawn.RaceProps.FleshType;
            if (flesh != null && !flesh.isOrganic)
            {
                return false;
            }
            if (!pawn.RaceProps.Animal && pawn.RaceProps.BloodDef == null && GiantFastLookup.TryGetCockpit(pawn) != null)
            {
                return false;
            }
            return true;
        }

        public static bool IsThermalOrScorchInjury(Hediff_Injury injury)
        {
            string name = injury?.def?.defName;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return name.IndexOf("Burn", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Frostbite", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Acid", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Plasma", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsBluntContusionInjury(Hediff_Injury injury)
        {
            string name = injury?.def?.defName;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return name.IndexOf("Bruise", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Crack", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Crush", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static Color ResolveOrganicBloodBaseColor(Pawn pawn, RotStage rotStage)
        {
            // Harmonized with vanilla RimWorld Filth_Blood (131, 34, 34) + deep arterial contrast
            Color baseBlood = new Color(0.58f, 0.075f, 0.075f, 0.91f);
            ThingDef bloodDef = pawn?.RaceProps?.BloodDef;
            if (bloodDef?.graphicData != null && bloodDef != ThingDefOf.Filth_Blood)
            {
                Color raw = bloodDef.graphicData.color;
                bool isStandardRed = raw.r > raw.g * 2.0f && raw.r > raw.b * 2.0f;
                if (!isStandardRed && (raw.r + raw.g + raw.b) > 0.05f)
                {
                    float maxCh = Mathf.Max(0.35f, Mathf.Max(raw.r, Mathf.Max(raw.g, raw.b)));
                    float gain = 0.76f / maxCh;
                    baseBlood = new Color(
                        Mathf.Clamp01(raw.r * gain),
                        Mathf.Clamp01(raw.g * gain),
                        Mathf.Clamp01(raw.b * gain),
                        0.90f);
                }
            }

            if (rotStage == RotStage.Rotting)
            {
                baseBlood = Color.Lerp(baseBlood, new Color(0.28f, 0.14f, 0.11f, 0.88f), 0.58f);
            }
            return baseBlood;
        }

        private static bool SafeIsBleeding(Hediff h)
        {
            if (h?.pawn == null || h.pawn.Dead)
            {
                return false;
            }
            if (h is Hediff_MissingPart mp && (mp.Part?.def == null || mp.def?.injuryProps == null))
            {
                return false;
            }
            if (h is Hediff_Injury inj && (inj.Part?.def == null || inj.def?.injuryProps == null))
            {
                return false;
            }
            return h.Bleeding;
        }

        public static ulong ComputeHediffWoundSignature(Pawn pawn, GiantRigDef rigDef)
        {
            if (pawn?.health?.hediffSet?.hediffs == null || rigDef == null)
            {
                return 0UL;
            }
            RotStage rotStage = pawn.GetRotStage();
            if (rotStage == RotStage.Dessicated)
            {
                return 1UL;
            }

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            ulong hash = 14695981039346656037UL;
            hash = MixHash(hash, (ulong)(uint)rigDef.shortHash);
            hash = MixHash(hash, (ulong)rotStage);
            hash = MixHash(hash, pawn.Dead ? 2UL : 1UL);

            int relevantCount = 0;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if (h is Hediff_Injury inj)
                {
                    if (inj.def != null && !inj.def.displayWound)
                    {
                        continue;
                    }
                    relevantCount++;
                    uint flags = (inj.IsTended() ? 1u : 0u)
                        | (inj.IsPermanent() ? 2u : 0u)
                        | (SafeIsBleeding(inj) ? 4u : 0u);
                    uint quantSev = (uint)Mathf.Clamp(Mathf.RoundToInt(inj.Severity * 2f), 0, 4095);
                    ulong packed = ((ulong)(uint)inj.loadID << 20) | ((ulong)quantSev << 4) | flags;
                    hash = MixHash(hash, packed);
                }
                else if (h is Hediff_MissingPart mp)
                {
                    relevantCount++;
                    bool isFresh = mp.Part != null && mp.IsFreshNonSolidExtremity;
                    uint flags = (isFresh ? 1u : 0u)
                        | (mp.IsTended() ? 2u : 0u)
                        | (SafeIsBleeding(mp) ? 4u : 0u);
                    ulong packed = ((ulong)(uint)mp.loadID << 8) | flags | 0x8000000000000000UL;
                    hash = MixHash(hash, packed);
                }
            }

            return relevantCount == 0 ? 1UL : hash;
        }

        private static ulong MixHash(ulong h, ulong val)
        {
            return (h ^ val) * 1099511628211UL;
        }

        private static int AvalancheSeed(int pawnId, int itemSalt)
        {
            unchecked
            {
                uint h = (uint)pawnId * 0x9E3779B1u ^ ((uint)itemSalt * 0x85EBCA77u + 0xC2B2AE3Du);
                h ^= h >> 16;
                h *= 0x85EBCA6Bu;
                h ^= h >> 13;
                h *= 0xC2B2AE35u;
                h ^= h >> 16;
                return (int)h;
            }
        }

        private static int PositiveMod(int value, int mod)
        {
            if (mod <= 1)
            {
                return 0;
            }
            int r = value % mod;
            return r < 0 ? r + mod : r;
        }

        /// <summary>
        /// Resolves a BodyPartRecord on a giant to a currently intact visual BoneNodeDef index,
        /// deterministically distributing wounds across multi-segment chains sharing the same BodyPartRecord.
        /// </summary>
        public static int ResolveTargetVisualBoneForPart(
            BoneNodeDef[] sortedNodes,
            ReadOnlySpan<BodyPartRecord> boneLinkedParts,
            ReadOnlySpan<bool> missingMask,
            BodyPartRecord part,
            int deterministicSeed)
        {
            if (sortedNodes == null || sortedNodes.Length == 0)
            {
                return -1;
            }
            int n = Mathf.Min(sortedNodes.Length, boneLinkedParts.Length);
            Span<int> candidates = n <= 128 ? stackalloc int[n] : new int[n];

            BodyPartRecord cur = part;
            while (cur != null)
            {
                int hitboxMatches = 0;
                for (int i = 0; i < n; i++)
                {
                    if (boneLinkedParts[i] != cur)
                    {
                        continue;
                    }
                    bool hidden = i < missingMask.Length && missingMask[i] && sortedNodes[i].hideWhenMissing;
                    if (hidden || !sortedNodes[i].contributesToHitbox)
                    {
                        continue;
                    }
                    candidates[hitboxMatches++] = i;
                }
                if (hitboxMatches > 0)
                {
                    return candidates[PositiveMod(deterministicSeed, hitboxMatches)];
                }

                int anyMatches = 0;
                for (int i = 0; i < n; i++)
                {
                    if (boneLinkedParts[i] != cur)
                    {
                        continue;
                    }
                    bool hidden = i < missingMask.Length && missingMask[i] && sortedNodes[i].hideWhenMissing;
                    if (!hidden)
                    {
                        candidates[anyMatches++] = i;
                    }
                }
                if (anyMatches > 0)
                {
                    return candidates[PositiveMod(deterministicSeed, anyMatches)];
                }

                cur = cur.parent;
            }

            for (int i = 0; i < sortedNodes.Length; i++)
            {
                bool hidden = i < missingMask.Length && missingMask[i] && sortedNodes[i].hideWhenMissing;
                if (!hidden)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Deterministically finds an interior-opaque UV point inside a bone's TextureAlphaMask so wound/blood
        /// decals never float in transparent air or spill across outer dark comic outlines.
        /// </summary>
        public static Vector2 SampleInteriorOpaqueUV(
            TextureAlphaMask mask,
            BoneNodeDef boneDef,
            GiantBoneViewSector sector,
            Vector2 decalWorldSize,
            int seed,
            Vector2? preferredAnchorUV = null)
        {
            boneDef.GetResolvedSelfParams(sector, out Vector2 boneDrawSize, out Vector2 pivotUV, out _);
            if (mask == null || !mask.HasAnyOpaquePixels)
            {
                if (preferredAnchorUV.HasValue)
                {
                    Vector2 p = preferredAnchorUV.Value;
                    return new Vector2(Mathf.Clamp(p.x, 0.15f, 0.85f), Mathf.Clamp(p.y, 0.15f, 0.85f));
                }
                float fallbackAng = (PositiveMod(seed, 360)) * Mathf.Deg2Rad;
                float fallbackR = 0.04f + 0.08f * ((PositiveMod(seed >> 8, 100)) * 0.01f);
                return new Vector2(
                    Mathf.Clamp(pivotUV.x + Mathf.Cos(fallbackAng) * fallbackR, 0.18f, 0.82f),
                    Mathf.Clamp(pivotUV.y + Mathf.Sin(fallbackAng) * fallbackR, 0.18f, 0.82f));
            }

            float minU = mask.minU;
            float maxU = mask.maxU;
            float minV = mask.minV;
            float maxV = mask.maxV;
            float centerU = (minU + maxU) * 0.5f;
            float centerV = (minV + maxV) * 0.5f;

            // Visible paint in 256x256 decal textures stays within central ~62% of quad; add 1.2% outline safety margin
            float safeW = Mathf.Max(0.05f, boneDrawSize.x);
            float safeH = Mathf.Max(0.05f, boneDrawSize.y);
            float baseHalfU = Mathf.Clamp((decalWorldSize.x * 0.30f) / safeW + 0.012f, 0.014f, 0.22f);
            float baseHalfV = Mathf.Clamp((decalWorldSize.y * 0.30f) / safeH + 0.012f, 0.014f, 0.22f);

            // Stump / socket-preferred placement: step inward from socket UV toward bone's opaque interior centroid
            if (preferredAnchorUV.HasValue)
            {
                Vector2 target = preferredAnchorUV.Value;
                Vector2 interiorGoal = FindBestOpaqueCentroid(mask, centerU, centerV, minU, maxU, minV, maxV);
                for (int step = 0; step <= 10; step++)
                {
                    float t = step * 0.10f;
                    float cu = Mathf.Lerp(target.x, interiorGoal.x, t);
                    float cv = Mathf.Lerp(target.y, interiorGoal.y, t);
                    if (IsDecalFootprintInteriorOpaque(mask, cu, cv, baseHalfU * 0.70f, baseHalfV * 0.70f))
                    {
                        return new Vector2(cu, cv);
                    }
                }
                for (int step = 0; step <= 10; step++)
                {
                    float t = step * 0.10f;
                    float cu = Mathf.Lerp(target.x, interiorGoal.x, t);
                    float cv = Mathf.Lerp(target.y, interiorGoal.y, t);
                    if (mask.IsOpaqueUV(cu, cv))
                    {
                        return new Vector2(cu, cv);
                    }
                }
            }

            float spanU = Mathf.Max(0.02f, maxU - minU);
            float spanV = Mathf.Max(0.02f, maxV - minV);
            float padMinU = minU + Mathf.Min(spanU * 0.16f, baseHalfU);
            float padMaxU = maxU - Mathf.Min(spanU * 0.16f, baseHalfU);
            float padMinV = minV + Mathf.Min(spanV * 0.16f, baseHalfV);
            float padMaxV = maxV - Mathf.Min(spanV * 0.16f, baseHalfV);

            // Pass 1: Strict O(1) 8x8 summed-area table 100% interior-opaque box check
            uint state = (uint)seed ^ 0x9E3779B9u;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                state = NextLcg(state);
                float rx = ((state & 0xFFFFu) / 65535f);
                state = NextLcg(state);
                float ry = ((state & 0xFFFFu) / 65535f);
                float cu = Mathf.Lerp(padMinU, padMaxU, rx);
                float cv = Mathf.Lerp(padMinV, padMaxV, ry);
                if (mask.ClassifyUVRect(cu - baseHalfU, cu + baseHalfU, cv - baseHalfV, cv + baseHalfV) == 2)
                {
                    return new Vector2(cu, cv);
                }
            }

            // Pass 2: Narrow curved limbs/claws where axis-aligned 8x8 block touches edge — check 9-point footprint
            float narrowHalfU = baseHalfU * 0.58f;
            float narrowHalfV = baseHalfV * 0.58f;
            for (int attempt = 0; attempt < 28; attempt++)
            {
                state = NextLcg(state);
                float rx = ((state & 0xFFFFu) / 65535f);
                state = NextLcg(state);
                float ry = ((state & 0xFFFFu) / 65535f);
                float cu = Mathf.Lerp(minU + spanU * 0.08f, maxU - spanU * 0.08f, rx);
                float cv = Mathf.Lerp(minV + spanV * 0.08f, maxV - spanV * 0.08f, ry);
                if (IsDecalFootprintInteriorOpaque(mask, cu, cv, narrowHalfU, narrowHalfV))
                {
                    return new Vector2(cu, cv);
                }
            }

            // Pass 3: Guaranteed opaque interior pixel fallback
            return FindBestOpaqueCentroid(mask, centerU, centerV, minU, maxU, minV, maxV);
        }

        private static bool IsDecalFootprintInteriorOpaque(
            TextureAlphaMask mask,
            float cu,
            float cv,
            float hu,
            float hv)
        {
            if (mask.ClassifyUVRect(cu - hu, cu + hu, cv - hv, cv + hv) == 2)
            {
                return true;
            }
            if (!mask.IsOpaqueUV(cu, cv))
            {
                return false;
            }
            float d = 0.7071f;
            return mask.IsOpaqueUV(cu - hu, cv)
                && mask.IsOpaqueUV(cu + hu, cv)
                && mask.IsOpaqueUV(cu, cv - hv)
                && mask.IsOpaqueUV(cu, cv + hv)
                && mask.IsOpaqueUV(cu - hu * d, cv - hv * d)
                && mask.IsOpaqueUV(cu + hu * d, cv - hv * d)
                && mask.IsOpaqueUV(cu - hu * d, cv + hv * d)
                && mask.IsOpaqueUV(cu + hu * d, cv + hv * d);
        }

        private static Vector2 FindBestOpaqueCentroid(
            TextureAlphaMask mask,
            float centerU,
            float centerV,
            float minU,
            float maxU,
            float minV,
            float maxV)
        {
            if (mask.IsOpaqueUV(centerU, centerV))
            {
                return new Vector2(centerU, centerV);
            }
            for (int gy = 1; gy <= 7; gy++)
            {
                float v = Mathf.Lerp(minV, maxV, gy / 8f);
                for (int gx = 1; gx <= 7; gx++)
                {
                    float u = Mathf.Lerp(minU, maxU, gx / 8f);
                    if (mask.IsOpaqueUV(u, v))
                    {
                        return new Vector2(u, v);
                    }
                }
            }
            return new Vector2(centerU, centerV);
        }

        private static uint NextLcg(uint state)
        {
            unchecked
            {
                return state * 1664525u + 1013904223u;
            }
        }

        private static Vector2 ComputeLimbOpaqueWorldSpan(BoneNodeDef boneDef, TextureAlphaMask mask)
        {
            Vector2 ds = boneDef.drawSize;
            if (mask != null && mask.HasAnyOpaquePixels)
            {
                float uSpan = Mathf.Clamp(mask.maxU - mask.minU, 0.08f, 1f);
                float vSpan = Mathf.Clamp(mask.maxV - mask.minV, 0.08f, 1f);
                return new Vector2(Mathf.Max(0.35f, ds.x * uSpan), Mathf.Max(0.35f, ds.y * vSpan));
            }
            return new Vector2(Mathf.Max(0.5f, ds.x * 0.45f), Mathf.Max(0.5f, ds.y * 0.45f));
        }

        /// <summary>
        /// Rebuilds the deterministic per-bone wound &amp; bloodstain decal list for a giant rig.
        /// Pure C# computation (zero Unity API calls) — safe to run in headless unit tests as well as live play.
        /// </summary>
        public static void BuildDecalSpecsForGiant(
            Pawn pawn,
            CompGiantRig comp,
            List<GiantWoundDecalSpec> outSpecs)
        {
            outSpecs.Clear();
            if (pawn?.health?.hediffSet?.hediffs == null || comp?.RigDef == null)
            {
                return;
            }
            RotStage rotStage = pawn.GetRotStage();
            if (rotStage == RotStage.Dessicated)
            {
                return;
            }

            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            int nodeCount = nodes.Length;
            if (nodeCount == 0)
            {
                return;
            }

            ReadOnlySpan<BodyPartRecord> boneParts = comp.LinkedBodyPartsSpan;
            ReadOnlySpan<bool> missingMask = comp.PartMissingMaskSpan;
            bool isOrganic = IsOrganicFleshGiant(pawn);
            Color bloodBase = isOrganic ? ResolveOrganicBloodBaseColor(pawn, rotStage) : Color.white;
            int pawnId = pawn.thingIDNumber;

            Span<int> perBoneCount = nodeCount <= 128 ? stackalloc int[nodeCount] : new int[nodeCount];
            perBoneCount.Clear();

            // 1. Severed limb detachment stumps: place arterial bloodstain / mech crater on intact parent bone socket
            for (int childIdx = 1; childIdx < nodeCount; childIdx++)
            {
                bool childMissing = childIdx < missingMask.Length && missingMask[childIdx] && nodes[childIdx].hideWhenMissing;
                if (!childMissing)
                {
                    continue;
                }
                int parentIdx = nodes[childIdx].parentRuntimeIndex;
                if ((uint)parentIdx >= (uint)nodeCount)
                {
                    continue;
                }
                bool parentHidden = parentIdx < missingMask.Length && missingMask[parentIdx] && nodes[parentIdx].hideWhenMissing;
                if (parentHidden || perBoneCount[parentIdx] >= MaxDecalsPerBone)
                {
                    continue;
                }

                BoneNodeDef parentDef = nodes[parentIdx];
                BoneNodeDef childDef = nodes[childIdx];
                TextureAlphaMask dMask = comp.GetSectorAlphaMask(parentIdx, GiantBoneViewSector.Dorsal);
                TextureAlphaMask eMask = comp.GetSectorAlphaMask(parentIdx, GiantBoneViewSector.East);
                TextureAlphaMask wMask = comp.GetSectorAlphaMask(parentIdx, GiantBoneViewSector.West);

                Vector2 span = ComputeLimbOpaqueWorldSpan(parentDef, dMask);
                float limbMin = Mathf.Min(span.x, span.y);
                float stumpSize = Mathf.Clamp(limbMin * 0.38f, 0.50f, 1.55f);
                Vector2 decalSize = new Vector2(stumpSize, stumpSize);
                int seed = AvalancheSeed(pawnId, 0x57000 + childIdx * 977);

                Vector2 uvD = SampleInteriorOpaqueUV(
                    dMask, parentDef, GiantBoneViewSector.Dorsal, decalSize, seed,
                    childDef.GetResolvedParentSocketUV(GiantBoneViewSector.Dorsal));
                Vector2 uvE = ReferenceEquals(eMask, dMask)
                    ? uvD
                    : SampleInteriorOpaqueUV(
                        eMask, parentDef, GiantBoneViewSector.East, decalSize, seed ^ 0x1111,
                        childDef.GetResolvedParentSocketUV(GiantBoneViewSector.East));
                Vector2 uvW = ReferenceEquals(wMask, dMask)
                    ? uvD
                    : SampleInteriorOpaqueUV(
                        wMask, parentDef, GiantBoneViewSector.West, decalSize, seed ^ 0x2222,
                        childDef.GetResolvedParentSocketUV(GiantBoneViewSector.West));

                float angle = PositiveMod(seed >> 3, 360);
                if (isOrganic)
                {
                    Color stumpCol = new Color(
                        bloodBase.r * 0.90f,
                        bloodBase.g * 0.85f,
                        bloodBase.b * 0.85f,
                        0.95f);
                    outSpecs.Add(new GiantWoundDecalSpec
                    {
                        boneIndex = parentIdx,
                        kind = GiantWoundVisualKind.OrganicBloodSoak,
                        variantIndex = (byte)PositiveMod(seed, OrganicStainTexPaths.Length),
                        uvDorsal = uvD,
                        uvEast = uvE,
                        uvWest = uvW,
                        localAngleDeg = angle,
                        baseWorldSize = decalSize,
                        surfaceLayerDelta = BloodSoakLayerDelta,
                        tintColor = stumpCol,
                        priorityScore = 1000f + stumpSize
                    });
                }
                else
                {
                    outSpecs.Add(new GiantWoundDecalSpec
                    {
                        boneIndex = parentIdx,
                        kind = GiantWoundVisualKind.MechanoidBattleScar,
                        variantIndex = 1, // Crater scar
                        uvDorsal = uvD,
                        uvEast = uvE,
                        uvWest = uvW,
                        localAngleDeg = angle,
                        baseWorldSize = decalSize * 0.92f,
                        surfaceLayerDelta = WoundCutLayerDelta,
                        tintColor = new Color(0.32f, 0.30f, 0.28f, 0.94f),
                        priorityScore = 1000f + stumpSize
                    });
                }
                perBoneCount[parentIdx]++;
            }

            // 2. Active Hediff_Injury decals across intact bones
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int hIdx = 0; hIdx < hediffs.Count; hIdx++)
            {
                if (!(hediffs[hIdx] is Hediff_Injury injury))
                {
                    continue;
                }
                if (injury.def != null && !injury.def.displayWound)
                {
                    continue;
                }
                if (injury.Severity <= 0.15f)
                {
                    continue;
                }

                int seed = AvalancheSeed(pawnId, injury.loadID);
                int boneIdx = ResolveTargetVisualBoneForPart(nodes, boneParts, missingMask, injury.Part, seed);
                if ((uint)boneIdx >= (uint)nodeCount)
                {
                    continue;
                }

                BoneNodeDef boneDef = nodes[boneIdx];
                TextureAlphaMask dMask = comp.GetSectorAlphaMask(boneIdx, GiantBoneViewSector.Dorsal);
                TextureAlphaMask eMask = comp.GetSectorAlphaMask(boneIdx, GiantBoneViewSector.East);
                TextureAlphaMask wMask = comp.GetSectorAlphaMask(boneIdx, GiantBoneViewSector.West);

                Vector2 span = ComputeLimbOpaqueWorldSpan(boneDef, dMask);
                float limbMin = Mathf.Min(span.x, span.y);
                float sevFactor = Mathf.Clamp(0.70f + Mathf.Sqrt(Mathf.Max(0.5f, injury.Severity)) * 0.14f, 0.72f, 1.42f);
                float baseRadiusCells = Mathf.Clamp(limbMin * 0.25f * sevFactor, 0.36f, 1.32f);
                Vector2 cutSize = new Vector2(baseRadiusCells * 1.14f, baseRadiusCells * 0.92f);

                Vector2 uvD = SampleInteriorOpaqueUV(dMask, boneDef, GiantBoneViewSector.Dorsal, cutSize, seed);
                Vector2 uvE = ReferenceEquals(eMask, dMask)
                    ? uvD
                    : SampleInteriorOpaqueUV(eMask, boneDef, GiantBoneViewSector.East, cutSize, AvalancheSeed(seed, 0x3333));
                Vector2 uvW = ReferenceEquals(wMask, dMask)
                    ? uvD
                    : SampleInteriorOpaqueUV(wMask, boneDef, GiantBoneViewSector.West, cutSize, AvalancheSeed(seed, 0x4444));

                float localAng = PositiveMod(AvalancheSeed(seed, 0x7F4A7C15), 360);
                bool isTended = injury.IsTended();
                bool isPermanent = injury.IsPermanent();
                bool isBleeding = SafeIsBleeding(injury);
                float prio = injury.Severity + (isBleeding ? 35f : 0f) + (isTended ? -5f : 0f);

                int primaryVarSeed = AvalancheSeed(seed, 0x2244);
                if (!isOrganic)
                {
                    outSpecs.Add(new GiantWoundDecalSpec
                    {
                        boneIndex = boneIdx,
                        kind = GiantWoundVisualKind.MechanoidBattleScar,
                        variantIndex = (byte)PositiveMod(primaryVarSeed, MechanoidScarTexPaths.Length),
                        uvDorsal = uvD,
                        uvEast = uvE,
                        uvWest = uvW,
                        localAngleDeg = localAng,
                        baseWorldSize = cutSize,
                        surfaceLayerDelta = WoundCutLayerDelta,
                        tintColor = new Color(0.36f, 0.34f, 0.32f, Mathf.Clamp(0.76f + injury.Severity * 0.015f, 0.76f, 0.95f)),
                        priorityScore = prio
                    });
                    continue;
                }

                if (isTended && !isPermanent)
                {
                    Color bandageCol = isBleeding
                        ? Color.Lerp(new Color(0.92f, 0.88f, 0.80f, 0.95f), bloodBase, 0.25f)
                        : new Color(0.92f, 0.88f, 0.80f, 0.95f);
                    outSpecs.Add(new GiantWoundDecalSpec
                    {
                        boneIndex = boneIdx,
                        kind = GiantWoundVisualKind.OrganicBandaged,
                        variantIndex = (byte)PositiveMod(primaryVarSeed, BandageTexPaths.Length),
                        uvDorsal = uvD,
                        uvEast = uvE,
                        uvWest = uvW,
                        localAngleDeg = localAng,
                        baseWorldSize = new Vector2(baseRadiusCells, baseRadiusCells),
                        surfaceLayerDelta = WoundCutLayerDelta + 0.01f,
                        tintColor = bandageCol,
                        priorityScore = prio
                    });
                    continue;
                }

                if (IsThermalOrScorchInjury(injury))
                {
                    outSpecs.Add(new GiantWoundDecalSpec
                    {
                        boneIndex = boneIdx,
                        kind = GiantWoundVisualKind.OrganicBurnScorch,
                        variantIndex = (byte)PositiveMod(primaryVarSeed, MechanoidScarTexPaths.Length),
                        uvDorsal = uvD,
                        uvEast = uvE,
                        uvWest = uvW,
                        localAngleDeg = localAng,
                        baseWorldSize = cutSize,
                        surfaceLayerDelta = WoundCutLayerDelta,
                        tintColor = new Color(0.20f, 0.14f, 0.12f, 0.88f),
                        priorityScore = prio
                    });
                    continue;
                }

                // Organic bleeding / flesh laceration or heavy contusion:
                // Add visceral wet blood-soak blotch under bleeding or medium/heavy flesh wounds!
                bool isContusion = IsBluntContusionInjury(injury) && !isBleeding;
                if (!isPermanent && (isBleeding || injury.Severity >= 7f) && !isContusion)
                {
                    float soakScale = Mathf.Clamp(baseRadiusCells * (isBleeding ? 1.32f : 1.15f), 0.46f, 1.68f);
                    Color soakColor = new Color(
                        bloodBase.r * 0.88f,
                        bloodBase.g * 0.88f,
                        bloodBase.b * 0.88f,
                        Mathf.Clamp(0.66f + injury.Severity * 0.012f, 0.66f, 0.86f));
                    outSpecs.Add(new GiantWoundDecalSpec
                    {
                        boneIndex = boneIdx,
                        kind = GiantWoundVisualKind.OrganicBloodSoak,
                        variantIndex = (byte)PositiveMod(AvalancheSeed(seed, 0x51A1), OrganicStainTexPaths.Length),
                        uvDorsal = uvD,
                        uvEast = uvE,
                        uvWest = uvW,
                        localAngleDeg = PositiveMod(AvalancheSeed(seed, 0x9931), 360),
                        baseWorldSize = new Vector2(soakScale, soakScale),
                        surfaceLayerDelta = BloodSoakLayerDelta,
                        tintColor = soakColor,
                        priorityScore = prio - 0.1f
                    });
                }

                Color woundColor;
                GiantWoundVisualKind primaryKind;
                byte primaryVar;
                if (isPermanent)
                {
                    woundColor = new Color(0.34f, 0.20f, 0.19f, 0.78f);
                    primaryKind = GiantWoundVisualKind.OrganicBloodSlash;
                    primaryVar = (byte)PositiveMod(primaryVarSeed, OrganicSlashTexPaths.Length);
                }
                else if (isContusion)
                {
                    woundColor = Color.Lerp(bloodBase, new Color(0.26f, 0.12f, 0.20f, 0.80f), 0.55f);
                    primaryKind = GiantWoundVisualKind.OrganicBloodSoak;
                    primaryVar = (byte)PositiveMod(primaryVarSeed, OrganicStainTexPaths.Length);
                }
                else
                {
                    woundColor = new Color(
                        bloodBase.r,
                        bloodBase.g,
                        bloodBase.b,
                        Mathf.Clamp(0.82f + injury.Severity * 0.01f, 0.82f, 0.95f));
                    primaryKind = GiantWoundVisualKind.OrganicBloodSlash;
                    primaryVar = (byte)PositiveMod(primaryVarSeed, OrganicSlashTexPaths.Length);
                }

                outSpecs.Add(new GiantWoundDecalSpec
                {
                    boneIndex = boneIdx,
                    kind = primaryKind,
                    variantIndex = primaryVar,
                    uvDorsal = uvD,
                    uvEast = uvE,
                    uvWest = uvW,
                    localAngleDeg = localAng,
                    baseWorldSize = cutSize,
                    surfaceLayerDelta = WoundCutLayerDelta,
                    tintColor = woundColor,
                    priorityScore = prio
                });
            }

            if (outSpecs.Count <= 1)
            {
                return;
            }

            // Sort high-priority wounds first, enforce per-bone and total decal budgets, then order by layerDelta for clean alpha blending
            outSpecs.Sort(CompareByPriorityDesc);
            int writeIdx = 0;
            perBoneCount.Clear();
            for (int i = 0; i < outSpecs.Count && writeIdx < MaxDecalsPerGiant; i++)
            {
                GiantWoundDecalSpec spec = outSpecs[i];
                int bIdx = spec.boneIndex;
                if ((uint)bIdx < (uint)perBoneCount.Length)
                {
                    if (perBoneCount[bIdx] >= MaxDecalsPerBone)
                    {
                        continue;
                    }
                    perBoneCount[bIdx]++;
                }
                outSpecs[writeIdx++] = spec;
            }
            if (writeIdx < outSpecs.Count)
            {
                outSpecs.RemoveRange(writeIdx, outSpecs.Count - writeIdx);
            }
            outSpecs.Sort(CompareByLayerAsc);
        }

        private static int CompareByPriorityDesc(GiantWoundDecalSpec a, GiantWoundDecalSpec b)
        {
            int cmp = b.priorityScore.CompareTo(a.priorityScore);
            return cmp != 0 ? cmp : a.boneIndex.CompareTo(b.boneIndex);
        }

        private static int CompareByLayerAsc(GiantWoundDecalSpec a, GiantWoundDecalSpec b)
        {
            int cmp = a.surfaceLayerDelta.CompareTo(b.surfaceLayerDelta);
            return cmp != 0 ? cmp : a.boneIndex.CompareTo(b.boneIndex);
        }
    }

    /// <summary>
    /// Anatomical per-limb ground blood splatter (on melee/ranged hit, severing, corpse collapse)
    /// and un-diluted periodic bleeding drip trail for grounded organic giants.
    /// </summary>
    public static class GiantBloodFilthUtility
    {
        public static int TotalSpawnedGiantBloodFilthCount { get; private set; }

        public static void ResetCountersForTesting()
        {
            TotalSpawnedGiantBloodFilthCount = 0;
        }

        public static bool CanDropGroundBloodNow(Pawn pawn, CompGiantRig comp)
        {
            if (pawn == null || comp == null || !pawn.Spawned || pawn.Map == null)
            {
                return false;
            }
            if (pawn.RaceProps?.BloodDef == null || !GiantWoundBloodSystem.IsOrganicFleshGiant(pawn))
            {
                return false;
            }
            if (pawn.health != null && !pawn.Dead && !pawn.health.CanBleed)
            {
                return false;
            }
            if (comp.IsCurrentlyAirborne || comp.IsFlyingEntry || GiantMotionOverrideRegistry.HasActiveHighAltitudeOverride(pawn))
            {
                return false;
            }
            return true;
        }

        public static IntVec3 ResolveBoneWorldGroundCell(Pawn pawn, CompGiantRig comp, int boneIndex, float jitterRadius = 0.65f)
        {
            Map map = pawn.Map;
            IntVec3 fallback = pawn.PositionHeld;
            ReadOnlySpan<BoneWorldPose> poses = comp.FootprintBonePosesSpan;
            if ((uint)boneIndex < (uint)poses.Length && poses[boneIndex].visible)
            {
                Vector2 xz = poses[boneIndex].quadCenterWorldXZ;
                if (jitterRadius > 0.01f)
                {
                    float ang = Rand.Range(0f, Mathf.PI * 2f);
                    float r = Rand.Range(0f, jitterRadius);
                    xz.x += Mathf.Cos(ang) * r;
                    xz.y += Mathf.Sin(ang) * r;
                }
                IntVec3 candidate = new Vector3(xz.x, 0f, xz.y).ToIntVec3();
                if (map != null && candidate.InBounds(map))
                {
                    return candidate;
                }
            }
            return fallback;
        }

        public static int SpawnBloodSplatterAtCell(Pawn sourcePawn, Map map, IntVec3 centerCell, int count, float scatterRadius = 1.35f)
        {
            ThingDef bloodDef = sourcePawn?.RaceProps?.BloodDef;
            if (bloodDef == null || map == null || count <= 0 || !centerCell.InBounds(map))
            {
                return 0;
            }

            int spawned = 0;
            string sourceLabel = sourcePawn.LabelIndefinite();
            for (int i = 0; i < count; i++)
            {
                IntVec3 targetCell = centerCell;
                if (i > 0 && scatterRadius > 0.2f)
                {
                    float ang = Rand.Range(0f, Mathf.PI * 2f);
                    float dist = Rand.Range(0.35f, scatterRadius);
                    IntVec3 offsetCell = new IntVec3(
                        Mathf.RoundToInt(Mathf.Cos(ang) * dist),
                        0,
                        Mathf.RoundToInt(Mathf.Sin(ang) * dist));
                    IntVec3 cand = centerCell + offsetCell;
                    if (cand.InBounds(map) && cand.Walkable(map))
                    {
                        targetCell = cand;
                    }
                }
                if (targetCell.InBounds(map) && FilthMaker.TryMakeFilth(targetCell, map, bloodDef, sourceLabel))
                {
                    spawned++;
                    TotalSpawnedGiantBloodFilthCount++;
                }
            }
            return spawned;
        }

        public static void TrySpawnHitBloodFilth(Pawn pawn, CompGiantRig comp, DamageInfo dinfo, float totalDamageDealt, int hintedBoneIndex)
        {
            if (!CanDropGroundBloodNow(pawn, comp))
            {
                return;
            }
            if (dinfo.Def == null || !dinfo.Def.harmsHealth || totalDamageDealt < 2.0f)
            {
                return;
            }

            float injuryBleedFactor = dinfo.Def.hediff?.injuryProps?.bleedRate ?? 0f;
            bool causesBleeding = injuryBleedFactor > 0.001f
                || dinfo.Def.armorCategory == DamageArmorCategoryDefOf.Sharp
                || (pawn.health?.hediffSet != null && pawn.health.hediffSet.BleedRateTotal > 0.02f && totalDamageDealt >= 12f);
            if (!causesBleeding)
            {
                return;
            }

            int bCount = comp.RigDef?.SortedNodes?.Length ?? 0;
            if (bCount <= 0)
            {
                return;
            }

            int targetBone = -1;
            Span<bool> bFlashed = bCount <= 64 ? stackalloc bool[bCount] : new bool[bCount];
            Span<bool> wFlashed = stackalloc bool[16];
            ReadOnlySpan<BodyPartRecord> linkedSpan = comp.LinkedBodyPartsSpan;
            if (CompGiantRig.TryResolveFlashTargetsForHitPart(
                comp.RigDef.SortedNodes,
                comp.LinkedBodyPartsRawArray,
                null,
                dinfo.HitPart,
                hintedBoneIndex,
                bFlashed,
                wFlashed))
            {
                for (int i = 0; i < bCount; i++)
                {
                    if (bFlashed[i])
                    {
                        targetBone = i;
                        break;
                    }
                }
            }
            if (targetBone < 0)
            {
                targetBone = GiantWoundBloodSystem.ResolveTargetVisualBoneForPart(
                    comp.RigDef.SortedNodes,
                    linkedSpan,
                    comp.PartMissingMaskSpan,
                    dinfo.HitPart,
                    pawn.thingIDNumber ^ GenTicks.TicksGame);
            }

            int filthCount = 0;
            if (totalDamageDealt >= 26f)
            {
                filthCount = Rand.RangeInclusive(2, 3);
            }
            else if (totalDamageDealt >= 10f)
            {
                filthCount = Rand.Chance(0.55f) ? 2 : 1;
            }
            else if (Rand.Chance(Mathf.Clamp01(0.35f + totalDamageDealt * 0.055f)))
            {
                filthCount = 1;
            }

            if (filthCount > 0)
            {
                IntVec3 limbCell = ResolveBoneWorldGroundCell(pawn, comp, targetBone, jitterRadius: 0.85f);
                SpawnBloodSplatterAtCell(pawn, pawn.Map, limbCell, filthCount, scatterRadius: 1.55f);
            }
        }

        public static void TrySpawnSeverBloodFilth(Pawn pawn, CompGiantRig comp, int severedBoneIndex)
        {
            if (!CanDropGroundBloodNow(pawn, comp))
            {
                return;
            }
            int anchorIdx = severedBoneIndex;
            ReadOnlySpan<BoneWorldPose> poses = comp.FootprintBonePosesSpan;
            if ((uint)anchorIdx >= (uint)poses.Length || !poses[anchorIdx].visible)
            {
                BoneNodeDef[] nodes = comp.RigDef?.SortedNodes;
                if (nodes != null && (uint)severedBoneIndex < (uint)nodes.Length && nodes[severedBoneIndex].parentRuntimeIndex >= 0)
                {
                    anchorIdx = nodes[severedBoneIndex].parentRuntimeIndex;
                }
            }
            IntVec3 severCell = ResolveBoneWorldGroundCell(pawn, comp, anchorIdx, jitterRadius: 0.75f);
            SpawnBloodSplatterAtCell(pawn, pawn.Map, severCell, Rand.RangeInclusive(3, 5), scatterRadius: 2.1f);
        }

        public static bool TryDropAnatomicalBleedingFilth(Pawn pawn, CompGiantRig comp)
        {
            if (!CanDropGroundBloodNow(pawn, comp) || pawn.health?.hediffSet?.hediffs == null)
            {
                return false;
            }

            BoneNodeDef[] nodes = comp.RigDef?.SortedNodes;
            if (nodes == null || nodes.Length == 0)
            {
                return false;
            }

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            Hediff chosenBleeding = null;
            float totalWeight = 0f;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                float br = h.BleedRate;
                if (br <= 0.001f)
                {
                    continue;
                }
                totalWeight += br;
                if (Rand.Value * totalWeight <= br)
                {
                    chosenBleeding = h;
                }
            }

            int boneIdx = 0;
            if (chosenBleeding != null)
            {
                boneIdx = GiantWoundBloodSystem.ResolveTargetVisualBoneForPart(
                    nodes,
                    comp.LinkedBodyPartsSpan,
                    comp.PartMissingMaskSpan,
                    chosenBleeding.Part,
                    pawn.thingIDNumber ^ chosenBleeding.loadID);
            }
            IntVec3 cell = ResolveBoneWorldGroundCell(pawn, comp, Math.Max(0, boneIdx), jitterRadius: 0.70f);
            return SpawnBloodSplatterAtCell(pawn, pawn.Map, cell, 1, scatterRadius: 0.8f) > 0;
        }

        /// <summary>
        /// Compensates ground blood dripping for multi-bone giants whose HediffSet.CalculateBleedRate was divided
        /// by NormalizeGiantBleedRate (~8x-12x) to prevent premature BloodLoss coma.
        /// Invoked once every 30 ticks from CompGiantRig.CompTick while grounded.
        /// </summary>
        public static void TickGroundedBleedingDrip(Pawn pawn, CompGiantRig comp, int ticksGame)
        {
            if (((ticksGame + pawn.thingIDNumber) % 30) != 0)
            {
                return;
            }
            if (!CanDropGroundBloodNow(pawn, comp) || pawn.health?.hediffSet == null)
            {
                return;
            }
            float normalizedBleed = pawn.health.hediffSet.BleedRateTotal;
            if (normalizedBleed <= 0.005f)
            {
                return;
            }

            BodyPartRecord core = comp.GetLinkedBodyPartRecord(0) ?? pawn.RaceProps?.body?.corePart;
            float coreBaseHp = core?.def != null ? Mathf.Max(40f, core.def.hitPoints) : 40f;
            float divisor = (comp.RigDef != null && comp.RigDef.enableLimbPartDamageRouting)
                ? Mathf.Max(1f, GiantCombatMechanicsUtility.ComputeGiantPainNormalizationDivisor(comp, coreBaseHp))
                : 1f;
            float missingDivisor = Mathf.Max(0f, divisor - 1f);
            if (missingDivisor <= 0.05f)
            {
                return;
            }

            // Equivalent 30-tick aggregate of vanilla's per-tick (rawBleed - normalizedBleed) * BodySize * 0.0022f, capped cleanly
            float chance30 = Mathf.Clamp(normalizedBleed * missingDivisor * Mathf.Min(pawn.BodySize, 6.5f) * 0.0022f * 30f, 0f, 0.55f);
            if (Rand.Value < chance30)
            {
                TryDropAnatomicalBleedingFilth(pawn, comp);
            }
        }
    }

    public class PawnRenderNodeProperties_GiantWoundOverlay : PawnRenderNodeProperties
    {
        public CompGiantRig rigComp;

        public PawnRenderNodeProperties_GiantWoundOverlay()
        {
            nodeClass = typeof(PawnRenderNode_GiantWoundOverlay);
            workerClass = typeof(PawnRenderNodeWorker_GiantWoundOverlay);
            useGraphic = false;
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting;
            pawnType = RenderNodePawnType.Any;
            baseLayer = 10f;
        }
    }

    public class PawnRenderNode_GiantWoundOverlay : PawnRenderNode
    {
        public CompGiantRig RigComp => (props as PawnRenderNodeProperties_GiantWoundOverlay)?.rigComp;

        public PawnRenderNode_GiantWoundOverlay(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        protected override IEnumerable<Graphic> GraphicsFor(Pawn pawn)
        {
            GiantWoundBloodSystem.PrewarmMaterialsOnMainThread();
            return base.GraphicsFor(pawn);
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            return null;
        }

        public override Mesh GetMesh(PawnDrawParms parms)
        {
            return MeshPool.plane10;
        }
    }

    public class PawnRenderNodeWorker_GiantWoundOverlay : PawnRenderNodeWorker
    {
        public override bool ShouldListOnGraph(PawnRenderNode node, PawnDrawParms parms)
        {
            return true;
        }

        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantWoundOverlay woundNode)
            {
                if (woundNode.RigComp?.RigDef == null || parms.rotDrawMode == RotDrawMode.Dessicated)
                {
                    return false;
                }
                return node.DebugEnabled;
            }
            return false;
        }

        public override void AppendDrawRequests(PawnRenderNode node, PawnDrawParms parms, List<PawnGraphicDrawRequest> requests)
        {
            if (!CanDrawNow(node, parms))
            {
                return;
            }
            // Material is intentionally null: zero worker-thread Unity resource calls; PostDraw runs on Unity main thread.
            requests.Add(new PawnGraphicDrawRequest(node, MeshPool.plane10));
        }

        public override void PostDraw(PawnRenderNode node, PawnDrawParms parms, Mesh mesh, Matrix4x4 matrix)
        {
            if (!UnityData.IsInMainThread || !(node is PawnRenderNode_GiantWoundOverlay woundNode))
            {
                return;
            }
            CompGiantRig comp = woundNode.RigComp;
            Pawn ownerPawn = comp?.Pawn ?? parms.pawn;
            if (comp?.RigDef == null || ownerPawn == null)
            {
                return;
            }
            if (parms.flags.FlagSet(PawnRenderFlags.Invisible)
                || parms.rotDrawMode == RotDrawMode.Dessicated
                || GiantMotionOverrideRegistry.IsHiddenInOrbit(ownerPawn))
            {
                return;
            }

            IReadOnlyList<GiantWoundDecalSpec> decals = comp.GetOrRefreshWoundDecalSpecs();
            int decalCount = decals.Count;
            if (decalCount == 0)
            {
                return;
            }

            PawnDrawParms alignedParms = parms;
            alignedParms.matrix = matrix;

            BoneWorldPose[] solvedPoses = comp.SolveOrGetCachedPosesForOverlay(
                alignedParms,
                out Vector4 rootCol,
                out GiantRenderSnapshot snap);
            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            int nodeCount = nodes.Length;
            if (solvedPoses == null || solvedPoses.Length < nodeCount)
            {
                return;
            }

            bool pawnDead = ownerPawn.Dead || (snap != null && snap.isDead);
            float portraitOrScale = parms.Portrait ? comp.RigDef.portraitScaleFactor : 1f;
            MaterialPropertyBlock mpb = GiantWoundBloodSystem.SharedDecalPropertyBlock;

            for (int i = 0; i < decalCount; i++)
            {
                GiantWoundDecalSpec spec = decals[i];
                int bIdx = spec.boneIndex;
                if ((uint)bIdx >= (uint)nodeCount)
                {
                    continue;
                }
                ref readonly BoneWorldPose pose = ref solvedPoses[bIdx];
                if (!pose.visible)
                {
                    continue;
                }

                Material mat = GiantWoundBloodSystem.ResolveDecalMaterialOnMainThread(spec.kind, spec.variantIndex);
                if (mat == null)
                {
                    continue;
                }

                BoneNodeDef bDef = nodes[bIdx];
                Vector2 uv = spec.GetSectorUV(pose.activeSector);
                float surfaceLayer = bDef.GetRenderedSurfaceLayerOffset(pose.activeSector);
                float partLayer = surfaceLayer + spec.surfaceLayerDelta + woundNode.debugLayerOffset;
                float worldY = comp.ResolveOverlayWorldAltitude(alignedParms, rootCol, snap, partLayer, out _);

                Vector2 scaledSize = spec.baseWorldSize * portraitOrScale;
                Matrix4x4 decalMat = pose.BuildSubQuadRenderMatrix(
                    uv.x,
                    uv.y,
                    spec.localAngleDeg,
                    scaledSize,
                    worldY);

                Color finalCol = spec.tintColor;
                finalCol.r *= parms.tint.r;
                finalCol.g *= parms.tint.g;
                finalCol.b *= parms.tint.b;
                finalCol.a *= parms.tint.a;

                if (!parms.Portrait)
                {
                    if (pawnDead)
                    {
                        finalCol.r *= 0.60f;
                        finalCol.g *= 0.56f;
                        finalCol.b *= 0.57f;
                    }
                    else
                    {
                        float flash01 = comp.GetBoneDamageFlash01(bIdx);
                        if (flash01 > 0.001f)
                        {
                            finalCol = Color.Lerp(finalCol, new Color(1f, 0.18f, 0.15f, finalCol.a), flash01 * 0.65f);
                        }
                    }
                }

                mpb.Clear();
                mpb.SetColor(ShaderPropertyIDs.Color, finalCol);
                GenDraw.DrawMeshNowOrLater(MeshPool.plane10, decalMat, mat, parms.DrawNow, mpb);
            }
            mpb.Clear();
        }
    }
}
