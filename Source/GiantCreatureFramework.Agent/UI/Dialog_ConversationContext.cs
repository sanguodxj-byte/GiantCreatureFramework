using GiantCreatureFramework.Agent;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.UI;

/// <summary>Defers the second player request until its conversation context mode is chosen.</summary>
public sealed class Dialog_ConversationContext : Window
{
    private readonly Action<bool, bool> _onChoice;
    private readonly Action _onCancel;
    private bool _remember;
    private bool _resolved;
    public override Vector2 InitialSize => new(540f, 286f);

    public Dialog_ConversationContext(Action<bool, bool> onChoice, Action onCancel)
    {
        _onChoice = onChoice;
        _onCancel = onCancel;
        closeOnAccept = false;
        closeOnCancel = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        doCloseX = true;
    }

    public override void OnAcceptKeyPressed() { }

    public override void DoWindowContents(Rect inRect)
    {
        var font = Text.Font;
        var wrap = Text.WordWrap;
        try
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 30f, 35f), UiText.Get("GCFAgent_ContextDialogTitle"));
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            Widgets.Label(new Rect(0f, 46f, inRect.width, 96f),
                UiText.Get("GCFAgent_ContextDialogBody"));
            ModernUiKit.DrawModernCheckbox(new Rect(0f, 153f, inRect.width, 28f),
                UiText.Get("GCFAgent_ContextDialogRemember"), ref _remember,
                UiText.Get("GCFAgent_ContextDialogRememberTip"), GameFont.Small);
            var y = inRect.height - 38f;
            if (ModernUiKit.DrawButton(new Rect(0f, y, 90f, 32f), UiText.Get("GCFAgent_CommonCancel"), ModernButtonVariant.Ghost)) Close();
            if (ModernUiKit.DrawButton(new Rect(inRect.width - 300f, y, 140f, 32f), UiText.Get("GCFAgent_ContextDisable"), ModernButtonVariant.Secondary)) Choose(false);
            if (ModernUiKit.DrawButton(new Rect(inRect.width - 150f, y, 150f, 32f), UiText.Get("GCFAgent_ContextEnable"), ModernButtonVariant.Primary)) Choose(true);
        }
        finally { Text.Font = font; Text.WordWrap = wrap; }
    }

    /// <summary>Commits one choice; used by both buttons and structured runtime acceptance.</summary>
    public void Choose(bool useHistory, bool? remember = null)
    {
        if (_resolved) return;
        _resolved = true;
        Close();
        _onChoice(useHistory, remember ?? _remember);
    }

    public override void PostClose()
    {
        base.PostClose();
        if (_resolved) return;
        _resolved = true;
        _onCancel();
    }
}
