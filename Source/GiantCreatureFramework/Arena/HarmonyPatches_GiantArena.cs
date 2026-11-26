using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Arena-only Harmony hooks. Never patched at mod startup: dynamically applied via
    /// <see cref="EnsurePatched"/> only while an Arena match / cinematic recording is active,
    /// and completely unpatched via <see cref="EnsureUnpatched"/> on <see cref="GiantArenaService.StopArena"/>
    /// so normal RimWorld gameplay incurs zero Unity GUI / WindowStack / Storyteller hook overhead.
    /// </summary>
    internal static class HarmonyPatches_GiantArena
    {
        public const string ArenaHarmonyId = "sanguo.giantcreatureframework.arena";

        private static readonly object PatchLock = new object();
        private static Harmony arenaHarmony;
        private static bool isPatched;

        private static readonly FieldInfo ScreenshotModeActiveField =
            AccessTools.Field(typeof(ScreenshotModeHandler), "active");

        public static bool IsPatched => isPatched;

        public static void EnsurePatched()
        {
            lock (PatchLock)
            {
                if (isPatched)
                {
                    return;
                }
                arenaHarmony ??= new Harmony(ArenaHarmonyId);

                TryPatch(
                    AccessTools.Method(typeof(GameEnder), nameof(GameEnder.CheckOrUpdateGameOver)),
                    postfix: nameof(GameEnder_CheckOrUpdateGameOver_Postfix));
                TryPatch(
                    AccessTools.Method(typeof(Storyteller), nameof(Storyteller.StorytellerTick)),
                    prefix: nameof(Storyteller_StorytellerTick_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) }),
                    prefix: nameof(LetterStack_ReceiveLetter_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutOnGUI)),
                    prefix: nameof(AlertsReadout_AlertsReadoutOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(Messages), nameof(Messages.MessagesDoGUI)),
                    prefix: nameof(Messages_MessagesDoGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(MainButtonsRoot), nameof(MainButtonsRoot.MainButtonsOnGUI)),
                    prefix: nameof(MainButtonsRoot_MainButtonsOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI)),
                    prefix: nameof(GlobalControls_GlobalControlsOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI)),
                    prefix: nameof(ResourceReadout_ResourceReadoutOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(MouseoverReadout), nameof(MouseoverReadout.MouseoverReadoutOnGUI)),
                    prefix: nameof(MouseoverReadout_MouseoverReadoutOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)),
                    prefix: nameof(WindowStack_Add_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(WindowStack), nameof(WindowStack.ImmediateWindow)),
                    prefix: nameof(WindowStack_ImmediateWindow_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI)),
                    prefix: nameof(WindowStack_WindowStackOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(GameComponentUtility), nameof(GameComponentUtility.GameComponentOnGUI)),
                    prefix: nameof(GameComponentUtility_GameComponentOnGUI_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawBoxSolid)),
                    prefix: nameof(Widgets_DrawBoxSolid_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UnityEngine.GUI), "DrawTextureImpl"),
                    prefix: nameof(GUI_DrawTextureImpl_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UnityEngine.GUI), "DrawTextureWithTexCoordsImpl"),
                    prefix: nameof(GUI_DrawTextureWithTexCoordsImpl_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UnityEngine.Graphics), "Internal_DrawTexture"),
                    prefix: nameof(Graphics_Internal_DrawTexture_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UnityEngine.GUI), nameof(UnityEngine.GUI.Box), new[] { typeof(UnityEngine.Rect), typeof(UnityEngine.GUIContent), typeof(UnityEngine.GUIStyle) }),
                    prefix: nameof(GUI_Box_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UnityEngine.GUI), nameof(UnityEngine.GUI.Label), new[] { typeof(UnityEngine.Rect), typeof(UnityEngine.GUIContent), typeof(UnityEngine.GUIStyle) }),
                    prefix: nameof(GUI_Label_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
                    postfix: nameof(UIRoot_Play_UIRootOnGUI_ArenaVersusHud_Postfix),
                    priority: Priority.Last);
                TryPatch(
                    AccessTools.Method(typeof(CameraDriver), "CalculateCurInputDollyVect"),
                    prefix: nameof(CameraDriver_CalculateCurInputDollyVect_Prefix));
                TryPatch(
                    AccessTools.Method(typeof(UnityEngine.ScreenCapture), "WritePendingScreenshots"),
                    postfix: nameof(ScreenCapture_WritePendingScreenshots_ArenaRecorder_Postfix));

                // Clean-video social & speech MoteBubble suppression
                TryPatch(
                    AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeInteractionBubble)),
                    prefix: nameof(MoteMaker_MakeInteractionBubble_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeSpeechBubble)),
                    prefix: nameof(MoteMaker_MakeSpeechBubble_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeMoodThoughtBubble)),
                    prefix: nameof(MoteMaker_MakeMoodThoughtBubble_Prefix),
                    priority: Priority.First);

                // Post-KO combat, melee, ranged & ability lock once match concludes
                TryPatch(
                    AccessTools.Method(typeof(Pawn), nameof(Pawn.TryStartAttack)),
                    prefix: nameof(Pawn_TryStartAttack_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack)),
                    prefix: nameof(Pawn_MeleeVerbs_TryMeleeAttack_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(
                        typeof(Verb),
                        nameof(Verb.TryStartCastOn),
                        new[]
                        {
                            typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool),
                            typeof(bool), typeof(bool), typeof(bool)
                        }),
                    prefix: nameof(Verb_TryStartCastOn_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(Verb_MeleeAttack), "TryCastShot"),
                    prefix: nameof(Verb_MeleeAttack_TryCastShot_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(Verb_LaunchProjectile), "TryCastShot"),
                    prefix: nameof(Verb_LaunchProjectile_TryCastShot_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) }),
                    prefix: nameof(Ability_Activate_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(Ability), nameof(Ability.QueueCastingJob), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) }),
                    prefix: nameof(Ability_QueueCastingJob_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(JobGiver_AIFightEnemy), "TryGiveJob"),
                    prefix: nameof(JobGiver_Combat_TryGiveJob_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(JobGiver_ReactToCloseMeleeThreat), "TryGiveJob"),
                    prefix: nameof(JobGiver_Combat_TryGiveJob_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(JobGiver_Manhunter), "TryGiveJob"),
                    prefix: nameof(JobGiver_Combat_TryGiveJob_ArenaPostKo_Prefix),
                    priority: Priority.First);
                TryPatch(
                    AccessTools.Method(typeof(JobGiver_Berserk), "TryGiveJob"),
                    prefix: nameof(JobGiver_Combat_TryGiveJob_ArenaPostKo_Prefix),
                    priority: Priority.First);

                isPatched = true;
            }
        }

        public static void EnsureUnpatched()
        {
            lock (PatchLock)
            {
                if (!isPatched || arenaHarmony == null)
                {
                    return;
                }
                try
                {
                    arenaHarmony.UnpatchAll(ArenaHarmonyId);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[GiantCreatureFramework] Failed to unpatch Arena Harmony hooks: {ex.Message}");
                }
                finally
                {
                    isPatched = false;
                }
            }
        }

        private static void TryPatch(MethodBase target, string prefix = null, string postfix = null, int priority = Priority.Normal)
        {
            if (target == null || arenaHarmony == null)
            {
                return;
            }
            HarmonyMethod pre = prefix != null
                ? new HarmonyMethod(typeof(HarmonyPatches_GiantArena), prefix) { priority = priority }
                : null;
            HarmonyMethod post = postfix != null
                ? new HarmonyMethod(typeof(HarmonyPatches_GiantArena), postfix) { priority = priority }
                : null;
            arenaHarmony.Patch(target, prefix: pre, postfix: post);
        }

        internal static void SetScreenshotModeActive(bool active)
        {
            try
            {
                ScreenshotModeHandler handler = Find.UIRoot?.screenshotMode;
                if (handler != null && ScreenshotModeActiveField != null)
                {
                    ScreenshotModeActiveField.SetValue(handler, active);
                }
            }
            catch
            {
            }
        }

        internal static bool IsScreenshotModeActive()
        {
            try
            {
                return Find.UIRoot?.screenshotMode?.FiltersCurrentEvent == true;
            }
            catch
            {
                return false;
            }
        }

        public static void GameEnder_CheckOrUpdateGameOver_Postfix(GameEnder __instance)
        {
            if (__instance != null && __instance.gameEnding && GiantArenaService.IsArenaActive)
            {
                __instance.gameEnding = false;
            }
        }

        public static bool Storyteller_StorytellerTick_Prefix()
        {
            return !GiantArenaService.IsArenaActive;
        }

        public static bool LetterStack_ReceiveLetter_Prefix()
        {
            return !GiantArenaService.IsCleanVideoMode;
        }

        public static bool AlertsReadout_AlertsReadoutOnGUI_Prefix()
        {
            return !GiantArenaService.IsCleanVideoMode;
        }

        public static bool Messages_MessagesDoGUI_Prefix()
        {
            return !GiantArenaService.IsCleanVideoMode;
        }

        public static bool MainButtonsRoot_MainButtonsOnGUI_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        public static bool GlobalControls_GlobalControlsOnGUI_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        public static bool ResourceReadout_ResourceReadoutOnGUI_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        public static bool MouseoverReadout_MouseoverReadoutOnGUI_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        public static bool WindowStack_Add_Prefix(Window window)
        {
            if (!GiantArenaService.IsCinematicHudOnly)
            {
                return true;
            }
            return window == null;
        }

        public static bool WindowStack_ImmediateWindow_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        private static bool ShouldMuteNonArenaGui()
        {
            return GiantArenaService.IsCinematicHudOnly && !GiantBossHealthBarHUD.IsDrawingArenaVersusHud;
        }

        public static bool WindowStack_WindowStackOnGUI_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        public static bool GameComponentUtility_GameComponentOnGUI_Prefix()
        {
            return !GiantArenaService.IsCinematicHudOnly;
        }

        public static bool Widgets_DrawBoxSolid_Prefix()
        {
            return !ShouldMuteNonArenaGui();
        }

        public static bool GUI_DrawTextureImpl_Prefix()
        {
            return !ShouldMuteNonArenaGui();
        }

        public static bool GUI_DrawTextureWithTexCoordsImpl_Prefix()
        {
            return !ShouldMuteNonArenaGui();
        }

        public static bool Graphics_Internal_DrawTexture_Prefix()
        {
            if (!ShouldMuteNonArenaGui())
            {
                return true;
            }
            return UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint;
        }

        public static bool GUI_Box_Prefix()
        {
            return !ShouldMuteNonArenaGui();
        }

        public static bool GUI_Label_Prefix()
        {
            return !ShouldMuteNonArenaGui();
        }

        public static void UIRoot_Play_UIRootOnGUI_ArenaVersusHud_Postfix()
        {
            if (!GiantArenaService.IsCinematicHudOnly || !IsScreenshotModeActive())
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp?.BossHealthBarHud == null)
            {
                return;
            }
            GiantBossHealthBarHUD.IsDrawingArenaVersusHud = true;
            try
            {
                mapComp.BossHealthBarHud.DrawOnGUI(map, mapComp.ActiveGiants);
            }
            finally
            {
                GiantBossHealthBarHUD.IsDrawingArenaVersusHud = false;
            }
        }

        public static bool CameraDriver_CalculateCurInputDollyVect_Prefix(ref UnityEngine.Vector2 __result)
        {
            if (GiantArenaVideoRecorder.IsRecording)
            {
                __result = UnityEngine.Vector2.zero;
                return false;
            }
            return true;
        }

        public static void ScreenCapture_WritePendingScreenshots_ArenaRecorder_Postfix()
        {
            if (GiantArenaVideoRecorder.IsRecording)
            {
                GiantArenaVideoRecorder.OnEndOfRenderFrame();
            }
        }

        public static bool MoteMaker_MakeInteractionBubble_Prefix(Pawn initiator, Pawn recipient, ref MoteBubble __result)
        {
            Map map = initiator?.MapHeld ?? recipient?.MapHeld;
            if (GiantArenaService.ShouldSuppressSpeechBubblesOnMap(map))
            {
                __result = null;
                return false;
            }
            return true;
        }

        public static bool MoteMaker_MakeSpeechBubble_Prefix(Pawn initiator, ref MoteBubble __result)
        {
            if (GiantArenaService.ShouldSuppressSpeechBubblesOnMap(initiator?.MapHeld))
            {
                __result = null;
                return false;
            }
            return true;
        }

        public static bool MoteMaker_MakeMoodThoughtBubble_Prefix(Pawn pawn, ref MoteBubble __result)
        {
            if (GiantArenaService.ShouldSuppressSpeechBubblesOnMap(pawn?.MapHeld))
            {
                __result = null;
                return false;
            }
            return true;
        }

        public static bool Pawn_TryStartAttack_ArenaPostKo_Prefix(Pawn __instance, ref bool __result)
        {
            if (GiantArenaService.ShouldBlockPostMatchHostileAction(__instance))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool Pawn_MeleeVerbs_TryMeleeAttack_ArenaPostKo_Prefix(Pawn_MeleeVerbs __instance, ref bool __result)
        {
            if (GiantArenaService.ShouldBlockPostMatchHostileAction(__instance?.Pawn))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool Verb_TryStartCastOn_ArenaPostKo_Prefix(Verb __instance, ref bool __result)
        {
            if (__instance?.CasterPawn is Pawn caster && GiantArenaService.ShouldBlockPostMatchHostileAction(caster))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool Verb_MeleeAttack_TryCastShot_ArenaPostKo_Prefix(Verb_MeleeAttack __instance, ref bool __result)
        {
            if (__instance?.CasterPawn is Pawn caster && GiantArenaService.ShouldBlockPostMatchHostileAction(caster))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool Verb_LaunchProjectile_TryCastShot_ArenaPostKo_Prefix(Verb_LaunchProjectile __instance, ref bool __result)
        {
            if (__instance?.CasterPawn is Pawn caster && GiantArenaService.ShouldBlockPostMatchHostileAction(caster))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool Ability_Activate_ArenaPostKo_Prefix(Ability __instance, ref bool __result)
        {
            if (GiantArenaService.ShouldBlockPostMatchHostileAction(__instance?.pawn))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static bool Ability_QueueCastingJob_ArenaPostKo_Prefix(Ability __instance)
        {
            return !GiantArenaService.ShouldBlockPostMatchHostileAction(__instance?.pawn);
        }

        public static bool JobGiver_Combat_TryGiveJob_ArenaPostKo_Prefix(Pawn pawn, ref Job __result)
        {
            if (GiantArenaService.ShouldBlockPostMatchHostileAction(pawn))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }
}
