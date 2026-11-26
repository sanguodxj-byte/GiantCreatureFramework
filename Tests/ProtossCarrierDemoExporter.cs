using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Tests
{
    internal static class ProtossCarrierDemoExporter
    {
        public static void Export(
            string outJsonPath,
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            Vector2[] tipUVs = ComputeBoneTipUVs(rig, masks);

            var rootObj = new Dictionary<string, object>
            {
                ["nodes"] = BuildNodeMeta(nodes),
                ["seq1_rig_actions"] = BuildRigAndActionsSequence(rig, masks, anims, tipUVs),
                ["seq2_interceptor_sortie"] = BuildInterceptorSortieAndRecallSequence(rig, masks, anims, tipUVs)
            };

            string dir = Path.GetDirectoryName(outJsonPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(outJsonPath, JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = false }));
            Console.WriteLine($"Exported ProtossCarrier C# engine frame data to: {outJsonPath}");
        }

        private static List<object> BuildNodeMeta(BoneNodeDef[] nodes)
        {
            var list = new List<object>(nodes.Length);
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef b = nodes[i];
                list.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["nodeId"] = b.nodeId,
                    ["label"] = b.label,
                    ["parentIndex"] = b.parentRuntimeIndex,
                    ["texPath"] = b.texPath,
                    ["layerOffset"] = b.layerOffset,
                    ["drawSize"] = new[] { b.drawSize.x, b.drawSize.y },
                    ["pivotUV"] = new[] { b.resolvedPivotUV.x, b.resolvedPivotUV.y },
                    ["parentSocketUV"] = new[] { b.resolvedParentSocketUV.x, b.resolvedParentSocketUV.y }
                });
            }
            return list;
        }

        private static Vector2[] ComputeBoneTipUVs(GiantRigDef rig, TextureAlphaMask[] masks)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            var tips = new Vector2[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                Vector2 piv = nodes[i].resolvedPivotUV;
                TextureAlphaMask mask = masks[i];
                float bestDistSq = -1f;
                Vector2 bestUV = piv;
                for (int py = 0; py < mask.height; py += 4)
                {
                    float v = 1f - (py + 0.5f) / mask.height;
                    for (int px = 0; px < mask.width; px += 4)
                    {
                        float u = (px + 0.5f) / mask.width;
                        if (mask.IsOpaqueUV(u, v))
                        {
                            float du = (u - piv.x) * nodes[i].drawSize.x;
                            float dv = (v - piv.y) * nodes[i].drawSize.y;
                            float d2 = du * du + dv * dv;
                            if (d2 > bestDistSq)
                            {
                                bestDistSq = d2;
                                bestUV = new Vector2(u, v);
                            }
                        }
                    }
                }
                tips[i] = bestUV;
            }
            return tips;
        }

        private static float Round3(float v) => MathF.Round(v, 3);

        private static Dictionary<string, object> BuildCarrierSnapshot(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Vector2[] tipUVs,
            Vector2 rootXZ,
            float headingDeg,
            BoneLocalPose[] localPoses,
            bool[] missing,
            string title,
            string subtitle,
            bool showFootprint,
            bool showSkeleton,
            Dictionary<string, Vector2> explodeOffsets = null,
            List<object> fighters = null,
            List<object> beams = null,
            List<object> targets = null,
            Dictionary<string, object> vfx = null)
        {
            int n = rig.SortedNodes.Length;
            var worldPoses = new BoneWorldPose[n];
            var fp = new GiantFootprintData();
            IntVec3 center = new IntVec3(Mathf.FloorToInt(rootXZ.x), 0, Mathf.FloorToInt(rootXZ.y));
            IntVec3 mapSize = new IntVec3(120, 1, 120);

            SkeletalKinematics.SolveWorldPoses(rig, rootXZ, headingDeg, localPoses, missing, worldPoses);
            fp.Rebuild(rig, worldPoses, masks, center, mapSize);

            var bonesOut = new List<object>(n);
            for (int i = 0; i < n; i++)
            {
                BoneWorldPose wp = worldPoses[i];
                Vector2 sockXZ = Vector2.zero;
                if (rig.SortedNodes[i].parentRuntimeIndex >= 0)
                {
                    int pIdx = rig.SortedNodes[i].parentRuntimeIndex;
                    Vector2 suv = rig.SortedNodes[i].resolvedParentSocketUV;
                    sockXZ = worldPoses[pIdx].UVToWorldXZ(suv.x, suv.y);
                }

                if (explodeOffsets != null && explodeOffsets.TryGetValue(rig.SortedNodes[i].nodeId, out Vector2 disp))
                {
                    wp.jointWorldXZ += disp;
                    wp.quadCenterWorldXZ += disp;
                    sockXZ += disp * 0.2f;
                }

                Vector2 tipXZ = wp.UVToWorldXZ(tipUVs[i].x, tipUVs[i].y);
                bonesOut.Add(new Dictionary<string, object>
                {
                    ["visible"] = wp.visible,
                    ["jointXZ"] = new[] { Round3(wp.jointWorldXZ.x), Round3(wp.jointWorldXZ.y) },
                    ["quadCenterXZ"] = new[] { Round3(wp.quadCenterWorldXZ.x), Round3(wp.quadCenterWorldXZ.y) },
                    ["angleDeg"] = Round3(wp.worldAngleDeg),
                    ["worldSize"] = new[] { Round3(wp.worldSize.x), Round3(wp.worldSize.y) },
                    ["tipXZ"] = new[] { Round3(tipXZ.x), Round3(tipXZ.y) },
                    ["socketXZ"] = new[] { Round3(sockXZ.x), Round3(sockXZ.y) }
                });
            }

            var cellsOut = new List<object>(showFootprint ? fp.hitCells.Count : 0);
            if (showFootprint)
            {
                for (int i = 0; i < fp.hitCells.Count; i++)
                {
                    HitCellEntry ce = fp.hitCells[i];
                    cellsOut.Add(new object[] { ce.cell.x, ce.cell.z, ce.isCollisionCell ? 1 : 0 });
                }
            }

            var res = new Dictionary<string, object>
            {
                ["rootXZ"] = new[] { Round3(rootXZ.x), Round3(rootXZ.y) },
                ["headingDeg"] = Round3(headingDeg),
                ["title"] = title,
                ["subtitle"] = subtitle,
                ["showFootprint"] = showFootprint,
                ["showSkeleton"] = showSkeleton,
                ["hitCount"] = fp.hitCells.Count,
                ["colCount"] = fp.collisionCells.Count,
                ["bones"] = bonesOut,
                ["cells"] = cellsOut
            };
            if (fighters != null) res["fighters"] = fighters;
            if (beams != null) res["beams"] = beams;
            if (targets != null) res["targets"] = targets;
            if (vfx != null) res["vfx"] = vfx;
            return res;
        }

        private static void ApplyContinuousSpin(GiantRigDef rig, BoneLocalPose[] poses, float globalTick)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (Mathf.Abs(nodes[i].continuousSpinDegPerTick) > 0.001f)
                {
                    BoneLocalPose p = poses[i];
                    p.angle += nodes[i].continuousSpinDegPerTick * globalTick;
                    poses[i] = p;
                }
            }
        }

        private static List<object> BuildRigAndActionsSequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            Vector2[] tipUVs)
        {
            int n = rig.SortedNodes.Length;
            var frames = new List<object>(128);
            Vector2 centerXZ = new Vector2(50.5f, 50.5f);
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];

            var explodeMax = new Dictionary<string, Vector2>
            {
                ["command_crest"] = new Vector2(0f, 1.9f),
                ["khaydarin_core"] = new Vector2(0f, 0.9f),
                ["prow_left"] = new Vector2(-2.2f, 2.1f),
                ["prow_right"] = new Vector2(2.2f, 2.1f),
                ["hangar_bay_left"] = new Vector2(-2.6f, 0f),
                ["hangar_bay_right"] = new Vector2(2.6f, 0f),
                ["hangar_door_left"] = new Vector2(-4.0f, 0.4f),
                ["hangar_door_right"] = new Vector2(4.0f, 0.4f),
                ["fin_rear_left"] = new Vector2(-2.2f, -2.0f),
                ["fin_rear_right"] = new Vector2(2.2f, -2.0f),
                ["aft_thruster"] = new Vector2(0f, -2.5f)
            };

            int globalTick = 0;

            // 1. Exploded 12-module assembly -> pin-joint lock (24 frames)
            for (int f = 0; f < 24; f++, globalTick += 2)
            {
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                ApplyContinuousSpin(rig, localPoses, globalTick);

                float t = f < 4 ? 1f : (f >= 18 ? 0f : 1f - Smooth01((f - 4) / 14f));
                Dictionary<string, Vector2> exp = null;
                if (t > 0.001f)
                {
                    exp = new Dictionary<string, Vector2>();
                    foreach (var kv in explodeMax) exp[kv.Key] = kv.Value * t;
                }

                frames.Add(BuildCarrierSnapshot(
                    rig, masks, tipUVs, centerXZ, 0f, localPoses, missing,
                    "1/4  12-Module Skeletal Assembly & Pin-Joint Lock",
                    t > 0.02f ? "Exploded 512x512 Khalai modules converging to exact parent sockets" : "All 12 joints locked (0.00 cell socket drift) + Khaydarin rotor spin",
                    showFootprint: f >= 18,
                    showSkeleton: true,
                    explodeOffsets: exp));
            }

            // 2. Rigid 360-degree capital-hull rotation + dynamic HitCells/CollisionCells (32 frames)
            // All welded hull modules (command_crest, prow_left/right, fin_rear_left/right, aft_thruster) stay locked at 0 deg!
            for (int f = 0; f < 32; f++, globalTick += 2)
            {
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                anims["GCF_Anim_Carrier_Walk"].EvaluateInto(rig, globalTick, localPoses);
                float u = f / 32f;
                float heading = u * 360f;
                ApplyContinuousSpin(rig, localPoses, globalTick);

                frames.Add(BuildCarrierSnapshot(
                    rig, masks, tipUVs, centerXZ, heading, localPoses, missing,
                    "2/4  Rigid Capital Hull 360-Deg Turn & Dynamic Footprint",
                    $"Heading {heading:F0} deg | Spire, prongs, fins & thruster 100% locked to rigid hull",
                    showFootprint: true,
                    showSkeleton: false));
            }

            // 3. Decoupled Per-Bay Hangar Shutters (Zero Whole-Ship Motion): Port Shutter Alone (16 frames) -> Starboard Shutter Alone (16 frames)
            rig.TryGetNodeIndex("hangar_door_left", out int doorLIdx);
            rig.TryGetNodeIndex("hangar_door_right", out int doorRIdx);
            for (int f = 0; f < 16; f++, globalTick += 2)
            {
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                float bell = Mathf.Sin((f / 15f) * Mathf.PI);
                if (doorLIdx >= 0) localPoses[doorLIdx].angle = -36f * Smooth01(bell);
                ApplyContinuousSpin(rig, localPoses, globalTick);
                var vfx = f == 7 || f == 8
                    ? new Dictionary<string, object> { ["type"] = "catapult", ["xz"] = new[] { 48.2f, 50.6f }, ["r"] = 1.6f }
                    : null;
                frames.Add(BuildCarrierSnapshot(
                    rig, masks, tipUVs, centerXZ, 0f, localPoses, missing,
                    "3/4  Decoupled Per-Bay Shutters (Zero Whole-Ship Motion)",
                    "Port Bay Active: ONLY left shutter opens (-36 deg); right shutter & hull stay motionless",
                    showFootprint: true,
                    showSkeleton: false,
                    vfx: vfx));
            }

            for (int f = 0; f < 16; f++, globalTick += 2)
            {
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                float bell = Mathf.Sin((f / 15f) * Mathf.PI);
                if (doorRIdx >= 0) localPoses[doorRIdx].angle = 36f * Smooth01(bell);
                ApplyContinuousSpin(rig, localPoses, globalTick);
                var vfx = f == 7 || f == 8
                    ? new Dictionary<string, object> { ["type"] = "catapult", ["xz"] = new[] { 52.8f, 50.6f }, ["r"] = 1.6f }
                    : null;
                frames.Add(BuildCarrierSnapshot(
                    rig, masks, tipUVs, centerXZ, 0f, localPoses, missing,
                    "3/4  Decoupled Per-Bay Shutters (Zero Whole-Ship Motion)",
                    "Starboard Bay Active: ONLY right shutter opens (+36 deg); left shutter & hull stay motionless",
                    showFootprint: true,
                    showSkeleton: false,
                    vfx: vfx));
            }

            // 4. Port Hangar Bay Severing & Live Silhouette Contraction (18 frames)
            rig.TryGetNodeIndex("hangar_bay_left", out int leftBayIdx);
            for (int f = 0; f < 18; f++, globalTick += 2)
            {
                missing[leftBayIdx] = f < 13;
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                ApplyContinuousSpin(rig, localPoses, globalTick);
                var vfx = f == 0 || f == 1
                    ? new Dictionary<string, object> { ["type"] = "sever", ["xz"] = new[] { 48.2f, 50.5f }, ["r"] = 2.4f }
                    : null;
                frames.Add(BuildCarrierSnapshot(
                    rig, masks, tipUVs, centerXZ, 0f, localPoses, missing,
                    "4/4  Port Hangar Severing & Live Footprint Contraction",
                    missing[leftBayIdx]
                        ? "Port Hangar Destroyed -> Bay + Shutter hidden, HitCells shrink, Cap 8 -> 4!"
                        : "Hangar Restored -> Full 8-Interceptor Capacity Ready",
                    showFootprint: true,
                    showSkeleton: true,
                    vfx: vfx));
            }

            return frames;
        }

        private sealed class SimBolt
        {
            public Vector2 StartXZ;
            public Vector2 EndXZ;
            public int Age;
            public int MaxAge = 4;
        }

        private sealed class SimEnemy
        {
            public string Label;
            public Vector2 XZ;
            public float MaxHp;
            public float Hp;
            public bool Alive => Hp > 0.1f;
        }

        private static List<object> BuildInterceptorSortieAndRecallSequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            Vector2[] tipUVs)
        {
            int n = rig.SortedNodes.Length;
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];
            var worldPoses = new BoneWorldPose[n];

            rig.TryGetNodeIndex("hangar_bay_left", out int leftBayIdx);
            rig.TryGetNodeIndex("hangar_bay_right", out int rightBayIdx);
            rig.TryGetNodeIndex("hangar_door_left", out int leftDoorIdx);
            rig.TryGetNodeIndex("hangar_door_right", out int rightDoorIdx);
            int leftHoldTicks = 0;
            int rightHoldTicks = 0;
            float leftOpen01 = 0f;
            float rightOpen01 = 0f;

            var enemies = new List<SimEnemy>
            {
                new SimEnemy { Label = "Hostile Turret W", XZ = new Vector2(44.5f, 65.5f), MaxHp = 180f, Hp = 180f },
                new SimEnemy { Label = "Command Bunker", XZ = new Vector2(50.5f, 68.0f), MaxHp = 320f, Hp = 320f },
                new SimEnemy { Label = "Hostile Turret E", XZ = new Vector2(56.5f, 65.0f), MaxHp = 180f, Hp = 180f }
            };

            var fighters = new InterceptorKinematicState[8];
            var launched = new bool[8];
            var assignedEnemyIdx = new int[8];
            for (int i = 0; i < 8; i++)
            {
                assignedEnemyIdx[i] = i % enemies.Count;
            }

            var activeBolts = new List<SimBolt>();
            var frames = new List<object>(140);

            int totalTicks = 420; // Sampled every 3 ticks -> 140 smooth frames
            for (int tick = 0; tick < totalTicks; tick++)
            {
                // Carrier slowly glides North from z=43.0 to z=47.2 behind stone wall at z=54
                float moveFrac = Mathf.Clamp01(tick / (float)totalTicks);
                Vector2 carrierXZ = new Vector2(50.5f, Mathf.Lerp(43.0f, 47.2f, Smooth01(moveFrac)));
                float carrierHeading = 0f;

                bool recallPhase = tick >= 285;

                // Rigid Carrier hull glide (zero whole-ship action animation; per-bay shutters driven independently below)
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                anims["GCF_Anim_Carrier_Walk"].EvaluateInto(rig, tick, localPoses);
                ApplyContinuousSpin(rig, localPoses, tick);

                if (leftHoldTicks > 0) leftHoldTicks--;
                if (rightHoldTicks > 0) rightHoldTicks--;

                SkeletalKinematics.SolveWorldPoses(rig, carrierXZ, carrierHeading, localPoses, missing, worldPoses);
                Vector2 leftBayXZ = worldPoses[leftBayIdx].UVToWorldXZ(216f / 512f, 1f - 224f / 512f);
                Vector2 rightBayXZ = worldPoses[rightBayIdx].UVToWorldXZ(296f / 512f, 1f - 224f / 512f);

                // Staggered Graviton Catapult launch of all 8 Interceptors during ticks 6..48 (each bay opens its own shutter only!)
                if (!recallPhase && tick >= 6)
                {
                    int slotToLaunch = (tick - 6) / 6;
                    if ((tick - 6) % 6 == 0 && slotToLaunch >= 0 && slotToLaunch < 8 && !launched[slotToLaunch])
                    {
                        bool port = (slotToLaunch & 1) == 0;
                        if (port) leftHoldTicks = Mathf.Max(leftHoldTicks, 14);
                        else rightHoldTicks = Mathf.Max(rightHoldTicks, 14);
                        launched[slotToLaunch] = true;
                        fighters[slotToLaunch] = new InterceptorKinematicState
                        {
                            State = InterceptorFlightState.Launching,
                            WorldXZ = port ? leftBayXZ : rightBayXZ,
                            HeadingDeg = port ? -22f : 22f,
                            SpeedCellsPerSec = 18.0f,
                            CooldownTicksLeft = 4,
                            OrbitDirectionSign = port ? -1 : 1
                        };
                    }
                }

                // Step active bolts
                for (int b = activeBolts.Count - 1; b >= 0; b--)
                {
                    activeBolts[b].Age++;
                    if (activeBolts[b].Age > activeBolts[b].MaxAge)
                    {
                        activeBolts.RemoveAt(b);
                    }
                }

                // Step all launched Interceptors
                int airborneCount = 0;
                int dockedCount = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (!launched[i])
                    {
                        dockedCount++;
                        continue;
                    }
                    if (fighters[i].State == InterceptorFlightState.Docked)
                    {
                        dockedCount++;
                        continue;
                    }

                    airborneCount++;
                    bool port = (i & 1) == 0;
                    Vector2 homeXZ = port ? leftBayXZ : rightBayXZ;

                    // Pick live enemy target (retarget automatically when one dies!)
                    SimEnemy curEnemy = enemies[assignedEnemyIdx[i]];
                    if (!curEnemy.Alive)
                    {
                        curEnemy = null;
                        for (int e = 0; e < enemies.Count; e++)
                        {
                            if (enemies[e].Alive)
                            {
                                assignedEnemyIdx[i] = e;
                                curEnemy = enemies[e];
                                break;
                            }
                        }
                    }

                    bool hasTarget = curEnemy != null && curEnemy.Alive;
                    Vector2 targXZ = hasTarget ? curEnemy.XZ : new Vector2(50.5f, 64.0f);
                    float speedMult = tick < 120 ? 1.25f : (recallPhase ? 1.25f : 1.05f);

                    fighters[i].Step(
                        hangarSocketXZ: homeXZ,
                        carrierHeadingDeg: carrierHeading,
                        hasValidTarget: hasTarget,
                        targetXZ: targXZ,
                        recallOrdered: recallPhase,
                        maxLeashRadius: 42f,
                        baseCruiseSpeed: 16.8f,
                        speedMultiplier: speedMult,
                        out bool firedSalvo);

                    if (fighters[i].State == InterceptorFlightState.Returning && (fighters[i].WorldXZ - homeXZ).sqrMagnitude <= 16f)
                    {
                        if (port) leftHoldTicks = Mathf.Max(leftHoldTicks, 8);
                        else rightHoldTicks = Mathf.Max(rightHoldTicks, 8);
                    }

                    if (firedSalvo && hasTarget && !recallPhase)
                    {
                        float rad = fighters[i].HeadingDeg * Mathf.Deg2Rad;
                        Vector2 fwd = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
                        Vector2 right = new Vector2(fwd.y, -fwd.x);
                        for (int w = -1; w <= 1; w += 2)
                        {
                            activeBolts.Add(new SimBolt
                            {
                                StartXZ = fighters[i].WorldXZ + fwd * 0.45f + right * (0.24f * w),
                                EndXZ = targXZ + right * (0.18f * w),
                                Age = 0,
                                MaxAge = 4
                            });
                        }
                        curEnemy.Hp = Mathf.Max(0f, curEnemy.Hp - 18f);
                    }
                }

                leftOpen01 = Mathf.MoveTowards(leftOpen01, leftHoldTicks > 0 ? 1f : 0f, leftHoldTicks > 0 ? 0.24f : 0.10f);
                rightOpen01 = Mathf.MoveTowards(rightOpen01, rightHoldTicks > 0 ? 1f : 0f, rightHoldTicks > 0 ? 0.24f : 0.10f);
                if (leftDoorIdx >= 0) localPoses[leftDoorIdx].angle = -36f * Smooth01(leftOpen01);
                if (rightDoorIdx >= 0) localPoses[rightDoorIdx].angle = 36f * Smooth01(rightOpen01);

                // Export every 3rd tick -> 140 crisp 20fps frames
                if (tick % 3 == 0)
                {
                    var fList = new List<object>();
                    for (int i = 0; i < 8; i++)
                    {
                        if (launched[i] && fighters[i].State != InterceptorFlightState.Docked)
                        {
                            fList.Add(new Dictionary<string, object>
                            {
                                ["slot"] = i,
                                ["xz"] = new[] { Round3(fighters[i].WorldXZ.x), Round3(fighters[i].WorldXZ.y) },
                                ["ang"] = Round3(fighters[i].HeadingDeg),
                                ["bank"] = Round3(fighters[i].BankDeg),
                                ["state"] = fighters[i].State.ToString()
                            });
                        }
                    }

                    var bList = new List<object>();
                    for (int b = 0; b < activeBolts.Count; b++)
                    {
                        bList.Add(new Dictionary<string, object>
                        {
                            ["s"] = new[] { Round3(activeBolts[b].StartXZ.x), Round3(activeBolts[b].StartXZ.y) },
                            ["e"] = new[] { Round3(activeBolts[b].EndXZ.x), Round3(activeBolts[b].EndXZ.y) },
                            ["a"] = activeBolts[b].Age
                        });
                    }

                    var tList = new List<object>();
                    for (int e = 0; e < enemies.Count; e++)
                    {
                        tList.Add(new Dictionary<string, object>
                        {
                            ["label"] = enemies[e].Label,
                            ["xz"] = new[] { Round3(enemies[e].XZ.x), Round3(enemies[e].XZ.y) },
                            ["hpFrac"] = Round3(enemies[e].Hp / enemies[e].MaxHp)
                        });
                    }

                    string phaseTitle;
                    string subTitle;
                    Dictionary<string, object> vfx = null;

                    if (tick < 60)
                    {
                        phaseTitle = "Act 1/3  Graviton Catapult Scramble (Over-Wall Launch)";
                        subTitle = $"Airborne: {airborneCount}/8 | Docked: {dockedCount}/8 | Catapulting squadron across stone wall";
                        if (tick >= 12 && tick <= 24)
                        {
                            vfx = new Dictionary<string, object>
                            {
                                ["type"] = "catapult",
                                ["xz"] = new[] { Round3(carrierXZ.x), Round3(carrierXZ.y + 1.5f) },
                                ["r"] = 3.0f
                            };
                        }
                    }
                    else if (!recallPhase)
                    {
                        int aliveTargets = 0;
                        for (int e = 0; e < enemies.Count; e++) if (enemies[e].Alive) aliveTargets++;
                        phaseTitle = "Act 2/3  360-Deg Dogfight Strafing & Twin-Beam Salvos";
                        subTitle = $"Airborne: {airborneCount}/8 | Active Hostiles: {aliveTargets}/3 | Autonomous figure-8 banking passes";
                    }
                    else
                    {
                        phaseTitle = "Act 3/3  Khaydarin Fleet Recall & Moving Hangar Docking";
                        subTitle = $"Airborne: {airborneCount}/8 | Docked: {dockedCount}/8 | Tracking live port/starboard hangar sockets";
                        if (tick >= 285 && tick <= 312)
                        {
                            float prog = (tick - 285) / 27f;
                            vfx = new Dictionary<string, object>
                            {
                                ["type"] = "recall_ring",
                                ["xz"] = new[] { Round3(carrierXZ.x), Round3(carrierXZ.y) },
                                ["r"] = Round3(2.2f + prog * 6.5f)
                            };
                        }
                    }

                    frames.Add(BuildCarrierSnapshot(
                        rig, masks, tipUVs, carrierXZ, carrierHeading, localPoses, missing,
                        phaseTitle, subTitle,
                        showFootprint: false,
                        showSkeleton: false,
                        fighters: fList,
                        beams: bList,
                        targets: tList,
                        vfx: vfx));
                }
            }

            return frames;
        }

        private static float Smooth01(float x)
        {
            float t = Mathf.Clamp01(x);
            return t * t * (3f - 2f * t);
        }
    }
}
