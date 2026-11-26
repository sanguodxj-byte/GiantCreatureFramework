using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class ITab_GiantCockpit : ITab
    {
        private Vector2 scrollPos = Vector2.zero;

        public ITab_GiantCockpit()
        {
            size = new Vector2(468f, 316f);
            labelKey = "GCF_Tab_Cockpit";
        }

        private Pawn SelGiant => SelPawn ?? (SelThing as Pawn);
        private CompGiantCockpit SelCockpit => GiantFastLookup.TryGetCockpit(SelGiant);

        public override bool IsVisible
        {
            get
            {
                Pawn p = SelGiant;
                return p != null
                    && p.Spawned
                    && !p.Dead
                    && (p.Faction == Faction.OfPlayer || DebugSettings.godMode)
                    && SelCockpit != null;
            }
        }

        protected override void FillTab()
        {
            Pawn host = SelGiant;
            CompGiantCockpit cockpit = SelCockpit;
            if (host == null || cockpit == null)
            {
                return;
            }

            bool canManageCrew = host.Faction == Faction.OfPlayer;
            Rect outer = new Rect(12f, 12f, size.x - 24f, size.y - 24f);
            GUI.BeginGroup(outer);
            try
            {
                // Header bar: Control Mode + Cached Efficiency Multipliers + Disembark All
                Rect headerRect = new Rect(0f, 0f, outer.width, 56f);
                GUI.DrawTexture(headerRect, GiantCockpitGizmoAssets.CardBgTex);
                Widgets.DrawBox(headerRect, 1);

                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;

                string modeTitle = cockpit.GetLocalizedControlModeLabel();
                Color modeColor = cockpit.CurrentMode switch
                {
                    GiantCockpitControlMode.Piloted => new Color(0.38f, 0.92f, 1f),
                    GiantCockpitControlMode.Autonomous => new Color(0.75f, 0.88f, 0.62f),
                    _ => new Color(0.72f, 0.74f, 0.78f)
                };

                GUI.color = modeColor;
                Widgets.Label(new Rect(10f, 4f, outer.width - 130f, 24f), modeTitle);
                GUI.color = Color.white;

                string movePct = GiantCockpitPercentCache.FormatMultiplierPct(cockpit.ComputeMoveSpeedMultiplier());
                string accPct = GiantCockpitPercentCache.FormatMultiplierPct(cockpit.ComputeWeaponAccuracyMultiplier());
                string relPct = GiantCockpitPercentCache.FormatMultiplierPct(cockpit.ComputeReloadSpeedMultiplier());
                string statsSummary = "GCF_CockpitTab_EfficiencySummary".CanTranslate()
                    ? string.Format("GCF_CockpitTab_EfficiencySummary".Translate(), movePct, accPct, relPct)
                    : $"Move: {movePct}  |  Accuracy: {accPct}  |  Reload: {relPct}";

                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.82f, 0.85f, 0.90f);
                Widgets.Label(new Rect(10f, 29f, outer.width - 130f, 22f), statsSummary);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (cockpit.CrewCount > 0 && canManageCrew)
                {
                    Rect ejectAllBtn = new Rect(outer.width - 114f, 13f, 104f, 30f);
                    string btnLabel = "GCF_CockpitTab_EjectAllBtn".CanTranslate()
                        ? "GCF_CockpitTab_EjectAllBtn".Translate().ToString()
                        : "Disembark All";
                    if (Widgets.ButtonText(ejectAllBtn, btnLabel))
                    {
                        cockpit.EjectAll(violent: false);
                    }
                }

                // Seat list scroll area
                float listTop = 64f;
                float listHeight = outer.height - listTop;
                int seatCount = cockpit.SeatCount;
                float rowHeight = 64f;
                float viewHeight = Mathf.Max(listHeight, seatCount * (rowHeight + 6f));

                Rect scrollOutRect = new Rect(0f, listTop, outer.width, listHeight);
                Rect viewRect = new Rect(0f, 0f, outer.width - 16f, viewHeight);

                Widgets.BeginScrollView(scrollOutRect, ref scrollPos, viewRect);
                try
                {
                    float curY = 0f;

                    for (int i = 0; i < seatCount; i++)
                    {
                        DrawSeatRow(host, cockpit, i, new Rect(0f, curY, viewRect.width, rowHeight), canManageCrew);
                        curY += rowHeight + 6f;
                    }
                }
                finally
                {
                    Widgets.EndScrollView();
                }
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                GUI.EndGroup();
            }
        }

        private static void DrawSeatRow(Pawn host, CompGiantCockpit cockpit, int seatIdx, Rect rowRect, bool canManageCrew)
        {
            GUI.DrawTexture(rowRect, GiantCockpitGizmoAssets.CardBgTex);
            Widgets.DrawBox(rowRect, 1);

            GiantCockpitSeatDef seatDef = cockpit.Props.seats[seatIdx];
            string roleLabel = seatDef.GetLocalizedRoleLabel();
            string partLabel = cockpit.ResolveSeatPartDisplayLabel(seatIdx);
            bool partIntact = cockpit.IsSeatPartIntact(seatIdx);
            float partHp01 = cockpit.GetSeatPartHealthPct(seatIdx);
            Pawn occupant = cockpit.GetOccupantAt(seatIdx);

            // Row top: Role + Linked Part Label (strictly bounded left of occRect at x + 196f)
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rowRect.x + 10f, rowRect.y + 5f, 176f, 22f), $"[{roleLabel}] {partLabel}");

            // Structural Integrity Bar with centered integer %
            Rect barRect = new Rect(rowRect.x + 10f, rowRect.y + 32f, 176f, 20f);
            GUI.DrawTexture(barRect, GiantCockpitGizmoAssets.BarBgTex);
            Texture2D fillTex = !partIntact
                ? GiantCockpitGizmoAssets.BarSeveredFillTex
                : (partHp01 < 0.5f ? GiantCockpitGizmoAssets.BarDamagedFillTex : GiantCockpitGizmoAssets.BarHpFillTex);
            float fillW = partIntact ? Mathf.Round((barRect.width - 2f) * Mathf.Clamp01(partHp01)) : (barRect.width - 2f);
            if (fillW > 0.5f)
            {
                GUI.DrawTexture(new Rect(barRect.x + 1f, barRect.y + 1f, fillW, barRect.height - 2f), fillTex);
            }
            Widgets.DrawBox(barRect, 1);

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            string barText = partIntact
                ? GiantCockpitPercentCache.FormatRatio01Pct(partHp01)
                : ("GCF_CockpitTab_PartDestroyed".CanTranslate()
                    ? "GCF_CockpitTab_PartDestroyed".Translate().ToString()
                    : "DESTROYED");
            Widgets.Label(barRect, barText);

            // Occupant status + action button on right
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect occRect = new Rect(rowRect.x + 196f, rowRect.y + 6f, rowRect.width - 294f, 52f);
            Rect btnRect = new Rect(rowRect.xMax - 90f, rowRect.y + 17f, 80f, 30f);

            if (occupant != null)
            {
                float consc01 = occupant.health?.capacities?.GetLevel(PawnCapacityDefOf.Consciousness) ?? 1f;
                string conscPct = GiantCockpitPercentCache.FormatRatio01Pct(consc01);
                string occInfo = "GCF_CockpitTab_OccupantStatus".CanTranslate()
                    ? string.Format("GCF_CockpitTab_OccupantStatus".Translate(), occupant.LabelShort, conscPct)
                    : $"{occupant.LabelShort} ({conscPct})";
                Widgets.Label(occRect, occInfo);

                if (canManageCrew)
                {
                    string ejectLbl = "GCF_CockpitTab_DisembarkBtn".CanTranslate()
                        ? "GCF_CockpitTab_DisembarkBtn".Translate().ToString()
                        : "Eject";
                    if (Widgets.ButtonText(btnRect, ejectLbl))
                    {
                        cockpit.EjectSeat(seatIdx, violent: false);
                    }
                }
            }
            else if (partIntact)
            {
                GUI.color = new Color(0.68f, 0.72f, 0.78f);
                string emptyStr = "GCF_CockpitTab_SeatEmpty".CanTranslate()
                    ? "GCF_CockpitTab_SeatEmpty".Translate().ToString()
                    : "Empty";
                Widgets.Label(occRect, emptyStr);
                GUI.color = Color.white;

                if (canManageCrew)
                {
                    string boardLbl = "GCF_CockpitTab_AssignBtn".CanTranslate()
                        ? "GCF_CockpitTab_AssignBtn".Translate().ToString()
                        : "Assign";
                    if (Widgets.ButtonText(btnRect, boardLbl))
                    {
                        cockpit.OpenQuickBoardFloatMenu(host, seatIdx);
                    }
                }
            }
            else
            {
                GUI.color = new Color(0.88f, 0.35f, 0.35f);
                string offlineStr = "GCF_CockpitTab_PartDestroyed".CanTranslate()
                    ? "GCF_CockpitTab_PartDestroyed".Translate().ToString()
                    : "DESTROYED";
                Widgets.Label(occRect, offlineStr);
                GUI.color = Color.white;
            }

            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
