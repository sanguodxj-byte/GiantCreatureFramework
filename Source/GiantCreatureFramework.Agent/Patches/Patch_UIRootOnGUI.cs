using System.Reflection;
using GiantCreatureFramework.Agent.UI;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.Patches;

/// <summary>
/// Pumps main-thread continuations every frame and handles the GCF_ToggleAuthoringTerminal hotkey
/// strictly gated by GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal.
/// </summary>
[HarmonyPatch(typeof(UIRoot), nameof(UIRoot.UIRootOnGUI))]
public static class Patch_UIRootOnGUI
{
    private static KeyBindingDef? _toggleKeyDef;

    [HarmonyPostfix]
    public static void Postfix()
    {
        MainThreadDispatcher.MarkMainThread();
        MainThreadDispatcher.Pump();

        if (!GiantCreatureAgentMod.IsTerminalEnabled)
        {
            return;
        }

        AgentSessionManager.RefreshScope();

        if (Event.current == null || Event.current.type != EventType.KeyDown)
        {
            return;
        }

        _toggleKeyDef ??= DefDatabase<KeyBindingDef>.GetNamedSilentFail("GCF_ToggleAuthoringTerminal");
        if (_toggleKeyDef != null && _toggleKeyDef.KeyDownEvent)
        {
            Event.current.Use();
            Dialog_AgentChat.ToggleWindow();
        }
        else if (_toggleKeyDef == null && Event.current.keyCode == KeyCode.F10)
        {
            Event.current.Use();
            Dialog_AgentChat.ToggleWindow();
        }
    }
}

[HarmonyPatch(typeof(UIRoot), nameof(UIRoot.UIRootUpdate))]
public static class Patch_UIRootUpdate
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        MainThreadDispatcher.MarkMainThread();
        MainThreadDispatcher.Pump();
    }
}

/// <summary>
/// Updates the Creature Forge main-tab button icon to show an unread badge when a background turn finishes
/// while the terminal window is closed. Invoked directly by MainButtonWorker_AuthoringTerminal.DoButton
/// (no global Harmony patch on MainButtonWorker.DoButton).
/// </summary>
public static class Patch_MainButtonWorker_DoButton
{
    private const string TargetDefName = "GCF_AuthoringTerminal_MainTab";
    private static readonly FieldInfo? IconField = AccessTools.Field(typeof(MainButtonDef), "icon");
    private static Texture2D? _normalIcon;
    private static Texture2D? _alertIcon;
    private static bool _lastAppliedAlertState;

    public static void HandleBeforeButtonDraw(MainButtonDef? def)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || def == null)
        {
            return;
        }

        SyncIconForDef(def, Dialog_AgentChat.HasUnreadReply);
    }

    public static void HandleAfterButtonDraw(MainButtonDef? def, Rect rect)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled || def == null)
        {
            return;
        }

        if (Dialog_AgentChat.HasUnreadReply && Mouse.IsOver(rect))
        {
            TooltipHandler.TipRegion(rect, UiText.Get("GCFAgent_MainTabUnreadTip"));
        }
    }

    public static void SyncMainTabIcon(bool hasUnread)
    {
        var def = DefDatabase<MainButtonDef>.GetNamedSilentFail(TargetDefName);
        if (def != null)
        {
            SyncIconForDef(def, hasUnread);
        }
    }

    private static void SyncIconForDef(MainButtonDef def, bool hasUnread)
    {
        if (IconField == null)
        {
            return;
        }

        if (_normalIcon == null)
        {
            var current = def.Icon;
            if (current != null && current != BaseContent.BadTex)
            {
                _normalIcon = current;
            }
            else
            {
                _normalIcon = ContentFinder<Texture2D>.Get("UI/Icons/GCF_Agent_MainTab", reportFailure: false);
            }
        }

        if (hasUnread == _lastAppliedAlertState && IconField.GetValue(def) != null)
        {
            return;
        }

        if (hasUnread)
        {
            var alertTex = GetOrCreateAlertIcon();
            if (alertTex != null)
            {
                IconField.SetValue(def, alertTex);
                _lastAppliedAlertState = true;
            }
        }
        else if (_normalIcon != null)
        {
            IconField.SetValue(def, _normalIcon);
            _lastAppliedAlertState = false;
        }
    }

    private static Texture2D? GetOrCreateAlertIcon()
    {
        if (_alertIcon != null)
        {
            return _alertIcon;
        }

        var loaded = ContentFinder<Texture2D>.Get("UI/Icons/GCF_Agent_MainTab_Alert", reportFailure: false);
        if (loaded != null && loaded != BaseContent.BadTex)
        {
            _alertIcon = loaded;
            return _alertIcon;
        }

        try
        {
            var rootDir = GiantCreatureAgentMod.Instance?.Content?.RootDir;
            if (!string.IsNullOrWhiteSpace(rootDir))
            {
                var pngPath = Path.Combine(rootDir, "1.7", "Textures", "UI", "Icons", "GCF_Agent_MainTab_Alert.png");
                if (File.Exists(pngPath))
                {
                    var bytes = File.ReadAllBytes(pngPath);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, bytes))
                    {
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = FilterMode.Bilinear;
                        _alertIcon = tex;
                        return _alertIcon;
                    }
                    UnityEngine.Object.Destroy(tex);
                }
            }
        }
        catch
        {
            // Fall back to normal icon if disk read fails
        }

        return _normalIcon;
    }
}
