using System.Collections;
using System.Text;
using GiantCreatureFramework.Agent.Patches;
using RimWorld;
using UnityEngine;
using UnityStubs.Gui;
using UnityStubs.Rendering;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework.Agent.UI;

/// <summary>
/// Runtime Creature Forge authoring terminal chat window, gated by GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal.
/// </summary>
public sealed class Dialog_AgentChat : Window
{
    private static Dialog_AgentChat? _activeInstance;
    private static List<AgentSession> Sessions => AgentSessionManager.Sessions;
    private static AgentSession? _currentSession { get => AgentSessionManager.CurrentSession; set => AgentSessionManager.CurrentSession = value; }
    private readonly Dictionary<string, string> _drafts = new();
    private string _lastSessionId = "";
    private readonly record struct LayoutKey(string Content, string Reasoning, bool Streaming, bool Expanded, int Calls, int Completed, int TotalTokens, bool LacksEvidence, string EvidenceNotice, bool HasScreenshot, bool HasPreviewTex);
    private readonly Dictionary<ChatMessage, (LayoutKey Key, float Width, float Height)> _heightCache = new();
    private static readonly Dictionary<string, List<RichBlock>> BlockCache = new();
    private static readonly List<Rect> SharedTermSelectionRects = new();
    private static readonly List<(InlineTermSpan Span, Rect Rect)> SharedTermResolvedSegments = new();

    private const string ComposerControlName = "gcf_chat_composer_input";

    private Vector2 _sessionScrollPos = Vector2.zero;
    private Vector2 _chatScrollPos = Vector2.zero;
    private string _inputBuffer = string.Empty;
    private bool _showConfigDrawer;
    private bool _suppressWindowForScreenshot;
    private Dialog_ConversationContext? _contextChoiceDialog;
    private bool _needsInitialFocus = true;
    private bool _moveComposerCaretToEnd;
    private float _lastKnownContentHeight = 400f;
    private int _lastMessageCount;

    public override Vector2 InitialSize => new(1080f, 750f);
    protected override float Margin => 0f;

    public Dialog_AgentChat()
    {
        draggable = true;
        resizeable = true;
        doCloseX = false;
        doCloseButton = false;
        doWindowBackground = false;
        drawShadow = false;
        closeOnAccept = false;
        closeOnCancel = true;
        forceCatchAcceptAndCancelEventEvenIfUnfocused = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        preventCameraMotion = false;

        MainThreadDispatcher.MarkMainThread();
        AgentSessionManager.RefreshScope();
        EnsureDefaultSession();
    }

    public override void OnAcceptKeyPressed()
    {
    }

    public static bool HasUnreadReply { get; private set; }
    public static bool IsWindowOpen => _activeInstance != null && Find.WindowStack != null && Find.WindowStack.IsOpen(_activeInstance);

    public static void ClearUnreadReply()
    {
        if (!HasUnreadReply)
        {
            return;
        }

        HasUnreadReply = false;
        Patch_MainButtonWorker_DoButton.SyncMainTabIcon(false);
    }

    public static void NotifyReplyCompletedOnMainThread(AgentSession session, ChatMessage assistant)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled)
        {
            return;
        }

        if (IsWindowOpen)
        {
            ClearUnreadReply();
            return;
        }

        if (string.IsNullOrWhiteSpace(assistant.Content))
        {
            return;
        }

        HasUnreadReply = true;
        Patch_MainButtonWorker_DoButton.SyncMainTabIcon(true);
    }

    public static void CloseIfOpen()
    {
        if (_activeInstance != null)
        {
            _activeInstance._contextChoiceDialog?.Close(doCloseSound: false);
            _activeInstance._contextChoiceDialog = null;
            if (Find.WindowStack != null && Find.WindowStack.IsOpen(_activeInstance))
            {
                _activeInstance.Close(doCloseSound: false);
            }
            _activeInstance = null;
        }
    }

    public static void ToggleWindow()
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || Find.WindowStack == null)
        {
            return;
        }

        if (_activeInstance != null && Find.WindowStack.IsOpen(_activeInstance))
        {
            _activeInstance.Close();
            _activeInstance = null;
            return;
        }

        OpenWindow();
    }

    public static void OpenWindow()
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || Find.WindowStack == null)
        {
            return;
        }

        ClearUnreadReply();
        if (_activeInstance != null && Find.WindowStack.IsOpen(_activeInstance))
        {
            Find.WindowStack.TryRemove(_activeInstance, doCloseSound: false);
        }

        _activeInstance = new Dialog_AgentChat();
        Find.WindowStack.Add(_activeInstance);
    }

    public override void PostClose()
    {
        base.PostClose();
        _contextChoiceDialog?.Close();
        _contextChoiceDialog = null;
        _suppressWindowForScreenshot = false;
        AgentSessionManager.QueueSave();
        if (ReferenceEquals(_activeInstance, this))
        {
            _activeInstance = null;
        }
    }

    public override void WindowOnGUI()
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled)
        {
            Close(doCloseSound: false);
            return;
        }

        if (_suppressWindowForScreenshot)
        {
            return;
        }

        base.WindowOnGUI();
    }

    public override void DoWindowContents(Rect inRect)
    {
        MainThreadDispatcher.MarkMainThread();
        ClearUnreadReply();
        EnsureDefaultSession();

        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevColor = GUI.color;
        var prevWordWrap = Text.WordWrap;

        try
        {
            Text.WordWrap = true;

            ModernUiKit.DrawRoundedCard(inRect, ModernUiKit.BgWindow, ModernUiKit.BorderWindow, radius: 12, borderThickness: 1);

            const float sidebarW = 228f;
            var sidebarRect = new Rect(inRect.x + 2f, inRect.y + 2f, sidebarW, inRect.height - 4f);
            var mainRect = new Rect(sidebarRect.xMax + 1f, inRect.y + 2f, inRect.width - sidebarW - 5f, inRect.height - 4f);

            DrawSidebar(sidebarRect);
            ModernUiKit.DrawVerticalHairline(sidebarRect.xMax, inRect.y + 2f, inRect.height - 4f, ModernUiKit.BorderSubtle);
            DrawMainArea(mainRect);

            if (_needsInitialFocus && Event.current != null && Event.current.type == EventType.Repaint)
            {
                _needsInitialFocus = false;
                GUI.FocusWindow(ID);
                GUI.FocusControl(ComposerControlName);
            }

            if (Event.current != null &&
                Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                Event.current.Use();
            }
        }
        finally
        {
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            GUI.color = prevColor;
            Text.WordWrap = prevWordWrap;
        }
    }

    private void DrawSidebar(Rect rect)
    {
        ModernUiKit.DrawRoundedRect(rect, ModernUiKit.BgSidebar, radius: 10);
        var curY = rect.y;

        var brandRect = new Rect(rect.x, curY, rect.width, 54f);
        var logoRect = new Rect(brandRect.x + 14f, brandRect.y + 11f, 32f, 32f);
        ModernUiKit.DrawBrandAvatar(logoRect, interactive: true);

        ModernUiKit.DrawSingleLineLabel(
            new Rect(logoRect.xMax + 10f, brandRect.y + 8f, rect.width - 64f, 22f),
            UiText.Get("GCFAgent_SidebarTitle"),
            GameFont.Small,
            ModernUiKit.TextBright);

        ModernUiKit.DrawSingleLineLabel(
            new Rect(logoRect.xMax + 10f, brandRect.y + 27f, rect.width - 64f, 18f),
            UiText.Get("GCFAgent_SidebarSubtitle"),
            GameFont.Tiny,
            ModernUiKit.TextMuted);

        curY += 54f;
        ModernUiKit.DrawHorizontalHairline(rect.x + 10f, curY, rect.width - 20f, ModernUiKit.BorderSubtle);
        curY += 10f;

        var manualBtnRect = new Rect(rect.x + 12f, curY, rect.width - 24f, 30f);
        if (ModernUiKit.DrawButton(
                manualBtnRect,
                UiText.Get("GCFAgent_ManualButton"),
                ModernButtonVariant.AccentPill,
                radius: 8,
                active: Dialog_ModManual.IsWindowOpen,
                font: GameFont.Small))
        {
            Dialog_ModManual.OpenWindow();
        }
        TooltipHandler.TipRegion(manualBtnRect, UiText.Get("GCFAgent_ManualButtonTip"));
        curY += 38f;

        var newBtnRect = new Rect(rect.x + 12f, curY, rect.width - 24f, 34f);
        if (ModernUiKit.DrawButton(newBtnRect, "+ " + UiText.Get("GCFAgent_SessionNew"), ModernButtonVariant.Primary, radius: 8))
        {
            AgentSessionManager.Create();
            _chatScrollPos = Vector2.zero;
        }
        curY += 46f;

        ModernUiKit.DrawSingleLineLabel(
            new Rect(rect.x + 14f, curY, rect.width - 28f, 18f),
            UiText.Format("GCFAgent_SessionListTitle", Sessions.Count.ToString()),
            GameFont.Tiny,
            ModernUiKit.TextMuted);
        curY += 22f;

        var listRect = new Rect(rect.x + 8f, curY, rect.width - 16f, rect.height - (curY - rect.y) - 12f);
        const float rowH = 42f;
        var viewRect = new Rect(0f, 0f, listRect.width - 14f, Mathf.Max(listRect.height, Sessions.Count * rowH));

        Widgets.BeginScrollView(listRect, ref _sessionScrollPos, viewRect);
        for (var i = 0; i < Sessions.Count; i++)
        {
            var item = Sessions[i];
            var itemRect = new Rect(0f, i * rowH, viewRect.width, 38f);
            var isSelected = ReferenceEquals(item, _currentSession);
            var hovered = Mouse.IsOver(itemRect);

            if (isSelected)
            {
                ModernUiKit.DrawRoundedCard(
                    itemRect,
                    new Color(0.086f, 0.125f, 0.204f, 1f),
                    new Color(0.176f, 0.259f, 0.416f, 1f),
                    radius: 8,
                    borderThickness: 1);
                Widgets.DrawBoxSolid(
                    new Rect(itemRect.x + 4f, itemRect.y + 8f, 3f, itemRect.height - 16f),
                    ModernUiKit.AccentPrimaryHover);
            }
            else if (hovered)
            {
                ModernUiKit.DrawRoundedRect(itemRect, new Color(0.067f, 0.086f, 0.129f, 1f), radius: 8);
            }

            var showDelete = Sessions.Count > 1 && (hovered || isSelected);
            var rightReserved = showDelete ? 30f : 8f;
            var textW = Mathf.Max(40f, itemRect.width - 16f - rightReserved);

            var titleColor = isSelected ? ModernUiKit.TextBright : (hovered ? ModernUiKit.TextPrimary : ModernUiKit.TextSecondary);
            var titleRect = new Rect(itemRect.x + 14f, itemRect.y + 3f, textW, 19f);
            ModernUiKit.DrawSingleLineLabel(titleRect, UiText.Render(item.Title), GameFont.Small, titleColor, TextAnchor.MiddleLeft, UiText.Render(item.Title));

            var subInfo = item.TotalTokens > 0
                ? (item.TotalCachedPromptTokens > 0
                    ? UiText.Format("GCFAgent_SessionItemSubCached", item.Messages.Count.ToString(), TokenEstimator.FormatCompact(item.TotalTokens), item.CacheHitRate.ToString("P0"))
                    : UiText.Format("GCFAgent_SessionItemSubTokens", item.Messages.Count.ToString(), TokenEstimator.FormatCompact(item.TotalTokens)))
                : UiText.Format("GCFAgent_SessionItemSubMessages", item.Messages.Count.ToString());
            var subRect = new Rect(itemRect.x + 14f, itemRect.y + 20f, textW, 15f);
            ModernUiKit.DrawSingleLineLabel(subRect, subInfo, GameFont.Tiny, ModernUiKit.TextMuted);

            var clickRect = new Rect(itemRect.x, itemRect.y, itemRect.width - rightReserved, itemRect.height);
            if (Widgets.ButtonInvisible(clickRect))
            {
                _currentSession = item;
            }

            if (showDelete)
            {
                var delRect = new Rect(itemRect.xMax - 26f, itemRect.y + 9f, 20f, 20f);
                var delHovered = Mouse.IsOver(delRect);
                if (delHovered)
                {
                    ModernUiKit.DrawRoundedRect(delRect, new Color(0.32f, 0.10f, 0.12f, 1f), radius: 5);
                }
                ModernUiKit.DrawSingleLineLabel(
                    delRect,
                    "×",
                    GameFont.Tiny,
                    delHovered ? new Color(0.98f, 0.45f, 0.45f, 1f) : ModernUiKit.TextMuted,
                    TextAnchor.MiddleCenter,
                    UiText.Get("GCFAgent_SessionDeleteTip"));
                if (Widgets.ButtonInvisible(delRect))
                {
                    AgentSessionManager.Delete(item);
                    if (isSelected)
                    {
                        _currentSession = Sessions[0];
                    }
                    break;
                }
            }
        }
        Widgets.EndScrollView();
    }

    private void DrawMainArea(Rect rect)
    {
        var curY = rect.y;
        var headerRect = new Rect(rect.x, curY, rect.width, 54f);

        var session = _currentSession!;
        if (_lastSessionId != session.Id)
        {
            if (_lastSessionId.Length > 0) _drafts[_lastSessionId] = _inputBuffer;
            _inputBuffer = _drafts.TryGetValue(session.Id, out var draft) ? draft : "";
            _lastSessionId = session.Id;
            _heightCache.Clear();
            _lastMessageCount = -1;
            _chatScrollPos = Vector2.zero;
        }

        var rightX = headerRect.xMax - 14f;

        var closeRect = new Rect(rightX - 28f, headerRect.y + 13f, 28f, 28f);
        if (ModernUiKit.DrawCloseIconButton(closeRect, radius: 7))
        {
            Close();
            return;
        }
        rightX = closeRect.x - 8f;

        var cfgBtnRect = new Rect(rightX - 88f, headerRect.y + 13f, 88f, 28f);
        if (ModernUiKit.DrawButton(cfgBtnRect, UiText.Get("GCFAgent_HeaderModelConfig"), ModernButtonVariant.Pill, radius: 7, active: _showConfigDrawer, font: GameFont.Tiny))
        {
            _showConfigDrawer = !_showConfigDrawer;
        }
        rightX = cfgBtnRect.x - 8f;

        var settings = GiantCreatureAgentMod.Settings;
        settings.EnsureApiConfigsInitialized();
        var activeCfg = settings.GetEffectiveActiveConfig();
        var requestIdentity = ModelIdentityDisplay.CurrentRequest(session);
        var replyIdentity = requestIdentity ?? ModelIdentityDisplay.LastReply(session);
        var isConfigured = replyIdentity != null || (activeCfg != null && activeCfg.IsMinimallyValid());
        var modelTag = replyIdentity != null
            ? UiText.Format("GCFAgent_ModelTagWithIdentity", session.IsBusy ? UiText.Get("GCFAgent_ModelIdentityCurrent") : UiText.Get("GCFAgent_ModelIdentityLast"), replyIdentity.Label)
            : isConfigured
            ? UiText.Format("GCFAgent_ModelTagStandby", activeCfg!.Provider.ShortLabel(), activeCfg.GetResolvedModel())
            : UiText.Get("GCFAgent_ModelTagUnconfigured");

        var prevWrap = Text.WordWrap;
        Text.Font = GameFont.Tiny;
        Text.WordWrap = false;
        var desiredModelPillW = Mathf.Clamp(Text.CalcSize(modelTag).x + 30f, 108f, 196f);
        var modelPillRect = new Rect(rightX - desiredModelPillW, headerRect.y + 13f, desiredModelPillW, 28f);
        if (DrawModelBadgeButton(modelPillRect, modelTag, isConfigured))
        {
            OpenQuickProviderModelSwitcher(settings);
        }
        TooltipHandler.TipRegion(modelPillRect, replyIdentity?.Details ?? UiText.Get("GCFAgent_ModelTagNoRequestTip"));
        rightX = modelPillRect.x - 8f;

        session.RecalculateTokenTotals();
        var tokenText = session.TotalPromptTokens > 0
            ? UiText.Format("GCFAgent_TokenPillCached", TokenEstimator.FormatCompact(session.TotalTokens), session.CacheHitRate.ToString("P0"))
            : UiText.Format("GCFAgent_TokenPillPlain", TokenEstimator.FormatCompact(session.TotalTokens));
        var tokenPillW = Mathf.Clamp(Text.CalcSize(tokenText).x + 22f, 88f, 198f);
        Text.WordWrap = prevWrap;

        var tokenPillRect = new Rect(rightX - tokenPillW, headerRect.y + 13f, tokenPillW, 28f);
        if (ModernUiKit.DrawButton(
            tokenPillRect,
            tokenText,
            ModernButtonVariant.Pill,
            radius: 7,
            active: _showConfigDrawer && ConfigPanelDrawer.ActiveTabIndex == 1,
            font: GameFont.Tiny))
        {
            if (_showConfigDrawer && ConfigPanelDrawer.ActiveTabIndex == 1)
            {
                _showConfigDrawer = false;
            }
            else
            {
                _showConfigDrawer = true;
                ConfigPanelDrawer.ActiveTabIndex = 1;
            }
        }
        TooltipHandler.TipRegion(
            tokenPillRect,
            UiText.Format("GCFAgent_TokenPillTooltip",
                session.TotalTokens.ToString("N0"),
                session.TotalPromptTokens.ToString("N0"),
                session.TotalCachedPromptTokens.ToString("N0"),
                session.CacheHitRate.ToString("P1"),
                session.TotalUncachedPromptTokens.ToString("N0"),
                session.PeakContextTokens.ToString("N0"),
                session.TotalCompletionTokens.ToString("N0")));
        rightX = tokenPillRect.x - 10f;

        var leftStartX = headerRect.x + 16f;
        var maxLeftAvailableW = Mathf.Max(120f, rightX - leftStartX);
        var statusText = string.IsNullOrWhiteSpace(session.StatusText) ? UiText.Get(UiText.KeySessionStatusReady) : UiText.Render(session.StatusText);
        var statusIsError = !string.IsNullOrWhiteSpace(session.StatusText) && session.StatusIsError;

        Text.Font = GameFont.Tiny;
        Text.WordWrap = false;
        var rawStatusW = Text.CalcSize(statusText).x + 30f;
        Text.WordWrap = prevWrap;

        var maxStatusW = Mathf.Clamp(maxLeftAvailableW * 0.52f, 68f, 210f);
        var statusPillW = Mathf.Clamp(rawStatusW, 64f, maxStatusW);
        var maxTitleW = Mathf.Max(54f, maxLeftAvailableW - statusPillW - 10f);

        Text.Font = GameFont.Small;
        Text.WordWrap = false;
        var rawTitleW = Text.CalcSize(UiText.Render(session.Title)).x + 8f;
        Text.WordWrap = prevWrap;
        var titleW = Mathf.Min(maxTitleW, Mathf.Max(54f, rawTitleW));

        var titleRect = new Rect(leftStartX, headerRect.y, titleW, headerRect.height);
        ModernUiKit.DrawSingleLineLabel(titleRect, UiText.Render(session.Title), GameFont.Small, ModernUiKit.TextBright, TextAnchor.MiddleLeft, UiText.Render(session.Title));

        var statusPillRect = new Rect(titleRect.xMax + 8f, headerRect.y + 14f, statusPillW, 26f);
        DrawRuntimeStatusBadge(statusPillRect, statusText, session.IsBusy, statusIsError);

        curY += 54f;
        ModernUiKit.DrawHorizontalHairline(rect.x, curY, rect.width, ModernUiKit.BorderSubtle);
        curY += 1f;

        var composerInputW = Mathf.Max(60f, rect.width - 36f - 28f);
        var inputH = CalculateComposerInputHeight(_inputBuffer, composerInputW);
        var pillH = inputH + 44f;
        var composerAreaHeight = pillH + 18f;

        var contentAreaRect = new Rect(rect.x, curY, rect.width, Mathf.Max(60f, rect.height - (curY - rect.y) - composerAreaHeight));

        if (_showConfigDrawer)
        {
            DrawInlineConfigDrawer(contentAreaRect, session);
        }
        else
        {
            DrawMessageStream(contentAreaRect, session);
        }

        var composerRect = new Rect(rect.x, rect.yMax - composerAreaHeight, rect.width, composerAreaHeight);
        DrawComposer(composerRect, session, inputH, pillH);
    }

    private static void DrawRuntimeStatusBadge(Rect rect, string statusText, bool isBusy, bool isErr)
    {
        var fill = isBusy
            ? new Color(0.180f, 0.125f, 0.039f, 1f)
            : (isErr ? new Color(0.200f, 0.067f, 0.082f, 1f) : new Color(0.039f, 0.145f, 0.110f, 1f));
        var border = isBusy
            ? new Color(0.455f, 0.298f, 0.071f, 1f)
            : (isErr ? new Color(0.520f, 0.145f, 0.165f, 1f) : new Color(0.078f, 0.325f, 0.235f, 1f));
        var dotColor = isBusy ? ModernUiKit.AccentAmber : (isErr ? ModernUiKit.AccentDanger : ModernUiKit.AccentEmerald);
        var textColor = isBusy
            ? new Color(0.988f, 0.827f, 0.302f, 1f)
            : (isErr ? new Color(0.992f, 0.650f, 0.650f, 1f) : new Color(0.431f, 0.906f, 0.718f, 1f));

        ModernUiKit.DrawRoundedCard(rect, fill, border, radius: 10, borderThickness: 1);
        ModernUiKit.DrawCircle(new Rect(rect.x + 8f, rect.y + (rect.height - 8f) * 0.5f, 8f, 8f), dotColor);

        var labelRect = new Rect(rect.x + 20f, rect.y, Mathf.Max(16f, rect.width - 24f), rect.height);
        ModernUiKit.DrawSingleLineLabel(labelRect, statusText, GameFont.Tiny, textColor, TextAnchor.MiddleLeft, statusText);
    }

    private static bool DrawModelBadgeButton(Rect rect, string modelTag, bool isConfigured)
    {
        var hovered = Mouse.IsOver(rect);
        var fill = hovered ? new Color(0.094f, 0.129f, 0.204f, 1f) : new Color(0.067f, 0.094f, 0.149f, 1f);
        var border = isConfigured
            ? (hovered ? ModernUiKit.AccentPrimaryHover : new Color(0.153f, 0.247f, 0.408f, 1f))
            : new Color(0.420f, 0.275f, 0.082f, 1f);
        var dotColor = isConfigured ? ModernUiKit.AccentCyan : ModernUiKit.AccentAmber;

        ModernUiKit.DrawRoundedCard(rect, fill, border, radius: 10, borderThickness: 1);
        ModernUiKit.DrawCircle(new Rect(rect.x + 9f, rect.y + (rect.height - 8f) * 0.5f, 8f, 8f), dotColor);

        var labelRect = new Rect(rect.x + 21f, rect.y, Mathf.Max(20f, rect.width - 25f), rect.height);
        ModernUiKit.DrawSingleLineLabel(
            labelRect,
            modelTag,
            GameFont.Tiny,
            hovered ? ModernUiKit.TextBright : ModernUiKit.TextPrimary,
            TextAnchor.MiddleLeft,
            UiText.Get("GCFAgent_ModelBadgeTip"));

        return Widgets.ButtonInvisible(rect);
    }

    private void OpenQuickProviderModelSwitcher(AgentSettings settings)
    {
        var options = new List<FloatMenuOption>();

        for (var i = 0; i < settings.ApiConfigs.Count; i++)
        {
            var idx = i;
            var cfg = settings.ApiConfigs[i];
            var tag = UiText.Format("GCFAgent_SwitcherConfigTag", (idx + 1).ToString(), cfg.Provider.Label(), cfg.GetResolvedModel(),
                cfg.IsEnabled ? "" : " (" + UiText.Get("GCFAgent_NotEnabled") + ")");
            options.Add(new FloatMenuOption(tag, () =>
            {
                cfg.IsEnabled = true;
                if (idx > 0)
                {
                    settings.ApiConfigs.RemoveAt(idx);
                    settings.ApiConfigs.Insert(0, cfg);
                }
                settings.SyncActiveConfigToPrimaryFields();
                settings.SaveToUserConfig();
            }));
        }

        var active = settings.GetEffectiveActiveConfig();
        if (active != null && active.Provider != AIProvider.Player2)
        {
            var fetchedModels = AIProviderRegistry.GetCachedModels(active.Provider, active.CustomUrl);
            foreach (var modelId in fetchedModels.Take(12))
            {
                var m = modelId;
                options.Add(new FloatMenuOption(UiText.Format("GCFAgent_SwitcherFetchedModel", m), () =>
                {
                    active.SelectedModel = m;
                    active.CustomModelName = m;
                    settings.SyncActiveConfigToPrimaryFields();
                    settings.SaveToUserConfig();
                }));
            }
        }

        options.Add(new FloatMenuOption(UiText.Get("GCFAgent_SwitcherOpenConfig"), () =>
        {
            _showConfigDrawer = true;
            ConfigPanelDrawer.ActiveTabIndex = 0;
        }));

        Find.WindowStack?.Add(new FloatMenu(options));
    }

    private void DrawInlineConfigDrawer(Rect rect, AgentSession session)
    {
        Widgets.DrawBoxSolid(rect, ModernUiKit.BgDrawer);
        var inner = new Rect(rect.x + 12f, rect.y + 8f, rect.width - 24f, rect.height - 16f);
        ConfigPanelDrawer.DrawConfigPanel(inner, GiantCreatureAgentMod.Settings, session, () => _showConfigDrawer = false);
    }

    private void DrawMessageStream(Rect rect, AgentSession session)
    {
        List<ChatMessage> snapshot;
        lock (session.SyncRoot)
        {
            snapshot = session.Messages.ToList();
        }

        var innerWidth = Mathf.Max(160f, rect.width - 36f);
        var totalHeight = 24f;

        foreach (var msg in snapshot)
        {
            totalHeight += MeasureMessageHeight(msg, innerWidth, session.SyncRoot) + 16f;
        }

        var followBottom = _chatScrollPos.y >= _lastKnownContentHeight - rect.height - 36f;
        _lastKnownContentHeight = Mathf.Max(rect.height - 2f, totalHeight);
        if (followBottom) _chatScrollPos.y = Mathf.Max(0f, _lastKnownContentHeight - rect.height);
        var viewRect = new Rect(0f, 0f, rect.width - 16f, _lastKnownContentHeight);

        if (snapshot.Count != _lastMessageCount)
        {
            _lastMessageCount = snapshot.Count;
            _chatScrollPos.y = Mathf.Max(0f, _lastKnownContentHeight - rect.height);
        }

        Widgets.BeginScrollView(rect, ref _chatScrollPos, viewRect);
        var curY = 14f;

        foreach (var msg in snapshot)
        {
            var msgH = MeasureMessageHeight(msg, innerWidth, session.SyncRoot);
            if (curY + msgH >= _chatScrollPos.y && curY <= _chatScrollPos.y + rect.height)
                DrawSingleMessage(new Rect(16f, curY, innerWidth, 0f), msg, session.SyncRoot);
            curY += msgH + 16f;
        }

        Widgets.EndScrollView();
    }

    private float MeasureMessageHeight(ChatMessage msg, float width, object syncRoot)
    {
        lock (syncRoot)
        {
            var key = new LayoutKey(
                msg.Content,
                msg.ReasoningContent,
                msg.IsStreaming,
                msg.ShowEvidence,
                msg.ToolCalls.Count,
                msg.ToolCalls.Count(t => t.IsCompleted),
                msg.TotalTokens,
                msg.LacksRuntimeEvidence,
                msg.EvidenceNotice,
                msg.HasScreenshot,
                msg.PreviewTexture is Texture2D);
            if (_heightCache.TryGetValue(msg, out var cached) && cached.Key == key && Mathf.Abs(cached.Width - width) < 0.5f)
                return cached.Height;
            var height = CalculateMessageHeight(msg, width, syncRoot);
            if (_heightCache.Count > 80) _heightCache.Clear();
            _heightCache[msg] = (key, width, height);
            return height;
        }
    }

    private float CalculateMessageHeight(ChatMessage msg, float width, object syncRoot)
    {
        var prevFont = Text.Font;
        var prevWrap = Text.WordWrap;
        Text.WordWrap = true;
        try
        {
            if (msg.Role == ChatRole.User)
            {
                var safeUserText = EscapeRichText(msg.Content);
                var userBubbleW = MeasureUserBubbleWidth(safeUserText, width, msg.HasScreenshot);
                var innerW = Mathf.Max(40f, userBubbleW - 32f);
                Text.Font = GameFont.Small;
                var txtH = Text.CalcHeight(safeUserText, innerW);
                var screenshotExtraH = msg.HasScreenshot
                    ? (msg.PreviewTexture is Texture2D ? 156f : 26f)
                    : 0f;
                return Mathf.Max(38f, txtH + 20f + screenshotExtraH);
            }

            var contentW = Mathf.Max(100f, width - 44f);
            var h = 30f;

            lock (syncRoot)
            {
                if (HasFirstLineEvidenceNotice(msg))
                {
                    h += 30f;
                }

                if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
                {
                    Text.Font = GameFont.Tiny;
                    var rText = EscapeRichText(TruncateForUi(msg.ReasoningContent, 900));
                    h += Text.CalcHeight(rText, contentW - 18f) + 16f;
                }

                var body = string.IsNullOrWhiteSpace(msg.Content)
                    ? (msg.IsStreaming ? "..." : string.Empty)
                    : msg.Content;

                if (!string.IsNullOrEmpty(body))
                {
                    h += MeasureFormattedBodyHeight(body, contentW);
                }

                if (msg.ToolCalls.Count > 0)
                {
                    h += 32f;
                    if (msg.ShowEvidence)
                    {
                        Text.Font = GameFont.Tiny;
                        var evInnerW = Mathf.Max(60f, contentW - 20f);
                        foreach (var call in msg.ToolCalls)
                        {
                            var preview = EvidencePreview(call);
                            var cardH = Text.CalcHeight(preview, evInnerW) + 14f;
                            h += cardH + 6f;
                        }
                    }
                }

                if (!msg.IsStreaming && msg.TotalTokens > 0)
                {
                    h += 28f;
                }
            }

            return h + 8f;
        }
        finally
        {
            Text.Font = prevFont;
            Text.WordWrap = prevWrap;
        }
    }

    private static float MeasureUserBubbleWidth(string safeContent, float maxAreaWidth, bool hasScreenshot = false)
    {
        var prevFont = Text.Font;
        var prevWrap = Text.WordWrap;
        Text.Font = GameFont.Small;
        Text.WordWrap = false;
        try
        {
            var lines = (safeContent ?? string.Empty).Split('\n');
            var maxLineW = 0f;
            foreach (var line in lines)
            {
                var w = Text.CalcSize(line).x;
                if (w > maxLineW) maxLineW = w;
            }
            var rawW = maxLineW + 44f;
            var minW = hasScreenshot ? 268f : 88f;
            return Mathf.Clamp(rawW, minW, maxAreaWidth * 0.76f);
        }
        finally
        {
            Text.Font = prevFont;
            Text.WordWrap = prevWrap;
        }
    }

    private float DrawSingleMessage(Rect area, ChatMessage msg, object syncRoot)
    {
        if (msg.Role == ChatRole.User)
        {
            return DrawUserBubble(area, msg);
        }

        return DrawAssistantCard(area, msg, syncRoot);
    }

    private float DrawUserBubble(Rect area, ChatMessage msg)
    {
        var safeContent = EscapeRichText(msg.Content);
        var bubbleW = MeasureUserBubbleWidth(safeContent, area.width, msg.HasScreenshot);
        var innerW = Mathf.Max(40f, bubbleW - 32f);
        Text.Font = GameFont.Small;
        Text.WordWrap = true;
        var txtH = Text.CalcHeight(safeContent, innerW);
        var screenshotExtraH = msg.HasScreenshot
            ? (msg.PreviewTexture is Texture2D ? 156f : 26f)
            : 0f;
        var totalH = Mathf.Max(38f, txtH + 20f + screenshotExtraH);

        var bubbleRect = new Rect(area.xMax - bubbleW, area.y, bubbleW, totalH);
        ModernUiKit.DrawRoundedCard(
            bubbleRect,
            ModernUiKit.BgBubbleUser,
            ModernUiKit.BorderUserBubble,
            radius: 12,
            borderThickness: 1);

        var curY = bubbleRect.y + 10f;
        if (msg.HasScreenshot)
        {
            var tagRect = new Rect(bubbleRect.x + 16f, curY, innerW, 20f);
            ModernUiKit.DrawSingleLineLabel(tagRect, UiText.Get("GCFAgent_ScreenshotTag"), GameFont.Tiny, ModernUiKit.AccentCyan);
            curY += 22f;

            if (msg.PreviewTexture is Texture2D previewTex && previewTex != null)
            {
                var thumbRect = new Rect(bubbleRect.x + 16f, curY, innerW, 126f);
                GUI.DrawTexture(thumbRect, previewTex, ScaleMode.ScaleAndCrop, true);
                ModernUiKit.DrawRoundedOutline(thumbRect, ModernUiKit.BorderSubtle, radius: 6, thickness: 1);
                curY += 134f;
            }
            else
            {
                curY += 4f;
            }
        }

        Text.Font = GameFont.Small;
        Text.Anchor = !msg.HasScreenshot && txtH <= 26f ? TextAnchor.MiddleLeft : TextAnchor.UpperLeft;
        Text.WordWrap = true;
        GUI.color = ModernUiKit.TextBright;
        Widgets.Label(new Rect(bubbleRect.x + 16f, curY, innerW, Mathf.Max(22f, txtH + 4f)), safeContent);
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;

        return totalH;
    }

    private float DrawAssistantCard(Rect area, ChatMessage msg, object syncRoot)
    {
        var totalH = MeasureMessageHeight(msg, area.width, syncRoot);
        var avatarRect = new Rect(area.x, area.y + 2f, 28f, 28f);
        ModernUiKit.DrawBrandAvatar(avatarRect, interactive: true);

        var contentX = avatarRect.xMax + 14f;
        var contentW = Mathf.Max(100f, area.width - 44f);
        var headerY = area.y;

        ModernUiKit.DrawSingleLineLabel(
            new Rect(contentX, headerY, 96f, 24f),
            UiText.Get("GCFAgent_SidebarTitle"),
            GameFont.Tiny,
            ModernUiKit.TextBright, tooltip: msg.ModelIdentity?.Details);

        ModernUiKit.DrawSingleLineLabel(
            new Rect(contentX + 100f, headerY, 68f, 24f),
            msg.Timestamp.ToString("HH:mm:ss"),
            GameFont.Tiny,
            ModernUiKit.TextMuted);

        var copyRect = new Rect(area.xMax - 52f, headerY + 2f, 48f, 20f);
        if (ModernUiKit.DrawButton(copyRect, UiText.Get("GCFAgent_CommonCopy"), ModernButtonVariant.Pill, radius: 6, font: GameFont.Tiny))
        {
            GUIUtility.systemCopyBuffer = TermTagFormatter.StripTermTags(msg.Content ?? string.Empty);
        }

        if (msg.TotalTokens > 0)
        {
            var cachePart = msg.PromptTokens > 0 ? " " + UiText.Format("GCFAgent_TokLabelCache", msg.CacheHitRate.ToString("P0")) : "";
            var roundsPart = msg.LlmRounds > 1 ? " " + UiText.Format("GCFAgent_TokLabelRounds", msg.LlmRounds.ToString()) : "";
            var tokLabel = "◇ " + UiText.Format("GCFAgent_TokLabel", msg.IsEstimatedTokens ? "≈" : "", TokenEstimator.FormatCompact(msg.TotalTokens), TokenEstimator.FormatCompact(msg.UncachedPromptTokens), cachePart, roundsPart);
            var tokW = Mathf.Min(320f, Mathf.Max(60f, copyRect.x - (contentX + 172f) - 8f));
            if (tokW >= 70f)
            {
                var peakCtx = msg.PeakContextTokens > 0 ? msg.PeakContextTokens : msg.PromptTokens;
                ModernUiKit.DrawSingleLineLabel(
                    new Rect(copyRect.x - tokW - 6f, headerY, tokW, 24f),
                    tokLabel,
                    GameFont.Tiny,
                    ModernUiKit.AccentCyan,
                    TextAnchor.MiddleRight,
                    UiText.Format("GCFAgent_TokLabelTooltip",
                        msg.TotalTokens.ToString("N0"),
                        msg.LlmRounds > 0 ? " (" + UiText.Format("GCFAgent_TokLabelTotalRounds", msg.LlmRounds.ToString()) + ")" : "",
                        msg.PromptTokens.ToString("N0"),
                        msg.CachedPromptTokens.ToString("N0"),
                        msg.CacheHitRate.ToString("P1"),
                        msg.UncachedPromptTokens.ToString("N0"),
                        msg.CompletionTokens.ToString("N0"),
                        peakCtx.ToString("N0")));
            }
        }

        var curY = headerY + 28f;

        lock (syncRoot)
        {
            if (HasFirstLineEvidenceNotice(msg))
            {
                var bannerRect = new Rect(contentX, curY, contentW, 24f);
                ModernUiKit.DrawRoundedCard(
                    bannerRect,
                    new Color(0.165f, 0.118f, 0.043f, 0.95f),
                    new Color(0.420f, 0.286f, 0.078f, 1f),
                    radius: 6,
                    borderThickness: 1);
                var noticeText = !string.IsNullOrWhiteSpace(msg.EvidenceNotice)
                    ? msg.EvidenceNotice
                    : UiText.Get("GCFAgent_EvidenceNoticeFallback");
                ModernUiKit.DrawSingleLineLabel(
                    new Rect(bannerRect.x + 10f, bannerRect.y + 1f, bannerRect.width - 20f, 22f),
                    "◇ " + noticeText,
                    GameFont.Tiny,
                    new Color(0.988f, 0.827f, 0.345f, 1f),
                    TextAnchor.MiddleLeft,
                    noticeText);
                curY += 30f;
            }

            if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
            {
                Text.Font = GameFont.Tiny;
                Text.WordWrap = true;
                var rText = EscapeRichText(TruncateForUi(msg.ReasoningContent, 900));
                var rHeight = Text.CalcHeight(rText, contentW - 18f) + 12f;
                Widgets.DrawBoxSolid(
                    new Rect(contentX, curY + 2f, 2f, Mathf.Max(4f, rHeight - 4f)),
                    ModernUiKit.BorderCard);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = ModernUiKit.TextMuted;
                Widgets.Label(new Rect(contentX + 12f, curY + 4f, contentW - 18f, rHeight - 6f), rText);
                curY += rHeight + 4f;
            }

            var body = string.IsNullOrWhiteSpace(msg.Content)
                ? (msg.IsStreaming ? "..." : string.Empty)
                : msg.Content;

            if (!string.IsNullOrEmpty(body))
            {
                curY += DrawFormattedBody(new Rect(contentX, curY, contentW, 0f), body, msg.IsError);
            }

            if (msg.ToolCalls.Count > 0)
            {
                if (ModernUiKit.DrawButton(new Rect(contentX, curY + 2f, 136f, 26f), UiText.Format("GCFAgent_EvidenceToggle", msg.ToolCalls.Count.ToString()), ModernButtonVariant.Pill, active: msg.ShowEvidence, font: GameFont.Tiny))
                    msg.ShowEvidence = !msg.ShowEvidence;
                if (ModernUiKit.DrawButton(new Rect(contentX + 144f, curY + 2f, 116f, 26f), UiText.Get("GCFAgent_EvidenceCopyAll"), ModernButtonVariant.Pill, font: GameFont.Tiny))
                    GUIUtility.systemCopyBuffer = System.Text.Json.JsonSerializer.Serialize(
                        msg.ToolCalls,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                curY += 32f;

                if (msg.ShowEvidence)
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.WordWrap = true;
                    var evInnerW = Mathf.Max(60f, contentW - 20f);

                    foreach (var call in msg.ToolCalls)
                    {
                        var preview = EvidencePreview(call);
                        var textH = Text.CalcHeight(preview, evInnerW);
                        var cardH = textH + 14f;
                        var evCardRect = new Rect(contentX, curY, contentW, cardH);

                        ModernUiKit.DrawRoundedCard(
                            evCardRect,
                            ModernUiKit.BgCodeBody,
                            call.IsError ? new Color(0.45f, 0.16f, 0.18f, 1f) : ModernUiKit.BorderSubtle,
                            radius: 6,
                            borderThickness: 1);

                        GUI.color = call.IsError ? new Color(0.98f, 0.55f, 0.55f, 1f) : ModernUiKit.TextSecondary;
                        Widgets.Label(new Rect(evCardRect.x + 10f, evCardRect.y + 7f, evInnerW, textH + 2f), preview);
                        curY += cardH + 6f;
                    }
                }
            }

            if (!msg.IsStreaming && msg.TotalTokens > 0)
            {
                var reportBarRect = new Rect(contentX, curY + 4f, contentW, 24f);
                ModernUiKit.DrawRoundedCard(
                    reportBarRect,
                    ModernUiKit.BgTableRowEven,
                    ModernUiKit.BorderSubtle,
                    radius: 6,
                    borderThickness: 1);

                var approxPrefix = msg.IsEstimatedTokens ? "≈" : "";
                var sessionTotal = _currentSession?.TotalTokens ?? msg.TotalTokens;
                var sessionCacheRate = _currentSession?.CacheHitRate ?? msg.CacheHitRate;
                var peakCtx = msg.PeakContextTokens > 0 ? msg.PeakContextTokens : msg.PromptTokens;
                var roundsTag = msg.LlmRounds > 0 ? UiText.Format("GCFAgent_ReportRoundsTag", msg.LlmRounds.ToString()) + " " : "";
                var detailSegment = msg.CachedPromptTokens > 0
                    ? UiText.Format("GCFAgent_ReportDetailCached",
                        msg.UncachedPromptTokens.ToString("N0"), msg.CachedPromptTokens.ToString("N0"), msg.CacheHitRate.ToString("P1"), msg.CompletionTokens.ToString("N0"))
                    : UiText.Format("GCFAgent_ReportDetailPlain",
                        msg.PromptTokens.ToString("N0"), msg.CompletionTokens.ToString("N0"));
                var reportText =
                    UiText.Format("GCFAgent_ReportLine", roundsTag, peakCtx.ToString("N0"), approxPrefix, msg.TotalTokens.ToString("N0"), detailSegment, sessionTotal.ToString("N0"), sessionCacheRate.ToString("P1"));
                ModernUiKit.DrawSingleLineLabel(
                    new Rect(reportBarRect.x + 10f, reportBarRect.y + 1f, reportBarRect.width - 20f, 22f),
                    reportText,
                    GameFont.Tiny,
                    ModernUiKit.AccentCyan,
                    TextAnchor.MiddleLeft,
                    reportText);
                curY += 28f;
            }
        }

        GUI.color = Color.white;
        return totalH;
    }

    private static bool HasFirstLineEvidenceNotice(ChatMessage msg)
    {
        if (msg.IsStreaming || msg.IsError)
        {
            return false;
        }
        return msg.LacksRuntimeEvidence || (string.IsNullOrEmpty(msg.AnswerKind) && msg.ToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(msg.Content));
    }

    private static string EvidencePreview(ToolCallRecord call)
    {
        var status = !call.IsCompleted ? UiText.Get("GCFAgent_ToolStatusRunning") : call.IsError ? UiText.Get("GCFAgent_ToolStatusFailed") : UiText.Get("GCFAgent_ToolStatusDone");
        var rawPreview = call.ResultJson.Length > 380
            ? call.ResultJson[..380] + "…" + UiText.Get("GCFAgent_EvidencePreviewHint")
            : call.ResultJson;
        var breakablePreview = rawPreview
            .Replace("\",\"", "\", \"")
            .Replace("\":", "\": ")
            .Replace("},{", "}, {");
        return EscapeRichText(UiText.Format("GCFAgent_EvidencePreviewHeader", call.DisplayTitle, status, call.DurationMs.ToString(), breakablePreview));
    }

    private float MeasureFormattedBodyHeight(string body, float width)
    {
        var blocks = ParseRichBlocks(body);
        var h = 0f;
        Text.WordWrap = true;

        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Code:
                {
                    Text.Font = GameFont.Tiny;
                    var safeCode = EscapeRichText(b.Text);
                    var bodyH = Text.CalcHeight(safeCode, Mathf.Max(40f, width - 24f)) + 16f;
                    var totalCodeH = 28f + bodyH;
                    h += totalCodeH + 6f;
                    break;
                }

                case BlockKind.Divider:
                    h += 14f;
                    break;

                case BlockKind.Heading:
                {
                    Text.Font = GameFont.Small;
                    var rendered = TermTagFormatter.FormatInlineMarkdown(b.Text, wrapBold: true);
                    var txtH = Text.CalcHeight(rendered, Mathf.Max(40f, width - 14f));
                    h += txtH + 10f;
                    break;
                }

                case BlockKind.Table:
                    h += MeasureTableHeight(b.TableRows!, width) + 10f;
                    break;

                default:
                {
                    Text.Font = GameFont.Small;
                    var rendered = TermTagFormatter.FormatInlineMarkdown(b.Text);
                    h += Text.CalcHeight(rendered, Mathf.Max(40f, width)) + 8f;
                    break;
                }
            }
        }
        return h;
    }

    private float DrawFormattedBody(Rect startRect, string body, bool isError)
    {
        var blocks = ParseRichBlocks(body);
        var curY = startRect.y;
        Text.WordWrap = true;

        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Code:
                {
                    Text.Font = GameFont.Tiny;
                    var safeCode = EscapeRichText(b.Text);
                    var codeInnerW = Mathf.Max(40f, startRect.width - 24f);
                    var bodyH = Text.CalcHeight(safeCode, codeInnerW) + 16f;
                    var totalCodeH = 28f + bodyH;
                    var boxRect = new Rect(startRect.x, curY, startRect.width, totalCodeH);

                    ModernUiKit.DrawRoundedCard(
                        boxRect,
                        ModernUiKit.BgCodeBody,
                        ModernUiKit.BorderCard,
                        radius: 8,
                        borderThickness: 1);

                    var codeHeaderRect = new Rect(boxRect.x + 1f, boxRect.y + 1f, boxRect.width - 2f, 26f);
                    ModernUiKit.DrawRoundedRect(codeHeaderRect, ModernUiKit.BgCodeHeader, radius: 7);
                    ModernUiKit.DrawHorizontalHairline(boxRect.x + 1f, codeHeaderRect.yMax, boxRect.width - 2f, ModernUiKit.BorderSubtle);

                    var langLabel = string.IsNullOrWhiteSpace(b.Language) ? "XML / C#" : b.Language.ToUpperInvariant();
                    ModernUiKit.DrawSingleLineLabel(
                        new Rect(codeHeaderRect.x + 12f, codeHeaderRect.y, Mathf.Max(60f, codeHeaderRect.width - 84f), 26f),
                        langLabel,
                        GameFont.Tiny,
                        ModernUiKit.AccentCyan);

                    var copyBtn = new Rect(codeHeaderRect.xMax - 64f, codeHeaderRect.y + 3f, 56f, 20f);
                    if (ModernUiKit.DrawButton(copyBtn, UiText.Get("GCFAgent_CommonCopyCode"), ModernButtonVariant.Pill, radius: 5, font: GameFont.Tiny))
                    {
                        GUIUtility.systemCopyBuffer = b.Text;
                    }

                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.WordWrap = true;
                    GUI.color = new Color(0.820f, 0.894f, 0.980f, 1f);
                    Widgets.Label(new Rect(boxRect.x + 12f, codeHeaderRect.yMax + 8f, codeInnerW, bodyH - 8f), safeCode);

                    curY += totalCodeH + 6f;
                    break;
                }

                case BlockKind.Divider:
                {
                    ModernUiKit.DrawHorizontalHairline(startRect.x, curY + 6f, startRect.width, ModernUiKit.BorderCard);
                    curY += 14f;
                    break;
                }

                case BlockKind.Heading:
                {
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.WordWrap = true;
                    var rendered = TermTagFormatter.FormatInlineMarkdown(b.Text, out var termSpans, wrapBold: true);
                    var headW = Mathf.Max(40f, startRect.width - 14f);
                    var txtH = Text.CalcHeight(rendered, headW);

                    Widgets.DrawBoxSolid(
                        new Rect(startRect.x, curY + 3f, 3f, Mathf.Max(14f, txtH - 4f)),
                        ModernUiKit.AccentPrimaryHover);

                    var headColor = b.HeadingLevel <= 2 ? ModernUiKit.AccentCyan : ModernUiKit.TextBright;
                    DrawFormattedLabelWithTerms(new Rect(startRect.x + 10f, curY, headW, txtH + 2f), rendered, termSpans, headColor);
                    curY += txtH + 10f;
                    break;
                }

                case BlockKind.Table:
                {
                    var tableH = DrawMarkdownTable(new Rect(startRect.x, curY, startRect.width, 0f), b.TableRows!);
                    curY += tableH + 10f;
                    break;
                }

                default:
                {
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.WordWrap = true;
                    var paraColor = isError ? new Color(0.98f, 0.45f, 0.45f, 1f) : ModernUiKit.TextPrimary;
                    var rendered = TermTagFormatter.FormatInlineMarkdown(b.Text, out var termSpans);
                    var paraW = Mathf.Max(40f, startRect.width);
                    var txtH = Text.CalcHeight(rendered, paraW);
                    DrawFormattedLabelWithTerms(new Rect(startRect.x, curY, paraW, txtH + 2f), rendered, termSpans, paraColor);
                    curY += txtH + 8f;
                    break;
                }
            }
        }

        return curY - startRect.y;
    }

    private static float MeasureTableHeight(List<string[]> rows, float width)
    {
        if (rows.Count == 0) return 0f;
        var colCount = rows.Max(r => r.Length);
        if (colCount <= 0) return 0f;

        var colW = (width - 4f) / Math.Max(1, colCount);
        var cellInnerW = Mathf.Max(20f, colW - 14f);
        var totalH = 4f;
        Text.Font = GameFont.Tiny;
        Text.WordWrap = true;

        for (var rIdx = 0; rIdx < rows.Count; rIdx++)
        {
            var row = rows[rIdx];
            var rowMaxH = 26f;
            for (var c = 0; c < row.Length; c++)
            {
                var finalCell = TermTagFormatter.FormatInlineMarkdown(row[c], wrapBold: rIdx == 0);
                var ch = Text.CalcHeight(finalCell, cellInnerW) + 12f;
                if (ch > rowMaxH) rowMaxH = ch;
            }
            totalH += rowMaxH;
        }

        return totalH + 4f;
    }

    private float DrawMarkdownTable(Rect startRect, List<string[]> rows)
    {
        if (rows.Count == 0) return 0f;

        var totalH = MeasureTableHeight(rows, startRect.width);
        var tableRect = new Rect(startRect.x, startRect.y, startRect.width, totalH);
        ModernUiKit.DrawRoundedCard(tableRect, ModernUiKit.BgTableRowEven, ModernUiKit.BorderCard, radius: 8, borderThickness: 1);

        var colCount = rows.Max(r => r.Length);
        var colW = (startRect.width - 4f) / Math.Max(1, colCount);
        var cellInnerW = Mathf.Max(20f, colW - 14f);
        var curY = startRect.y + 2f;

        Text.Font = GameFont.Tiny;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.WordWrap = true;

        for (var rIdx = 0; rIdx < rows.Count; rIdx++)
        {
            var row = rows[rIdx];
            var rowMaxH = 26f;
            for (var c = 0; c < row.Length; c++)
            {
                var finalCell = TermTagFormatter.FormatInlineMarkdown(row[c], wrapBold: rIdx == 0);
                var ch = Text.CalcHeight(finalCell, cellInnerW) + 12f;
                if (ch > rowMaxH) rowMaxH = ch;
            }

            var rowRect = new Rect(startRect.x + 2f, curY, startRect.width - 4f, rowMaxH);
            if (rIdx == 0) ModernUiKit.DrawRoundedRect(rowRect, ModernUiKit.BgTableHeader, radius: 6);
            else if (rIdx % 2 == 1) Widgets.DrawBoxSolid(rowRect, ModernUiKit.BgTableRowOdd);

            if (rIdx > 0) ModernUiKit.DrawHorizontalHairline(rowRect.x, rowRect.y, rowRect.width, ModernUiKit.BorderSubtle);

            for (var c = 0; c < row.Length; c++)
            {
                var cellX = rowRect.x + c * colW;
                if (c > 0) ModernUiKit.DrawVerticalHairline(cellX, rowRect.y, rowMaxH, ModernUiKit.BorderSubtle);

                var finalCell = TermTagFormatter.FormatInlineMarkdown(row[c], out var cellTerms, wrapBold: rIdx == 0);
                var cellTextH = Text.CalcHeight(finalCell, cellInnerW);
                var yOffset = Mathf.Max(5f, (rowMaxH - cellTextH) * 0.5f);

                var cellColor = rIdx == 0 ? ModernUiKit.AccentCyan : ModernUiKit.TextPrimary;
                DrawFormattedLabelWithTerms(
                    new Rect(cellX + 7f, rowRect.y + yOffset, cellInnerW, cellTextH + 2f),
                    finalCell,
                    cellTerms,
                    cellColor);
            }

            curY += rowMaxH;
        }

        return totalH;
    }

    private void DrawFormattedLabelWithTerms(Rect labelRect, string rendered, List<InlineTermSpan> termSpans, Color baseColor)
    {
        if (termSpans.Count == 0)
        {
            GUI.color = baseColor;
            Widgets.Label(labelRect, rendered);
            return;
        }

        SharedTermResolvedSegments.Clear();
        var style = Text.CurFontStyle;
        var contentX = labelRect.x + style.padding.left + style.contentOffset.x;
        var contentY = labelRect.y + style.padding.top + style.contentOffset.y;
        var contentW = Mathf.Max(0f, labelRect.width - style.padding.horizontal);
        var wrapW = style.wordWrap ? contentW : -1f;
        var backend = GuiBackend.Current;

        for (var i = 0; i < termSpans.Count; i++)
        {
            var span = termSpans[i];
            if (span.RichTextEnd <= span.RichTextStart || string.IsNullOrEmpty(span.TermText)) continue;

            SharedTermSelectionRects.Clear();
            backend?.TextSelectionRects(
                style.FontIndex,
                style.fontSize,
                style.fontStyle,
                rendered,
                wrapW,
                span.RichTextStart,
                span.RichTextEnd,
                SharedTermSelectionRects,
                richText: true);

            for (var rIdx = 0; rIdx < SharedTermSelectionRects.Count; rIdx++)
            {
                var r = SharedTermSelectionRects[rIdx];
                if (r.width <= 0.5f || r.height <= 0.5f) continue;
                var segRect = new Rect(contentX + r.x, contentY + r.y, r.width, r.height);
                SharedTermResolvedSegments.Add((span, segRect));
            }
        }

        var hoveredRichStart = -1;
        for (var i = 0; i < SharedTermResolvedSegments.Count; i++)
        {
            if (Mouse.IsOver(SharedTermResolvedSegments[i].Rect))
            {
                hoveredRichStart = SharedTermResolvedSegments[i].Span.RichTextStart;
                break;
            }
        }

        if (hoveredRichStart >= 0)
        {
            for (var i = 0; i < SharedTermResolvedSegments.Count; i++)
            {
                var (span, segRect) = SharedTermResolvedSegments[i];
                if (span.RichTextStart == hoveredRichStart)
                {
                    var bgRect = new Rect(segRect.x - 2f, segRect.y, segRect.width + 4f, segRect.height);
                    ModernUiKit.DrawRoundedRect(bgRect, new Color(0.220f, 0.741f, 0.973f, 0.16f), radius: 3);
                }
            }
        }

        GUI.color = baseColor;
        Widgets.Label(labelRect, rendered);

        var normalUnderlineColor = new Color(0.220f, 0.741f, 0.973f, 0.85f);
        var hoverUnderlineColor = new Color(0.576f, 0.898f, 1.000f, 1.00f);
        string? clickedTerm = null;

        for (var i = 0; i < SharedTermResolvedSegments.Count; i++)
        {
            var (span, segRect) = SharedTermResolvedSegments[i];
            var isHovered = span.RichTextStart == hoveredRichStart;
            var underlineH = isHovered ? 1.5f : 1f;
            var underlineY = Mathf.Round(segRect.yMax - 2f);
            var underlineRect = new Rect(segRect.x, underlineY, segRect.width, underlineH);
            Widgets.DrawBoxSolid(underlineRect, isHovered ? hoverUnderlineColor : normalUnderlineColor);

            TooltipHandler.TipRegion(segRect, UiText.Format("GCFAgent_TermInsertTip", span.TermText));
            if (Widgets.ButtonInvisible(segRect))
            {
                clickedTerm = span.TermText;
            }
        }

        if (!string.IsNullOrEmpty(clickedTerm))
        {
            try { SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); } catch { /* ignore */ }
            AppendTermToComposer(clickedTerm!);
        }
    }

    private void AppendTermToComposer(string term)
    {
        var updated = TermTagFormatter.AppendTermToInput(_inputBuffer, term);
        if (string.Equals(updated, _inputBuffer, StringComparison.Ordinal)) return;

        _inputBuffer = updated;
        if (_currentSession != null) _drafts[_currentSession.Id] = _inputBuffer;

        _moveComposerCaretToEnd = true;
        GUI.FocusWindow(ID);
        if (!string.Equals(GUI.GetNameOfFocusedControl(), ComposerControlName, StringComparison.Ordinal))
        {
            GUI.FocusControl(ComposerControlName);
        }
        SyncComposerCaretToEnd(_inputBuffer);
    }

    private static void SyncComposerCaretToEnd(string? text)
    {
        if (GUIUtility.keyboardControl == 0) return;
        try
        {
            if (GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) is TextEditor editor)
            {
                var safe = text ?? string.Empty;
                editor.text = safe;
                editor.cursorIndex = safe.Length;
                editor.selectIndex = safe.Length;
            }
        }
        catch { /* ignore */ }
    }

    private static float CalculateComposerInputHeight(string? text, float inputWidth)
    {
        if (string.IsNullOrEmpty(text)) return 34f;

        var prevFont = Text.Font;
        var prevWrap = Text.WordWrap;
        try
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            var measureText = text + " ";
            var textH = Text.CalcHeight(measureText, Mathf.Max(40f, inputWidth - 6f));
            return Mathf.Clamp(Mathf.Ceil(textH + 10f), 34f, 108f);
        }
        finally
        {
            Text.Font = prevFont;
            Text.WordWrap = prevWrap;
        }
    }

    private static void ResetComposerCaret()
    {
        if (GUIUtility.keyboardControl == 0) return;
        try
        {
            if (GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) is TextEditor editor)
            {
                editor.text = string.Empty;
                editor.cursorIndex = 0;
                editor.selectIndex = 0;
                editor.scrollOffset = Vector2.zero;
            }
        }
        catch { /* ignore */ }
    }

    private void DrawComposer(Rect rect, AgentSession session, float inputH, float pillH)
    {
        var pillRect = new Rect(rect.x + 18f, rect.y + 8f, rect.width - 36f, pillH);
        var focused = string.Equals(GUI.GetNameOfFocusedControl(), ComposerControlName, StringComparison.Ordinal);
        var hovered = Mouse.IsOver(pillRect);
        var border = focused
            ? ModernUiKit.BorderInputFocus
            : (hovered ? new Color(0.247f, 0.337f, 0.490f, 1f) : ModernUiKit.BorderCard);
        ModernUiKit.DrawRoundedCard(pillRect, ModernUiKit.BgComposer, border, radius: 14, borderThickness: 1);

        var evt = Event.current;
        var isComposingIme = !string.IsNullOrEmpty(Input.compositionString);
        var isShiftHeld = evt != null && (
            evt.shift ||
            (evt.modifiers & EventModifiers.Shift) != 0 ||
            Input.GetKey(KeyCode.LeftShift) ||
            Input.GetKey(KeyCode.RightShift));
        var isEnterKeyDown = evt != null &&
            evt.type == EventType.KeyDown &&
            (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter);

        if (focused && !isComposingIme && evt != null && evt.type == EventType.KeyDown)
        {
            if (isEnterKeyDown && !isShiftHeld)
            {
                evt.Use();
                if (!session.IsBusy && !string.IsNullOrWhiteSpace(_inputBuffer))
                {
                    RequestSend(session);
                }
            }
            else if (!isShiftHeld && evt.keyCode == KeyCode.None && (evt.character == '\r' || evt.character == '\n'))
            {
                evt.Use();
            }
        }

        if (_moveComposerCaretToEnd)
        {
            SyncComposerCaretToEnd(_inputBuffer);
        }

        var inputRect = new Rect(pillRect.x + 14f, pillRect.y + 8f, Mathf.Max(60f, pillRect.width - 28f), inputH);
        _inputBuffer = ModernUiKit.DrawBareComposerInput(
            inputRect,
            _inputBuffer ?? string.Empty,
            placeholder: UiText.Get("GCFAgent_ComposerPlaceholder"),
            controlName: ComposerControlName,
            multiline: true);

        if (_moveComposerCaretToEnd)
        {
            SyncComposerCaretToEnd(_inputBuffer);
            if (evt != null &&
                evt.type == EventType.Repaint &&
                string.Equals(GUI.GetNameOfFocusedControl(), ComposerControlName, StringComparison.Ordinal))
            {
                _moveComposerCaretToEnd = false;
            }
        }

        if (focused && !isComposingIme && isEnterKeyDown && isShiftHeld && evt != null && evt.type == EventType.KeyDown)
        {
            _inputBuffer = (_inputBuffer ?? string.Empty) + "\n";
            evt.Use();
        }

        var toolbarY = pillRect.y + inputH + 10f;

        var cameraBtnRect = new Rect(pillRect.x + 12f, toolbarY, 28f, 26f);
        if (ModernUiKit.DrawCameraIconButton(
                cameraBtnRect,
                radius: 8,
                disabled: session.IsBusy || _suppressWindowForScreenshot || _contextChoiceDialog != null,
                tooltip: UiText.Get("GCFAgent_CameraButtonTip")))
        {
            RequestSend(session, withScreenshot: true);
        }

        var settings = GiantCreatureAgentMod.Settings;
        var activeCfg = settings.GetEffectiveActiveConfig();
        var isConfigured = activeCfg != null && activeCfg.IsMinimallyValid();
        var sendingIdentity = ModelIdentityDisplay.CurrentRequest(session);
        var modelChipLabel = sendingIdentity != null
            ? UiText.Format("GCFAgent_ModelChipCurrent", sendingIdentity.Label)
            : isConfigured
            ? UiText.Format("GCFAgent_ModelChipNext", activeCfg!.GetResolvedModel())
            : UiText.Get("GCFAgent_ModelChipChoose");

        var prevWrap = Text.WordWrap;
        var prevFont = Text.Font;
        Text.Font = GameFont.Tiny;
        Text.WordWrap = false;
        var modelChipW = Mathf.Clamp(Text.CalcSize(modelChipLabel).x + 20f, 92f, 220f);
        Text.WordWrap = prevWrap;
        Text.Font = prevFont;

        var modelChipRect = new Rect(cameraBtnRect.xMax + 6f, toolbarY, modelChipW, 26f);
        if (ModernUiKit.DrawButton(modelChipRect, modelChipLabel, ModernButtonVariant.Ghost, radius: 8, font: GameFont.Tiny))
        {
            OpenQuickProviderModelSwitcher(settings);
        }
        TooltipHandler.TipRegion(modelChipRect, sendingIdentity != null
            ? sendingIdentity.Details + "\n" + UiText.Get("GCFAgent_ModelChipNextSendTip") : UiText.Get("GCFAgent_ModelChipChooseTip"));

        var sendBtnRect = new Rect(pillRect.xMax - 76f, toolbarY, 64f, 26f);
        var clearBtnRect = new Rect(sendBtnRect.x - 56f, toolbarY, 50f, 26f);

        var contextRect = new Rect(modelChipRect.xMax + 6f, toolbarY,
            Mathf.Min(108f, clearBtnRect.x - modelChipRect.xMax - 12f), 26f);
        if (contextRect.width >= 68f)
        {
            var contextLabel = session.UseConversationHistory switch
            {
                true => UiText.Get("GCFAgent_ContextOn"),
                false => UiText.Get("GCFAgent_ContextOff"),
                _ => UiText.Get("GCFAgent_ContextPending")
            };
            if (ModernUiKit.DrawButton(contextRect, contextLabel, ModernButtonVariant.Ghost, radius: 8, font: GameFont.Tiny)
                && !session.IsBusy && _contextChoiceDialog == null)
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new(UiText.Get("GCFAgent_ContextEnableThisChat"), () => SetConversationHistory(session, true)),
                    new(UiText.Get("GCFAgent_ContextDisableThisChat"), () => SetConversationHistory(session, false))
                }));
            }
            TooltipHandler.TipRegion(contextRect, UiText.Get("GCFAgent_ContextScopeTip"));
        }

        if (ModernUiKit.DrawButton(clearBtnRect, UiText.Get("GCFAgent_CommonClear"), ModernButtonVariant.Ghost, radius: 8, font: GameFont.Tiny))
        {
            if (!string.IsNullOrEmpty(_inputBuffer))
            {
                _inputBuffer = string.Empty;
                ResetComposerCaret();
                GUI.FocusControl(ComposerControlName);
            }
            else
            {
                var cleared = false;
                List<object?>? releasedTextures = null;
                lock (session.SyncRoot)
                {
                    if (!session.IsBusy)
                    {
                        foreach (var msg in session.Messages)
                        {
                            if (msg.PreviewTexture != null)
                            {
                                releasedTextures ??= new List<object?>();
                                releasedTextures.Add(msg.PreviewTexture);
                                msg.PreviewTexture = null;
                            }
                        }
                        session.Messages.Clear();
                        session.RecalculateTokenTotals();
                        _heightCache.Clear();
                        cleared = true;
                    }
                }
                if (releasedTextures != null)
                {
                    foreach (var tex in releasedTextures)
                    {
                        AgentSessionManager.ReleasePreviewTexture(tex);
                    }
                }
                if (cleared)
                {
                    AgentSessionManager.QueueSave();
                }
            }
        }
        TooltipHandler.TipRegion(clearBtnRect, UiText.Get("GCFAgent_ClearButtonTip"));

        if (session.IsBusy)
        {
            if (ModernUiKit.DrawButton(sendBtnRect, "■ " + UiText.Get("GCFAgent_CommonAbort"), ModernButtonVariant.Danger, radius: 10, font: GameFont.Tiny))
            {
                AgentLlmClient.CancelActiveTurn(session);
            }
        }
        else
        {
            if (ModernUiKit.DrawButton(sendBtnRect, "↑ " + UiText.Get("GCFAgent_CommonSend"), ModernButtonVariant.Primary, radius: 10, font: GameFont.Tiny) &&
                !string.IsNullOrWhiteSpace(_inputBuffer))
            {
                RequestSend(session);
            }
        }

        if (evt != null && evt.type == EventType.MouseDown && evt.button == 0 && pillRect.Contains(evt.mousePosition))
        {
            GUI.FocusControl(ComposerControlName);
            evt.Use();
        }
    }

    private static void SetConversationHistory(AgentSession session, bool enabled)
    {
        if (session.IsBusy) return;
        session.UseConversationHistory = enabled;
        AgentSessionManager.QueueSave();
    }

    private void RequestSend(AgentSession session, bool withScreenshot = false)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || session.IsBusy || _suppressWindowForScreenshot || _contextChoiceDialog != null) return;
        var draft = _inputBuffer;
        if (!withScreenshot && string.IsNullOrWhiteSpace(draft)) return;
        var scope = session.ScopeId;
        var settings = GiantCreatureAgentMod.Settings;

        bool CanSend() => GiantCreatureAgentMod.IsTerminalEnabled
            && !session.IsBusy && session.ScopeId == AgentSessionManager.ScopeId && scope == session.ScopeId
            && Sessions.Contains(session) && ReferenceEquals(_currentSession, session) && _inputBuffer == draft;

        void Send()
        {
            if (!CanSend()) return;
            var prompt = string.IsNullOrWhiteSpace(draft)
                ? AgentProtocol.Get("ChatWindow_ScreenshotDefaultPrompt")
                : draft.Trim();
            var previousCount = session.Messages.Count;
            if (withScreenshot) TriggerSendCurrentScreenCapture(session, prompt);
            else AgentLlmClient.StartTurn(session, prompt);
            if (_suppressWindowForScreenshot || session.Messages.Count > previousCount)
            {
                _inputBuffer = string.Empty;
                _drafts[session.Id] = string.Empty;
                ResetComposerCaret();
            }
        }

        if (ConversationContextChoice.PrepareSend(session, settings.AskConversationHistory, settings.DefaultConversationHistory))
        {
            Send();
            return;
        }

        _contextChoiceDialog = new Dialog_ConversationContext((enabled, remember) =>
        {
            _contextChoiceDialog = null;
            if (!CanSend()) return;
            SetConversationHistory(session, enabled);
            if (remember)
            {
                settings.AskConversationHistory = false;
                settings.DefaultConversationHistory = enabled;
                settings.SaveToUserConfig();
            }
            Send();
        }, () => _contextChoiceDialog = null);
        Find.WindowStack.Add(_contextChoiceDialog);
    }

    private void TriggerSendCurrentScreenCapture(AgentSession session, string promptText)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || session.IsBusy || _suppressWindowForScreenshot)
        {
            return;
        }

        if (Current.Root != null)
        {
            _suppressWindowForScreenshot = true;
            session.SetStatus(UiText.Get("GCFAgent_StatusCapturingScreenshot"), isError: false);
            Current.Root.StartCoroutine(CaptureAndSendScreenshotCoroutine(session, promptText));
        }
        else
        {
            AgentLlmClient.StartTurn(session, promptText);
        }
    }

    private IEnumerator CaptureAndSendScreenshotCoroutine(AgentSession session, string promptText)
    {
        yield return null;

        try
        {
            ScreenCaptureBackend.Current?.RequestCapture();
        }
        catch
        {
            // Ignore capture request failure
        }

        yield return new WaitForEndOfFrame();

        _suppressWindowForScreenshot = false;
        var capturingStatus = UiText.Get("GCFAgent_StatusCapturingScreenshot");
        void ClearStuckCapturingStatus()
        {
            lock (session.SyncRoot)
            {
                if (!session.IsBusy && string.Equals(session.StatusText, capturingStatus, StringComparison.Ordinal))
                {
                    session.SetStatus(UiText.KeySessionStatusReady, isError: false);
                }
            }
        }

        if (!GiantCreatureAgentMod.IsTerminalEnabled)
        {
            ClearStuckCapturingStatus();
            yield break;
        }

        string? dataUrl = null;
        Texture2D? previewTex = null;
        try
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex != null && tex.width > 0 && tex.height > 0)
            {
                previewTex = tex;
                var jpgBytes = tex.EncodeToJPG(78);
                var mime = "image/jpeg";
                if (jpgBytes == null || jpgBytes.Length == 0)
                {
                    jpgBytes = tex.EncodeToPNG();
                    mime = "image/png";
                }

                if (jpgBytes != null && jpgBytes.Length > 0)
                {
                    dataUrl = $"data:{mime};base64,{Convert.ToBase64String(jpgBytes)}";
                }
                else
                {
                    UnityEngine.Object.Destroy(previewTex);
                    previewTex = null;
                }
            }
            else if (tex != null)
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[GiantCreatureFramework.Agent] Failed to capture screen: {ex.Message}");
            if (previewTex != null)
            {
                UnityEngine.Object.Destroy(previewTex);
                previewTex = null;
            }
        }

        var started = false;
        try
        {
            started = AgentLlmClient.StartTurn(session, promptText, dataUrl, previewTex);
        }
        catch
        {
            ClearStuckCapturingStatus();
            throw;
        }
        if (!started)
        {
            ClearStuckCapturingStatus();
        }
    }

    private static void EnsureDefaultSession() => AgentSessionManager.EnsureDefault();

    private enum BlockKind
    {
        Paragraph,
        Heading,
        Divider,
        Code,
        Table
    }

    private sealed class RichBlock
    {
        public BlockKind Kind { get; init; }
        public string Text { get; init; } = string.Empty;
        public string Language { get; init; } = string.Empty;
        public int HeadingLevel { get; init; }
        public List<string[]>? TableRows { get; init; }
    }

    private static List<RichBlock> ParseRichBlocks(string input)
    {
        if (BlockCache.TryGetValue(input, out var cached)) return cached;
        var blocks = ParseRichBlocksCore(input);
        if (BlockCache.Count >= 128) BlockCache.Clear();
        BlockCache[input] = blocks;
        return blocks;
    }

    private static List<RichBlock> ParseRichBlocksCore(string input)
    {
        var result = new List<RichBlock>();
        if (string.IsNullOrEmpty(input)) return result;

        var normalized = input.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var lang = trimmed.Substring(3).Trim();
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;

                result.Add(new RichBlock
                {
                    Kind = BlockKind.Code,
                    Language = lang,
                    Text = string.Join("\n", codeLines).TrimEnd()
                });
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                result.Add(new RichBlock { Kind = BlockKind.Divider });
                i++;
                continue;
            }

            if (IsHeadingLine(trimmed, out var headingLevel, out var headingText))
            {
                result.Add(new RichBlock
                {
                    Kind = BlockKind.Heading,
                    HeadingLevel = headingLevel,
                    Text = headingText
                });
                i++;
                continue;
            }

            if (IsTableRowLine(trimmed))
            {
                var tableRows = new List<string[]>();
                while (i < lines.Length)
                {
                    var tLine = lines[i].Trim();
                    if (IsTableRowLine(tLine))
                    {
                        var inner = tLine.Substring(1, tLine.Length - 2);
                        var cells = inner.Split('|').Select(c => c.Trim()).ToArray();
                        var isSeparator = cells.All(c => c.All(ch => ch is '-' or ':' or ' '));
                        if (!isSeparator && cells.Length > 0) tableRows.Add(cells);
                        i++;
                    }
                    else if (tableRows.Count > 0 && i == lines.Length - 1 && tLine.StartsWith("|", StringComparison.Ordinal))
                    {
                        var inner = tLine.Length > 1 ? tLine.Substring(1) : string.Empty;
                        if (!string.IsNullOrWhiteSpace(inner))
                        {
                            var cells = inner.Split('|').Select(c => c.Trim()).ToArray();
                            var isSeparator = cells.All(c => c.All(ch => ch is '-' or ':' or ' '));
                            if (!isSeparator && cells.Length > 0) tableRows.Add(cells);
                        }
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (tableRows.Count > 0)
                {
                    result.Add(new RichBlock
                    {
                        Kind = BlockKind.Table,
                        TableRows = tableRows
                    });
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                i++;
                continue;
            }

            var paraBuilder = new StringBuilder();
            while (i < lines.Length)
            {
                var pLine = lines[i];
                var pTrim = pLine.Trim();
                if (paraBuilder.Length > 0 &&
                    (string.IsNullOrWhiteSpace(pTrim) ||
                     pTrim.StartsWith("```", StringComparison.Ordinal) ||
                     pTrim is "---" or "***" or "___" ||
                     IsHeadingLine(pTrim, out _, out _) ||
                     IsTableRowLine(pTrim)))
                {
                    break;
                }

                if (paraBuilder.Length > 0) paraBuilder.Append('\n');
                paraBuilder.Append(FormatListPrefix(pLine));
                i++;
            }

            if (paraBuilder.Length > 0)
            {
                result.Add(new RichBlock
                {
                    Kind = BlockKind.Paragraph,
                    Text = paraBuilder.ToString()
                });
            }
        }

        return result;
    }

    private static bool IsHeadingLine(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '#') return false;

        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' ')
        {
            text = trimmed.Substring(level + 1).Trim();
            return true;
        }
        return false;
    }

    private static bool IsTableRowLine(string trimmed) =>
        trimmed.Length >= 2 &&
        trimmed.StartsWith("|", StringComparison.Ordinal) &&
        trimmed.EndsWith("|", StringComparison.Ordinal);

    private static string FormatListPrefix(string line)
    {
        var trimmedStart = line.TrimStart();
        var indentSpaces = line.Length - trimmedStart.Length;
        var indent = indentSpaces >= 2 ? "    " : string.Empty;

        if (trimmedStart.StartsWith("* ", StringComparison.Ordinal) ||
            trimmedStart.StartsWith("- ", StringComparison.Ordinal) ||
            trimmedStart.StartsWith("• ", StringComparison.Ordinal))
        {
            return indent + TermTagFormatter.BulletPlaceholder + trimmedStart.Substring(2);
        }

        return line;
    }

    private static string EscapeRichText(string? text) => TermTagFormatter.EscapeRichText(text);

    private static string TruncateForUi(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text.Substring(0, maxChars) + "\n... " + UiText.Get("GCFAgent_NoticeUiTruncated");
    }
}
