using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class Dialog_GiantRigInspector : Window
    {
        private readonly CompGiantRig comp;
        private Vector2 scrollPos;
        private GiantRigDef cachedClipsRig;
        private List<GiantAnimDef> cachedClips;

        public override Vector2 InitialSize => new Vector2(540f, 620f);

        public Dialog_GiantRigInspector(CompGiantRig comp)
        {
            this.comp = comp;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (comp == null || comp.Pawn == null || comp.Pawn.Destroyed || comp.RigDef == null)
            {
                Close();
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 36f, 32f),
                $"{"GCF_InspectorTitle".Translate()} - {comp.Pawn.LabelShortCap}");
            Text.Font = GameFont.Small;

            float y = inRect.y + 38f;
            GiantFootprintData fp = comp.Footprint;
            string stats = string.Format(
                "GCF_InspectorSummary".Translate(),
                comp.CurrentHeadingDeg.ToString("F1"),
                comp.CurrentAnimState.ToString(),
                comp.ActiveClip?.defName ?? "None",
                fp.hitCells.Count,
                fp.collisionCells.Count);
            if (comp.SteeringRuntime != null && comp.SteeringRuntime.HasAnySteeringParts)
            {
                stats += $" | {comp.GetSteeringDebugSummary()}";
            }
            stats += $" | {comp.GetEmitterDebugSummary()}";
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 44f), stats);
            y += 48f;

            // Overlay toggles (responsive half widths)
            float halfW = Mathf.Max(80f, (inRect.width - 10f) * 0.5f);
            Rect row1 = new Rect(inRect.x, y, inRect.width, 26f);
            Widgets.CheckboxLabeled(new Rect(row1.x, row1.y, halfW, 24f), "GCF_ToggleFootprint".Translate(), ref comp.DebugDrawFootprint);
            Widgets.CheckboxLabeled(new Rect(row1.x + halfW + 10f, row1.y, halfW, 24f), "GCF_ToggleSkeleton".Translate(), ref comp.DebugDrawSkeleton);
            y += 30f;

            Rect row2 = new Rect(inRect.x, y, inRect.width, 26f);
            bool prevGlobalFp = GiantFootprintMapComponent.GlobalDebugDrawFootprints;
            bool prevManualOverride = comp.ManualOverrideActive;
            Widgets.CheckboxLabeled(new Rect(row2.x, row2.y, halfW, 24f), "GCF_ToggleGlobalFootprint".Translate(), ref GiantFootprintMapComponent.GlobalDebugDrawFootprints);
            Widgets.CheckboxLabeled(new Rect(row2.x + halfW + 10f, row2.y, halfW, 24f), "GCF_ToggleManualPose".Translate(), ref comp.ManualOverrideActive);
            if (GiantFootprintMapComponent.GlobalDebugDrawFootprints != prevGlobalFp)
            {
                bool gfp = GiantFootprintMapComponent.GlobalDebugDrawFootprints;
                GiantFootprintMapComponent.GlobalDebugDrawCollisionCells = gfp;
                GiantFootprintMapComponent.GlobalDebugDrawHitCells = gfp;
                GiantCreatureFrameworkMod.Settings.drawCollisionCells = gfp;
                GiantCreatureFrameworkMod.Settings.drawHitCells = gfp;
            }
            if (comp.ManualOverrideActive != prevManualOverride)
            {
                comp.EvaluateCurrentPoseAndPublish(0f);
                comp.RebuildFootprintNow(notifyMap: true);
            }
            y += 34f;

            // Animation clip triggers
            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 8f;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), "GCF_AnimClipSection".Translate());
            y += 24f;

            if (cachedClips == null || cachedClipsRig != comp.RigDef)
            {
                cachedClipsRig = comp.RigDef;
                cachedClips = CollectClips(comp.RigDef);
            }
            var clips = cachedClips;
            float btnW = (inRect.width - 16f) / 3f;
            int col = 0;
            for (int i = 0; i < clips.Count; i++)
            {
                GiantAnimDef clip = clips[i];
                Rect btnRect = new Rect(inRect.x + col * (btnW + 8f), y, btnW, 28f);
                if (Widgets.ButtonText(btnRect, clip.label.NullOrEmpty() ? clip.defName : clip.label))
                {
                    comp.TriggerPreviewClip(clip);
                }
                col++;
                if (col >= 3)
                {
                    col = 0;
                    y += 32f;
                }
            }
            if (col > 0)
            {
                y += 32f;
            }

            if (Widgets.ButtonText(new Rect(inRect.x, y, 148f, 26f), "GCF_ResetPoseBtn".Translate()))
            {
                comp.ManualOverrideActive = false;
                if (comp.ManualBoneAngleOffsets != null)
                {
                    System.Array.Clear(comp.ManualBoneAngleOffsets, 0, comp.ManualBoneAngleOffsets.Length);
                }
                comp.HitReactionRuntime?.Reset();
                comp.TriggerPreviewClip(null);
                comp.EvaluateCurrentPoseAndPublish(0f);
                comp.RebuildFootprintNow(notifyMap: true);
            }
            float hitX = inRect.x + 156f;
            float hitBtnW = Mathf.Max(68f, (inRect.width - 156f - 12f) / 3f);
            if (Widgets.ButtonText(new Rect(hitX, y, hitBtnW, 26f), "Hit: Light"))
            {
                comp.TriggerSyntheticHitReaction(GiantHitReactionTier.LocalFlinch);
            }
            if (Widgets.ButtonText(new Rect(hitX + hitBtnW + 6f, y, hitBtnW, 26f), "Hit: Heavy"))
            {
                comp.TriggerSyntheticHitReaction(GiantHitReactionTier.HeavyFlinch);
            }
            if (Widgets.ButtonText(new Rect(hitX + (hitBtnW + 6f) * 2f, y, hitBtnW, 26f), "Hit: Break"))
            {
                comp.TriggerSyntheticHitReaction(GiantHitReactionTier.PartBreakStagger);
            }
            y += 34f;

            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 8f;

            // Bone hierarchy & angle sliders
            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(60f, inRect.yMax - y));
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(40f, outRect.width - 20f), nodes.Length * 58f + 12f);
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            try
            {
                float sy = 4f;
                for (int i = 0; i < nodes.Length; i++)
                {
                    BoneNodeDef bDef = nodes[i];
                    bool vis = comp.IsBoneVisibleThreadSafe(i);
                    string parentStr = string.IsNullOrEmpty(bDef.parentId) ? "Root" : bDef.parentId;
                    string status = vis ? "OK" : "MISSING";
                    string partInfo = "";
                    BodyPartRecord part = comp.GetLinkedBodyPartRaw(i);
                    if (part != null && comp.Pawn?.health?.hediffSet != null)
                    {
                        float maxHp = part.def.GetMaxHealth(comp.Pawn);
                        float curHp = comp.Pawn.health.hediffSet.GetPartHealth(part);
                        partInfo = $" | {part.LabelCap}: {curHp:F0}/{maxHp:F0} HP";
                    }
                    Widgets.Label(new Rect(4f, sy, viewRect.width - 8f, 22f),
                        $"[{i}] {bDef.nodeId} ({bDef.label ?? bDef.nodeId}) | Parent: {parentStr} | Layer: {bDef.layerOffset:+0.#;-0.#;0} | {status}{partInfo}");
                    sy += 22f;

                    if (comp.ManualBoneAngleOffsets != null && i < comp.ManualBoneAngleOffsets.Length)
                    {
                        float prev = comp.ManualBoneAngleOffsets[i];
                        float next = Widgets.HorizontalSlider(
                            new Rect(8f, sy, viewRect.width - 16f, 24f),
                            prev, -90f, 90f,
                            middleAlignment: true,
                            label: $"Angle Delta: {prev:+0.0;-0.0;0.0} deg");
                        if (Mathf.Abs(next - prev) > 0.01f)
                        {
                            comp.ManualOverrideActive = true;
                            comp.ManualBoneAngleOffsets[i] = next;
                            comp.EvaluateCurrentPoseAndPublish(0f);
                            comp.RebuildFootprintNow(notifyMap: true);
                        }
                    }
                    sy += 32f;
                }
            }
            finally
            {
                Widgets.EndScrollView();
            }
        }

        private static List<GiantAnimDef> CollectClips(GiantRigDef rig)
        {
            var list = new List<GiantAnimDef>();
            void AddUnique(GiantAnimDef d)
            {
                if (d != null && !list.Contains(d))
                {
                    list.Add(d);
                }
            }
            AddUnique(rig.walkAnim);
            AddUnique(rig.runAnim);
            if (rig.meleeAttackAnims != null)
            {
                for (int i = 0; i < rig.meleeAttackAnims.Count; i++)
                {
                    AddUnique(rig.meleeAttackAnims[i]);
                }
            }
            AddUnique(rig.rangedAttackAnim);
            if (rig.abilityAnims != null)
            {
                for (int i = 0; i < rig.abilityAnims.Count; i++)
                {
                    AddUnique(rig.abilityAnims[i]);
                }
            }
            AddUnique(rig.downedAnim);
            AddUnique(rig.deathAnim);
            return list;
        }
    }
}
