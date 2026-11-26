using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.UI
{
    /// <summary>
    /// Zero-collision host bridge allowing the external GiantCreatureFramework.Agent assembly
    /// to attach runtime UI rendering and visibility logic without declaring duplicate
    /// MainButtonWorker / MainTabWindow class names across assemblies.
    /// </summary>
    public static class AuthoringTerminalHost
    {
        private static Func<bool> visibilityEvaluator;
        private static Action<MainTabWindow, Rect> windowDrawer;
        private static Action<MainButtonDef> beforeButtonDraw;
        private static Action<MainButtonDef, Rect> afterButtonDraw;
        private static Func<Def, bool> customDefEvaluator;
        private static Vector2 requestedSize = new Vector2(620f, 360f);

        public static bool IsRegistered => windowDrawer != null;
        public static Vector2 RequestedSize => requestedSize;

        public static void RegisterCustomDefClassifier(Func<Def, bool> isCustomDef)
        {
            customDefEvaluator = isCustomDef;
        }

        public static bool IsCustomAuthoredDef(Def def) =>
            def != null && customDefEvaluator != null && customDefEvaluator(def);

        public static void RegisterImplementation(
            Func<bool> isVisible,
            Action<MainTabWindow, Rect> drawWindowContents,
            Vector2? tabSize = null)
        {
            visibilityEvaluator = isVisible;
            windowDrawer = drawWindowContents;
            if (tabSize.HasValue)
            {
                requestedSize = tabSize.Value;
            }
        }

        public static void RegisterButtonHooks(
            Action<MainButtonDef> onBeforeButtonDraw,
            Action<MainButtonDef, Rect> onAfterButtonDraw)
        {
            beforeButtonDraw = onBeforeButtonDraw;
            afterButtonDraw = onAfterButtonDraw;
        }

        public static void ClearImplementation()
        {
            visibilityEvaluator = null;
            windowDrawer = null;
            beforeButtonDraw = null;
            afterButtonDraw = null;
            requestedSize = new Vector2(620f, 360f);
        }

        internal static bool EvaluateVisible() => visibilityEvaluator != null && visibilityEvaluator();

        internal static void InvokeBeforeButtonDraw(MainButtonDef def) => beforeButtonDraw?.Invoke(def);

        internal static void InvokeAfterButtonDraw(MainButtonDef def, Rect rect) => afterButtonDraw?.Invoke(def, rect);

        internal static bool TryDraw(MainTabWindow window, Rect inRect)
        {
            if (windowDrawer == null)
            {
                return false;
            }
            windowDrawer(window, inRect);
            return true;
        }
    }

    /// <summary>
    /// Lightweight MainButtonWorker for GCF_AuthoringTerminal_MainTab (delegates to AuthoringTerminalHost when registered).
    /// </summary>
    public class MainButtonWorker_AuthoringTerminal : MainButtonWorker_ToggleTab
    {
        public override bool Visible => AuthoringTerminalHost.EvaluateVisible();

        public override void DoButton(Rect rect)
        {
            AuthoringTerminalHost.InvokeBeforeButtonDraw(def);
            base.DoButton(rect);
            AuthoringTerminalHost.InvokeAfterButtonDraw(def, rect);
        }
    }

    /// <summary>
    /// Safe host MainTabWindow for GCF_AuthoringTerminal_MainTab so Def parsing and OnGUI never fail
    /// while avoiding cross-assembly duplicate type ambiguity when GiantCreatureFramework.Agent is loaded.
    /// </summary>
    public class MainTabWindow_AuthoringTerminal : MainTabWindow
    {
        public override Vector2 RequestedTabSize => AuthoringTerminalHost.RequestedSize;

        public override void DoWindowContents(Rect inRect)
        {
            if (AuthoringTerminalHost.TryDraw(this, inRect))
            {
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "Runtime Creature Authoring Terminal");

            Text.Font = GameFont.Small;
            Rect bodyRect = new Rect(inRect.x, inRect.y + 42f, inRect.width, inRect.height - 90f);
            Widgets.Label(bodyRect, "Giant Creature Framework runtime terminal host.");

            Rect btnRect = new Rect(inRect.x, inRect.yMax - 36f, 180f, 32f);
            if (Widgets.ButtonText(btnRect, "Close".CanTranslate() ? "Close".Translate().ToString() : "Close"))
            {
                Close();
            }
        }
    }
}
