using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Core framework progression component: tracks Heavy Mech Scorpion Biotech War Queen / Powerfocus Chip unlock
    /// and shared per-pawn part-break trophy deduplication across saves.
    /// </summary>
    public sealed class GameComponent_GiantProgression : GameComponent
    {
        public const int PowerfocusChipAnalysisId = 652343245;

        private bool warQueenDefeatedUnlock;
        private HashSet<string> awardedPartBreaks = new HashSet<string>();

        public static GameComponent_GiantProgression Instance =>
            Current.Game?.GetComponent<GameComponent_GiantProgression>();

        public bool WarQueenDefeatedUnlock => warQueenDefeatedUnlock;

        public GameComponent_GiantProgression(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref warQueenDefeatedUnlock, "gcf_warQueenDefeatedUnlock", defaultValue: false);
            Scribe_Collections.Look(ref awardedPartBreaks, "gcf_awardedPartBreaks", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                awardedPartBreaks ??= new HashSet<string>();
            }
        }

        public static bool IsWarQueenDefeatedOrAnalyzed()
        {
            GameComponent_GiantProgression comp = Instance;
            if (comp != null && comp.warQueenDefeatedUnlock)
            {
                return true;
            }
            if (ModsConfig.BiotechActive && (Find.AnalysisManager?.IsAnalysisComplete(PowerfocusChipAnalysisId) ?? false))
            {
                if (comp != null)
                {
                    comp.warQueenDefeatedUnlock = true;
                }
                return true;
            }
            return false;
        }

        public void NotifyWarQueenDefeated()
        {
            if (warQueenDefeatedUnlock)
            {
                return;
            }
            warQueenDefeatedUnlock = true;
            if (ModsConfig.BiotechActive && Find.LetterStack != null)
            {
                Find.LetterStack.ReceiveLetter(
                    GiantProgressionUtility.SafeTranslate("GCF_Letter_WarQueenScorpionUnlock_Label", "Siege Schematic Unlocked: Heavy Mech Scorpion"),
                    GiantProgressionUtility.SafeTranslate("GCF_Letter_WarQueenScorpionUnlock_Text",
                        "With the destruction of the War Queen, your colony can now gestate Heavy Mech Scorpions inside a Large Mech Gestator once High Mechtech research and two Powerfocus Chips are ready."),
                    LetterDefOf.PositiveEvent);
            }
        }

        public bool TryRecordPartBreakReward(int pawnThingId, string partGroupKey)
        {
            awardedPartBreaks ??= new HashSet<string>();
            return awardedPartBreaks.Add($"{pawnThingId}:{partGroupKey}");
        }

        public override void GameComponentTick()
        {
            int ticks = Find.TickManager?.TicksGame ?? 0;
            if ((ticks & 255) != 0)
            {
                return;
            }

            if (!warQueenDefeatedUnlock && ModsConfig.BiotechActive &&
                (Find.AnalysisManager?.IsAnalysisComplete(PowerfocusChipAnalysisId) ?? false))
            {
                warQueenDefeatedUnlock = true;
            }
        }
    }

    public static class GiantProgressionUtility
    {
        private static readonly Dictionary<string, Func<bool>> ExtraRecipeGates = new Dictionary<string, Func<bool>>(StringComparer.Ordinal);

        public static void RegisterRecipeAvailabilityGate(string recipeDefName, Func<bool> availabilityPredicate)
        {
            if (!string.IsNullOrWhiteSpace(recipeDefName) && availabilityPredicate != null)
            {
                ExtraRecipeGates[recipeDefName.Trim()] = availabilityPredicate;
            }
        }

        internal static bool EvaluateExtraRecipeGate(string recipeDefName)
        {
            if (ExtraRecipeGates.Count == 0 || string.IsNullOrEmpty(recipeDefName))
            {
                return true;
            }
            return !ExtraRecipeGates.TryGetValue(recipeDefName, out Func<bool> gate) || gate();
        }

        public static string SafeTranslate(string key, string fallback, params NamedArgument[] args)
        {
            if (LanguageDatabase.activeLanguage != null && key.CanTranslate())
            {
                return args != null && args.Length > 0
                    ? key.Translate(args).ToString()
                    : key.Translate().ToString();
            }
            if (string.IsNullOrEmpty(fallback) || args == null || args.Length == 0)
            {
                return fallback;
            }
            if (LanguageDatabase.activeLanguage != null)
            {
                try
                {
                    return new TaggedString(fallback).Formatted(args).ToString();
                }
                catch
                {
                }
            }
            try
            {
                object[] rawArgs = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    rawArgs[i] = args[i].arg;
                }
                return string.Format(fallback, rawArgs);
            }
            catch
            {
                return fallback;
            }
        }

        public static void EnsureGuaranteedButcherTrophy(
            List<Thing> products,
            ThingDef requiredDef,
            Func<ThingDef, Thing> thingFactory = null)
        {
            if (products == null || requiredDef == null)
            {
                return;
            }

            for (int i = 0; i < products.Count; i++)
            {
                Thing existing = products[i];
                if (existing != null && existing.def == requiredDef && existing.stackCount > 0)
                {
                    return;
                }
            }

            Func<ThingDef, Thing> factory = thingFactory ?? (def => ThingMaker.MakeThing(def));
            Thing guaranteed = factory(requiredDef);
            if (guaranteed != null)
            {
                if (guaranteed.stackCount <= 0)
                {
                    guaranteed.stackCount = 1;
                }
                products.Add(guaranteed);
            }
        }

        public static void SpawnPartBreakTrophy(Pawn pawn, ThingDef trophyDef, string msgKey, string fallbackMsg)
        {
            if (trophyDef == null || pawn?.Map == null)
            {
                return;
            }
            Thing drop = ThingMaker.MakeThing(trophyDef);
            GenPlace.TryPlaceThing(drop, pawn.Position, pawn.Map, ThingPlaceMode.Near);
            Messages.Message(SafeTranslate(msgKey, fallbackMsg), new LookTargets(drop), MessageTypeDefOf.PositiveEvent);
        }

        public static void ResolveAncientRuinsDefsForBiome(
            string biomeDefName,
            out string landmarkDefName,
            out string mutatorDefName)
        {
            if (string.Equals(biomeDefName, "IceSheet", StringComparison.Ordinal) ||
                string.Equals(biomeDefName, "GlacialPlain", StringComparison.Ordinal) ||
                string.Equals(biomeDefName, "SeaIce", StringComparison.Ordinal))
            {
                landmarkDefName = "FrozenRuins";
                mutatorDefName = "AncientRuins_Frozen";
            }
            else
            {
                landmarkDefName = "Ruins";
                mutatorDefName = "AncientRuins";
            }
        }

        public static bool TileHasAncientRuinsTerrain(PlanetTile tile)
        {
            if (!tile.Valid || Find.WorldGrid == null)
            {
                return false;
            }

            Tile tileInfo = Find.WorldGrid[tile];
            if (tileInfo?.Mutators != null)
            {
                for (int i = 0; i < tileInfo.Mutators.Count; i++)
                {
                    string mName = tileInfo.Mutators[i]?.defName;
                    if (string.Equals(mName, "AncientRuins", StringComparison.Ordinal) ||
                        string.Equals(mName, "AncientRuins_Frozen", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            if (ModsConfig.OdysseyActive && Find.World?.landmarks != null)
            {
                string lName = Find.World.landmarks[tile]?.def?.defName;
                if (string.Equals(lName, "Ruins", StringComparison.Ordinal) ||
                    string.Equals(lName, "FrozenRuins", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static void EnsureAncientRuinsOnTile(PlanetTile siteTile)
        {
            if (!siteTile.Valid || Find.WorldGrid == null)
            {
                return;
            }

            Tile tileInfo = Find.WorldGrid[siteTile];
            if (tileInfo == null)
            {
                return;
            }

            ResolveAncientRuinsDefsForBiome(
                tileInfo.PrimaryBiome?.defName,
                out string landmarkDefName,
                out string mutatorDefName);

            if (ModsConfig.OdysseyActive && Find.World?.landmarks != null && Find.World.landmarks[siteTile] == null)
            {
                LandmarkDef lmDef = DefDatabase<LandmarkDef>.GetNamedSilentFail(landmarkDefName)
                    ?? DefDatabase<LandmarkDef>.GetNamedSilentFail("Ruins");
                if (lmDef != null)
                {
                    Find.World.landmarks.AddLandmark(lmDef, siteTile, siteTile.Layer, forced: true);
                }
            }

            TileMutatorDef mutDef = DefDatabase<TileMutatorDef>.GetNamedSilentFail(mutatorDefName)
                ?? DefDatabase<TileMutatorDef>.GetNamedSilentFail("AncientRuins");
            if (mutDef != null && !tileInfo.Mutators.Contains(mutDef))
            {
                tileInfo.AddMutator(mutDef);
            }
        }

        public static bool TryFindAncientRuinsVaultTile(PlanetTile nearTile, out PlanetTile siteTile, bool attachRuinsIfNeeded = true)
        {
            siteTile = PlanetTile.Invalid;
            var ruinsLandmarks = new List<LandmarkDef>(2);
            LandmarkDef ruinsLm = DefDatabase<LandmarkDef>.GetNamedSilentFail("Ruins");
            LandmarkDef frozenRuinsLm = DefDatabase<LandmarkDef>.GetNamedSilentFail("FrozenRuins");
            if (ruinsLm != null) ruinsLandmarks.Add(ruinsLm);
            if (frozenRuinsLm != null) ruinsLandmarks.Add(frozenRuinsLm);

            if (ModsConfig.OdysseyActive && ruinsLandmarks.Count > 0)
            {
                if (TileFinder.TryFindNewSiteTile(
                        out siteTile,
                        nearTile,
                        minDist: 3,
                        maxDist: 24,
                        allowCaravans: false,
                        allowedLandmarks: ruinsLandmarks,
                        selectLandmarkChance: 1f,
                        canSelectComboLandmarks: true,
                        tileFinderMode: TileFinderMode.Near,
                        validator: TileHasAncientRuinsTerrain) ||
                    TileFinder.TryFindNewSiteTile(
                        out siteTile,
                        nearTile,
                        minDist: 2,
                        maxDist: 40,
                        allowCaravans: false,
                        allowedLandmarks: ruinsLandmarks,
                        selectLandmarkChance: 1f,
                        canSelectComboLandmarks: true,
                        tileFinderMode: TileFinderMode.Near,
                        validator: TileHasAncientRuinsTerrain))
                {
                    if (attachRuinsIfNeeded)
                    {
                        EnsureAncientRuinsOnTile(siteTile);
                    }
                    return true;
                }
            }

            bool NonSeaIceValidator(PlanetTile t)
            {
                if (!t.Valid || Find.WorldGrid == null) return false;
                Tile ti = Find.WorldGrid[t];
                return ti?.PrimaryBiome == null || !string.Equals(ti.PrimaryBiome.defName, "SeaIce", StringComparison.Ordinal);
            }

            if (!TileFinder.TryFindNewSiteTile(
                    out siteTile,
                    nearTile,
                    minDist: 3,
                    maxDist: 12,
                    allowCaravans: false,
                    allowedLandmarks: null,
                    selectLandmarkChance: 0f,
                    validator: NonSeaIceValidator) &&
                !TileFinder.TryFindNewSiteTile(
                    out siteTile,
                    nearTile,
                    minDist: 2,
                    maxDist: 20,
                    allowCaravans: false,
                    allowedLandmarks: null,
                    selectLandmarkChance: 0f))
            {
                return false;
            }

            if (attachRuinsIfNeeded)
            {
                EnsureAncientRuinsOnTile(siteTile);
            }
            return true;
        }

        public static bool TryChooseBestRuinContainer(
            IReadOnlyList<CellRect> candidateRuinRects,
            IntVec3 mapSize,
            int vaultSpan,
            out CellRect ruinContainer,
            out IntVec3 vaultCenter,
            out bool fullyEnclosedByExistingRuin)
        {
            int halfVault = vaultSpan / 2;
            int edgeMargin = Mathf.Min(18, Mathf.Max(4, (Mathf.Min(mapSize.x, mapSize.z) - vaultSpan) / 4));
            CellRect safeBounds = new CellRect(
                edgeMargin,
                edgeMargin,
                Mathf.Max(vaultSpan, mapSize.x - edgeMargin * 2),
                Mathf.Max(vaultSpan, mapSize.z - edgeMargin * 2));
            IntVec3 mapCenter = new IntVec3(mapSize.x / 2, 0, mapSize.z / 2);

            if (candidateRuinRects != null && candidateRuinRects.Count > 0)
            {
                int minEnclosingSpan = vaultSpan + 6;
                float bestScore = float.MinValue;
                int bestIdx = -1;

                for (int i = 0; i < candidateRuinRects.Count; i++)
                {
                    CellRect clipped = candidateRuinRects[i].ClipInsideRect(safeBounds);
                    if (clipped.Width < minEnclosingSpan || clipped.Height < minEnclosingSpan)
                    {
                        continue;
                    }
                    float dist = clipped.CenterCell.DistanceTo(mapCenter);
                    float score = clipped.Area - dist * 16f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0)
                {
                    CellRect chosen = candidateRuinRects[bestIdx].ClipInsideRect(safeBounds);
                    int innerPad = halfVault + 3;
                    int minX = chosen.minX + innerPad;
                    int maxX = chosen.maxX - innerPad;
                    int minZ = chosen.minZ + innerPad;
                    int maxZ = chosen.maxZ - innerPad;
                    IntVec3 rawCenter = chosen.CenterCell;
                    vaultCenter = new IntVec3(
                        Mathf.Clamp(rawCenter.x, minX, Mathf.Max(minX, maxX)),
                        0,
                        Mathf.Clamp(rawCenter.z, minZ, Mathf.Max(minZ, maxZ)));
                    ruinContainer = candidateRuinRects[bestIdx];
                    fullyEnclosedByExistingRuin = true;
                    return true;
                }

                float fallbackScore = float.MinValue;
                int fallbackIdx = -1;
                for (int i = 0; i < candidateRuinRects.Count; i++)
                {
                    CellRect r = candidateRuinRects[i];
                    if (r.Width < 12 || r.Height < 12) continue;
                    float score = r.Area - r.CenterCell.DistanceTo(mapCenter) * 14f;
                    if (score > fallbackScore)
                    {
                        fallbackScore = score;
                        fallbackIdx = i;
                    }
                }

                if (fallbackIdx >= 0)
                {
                    int wingHalf = halfVault + 11;
                    IntVec3 raw = candidateRuinRects[fallbackIdx].CenterCell;
                    vaultCenter = new IntVec3(
                        Mathf.Clamp(raw.x, wingHalf + 4, Mathf.Max(wingHalf + 4, mapSize.x - wingHalf - 5)),
                        0,
                        Mathf.Clamp(raw.z, wingHalf + 4, Mathf.Max(wingHalf + 4, mapSize.z - wingHalf - 5)));
                    ruinContainer = CellRect.CenteredOn(vaultCenter, wingHalf);
                    fullyEnclosedByExistingRuin = false;
                    return true;
                }
            }

            int defaultWingHalf = halfVault + 11;
            vaultCenter = new IntVec3(
                Mathf.Clamp(mapCenter.x, defaultWingHalf + 4, Mathf.Max(defaultWingHalf + 4, mapSize.x - defaultWingHalf - 5)),
                0,
                Mathf.Clamp(mapCenter.z, defaultWingHalf + 4, Mathf.Max(defaultWingHalf + 4, mapSize.z - defaultWingHalf - 5)));
            ruinContainer = CellRect.CenteredOn(vaultCenter, defaultWingHalf);
            fullyEnclosedByExistingRuin = false;
            return false;
        }
    }

    [HarmonyPatch]
    public static class HarmonyPatches_GiantProgression
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
        public static void Pawn_Kill_Progression_Postfix(Pawn __instance)
        {
            if (__instance?.def != null && string.Equals(__instance.def.defName, "Mech_Warqueen", StringComparison.Ordinal))
            {
                GameComponent_GiantProgression.Instance?.NotifyWarQueenDefeated();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RecipeDef), nameof(RecipeDef.AvailableNow), MethodType.Getter)]
        public static void RecipeDef_AvailableNow_Progression_Postfix(RecipeDef __instance, ref bool __result)
        {
            if (!__result || __instance == null)
            {
                return;
            }

            string dn = __instance.defName;
            if (string.Equals(dn, "GCF_Gestate_MechScorpion", StringComparison.Ordinal))
            {
                if (!GameComponent_GiantProgression.IsWarQueenDefeatedOrAnalyzed())
                {
                    __result = false;
                }
                return;
            }

            if (!GiantProgressionUtility.EvaluateExtraRecipeGate(dn))
            {
                __result = false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
        public static bool Thing_TakeDamage_VaultStasis_Prefix(Thing __instance, ref DamageWorker.DamageResult __result)
        {
            if (!GiantDormantLockRegistry.HasAnyLocked || !GiantDormantLockRegistry.IsLocked(__instance))
            {
                return true;
            }
            __result = new DamageWorker.DamageResult();
            return false;
        }
    }
}
