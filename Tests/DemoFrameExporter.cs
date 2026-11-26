using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Tests
{
    internal static class DemoFrameExporter
    {
        private static Vector2[] cachedTipUVs;

        public static void Export(
            string outJsonPath,
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            int n = nodes.Length;
            cachedTipUVs = ComputeBoneTipUVs(rig, masks);

            var (patrolFrames, patrolPath) = BuildCrawlPatrolSequence(rig, masks, anims, n);

            var rootObj = new Dictionary<string, object>
            {
                ["centerCell"] = new[] { 50, 50 },
                ["rootWorldXZ"] = new[] { 50.5f, 50.5f },
                ["nodes"] = BuildNodeMeta(nodes),
                ["seq1_assembly"] = BuildAssemblySequence(rig, masks, anims, n),
                ["seq2_turn360"] = BuildTurn360Sequence(rig, masks, anims, n),
                ["seq3_combat"] = BuildCombatSequence(rig, masks, anims, n),
                ["seq4_severing"] = BuildSeveringSequence(rig, masks, anims, n),
                ["seq5_crawl_closeup"] = BuildCrawlCloseupSequence(rig, masks, anims, n),
                ["seq6_crawl_patrol"] = patrolFrames,
                ["patrolPath"] = patrolPath
            };

            string dir = Path.GetDirectoryName(outJsonPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var opts = new JsonSerializerOptions { WriteIndented = false };
            File.WriteAllText(outJsonPath, JsonSerializer.Serialize(rootObj, opts));
            Console.WriteLine($"Exported C# engine frame data to: {outJsonPath}");
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

        private static Dictionary<string, object> BuildFrameDict(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            float headingDeg,
            BoneLocalPose[] localPoses,
            bool[] missing,
            string phaseLabel,
            Dictionary<string, object> extra = null,
            Dictionary<string, Vector2> blueprintExplodeWorldOffsets = null,
            Vector2? customRootXZ = null)
        {
            int n = rig.SortedNodes.Length;
            var worldPoses = new BoneWorldPose[n];
            var fp = new GiantFootprintData();
            Vector2 rootXZ = customRootXZ ?? new Vector2(50.5f, 50.5f);
            IntVec3 center = new IntVec3(Mathf.FloorToInt(rootXZ.x), 0, Mathf.FloorToInt(rootXZ.y));
            IntVec3 mapSize = new IntVec3(100, 1, 100);

            SkeletalKinematics.SolveWorldPoses(rig, rootXZ, headingDeg, localPoses, missing, worldPoses);
            fp.Rebuild(rig, worldPoses, masks, center, mapSize);

            var bonesOut = new List<object>(n);
            for (int i = 0; i < n; i++)
            {
                BoneWorldPose wp = worldPoses[i];
                Vector2 parentSocketXZ = Vector2.zero;
                if (rig.SortedNodes[i].parentRuntimeIndex >= 0)
                {
                    int pIdx = rig.SortedNodes[i].parentRuntimeIndex;
                    Vector2 suv = rig.SortedNodes[i].resolvedParentSocketUV;
                    parentSocketXZ = worldPoses[pIdx].UVToWorldXZ(suv.x, suv.y);
                }

                if (blueprintExplodeWorldOffsets != null &&
                    blueprintExplodeWorldOffsets.TryGetValue(rig.SortedNodes[i].nodeId, out Vector2 disp))
                {
                    wp.jointWorldXZ += disp;
                    wp.quadCenterWorldXZ += disp;
                }

                Vector2 tipUV = (cachedTipUVs != null && i < cachedTipUVs.Length)
                    ? cachedTipUVs[i]
                    : new Vector2(0.5f, rig.SortedNodes[i].nodeId == "tail_stinger" ? 0.07f : 0.86f);
                Vector2 tipXZ = wp.UVToWorldXZ(tipUV.x, tipUV.y);

                bonesOut.Add(new Dictionary<string, object>
                {
                    ["visible"] = wp.visible,
                    ["jointXZ"] = new[] { Round4(wp.jointWorldXZ.x), Round4(wp.jointWorldXZ.y) },
                    ["quadCenterXZ"] = new[] { Round4(wp.quadCenterWorldXZ.x), Round4(wp.quadCenterWorldXZ.y) },
                    ["angleDeg"] = Round4(wp.worldAngleDeg),
                    ["worldSize"] = new[] { Round4(wp.worldSize.x), Round4(wp.worldSize.y) },
                    ["tipXZ"] = new[] { Round4(tipXZ.x), Round4(tipXZ.y) },
                    ["socketXZ"] = new[] { Round4(parentSocketXZ.x), Round4(parentSocketXZ.y) }
                });
            }

            var cellsOut = new List<object>(fp.hitCells.Count);
            for (int i = 0; i < fp.hitCells.Count; i++)
            {
                HitCellEntry ce = fp.hitCells[i];
                cellsOut.Add(new object[]
                {
                    ce.cell.x,
                    ce.cell.z,
                    ce.isCollisionCell ? 1 : 0,
                    Round4(ce.hitCoverage),
                    ce.dominantBoneIndex
                });
            }

            var dict = new Dictionary<string, object>
            {
                ["heading"] = Round4(headingDeg),
                ["rootXZ"] = new[] { Round4(rootXZ.x), Round4(rootXZ.y) },
                ["centerCell"] = new[] { center.x, center.z },
                ["phase"] = phaseLabel,
                ["hitCount"] = fp.hitCells.Count,
                ["colCount"] = fp.collisionCells.Count,
                ["bones"] = bonesOut,
                ["cells"] = cellsOut
            };

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    dict[kv.Key] = kv.Value;
                }
            }

            return dict;
        }

        private static List<object> BuildAssemblySequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            int n)
        {
            var frames = new List<object>(72);
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];

            // Exploded blueprint displacements in world XZ space for pre-docking diagram (all 18 bones cleanly separated)
            var explodeOffsets = new Dictionary<string, Vector2>
            {
                ["carapace"] = Vector2.zero,
                ["claw_left"] = new Vector2(-1.25f, 1.15f),
                ["claw_right"] = new Vector2(1.25f, 1.15f),
                ["leg_l1"] = new Vector2(-1.55f, 0.72f),
                ["leg_l2"] = new Vector2(-1.78f, 0.24f),
                ["leg_l3"] = new Vector2(-1.78f, -0.24f),
                ["leg_l4"] = new Vector2(-1.55f, -0.72f),
                ["leg_r1"] = new Vector2(1.55f, 0.72f),
                ["leg_r2"] = new Vector2(1.78f, 0.24f),
                ["leg_r3"] = new Vector2(1.78f, -0.24f),
                ["leg_r4"] = new Vector2(1.55f, -0.72f),
                ["tail_seg1"] = new Vector2(0f, -0.35f),
                ["tail_seg2"] = new Vector2(0f, -0.70f),
                ["tail_seg3"] = new Vector2(0f, -1.05f),
                ["tail_seg4"] = new Vector2(0f, -1.40f),
                ["tail_seg5"] = new Vector2(0f, -1.75f),
                ["tail_seg6"] = new Vector2(0f, -2.10f),
                ["tail_stinger"] = new Vector2(0f, -2.55f)
            };

            GiantAnimDef walk = anims["GCF_Anim_MechScorpion_Walk"];

            for (int f = 0; f < 72; f++)
            {
                float explodeFactor;
                string label;
                float footprintAlpha;

                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;

                if (f < 14)
                {
                    explodeFactor = 1f;
                    footprintAlpha = 0f;
                    label = "18x 512x512 独立部件拆解 (8独立步足 + 7节串联弯曲长尾)";
                }
                else if (f < 32)
                {
                    float u = (f - 14) / 17f;
                    float s = u * u * (3f - 2f * u);
                    explodeFactor = 1f - s;
                    footprintAlpha = Mathf.Clamp01((u - 0.65f) / 0.35f);
                    label = "节点轴插槽同轴挂接装配 (17处同轴关节与串联尾节锁死)";
                }
                else if (f < 38)
                {
                    explodeFactor = 0f;
                    footprintAlpha = 1f;
                    label = "装配完成: 全局无呼吸动画, 17处同轴连接点零偏移锁死";
                }
                else if (f < 62)
                {
                    explodeFactor = 0f;
                    footprintAlpha = 1f;
                    float walkTick = ((f - 38) / 24f) * walk.durationTicks;
                    walk.EvaluateInto(rig, walkTick, localPoses);
                    label = "纯骨骼旋转驱动: 8足四足波浪步态 + 7节长尾波浪摆动";
                }
                else
                {
                    float u = (f - 62) / 9f;
                    float s = u * u * (3f - 2f * u);
                    explodeFactor = s;
                    footprintAlpha = 1f - s;
                    label = "部件拆解示意复位";
                }

                Dictionary<string, Vector2> dispDict = null;
                if (explodeFactor > 1e-4f)
                {
                    dispDict = new Dictionary<string, Vector2>(explodeOffsets.Count);
                    foreach (var kv in explodeOffsets)
                    {
                        dispDict[kv.Key] = kv.Value * explodeFactor;
                    }
                }

                frames.Add(BuildFrameDict(rig, masks, 0f, localPoses, missing, label, new Dictionary<string, object>
                {
                    ["explodeFactor"] = Round4(explodeFactor),
                    ["footprintAlpha"] = Round4(footprintAlpha)
                }, dispDict));
            }
            return frames;
        }

        private static List<object> BuildTurn360Sequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            int n)
        {
            var frames = new List<object>(80);
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];
            GiantAnimDef walk = anims["GCF_Anim_MechScorpion_Walk"];

            for (int f = 0; f < 80; f++)
            {
                float heading = (f / 80f) * 360f;
                // 4 complete walk cycles across 80 frames (20 frames per cycle) for seamless loop
                float walkTick = ((f % 20) / 20f) * walk.durationTicks;
                walk.EvaluateInto(rig, walkTick, localPoses);

                string label = $"360度连续转向与液压步态 | 朝向 {heading:F0} 度";
                frames.Add(BuildFrameDict(rig, masks, heading, localPoses, missing, label));
            }
            return frames;
        }

        private static List<object> BuildCombatSequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            int n)
        {
            var frames = new List<object>(96);
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];
            float heading = 22f;

            GiantAnimDef clawL = anims["GCF_Anim_MechScorpion_ClawLeft"];
            GiantAnimDef clawR = anims["GCF_Anim_MechScorpion_ClawRight"];
            GiantAnimDef tailSting = anims["GCF_Anim_MechScorpion_TailSting"];
            GiantAnimDef rangedTail = anims["GCF_Anim_MechScorpion_RangedTail"];

            for (int f = 0; f < 96; f++)
            {
                string label;
                var extra = new Dictionary<string, object>();

                int strikeBoneIdx = -1;
                if (f < 22)
                {
                    float t = (f / 21f) * clawL.durationTicks;
                    clawL.EvaluateInto(rig, t, localPoses);
                    label = "近战骨骼动画 1/4: 左液压粉碎巨钳钳击 (ClawLeft)";
                    if (f >= 10 && f <= 14)
                    {
                        rig.TryGetNodeIndex("claw_left", out strikeBoneIdx);
                        extra["shockProgress"] = Round4((f - 10) / 4f);
                    }
                }
                else if (f < 44)
                {
                    int lf = f - 22;
                    float t = (lf / 21f) * clawR.durationTicks;
                    clawR.EvaluateInto(rig, t, localPoses);
                    label = "近战骨骼动画 2/4: 右液压粉碎巨钳钳击 (ClawRight)";
                    if (lf >= 10 && lf <= 14)
                    {
                        rig.TryGetNodeIndex("claw_right", out strikeBoneIdx);
                        extra["shockProgress"] = Round4((lf - 10) / 4f);
                    }
                }
                else if (f < 70)
                {
                    int lf = f - 44;
                    float t = (lf / 25f) * tailSting.durationTicks;
                    tailSting.EvaluateInto(rig, t, localPoses);
                    label = "近战骨骼动画 3/4: 7节长尾弧形弯曲过顶突刺 (TailSting)";
                    if (lf >= 11 && lf <= 16)
                    {
                        rig.TryGetNodeIndex("tail_stinger", out strikeBoneIdx);
                        extra["shockProgress"] = Round4((lf - 11) / 5f);
                    }
                }
                else
                {
                    int lf = f - 70;
                    float t = (lf / 25f) * rangedTail.durationTicks;
                    rangedTail.EvaluateInto(rig, t, localPoses);
                    label = "远程骨骼动画 4/4: 长尾C形弯曲架炮等离子三连射 (RangedTail)";
                    extra["plasmaBurstFrame"] = lf;
                }

                if (strikeBoneIdx >= 0)
                {
                    extra["shockBoneIdx"] = strikeBoneIdx;
                }

                frames.Add(BuildFrameDict(rig, masks, heading, localPoses, missing, label, extra));
            }
            return frames;
        }

        private static List<object> BuildSeveringSequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            int n)
        {
            var frames = new List<object>(92);
            var localPoses = new BoneLocalPose[n];
            for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
            var missing = new bool[n];

            rig.TryGetNodeIndex("claw_left", out int clawLeftIdx);
            rig.TryGetNodeIndex("tail_seg2", out int tailIdx);

            // Pre-solve exact impact world coordinates using real GiantFootprintData.TryHitTestSegment on fixed rest pose!
            var refWorld = new BoneWorldPose[n];
            SkeletalKinematics.SolveWorldPoses(rig, new Vector2(50.5f, 50.5f), 0f, localPoses, missing, refWorld);
            var refFp = new GiantFootprintData();
            refFp.Rebuild(rig, refWorld, masks, new IntVec3(50, 0, 50), new IntVec3(100, 1, 100));

            Vector2 clTarget = refWorld[clawLeftIdx].UVToWorldXZ(0.45f, 0.56f);
            Vector3 shot2Start = new Vector3(45.8f, 0f, 55.6f);
            Vector3 shot2End = new Vector3(clTarget.x + 0.4f, 0f, clTarget.y - 0.4f);
            refFp.TryHitTestSegment(rig, masks, shot2Start, shot2End, out _, out Vector3 exactClawHit);

            Vector2 seg2Target = refWorld[tailIdx].UVToWorldXZ(0.5f, 0.52f);
            Vector3 shotTailStart = new Vector3(53.2f, 0f, 47.4f);
            Vector3 shotTailEnd = new Vector3(seg2Target.x - 0.1f, 0f, seg2Target.y);
            refFp.TryHitTestSegment(rig, masks, shotTailStart, shotTailEnd, out _, out Vector3 exactTailHit);

            int clawHp = 270;
            int tailHp = 338;

            for (int f = 0; f < 92; f++)
            {
                for (int i = 0; i < n; i++) localPoses[i] = BoneLocalPose.Identity;
                if (f >= 28 && f <= 35 && clawLeftIdx >= 0)
                {
                    float recoil = 1f - (f - 28) / 7f;
                    localPoses[clawLeftIdx] = new BoneLocalPose(Vector2.zero, 7f * recoil, Vector2.one);
                }

                string label;
                var extra = new Dictionary<string, object>();

                if (f < 20)
                {
                    label = "弹道测试 1: 穿过包围盒内透明像素区 -> 零误判直接穿透";
                    float u = f / 19f;
                    Vector2 p0 = new Vector2(45.8f, 46.8f);
                    Vector2 p1 = new Vector2(49.2f, 46.2f);
                    Vector2 cur = Vector2.Lerp(p0, p1, u);
                    extra["bullet"] = new object[] { Round4(p0.x), Round4(p0.y), Round4(cur.x), Round4(cur.y), 0 };
                }
                else if (f < 38)
                {
                    int lf = f - 20;
                    label = "弹道测试 2: 命中左粉碎巨钳像素 -> 路由至左巨钳部位扣血";
                    Vector2 p0 = new Vector2(shot2Start.x, shot2Start.z);
                    Vector2 p1 = new Vector2(exactClawHit.x, exactClawHit.z);
                    if (lf <= 8)
                    {
                        Vector2 cur = Vector2.Lerp(p0, p1, lf / 8f);
                        extra["bullet"] = new object[] { Round4(p0.x), Round4(p0.y), Round4(cur.x), Round4(cur.y), 1 };
                    }
                    if (lf >= 8)
                    {
                        clawHp = 135;
                        if (lf <= 14)
                        {
                            extra["hitSpark"] = new object[] { Round4(p1.x), Round4(p1.y), Round4((lf - 8) / 6f), 0 };
                        }
                    }
                }
                else if (f < 56)
                {
                    int lf = f - 38;
                    Vector2 p0 = new Vector2(46.2f, 55.4f);
                    Vector2 p1 = new Vector2(exactClawHit.x, exactClawHit.z);
                    if (lf < 8)
                    {
                        label = "弹道测试 3: 集火击毁左粉碎巨钳...";
                        Vector2 cur = Vector2.Lerp(p0, p1, lf / 8f);
                        extra["bullet"] = new object[] { Round4(p0.x), Round4(p0.y), Round4(cur.x), Round4(cur.y), 1 };
                    }
                    else
                    {
                        clawHp = 0;
                        missing[clawLeftIdx] = true;
                        label = "左巨钳被摧毁: 贴图自动隐藏 + 左前受击/碰撞格实时移除!";
                        if (lf <= 15)
                        {
                            extra["hitSpark"] = new object[] { Round4(p1.x), Round4(p1.y), Round4((lf - 8) / 7f), 1 };
                        }
                    }
                }
                else if (f < 76)
                {
                    int lf = f - 56;
                    Vector2 p0 = new Vector2(shotTailStart.x, shotTailStart.z);
                    Vector2 p1 = new Vector2(exactTailHit.x, exactTailHit.z);
                    if (lf < 9)
                    {
                        label = "弹道测试 4: 侧后集火命中第2装甲尾节 (tail_seg2)...";
                        Vector2 cur = Vector2.Lerp(p0, p1, lf / 9f);
                        extra["bullet"] = new object[] { Round4(p0.x), Round4(p0.y), Round4(cur.x), Round4(cur.y), 1 };
                        if (lf >= 5) tailHp = 169;
                    }
                    else
                    {
                        tailHp = 0;
                        missing[tailIdx] = true;
                        label = "第2尾节击毁: 链式断尾 (2~6节+尾刺消失) + 仅留第1尾节残桩!";
                        if (lf <= 16)
                        {
                            extra["hitSpark"] = new object[] { Round4(p1.x), Round4(p1.y), Round4((lf - 9) / 7f), 1 };
                        }
                    }
                }
                else
                {
                    clawHp = 0;
                    tailHp = 0;
                    missing[clawLeftIdx] = true;
                    missing[tailIdx] = true;
                    label = $"残损态持续运行: 剩余 11 部件受击体积由 {refFp.hitCells.Count} 格动态收缩!";
                }

                extra["clawHp"] = clawHp;
                extra["tailHp"] = tailHp;

                frames.Add(BuildFrameDict(rig, masks, 0f, localPoses, missing, label, extra));
            }
            return frames;
        }

        private static List<object> BuildCrawlCloseupSequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            int n)
        {
            var frames = new List<object>(64);
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];
            GiantAnimDef walk = anims["GCF_Anim_MechScorpion_Walk"];
            float cycleDist = Mathf.Max(0.25f, rig.walkCycleDistance);

            for (int f = 0; f < 64; f++)
            {
                float distMoved = (f / 64f) * (cycleDist * 2f);
                float z = 48.5f + distMoved;
                Vector2 rootXZ = new Vector2(50.5f, z);
                float walkTick = ((distMoved / cycleDist) * walk.durationTicks) % walk.durationTicks;
                walk.EvaluateInto(rig, walkTick, localPoses);

                int showOverlay = f >= 32 ? 1 : 0;
                string label = showOverlay == 0
                    ? "纯骨骼爬行特写 (纯净贴图): 8足四足波浪步态 + 7节长尾摆动"
                    : "骨骼插槽特写: 17组同轴关节锁死, 8足迈步 + 7节长尾联动";

                frames.Add(BuildFrameDict(rig, masks, 0f, localPoses, missing, label, new Dictionary<string, object>
                {
                    ["showOverlay"] = showOverlay,
                    ["walkTick"] = Round4(walkTick)
                }, customRootXZ: rootXZ));
            }
            return frames;
        }

        private static (List<object> frames, List<object> pathPoints) BuildCrawlPatrolSequence(
            GiantRigDef rig,
            TextureAlphaMask[] masks,
            Dictionary<string, GiantAnimDef> anims,
            int n)
        {
            Vector2 p0 = new Vector2(47.8f, 48.0f);
            Vector2 p1 = new Vector2(48.0f, 52.6f);
            Vector2 p2 = new Vector2(53.6f, 52.5f);

            static Vector2 EvalQuadBezier(Vector2 a, Vector2 b, Vector2 c, float s)
            {
                float u = 1f - s;
                return (u * u) * a + (2f * u * s) * b + (s * s) * c;
            }

            static Vector2 EvalQuadTangent(Vector2 a, Vector2 b, Vector2 c, float s)
            {
                return 2f * (1f - s) * (b - a) + 2f * s * (c - b);
            }

            const int samples = 500;
            var arcLen = new float[samples + 1];
            Vector2 prevPt = p0;
            arcLen[0] = 0f;
            for (int k = 1; k <= samples; k++)
            {
                float s = k / (float)samples;
                Vector2 pt = EvalQuadBezier(p0, p1, p2, s);
                arcLen[k] = arcLen[k - 1] + Vector2.Distance(prevPt, pt);
                prevPt = pt;
            }
            float totalLen = arcLen[samples];

            float DistanceToT(float d)
            {
                d = Mathf.Clamp(d, 0f, totalLen);
                int idx = Array.BinarySearch(arcLen, d);
                if (idx >= 0) return idx / (float)samples;
                int hi = ~idx;
                if (hi <= 0) return 0f;
                if (hi > samples) return 1f;
                int lo = hi - 1;
                float span = arcLen[hi] - arcLen[lo];
                float frac = span > 1e-6f ? (d - arcLen[lo]) / span : 0f;
                return (lo + frac) / samples;
            }

            var pathPoints = new List<object>(25);
            for (int k = 0; k <= 24; k++)
            {
                Vector2 pt = EvalQuadBezier(p0, p1, p2, k / 24f);
                pathPoints.Add(new[] { Round4(pt.x), Round4(pt.y) });
            }

            var frames = new List<object>(104);
            var walkPoses = new BoneLocalPose[n];
            var localPoses = new BoneLocalPose[n];
            var missing = new bool[n];
            GiantAnimDef walk = anims["GCF_Anim_MechScorpion_Walk"];
            float cycleDist = Mathf.Max(0.25f, rig.walkCycleDistance);

            Vector2 initTan = EvalQuadTangent(p0, p1, p2, 0f);
            float currentHeading = SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(initTan.x, initTan.y) * Mathf.Rad2Deg);
            float walkPhaseTicks = 0f;
            float prevDist = 0f;

            // Smooth trapezoid distance curve across 80 active frames
            static float SpeedRampDistFraction(float u)
            {
                // Velocity v(u) ramps 0->1 on [0, 0.12], stays 1 on [0.12, 0.88], ramps 1->0 on [0.88, 1.0]
                const float r = 0.12f;
                float norm = 1f - r;
                if (u <= 0f) return 0f;
                if (u >= 1f) return 1f;
                if (u < r) return (0.5f * u * u / r) / norm;
                if (u <= 1f - r) return (0.5f * r + (u - r)) / norm;
                float v = 1f - u;
                return 1f - (0.5f * v * v / r) / norm;
            }

            for (int f = 0; f < 104; f++)
            {
                Vector2 rootXZ;
                float blendWeight;
                string label;

                if (f < 12)
                {
                    rootXZ = p0;
                    blendWeight = 0f;
                    label = "战术驻停 (Idle): 全局禁止呼吸动画, 躯干与连接点完全静止";
                }
                else if (f < 92)
                {
                    float u = (f - 11) / 80f;
                    float curDist = SpeedRampDistFraction(u) * totalLen;
                    float stepDist = MathF.Max(0f, curDist - prevDist);
                    prevDist = curDist;

                    float curveS = DistanceToT(curDist);
                    rootXZ = EvalQuadBezier(p0, p1, p2, curveS);
                    Vector2 tan = EvalQuadTangent(p0, p1, p2, curveS);
                    float targetHeading = SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(tan.x, tan.y) * Mathf.Rad2Deg);

                    float maxStep = rig.turnSpeedDegPerTick * 2.5f;
                    float diff = Mathf.DeltaAngle(currentHeading, targetHeading);
                    currentHeading = MathF.Abs(diff) <= maxStep
                        ? targetHeading
                        : SkeletalKinematics.NormalizeAngle360(currentHeading + MathF.Sign(diff) * maxStep);

                    walkPhaseTicks = (walkPhaseTicks + (stepDist / cycleDist) * walk.durationTicks) % walk.durationTicks;
                    float inBlend = Mathf.Clamp01((f - 11) / 6f);
                    float outBlend = Mathf.Clamp01((92 - f) / 6f);
                    blendWeight = Mathf.Min(inBlend, outBlend);

                    if (f < 18)
                        label = "启动过渡 (Cross-Fade): 由静止无缝切入纯旋转液压步态";
                    else if (f < 86)
                        label = $"地图实景移动爬行 | 朝向 {currentHeading:F0} 度 | 体积格实时步进";
                    else
                        label = "抵达目标减速: 平滑收回刚性静止姿态";
                }
                else
                {
                    rootXZ = p2;
                    blendWeight = 0f;
                    label = "抵达驻停 (Idle): 零呼吸晃动, 全连接点100%锁死于躯干";
                }

                if (blendWeight > 1e-4f)
                {
                    walk.EvaluateInto(rig, walkPhaseTicks, walkPoses);
                    for (int i = 0; i < n; i++)
                    {
                        localPoses[i] = BoneLocalPose.Lerp(BoneLocalPose.Identity, in walkPoses[i], blendWeight);
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        localPoses[i] = BoneLocalPose.Identity;
                    }
                }

                frames.Add(BuildFrameDict(rig, masks, currentHeading, localPoses, missing, label, customRootXZ: rootXZ));
            }

            return (frames, pathPoints);
        }

        private static Vector2[] ComputeBoneTipUVs(GiantRigDef rig, TextureAlphaMask[] masks)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            var result = new Vector2[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef b = nodes[i];
                if (b.nodeId == "carapace" || b.nodeId == "claw_left" || b.nodeId == "claw_right")
                {
                    result[i] = new Vector2(0.5f, 0.86f);
                    continue;
                }
                if (b.nodeId == "tail_stinger")
                {
                    result[i] = new Vector2(0.5f, 1f - 408f / 512f);
                    continue;
                }

                // For serial chain bones (tail_seg1..tail_seg6), tip points cleanly to the child bone's socket UV
                Vector2? serialChildSocket = null;
                for (int j = i + 1; j < nodes.Length; j++)
                {
                    if (nodes[j].parentRuntimeIndex == i)
                    {
                        serialChildSocket = nodes[j].resolvedParentSocketUV;
                        break;
                    }
                }
                if (serialChildSocket.HasValue)
                {
                    result[i] = serialChildSocket.Value;
                    continue;
                }

                Vector2 piv = b.resolvedPivotUV;
                float bestDistSq = -1f;
                Vector2 bestUV = new Vector2(0.5f, 0.5f);
                TextureAlphaMask m = masks[i];
                for (int y = 0; y < 512; y += 2)
                {
                    float v = (y + 0.5f) / 512f;
                    for (int x = 0; x < 512; x += 2)
                    {
                        float u = (x + 0.5f) / 512f;
                        if (m.IsOpaqueUV(u, v))
                        {
                            float du = u - piv.x;
                            float dv = v - piv.y;
                            float d2 = du * du + dv * dv;
                            if (d2 > bestDistSq)
                            {
                                bestDistSq = d2;
                                bestUV = new Vector2(u, v);
                            }
                        }
                    }
                }
                result[i] = bestUV;
            }
            return result;
        }

        private static float Round4(float v) => MathF.Round(v, 4);
    }
}
