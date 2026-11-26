using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using GiantCreatureFramework;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Tests
{
    /// <summary>
    /// Non-destructive offline QA diagnostic and performance benchmark suite.
    /// Invoked via `dotnet run --project .\Tests\GiantCreatureFramework.Tests.csproj -c Release -- --qa-audit`.
    /// Each diagnostic probe deterministically tests a specific logic, visual-math, edge-case, or
    /// performance hypothesis and classifies whether a real bug is reproduced (`BUG_REPRODUCED`) or clean (`OK`).
    /// </summary>
    internal static class QaBugDiagnosticSuite
    {
        private sealed class ProbeResult
        {
            public string Id;
            public string Category;
            public string Severity;
            public string Title;
            public string Outcome; // "BUG_REPRODUCED", "OK", "ERROR"
            public string Evidence;
            public double ElapsedMs;
        }

        private sealed class BenchResult
        {
            public string Id;
            public string Title;
            public int Iterations;
            public double TotalMs;
            public double UsPerIter;
            public long AllocBytesTotal;
            public double AllocBytesPerIter;
            public string Notes;
        }

        private static readonly List<ProbeResult> Probes = new List<ProbeResult>();
        private static readonly List<BenchResult> Benches = new List<BenchResult>();

        public static int RunAll(string modRoot)
        {
            Probes.Clear();
            Benches.Clear();

            Console.WriteLine("======================================================================");
            Console.WriteLine("  GiantCreatureFramework Offline QA Bug Diagnostic & Perf Suite");
            Console.WriteLine("======================================================================");

            var (scorpRig, scorpMasks, scorpAnims) = Program.LoadRigFromXml("GiantRigDefs_MechScorpion.xml", "GiantAnimDefs_MechScorpion.xml");
            var (valRig, valMasks, valAnims) = Program.LoadRigFromXml("GiantRigDefs_Valstrax.xml", "GiantAnimDefs_Valstrax.xml");
            var (rathRig, rathMasks, rathAnims) = Program.LoadRigFromXml("GiantRigDefs_Rathalos.xml", "GiantAnimDefs_Rathalos.xml");
            var (panzerRig, panzerMasks, panzerAnims) = Program.LoadRigFromXml("GiantRigDefs_PanzerIV.xml", "GiantAnimDefs_PanzerIV.xml");
            var (carrierRig, carrierMasks, carrierAnims) = Program.LoadRigFromXml("GiantRigDefs_ProtossCarrier.xml", "GiantAnimDefs_ProtossCarrier.xml");
            var (colRig, colMasks, colAnims) = Program.LoadRigFromXml("GiantRigDefs_MechColossus.xml", "GiantAnimDefs_MechColossus.xml", "GCF_Rig_MechColossus_Stage1");

            // --- Logic, Visual-Math, Memory & Edge-Case Diagnostic Probes ---
            RunProbe("QA-CORE-01", "Logic/Crash", "High", "SoftWhipRuntime particleCount<=16 strike IndexOutOfRangeException",
                () => ProbeSoftWhipLowParticleCrash(scorpRig));

            RunProbe("QA-CORE-02", "Logic/Visual", "High", "DampedBezierChainRuntime resets physics state when deltaTicks==0",
                () => ProbeBezierZeroDeltaReset(scorpRig));

            RunProbe("QA-CORE-03", "Logic/Visual", "High", "GiantSteeringRuntime turret 352-deg wrap flip & intensity!=1 aim error",
                ProbeSteeringTurretWrapAndIntensity);

            RunProbe("QA-CORE-04", "Visual/Kinematics", "High", "SkeletalKinematics nested coronal flap hinge detaches child socket from parent tip",
                ProbeNestedCoronalHingeGap);

            RunProbe("QA-CORE-05", "Visual/Perf", "High", "GiantSkinnedMeshDef capsule sigVal overwrite & HasVisibilityChanged thrash on 0x0",
                () => ProbeSkinnedMeshCapsuleAndVisibility(rathRig, rathMasks));

            RunProbe("QA-CORE-06", "Logic/Visual", "Medium", "GiantAnimDef DidCrossEventTick tick-0 miss / backward false-fire & InvertEasing Cubic skip",
                () => ProbeAnimDefEdgeCases(scorpRig));

            RunProbe("QA-CORE-07", "Visual/Logic", "High", "GiantSleepCurlSolver unblended leg snap at wCoil~0 & jaw misclassified as neck",
                () => ProbeSleepCurlLegSnapAndJaw(rathRig, valRig));

            RunProbe("QA-CORE-08", "Logic/Footprint", "Medium", "GiantFootprintData.ClearCollisionVolume non-idempotent wipes groundedLandingOffsets",
                () => ProbeFootprintClearCollisionNonIdempotent(rathRig, rathMasks));

            RunProbe("QA-CORE-09", "Visual", "High", "GiantSeveredDebrisSystem shadow alpha discontinuity across h=0.035",
                ProbeDebrisShadowDiscontinuity);

            RunProbe("QA-CORE-10", "Logic/Combat", "Critical", "Verb_TryFindShootLineFromTo_Transpiler patches get_Cell #3 (OutOfRange) instead of #4 (!requireLOS)",
                ProbeVerbShootLineTranspilerCallIndex);

            RunProbe("QA-CORE-11", "Logic/Movement", "Critical", "Vehicle/Cockpit/FlightVehicle MoveSpeed squared/cubed across StatWorker + TicksPerMove + PatherTick",
                ProbeDoubleTripleMoveSpeedScaling);

            RunProbe("QA-CORE-12", "Logic/Rig", "Medium", "CompGiantRig 64-bit ulong missing-mask truncates bone index >= 64 (Colossus already at 64/64)",
                () => ProbeMissingMask64BitLimit(colRig));

            RunProbe("QA-CORE-13", "Visual/UI", "High", "GiantBossHealthBarHUD 2-col compact text-over-meter overlap & 10px title overlap",
                ProbeBossHudCompactOverlap);

            RunProbe("QA-CORE-14", "Visual/Layering", "Medium", "Dead/Downed giants render ABOVE Projectile altitude; CarrierInterceptor renders BELOW Carrier",
                ProbeAltitudeLayerOrderingInversions);

            RunProbe("QA-CORE-15", "Visual/Seats", "High", "Mounted crew rider north of host center clips beneath dorsal parts at layer -10",
                () => ProbeCrewSeatNorthernClipping(rathRig));

            RunProbe("QA-CORE-16", "Visual/World", "Critical", "World-map giant lifted by ~8.415 map Pawn altitude; bone glow overlay omits glowLayerOffset",
                ProbeWorldAltitudeAndBoneGlowLayer);

            RunProbe("QA-CORE-17", "Logic/Flight", "High", "Grounded-departing flyer EffectiveFlightSpeedMultiplier==1f dead code; FixedWing LoiterOrbit out-of-bounds",
                () => ProbeFlightWorldSpeedAndFixedWingLoiter(carrierRig));

            RunProbe("QA-CORE-18", "Logic/SaveLoad", "High", "GiantTargetAimTracker static lastSweepTick disables expiry after loading earlier-tick save",
                ProbeAimTrackerCrossSaveTickUnderflow);

            RunProbe("QA-CORE-19", "Perf/Memory", "Critical", "GiantFootprintMapComponent owns Allocator.Persistent NativeArrays without implementing IDisposable",
                ProbeFootprintMapCompDisposableContract);

            RunProbe("QA-CORE-20", "Perf/Memory", "Critical", "Dynamic render nodes never destroy Waylith GPU Meshes; CompGiantCockpit uses ThingOwner<Pawn>",
                ProbeGpuMeshCleanupAndCockpitContainerType);

            RunProbe("QA-CORE-21", "Logic/Progression", "Critical", "VaultCompsByPawnId static dict cross-save thingIDNumber collision without parent equality check",
                ProbeVaultStaticDictCrossSaveCollision);

            RunProbe("QA-CORE-22", "Logic/Breach", "Critical", "Wild/Manhunter (Faction==null) giant blocked by IsProtectedAlliedThing (HostileTo(null)==false)",
                ProbeWildGiantBreachProtectionBug);

            // --- Performance & GC Allocation Microbenchmarks ---
            RunBench("QA-PERF-01", "GiantRollingTrackMeshBuilder.BuildClosedLoopStrip per-frame scroll GC alloc (600 frames)",
                600, () => BenchRollingTrackPerFrameAlloc(panzerRig));

            RunBench("QA-PERF-02", "GiantFootprintData.Rebuild throughput & GC alloc (PanzerIV vs Rathalos 44b vs Colossus 64b)",
                200, () => BenchFootprintRebuildAllocAndTime(panzerRig, panzerMasks, rathRig, rathMasks, colRig, colMasks));

            RunBench("QA-PERF-03", "GiantAnimDef.EvaluateInto multi-rig cache thrashing (SameRig vs Alternating2Rigs x 2000)",
                2000, () => BenchAnimCacheThrash(scorpRig, valRig, scorpAnims));

            RunBench("QA-PERF-04", "GiantSteeringRuntime.ApplyBoneSteering (6 giants x 500 ticks) timing & GC alloc",
                500, () => BenchSteeringSixGiants(scorpRig, valRig, rathRig, panzerRig, carrierRig, colRig));

            RunBench("QA-PERF-05", "SkeletalKinematics.SolveWorldPoses + SkinnedMeshRuntime.EvaluateDeformedVertices (Rathalos 44b, 600 frames)",
                600, () => BenchKinematicsAndSkinnedMesh(rathRig, rathMasks, rathAnims));

            int bugs = Probes.Count(p => p.Outcome == "BUG_REPRODUCED");
            int oks = Probes.Count(p => p.Outcome == "OK");
            int errs = Probes.Count(p => p.Outcome == "ERROR");

            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine($"DIAGNOSTIC PROBES: {Probes.Count} total | {bugs} BUG_REPRODUCED | {oks} OK | {errs} ERROR");
            Console.WriteLine($"PERF BENCHMARKS:   {Benches.Count} completed");
            Console.WriteLine("----------------------------------------------------------------------");

            WriteJsonReport(modRoot, bugs, oks, errs);
            return errs > 0 ? 1 : 0;
        }

        private static void RunProbe(string id, string category, string severity, string title, Func<(bool bugReproduced, string evidence)> fn)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (bug, evidence) = fn();
                sw.Stop();
                string outcome = bug ? "BUG_REPRODUCED" : "OK";
                Probes.Add(new ProbeResult
                {
                    Id = id,
                    Category = category,
                    Severity = severity,
                    Title = title,
                    Outcome = outcome,
                    Evidence = evidence,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                });
                Console.WriteLine($"[{outcome,-14}] {id} ({severity,-8} | {category,-17}) {title}");
                Console.WriteLine($"                 -> {evidence}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Probes.Add(new ProbeResult
                {
                    Id = id,
                    Category = category,
                    Severity = severity,
                    Title = title,
                    Outcome = "ERROR",
                    Evidence = $"{ex.GetType().Name}: {ex.Message}",
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                });
                Console.WriteLine($"[ERROR         ] {id} {title} -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RunBench(string id, string title, int iterations, Func<(long allocBytes, string notes)> fn)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var sw = Stopwatch.StartNew();
            var (allocBytes, notes) = fn();
            sw.Stop();
            double totalMs = sw.Elapsed.TotalMilliseconds;
            double usPer = (totalMs * 1000.0) / Math.Max(1, iterations);
            double bytesPer = (double)allocBytes / Math.Max(1, iterations);
            Benches.Add(new BenchResult
            {
                Id = id,
                Title = title,
                Iterations = iterations,
                TotalMs = totalMs,
                UsPerIter = usPer,
                AllocBytesTotal = allocBytes,
                AllocBytesPerIter = bytesPer,
                Notes = notes
            });
            Console.WriteLine($"[PERF          ] {id}: {totalMs:F2} ms ({usPer:F2} us/iter), {allocBytes:N0} B total ({bytesPer:F1} B/iter) | {notes}");
        }

        // =========================================================================
        // PROBE IMPLEMENTATIONS
        // =========================================================================

        private static (bool, string) ProbeSoftWhipLowParticleCrash(GiantRigDef hostRig)
        {
            string rootId = hostRig.SortedNodes[0].nodeId;
            var def = new SoftWhipDef
            {
                whipId = "qa_short_whip",
                rootBoneId = rootId,
                torsoBoneId = rootId,
                particleCount = 12, // Clamped range in SoftWhipRuntime ctor is [8, 96]
                totalLength = 12.0f,
                baseWidth = 1.10f,
                lateralSideSign = -1f,
                strikeAnimDefs = new List<string> { "QA_Strike" }
            };
            def.resolvedRootBoneIndex = 0;
            def.resolvedTorsoBoneIndex = 0;
            def.ResolveCoordinates(512);

            var rt = new SoftWhipRuntime(def) { PartnerTendrilIntact = true };
            var localPoses = new BoneLocalPose[hostRig.SortedNodes.Length];
            var missing = new bool[hostRig.SortedNodes.Length];
            // Step across strike windup -> bilateral snap (uSub >= 0.36) with partner whip intact
            try
            {
                for (int step = 0; step <= 8; step++)
                {
                    var ctx = new BezierChainStepContext
                    {
                        BodyWorldAngleDeg = 0f,
                        ActiveClipDefName = "QA_Strike",
                        ActiveClipNormalizedTime = 0.20f + step * 0.04f,
                        HasAttackTarget = true,
                        AttackTargetWorldXZ = new Vector2(0f, 8f)
                    };
                    rt.Step(hostRig, Vector2.zero, 0f, localPoses, missing, false, 1f, in ctx);
                }
                return (false, "Stepped 12-particle SoftWhip bilateral strike without throwing.");
            }
            catch (IndexOutOfRangeException ex)
            {
                return (true, $"Crashed with IndexOutOfRangeException on valid particleCount=12 (ctor clamps [8,96], bilateral strike indexes hardcoded pos[16]): {ex.Message}");
            }
        }

        private static (bool, string) ProbeBezierZeroDeltaReset(GiantRigDef scorpRig)
        {
            if (scorpRig.bezierChains == null || scorpRig.bezierChains.Count == 0)
            {
                return (false, "No bezier chains on scorpion rig.");
            }
            var chain = scorpRig.bezierChains[0];
            var rt = new DampedBezierChainRuntime(chain);
            var poses = new BoneLocalPose[scorpRig.SortedNodes.Length];
            var missing = new bool[scorpRig.SortedNodes.Length];
            var ctx = new BezierChainStepContext();

            for (int t = 0; t < 20; t++)
            {
                rt.Step(scorpRig, new Vector2(t * 0.25f, 0f), t * 6f, poses, missing, 1f, in ctx);
            }
            bool settlingBefore = rt.IsSettling;
            float angleBefore = poses[chain.resolvedBoneIndices[0]].angle;

            rt.Step(scorpRig, new Vector2(19 * 0.25f, 0f), 19 * 6f, poses, missing, 0f, in ctx);
            bool settlingAfter = rt.IsSettling;
            float angleAfter = poses[chain.resolvedBoneIndices[0]].angle;

            bool bug = settlingBefore && !settlingAfter;
            return (bug, $"Before dt=0: IsSettling={settlingBefore}, bone0Angle={angleBefore:F2} deg -> After dt=0: IsSettling={settlingAfter}, bone0Angle={angleAfter:F2} deg (delta={Mathf.Abs(angleAfter - angleBefore):F2} deg).");
        }

        private static (bool, string) ProbeSteeringTurretWrapAndIntensity()
        {
            // 1) Limited-arc turret gyro-stabilization wrap flip (+/-180 deg seam)
            var rigLim = new GiantRigDef { defName = "QA_LimTurret" };
            rigLim.nodes = new List<BoneNodeDef>
            {
                new BoneNodeDef { nodeId = "hull", drawSize = new Vector2(4f, 4f) },
                new BoneNodeDef { nodeId = "turret", parentId = "hull", isTurret = true, drawSize = new Vector2(2f, 2f) }
            };
            rigLim.steering = new SteeringConfigDef
            {
                enabled = true,
                lookChains = new List<SteeringLookChainDef>
                {
                    new SteeringLookChainDef
                    {
                        chainId = "lim",
                        boneIds = new List<string> { "turret" },
                        weights = new List<float> { 1f },
                        maxChainAngleDeg = 178f,
                        maxBoneAngleDeg = 178f,
                        full360Traverse = false,
                        turnSpeedDegPerTick = 3f,
                        responsiveness = 1f,
                        combatTargetWeight = 1f
                    }
                }
            };
            rigLim.ResolveReferences();

            var stLim = new GiantSteeringRuntime(rigLim);
            stLim.Reset(0f);
            var poses = new BoneLocalPose[2];
            var missing = new bool[2];
            for (int t = 0; t < 80; t++)
            {
                poses[0] = default; poses[1] = default;
                var ctx = new GiantSteeringContext
                {
                    DesiredBodyHeadingDeg = 0f,
                    DesiredLookHeadingDeg = 176f,
                    HasExplicitLookTarget = true,
                    IsCombatTarget = true,
                    SteeringEnabled = true,
                    IntensityScale = 1f
                };
                stLim.ApplyBoneSteering(rigLim, 0f, poses, missing, 1f, in ctx);
            }
            float beforeHullTurn = poses[1].angle;

            poses[0] = default; poses[1] = default;
            var ctxStep = new GiantSteeringContext
            {
                DesiredBodyHeadingDeg = -8f,
                DesiredLookHeadingDeg = 176f,
                HasExplicitLookTarget = true,
                IsCombatTarget = true,
                SteeringEnabled = true,
                IntensityScale = 1f,
                StabilizeTurretsAgainstHullTurn = true,
                LastBodyHeadingStepDeg = -8f
            };
            stLim.ApplyBoneSteering(rigLim, -8f, poses, missing, 1f, in ctxStep);
            float afterHullTurn = poses[1].angle;
            float turretJump = Mathf.Abs(afterHullTurn - beforeHullTurn);

            // 2) Full 360 turret with IntensityScale = 0.5 aims off-target
            var rig360 = new GiantRigDef { defName = "QA_360Turret" };
            rig360.nodes = new List<BoneNodeDef>
            {
                new BoneNodeDef { nodeId = "hull", drawSize = new Vector2(4f, 4f) },
                new BoneNodeDef { nodeId = "turret", parentId = "hull", isTurret = true, drawSize = new Vector2(2f, 2f) }
            };
            rig360.steering = new SteeringConfigDef
            {
                enabled = true,
                lookChains = new List<SteeringLookChainDef>
                {
                    new SteeringLookChainDef
                    {
                        chainId = "full360",
                        boneIds = new List<string> { "turret" },
                        weights = new List<float> { 1f },
                        maxChainAngleDeg = 180f,
                        maxBoneAngleDeg = 180f,
                        full360Traverse = true,
                        turnSpeedDegPerTick = 180f,
                        responsiveness = 1f,
                        combatTargetWeight = 1f
                    }
                }
            };
            rig360.ResolveReferences();
            var st360 = new GiantSteeringRuntime(rig360);
            st360.Reset(0f);
            poses[0] = default; poses[1] = default;
            var ctx360 = new GiantSteeringContext
            {
                DesiredBodyHeadingDeg = 0f,
                DesiredLookHeadingDeg = 120f,
                HasExplicitLookTarget = true,
                IsCombatTarget = true,
                SteeringEnabled = true,
                IntensityScale = 0.5f
            };
            st360.ApplyBoneSteering(rig360, 0f, poses, missing, 1f, in ctx360);
            float aimed360 = poses[1].angle;

            bool bug = turretJump > 300f || Mathf.Abs(aimed360 - 120f) > 30f;
            return (bug, $"Limited-arc turret jumped {turretJump:F1} deg on -8 deg hull step ({beforeHullTurn:F1} -> {afterHullTurn:F1} deg); 360-deg turret with IntensityScale=0.5 aimed at {aimed360:F1} deg instead of 120.0 deg.");
        }

        private static (bool, string) ProbeNestedCoronalHingeGap()
        {
            var rig = new GiantRigDef { defName = "QA_NestedCoronalRig" };
            rig.nodes = new List<BoneNodeDef>
            {
                new BoneNodeDef { nodeId = "body", drawSize = new Vector2(4f, 4f), pivotUV = new Vector2(0.5f, 0.5f) },
                new BoneNodeDef
                {
                    nodeId = "wing_upper",
                    parentId = "body",
                    drawSize = new Vector2(4f, 4f),
                    pivotUV = new Vector2(0f, 0.5f),
                    parentSocketUV = new Vector2(0.75f, 0.5f),
                    isCoronalFlapHinge = true
                },
                new BoneNodeDef
                {
                    nodeId = "wing_lower",
                    parentId = "wing_upper",
                    drawSize = new Vector2(4f, 4f),
                    pivotUV = new Vector2(0f, 0.5f),
                    parentSocketUV = new Vector2(1.0f, 0.5f),
                    isCoronalFlapHinge = true
                }
            };
            rig.ResolveReferences();

            var local = new BoneLocalPose[3];
            local[1] = new BoneLocalPose { coronalFlap = 60f }; // cos(60)=0.5
            local[2] = new BoneLocalPose { coronalFlap = 45f }; // nested hinge
            var poses = new BoneWorldPose[3];
            SkeletalKinematics.SolveWorldPoses(rig, Vector2.zero, 0f, local, ReadOnlySpan<bool>.Empty, poses, 1f);

            // Where wing_upper's projected tip renders vs where wing_lower's projected pivot renders!
            Vector2 upperTipProjected = poses[1].UVToWorldXZ(1.0f, 0.5f);
            Vector2 lowerPivotProjected = poses[2].UVToWorldXZ(0.0f, 0.5f);
            float gap = Vector2.Distance(upperTipProjected, lowerPivotProjected);
            return (gap > 0.05f, $"Nested coronal hinge gap: upper projected tip={upperTipProjected}, child projected pivot={lowerPivotProjected}, detachment={gap:F3} cells.");
        }

        private static (bool, string) ProbeSkinnedMeshCapsuleAndVisibility(GiantRigDef rathRig, TextureAlphaMask[] rathMasks)
        {
            // 1) Test HasVisibilityChanged thrashing when all member bones are missing (visMask == 0)
            var meshDef = rathRig.skinnedMeshes?.FirstOrDefault(m => m.resolvedMemberBoneIndices != null && m.resolvedMemberBoneIndices.Length > 0);
            bool chg1 = false, chg2 = false;
            if (meshDef != null)
            {
                var runtime = new SkinnedMeshRuntime(rathRig, meshDef);
                var poses = new BoneWorldPose[rathRig.SortedNodes.Length];
                var allMissing = new bool[rathRig.SortedNodes.Length];
                for (int i = 0; i < allMissing.Length; i++) allMissing[i] = true;
                SkeletalKinematics.SolveWorldPoses(rathRig, Vector2.zero, 0f, ReadOnlySpan<BoneLocalPose>.Empty, allMissing, poses, 1f);

                runtime.EnsureBindTopology(null);
                runtime.EvaluateDeformedVertices(poses, Vector2.zero, false, out _);
                chg1 = runtime.HasVisibilityChanged(poses, false);
                chg2 = runtime.HasVisibilityChanged(poses, false);
            }

            // 2) Synthetic 2-capsule test for `sigVal = seg.radius` outside `if (d2 < minD2)`
            var capRig = new GiantRigDef { defName = "QA_CapRig" };
            capRig.nodes = new List<BoneNodeDef>
            {
                new BoneNodeDef { nodeId = "b0", drawSize = new Vector2(4f, 4f) },
                new BoneNodeDef { nodeId = "b1", parentId = "b0", drawSize = new Vector2(4f, 4f), parentSocketUV = new Vector2(0.5f, 0.75f) }
            };
            var smDef = new GiantSkinnedMeshDef
            {
                meshId = "cap_test",
                texPath = "Dummy",
                drawSize = new Vector2(4f, 4f),
                subdivisionsX = 6,
                subdivisionsY = 6,
                anchorBoneId = "b0",
                boneIds = new List<string> { "b0", "b1" },
                memberBoneIds = new List<string> { "b0", "b1" },
                capsules = new List<GiantSkinCapsuleDef>
                {
                    new GiantSkinCapsuleDef { boneId = "b0", startXZ = new Vector2(0f, -1.6f), endXZ = new Vector2(0f, -0.4f), radius = 0.05f },
                    new GiantSkinCapsuleDef { boneId = "b0", startXZ = new Vector2(0f, 1.2f), endXZ = new Vector2(0f, 1.6f), radius = 3.50f },
                    new GiantSkinCapsuleDef { boneId = "b1", startXZ = new Vector2(0f, 0.0f), endXZ = new Vector2(0f, 0.8f), radius = 0.05f }
                }
            };
            capRig.skinnedMeshes = new List<GiantSkinnedMeshDef> { smDef };
            capRig.ResolveReferences();

            var capRt = new SkinnedMeshRuntime(capRig, smDef);
            capRt.EnsureBindTopology(null);
            if (meshDef == null)
            {
                var cPoses = new BoneWorldPose[2];
                var allMiss2 = new bool[] { true, true };
                SkeletalKinematics.SolveWorldPoses(capRig, Vector2.zero, 0f, ReadOnlySpan<BoneLocalPose>.Empty, allMiss2, cPoses, 1f);
                capRt.EvaluateDeformedVertices(cPoses, Vector2.zero, false, out _);
                chg1 = capRt.HasVisibilityChanged(cPoses, false);
                chg2 = capRt.HasVisibilityChanged(cPoses, false);
            }
            var bindField = typeof(SkinnedMeshRuntime).GetField("bindings", BindingFlags.Instance | BindingFlags.NonPublic);
            string capDiag = "reflection_unavailable";
            bool capBug = false;
            if (bindField?.GetValue(capRt) is SkinnedVertexBinding[] bindArr && bindArr.Length > 0)
            {
                int bestV = 0;
                float bestDist = float.MaxValue;
                Vector2 target = new Vector2(0f, 0.4f);
                for (int v = 0; v < bindArr.Length; v++)
                {
                    float d = Vector2.Distance(bindArr[v].restRigXZ, target);
                    if (d < bestDist) { bestDist = d; bestV = v; }
                }
                SkinnedVertexBinding bv = bindArr[bestV];
                int[] bIds = { bv.b0, bv.b1, bv.b2, bv.b3 };
                float[] bWs = { bv.w0, bv.w1, bv.w2, bv.w3 };
                float wB0 = 0f, wB1 = 0f;
                for (int k = 0; k < bv.influenceCount && k < 4; k++)
                {
                    if (bIds[k] == 0) wB0 += bWs[k];
                    else if (bIds[k] == 1) wB1 += bWs[k];
                }
                capBug = wB0 > 0.35f;
                capDiag = $"vert@b1Center({bv.restRigXZ.x:F2},{bv.restRigXZ.y:F2}): wB0={wB0:F3} (polluted by last cap r=3.5), wB1={wB1:F3}";
            }

            bool bug = (chg1 && chg2) || capBug;
            return (bug, $"HasVisibilityChanged on 0x0 hidden mesh: call1={chg1}, call2={chg2} (thrashes true every frame); Capsule test: {capDiag}.");
        }

        private static (bool, string) ProbeAnimDefEdgeCases(GiantRigDef scorpRig)
        {
            bool crossTick0 = GiantAnimDef.DidCrossEventTick(0f, 1f, 0f, 60f, true);
            bool crossBackward = GiantAnimDef.DidCrossEventTick(25f, 10f, 50f, 60f, true);

            var invMethod = typeof(BoneAnimTrack).GetMethod("InvertEasing", BindingFlags.Static | BindingFlags.NonPublic);
            AnimEasing invCubicIn = invMethod != null ? (AnimEasing)invMethod.Invoke(null, new object[] { AnimEasing.EaseInCubic }) : AnimEasing.EaseOutCubic;

            bool bug = !crossTick0 || crossBackward || invCubicIn == AnimEasing.EaseInCubic;
            return (bug, $"DidCrossEventTick(0->1, evt=0)={crossTick0} (missed!), DidCrossEventTick(25->10, evt=50, loop)={crossBackward} (false-fire on backward scrub!), InvertEasing(EaseInCubic)={invCubicIn} (expected EaseOutCubic).");
        }

        private static (bool, string) ProbeSleepCurlLegSnapAndJaw(GiantRigDef rathRig, GiantRigDef abyssalRig)
        {
            var poses = new BoneLocalPose[abyssalRig.SortedNodes.Length];
            int legIdx = Array.FindIndex(abyssalRig.SortedNodes, n => n.nodeId.Contains("leg_fl"));
            if (legIdx < 0) legIdx = 1;
            poses[legIdx] = new BoneLocalPose { angle = 35f };
            GiantSleepCurlSolver.ApplySleepCurl(abyssalRig, poses, ReadOnlySpan<bool>.Empty, 0.01f, 1f, 0f);
            float legAfterTinySleep = poses[legIdx].angle;

            var prof = GiantSleepCurlSolver.GetOrBuildProfile(rathRig);
            int jawIdx = Array.FindIndex(rathRig.SortedNodes, n => n.nodeId.Contains("jaw"));
            string jawRole = "none";
            if (jawIdx >= 0 && prof != null)
            {
                var rolesField = prof.GetType().GetField("Roles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (rolesField?.GetValue(prof) is Array arr && jawIdx < arr.Length)
                {
                    jawRole = arr.GetValue(jawIdx)?.ToString() ?? "null";
                }
            }

            bool bug = Mathf.Abs(legAfterTinySleep) < 1f || jawRole == "AnteriorNeckHead";
            return (bug, $"At sleepProgress=0.01 (wCoil~0), leg angle snapped 35.0 -> {legAfterTinySleep:F2} deg; Rathalos jaw bone role='{jawRole}' (twists jaw sideways during sleep curl).");
        }

        private static (bool, string) ProbeFootprintClearCollisionNonIdempotent(GiantRigDef rathRig, TextureAlphaMask[] rathMasks)
        {
            var fp = new GiantFootprintData();
            var local = new BoneLocalPose[rathRig.SortedNodes.Length];
            var world = new BoneWorldPose[rathRig.SortedNodes.Length];
            SkeletalKinematics.SolveWorldPoses(rathRig, new Vector2(50.5f, 50.5f), 0f, local, ReadOnlySpan<bool>.Empty, world, 1f);
            fp.Rebuild(rathRig, world, rathMasks, new IntVec3(50, 0, 50));
            int origColCount = fp.collisionCells.Count;

            fp.ClearCollisionVolume();
            int afterFirstClearLanding = fp.groundedLandingOffsets.Count;

            fp.ClearCollisionVolume();
            int afterSecondClearLanding = fp.groundedLandingOffsets.Count;

            bool bug = origColCount > 0 && afterFirstClearLanding > 0 && afterSecondClearLanding == 0;
            return (bug, $"Initial collisionCells={origColCount}; after 1st ClearCollisionVolume() groundedLandingOffsets={afterFirstClearLanding}; after 2nd ClearCollisionVolume() groundedLandingOffsets={afterSecondClearLanding} (wiped!).");
        }

        private static (bool, string) ProbeDebrisShadowDiscontinuity()
        {
            float aAbove = GiantSeveredDebrisSystem.ComputeDebrisShadowAlpha(0.036f);
            float aBelow = GiantSeveredDebrisSystem.ComputeDebrisShadowAlpha(0.034f);
            float jump = Mathf.Abs(aAbove - aBelow);
            return (jump > 0.05f, $"ComputeDebrisShadowAlpha across h=0.035: h=0.036 -> {aAbove:F4}, h=0.034 -> {aBelow:F4} (delta={jump:F4}).");
        }

        private static (bool, string) ProbeVerbShootLineTranspilerCallIndex()
        {
            MethodInfo target = typeof(Verb).GetMethod(nameof(Verb.TryFindShootLineFromTo),
                new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(bool) });
            MethodInfo getCell = typeof(LocalTargetInfo).GetProperty(nameof(LocalTargetInfo.Cell)).GetGetMethod();
            MethodInfo outOfRange = typeof(Verb).GetMethod(nameof(Verb.OutOfRange));
            FieldInfo reqLos = typeof(VerbProperties).GetField(nameof(VerbProperties.requireLineOfSight));
            MethodInfo resolveRef = typeof(HarmonyPatches_CombatAndHitbox).GetMethod(
                "ResolveShootLineTargetCellRef", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var origInstrs = PatchProcessor.GetOriginalInstructions(target);
            var patchedInstrs = HarmonyPatches_CombatAndHitbox.Verb_TryFindShootLineFromTo_Transpiler(origInstrs).ToList();

            int outOfRangeIdx = -1;
            int reqLosIdx = -1;
            int getCellBetweenOutOfRangeAndReqLos = 0;
            int resolveBetweenOutOfRangeAndReqLos = 0;
            int resolveAfterReqLos = 0;

            for (int i = 0; i < patchedInstrs.Count; i++)
            {
                var ci = patchedInstrs[i];
                if (ci.Calls(outOfRange)) outOfRangeIdx = i;
                if (ci.LoadsField(reqLos)) reqLosIdx = i;
                if (outOfRangeIdx >= 0 && reqLosIdx < 0)
                {
                    if (ci.Calls(getCell)) getCellBetweenOutOfRangeAndReqLos++;
                    if (resolveRef != null && ci.Calls(resolveRef)) resolveBetweenOutOfRangeAndReqLos++;
                }
                else if (reqLosIdx >= 0 && resolveRef != null && ci.Calls(resolveRef))
                {
                    resolveAfterReqLos++;
                }
            }

            bool bug = resolveBetweenOutOfRangeAndReqLos > 0 || getCellBetweenOutOfRangeAndReqLos == 0 || resolveAfterReqLos == 0;
            return (bug,
                $"Transpiled Verb.TryFindShootLineFromTo: OutOfRange..requireLOS has get_Cell={getCellBetweenOutOfRangeAndReqLos}, resolveRef={resolveBetweenOutOfRangeAndReqLos}; after requireLOS has resolveRef={resolveAfterReqLos}.");
        }

        private static (bool, string) ProbeDoubleTripleMoveSpeedScaling()
        {
            MethodInfo tpmPatch = typeof(HarmonyPatches_VehicleAndTurrets).GetMethod(nameof(HarmonyPatches_VehicleAndTurrets.Pawn_TicksPerMove_Crew_Postfix));
            var tpmInstrs = tpmPatch != null ? PatchProcessor.GetOriginalInstructions(tpmPatch) : new List<CodeInstruction>();
            bool tpmAppliesCrewMoveSpeed = tpmInstrs.Any(ci =>
                ci.operand is MethodInfo mi &&
                (mi.Name.Contains("GetMoveSpeedFactor") || mi.Name.Contains("EffectiveDriveSpeedMultiplier") || mi.Name.Contains("EffectivePropulsionSpeedMultiplier")));

            bool fvMultipliesPropulsionSpeed = typeof(FlightVehicleLocomotionRuntime)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.Name.Contains("Step", StringComparison.Ordinal))
                .Any(m => PatchProcessor.GetOriginalInstructions(m).Any(ci =>
                    ci.operand is MethodInfo mi && mi.Name.Contains("EffectivePropulsionSpeedMultiplier")));

            bool bug = tpmAppliesCrewMoveSpeed || fvMultipliesPropulsionSpeed;
            return (bug, $"Pawn_TicksPerMove_Crew_Postfix re-applies speed factor={tpmAppliesCrewMoveSpeed}, FlightVehicleLocomotionRuntime.Step* re-multiplies EffectivePropulsionSpeedMultiplier={fvMultipliesPropulsionSpeed}.");
        }

        private static (bool, string) ProbeMissingMask64BitLimit(GiantRigDef colRig)
        {
            int colBoneCount = colRig.SortedNodes.Length;
            // Find the ulong missing-mask field in CompGiantRig
            FieldInfo fBits = typeof(CompGiantRig)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(ulong) && f.Name.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0);
            bool is64Bit = fBits != null && fBits.FieldType == typeof(ulong);
            ulong bits = 0UL;
            bool[] mask66 = new bool[66];
            mask66[64] = true;
            mask66[65] = true;
            for (int i = 0; i < mask66.Length && i < 64; i++)
            {
                if (mask66[i]) bits |= 1UL << i;
            }
            bool bug = is64Bit && bits == 0UL;
            return (bug, $"CompGiantRig.{fBits?.Name ?? "missing"} type={fBits?.FieldType.Name ?? "missing"} (64-bit cap); MechColossus already uses {colBoneCount}/64 bones; severing bone 64/65 yields bits=0x{bits:X} (silently ignored).");
        }

        private static (bool, string) ProbeBossHudCompactOverlap()
        {
            float colW = 200f;
            float colGap = 14f;
            float titleW = GiantBossHealthBarHUD.ComputeBossTitleWidth(colW, compactMode: true);
            float titleOverlapPx = Mathf.Max(0f, titleW - colW - colGap);
            GiantBossHealthBarHUD.ComputeSubPartRowLayout(
                totalW: colW,
                compactMode: true,
                needsScroll: true,
                rowH: 20f,
                defSubMeterWidth: 110f,
                depth: 5,
                out float labelX,
                out float nameW,
                out float meterX,
                out float meterW,
                out float pctLabelW);

            bool bug = (labelX + nameW > meterX + 0.01f) || titleW > colW;
            return (bug, $"2-col compact (colW=200, depth=5): titleW={titleW:F1}px (overlap={titleOverlapPx:F1}px), labelX={labelX:F1}px, nameW={nameW:F2}px, meterX={meterX:F2}px (meterW={meterW:F2}, pctW={pctLabelW:F1}).");
        }

        private static (bool, string) ProbeAltitudeLayerOrderingInversions()
        {
            float projY = Altitudes.AltitudeFor(AltitudeLayer.Projectile);
            float deadGiantY = CompGiantRig.ComputeGiantEntityBaseWorldY(
                isPortrait: false, isDead: true, isDowned: false,
                rootColY: 0f, worldZ: 0f, thingIdNumber: 1001,
                isFlyingVisual: false, flightAltitudeBoost: 0f);
            float downedGiantY = CompGiantRig.ComputeGiantEntityBaseWorldY(
                isPortrait: false, isDead: false, isDowned: true,
                rootColY: 0f, worldZ: 0f, thingIdNumber: 1001,
                isFlyingVisual: false, flightAltitudeBoost: 0f);

            float skyfallerDeltaY = (Altitudes.AltitudeFor(AltitudeLayer.Skyfaller) + 0.04f) - Altitudes.AltitudeFor(AltitudeLayer.Pawn);
            float highAirborneCarrierY = CompGiantRig.ComputeGiantEntityBaseWorldY(
                isPortrait: false, isDead: false, isDowned: false,
                rootColY: 0f, worldZ: 0f, thingIdNumber: 1002,
                isFlyingVisual: true, flightAltitudeBoost: skyfallerDeltaY);
            var interceptor = (CarrierInterceptor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CarrierInterceptor));
            float interceptorY = interceptor.DrawPos.y;

            bool corpseAboveProj = deadGiantY >= projY || downedGiantY >= projY;
            bool interceptorBelowCarrier = interceptorY <= highAirborneCarrierY;
            return (corpseAboveProj || interceptorBelowCarrier,
                $"DeadGiantY={deadGiantY:F4}, DownedGiantY={downedGiantY:F4}, ProjectileY={projY:F4}; InterceptorY={interceptorY:F4}, HighAirborneCarrierY={highAirborneCarrierY:F4}.");
        }

        private static (bool, string) ProbeCrewSeatNorthernClipping(GiantRigDef rathRig)
        {
            float maxPartY = CompGiantRig.ComputeGiantPartWorldAltitude(
                isPortrait: false, isDead: false, isDowned: false, rootColY: 0f, worldZ: 0f,
                thingIdNumber: 1001, isFlyingVisual: false, flightAltitudeBoost: 0f,
                partLayerOffset: rathRig.maxAuthoredLayerOffset);
            float partAltY = maxPartY + 2.0f * 0.00025f;
            float clearanceY = CompGiantRig.CrewRenderTreeFloorClearanceY;
            float zSortTieBreak = Mathf.Clamp((0f - 10f) * 0.00035f, -0.0030f, 0.0030f);
            float riderBaseY = partAltY + clearanceY + zSortTieBreak;
            float riderBackLayerY = riderBaseY + PawnRenderUtility.AltitudeForLayer(-10f);
            float deficit = maxPartY - riderBackLayerY;
            return (deficit > 0f, $"Rider 10 cells north with clearanceY={clearanceY:F4}: riderLayer(-10)={riderBackLayerY:F6} vs maxPartY={maxPartY:F6} (deficit={deficit:F6}).");
        }

        private static (bool, string) ProbeWorldAltitudeAndBoneGlowLayer()
        {
            float worldBaseY = CompGiantRig.ComputeGiantEntityBaseWorldY(
                isPortrait: false, isDead: false, isDowned: false,
                rootColY: 0f, worldZ: 0f, thingIdNumber: 1001,
                isFlyingVisual: false, flightAltitudeBoost: 0f, isWorldMap: true);

            MethodInfo boneMatMethod = typeof(CompGiantRig).GetMethod("TryComputeBoneRenderMatrix", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo glowOffField = typeof(BoneNodeDef).GetField(nameof(BoneNodeDef.glowLayerOffset));
            bool loadsGlowOffset = boneMatMethod != null && PatchProcessor.GetOriginalInstructions(boneMatMethod).Any(ci => ci.LoadsField(glowOffField));

            bool bug = worldBaseY > 1.0f || !loadsGlowOffset;
            return (bug, $"ComputeGiantEntityBaseWorldY(rootColY=0, isPortrait=false)={worldBaseY:F4} (lifts world-map giant ~8.41 units off planet surface); TryComputeBoneRenderMatrix loads glowLayerOffset={loadsGlowOffset} (Z-fighting on bone glow overlays).");
        }

        private static (bool, string) ProbeFlightWorldSpeedAndFixedWingLoiter(GiantRigDef carrierRig)
        {
            var talDef = new GiantFlightDef
            {
                flightMode = GiantFlightMode.TakeoffAndLanding,
                flightSpeedMultiplier = 2.0f
            };
            var flightRt = new GiantFlightRuntime(carrierRig, talDef);
            flightRt.ResetForSpawn(12345);
            float groundedMult = flightRt.EffectiveFlightSpeedMultiplier;

            float worldMult = flightRt.WorldFlightSpeedMultiplier;

            var fwDef = new FlightVehicleLocomotionDef
            {
                airframeType = FlightVehicleAirframeType.FixedWing,
                canHoverInPlace = false,
                loiterOrbitRadiusCells = 6.5f,
                maxTurnRateDegPerTick = 3.0f
            };
            var fwRt = new FlightVehicleLocomotionRuntime(carrierRig, fwDef);
            // Facing South (180 deg) near West map border (x=4): clockwise loiter orbits into negative X (< 0)
            fwRt.Reset(180f, new Vector2(4f, 50f));
            float minX = 4f;
            float maxDriftFromOrigin = 0f;
            for (int t = 0; t < 240; t++)
            {
                var inp = new FlightVehicleKinematicsStepInput
                {
                    IsAirborne = true,
                    HasMoveOrder = false,
                    TotalRemainingPathCells = 0f,
                    LookaheadBearingDeg = 180f,
                    MaxCruiseSpeedCellsPerSec = 10f,
                    DeltaTicks = 1f
                };
                fwRt.StepKinematicsCore(in inp);
                if (fwRt.ExactWorldPosXZ.x < minX) minX = fwRt.ExactWorldPosXZ.x;
                float d = Vector2.Distance(fwRt.ExactWorldPosXZ, new Vector2(4f, 50f));
                if (d > maxDriftFromOrigin) maxDriftFromOrigin = d;
            }

            bool bug = Mathf.Approximately(worldMult, 1f) || minX < 0f;
            return (bug, $"TakeoffAndLanding grounded WorldFlightSpeedMultiplier={worldMult:F2} (EffectiveFlightSpeedMultiplier={groundedMult:F2}); FixedWing loitering from (4,50, hdg=180) stayed within bounds minX={minX:F2} (maxDrift={maxDriftFromOrigin:F2} cells).");
        }

        private static (bool, string) ProbeAimTrackerCrossSaveTickUnderflow()
        {
            var lastSweepField = typeof(GiantTargetAimTracker).GetField("lastSweepTick", BindingFlags.Static | BindingFlags.NonPublic);
            var sweepMethod = typeof(GiantTargetAimTracker).GetMethod("SweepExpiredIfNeeded", BindingFlags.Static | BindingFlags.NonPublic);
            if (lastSweepField == null || sweepMethod == null) return (false, "Field/method not found.");
            int prevSweep = (int)lastSweepField.GetValue(null);
            try
            {
                lastSweepField.SetValue(null, 500000);
                int nowTick = 1000;
                sweepMethod.Invoke(null, new object[] { nowTick });
                int afterSweep = (int)lastSweepField.GetValue(null);
                GiantTargetAimTracker.ClearAll();
                int afterClear = (int)lastSweepField.GetValue(null);
                bool bug = afterSweep > nowTick || afterClear >= 0;
                return (bug, $"After Save A (500000) -> Save B (1000) SweepExpiredIfNeeded: lastSweepTick={afterSweep}; after ClearAll(): lastSweepTick={afterClear}.");
            }
            finally
            {
                lastSweepField.SetValue(null, prevSweep);
            }
        }

        private static (bool, string) ProbeFootprintMapCompDisposableContract()
        {
            Type t = typeof(GiantFootprintMapComponent);
            bool implementsDisposable = typeof(IDisposable).IsAssignableFrom(t);
            var nativeFields = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType.Name.Contains("NativeArray") || f.FieldType.Name.Contains("Customizer"))
                .Select(f => $"{f.FieldType.Name} {f.Name}")
                .ToList();
            bool bug = !implementsDisposable && nativeFields.Count > 0;
            return (bug, $"GiantFootprintMapComponent implements IDisposable={implementsDisposable}; owns {nativeFields.Count} native/customizer fields ({string.Join(", ", nativeFields.Take(5))}) -> leaks Allocator.Persistent memory on save load.");
        }

        private static (bool, string) ProbeGpuMeshCleanupAndCockpitContainerType()
        {
            Type[] nodeTypes =
            {
                typeof(PawnRenderNode_GiantSkinnedMesh),
                typeof(PawnRenderNode_GiantSoftWhip),
                typeof(PawnRenderNode_GiantRollingTrack)
            };
            var missingCleanup = new List<string>();
            foreach (Type nt in nodeTypes)
            {
                bool hasCleanup = typeof(IDisposable).IsAssignableFrom(nt) ||
                    nt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                      .Any(m => m.Name.Contains("Dispose") || m.Name.Contains("Destroy") || m.Name.Contains("Release"));
                if (!hasCleanup) missingCleanup.Add(nt.Name);
            }

            FieldInfo containerField = typeof(CompGiantCockpit).GetField("innerContainer", BindingFlags.Instance | BindingFlags.NonPublic);
            Type cType = containerField?.FieldType;
            bool isPawnOnlyOwner = cType == typeof(ThingOwner<Pawn>);

            bool bug = missingCleanup.Count > 0 || isPawnOnlyOwner;
            return (bug, $"Nodes lacking GPU Mesh cleanup: [{string.Join(", ", missingCleanup)}]; CompGiantCockpit.innerContainer={cType?.Name}<{cType?.GetGenericArguments().FirstOrDefault()?.Name}>.");
        }

        private static (bool, string) ProbeVaultStaticDictCrossSaveCollision()
        {
            MethodInfo tryGetVault = typeof(MechColossusProgressionUtility).GetMethod("TryGetVaultComp", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo parentField = typeof(ThingComp).GetField(nameof(ThingComp.parent));
            FieldInfo dictField = typeof(MechColossusProgressionUtility).GetField("VaultCompsByPawnId", BindingFlags.Static | BindingFlags.NonPublic);
            bool checksParentEq = tryGetVault != null && parentField != null && PatchProcessor.GetOriginalInstructions(tryGetVault).Any(ci => ci.LoadsField(parentField));
            bool isStaticDict = dictField != null;

            Type lifecycleType = typeof(GiantCreatureFrameworkMod).Assembly.GetType("GiantCreatureFramework.GameComponent_GiantCacheLifecycle");
            MethodInfo finInit = lifecycleType?.GetMethod("FinalizeInit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo crossSaveEventField = lifecycleType?.GetField("CrossSaveCacheCleared", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            bool clearsVaultDict = finInit != null && crossSaveEventField != null && crossSaveEventField.GetValue(null) != null;

            bool bug = isStaticDict && !checksParentEq && !clearsVaultDict;
            return (bug, $"VaultCompsByPawnId static={isStaticDict}, TryGetVaultComp checks comp.parent==thing: {checksParentEq}, cleared via CrossSaveCacheCleared: {clearsVaultDict}");
        }

        private static (bool, string) ProbeWildGiantBreachProtectionBug()
        {
            MethodInfo m = typeof(GiantDestructionUtility).GetMethod("IsProtectedAlliedThing", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return (false, "Method IsProtectedAlliedThing not found.");

            // Direct execution with uninitialized Pawn (Faction == null) and Thing with non-null Faction!
            var wildPawn = (Pawn)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
            var wallThing = (Thing)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Thing));
            var playerFaction = (Faction)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Faction));
            typeof(Thing).GetField("factionInt", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(wallThing, playerFaction);
            bool protectedFromWild = (bool)m.Invoke(null, new object[] { wildPawn, wallThing });
            return (protectedFromWild, $"Direct call IsProtectedAlliedThing(wildGiant[Faction=null], playerWall[Faction!=null]) returned {protectedFromWild} (expected False -> wild/manhunter giants cannot breach colony walls!).");
        }

        // =========================================================================
        // PERFORMANCE & ALLOCATION MICROBENCHMARKS
        // =========================================================================

        private static (long, string) BenchRollingTrackPerFrameAlloc(GiantRigDef panzerRig)
        {
            var verts = new List<Vector3>(128);
            var uvs = new List<Vector2>(128);
            var tris = new List<int>(256);
            // 10-point realistic closed tank track loop matching PanzerIV lateral hull
            var loopPx = new List<Vector2>
            {
                new Vector2(96, 360), new Vector2(160, 390), new Vector2(256, 396), new Vector2(352, 390), new Vector2(416, 360),
                new Vector2(408, 315), new Vector2(330, 305), new Vector2(256, 302), new Vector2(180, 305), new Vector2(104, 315)
            };
            Vector2 drawSize = new Vector2(6.4f, 6.4f);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 600; f++)
            {
                GiantRollingTrackMeshBuilder.BuildClosedLoopStrip(
                    loopPx, 0.30f, 2.60f, 512, drawSize, false, f * 0.08f, verts, uvs, tris);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            return (alloc, $"verts={verts.Count}, tris={tris.Count / 3}; allocates new Vector2[] + float[] on every single scroll frame.");
        }

        private static (long, string) BenchFootprintRebuildAllocAndTime(
            GiantRigDef panzerRig, TextureAlphaMask[] panzerMasks,
            GiantRigDef rathRig, TextureAlphaMask[] rathMasks,
            GiantRigDef colRig, TextureAlphaMask[] colMasks)
        {
            var fpP = new GiantFootprintData();
            var fpR = new GiantFootprintData();
            var fpC = new GiantFootprintData();
            var pP = new BoneLocalPose[panzerRig.SortedNodes.Length];
            var pR = new BoneLocalPose[rathRig.SortedNodes.Length];
            var pC = new BoneLocalPose[colRig.SortedNodes.Length];
            var wP = new BoneWorldPose[panzerRig.SortedNodes.Length];
            var wR = new BoneWorldPose[rathRig.SortedNodes.Length];
            var wC = new BoneWorldPose[colRig.SortedNodes.Length];

            SkeletalKinematics.SolveWorldPoses(panzerRig, new Vector2(50.5f, 50.5f), 90f, pP, ReadOnlySpan<bool>.Empty, wP, 1f);
            SkeletalKinematics.SolveWorldPoses(rathRig, new Vector2(50.5f, 50.5f), 45f, pR, ReadOnlySpan<bool>.Empty, wR, 1f);
            SkeletalKinematics.SolveWorldPoses(colRig, new Vector2(50.5f, 50.5f), 135f, pC, ReadOnlySpan<bool>.Empty, wC, 1f);
            fpP.Rebuild(panzerRig, wP, panzerMasks, new IntVec3(50, 0, 50));
            fpR.Rebuild(rathRig, wR, rathMasks, new IntVec3(50, 0, 50));
            fpC.Rebuild(colRig, wC, colMasks, new IntVec3(50, 0, 50));

            long bP = GC.GetAllocatedBytesForCurrentThread();
            var swP = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                SkeletalKinematics.SolveWorldPoses(panzerRig, new Vector2(50.5f, 50.5f), (i * 7) % 360, pP, ReadOnlySpan<bool>.Empty, wP, 1f);
                fpP.Rebuild(panzerRig, wP, panzerMasks, new IntVec3(50, 0, 50));
            }
            swP.Stop();
            long aP = GC.GetAllocatedBytesForCurrentThread() - bP;

            long bR = GC.GetAllocatedBytesForCurrentThread();
            var swR = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                SkeletalKinematics.SolveWorldPoses(rathRig, new Vector2(50.5f, 50.5f), (i * 7) % 360, pR, ReadOnlySpan<bool>.Empty, wR, 1f);
                fpR.Rebuild(rathRig, wR, rathMasks, new IntVec3(50, 0, 50));
            }
            swR.Stop();
            long aR = GC.GetAllocatedBytesForCurrentThread() - bR;

            long bC = GC.GetAllocatedBytesForCurrentThread();
            var swC = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                SkeletalKinematics.SolveWorldPoses(colRig, new Vector2(50.5f, 50.5f), (i * 7) % 360, pC, ReadOnlySpan<bool>.Empty, wC, 1f);
                fpC.Rebuild(colRig, wC, colMasks, new IntVec3(50, 0, 50));
            }
            swC.Stop();
            long aC = GC.GetAllocatedBytesForCurrentThread() - bC;

            return (aP + aR + aC,
                $"PanzerIV={swP.Elapsed.TotalMilliseconds / 200.0:F3}ms ({aP / 200} B/call), Rathalos44b={swR.Elapsed.TotalMilliseconds / 200.0:F3}ms ({aR / 200} B/call), Colossus64b={swC.Elapsed.TotalMilliseconds / 200.0:F3}ms ({aC / 200} B/call)");
        }

        private static (long, string) BenchAnimCacheThrash(
            GiantRigDef r1, GiantRigDef r2, Dictionary<string, GiantAnimDef> a1)
        {
            var clip = a1.Values.First();
            var p1 = new BoneLocalPose[r1.SortedNodes.Length];
            var p2 = new BoneLocalPose[r2.SortedNodes.Length];

            clip.EvaluateInto(r1, 10f, p1);
            long bSame = GC.GetAllocatedBytesForCurrentThread();
            var swSame = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) clip.EvaluateInto(r1, i % 60, p1);
            swSame.Stop();
            long aSame = GC.GetAllocatedBytesForCurrentThread() - bSame;

            long bAlt = GC.GetAllocatedBytesForCurrentThread();
            var swAlt = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                if ((i & 1) == 0) clip.EvaluateInto(r1, i % 60, p1);
                else clip.EvaluateInto(r2, i % 60, p2);
            }
            swAlt.Stop();
            long aAlt = GC.GetAllocatedBytesForCurrentThread() - bAlt;

            return (aAlt, $"SameRig: {swSame.Elapsed.TotalMilliseconds:F2}ms ({aSame} B) vs Alternating2Rigs: {swAlt.Elapsed.TotalMilliseconds:F2}ms ({aAlt:N0} B -> {aAlt / 2000.0:F1} B/eval cache thrash!)");
        }

        private static (long, string) BenchSteeringSixGiants(
            GiantRigDef r1, GiantRigDef r2, GiantRigDef r3, GiantRigDef r4, GiantRigDef r5, GiantRigDef r6)
        {
            GiantRigDef[] rigs = { r1, r2, r3, r4, r5, r6 };
            var runtimes = rigs.Select(r => { var s = new GiantSteeringRuntime(r); s.Reset(0f); return s; }).ToArray();
            var poses = rigs.Select(r => new BoneLocalPose[r.SortedNodes.Length]).ToArray();
            var masks = rigs.Select(r => new bool[r.SortedNodes.Length]).ToArray();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 500; t++)
            {
                float hdg = (t * 2.5f) % 360f;
                var ctx = new GiantSteeringContext
                {
                    DesiredBodyHeadingDeg = (hdg + 35f) % 360f,
                    DesiredLookHeadingDeg = (hdg + 60f) % 360f,
                    IsMoving = true,
                    HasExplicitLookTarget = true,
                    IsCombatTarget = true,
                    SteeringEnabled = true,
                    IntensityScale = 1f
                };
                for (int g = 0; g < rigs.Length; g++)
                {
                    runtimes[g].ApplyBoneSteering(rigs[g], hdg, poses[g], masks[g], 1f, in ctx);
                }
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            return (alloc, "6 giants steered per tick x 500 ticks");
        }

        private static (long, string) BenchKinematicsAndSkinnedMesh(
            GiantRigDef rathRig, TextureAlphaMask[] rathMasks, Dictionary<string, GiantAnimDef> rathAnims)
        {
            var walk = rathAnims.Values.FirstOrDefault(a => a.defName.Contains("Walk") || a.defName.Contains("Fly")) ?? rathAnims.Values.First();
            var local = new BoneLocalPose[rathRig.SortedNodes.Length];
            var world = new BoneWorldPose[rathRig.SortedNodes.Length];
            var smRts = rathRig.skinnedMeshes.Select(m => { var rt = new SkinnedMeshRuntime(rathRig, m); rt.EnsureBindTopology(null); return rt; }).ToArray();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 600; f++)
            {
                walk.EvaluateInto(rathRig, f % Math.Max(1f, walk.durationTicks), local);
                SkeletalKinematics.SolveWorldPoses(rathRig, new Vector2(50.5f, 50.5f), (f * 1.5f) % 360f, local, ReadOnlySpan<bool>.Empty, world, 1f);
                for (int s = 0; s < smRts.Length; s++)
                {
                    smRts[s].EvaluateDeformedVertices(world, new Vector2(50.5f, 50.5f), false, out _);
                }
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            return (alloc, $"Rathalos 44b + {smRts.Length} skinned meshes ({smRts.Sum(s => s.VertexCount)} verts total) x 600 frames");
        }

        private static void WriteJsonReport(string modRoot, int bugs, int oks, int errs)
        {
            string outDir = Path.Combine(Path.GetTempPath(), "GCF_QaAudit");
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, "offline_core_qa_report.json");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"probeCount\": {Probes.Count},");
            sb.AppendLine($"  \"bugsReproduced\": {bugs},");
            sb.AppendLine($"  \"okCount\": {oks},");
            sb.AppendLine($"  \"errorCount\": {errs},");
            sb.AppendLine("  \"probes\": [");
            for (int i = 0; i < Probes.Count; i++)
            {
                var p = Probes[i];
                sb.Append("    {");
                sb.Append($"\"id\":\"{Esc(p.Id)}\",\"category\":\"{Esc(p.Category)}\",\"severity\":\"{Esc(p.Severity)}\",");
                sb.Append($"\"outcome\":\"{Esc(p.Outcome)}\",\"elapsedMs\":{p.ElapsedMs.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.Append($"\"title\":\"{Esc(p.Title)}\",\"evidence\":\"{Esc(p.Evidence)}\"");
                sb.AppendLine(i + 1 < Probes.Count ? "}," : "}");
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"benchmarks\": [");
            for (int i = 0; i < Benches.Count; i++)
            {
                var b = Benches[i];
                sb.Append("    {");
                sb.Append($"\"id\":\"{Esc(b.Id)}\",\"iterations\":{b.Iterations},");
                sb.Append($"\"totalMs\":{b.TotalMs.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.Append($"\"usPerIter\":{b.UsPerIter.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.Append($"\"allocBytesTotal\":{b.AllocBytesTotal},");
                sb.Append($"\"allocBytesPerIter\":{b.AllocBytesPerIter.ToString("F1", CultureInfo.InvariantCulture)},");
                sb.Append($"\"title\":\"{Esc(b.Title)}\",\"notes\":\"{Esc(b.Notes)}\"");
                sb.AppendLine(i + 1 < Benches.Count ? "}," : "}");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"JSON report saved to: {outPath}");
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
