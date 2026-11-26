using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.UI;

/// <summary>In-game editing of all instruction sections with explicit save and discard.</summary>
public sealed class Dialog_SystemPrompts : Window
{
    private static Dialog_SystemPrompts? _activeInstance;
    private readonly AgentSettings _settings;
    private PromptEditorDraft _draft;
    private AgentPromptSection _section = AgentPromptSection.Main;
    private bool _preview;
    private Vector2 _editorScroll;
    private string _status = "GCFAgent_PromptsInitialStatus";

    public override Vector2 InitialSize => new(900f, 700f);
    protected override float Margin => 0f;

    public Dialog_SystemPrompts(AgentSettings settings)
    {
        _settings = settings;
        _draft = new(settings.CapturePrompts());
        draggable = true;
        doCloseX = false;
        doCloseButton = false;
        doWindowBackground = false;
        closeOnAccept = false;
        closeOnCancel = true;
        forceCatchAcceptAndCancelEventEvenIfUnfocused = true;
        absorbInputAroundWindow = true;
        preventCameraMotion = false;
    }

    public override void OnAcceptKeyPressed() { }

    public static void Open(AgentSettings settings)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled) return;
        if (Find.WindowStack == null || Find.WindowStack.IsOpen<Dialog_SystemPrompts>()) return;
        _activeInstance = new Dialog_SystemPrompts(settings);
        Find.WindowStack.Add(_activeInstance);
    }

    public static void CloseIfOpen()
    {
        if (_activeInstance != null && Find.WindowStack != null && Find.WindowStack.IsOpen(_activeInstance))
        {
            _activeInstance.Close(doCloseSound: false);
        }
        _activeInstance = null;
    }

    public override void PostClose()
    {
        base.PostClose();
        if (ReferenceEquals(_activeInstance, this))
        {
            _activeInstance = null;
        }
    }

    private void SaveChanges()
    {
        _draft.Apply(_settings.SetPrompt, _settings.ResetPrompt);
        if (!_settings.TrySaveToUserConfig())
        {
            _status = UiText.Format("GCFAgent_PromptsSaveIncomplete", UiText.Render(_settings.ConfigurationError));
            return;
        }
        _draft = new(_settings.CapturePrompts());
        _status = UiText.Get("GCFAgent_PromptsSaved");
    }

    public override void DoWindowContents(Rect rect)
    {
        var oldFont = Text.Font;
        var oldColor = GUI.color;
        var oldAnchor = Text.Anchor;
        var oldWrap = Text.WordWrap;
        try
        {
            ModernUiKit.DrawRoundedCard(rect, ModernUiKit.BgWindow, ModernUiKit.BorderWindow, 12, 1);
            ModernUiKit.DrawSingleLineLabel(new Rect(20f, 12f, rect.width - 80f, 30f),
                UiText.Get("GCFAgent_PromptsTitle") + (_draft.IsDirty ? " " + UiText.Get("GCFAgent_PromptsUnsavedMark") : ""), GameFont.Medium, ModernUiKit.TextBright);
            if (ModernUiKit.DrawCloseIconButton(new Rect(rect.width - 46f, 14f, 28f, 28f), 7)) { Close(); return; }

            var definition = AgentPrompts.Definition(_section);
            if (ModernUiKit.DrawButton(new Rect(20f, 58f, 260f, 32f), UiText.Render(definition.Label) + " ▾", ModernButtonVariant.Secondary, 6))
                Find.WindowStack.Add(new FloatMenu(AgentPrompts.Definitions.Select(d => new FloatMenuOption(UiText.Render(d.Label),
                    () => { _section = d.Section; _preview = false; _editorScroll = Vector2.zero; GUI.FocusControl(null); })).ToList()));
            if (ModernUiKit.DrawButton(new Rect(292f, 58f, 128f, 32f), _preview ? UiText.Get("GCFAgent_PromptsBackToEdit") : UiText.Get("GCFAgent_PromptsPreview"), ModernButtonVariant.Secondary, 6))
            { _preview = !_preview; _editorScroll = Vector2.zero; }
            if (ModernUiKit.DrawButton(new Rect(rect.width - 282f, 58f, 124f, 32f), UiText.Get("GCFAgent_PromptsResetSection"), ModernButtonVariant.Secondary, 6))
                _draft.Reset(_section);
            if (ModernUiKit.DrawButton(new Rect(rect.width - 150f, 58f, 130f, 32f), UiText.Get("GCFAgent_PromptsResetAll"), ModernButtonVariant.Secondary, 6))
                foreach (var item in AgentPrompts.Definitions) _draft.Reset(item.Section);

            ModernUiKit.DrawSingleLineLabel(new Rect(20f, 100f, rect.width - 40f, 24f),
                _preview ? UiText.Get("GCFAgent_PromptsPreviewNote") : UiText.Render(definition.Description),
                GameFont.Small, ModernUiKit.TextSecondary);
            var current = _preview ? string.Join("\n\n", AgentPrompts.Definitions.Select(d =>
                UiText.Format("GCFAgent_PromptsPreviewSection", UiText.Render(d.Label), _draft.Get(d.Section)))) : _draft.Get(_section);
            var viewport = new Rect(20f, 136f, rect.width - 40f, rect.height - 242f);
            Text.Font = GameFont.Small;
            var content = new Rect(0f, 0f, viewport.width - 18f,
                Mathf.Max(viewport.height, Text.CalcHeight(current, viewport.width - 42f) + 32f));
            Widgets.BeginScrollView(viewport, ref _editorScroll, content);
            try
            {
                if (_preview) Widgets.Label(content.ContractedBy(10f), current);
                else
                {
                    var edited = ModernUiKit.DrawModernTextArea(content, current,
                        placeholder: UiText.Get("GCFAgent_PromptsPlaceholder"), radius: 8, controlName: "gcf_prompt_" + _section);
                    if (edited != current) _draft.Edit(_section, edited);
                }
            }
            finally { Widgets.EndScrollView(); }

            ModernUiKit.DrawSingleLineLabel(new Rect(20f, rect.height - 96f, rect.width - 40f, 22f),
                UiText.Format("GCFAgent_PromptsContentStats", current.Length.ToString("N0"), TokenEstimator.EstimateTokens(current).ToString("N0")), GameFont.Tiny, ModernUiKit.TextMuted);
            ModernUiKit.DrawSingleLineLabel(new Rect(20f, rect.height - 64f, rect.width - 280f, 42f),
                UiText.Render(_status), GameFont.Tiny, ModernUiKit.TextSecondary, tooltip: UiText.Render(_status));
            if (ModernUiKit.DrawButton(new Rect(rect.width - 250f, rect.height - 62f, 108f, 36f), UiText.Get("GCFAgent_CommonClose"), ModernButtonVariant.Secondary, 6)) Close();
            if (ModernUiKit.DrawButton(new Rect(rect.width - 132f, rect.height - 62f, 112f, 36f), UiText.Get("GCFAgent_PromptsSave"), ModernButtonVariant.Primary, 6)) SaveChanges();
        }
        finally { Text.Font = oldFont; GUI.color = oldColor; Text.Anchor = oldAnchor; Text.WordWrap = oldWrap; }
    }
}
