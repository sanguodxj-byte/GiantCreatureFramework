using GiantCreatureFramework.Agent.Authoring;
using GiantCreatureFramework.Agent.Injection;
using GiantCreatureFramework.Agent.Patches;
using GiantCreatureFramework.Agent.Probes;
using GiantCreatureFramework.Agent.UI;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent;

/// <summary>
/// Entry point of the agent assembly. Creatures already on disk must load on every start whether
/// or not the authoring terminal is enabled, because saved games reference their Defs.
/// </summary>
public sealed class GiantCreatureAgentMod : Mod
{
    public const string HarmonyId = "sanguo.giantcreatureframework.agent";

    public static GiantCreatureAgentMod? Instance { get; private set; }
    public static AgentSettings Settings { get; } = new();
    public static bool IsTerminalEnabled => GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal;

    private static bool _subscribed;

    public GiantCreatureAgentMod(ModContentPack content) : base(content)
    {
        Instance = this;
        UiText.Resolver = static key =>
            LanguageDatabase.activeLanguage != null && key.CanTranslate()
                ? key.Translate().ToString()
                : key;
        AgentSkillStore.InitializeFromModRoot(content.RootDir);
        Settings.LoadFromUserConfigIfPresent();
        AgentContentRoot.Register(content);
        AuthoringTerminalHost.RegisterCustomDefClassifier(GcfDefInjector.IsAgentInjected);

        if (!_subscribed)
        {
            GiantCreatureFrameworkMod.AuthoringTerminalChanged += OnAuthoringTerminalChanged;
            _subscribed = true;
        }

        RuntimeLogCapture.Initialize();
        new Harmony(HarmonyId).PatchAll(typeof(GiantCreatureAgentMod).Assembly);
    }

    private static void OnAuthoringTerminalChanged(bool enabled)
    {
        if (enabled)
        {
            return;
        }

        AgentSessionManager.CancelAllActiveTurns();
        Player2Support.CancelAuth();
        Dialog_AgentChat.CloseIfOpen();
        Dialog_SystemPrompts.CloseIfOpen();
        Dialog_PersonaAndAvatar.CloseIfOpen();
        Patch_MainButtonWorker_DoButton.SyncMainTabIcon(false);
    }
}

/// <summary>
/// Startup wiring executed after all Defs and textures finish loading on the Unity main thread:
/// initializes skills/tools, attaches the AuthoringTerminalHost UI bridge, and warms runtime
/// materials and 512x512 alpha masks for all on-disk custom creatures.
/// </summary>
[StaticConstructorOnStartup]
public static class GiantCreatureAgentBootstrap
{
    private static bool _toolsRegistered;

    static GiantCreatureAgentBootstrap()
    {
        MainThreadDispatcher.MarkMainThread();

        var rootDir = GiantCreatureAgentMod.Instance?.Content?.RootDir ?? AgentContentRoot.Owner?.RootDir;
        if (!string.IsNullOrWhiteSpace(rootDir))
        {
            AgentSkillStore.InitializeFromModRoot(rootDir);
        }

        RegisterToolsOnce();

        AuthoringTerminalHost.RegisterCustomDefClassifier(GcfDefInjector.IsAgentInjected);
        AuthoringTerminalHost.RegisterImplementation(
            isVisible: static () => GiantCreatureAgentMod.IsTerminalEnabled,
            drawWindowContents: static (window, _) =>
            {
                if (GiantCreatureAgentMod.IsTerminalEnabled)
                {
                    Dialog_AgentChat.OpenWindow();
                }
                window.Close(doCloseSound: false);
            },
            tabSize: Vector2.zero);
        AuthoringTerminalHost.RegisterButtonHooks(
            Patch_MainButtonWorker_DoButton.HandleBeforeButtonDraw,
            Patch_MainButtonWorker_DoButton.HandleAfterButtonDraw);

        GcfDefInjector.WarmStartupCreatures();
    }

    public static void RegisterToolsOnce()
    {
        if (_toolsRegistered)
        {
            return;
        }

        AgentToolRegistry.ResetForTesting();
        AgentToolRegistry.RegisterProvider(new ReadProbeToolProvider());
        AgentToolRegistry.RegisterProvider(new AuthoringToolProvider());
        _toolsRegistered = true;
    }
}
