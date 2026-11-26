using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    [HarmonyPatch]
    public static class HarmonyPatches_PhaseTransform
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
        public static void Thing_TakeDamage_Prefix(Thing __instance, ref DamageInfo dinfo)
        {
            if (__instance is Pawn pawn
                && GiantFastLookup.TryGetPhaseTransform(pawn) is CompGiantPhaseTransform phaseComp
                && phaseComp.IsTransforming)
            {
                dinfo.SetAmount(0f);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.CheckForStateChange))]
        public static bool Pawn_HealthTracker_CheckForStateChange_Prefix(
            Pawn ___pawn,
            DamageInfo? dinfo)
        {
            if (___pawn == null || ___pawn.Destroyed)
            {
                return true;
            }

            CompGiantPhaseTransform phaseComp = GiantFastLookup.TryGetPhaseTransform(___pawn);
            if (phaseComp == null)
            {
                return true;
            }

            if (phaseComp.ShouldInterceptDepletionNow() && phaseComp.TryInterceptFatalOrDowned(dinfo))
            {
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
        public static bool Pawn_Kill_Prefix(Pawn __instance, DamageInfo? dinfo)
        {
            if (__instance == null || __instance.Destroyed)
            {
                return true;
            }

            CompGiantPhaseTransform phaseComp = GiantFastLookup.TryGetPhaseTransform(__instance);
            if (phaseComp != null && phaseComp.TryInterceptFatalOrDowned(dinfo))
            {
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDead))]
        public static void Pawn_HealthTracker_ShouldBeDead_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result
                && GiantFastLookup.TryGetPhaseTransform(___pawn) is CompGiantPhaseTransform phaseComp
                && phaseComp.IsTransforming)
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDowned))]
        public static void Pawn_HealthTracker_ShouldBeDowned_Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result
                && GiantFastLookup.TryGetPhaseTransform(___pawn) is CompGiantPhaseTransform phaseComp
                && !phaseComp.IsEvaluatingRawDowned
                && (phaseComp.IsTransforming || phaseComp.HasNextStage))
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.BodySize), MethodType.Getter)]
        public static void Pawn_BodySize_Postfix(Pawn __instance, ref float __result)
        {
            if (GiantFastLookup.TryGetPhaseTransform(__instance) is CompGiantPhaseTransform phaseComp)
            {
                float mult = phaseComp.GetBodySizeMultiplier();
                if (!Mathf.Approximately(mult, 1f))
                {
                    __result *= mult;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.HealthScale), MethodType.Getter)]
        public static void Pawn_HealthScale_Postfix(Pawn __instance, ref float __result)
        {
            if (GiantFastLookup.TryGetPhaseTransform(__instance) is CompGiantPhaseTransform phaseComp)
            {
                float mult = phaseComp.GetHealthScaleMultiplier();
                if (!Mathf.Approximately(mult, 1f))
                {
                    __result *= mult;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.GetGizmos))]
        public static IEnumerable<Gizmo> Pawn_AbilityTracker_GetGizmos_PhaseTransform_Postfix(
            IEnumerable<Gizmo> __result,
            Pawn_AbilityTracker __instance)
        {
            Pawn pawn = __instance?.pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed ||
                pawn.Faction != Faction.OfPlayer ||
                !pawn.IsColonyMech ||
                pawn.IsColonyMechPlayerControlled ||
                GiantFastLookup.TryGetPhaseTransform(pawn) == null)
            {
                return __result;
            }
            return EnsurePhaseTransformAbilityGizmos(__result, __instance, pawn);
        }

        private static IEnumerable<Gizmo> EnsurePhaseTransformAbilityGizmos(
            IEnumerable<Gizmo> source,
            Pawn_AbilityTracker tracker,
            Pawn pawn)
        {
            bool anyAbility = false;
            if (source != null)
            {
                foreach (Gizmo g in source)
                {
                    if (g is Command_Ability)
                    {
                        anyAbility = true;
                    }
                    yield return g;
                }
            }
            if (anyAbility)
            {
                yield break;
            }

            List<Ability> abilities = tracker.AllAbilitiesForReading;
            for (int i = 0; i < abilities.Count; i++)
            {
                Ability ab = abilities[i];
                if (ab?.def == null)
                {
                    continue;
                }
                bool visible = pawn.Drafted || ab.def.displayGizmoWhileUndrafted;
                if (!visible)
                {
                    continue;
                }
                foreach (Command cmd in ab.GetGizmos())
                {
                    yield return cmd;
                }
            }
        }
    }
}
