using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Holds unpatched vanilla PawnRenderer stubs bound at startup so GiantCreatureFramework giants
    /// execute RimWorld's clean PawnRenderTree pipeline directly while bypassing third-party animation
    /// Prefixes/Transpilers/Postfixes (such as Melee Animation's RenderPawnAt hijack).
    /// </summary>
    internal static class GiantCleanRenderStub
    {
        internal static bool IsRenderPawnAtBound;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RenderPawnAtUnpatched(PawnRenderer instance, Vector3 drawLoc, Rot4? rotOverride, bool neverAimWeapon)
        {
            throw new NotImplementedException("GiantCleanRenderStub.RenderPawnAtUnpatched reverse-patch placeholder");
        }
    }

    /// <summary>
    /// Four-layer root rendering and forced-animation sovereignty guard for GiantCreatureFramework units.
    /// Ensures third-party animation mods (Yayo's Animation, Melee Animation, HARAnimate, Facial Animation,
    /// Show Me Your Hands, custom PawnFlyer grapple/slice mods) continue working normally on colonists and
    /// other pawns while having zero ability to hijack, tilt, wobble, lasso, or execute GCF giants.
    /// </summary>
    public static class GiantRenderIsolationGuard
    {
        public const int ShieldFirstPriority = 10000;
        public const int ShieldLastPriority = -10000;

        internal static readonly Assembly VanillaAssembly = typeof(Pawn).Assembly;
        internal static readonly Assembly GcfAssembly = typeof(GiantRenderIsolationGuard).Assembly;

        // 1 = allowed, 2 = blocked external animation/duel/lasso/execution control job
        private static readonly ConcurrentDictionary<ushort, byte> JobDefDecisionCache = new ConcurrentDictionary<ushort, byte>();

        private static readonly string[] BlockedExternalDriverTokens =
        {
            "AM.",
            "MeleeAnimation",
            "AdvancedAnimation",
            "YayoAnimation",
            "yayoAni",
            "HARAnimate",
            "FacialAnimation",
            "BodyAnimation",
            "ShowMeYourHands",
            "Grapple",
            "Lasso",
        };

        private static readonly string[] BlockedExternalJobNameTokens =
        {
            "Duel",
            "Execut",
            "Lasso",
            "Grapple",
            "Suplex",
            "Finisher",
            "Choke",
            "Knockup",
        };

        public static long IsolatedRenderCalls;
        public static long SanitizedDrawParmsCalls;
        public static long BlockedForeignRenderNodes;
        public static long BlockedExternalJobs;
        public static long BlockedExternalFlyers;

        public static bool IsCleanRenderStubBound => GiantCleanRenderStub.IsRenderPawnAtBound;

        internal static void InitializeCleanRenderStub()
        {
            if (GiantCleanRenderStub.IsRenderPawnAtBound)
            {
                return;
            }
            try
            {
                MethodInfo orig = AccessTools.Method(
                    typeof(PawnRenderer),
                    nameof(PawnRenderer.RenderPawnAt),
                    new[] { typeof(Vector3), typeof(Rot4?), typeof(bool) });
                MethodInfo stub = AccessTools.Method(
                    typeof(GiantCleanRenderStub),
                    nameof(GiantCleanRenderStub.RenderPawnAtUnpatched));
                if (orig != null && stub != null)
                {
                    var harmony = new Harmony("sanguo.giantcreatureframework.renderisolation");
                    harmony.CreateReversePatcher(orig, new HarmonyMethod(stub)).Patch(HarmonyReversePatchType.Original);
                    GiantCleanRenderStub.IsRenderPawnAtBound = true;
                }
            }
            catch (Exception ex)
            {
                GiantCleanRenderStub.IsRenderPawnAtBound = false;
                Log.Warning($"[GiantCreatureFramework] RenderPawnAt clean reverse-patch fallback active: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix4x4 SanitizeGiantRootMatrix(Matrix4x4 incomingMatrix, Vector3 canonicalWorldPos, bool isPortraitOrCache)
        {
            if (isPortraitOrCache)
            {
                return incomingMatrix;
            }
            SanitizedDrawParmsCalls++;
            return Matrix4x4.TRS(
                new Vector3(canonicalWorldPos.x, incomingMatrix.m13, canonicalWorldPos.z),
                Quaternion.identity,
                Vector3.one);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsForeignRenderNodeAssembly(Assembly nodeAssembly)
        {
            return nodeAssembly != null
                && nodeAssembly != VanillaAssembly
                && nodeAssembly != GcfAssembly;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAllowedGiantFlyerDef(ThingDef flyingDef)
        {
            if (flyingDef == null)
            {
                return false;
            }
            if (flyingDef.defName != null && flyingDef.defName.StartsWith("GCF_", StringComparison.Ordinal))
            {
                return true;
            }
            Type tc = flyingDef.thingClass;
            return tc != null && tc.Assembly == GcfAssembly;
        }

        public static bool ShouldBlockExternalAnimationJob(string defName, string driverFullName, string packageId, bool driverIsVanillaOrGcf)
        {
            if (!string.IsNullOrEmpty(defName) && defName.StartsWith("GCF_", StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(packageId) &&
                (packageId.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(packageId, "sanguo.giantcreatureframework", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!driverIsVanillaOrGcf && !string.IsNullOrEmpty(driverFullName))
            {
                for (int i = 0; i < BlockedExternalDriverTokens.Length; i++)
                {
                    if (driverFullName.IndexOf(BlockedExternalDriverTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            if (!string.IsNullOrEmpty(defName))
            {
                if (defName.StartsWith("AM_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!driverIsVanillaOrGcf || !string.IsNullOrEmpty(packageId))
                {
                    for (int i = 0; i < BlockedExternalJobNameTokens.Length; i++)
                    {
                        if (defName.IndexOf(BlockedExternalJobNameTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBlockedExternalAnimationJob(JobDef jobDef)
        {
            if (jobDef == null)
            {
                return false;
            }

            ushort hash = jobDef.shortHash;
            if (hash != 0 && JobDefDecisionCache.TryGetValue(hash, out byte cached))
            {
                return cached == 2;
            }

            Type driverType = jobDef.driverClass;
            Assembly driverAsm = driverType?.Assembly;
            bool driverIsVanillaOrGcf = driverAsm == null || driverAsm == VanillaAssembly || driverAsm == GcfAssembly;
            string driverFullName = driverType?.FullName ?? string.Empty;
            string pkgId = jobDef.modContentPack?.PackageId ?? string.Empty;

            bool blocked = ShouldBlockExternalAnimationJob(jobDef.defName, driverFullName, pkgId, driverIsVanillaOrGcf);
            if (hash != 0)
            {
                JobDefDecisionCache[hash] = blocked ? (byte)2 : (byte)1;
            }
            return blocked;
        }
    }

    [HarmonyPatch]
    public static class HarmonyPatches_Rendering
    {
        [ThreadStatic] private static Pawn tsActiveGiantPawn;
        [ThreadStatic] private static Vector3 tsCleanGiantDrawLoc;

        /// <summary>
        /// Layer 0A: Flushes deferred dynamic Unity meshes (SoftWhip, RollingTrack, SkinnedMesh) once per frame right
        /// after all simulation ticks finish in TickManagerUpdate and strictly BEFORE Waylith enters its active Vulkan
        /// MapUpdate / DynamicDrawManager render pass (avoiding mid-render-pass vertex buffer staging stalls).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
        public static void TickManager_TickManagerUpdate_Postfix()
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            Map curMap = Find.CurrentMap;
            if (curMap == null)
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(curMap);
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return;
            }
            IReadOnlyList<CompGiantRig> liveGiants = mapComp.ActiveGiants;
            for (int i = 0; i < liveGiants.Count; i++)
            {
                liveGiants[i]?.FlushPendingDynamicMeshesOnMainThread();
            }
        }

        /// <summary>
        /// Layer 0B: Paused-frame fallback flush (no-op during active unpaused gameplay since Layer 0A already cleared
        /// pendingDynamicMeshUpload before entering DrawDynamicThings).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
        public static void DynamicDrawManager_DrawDynamicThings_Prefix(Map ___map)
        {
            if (GiantFastLookup.IsEmpty || ___map == null)
            {
                return;
            }
            if (Find.TickManager != null && !Find.TickManager.Paused)
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            if (mapComp == null)
            {
                return;
            }
            IReadOnlyList<CompGiantRig> liveGiants = mapComp.ActiveGiants;
            for (int i = 0; i < liveGiants.Count; i++)
            {
                liveGiants[i]?.FlushPendingDynamicMeshesOnMainThread();
            }
            IReadOnlyList<CompGiantRig> corpses = mapComp.ActiveCorpseGiants;
            for (int i = 0; i < corpses.Count; i++)
            {
                corpses[i]?.FlushPendingDynamicMeshesOnMainThread();
            }
        }

        /// <summary>
        /// Ensures airborne giant creatures flying above FogOfWar remain rendered even when their
        /// root cell passes over a fogged tile (where vanilla DynamicDrawManager.CullJob skips
        /// pawns whose def.seeThroughFog is false).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
        public static void DynamicDrawManager_DrawDynamicThings_Postfix(Map ___map)
        {
            if (GiantFastLookup.IsEmpty || ___map == null || ___map.Disposed || ___map.fogGrid == null || !DebugViewSettings.drawThingsDynamic)
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            if (mapComp == null)
            {
                return;
            }
            IReadOnlyList<CompGiantRig> liveGiants = mapComp.ActiveGiants;
            if (liveGiants == null || liveGiants.Count == 0)
            {
                return;
            }
            CellRect viewRect = Find.CameraDriver != null
                ? Find.CameraDriver.CurrentViewRect.ExpandedBy(1)
                : CellRect.WholeMap(___map);
            viewRect.ClipInsideMap(___map);

            for (int i = 0; i < liveGiants.Count; i++)
            {
                CompGiantRig comp = liveGiants[i];
                Pawn pawn = comp?.Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map != ___map || pawn.def == null || pawn.def.seeThroughFog)
                {
                    continue;
                }
                if (!comp.IsCurrentlyAirborne && !comp.IsFlyingEntry)
                {
                    continue;
                }
                IntVec3 drawCell = pawn.DrawPos.ToIntVec3();
                if (!drawCell.InBounds(___map) || !___map.fogGrid.IsFogged(drawCell))
                {
                    continue;
                }
                if (!pawn.def.drawOffscreen && !viewRect.Overlaps(pawn.OccupiedDrawRect()))
                {
                    continue;
                }
                try
                {
                    pawn.DynamicDrawPhase(DrawPhase.EnsureInitialized);
                    pawn.DynamicDrawPhase(DrawPhase.ParallelPreDraw);
                    pawn.DynamicDrawPhase(DrawPhase.Draw);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce($"[GCF] Exception drawing airborne giant above fog ({pawn}): {ex}", pawn.thingIDNumber ^ 0x51F06);
                }
            }
        }

        internal static bool IsBlockedVanillaOrForeignBodyNode(PawnRenderNode node)
        {
            if (node == null)
            {
                return false;
            }
            if (node is PawnRenderNode_AnimalPart ||
                node is PawnRenderNode_Body ||
                node is PawnRenderNode_Head ||
                node is PawnRenderNode_Fur)
            {
                return true;
            }
            if (GiantRenderIsolationGuard.IsForeignRenderNodeAssembly(node.GetType().Assembly))
            {
                GiantRenderIsolationGuard.BlockedForeignRenderNodes++;
                return true;
            }
            return false;
        }

        internal static bool TryComputeGiantNodeMatrix(PawnRenderNode node, PawnDrawParms parms, out Matrix4x4 matrix, out bool isGiantOwnedNode)
        {
            if (node is PawnRenderNode_GiantBone boneNode && boneNode.RigComp != null)
            {
                isGiantOwnedNode = true;
                return boneNode.RigComp.TryComputeBoneRenderMatrix(boneNode, parms, out matrix);
            }
            if (node is PawnRenderNode_GiantSoftWhip whipNode && whipNode.RigComp != null)
            {
                isGiantOwnedNode = true;
                return whipNode.RigComp.TryComputeSoftWhipRenderMatrix(whipNode, parms, out matrix);
            }
            if (node is PawnRenderNode_GiantSkinnedMesh skinNode && skinNode.RigComp != null)
            {
                isGiantOwnedNode = true;
                return skinNode.RigComp.TryComputeSkinnedMeshRenderMatrix(skinNode, parms, out matrix);
            }
            if (node is PawnRenderNode_GiantRollingTrack trackNode && trackNode.RigComp != null)
            {
                isGiantOwnedNode = true;
                return trackNode.RigComp.TryComputeRollingTrackRenderMatrix(trackNode, parms, out matrix);
            }
            if (node is PawnRenderNode_GiantTrackWheel wheelNode && wheelNode.RigComp != null)
            {
                isGiantOwnedNode = true;
                return wheelNode.RigComp.TryComputeTrackWheelRenderMatrix(wheelNode, parms, out matrix);
            }
            if (node is PawnRenderNode_GiantRoot || node is PawnRenderNode_GiantWoundOverlay)
            {
                isGiantOwnedNode = true;
                bool isPortraitOrCache = parms.Portrait || parms.flags.FlagSet(PawnRenderFlags.Cache);
                Vector4 col = parms.matrix.GetColumn(3);
                matrix = GiantRenderIsolationGuard.SanitizeGiantRootMatrix(
                    parms.matrix,
                    new Vector3(col.x, col.y, col.z),
                    isPortraitOrCache);
                return true;
            }
            isGiantOwnedNode = false;
            matrix = Matrix4x4.identity;
            return false;
        }

        /// <summary>
        /// Layer 1A: Replaces 4-directional Rot4 node matrices with exact 360-degree top-down Forward Kinematics
        /// matrices on all GCF rig nodes, sanitizes PawnRenderNode_GiantRoot, and blocks any foreign third-party
        /// PawnRenderNode injected onto a giant's PawnRenderTree (FacialAnimation, HARAnimate, ShowMeYourHands, etc.).
        /// Never returns __result = false on blocked nodes so PawnRenderTree.ParallelPreDraw never hits break;.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyBefore("wowgag.guiperformancepatch", "lea.puabodyanimation", "Palpha.godhands", "com.yayo.yayoAni", "rimworld.erdelf.alien_race.main")]
        [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.TryGetMatrix))]
        public static bool PawnRenderTree_TryGetMatrix_Prefix(
            PawnRenderNode node,
            PawnDrawParms parms,
            ref Matrix4x4 matrix,
            ref bool __result)
        {
            if (parms.pawn != null && !GiantFastLookup.MightBeGiant(parms.pawn))
            {
                return true;
            }
            if (TryComputeGiantNodeMatrix(node, parms, out Matrix4x4 gcfMat, out bool isGiantOwnedNode))
            {
                matrix = gcfMat;
                __result = true;
                return false;
            }
            if (isGiantOwnedNode)
            {
                matrix = Matrix4x4.zero;
                __result = true;
                return false;
            }
            if (node != null && parms.pawn != null && GiantFastLookup.TryGetRig(parms.pawn) != null)
            {
                if (IsBlockedVanillaOrForeignBodyNode(node))
                {
                    matrix = Matrix4x4.zero;
                    __result = true;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Layer 1A-Post: Direct PawnRenderTree.ParallelPreDraw sovereignty pass.
        /// Under RimWorld 1.7 (.NET 10 CoreCLR + Harmony 3), early third-party patches on ParallelPreDraw (such as
        /// GUIPerformancePatch) can cause CoreCLR to inline unpatched TryGetMatrix into ParallelPreDraw before GCF loads,
        /// collapsing MeshPool.plane10 GiantBone nodes to 1x1 scale at pawnPos. Writing preDrawnComputedMatrix directly
        /// onto drawRequests here guarantees 100% deterministic FK rendering regardless of TryGetMatrix inlining or caching.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldLastPriority)]
        [HarmonyAfter("wowgag.guiperformancepatch", "lea.puabodyanimation", "Palpha.godhands", "com.yayo.yayoAni", "rimworld.erdelf.alien_race.main")]
        [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.ParallelPreDraw))]
        public static void PawnRenderTree_ParallelPreDraw_Postfix(
            Pawn ___pawn,
            List<PawnGraphicDrawRequest> ___drawRequests,
            Color? ___debugTint,
            PawnDrawParms parms)
        {
            if (GiantFastLookup.IsEmpty || ___pawn == null || !GiantFastLookup.MightBeGiant(___pawn) || ___drawRequests == null)
            {
                return;
            }
            int reqCount = ___drawRequests.Count;
            if (reqCount == 0 || GiantFastLookup.TryGetRig(___pawn) == null)
            {
                return;
            }

            if (___debugTint.HasValue)
            {
                parms.tint *= ___debugTint.Value;
            }
            bool isPortraitOrCache = parms.Portrait || parms.flags.FlagSet(PawnRenderFlags.Cache);
            if (!isPortraitOrCache)
            {
                Vector4 col = parms.matrix.GetColumn(3);
                Vector3 cleanPos = (tsActiveGiantPawn == ___pawn)
                    ? tsCleanGiantDrawLoc
                    : new Vector3(col.x, col.y, col.z);
                parms.matrix = GiantRenderIsolationGuard.SanitizeGiantRootMatrix(parms.matrix, cleanPos, false);
            }

            for (int i = 0; i < reqCount; i++)
            {
                PawnGraphicDrawRequest req = ___drawRequests[i];
                PawnRenderNode node = req.node;
                if (node == null)
                {
                    continue;
                }
                if (TryComputeGiantNodeMatrix(node, parms, out Matrix4x4 gcfMat, out bool isGiantOwnedNode))
                {
                    if (!parms.Portrait && node is PawnRenderNode_GiantSkinnedMesh smNode && smNode.HasValidMesh)
                    {
                        Mesh dynMesh = smNode.ActiveDynamicMesh;
                        if (dynMesh != null && !ReferenceEquals(req.mesh, dynMesh))
                        {
                            req = new PawnGraphicDrawRequest(req.node, dynMesh, req.material);
                        }
                    }
                    req.preDrawnComputedMatrix = gcfMat;
                    ___drawRequests[i] = req;
                }
                else if (isGiantOwnedNode)
                {
                    req.preDrawnComputedMatrix = Matrix4x4.zero;
                    ___drawRequests[i] = req;
                }
                else if (IsBlockedVanillaOrForeignBodyNode(node))
                {
                    req.material = null;
                    req.preDrawnComputedMatrix = Matrix4x4.zero;
                    ___drawRequests[i] = req;
                }
            }
        }

        /// <summary>
        /// Layer 1B: Captures pristine unmutated drawLoc at highest priority before Yayo's Animation or other
        /// prefixes can inject bobbing/rotation offsets, disables small-pawn atlas caching, and suppresses weapon aiming.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyBefore("wowgag.guiperformancepatch", "lea.puabodyanimation", "Palpha.godhands", "com.yayo.yayoAni", "rimworld.erdelf.alien_race.main")]
        [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
        public static void PawnRenderer_ParallelGetPreRenderResults_Prefix(
            Pawn ___pawn,
            ref Vector3 drawLoc,
            ref Rot4? rotOverride,
            ref bool neverAimWeapon,
            ref bool disableCache)
        {
            if (___pawn != null && GiantFastLookup.TryGetRig(___pawn) is CompGiantRig rig)
            {
                if (GiantMotionOverrideRegistry.TryGetActiveWorldPos(___pawn, out Vector3 exactOverridePos))
                {
                    drawLoc = new Vector3(exactOverridePos.x, drawLoc.y, exactOverridePos.z);
                }
                else if (rig.VehicleLocomotion != null && rig.VehicleLocomotion.TryGetExactWorldPos(___pawn, out Vector3 exactVehPos))
                {
                    drawLoc = new Vector3(exactVehPos.x, drawLoc.y, exactVehPos.z);
                }
                else if (rig.FlightVehicleLocomotion != null && rig.FlightVehicleLocomotion.TryGetExactWorldPos(___pawn, out Vector3 exactFvehPos))
                {
                    drawLoc = new Vector3(exactFvehPos.x, drawLoc.y, exactFvehPos.z);
                }
                tsActiveGiantPawn = ___pawn;
                tsCleanGiantDrawLoc = drawLoc;
                rotOverride = null;
                neverAimWeapon = true;
                disableCache = true;
            }
        }

        public static void ClearThreadStaticRenderContext()
        {
            tsActiveGiantPawn = null;
            tsCleanGiantDrawLoc = default;
        }

        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldLastPriority)]
        [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
        public static void PawnRenderer_ParallelGetPreRenderResults_Postfix(Pawn ___pawn)
        {
            if (ReferenceEquals(tsActiveGiantPawn, ___pawn))
            {
                tsActiveGiantPawn = null;
            }
        }

        /// <summary>
        /// Layer 1C: Wipes out any body-position bobbing, bed offset, or hopping offset injected by external
        /// animation Postfixes (such as YayoAnimation.GetBodyPosPatch).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldLastPriority)]
        [HarmonyPatch(typeof(PawnRenderer), "GetBodyPos")]
        public static void PawnRenderer_GetBodyPos_Postfix(
            Pawn ___pawn,
            Vector3 drawLoc,
            ref bool showBody,
            ref Vector3 __result)
        {
            if (___pawn != null && GiantFastLookup.TryGetRig(___pawn) != null)
            {
                showBody = true;
                __result = (tsActiveGiantPawn == ___pawn) ? tsCleanGiantDrawLoc : drawLoc;
            }
        }

        /// <summary>
        /// Layer 1D: Forces zero whole-body tilt/wiggle angle on giants, overriding YayoAnimation.BodyAnglePatch
        /// and vanilla small-pawn downed wiggler angles.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldLastPriority)]
        [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.BodyAngle))]
        public static void PawnRenderer_BodyAngle_Postfix(Pawn ___pawn, ref float __result)
        {
            if (___pawn != null && GiantFastLookup.TryGetRig(___pawn) != null)
            {
                __result = 0f;
            }
        }

        /// <summary>
        /// Layer 1E: Final sanitization pass on PawnDrawParms. Resets root matrix rotation/scale to identity,
        /// restores pristine world X/Z position, locks Standing posture on live giants, and strips external
        /// duel-invisibility flags unless genuinely hidden from the player.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldLastPriority)]
        [HarmonyAfter("lea.puabodyanimation", "Dubwise.DubsBadHygiene", "keeptpa.NivarianRace", "sanguo.nivarianstarryroad", "rimworld.erdelf.alien_race.main")]
        [HarmonyPatch(typeof(PawnRenderer), "GetDrawParms")]
        public static void PawnRenderer_GetDrawParms_Postfix(
            Pawn ___pawn,
            Vector3 rootLoc,
            PawnRenderFlags flags,
            ref PawnDrawParms __result)
        {
            if (___pawn == null || GiantFastLookup.TryGetRig(___pawn) == null)
            {
                return;
            }

            bool isPortraitOrCache = flags.FlagSet(PawnRenderFlags.Portrait) || flags.FlagSet(PawnRenderFlags.Cache);
            if (!isPortraitOrCache)
            {
                Vector3 cleanPos = (tsActiveGiantPawn == ___pawn) ? tsCleanGiantDrawLoc : rootLoc;
                __result.matrix = GiantRenderIsolationGuard.SanitizeGiantRootMatrix(__result.matrix, cleanPos, false);
                if (!___pawn.Dead)
                {
                    __result.posture = PawnPosture.Standing;
                    __result.bed = null;
                    if (GiantMotionOverrideRegistry.IsHiddenInOrbit(___pawn))
                    {
                        __result.flags |= PawnRenderFlags.Invisible;
                    }
                    else if (___pawn.health?.hediffSet?.hediffs == null || !___pawn.IsPsychologicallyInvisible())
                    {
                        __result.flags &= ~PawnRenderFlags.Invisible;
                    }
                }
            }
        }

        /// <summary>
        /// Layer 2: Direct RenderPawnAt sovereignty. Executes RimWorld's unpatched RenderPawnAt stub directly
        /// for GCF giants at priority +10000 and returns false, completely bypassing third-party RenderPawnAt
        /// Prefixes/Transpilers/Postfixes (such as Melee Animation's duel/execution AnimRenderer hijack).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyBefore("lea.puabodyanimation", "Palpha.godhands", "com.yayo.yayoAni", "sanguo.nivarianstarryroad", "trigger.ceoocplan")]
        [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt),
            new[] { typeof(Vector3), typeof(Rot4?), typeof(bool) })]
        public static bool PawnRenderer_RenderPawnAt_Prefix(
            PawnRenderer __instance,
            Pawn ___pawn,
            ref Vector3 drawLoc,
            ref Rot4? rotOverride,
            ref bool neverAimWeapon)
        {
            if (___pawn == null || !(GiantFastLookup.TryGetRig(___pawn) is CompGiantRig rig))
            {
                return true;
            }
            if (GiantMotionOverrideRegistry.IsHiddenInOrbit(___pawn))
            {
                return false;
            }

            if (GiantMotionOverrideRegistry.TryGetActiveWorldPos(___pawn, out Vector3 exactOverridePos))
            {
                drawLoc = new Vector3(exactOverridePos.x, drawLoc.y, exactOverridePos.z);
            }
            else if (rig.VehicleLocomotion != null && rig.VehicleLocomotion.TryGetExactWorldPos(___pawn, out Vector3 exactVehPos))
            {
                drawLoc = new Vector3(exactVehPos.x, drawLoc.y, exactVehPos.z);
            }
            else if (rig.FlightVehicleLocomotion != null && rig.FlightVehicleLocomotion.TryGetExactWorldPos(___pawn, out Vector3 exactFvehPos))
            {
                drawLoc = new Vector3(exactFvehPos.x, drawLoc.y, exactFvehPos.z);
            }
            tsActiveGiantPawn = ___pawn;
            tsCleanGiantDrawLoc = drawLoc;
            rotOverride = null;
            neverAimWeapon = true;

            if (GiantCleanRenderStub.IsRenderPawnAtBound)
            {
                GiantRenderIsolationGuard.IsolatedRenderCalls++;
                try
                {
                    GiantCleanRenderStub.RenderPawnAtUnpatched(__instance, drawLoc, null, true);
                }
                finally
                {
                    tsActiveGiantPawn = null;
                }
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldLastPriority)]
        [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt),
            new[] { typeof(Vector3), typeof(Rot4?), typeof(bool) })]
        public static void PawnRenderer_RenderPawnAt_Postfix(Pawn ___pawn)
        {
            if (ReferenceEquals(tsActiveGiantPawn, ___pawn))
            {
                tsActiveGiantPawn = null;
            }
        }

        /// <summary>
        /// Layer 3A: Blocks external weapon-wiggle, dual-wield, apparel-extra, and floating-hand render patches
        /// on GCF giants (whose turrets, blades, and muzzles are rendered deterministically via GiantRigDef bones).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
        public static bool PawnRenderUtility_DrawEquipmentAndApparelExtras_Prefix(Pawn pawn)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) != null)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Layer 3B: Beats priority-800 weapon/hand overlays (e.g. Show Me Your Hands, Melee Animation, Yayo Aiming)
        /// if DrawEquipmentAiming is ever invoked directly for a giant's dummy turret/melee weapon Thing.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
        public static bool PawnRenderUtility_DrawEquipmentAiming_Prefix(Thing eq)
        {
            if (eq?.ParentHolder is Pawn_EquipmentTracker et &&
                et.pawn != null &&
                GiantFastLookup.TryGetRig(et.pawn) != null)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Layer 3C: Blocks external hand/carry overlays on DrawCarriedThing for GCF giants.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawCarriedThing),
            new[] { typeof(Pawn), typeof(Vector3), typeof(Thing) })]
        public static bool PawnRenderUtility_DrawCarriedThing_Prefix(Pawn pawn)
        {
            if (pawn != null && GiantFastLookup.TryGetRig(pawn) != null)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Layer 4A: Prevents external grapple, lasso, knockup, or melee-throw mods (Melee Animation GrappleFlyer,
        /// Milira/SuperSword flyer slices, etc.) from wrapping a multi-cell GCF giant into a non-GCF PawnFlyer.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.MakeFlyer))]
        public static bool PawnFlyer_MakeFlyer_Prefix(ThingDef flyingDef, Pawn pawn, ref PawnFlyer __result)
        {
            if (pawn != null &&
                GiantFastLookup.TryGetRig(pawn) != null &&
                !GiantRenderIsolationGuard.IsAllowedGiantFlyerDef(flyingDef))
            {
                GiantRenderIsolationGuard.BlockedExternalFlyers++;
                __result = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Layer 4B: Blocks external synchronized duel, execution, lasso, and grapple jobs from either hijacking
        /// a GCF giant directly or targeting a GCF giant as the victim/partner.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
        public static bool Pawn_JobTracker_StartJob_Prefix(Pawn ___pawn, ref Job newJob)
        {
            if (GiantFastLookup.IsEmpty || newJob?.def == null)
            {
                return true;
            }

            CompGiantRig actorRig = ___pawn != null ? GiantFastLookup.TryGetRig(___pawn) : null;
            bool actorIsGiant = actorRig != null;
            if (actorIsGiant && actorRig.IsFlyingEntry)
            {
                if (___pawn.Drafted && newJob.playerForced && GiantPlayerControlUtility.CanControl(___pawn))
                {
                    actorRig.SkipFlightEntryImmediate();
                }
                else
                {
                    return false;
                }
            }

            if (actorIsGiant && GiantVehicleBehaviorGuard.TryFilterVehicleStartJob(___pawn, ref newJob, out bool allowVehicleJob))
            {
                return allowVehicleJob;
            }

            // Prevent ErroredPather / ErrorRecover Wait(250/150) ("等待中。") from stalling drafted player giants
            if (actorIsGiant &&
                newJob.def == JobDefOf.Wait &&
                !newJob.playerForced &&
                ___pawn.Drafted &&
                GiantPlayerControlUtility.CanControl(___pawn))
            {
                JobMaker.ReturnToPool(newJob);
                newJob = GiantPlayerControlUtility.MakeDraftedIdleWaitJob(___pawn);
                return true;
            }

            if (actorIsGiant && (newJob.def == JobDefOf.AttackMelee || newJob.def == JobDefOf.AttackStatic))
            {
                if (newJob.targetA.Thing is Building blocker && ___pawn.Spawned)
                {
                    GiantDestructionUtility.EnsureCellClearedForGiantStep(___pawn, blocker.Position);
                }

                // When curJob is null (e.g. Search and Destroy or Lord AI issuing AttackMelee/AttackStatic),
                // convert cleanly into a GCF part-combat positioning/wait job so curJob never stays null and
                // never trips Pawn_JobTracker's 10-jobs-in-10-ticks TryStartErrorRecoverJob ("等待中。")!
                if (___pawn.jobs != null && ___pawn.jobs.curJob == null && ___pawn.Spawned && !___pawn.Dead && !___pawn.Downed)
                {
                    if (newJob.targetA.IsValid && newJob.targetA.HasThing && actorRig.PartCombat != null && ___pawn.Drafted && newJob.playerForced)
                    {
                        GiantForcedAttackMode extMode = newJob.def == JobDefOf.AttackMelee
                            ? GiantForcedAttackMode.Melee
                            : GiantForcedAttackMode.Ranged;
                        actorRig.PartCombat.SetForcedTarget(newJob.targetA, extMode);
                    }
                    Job replacement = GiantPartCombatUtility.TryGivePartCombatPositioningJob(___pawn);
                    if (replacement != null && (replacement.def == JobDefOf.AttackMelee || replacement.def == JobDefOf.AttackStatic))
                    {
                        replacement = null;
                    }
                    if (replacement == null)
                    {
                        replacement = ___pawn.Drafted
                            ? GiantPlayerControlUtility.MakeDraftedIdleWaitJob(___pawn, expiryInterval: 24, checkOverrideOnExpire: true)
                            : JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 24);
                        replacement.checkOverrideOnExpire = true;
                    }
                    newJob = replacement;
                    return true;
                }
                return false;
            }
            if (!actorIsGiant && newJob.def == JobDefOf.AttackMelee && newJob.targetA.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(___pawn, newJob.targetA.Thing))
            {
                return false;
            }
            bool targetIsGiant = !actorIsGiant &&
                ((newJob.targetA.Thing is Pawn pA && GiantFastLookup.TryGetRig(pA) != null) ||
                 (newJob.targetB.Thing is Pawn pB && GiantFastLookup.TryGetRig(pB) != null));

            if ((actorIsGiant || targetIsGiant) &&
                GiantRenderIsolationGuard.IsBlockedExternalAnimationJob(newJob.def))
            {
                GiantRenderIsolationGuard.BlockedExternalJobs++;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Layer 4C: Rejects player/AI ordered external duel, execution, lasso, and grapple jobs involving GCF giants.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
        public static bool Pawn_JobTracker_TryTakeOrderedJob_Prefix(Pawn ___pawn, Job job, ref bool __result)
        {
            if (GiantFastLookup.IsEmpty || job?.def == null)
            {
                return true;
            }

            bool actorIsGiant = ___pawn != null && GiantFastLookup.TryGetRig(___pawn) != null;
            if (actorIsGiant && GiantVehicleBehaviorGuard.IsVehiclePawn(___pawn, out _, out CompGiantCockpit ordCockpit))
            {
                if ((ordCockpit != null && !___pawn.Dead && !___pawn.Downed && ordCockpit.IsDormantUnmanned && !GiantVehicleBehaviorGuard.IsPassiveDormantAllowedJob(job.def))
                    || GiantVehicleBehaviorGuard.IsForbiddenNonVehicleJobDef(job.def, ___pawn.Downed))
                {
                    __result = false;
                    return false;
                }
            }
            if (actorIsGiant && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic))
            {
                __result = false;
                return false;
            }
            if (!actorIsGiant && job.def == JobDefOf.AttackMelee && job.targetA.HasThing &&
                GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(___pawn, job.targetA.Thing))
            {
                __result = false;
                return false;
            }
            bool targetIsGiant = !actorIsGiant &&
                ((job.targetA.Thing is Pawn pA && GiantFastLookup.TryGetRig(pA) != null) ||
                 (job.targetB.Thing is Pawn pB && GiantFastLookup.TryGetRig(pB) != null));

            if ((actorIsGiant || targetIsGiant) &&
                GiantRenderIsolationGuard.IsBlockedExternalAnimationJob(job.def))
            {
                GiantRenderIsolationGuard.BlockedExternalJobs++;
                __result = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Expands DynamicDrawManager coarse culling bounds so multi-cell giant creatures never vanish
        /// when their center cell scrolls slightly outside the camera viewport while claws/tail remain visible.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenAdj), nameof(GenAdj.OccupiedDrawRect))]
        public static void GenAdj_OccupiedDrawRect_Postfix(Thing t, ref CellRect __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (t is Pawn pawn)
            {
                CompGiantRig comp = GiantFastLookup.TryGetRig(pawn);
                if (comp != null)
                {
                    CellRect bounds = comp.Footprint.worldDrawBounds;
                    if (!bounds.IsEmpty)
                    {
                        if (comp.IsCurrentlyAirborne)
                        {
                            int extraZ = Mathf.CeilToInt(Mathf.Abs(comp.CurrentFlightVisualOffsetXZ.y)) + 2;
                            bounds = new CellRect(bounds.minX - 1, bounds.minZ - 1, bounds.Width + 2, bounds.Height + extraZ + 2);
                        }
                        __result = __result.Encapsulate(bounds);
                    }
                    if (GiantMotionOverrideRegistry.TryGetActiveWorldPos(pawn, out Vector3 exactOverrideBoundsPos))
                    {
                        __result = __result.Encapsulate(CellRect.CenteredOn(IntVec3.FromVector3(exactOverrideBoundsPos), 14));
                    }
                }
            }
            else if (t is Corpse corpse && corpse.InnerPawn != null)
            {
                CompGiantRig corpseComp = GiantFastLookup.TryGetRig(corpse.InnerPawn);
                if (corpseComp != null)
                {
                    CellRect bounds = corpseComp.Footprint.worldDrawBounds;
                    if (!bounds.IsEmpty)
                    {
                        __result = __result.Encapsulate(bounds);
                    }
                }
            }
        }

        /// <summary>
        /// Prevents giant creatures from shifting 0.32 tiles sideways due to vanilla small-pawn crowd tweener.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnCollisionTweenerUtility), nameof(PawnCollisionTweenerUtility.PawnCollisionPosOffsetFor))]
        public static bool PawnCollisionPosOffsetFor_Prefix(Pawn pawn, ref Vector3 __result)
        {
            if (GiantFastLookup.TryGetRig(pawn) != null)
            {
                __result = Vector3.zero;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Suppresses vanilla whole-pawn red DamageFlasher and small-pawn downed wiggler on multi-module giants
        /// so only the specific struck bone/whip flashes red via per-node MaterialPropertyBlock tinting.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.Notify_DamageApplied))]
        public static bool PawnRenderer_Notify_DamageApplied_Prefix(Pawn ___pawn)
        {
            if (GiantFastLookup.TryGetRig(___pawn) != null)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Suppresses ground-level pawn nameplates and miniature health bars on giants with a Boss Health Bar Def
        /// as well as all player-faction giants (player-faction giants must display zero health bars).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(PawnUIOverlay), nameof(PawnUIOverlay.DrawPawnGUIOverlay))]
        public static bool PawnUIOverlay_DrawPawnGUIOverlay_Prefix(Pawn ___pawn)
        {
            if (___pawn != null &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp &&
                (comp.RigDef?.healthBarDef != null || GiantVehicleBehaviorGuard.IsPlayerFaction(___pawn.Faction)))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Prevents DynamicDrawManager.DrawSilhouettes from throwing NullReferenceException at zoom-out when
        /// HighlightStyleMode.Silhouettes is active: multi-bone skeletal giants use PawnRenderNode_GiantRoot
        /// rather than a single 4-way BodyGraphic, leaving PawnRenderer.SilhouetteGraphic null.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(SilhouetteUtility), nameof(SilhouetteUtility.ShouldDrawSilhouette))]
        public static bool SilhouetteUtility_ShouldDrawSilhouette_Prefix(Thing thing, ref bool __result)
        {
            if (thing is Pawn pawn && GiantFastLookup.TryGetRig(pawn) != null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Prevents Graphic_PawnBodySilhouette.DrawWorker (e.g. Biotech Mote_MechUncontrolled on overseer-less
        /// player mechanoids, Anomaly/Psycast skip motes) from throwing NullReferenceException when linked to a
        /// skeletal giant whose PawnRenderTree has no monolithic PawnRenderer.BodyGraphic.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(GiantRenderIsolationGuard.ShieldFirstPriority)]
        [HarmonyPatch(typeof(Graphic_PawnBodySilhouette), nameof(Graphic_PawnBodySilhouette.DrawWorker))]
        public static bool Graphic_PawnBodySilhouette_DrawWorker_Prefix(Thing thing, Pawn ___lastPawn)
        {
            if (thing is Mote mote)
            {
                Thing linked = mote.link1.Target.Thing;
                Pawn targetPawn = (linked as Corpse)?.InnerPawn ?? (linked as Pawn) ?? ___lastPawn;
                if (targetPawn != null && GiantFastLookup.TryGetRig(targetPawn) != null)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
