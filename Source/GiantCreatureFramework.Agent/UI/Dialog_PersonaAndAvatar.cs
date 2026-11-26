using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.UI;

/// <summary>
/// Avatar &amp; persona prompt editor window for Creature Forge.
/// </summary>
public sealed class Dialog_PersonaAndAvatar : Window
{
    private static Dialog_PersonaAndAvatar? _activeInstance;
    private string _statusText = string.Empty;
    private bool _statusIsError;

    private static readonly (string Title, string Prompt)[] PersonaPresets =
    [
        ("GCFAgent_PersonaNone", string.Empty),
        ("GCFAgent_PersonaGentleMaid", "你是殖民地巨兽工坊专属的温柔女仆智囊，称呼玩家为“主人”或“领主大人”。说话温文尔雅、体贴细致，在汇报巨兽骨架与制作进度时也会用轻柔鼓励的语气。"),
        ("GCFAgent_PersonaTsundereAdvisor", "你是一位表面嫌弃、实则极其专业可靠的傲娇首席造兽师，称呼玩家为“笨蛋指挥官”。回答时偶尔吐槽，随后极其严谨地把制作与审计结果列清楚。"),
        ("GCFAgent_PersonaColdCore", "你是远古生物工程遗留的超算核心（代号 C.F.C.），称呼玩家为“授权管理员”。全程使用冷静、简练、富有科幻机械感的汇报口吻。"),
        ("GCFAgent_PersonaOldCaptain", "你是一位在边缘世界见多识广的资深老猎人舰长，称呼玩家为“老伙计”。说话风趣接地气，结合边缘世界经验给出实操建议。")
    ];

    public override Vector2 InitialSize => new(680f, 520f);
    protected override float Margin => 0f;

    public Dialog_PersonaAndAvatar()
    {
        draggable = true;
        resizeable = false;
        doCloseX = false;
        doCloseButton = false;
        doWindowBackground = false;
        drawShadow = false;
        closeOnAccept = false;
        closeOnCancel = true;
        forceCatchAcceptAndCancelEventEvenIfUnfocused = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
        preventCameraMotion = false;
    }

    public override void OnAcceptKeyPressed()
    {
    }

    public static void Open()
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || Find.WindowStack == null)
        {
            return;
        }

        if (_activeInstance != null && Find.WindowStack.IsOpen(_activeInstance))
        {
            Find.WindowStack.TryRemove(_activeInstance, doCloseSound: false);
        }

        _activeInstance = new Dialog_PersonaAndAvatar();
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
        GiantCreatureAgentMod.Settings.SaveToUserConfig();
        if (ReferenceEquals(_activeInstance, this))
        {
            _activeInstance = null;
        }
    }

    public static string AvatarsFolderPath
    {
        get
        {
            var dir = Path.Combine(GenFilePaths.ConfigFolderPath, "GiantCreatureFramework", "Avatars");
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
            }
            return dir;
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevColor = GUI.color;
        var prevWrap = Text.WordWrap;

        try
        {
            var settings = GiantCreatureAgentMod.Settings;
            ModernUiKit.DrawRoundedCard(inRect, ModernUiKit.BgWindow, ModernUiKit.BorderWindow, radius: 12, borderThickness: 1);

            var headerRect = new Rect(inRect.x + 1f, inRect.y + 1f, inRect.width - 2f, 52f);
            ModernUiKit.DrawRoundedRect(headerRect, ModernUiKit.BgHeader, radius: 11);
            ModernUiKit.DrawHorizontalHairline(inRect.x + 1f, headerRect.yMax, inRect.width - 2f, ModernUiKit.BorderSubtle);

            var headerAvatarRect = new Rect(headerRect.x + 16f, headerRect.y + 10f, 32f, 32f);
            ModernUiKit.DrawBrandAvatar(headerAvatarRect, interactive: false);

            ModernUiKit.DrawSingleLineLabel(
                new Rect(headerAvatarRect.xMax + 10f, headerRect.y + 6f, 360f, 22f),
                UiText.Get("GCFAgent_PersonaWindowTitle"),
                GameFont.Small,
                ModernUiKit.TextBright);

            ModernUiKit.DrawSingleLineLabel(
                new Rect(headerAvatarRect.xMax + 10f, headerRect.y + 26f, 420f, 18f),
                UiText.Get("GCFAgent_PersonaWindowSubtitle"),
                GameFont.Tiny,
                ModernUiKit.TextMuted);

            var closeRect = new Rect(headerRect.xMax - 42f, headerRect.y + 12f, 28f, 28f);
            if (ModernUiKit.DrawCloseIconButton(closeRect, radius: 7))
            {
                Close();
                return;
            }

            var curY = headerRect.yMax + 12f;
            var contentX = inRect.x + 16f;
            var contentW = inRect.width - 32f;

            var avatarCardRect = new Rect(contentX, curY, contentW, 116f);
            ModernUiKit.DrawRoundedCard(avatarCardRect, ModernUiKit.BgTableRowEven, ModernUiKit.BorderCard, radius: 8, borderThickness: 1);

            var bigPreviewRect = new Rect(avatarCardRect.x + 18f, avatarCardRect.y + 24f, 68f, 68f);
            ModernUiKit.DrawBrandAvatar(bigPreviewRect, interactive: false);

            var rightX = bigPreviewRect.xMax + 18f;
            var rightW = avatarCardRect.xMax - rightX - 14f;

            var hasCustomAvatar = !string.IsNullOrWhiteSpace(settings.CustomAvatarPath) && File.Exists(settings.CustomAvatarPath);
            ModernUiKit.DrawSingleLineLabel(
                new Rect(rightX, avatarCardRect.y + 10f, rightW, 20f),
                hasCustomAvatar
                    ? UiText.Format("GCFAgent_AvatarCurrentCustom", Path.GetFileName(settings.CustomAvatarPath))
                    : UiText.Get("GCFAgent_AvatarCurrentDefault"),
                GameFont.Tiny,
                hasCustomAvatar ? ModernUiKit.AccentEmerald : ModernUiKit.TextSecondary);

            var pathFieldRect = new Rect(rightX, avatarCardRect.y + 34f, rightW, 28f);
            var newPath = ModernUiKit.DrawModernTextField(
                pathFieldRect,
                settings.CustomAvatarPath ?? string.Empty,
                UiText.Get("GCFAgent_AvatarPathPlaceholder"),
                6);
            if (!string.Equals(newPath, settings.CustomAvatarPath, StringComparison.Ordinal))
            {
                settings.CustomAvatarPath = newPath.Trim().Trim('"');
                ModernUiKit.InvalidateAvatarCache();
            }

            var btnY = avatarCardRect.y + 72f;
            var browseBtnRect = new Rect(rightX, btnY, 126f, 28f);
            if (ModernUiKit.DrawButton(browseBtnRect, UiText.Get("GCFAgent_AvatarBrowse"), ModernButtonVariant.Primary, 6, false, GameFont.Tiny))
            {
                BrowseAndImportAvatar(settings);
            }

            var folderMenuRect = new Rect(browseBtnRect.xMax + 8f, btnY, 124f, 28f);
            if (ModernUiKit.DrawButton(folderMenuRect, UiText.Get("GCFAgent_AvatarFolderMenu") + " ▾", ModernButtonVariant.Secondary, 6, false, GameFont.Tiny))
            {
                OpenAvatarFolderMenu(settings);
            }

            var resetAvatarRect = new Rect(folderMenuRect.xMax + 8f, btnY, 106f, 28f);
            if (ModernUiKit.DrawButton(resetAvatarRect, UiText.Get("GCFAgent_AvatarReset"), ModernButtonVariant.Pill, 6, false, GameFont.Tiny))
            {
                settings.CustomAvatarPath = string.Empty;
                ModernUiKit.InvalidateAvatarCache();
                settings.SaveToUserConfig();
                SetStatus(UiText.Get("GCFAgent_AvatarResetDone"), isError: false);
            }

            curY = avatarCardRect.yMax + 14f;

            var personaHeaderRect = new Rect(contentX, curY, contentW, 24f);
            var hasPersona = !string.IsNullOrWhiteSpace(settings.PersonaPrompt);
            var personaToks = TokenEstimator.EstimateTokens(settings.PersonaPrompt);
            ModernUiKit.DrawSingleLineLabel(
                new Rect(personaHeaderRect.x + 2f, personaHeaderRect.y, 280f, 22f),
                hasPersona
                    ? UiText.Format("GCFAgent_PersonaHeaderActive", settings.PersonaPrompt.Length.ToString(), personaToks.ToString())
                    : UiText.Get("GCFAgent_PersonaHeaderIdle"),
                GameFont.Tiny,
                hasPersona ? ModernUiKit.AccentCyan : ModernUiKit.TextSecondary);

            curY += 24f;

            var presetY = curY;
            var presetX = contentX;
            ModernUiKit.DrawSingleLineLabel(new Rect(presetX + 2f, presetY, 64f, 26f), UiText.Get("GCFAgent_PersonaQuickPresets"), GameFont.Tiny, ModernUiKit.TextMuted);
            presetX += 66f;

            foreach (var (title, prompt) in PersonaPresets)
            {
                var titleDisplay = UiText.Get(title);
                var isMatched = string.IsNullOrWhiteSpace(prompt)
                    ? string.IsNullOrWhiteSpace(settings.PersonaPrompt)
                    : string.Equals(settings.PersonaPrompt?.Trim(), prompt, StringComparison.Ordinal);
                var pillW = Mathf.Clamp(titleDisplay.Length * 13f + 20f, 76f, 112f);
                if (presetX + pillW > contentX + contentW)
                {
                    break;
                }

                var pillRect = new Rect(presetX, presetY, pillW, 26f);
                if (ModernUiKit.DrawButton(
                    pillRect,
                    titleDisplay,
                    isMatched ? ModernButtonVariant.AccentPill : ModernButtonVariant.Pill,
                    6,
                    isMatched,
                    GameFont.Tiny))
                {
                    settings.PersonaPrompt = prompt;
                    SetStatus(string.IsNullOrEmpty(prompt) ? UiText.Get("GCFAgent_PersonaCleared") : UiText.Format("GCFAgent_PersonaApplied", titleDisplay), isError: false);
                }
                presetX += pillW + 6f;
            }

            curY += 32f;

            var bottomBarH = 44f;
            var editorH = Mathf.Max(110f, inRect.yMax - bottomBarH - curY - 12f);
            var editorOuterRect = new Rect(contentX, curY, contentW, editorH);
            var editedPersona = ModernUiKit.DrawModernTextArea(
                editorOuterRect,
                settings.PersonaPrompt ?? string.Empty,
                placeholder: UiText.Get("GCFAgent_PersonaEditorPlaceholder"),
                radius: 8,
                controlName: "gcf_persona_prompt_editor");
            if (!string.Equals(editedPersona, settings.PersonaPrompt, StringComparison.Ordinal))
            {
                settings.PersonaPrompt = editedPersona;
            }

            var bottomRect = new Rect(inRect.x + 12f, inRect.yMax - bottomBarH - 6f, inRect.width - 24f, bottomBarH - 4f);
            ModernUiKit.DrawRoundedCard(bottomRect, ModernUiKit.BgHeader, ModernUiKit.BorderSubtle, radius: 8, borderThickness: 1);

            var saveBtnW = 108f;
            var saveBtnRect = new Rect(bottomRect.xMax - saveBtnW - 8f, bottomRect.y + 6f, saveBtnW, 28f);
            if (ModernUiKit.DrawButton(saveBtnRect, UiText.Get("GCFAgent_PersonaSaveDone"), ModernButtonVariant.Primary, 6, false, GameFont.Tiny))
            {
                ModernUiKit.InvalidateAvatarCache();
                settings.SaveToUserConfig();
                Close();
                return;
            }

            var statusLabelW = Mathf.Max(100f, saveBtnRect.x - bottomRect.x - 18f);
            var statusMsg = !string.IsNullOrEmpty(_statusText)
                ? _statusText
                : UiText.Get("GCFAgent_PersonaStatusHint");
            var statusColor = !string.IsNullOrEmpty(_statusText)
                ? (_statusIsError ? ModernUiKit.AccentDanger : ModernUiKit.AccentEmerald)
                : ModernUiKit.TextSecondary;
            ModernUiKit.DrawSingleLineLabel(
                new Rect(bottomRect.x + 12f, bottomRect.y + 6f, statusLabelW, 28f),
                statusMsg,
                GameFont.Tiny,
                statusColor,
                TextAnchor.MiddleLeft,
                statusMsg);
        }
        finally
        {
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            GUI.color = prevColor;
            Text.WordWrap = prevWrap;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        _statusText = text;
        _statusIsError = isError;
    }

    private void BrowseAndImportAvatar(AgentSettings settings)
    {
        try
        {
            var selected = TryOpenWindowsFileDialog();
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            if (!File.Exists(selected))
            {
                SetStatus(UiText.Get("GCFAgent_AvatarFileMissing"), isError: true);
                return;
            }

            var destDir = AvatarsFolderPath;
            var safeFileName = Path.GetFileName(selected);
            var destPath = Path.Combine(destDir, safeFileName);
            if (!string.Equals(Path.GetFullPath(selected), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selected, destPath, overwrite: true);
            }

            settings.CustomAvatarPath = destPath;
            ModernUiKit.InvalidateAvatarCache();
            settings.SaveToUserConfig();
            SetStatus(UiText.Format("GCFAgent_AvatarLoaded", safeFileName), isError: false);
        }
        catch (Exception ex)
        {
            SetStatus(UiText.Format("GCFAgent_AvatarLoadFailed", UiText.Render(ex.Message)), isError: true);
        }
    }

    private void OpenAvatarFolderMenu(AgentSettings settings)
    {
        var dir = AvatarsFolderPath;
        var options = new List<FloatMenuOption>();

        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName)
                .ToList();

            foreach (var file in files)
            {
                var path = file;
                var name = Path.GetFileName(path);
                options.Add(new FloatMenuOption(UiText.Format("GCFAgent_AvatarUse", name), () =>
                {
                    settings.CustomAvatarPath = path;
                    ModernUiKit.InvalidateAvatarCache();
                    settings.SaveToUserConfig();
                    SetStatus(UiText.Format("GCFAgent_AvatarSwitched", name), isError: false);
                }));
            }
        }

        options.Add(new FloatMenuOption(UiText.Get("GCFAgent_AvatarOpenFolder"), () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetStatus(UiText.Format("GCFAgent_AvatarOpenFolderFailed", UiText.Render(ex.Message)), isError: true);
            }
        }));

        options.Add(new FloatMenuOption(UiText.Get("GCFAgent_AvatarReset"), () =>
        {
            settings.CustomAvatarPath = string.Empty;
            ModernUiKit.InvalidateAvatarCache();
            settings.SaveToUserConfig();
            SetStatus(UiText.Get("GCFAgent_AvatarResetDone"), isError: false);
        }));

        Find.WindowStack?.Add(new FloatMenu(options));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    private static string? TryOpenWindowsFileDialog()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        const int maxPathChars = 1024;
        var fileBuffer = Marshal.AllocHGlobal(maxPathChars * 2);
        try
        {
            Marshal.WriteInt16(fileBuffer, 0, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = "PNG / JPG (*.png;*.jpg;*.jpeg)\0*.png;*.jpg;*.jpeg\0All Files (*.*)\0*.*\0\0",
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = maxPathChars,
                lpstrInitialDir = AvatarsFolderPath,
                lpstrTitle = UiText.Get("GCFAgent_AvatarFileDialogTitle"),
                Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008
            };

            if (GetOpenFileNameW(ref ofn))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }
}
