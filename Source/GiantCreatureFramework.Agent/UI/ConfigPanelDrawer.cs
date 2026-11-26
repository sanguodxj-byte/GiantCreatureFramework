using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.UI;

/// <summary>
/// Shared, non-overlapping configuration UI component for Creature Forge.
/// Exposes multi-provider chat failover management, independent Image Generation endpoint
/// (Endpoint / Model / ApiKey), and prompt/context configuration.
/// </summary>
public static class ConfigPanelDrawer
{
    public static int ActiveTabIndex;
    private static Vector2 _scrollPos;
    private static bool _showPlainApiKeys;
    private static string _statusBannerText = string.Empty;
    private static bool _statusBannerIsError;

    private sealed class RowAutoFetchState
    {
        public string ObservedSignature = string.Empty;
        public string FetchedSignature = string.Empty;
        public float LastChangedRealtime;
        public bool IsFetching;
    }

    private static readonly Dictionary<ApiConfigEntry, RowAutoFetchState> _rowAutoFetchStates = new();

    public static void SetBanner(string text, bool isError = false)
    {
        _statusBannerText = text ?? string.Empty;
        _statusBannerIsError = isError;
    }

    public static void DrawConfigPanel(
        Rect rect,
        AgentSettings settings,
        AgentSession? currentSession = null,
        Action? onCloseDrawer = null)
    {
        settings.EnsureApiConfigsInitialized();

        // 1. Top Sub-navigation Bar
        var tabBarH = 32f;
        var tabBarRect = new Rect(rect.x, rect.y, rect.width, tabBarH);
        DrawTabBar(tabBarRect, settings);

        // 2. Bottom Action Bar
        var bottomBarH = 38f;
        var bottomBarRect = new Rect(rect.x, rect.yMax - bottomBarH, rect.width, bottomBarH);

        // 3. Scrollable Content Area
        var bodyY = tabBarRect.yMax + 8f;
        var bodyH = Mathf.Max(60f, bottomBarRect.y - bodyY - 8f);
        var bodyOuterRect = new Rect(rect.x, bodyY, rect.width, bodyH);
        ModernUiKit.DrawRoundedCard(bodyOuterRect, ModernUiKit.BgWindow, ModernUiKit.BorderSubtle, 8, 1);

        var scrollViewport = new Rect(bodyOuterRect.x + 6f, bodyOuterRect.y + 6f, bodyOuterRect.width - 12f, bodyOuterRect.height - 12f);
        var contentW = scrollViewport.width - 18f;
        var contentH = CalculateTabContentHeight(ActiveTabIndex, settings, currentSession);
        var viewRect = new Rect(0f, 0f, contentW, Mathf.Max(scrollViewport.height, contentH));

        Widgets.BeginScrollView(scrollViewport, ref _scrollPos, viewRect);
        try
        {
            if (ActiveTabIndex != 0 && ActiveTabIndex != 1)
            {
                ActiveTabIndex = 0;
            }

            switch (ActiveTabIndex)
            {
                case 0:
                    DrawProvidersAndModelsTab(viewRect, settings);
                    break;
                case 1:
                    DrawSystemPromptTab(viewRect, settings, currentSession);
                    break;
            }
        }
        finally
        {
            Widgets.EndScrollView();
        }

        DrawBottomActionBar(bottomBarRect, settings, onCloseDrawer);
    }

    private static void DrawTabBar(Rect rect, AgentSettings settings)
    {
        var tabCount = 2;
        var gap = 8f;
        var tabW = Mathf.Min(230f, (rect.width - gap * (tabCount - 1)) / tabCount);

        var tab0Rect = new Rect(rect.x, rect.y, tabW, rect.height);
        var tab1Rect = new Rect(tab0Rect.xMax + gap, rect.y, tabW, rect.height);

        if (ModernUiKit.DrawButton(
            tab0Rect,
            UiText.Format("GCFAgent_TabProviders", settings.ApiConfigs.Count(c => c.IsEnabled).ToString()),
            ActiveTabIndex == 0 ? ModernButtonVariant.AccentPill : ModernButtonVariant.Pill,
            7,
            ActiveTabIndex == 0,
            GameFont.Tiny))
        {
            ActiveTabIndex = 0;
            _scrollPos = Vector2.zero;
        }

        var hasCustomPrompt = AgentPrompts.Definitions.Any(d => settings.IsPromptCustomized(d.Section));
        if (ModernUiKit.DrawButton(
            tab1Rect,
            hasCustomPrompt ? UiText.Get("GCFAgent_TabPromptsCustomized") : UiText.Get("GCFAgent_TabPrompts"),
            ActiveTabIndex == 1 ? ModernButtonVariant.AccentPill : ModernButtonVariant.Pill,
            7,
            ActiveTabIndex == 1,
            GameFont.Tiny))
        {
            ActiveTabIndex = 1;
            _scrollPos = Vector2.zero;
        }

        var rightSpace = rect.xMax - (tab1Rect.xMax + 12f);
        if (rightSpace >= 130f)
        {
            var active = settings.GetEffectiveActiveConfig();
            var activeLabel = active != null
                ? UiText.Format("GCFAgent_ActiveProviderLabel", active.Provider.ShortLabel(), active.GetResolvedModel())
                : UiText.Get("GCFAgent_NoValidProvider");
            var infoRect = new Rect(tab1Rect.xMax + 12f, rect.y + 3f, rightSpace, rect.height - 6f);
            ModernUiKit.DrawRoundedCard(infoRect, ModernUiKit.BgInputWell, ModernUiKit.BorderSubtle, 6, 1);
            ModernUiKit.DrawSingleLineLabel(
                new Rect(infoRect.x + 8f, infoRect.y, infoRect.width - 16f, infoRect.height),
                activeLabel,
                GameFont.Tiny,
                active != null && active.IsMinimallyValid() ? ModernUiKit.AccentEmerald : ModernUiKit.AccentAmber,
                TextAnchor.MiddleLeft,
                active?.GetResolvedEndpoint());
        }
    }

    private static float CalculateTabContentHeight(int tabIndex, AgentSettings settings, AgentSession? session)
    {
        if (tabIndex == 0)
        {
            var h = 52f + 32f;
            foreach (var cfg in settings.ApiConfigs)
            {
                h += (cfg.Provider.UsesCustomUrl() ? 70f : 40f) + 6f;
            }
            h += 146f + 36f; // Dedicated Image Generation Endpoint card + Footer note
            return h + 12f;
        }

        return (session != null ? 58f : 0f) + 210f;
    }

    private static void DrawProvidersAndModelsTab(Rect viewRect, AgentSettings settings)
    {
        var curY = 4f;
        var width = viewRect.width;

        // 1. Top Toolbar: Show Keys + Add Provider
        var barRect = new Rect(4f, curY, width - 8f, 38f);
        ModernUiKit.DrawRoundedCard(barRect, ModernUiKit.BgCardAssistantHeader, ModernUiKit.BorderCard, 7, 1);

        var addBtnW = 96f;
        var keyToggleW = 76f;
        var rightButtonsTotalW = addBtnW + keyToggleW + 12f;

        var titleW = Mathf.Max(140f, barRect.width - rightButtonsTotalW - 16f);
        ModernUiKit.DrawSingleLineLabel(
            new Rect(barRect.x + 12f, barRect.y + 5f, titleW, 28f),
            UiText.Get("GCFAgent_ProviderListCaption"),
            GameFont.Tiny,
            ModernUiKit.TextSecondary);

        var btnX = barRect.xMax - 8f;
        btnX -= addBtnW;
        if (ModernUiKit.DrawButton(new Rect(btnX, barRect.y + 5f, addBtnW, 28f), "+ " + UiText.Get("GCFAgent_ProviderAdd"), ModernButtonVariant.Primary, 6, false, GameFont.Tiny))
        {
            settings.ApiConfigs.Add(new ApiConfigEntry
            {
                IsEnabled = true,
                Provider = AIProvider.Player2
            });
            settings.SyncActiveConfigToPrimaryFields();
        }

        btnX -= keyToggleW + 6f;
        if (ModernUiKit.DrawButton(
            new Rect(btnX, barRect.y + 5f, keyToggleW, 28f),
            _showPlainApiKeys ? UiText.Get("GCFAgent_KeysHide") : UiText.Get("GCFAgent_KeysShow"),
            ModernButtonVariant.Secondary,
            6,
            _showPlainApiKeys,
            GameFont.Tiny))
        {
            _showPlainApiKeys = !_showPlainApiKeys;
        }

        curY += 46f;

        // 2. Column Headers
        var headerRect = new Rect(4f, curY, width - 8f, 24f);
        ModernUiKit.DrawRoundedRect(headerRect, ModernUiKit.BgTableHeader, 5);
        var colEnableX = headerRect.x + 8f;
        var colProvX = colEnableX + 54f;
        var colProvW = 148f;
        var colActionsW = 86f;
        var remainingW = Mathf.Max(200f, headerRect.width - (colProvX - headerRect.x) - colProvW - colActionsW - 24f);
        var colKeyW = Mathf.Floor(remainingW * 0.48f);
        var colModelW = remainingW - colKeyW;
        var colKeyX = colProvX + colProvW + 6f;
        var colModelX = colKeyX + colKeyW + 6f;
        var colActionsX = headerRect.xMax - colActionsW - 6f;

        ModernUiKit.DrawSingleLineLabel(new Rect(colEnableX, curY, 52f, 24f), UiText.Get("GCFAgent_ColEnable"), GameFont.Tiny, ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(new Rect(colProvX, curY, colProvW, 24f), UiText.Get("GCFAgent_ColProvider"), GameFont.Tiny, ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(new Rect(colKeyX, curY, colKeyW, 24f), UiText.Get("GCFAgent_ColApiKey"), GameFont.Tiny, ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(new Rect(colModelX, curY, colModelW, 24f), UiText.Get("GCFAgent_ColModel"), GameFont.Tiny, ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(new Rect(colActionsX, curY, colActionsW, 24f), UiText.Get("GCFAgent_ColActions"), GameFont.Tiny, ModernUiKit.TextSecondary, TextAnchor.MiddleCenter);

        curY += 28f;

        // 3. Provider Rows
        for (var i = 0; i < settings.ApiConfigs.Count; i++)
        {
            var cfg = settings.ApiConfigs[i];
            var hasCustomUrl = cfg.Provider.UsesCustomUrl();
            var rowH = hasCustomUrl ? 70f : 40f;
            var rowRect = new Rect(4f, curY, width - 8f, rowH);

            var isPrimary = cfg.IsEnabled && settings.ApiConfigs.Take(i).All(c => !c.IsEnabled);
            var fill = isPrimary
                ? new Color(0.075f, 0.110f, 0.176f, 1f)
                : (i % 2 == 0 ? ModernUiKit.BgTableRowEven : ModernUiKit.BgTableRowOdd);
            var border = isPrimary ? ModernUiKit.AccentPrimaryBorder : ModernUiKit.BorderSubtle;
            ModernUiKit.DrawRoundedCard(rowRect, fill, border, 7, 1);

            var line1Y = curY + 6f;

            var en = cfg.IsEnabled;
            var checkCellRect = new Rect(colEnableX, line1Y, 50f, 28f);
            if (ModernUiKit.DrawModernCheckbox(checkCellRect, $"#{i + 1}", ref en, UiText.Get("GCFAgent_ProviderEnableTip"), GameFont.Tiny))
            {
                cfg.IsEnabled = en;
                settings.SyncActiveConfigToPrimaryFields();
            }

            var provBtnRect = new Rect(colProvX, line1Y, colProvW, 28f);
            if (ModernUiKit.DrawButton(provBtnRect, cfg.Provider.ShortLabel() + " ▾", ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                OpenProviderMenu(settings, cfg);
            }
            TooltipHandler.TipRegion(provBtnRect, UiText.Format("GCFAgent_ProviderRowTip", cfg.Provider.Label(), cfg.GetResolvedEndpoint()));

            var keyCellRect = new Rect(colKeyX, line1Y, colKeyW, 28f);
            if (cfg.Provider == AIProvider.Player2)
            {
                DrawPlayer2AuthCell(keyCellRect, settings, cfg);
            }
            else if (cfg.Provider == AIProvider.Local)
            {
                var newKey = ModernUiKit.DrawModernTextField(keyCellRect, cfg.ApiKey, UiText.Get("GCFAgent_LocalKeyPlaceholder"), 6);
                if (newKey != cfg.ApiKey)
                {
                    cfg.ApiKey = newKey;
                    settings.SyncActiveConfigToPrimaryFields();
                }
            }
            else
            {
                var newKey = _showPlainApiKeys
                    ? ModernUiKit.DrawModernTextField(keyCellRect, cfg.ApiKey, UiText.Get("GCFAgent_ApiKeyPlaceholder"), 6)
                    : ModernUiKit.DrawModernPasswordField(keyCellRect, cfg.ApiKey, 6);
                if (newKey != cfg.ApiKey)
                {
                    cfg.ApiKey = newKey;
                    settings.SyncActiveConfigToPrimaryFields();
                }
            }

            if (cfg.Provider == AIProvider.Player2)
            {
                var (isLocalRunning, localUrl) = Player2Support.GetLocalAppStatusCached();
                var modelBadgeRect = new Rect(colModelX, line1Y, colModelW, 28f);
                ModernUiKit.DrawRoundedCard(modelBadgeRect, ModernUiKit.BgInputWell, ModernUiKit.BorderSubtle, 6, 1);

                string statusLabel;
                Color statusColor;
                if (isLocalRunning)
                {
                    statusLabel = UiText.Get("GCFAgent_Player2ClientConnected");
                    statusColor = ModernUiKit.AccentEmerald;
                }
                else if (Player2Support.IsAuthenticating)
                {
                    statusLabel = string.IsNullOrWhiteSpace(Player2Support.AuthStatusText)
                        ? UiText.Get("GCFAgent_Player2AwaitingWebConfirm")
                        : Player2Support.AuthStatusText;
                    statusColor = ModernUiKit.AccentAmber;
                }
                else if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                {
                    statusLabel = UiText.Get("GCFAgent_Player2WebAuthReady");
                    statusColor = ModernUiKit.AccentCyan;
                }
                else
                {
                    statusLabel = UiText.Get("GCFAgent_Player2StartClientHint");
                    statusColor = ModernUiKit.TextMuted;
                }

                ModernUiKit.DrawSingleLineLabel(
                    new Rect(modelBadgeRect.x + 8f, modelBadgeRect.y, modelBadgeRect.width - 16f, modelBadgeRect.height),
                    statusLabel,
                    GameFont.Tiny,
                    statusColor,
                    TextAnchor.MiddleLeft,
                    isLocalRunning ? UiText.Format("GCFAgent_Player2LocalEndpoint", localUrl) : UiText.Get("GCFAgent_Player2AutoModelTip"));
            }
            else
            {
                var isFetchingModels = EnsureRowModelsAutoFetched(settings, cfg);
                var cachedModels = AIProviderRegistry.GetCachedModels(cfg.Provider, cfg.CustomUrl);
                var modelInputRect = new Rect(colModelX, line1Y, colModelW, 28f);

                var pickTriggerRect = new Rect(modelInputRect.xMax - 24f, modelInputRect.y, 24f, modelInputRect.height);
                if (cachedModels.Count > 0 && Event.current.type == EventType.MouseDown &&
                    ((Event.current.button == 0 && pickTriggerRect.Contains(Event.current.mousePosition)) ||
                     (Event.current.button == 1 && modelInputRect.Contains(Event.current.mousePosition))))
                {
                    Event.current.Use();
                    OpenAutoFetchedModelsMenu(settings, cfg, cachedModels);
                }

                var currentModelVal = cfg.GetResolvedModel();
                var modelPlaceholder = isFetchingModels
                    ? UiText.Get("GCFAgent_ModelFetchingPlaceholder")
                    : (cachedModels.Count > 0 ? UiText.Get("GCFAgent_ModelPickPlaceholder") : UiText.Get("GCFAgent_ModelInputPlaceholder"));
                var newModel = ModernUiKit.DrawModernTextField(modelInputRect, currentModelVal, modelPlaceholder, 6);
                if (newModel != currentModelVal)
                {
                    cfg.SelectedModel = newModel;
                    cfg.CustomModelName = newModel;
                    settings.SyncActiveConfigToPrimaryFields();
                }

                if (cachedModels.Count > 0)
                {
                    ModernUiKit.DrawSingleLineLabel(
                        pickTriggerRect,
                        "▾",
                        GameFont.Tiny,
                        Mouse.IsOver(pickTriggerRect) ? ModernUiKit.AccentCyan : ModernUiKit.TextMuted,
                        TextAnchor.MiddleCenter,
                        UiText.Format("GCFAgent_ModelFetchedTip", cachedModels.Count.ToString()));
                }
            }

            var upRect = new Rect(colActionsX, line1Y, 26f, 28f);
            var downRect = new Rect(colActionsX + 29f, line1Y, 26f, 28f);
            var delRect = new Rect(colActionsX + 58f, line1Y, 26f, 28f);

            if (i > 0 && ModernUiKit.DrawButton(upRect, "▲", ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                (settings.ApiConfigs[i - 1], settings.ApiConfigs[i]) = (settings.ApiConfigs[i], settings.ApiConfigs[i - 1]);
                settings.SyncActiveConfigToPrimaryFields();
                break;
            }

            if (i + 1 < settings.ApiConfigs.Count && ModernUiKit.DrawButton(downRect, "▼", ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                (settings.ApiConfigs[i + 1], settings.ApiConfigs[i]) = (settings.ApiConfigs[i], settings.ApiConfigs[i + 1]);
                settings.SyncActiveConfigToPrimaryFields();
                break;
            }

            if (settings.ApiConfigs.Count > 1 && ModernUiKit.DrawButton(delRect, "×", ModernButtonVariant.Danger, 6, false, GameFont.Tiny))
            {
                settings.ApiConfigs.RemoveAt(i);
                settings.SyncActiveConfigToPrimaryFields();
                break;
            }

            if (hasCustomUrl)
            {
                var line2Y = curY + 38f;
                var urlLabelW = 92f;
                ModernUiKit.DrawSingleLineLabel(
                    new Rect(colProvX, line2Y, urlLabelW, 26f),
                    UiText.Get("GCFAgent_UrlFieldLabel"),
                    GameFont.Tiny,
                    ModernUiKit.TextSecondary);

                var urlFieldW = Mathf.Max(120f, colActionsX - (colProvX + urlLabelW) - 6f);
                var urlFieldRect = new Rect(colProvX + urlLabelW, line2Y, urlFieldW, 26f);
                var placeholder = cfg.Provider == AIProvider.Local
                    ? UiText.Get("GCFAgent_UrlPlaceholderLocal")
                    : UiText.Get("GCFAgent_UrlPlaceholderCustom");

                var urlCtrlName = $"GCFAgent_UrlField_{i}";
                var newUrl = ModernUiKit.DrawModernTextField(urlFieldRect, cfg.CustomUrl, placeholder, 6, urlCtrlName);
                if (newUrl != cfg.CustomUrl)
                {
                    cfg.CustomUrl = newUrl;
                    settings.SyncActiveConfigToPrimaryFields();
                }

                var isFocused = string.Equals(GUI.GetNameOfFocusedControl(), urlCtrlName, StringComparison.Ordinal);
                var pressedEnter = isFocused && Event.current.type == EventType.KeyDown &&
                                   (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                if ((!isFocused || pressedEnter) && !string.IsNullOrWhiteSpace(cfg.CustomUrl))
                {
                    var normalized = AIProviderRegistry.NormalizeChatCompletionsEndpoint(cfg.CustomUrl);
                    if (!string.Equals(normalized, cfg.CustomUrl, StringComparison.Ordinal))
                    {
                        cfg.CustomUrl = normalized;
                        settings.SyncActiveConfigToPrimaryFields();
                    }
                }
            }

            curY += rowH + 6f;
        }

        // 4. Dedicated Image Generation Endpoint Section (Endpoint / Model / ApiKey)
        curY += 6f;
        var imgCardH = 136f;
        var imgCardRect = new Rect(4f, curY, width - 8f, imgCardH);
        ModernUiKit.DrawRoundedCard(imgCardRect, ModernUiKit.BgCardAssistantHeader, ModernUiKit.BorderCard, 7, 1);

        ModernUiKit.DrawSingleLineLabel(
            new Rect(imgCardRect.x + 12f, imgCardRect.y + 6f, imgCardRect.width - 24f, 22f),
            UiText.Get("GCFAgent_ImageApiHeader"),
            GameFont.Tiny,
            ModernUiKit.AccentCyan);

        const float imgLabelW = 112f;
        var imgInputX = imgCardRect.x + 12f + imgLabelW;
        var imgInputW = Mathf.Max(140f, imgCardRect.width - imgLabelW - 24f);

        // Row 1: Image Base URL
        var imgRow1Y = imgCardRect.y + 32f;
        ModernUiKit.DrawSingleLineLabel(
            new Rect(imgCardRect.x + 12f, imgRow1Y, imgLabelW, 28f),
            UiText.Get("GCFAgent_ImageEndpointLabel"),
            GameFont.Tiny,
            ModernUiKit.TextSecondary);
        var newImgEndpoint = ModernUiKit.DrawModernTextField(
            new Rect(imgInputX, imgRow1Y, imgInputW, 28f),
            settings.ImageApiEndpoint ?? string.Empty,
            UiText.Get("GCFAgent_ImageEndpointPlaceholder"),
            6,
            "GCFAgent_ImageEndpoint");
        if (!string.Equals(newImgEndpoint, settings.ImageApiEndpoint, StringComparison.Ordinal))
        {
            settings.ImageApiEndpoint = newImgEndpoint;
        }

        // Row 2: Image Model ID
        var imgRow2Y = imgRow1Y + 34f;
        ModernUiKit.DrawSingleLineLabel(
            new Rect(imgCardRect.x + 12f, imgRow2Y, imgLabelW, 28f),
            UiText.Get("GCFAgent_ImageModelLabel"),
            GameFont.Tiny,
            ModernUiKit.TextSecondary);
        var newImgModel = ModernUiKit.DrawModernTextField(
            new Rect(imgInputX, imgRow2Y, imgInputW, 28f),
            settings.ImageModelName ?? string.Empty,
            UiText.Get("GCFAgent_ImageModelPlaceholder"),
            6,
            "GCFAgent_ImageModel");
        if (!string.Equals(newImgModel, settings.ImageModelName, StringComparison.Ordinal))
        {
            settings.ImageModelName = newImgModel;
        }

        // Row 3: Image API Key
        var imgRow3Y = imgRow2Y + 34f;
        ModernUiKit.DrawSingleLineLabel(
            new Rect(imgCardRect.x + 12f, imgRow3Y, imgLabelW, 28f),
            UiText.Get("GCFAgent_ImageApiKeyLabel"),
            GameFont.Tiny,
            ModernUiKit.TextSecondary);
        var imgKeyRect = new Rect(imgInputX, imgRow3Y, imgInputW, 28f);
        var newImgKey = _showPlainApiKeys
            ? ModernUiKit.DrawModernTextField(imgKeyRect, settings.ImageApiKey ?? string.Empty, UiText.Get("GCFAgent_ImageApiKeyPlaceholder"), 6, "GCFAgent_ImageKey")
            : ModernUiKit.DrawModernPasswordField(imgKeyRect, settings.ImageApiKey ?? string.Empty, 6, "GCFAgent_ImageKey_Pw");
        if (!string.Equals(newImgKey, settings.ImageApiKey, StringComparison.Ordinal))
        {
            settings.ImageApiKey = newImgKey;
        }

        curY += imgCardH + 8f;

        // 5. Footer Note
        var noteRect = new Rect(8f, curY + 2f, width - 16f, 24f);
        ModernUiKit.DrawSingleLineLabel(
            noteRect,
            UiText.Get("GCFAgent_FailoverFooterNote"),
            GameFont.Tiny,
            ModernUiKit.TextMuted);
    }

    private static void DrawSystemPromptTab(Rect viewRect, AgentSettings settings, AgentSession? session)
    {
        var curY = 4f;
        var width = viewRect.width;

        var contextCard = new Rect(4f, curY, width - 8f, 98f);
        ModernUiKit.DrawRoundedCard(contextCard, ModernUiKit.BgTableRowEven, ModernUiKit.BorderSubtle, 7, 1);
        var ask = settings.AskConversationHistory;
        var defaultHistory = settings.DefaultConversationHistory;
        var changed = ModernUiKit.DrawModernCheckbox(new Rect(14f, curY + 5f, width - 28f, 28f),
            UiText.Get("GCFAgent_SettingAskContext"), ref ask, UiText.Get("GCFAgent_SettingAskContextTip"), GameFont.Tiny);
        changed |= ModernUiKit.DrawModernCheckbox(new Rect(14f, curY + 35f, width - 28f, 28f),
            UiText.Get("GCFAgent_SettingDefaultContext"), ref defaultHistory, UiText.Get("GCFAgent_SettingDefaultContextTip"), GameFont.Tiny);
        ModernUiKit.DrawSingleLineLabel(new Rect(14f, curY + 67f, width - 28f, 24f),
            UiText.Get("GCFAgent_SettingContextNote"), GameFont.Tiny, ModernUiKit.TextMuted);
        if (changed)
        {
            settings.AskConversationHistory = ask;
            settings.DefaultConversationHistory = defaultHistory;
            settings.SaveToUserConfig();
        }
        curY += 106f;

        if (session != null)
        {
            session.RecalculateTokenTotals();
            var statRect = new Rect(4f, curY, width - 8f, 52f);
            ModernUiKit.DrawRoundedCard(statRect, ModernUiKit.BgCardAssistantHeader, ModernUiKit.BorderCard, 7, 1);

            var line1 =
                UiText.Format("GCFAgent_UsageLine1", UiText.Render(session.Title), session.TotalTokens.ToString("N0"), session.PeakContextTokens.ToString("N0"), session.CacheHitRate.ToString("P1"));
            var line2 =
                UiText.Format("GCFAgent_UsageLine2",
                    session.TotalPromptTokens.ToString("N0"), session.TotalCachedPromptTokens.ToString("N0"), session.TotalUncachedPromptTokens.ToString("N0"), session.TotalCompletionTokens.ToString("N0"));
            ModernUiKit.DrawSingleLineLabel(
                new Rect(statRect.x + 12f, statRect.y + 5f, statRect.width - 130f, 22f),
                line1,
                GameFont.Small,
                ModernUiKit.AccentCyan,
                tooltip: line1 + "\n" + line2);
            ModernUiKit.DrawSingleLineLabel(
                new Rect(statRect.x + 12f, statRect.y + 26f, statRect.width - 130f, 20f),
                line2,
                GameFont.Tiny,
                ModernUiKit.TextSecondary,
                tooltip: line2);

            var resetStatBtn = new Rect(statRect.xMax - 110f, statRect.y + 12f, 100f, 28f);
            if (ModernUiKit.DrawButton(resetStatBtn, UiText.Get("GCFAgent_UsageResetStats"), ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                lock (session.SyncRoot)
                {
                    foreach (var m in session.Messages)
                    {
                        m.PromptTokens = 0;
                        m.CompletionTokens = 0;
                        m.TotalTokens = 0;
                        m.CachedPromptTokens = 0;
                        m.PeakContextTokens = 0;
                        m.LlmRounds = 0;
                    }
                    session.RecalculateTokenTotals();
                }
                AgentSessionManager.QueueSave();
            }

            curY += 58f;
        }

        var promptCard = new Rect(4f, curY, width - 8f, 86f);
        ModernUiKit.DrawRoundedCard(promptCard, ModernUiKit.BgTableRowEven, ModernUiKit.BorderSubtle, 7, 1);
        if (ModernUiKit.DrawButton(new Rect(16f, curY + 12f, 160f, 32f), UiText.Get("GCFAgent_PromptsOpen"), ModernButtonVariant.Primary, 6))
            Dialog_SystemPrompts.Open(settings);
        ModernUiKit.DrawSingleLineLabel(new Rect(190f, curY + 12f, width - 206f, 32f),
            UiText.Get("GCFAgent_PromptsOpenDesc"), GameFont.Small, ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(new Rect(16f, curY + 50f, width - 32f, 24f),
            UiText.Get("GCFAgent_PromptsOpenNote"), GameFont.Tiny, ModernUiKit.TextMuted);
    }

    private static void DrawPlayer2AuthCell(Rect rect, AgentSettings settings, ApiConfigEntry cfg)
    {
        var (isLocalRunning, localEndpoint) = Player2Support.GetLocalAppStatusCached();
        var isAuthenticating = Player2Support.IsAuthenticating;

        if (_showPlainApiKeys && !isAuthenticating)
        {
            var menuBtnW = 72f;
            var inputW = Mathf.Max(40f, rect.width - menuBtnW - 4f);
            var inputRect = new Rect(rect.x, rect.y, inputW, rect.height);
            var newKey = ModernUiKit.DrawModernTextField(inputRect, cfg.ApiKey, UiText.Get("GCFAgent_Player2KeyPlaceholder"), 6);
            if (newKey != cfg.ApiKey)
            {
                cfg.ApiKey = newKey;
                settings.SyncActiveConfigToPrimaryFields();
            }

            var menuRect = new Rect(inputRect.xMax + 4f, rect.y, menuBtnW, rect.height);
            if (ModernUiKit.DrawButton(menuRect, UiText.Get("GCFAgent_Player2AuthMenu") + " ▾", ModernButtonVariant.AccentPill, 6, false, GameFont.Tiny))
            {
                OpenPlayer2AuthMenu(settings, cfg);
            }
            return;
        }

        if (isAuthenticating)
        {
            var cancelBtnW = 68f;
            var statusBtnW = Mathf.Max(60f, rect.width - cancelBtnW - 4f);
            var statusBtnRect = new Rect(rect.x, rect.y, statusBtnW, rect.height);
            if (ModernUiKit.DrawButton(statusBtnRect, UiText.Get("GCFAgent_Player2WaitConfirm") + " ▾", ModernButtonVariant.AccentPill, 6, true, GameFont.Tiny))
            {
                OpenPlayer2AuthMenu(settings, cfg);
            }
            TooltipHandler.TipRegion(statusBtnRect, UiText.Get("GCFAgent_Player2WaitConfirmTip"));

            var cancelRect = new Rect(statusBtnRect.xMax + 4f, rect.y, cancelBtnW, rect.height);
            if (ModernUiKit.DrawButton(cancelRect, UiText.Get("GCFAgent_CommonCancel"), ModernButtonVariant.Danger, 6, false, GameFont.Tiny))
            {
                Player2Support.CancelAuth();
                SetBanner(UiText.Get("GCFAgent_Player2WebAuthCancelled"), isError: false);
            }
            return;
        }

        if (isLocalRunning)
        {
            var menuBtnW = 78f;
            var badgeW = Mathf.Max(60f, rect.width - menuBtnW - 4f);
            var badgeRect = new Rect(rect.x, rect.y, badgeW, rect.height);
            if (ModernUiKit.DrawButton(badgeRect, UiText.Get("GCFAgent_Player2ClientConnected"), ModernButtonVariant.AccentPill, 6, true, GameFont.Tiny))
            {
                OpenPlayer2AuthMenu(settings, cfg);
            }
            TooltipHandler.TipRegion(badgeRect, UiText.Format("GCFAgent_Player2ClientConnectedTip", localEndpoint));

            var menuRect = new Rect(badgeRect.xMax + 4f, rect.y, menuBtnW, rect.height);
            if (ModernUiKit.DrawButton(menuRect, UiText.Get("GCFAgent_Player2WebAuth") + " ▾", ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                OpenPlayer2AuthMenu(settings, cfg);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            var detectBtnW = 74f;
            var authBtnW = Mathf.Max(60f, rect.width - detectBtnW - 4f);
            var authBtnRect = new Rect(rect.x, rect.y, authBtnW, rect.height);
            if (ModernUiKit.DrawButton(authBtnRect, "✓ " + UiText.Get("GCFAgent_Player2WebAuthed") + " ▾", ModernButtonVariant.AccentPill, 6, false, GameFont.Tiny))
            {
                OpenPlayer2AuthMenu(settings, cfg);
            }
            TooltipHandler.TipRegion(authBtnRect, UiText.Get("GCFAgent_Player2WebAuthedTip"));

            var detectRect = new Rect(authBtnRect.xMax + 4f, rect.y, detectBtnW, rect.height);
            if (ModernUiKit.DrawButton(detectRect, UiText.Get("GCFAgent_Player2DetectClient"), ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                TriggerPlayer2LocalCheck(settings, cfg);
            }
            return;
        }

        var checkLocalW = 76f;
        var primaryAuthW = Mathf.Max(60f, rect.width - checkLocalW - 4f);
        var primaryAuthRect = new Rect(rect.x, rect.y, primaryAuthW, rect.height);
        if (ModernUiKit.DrawButton(primaryAuthRect, UiText.Get("GCFAgent_Player2OneClickAuth"), ModernButtonVariant.Primary, 6, false, GameFont.Tiny))
        {
            if (Event.current.button == 1)
            {
                OpenPlayer2AuthMenu(settings, cfg);
            }
            else
            {
                TriggerPlayer2WebLogin(settings, cfg);
            }
        }
        TooltipHandler.TipRegion(primaryAuthRect, UiText.Get("GCFAgent_Player2OneClickAuthTip"));

        var checkLocalRect = new Rect(primaryAuthRect.xMax + 4f, rect.y, checkLocalW, rect.height);
        if (ModernUiKit.DrawButton(checkLocalRect, UiText.Get("GCFAgent_Player2DetectClient"), ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
        {
            TriggerPlayer2LocalCheck(settings, cfg);
        }
        TooltipHandler.TipRegion(checkLocalRect, UiText.Get("GCFAgent_Player2DetectTip"));
    }

    private static void OpenPlayer2AuthMenu(AgentSettings settings, ApiConfigEntry cfg)
    {
        var options = new List<FloatMenuOption>();
        if (Player2Support.IsAuthenticating)
        {
            options.Add(new FloatMenuOption(UiText.Get("GCFAgent_Player2ReopenAuthPage"), Player2Support.ReopenApprovalPage));
            options.Add(new FloatMenuOption(UiText.Get("GCFAgent_Player2CancelAuth"), () =>
            {
                Player2Support.CancelAuth();
                SetBanner(UiText.Get("GCFAgent_Player2WebAuthCancelled"));
            }));
        }
        else
        {
            options.Add(new FloatMenuOption(UiText.Get("GCFAgent_Player2StartWebAuth"), () => TriggerPlayer2WebLogin(settings, cfg)));
            options.Add(new FloatMenuOption(UiText.Get("GCFAgent_Player2DetectNow"), () => TriggerPlayer2LocalCheck(settings, cfg)));
            options.Add(new FloatMenuOption(_showPlainApiKeys ? UiText.Get("GCFAgent_Player2HideKeyInput") : UiText.Get("GCFAgent_Player2ShowKeyInput"), () =>
            {
                _showPlainApiKeys = !_showPlainApiKeys;
            }));
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                options.Add(new FloatMenuOption(UiText.Get("GCFAgent_Player2ClearKey"), () =>
                {
                    cfg.ApiKey = string.Empty;
                    settings.SyncActiveConfigToPrimaryFields();
                    SetBanner(UiText.Get("GCFAgent_Player2KeyCleared"));
                }));
            }
        }

        options.Add(new FloatMenuOption(UiText.Get("GCFAgent_Player2OpenWebsite"), Player2Support.OpenWebsite));
        Find.WindowStack?.Add(new FloatMenu(options));
    }

    private static void TriggerPlayer2LocalCheck(AgentSettings settings, ApiConfigEntry cfg)
    {
        SetBanner(UiText.Get("GCFAgent_Player2Detecting"), isError: false);
        _ = Task.Run(async () =>
        {
            var local = await Player2Support.TryGetLocalConnectionAsync().ConfigureAwait(false);
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                if (local.HasValue)
                {
                    settings.SyncActiveConfigToPrimaryFields();
                    SetBanner(UiText.Format("GCFAgent_Player2LocalConnected", local.Value.EndpointUrl), isError: false);
                }
                else
                {
                    SetBanner(UiText.Get("GCFAgent_Player2NotDetected"), isError: true);
                }
                return true;
            }).ConfigureAwait(false);
        });
    }

    private static void DrawBottomActionBar(Rect rect, AgentSettings settings, Action? onCloseDrawer)
    {
        ModernUiKit.DrawRoundedCard(rect, ModernUiKit.BgHeader, ModernUiKit.BorderSubtle, 7, 1);

        var saveBtnW = 104f;
        var testBtnW = 92f;
        var closeBtnW = onCloseDrawer != null ? 76f : 0f;

        var rightX = rect.xMax - 8f;
        if (onCloseDrawer != null)
        {
            rightX -= closeBtnW;
            if (ModernUiKit.DrawButton(new Rect(rightX, rect.y + 5f, closeBtnW, 28f), UiText.Get("GCFAgent_BannerCollapse"), ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                onCloseDrawer();
            }
            rightX -= 6f;
        }

        rightX -= saveBtnW;
        if (ModernUiKit.DrawButton(new Rect(rightX, rect.y + 5f, saveBtnW, 28f), UiText.Get("GCFAgent_BannerSavePersist"), ModernButtonVariant.Primary, 6, false, GameFont.Tiny))
        {
            settings.SaveToUserConfig();
            if (string.IsNullOrEmpty(settings.ConfigurationError))
            {
                SetBanner(UiText.Get("GCFAgent_BannerSaved"), isError: false);
            }
            else
            {
                SetBanner(UiText.Format("GCFAgent_BannerSavedWithWarning", UiText.Render(settings.ConfigurationError)), isError: true);
            }
        }

        rightX -= testBtnW + 6f;
        if (ModernUiKit.DrawButton(new Rect(rightX, rect.y + 5f, testBtnW, 28f), UiText.Get("GCFAgent_BannerValidate"), ModernButtonVariant.AccentPill, 6, false, GameFont.Tiny))
        {
            try
            {
                var conn = settings.CaptureConnection();
                AgentSettings.Validate(conn);
                SetBanner(UiText.Format("GCFAgent_BannerValidateOk", conn.Provider.ShortLabel(), conn.Model, conn.Endpoint), isError: false);
            }
            catch (Exception ex)
            {
                SetBanner(UiText.Format("GCFAgent_BannerValidateFailed", UiText.Render(ex.Message)), isError: true);
            }
        }

        var statusW = Mathf.Max(80f, rightX - rect.x - 16f);
        var statusRect = new Rect(rect.x + 10f, rect.y + 5f, statusW, 28f);
        var msg = !string.IsNullOrEmpty(_statusBannerText)
            ? _statusBannerText
            : (!string.IsNullOrEmpty(settings.ConfigurationError)
                ? UiText.Render(settings.ConfigurationError)
                : UiText.Format("GCFAgent_BannerProviderCount", settings.ApiConfigs.Count(c => c.IsEnabled).ToString()));
        var isErr = !string.IsNullOrEmpty(_statusBannerText) ? _statusBannerIsError : !string.IsNullOrEmpty(settings.ConfigurationError);
        var color = isErr ? ModernUiKit.AccentDanger : (!string.IsNullOrEmpty(_statusBannerText) ? ModernUiKit.AccentEmerald : ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(statusRect, msg, GameFont.Tiny, color, TextAnchor.MiddleLeft, msg);
    }

    private static void OpenProviderMenu(AgentSettings settings, ApiConfigEntry cfg)
    {
        var options = new List<FloatMenuOption>();
        foreach (var provider in AIProviderRegistry.ConfigurableProviders)
        {
            var p = provider;
            options.Add(new FloatMenuOption(p.Label(), () =>
            {
                cfg.Provider = p;
                cfg.CustomUrl = p == AIProvider.Local ? "http://127.0.0.1:11434/v1/chat/completions" : string.Empty;
                cfg.SelectedModel = string.Empty;
                cfg.CustomModelName = string.Empty;
                if (p == AIProvider.Player2)
                {
                    _ = Player2Support.RefreshLocalAppStatusAsync();
                }
                settings.SyncActiveConfigToPrimaryFields();
            }));
        }

        Find.WindowStack?.Add(new FloatMenu(options));
    }

    private static void OpenAutoFetchedModelsMenu(AgentSettings settings, ApiConfigEntry cfg, IReadOnlyList<string> models)
    {
        var options = new List<FloatMenuOption>();
        foreach (var modelId in models)
        {
            var m = modelId;
            options.Add(new FloatMenuOption(m, () =>
            {
                cfg.SelectedModel = m;
                cfg.CustomModelName = m;
                settings.SyncActiveConfigToPrimaryFields();
            }));
        }

        if (options.Count > 0)
        {
            Find.WindowStack?.Add(new FloatMenu(options));
        }
    }

    private static bool EnsureRowModelsAutoFetched(AgentSettings settings, ApiConfigEntry cfg)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || !cfg.IsEnabled || cfg.Provider is AIProvider.Player2 or AIProvider.None)
        {
            return false;
        }

        var endpoint = cfg.GetResolvedEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.IsWellFormedUriString(endpoint, UriKind.Absolute))
        {
            return false;
        }

        var trimmedKey = (cfg.ApiKey ?? string.Empty).Trim();
        if (cfg.Provider.RequiresApiKey() && cfg.Provider != AIProvider.Custom && string.IsNullOrWhiteSpace(trimmedKey))
        {
            return false;
        }

        if (!_rowAutoFetchStates.TryGetValue(cfg, out var state))
        {
            state = new RowAutoFetchState();
            _rowAutoFetchStates[cfg] = state;
        }

        var signature = $"{cfg.Provider}|{trimmedKey}|{endpoint}";
        if (!string.Equals(state.ObservedSignature, signature, StringComparison.Ordinal))
        {
            state.ObservedSignature = signature;
            state.LastChangedRealtime = Time.realtimeSinceStartup;
        }

        if (state.IsFetching)
        {
            return true;
        }

        if (string.Equals(state.FetchedSignature, signature, StringComparison.Ordinal))
        {
            return false;
        }

        if (Time.realtimeSinceStartup - state.LastChangedRealtime < 0.6f)
        {
            return false;
        }

        state.FetchedSignature = signature;
        state.IsFetching = true;

        var provider = cfg.Provider;
        var customUrl = cfg.CustomUrl;

        _ = Task.Run(async () =>
        {
            try
            {
                var models = await AIProviderRegistry.FetchModelsAsync(provider, trimmedKey, customUrl, 10).ConfigureAwait(false);
                if (models.Count == 0 && provider == AIProvider.Local)
                {
                    var foundLocal = await AIProviderRegistry.ScanLocalServicesAsync().ConfigureAwait(false);
                    if (foundLocal.Count > 0)
                    {
                        var first = foundLocal[0];
                        await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                        {
                            state.IsFetching = false;
                            cfg.CustomUrl = first.EndpointUrl;
                            if (string.IsNullOrWhiteSpace(cfg.GetResolvedModel()))
                            {
                                cfg.SelectedModel = first.ModelName;
                                cfg.CustomModelName = first.ModelName;
                            }
                            settings.SyncActiveConfigToPrimaryFields();
                            SetBanner(UiText.Format("GCFAgent_BannerLocalServiceFound", first.ServiceName, first.ModelName), isError: false);
                            return true;
                        }).ConfigureAwait(false);
                        return;
                    }
                }

                await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    state.IsFetching = false;
                    if (models.Count > 0)
                    {
                        if (string.IsNullOrWhiteSpace(cfg.GetResolvedModel()))
                        {
                            cfg.SelectedModel = models[0];
                            cfg.CustomModelName = models[0];
                            settings.SyncActiveConfigToPrimaryFields();
                        }
                        SetBanner(UiText.Format("GCFAgent_BannerModelsFetched", provider.ShortLabel(), models.Count.ToString()), isError: false);
                    }
                    return true;
                }).ConfigureAwait(false);
            }
            catch
            {
                await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    state.IsFetching = false;
                    return true;
                }).ConfigureAwait(false);
            }
        });

        return true;
    }

    private static void TriggerPlayer2WebLogin(AgentSettings settings, ApiConfigEntry cfg)
    {
        SetBanner(UiText.Get("GCFAgent_Player2RequestingDeviceAuth"), isError: false);
        Player2Support.StartAuth(
            onSuccess: p2Key =>
            {
                _ = MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    cfg.ApiKey = p2Key;
                    settings.SyncActiveConfigToPrimaryFields();
                    settings.SaveToUserConfig();
                    SetBanner(UiText.Get("GCFAgent_BannerPlayer2AuthSuccess"), isError: false);
                    return true;
                });
            },
            onStatus: status =>
            {
                _ = MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    SetBanner(status, isError: false);
                    return true;
                });
            },
            onError: err =>
            {
                _ = MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    SetBanner(UiText.Format("GCFAgent_BannerPlayer2AuthIncomplete", UiText.Render(err)), isError: true);
                    return true;
                });
            });
    }
}
