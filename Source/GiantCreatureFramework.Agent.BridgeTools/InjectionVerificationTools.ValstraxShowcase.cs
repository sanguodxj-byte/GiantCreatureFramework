using System.Reflection;
using System.Text;
using System.Text.Json;
using GiantCreatureFramework;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework.Agent.BridgeTools;

public sealed partial class InjectionVerificationTools
{
    private sealed class ValstraxAudioCueEntry
    {
        public int Frame { get; set; }
        public float TimeSec { get; set; }
        public string Cue { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
        public string Detail { get; set; } = string.Empty;
    }

    private sealed class ValstraxIntervalEntry
    {
        public float StartSec { get; set; }
        public float EndSec { get; set; } = -1f;
    }

    private static readonly List<ValstraxAudioCueEntry> ValstraxCueLog = new();
    private static readonly List<ValstraxIntervalEntry> ValstraxActiveLoops = new();
    private static readonly List<string> ValstraxHuntEventLog = new();
    private static readonly HashSet<string> ValstraxObservedPartBreaks = new(StringComparer.Ordinal);
    private static bool valstraxRecorderHookRegistered;
    private static bool valstraxCueTrackingEnabled;
    private static bool valstraxSkillDirectorEnabled;
    private static string valstraxGifSkillChainMode = string.Empty;
    private static string lastValstraxDragonState = "Calm";
    private static string lastValstraxActionClip = string.Empty;
    private static bool lastValstraxDead;
    private static bool lastValstraxAirborne;
    private static int lastValstraxHitCueFrame = -999;
    private static int lastValstraxInjuryCount;
    private static float valstraxKoTimeSec = -1f;
    private static bool castIntakeTriggered;
    private static bool castSpinSlashTriggered;
    private static bool castDragonCannonTriggered;
    private static bool castWingThrustTriggered;
    private static bool castCometTriggered;

    private static void EnsureValstraxRecorderHook()
    {
        if (valstraxRecorderHookRegistered)
        {
            return;
        }
        valstraxRecorderHookRegistered = true;
        GiantArenaVideoRecorder.OnRecorderFrameStepped += OnValstraxRecorderFrame;
    }

    private static ThingComp? FindValstraxEnergyComp(Pawn? valstrax)
    {
        if (valstrax?.AllComps == null) return null;
        for (int i = 0; i < valstrax.AllComps.Count; i++)
        {
            ThingComp c = valstrax.AllComps[i];
            if (c != null && c.GetType().Name == "CompValstraxDragonEnergy")
            {
                return c;
            }
        }
        return null;
    }

    private static string GetValstraxDragonStateName(ThingComp? energyComp)
    {
        if (energyComp == null) return "Calm";
        object? val = energyComp.GetType().GetProperty("DragonState")?.GetValue(energyComp);
        return val?.ToString() ?? "Calm";
    }

    private static bool TryQueueValstraxAbility(Pawn valstrax, string abilityDefName, LocalTargetInfo target)
    {
        if (valstrax?.abilities?.abilities == null || valstrax.Dead || valstrax.Downed)
        {
            return false;
        }
        if (valstrax.stances?.curStance is Stance_Warmup)
        {
            return false;
        }
        Ability? ab = valstrax.abilities.abilities.FirstOrDefault(a => a?.def?.defName == abilityDefName);
        if (ab == null)
        {
            return false;
        }
        ab.ResetCooldown();
        if (!ab.def.targetRequired)
        {
            Job job = ab.GetJob(valstrax, valstrax);
            if (job != null)
            {
                valstrax.jobs?.StartJob(job, JobCondition.InterruptForced);
                return true;
            }
        }
        else if (target.IsValid)
        {
            Job job = ab.GetJob(target, target);
            if (job != null)
            {
                valstrax.jobs?.StartJob(job, JobCondition.InterruptForced);
                return true;
            }
        }
        return false;
    }

    private static Pawn? PickBestLiveKiiroTarget(Map map, Pawn valstrax)
    {
        return map.mapPawns.AllPawnsSpawned
            .Where(p => p != null && p != valstrax && p.RaceProps?.Humanlike == true && !p.Dead && !p.Downed)
            .OrderByDescending(p => p.Position.DistanceToSquared(valstrax.Position))
            .FirstOrDefault()
            ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p != null && p != valstrax && !p.Dead);
    }

    private static void AddAudioCue(int frame, float timeSec, string cue, float vol = 1f, string detail = "")
    {
        ValstraxCueLog.Add(new ValstraxAudioCueEntry
        {
            Frame = frame,
            TimeSec = (float)Math.Round(timeSec, 3),
            Cue = cue,
            Volume = vol,
            Detail = detail
        });
    }

    private static void OnValstraxRecorderFrame(int frame, float timeSec, Map? map)
    {
        if (!valstraxCueTrackingEnabled || map == null)
        {
            return;
        }

        Pawn? valstrax = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p?.def?.defName == "GCF_Valstrax")
            ?? map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)
                .OfType<Corpse>()
                .Select(c => c.InnerPawn)
                .FirstOrDefault(p => p?.def?.defName == "GCF_Valstrax");
        if (valstrax == null)
        {
            return;
        }

        if (frame == 4)
        {
            AddAudioCue(frame, timeSec, "roar", 0.95f, "Encounter opening roar");
        }

        bool isDeadNow = valstrax.Dead;
        if (isDeadNow && !lastValstraxDead)
        {
            lastValstraxDead = true;
            valstraxKoTimeSec = timeSec;
            AddAudioCue(frame, timeSec, "death", 1.0f, "Valstrax slain");
            ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 讨伐成功！天彗龙陨落！");
            if (ValstraxActiveLoops.Count > 0 && ValstraxActiveLoops[^1].EndSec < 0f)
            {
                ValstraxActiveLoops[^1].EndSec = timeSec;
            }
            return;
        }
        if (isDeadNow)
        {
            return;
        }

        CompGiantRig? rig = GiantFastLookup.TryGetRig(valstrax);
        ThingComp? energyComp = FindValstraxEnergyComp(valstrax);
        string curState = GetValstraxDragonStateName(energyComp);

        if (!string.Equals(curState, lastValstraxDragonState, StringComparison.Ordinal))
        {
            bool wasActive = lastValstraxDragonState is "DragonActive" or "CometAmbush";
            bool nowActive = curState is "DragonActive" or "CometAmbush";

            if (curState == "Intaking")
            {
                AddAudioCue(frame, timeSec, "intake", 0.95f, "DragonIntake started");
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 天彗龙开启胸部龙气吸引");
            }
            else if (curState == "DragonActive" && lastValstraxDragonState == "Intaking")
            {
                AddAudioCue(frame, timeSec, "blast", 0.85f, "Dragonheart ignition concussive wave");
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 龙气活性点火！六喷口赫星喷流全开");
            }
            else if (curState == "CometAmbush")
            {
                AddAudioCue(frame, timeSec, "wingthrust", 1.0f, "CometAmbush supersonic liftoff");
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 大招「彗星」超音速升空！");
            }
            else if (lastValstraxDragonState == "CometAmbush" && curState != "CometAmbush")
            {
                AddAudioCue(frame, timeSec, "comet_impact", 1.0f, "CometAmbush meteor touchdown");
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 「彗星」赫星坠地大爆炸！");
            }

            if (!wasActive && nowActive)
            {
                ValstraxActiveLoops.Add(new ValstraxIntervalEntry { StartSec = timeSec, EndSec = -1f });
            }
            else if (wasActive && !nowActive && ValstraxActiveLoops.Count > 0 && ValstraxActiveLoops[^1].EndSec < 0f)
            {
                ValstraxActiveLoops[^1].EndSec = timeSec;
            }

            lastValstraxDragonState = curState;
        }

        string activeClip = string.Empty;
        float clipTick = 0f;
        rig?.TryGetActiveActionClipAndTick(out activeClip, out clipTick);
        if (!string.IsNullOrEmpty(activeClip) && !string.Equals(activeClip, lastValstraxActionClip, StringComparison.Ordinal))
        {
            if (activeClip.IndexOf("JetSpinSlash", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAudioCue(frame, timeSec, "spinslash", 0.95f, activeClip);
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 释放「回旋翼斩」");
            }
            else if (activeClip.IndexOf("DragonCannon", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAudioCue(frame, timeSec, "cannon", 0.98f, activeClip);
                AddAudioCue(frame + 48, timeSec + 0.80f, "blast", 0.88f, "DragonCannon impact");
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 释放「翼枪前翻·龙气炮」");
            }
            else if (activeClip.IndexOf("JetWingThrust", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAudioCue(frame, timeSec, "wingthrust", 0.95f, activeClip);
                ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 释放「超音速翼枪突刺」");
            }
            else if (activeClip.IndexOf("FierceBite", StringComparison.OrdinalIgnoreCase) >= 0
                  || activeClip.IndexOf("ClawSlash", StringComparison.OrdinalIgnoreCase) >= 0
                  || activeClip.IndexOf("TailSpear", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (frame - lastValstraxHitCueFrame >= 270)
                {
                    AddAudioCue(frame + 10, timeSec + 0.16f, "wingslash_hit", 0.34f, activeClip);
                    lastValstraxHitCueFrame = frame;
                }
            }
            lastValstraxActionClip = activeClip;
        }
        else if (string.IsNullOrEmpty(activeClip))
        {
            lastValstraxActionClip = string.Empty;
        }

        // Track injury count without triggering Valstrax's heavy wing-slash SFX on hunter melee hits
        int curInjuries = valstrax.health?.hediffSet?.hediffs?.Count ?? 0;
        if (curInjuries > lastValstraxInjuryCount)
        {
            lastValstraxInjuryCount = curInjuries;
        }

        if (valstrax.health?.hediffSet != null)
        {
            foreach (Hediff_MissingPart mp in valstrax.health.hediffSet.GetMissingPartsCommonAncestors())
            {
                string key = mp.Part?.LabelCap ?? mp.LabelCap;
                if (!string.IsNullOrEmpty(key) && ValstraxObservedPartBreaks.Add(key))
                {
                    AddAudioCue(frame, timeSec, "pain", 0.92f, "Part broken: " + key);
                    ValstraxHuntEventLog.Add($"[{timeSec:F1}s] 部位破坏：{key}");
                }
            }
        }

        // GIF skill chain sequencing
        if (!string.IsNullOrEmpty(valstraxGifSkillChainMode) && valstrax.Spawned)
        {
            Pawn? target = PickBestLiveKiiroTarget(map, valstrax);
            if (valstraxGifSkillChainMode == "intake_spinslash")
            {
                if (!castIntakeTriggered && timeSec >= 0.35f)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_DragonIntake", valstrax))
                    {
                        castIntakeTriggered = true;
                    }
                }
                else if (castIntakeTriggered && !castSpinSlashTriggered && curState == "DragonActive" && timeSec >= 1.85f)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_JetSpinSlash", valstrax))
                    {
                        castSpinSlashTriggered = true;
                    }
                }
            }
            else if (valstraxGifSkillChainMode == "cannon_wingthrust")
            {
                if (!castDragonCannonTriggered && timeSec >= 0.30f && target != null)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_DragonCannon", target))
                    {
                        castDragonCannonTriggered = true;
                    }
                }
                else if (castDragonCannonTriggered && !castWingThrustTriggered && timeSec >= 3.00f && string.IsNullOrEmpty(activeClip) && target != null)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_JetWingThrust", target))
                    {
                        castWingThrustTriggered = true;
                    }
                }
            }
            else if (valstraxGifSkillChainMode == "comet_ambush")
            {
                if (!castCometTriggered && timeSec >= 0.35f && target != null)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_CometAmbush", target))
                    {
                        castCometTriggered = true;
                    }
                }
            }
            return;
        }

        // Full Hunt Choreographer: ensure Kiiro Valor heroes stay relentlessly engaged and Valstrax showcases its full moveset!
        if (valstraxSkillDirectorEnabled && valstrax.Spawned && rig != null)
        {
            bool bossInCometSky = curState == "CometAmbush"
                || rig.IsAirborneOrTakingOff
                || GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(null, valstrax);
            if (bossInCometSky != lastValstraxAirborne || frame % 8 == 0)
            {
                lastValstraxAirborne = bossInCometSky;
                MaintainKiiroHeroEngagement(map, valstrax, bossInCometSky);
            }

            float hpPct = GiantBossHealthBarHUD.ComputeSynchronizedGiantHealthPercent(valstrax, rig);
            Pawn? target = PickBestLiveKiiroTarget(map, valstrax);

            if (!castIntakeTriggered && timeSec >= 2.2f && curState == "Calm")
            {
                if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_DragonIntake", valstrax))
                {
                    castIntakeTriggered = true;
                }
            }
            else if (curState == "DragonActive" && string.IsNullOrEmpty(activeClip))
            {
                if (!castSpinSlashTriggered && (hpPct <= 0.88f || timeSec >= 8.5f))
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_JetSpinSlash", valstrax))
                    {
                        castSpinSlashTriggered = true;
                    }
                }
                else if (!castDragonCannonTriggered && (hpPct <= 0.74f || timeSec >= 15.5f) && target != null)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_DragonCannon", target))
                    {
                        castDragonCannonTriggered = true;
                    }
                }
                else if (!castWingThrustTriggered && (hpPct <= 0.60f || timeSec >= 22.0f) && target != null)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_JetWingThrust", target))
                    {
                        castWingThrustTriggered = true;
                    }
                }
                else if (!castCometTriggered && (hpPct <= 0.48f || timeSec >= 28.0f) && target != null)
                {
                    if (TryQueueValstraxAbility(valstrax, "GCF_Ability_Valstrax_CometAmbush", target))
                    {
                        castCometTriggered = true;
                    }
                }
            }
        }
    }

    private static void MaintainKiiroHeroEngagement(Map map, Pawn valstrax, bool bossAirborne = false)
    {
        if (map?.mapPawns?.AllPawnsSpawned == null || valstrax == null || !valstrax.Spawned || valstrax.Dead)
        {
            return;
        }
        IntVec3 summitAnchor = new IntVec3(Mathf.RoundToInt(map.Size.x * 0.51f), 0, Mathf.RoundToInt(map.Size.z * 0.80f));
        IntVec3[] tacticalSpreadOffsets =
        [
            new IntVec3(-8, 0, -4),
            new IntVec3(-7, 0, +4),
            new IntVec3(0,  0, -5),
            new IntVec3(+7, 0, -3),
            new IntVec3(+7, 0, +4)
        ];

        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
        int heroIdx = 0;
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn h = pawns[i];
            if (h == null || h == valstrax || h.Dead || h.Downed || !h.Spawned || h.RaceProps?.Humanlike != true)
            {
                continue;
            }
            int slot = heroIdx++;
            if (h.playerSettings != null)
            {
                h.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
            }
            if (h.drafter != null && !h.drafter.Drafted)
            {
                h.drafter.Drafted = true;
            }
            if (h.needs?.food != null) h.needs.food.CurLevelPercentage = 1f;
            if (h.needs?.rest != null) h.needs.rest.CurLevelPercentage = 1f;
            if (h.InMentalState)
            {
                h.mindState?.mentalStateHandler?.Reset();
            }
            if (h.mindState != null)
            {
                h.mindState.canFleeIndividual = false;
                h.mindState.enemyTarget = valstrax;
            }

            if (bossAirborne)
            {
                MethodInfo? puaStop = GenTypes.GetTypeInAnyAssembly("PUABodyAnimation.PUAAnimTracker")
                    ?.GetMethod("TryStop", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Pawn) }, null);
                puaStop?.Invoke(null, new object[] { h });

                // Let an already-issued tactical reposition finish without interrupting pathing or snapping rotation
                if (h.CurJobDef == JobDefOf.Goto || (h.pather != null && h.pather.Moving))
                {
                    continue;
                }

                IntVec3 desired = (summitAnchor + tacticalSpreadOffsets[slot % tacticalSpreadOffsets.Length]).ClampInsideMap(map);
                IntVec3 dest = CellFinder.StandableCellNear(desired, map, 3f);
                if (dest.IsValid && h.Position.DistanceToSquared(dest) > 16 && h.CanReach(dest, PathEndMode.OnCell, Danger.Deadly))
                {
                    Job reposition = JobMaker.MakeJob(JobDefOf.Goto, dest);
                    reposition.playerForced = true;
                    reposition.expiryInterval = 240;
                    reposition.checkOverrideOnExpire = false;
                    h.jobs?.StartJob(reposition, JobCondition.InterruptForced);
                    continue;
                }

                Vector3 toBoss = valstrax.DrawPos - h.DrawPos;
                toBoss.y = 0f;
                float bossAngle = toBoss.sqrMagnitude > 0.04f ? toBoss.AngleFlat() : h.Rotation.AsAngle;
                Rot4 steadyFacing = Rot4.FromAngleFlat(bossAngle);

                // Use Wait_Combat with non-Invalid overrideFacing so JobDriver_Wait sets handlingFacing=true
                // (preventing Pawn_RotationTracker.UpdateRotation from snapping Drafted pawns to Rot4.South!)
                if (h.CurJob != null && h.CurJob.def == JobDefOf.Wait_Combat && h.CurJob.overrideFacing != Rot4.Invalid)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(h.CurJob.overrideFacing.AsAngle, bossAngle)) > 68f)
                    {
                        h.CurJob.overrideFacing = steadyFacing;
                    }
                }
                else
                {
                    Job holdGuard = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                    holdGuard.playerForced = true;
                    holdGuard.overrideFacing = steadyFacing;
                    holdGuard.expiryInterval = 2400;
                    holdGuard.checkOverrideOnExpire = false;
                    h.jobs?.StartJob(holdGuard, JobCondition.InterruptForced);
                }
                continue;
            }

            if (h.CurJob == null || (h.CurJob.def != JobDefOf.AttackMelee && h.CurJob.def != JobDefOf.AttackStatic))
            {
                Job atk = JobMaker.MakeJob(JobDefOf.AttackMelee, valstrax);
                atk.playerForced = true;
                atk.expiryInterval = 60000;
                atk.checkOverrideOnExpire = false;
                h.jobs?.StartJob(atk, JobCondition.InterruptForced);
            }
        }
    }

    private static Map EnsureRuinedPinnacleBattleMap()
    {
        if (Find.Maps != null)
        {
            foreach (Map existing in Find.Maps)
            {
                if (existing?.Parent is Site existingSite &&
                    existingSite.parts != null &&
                    existingSite.parts.Any(sp => sp?.def?.defName == "GCF_SitePart_RuinedPinnacle"))
                {
                    return existing;
                }
            }
        }

        SitePartDef? pinnacleDef = DefDatabase<SitePartDef>.GetNamedSilentFail("GCF_SitePart_RuinedPinnacle");
        if (pinnacleDef == null)
        {
            return Find.CurrentMap ?? throw new InvalidOperationException("Missing GCF_SitePart_RuinedPinnacle");
        }

        PlanetTile nearTile = Find.AnyPlayerHomeMap != null ? Find.AnyPlayerHomeMap.Tile : PlanetTile.Invalid;
        if (!TileFinder.TryFindNewSiteTile(out PlanetTile siteTile, nearTile, minDist: 2, maxDist: 14, allowCaravans: false))
        {
            TileFinder.TryFindNewSiteTile(out siteTile, minDist: 2, maxDist: 20);
        }

        Site site = SiteMaker.MakeSite(
            Gen.YieldSingle(new SitePartDefWithParams(pinnacleDef, new SitePartParams { threatPoints = 0f })),
            siteTile,
            null);
        site.customLabel = "遗群岭";
        Find.WorldObjects.Add(site);

        Map generated = GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, new IntVec3(200, 1, 200), null);
        generated.fogGrid?.ClearAllFog();
        return generated;
    }

    private static IntVec3 PrepareRuinedPinnacleSummitArena(Map map, out CellRect summitInnerRect)
    {
        int cx = Mathf.RoundToInt(map.Size.x * 0.51f);
        int cz = Mathf.RoundToInt(map.Size.z * 0.80f);
        IntVec3 summitCenter = new IntVec3(cx, 0, cz);
        summitInnerRect = CellRect.CenteredOn(summitCenter, 28, 18).ClipInsideMap(map);

        TerrainDef flagstone = DefDatabase<TerrainDef>.GetNamedSilentFail("FlagstoneGranite")
            ?? DefDatabase<TerrainDef>.GetNamedSilentFail("FlagstoneSandstone")
            ?? TerrainDefOf.PavedTile;

        // Ensure central clash circle on Northern Summit is unroofed, standable, and clear of blocking boulders while preserving outer columns & SkyChasm
        CellRect clearCore = CellRect.CenteredOn(summitCenter, 22, 13).ClipInsideMap(map);
        var scratch = new List<Thing>(16);
        foreach (IntVec3 c in clearCore)
        {
            map.roofGrid?.SetRoof(c, null);
            map.fogGrid?.Unfog(c);
            TerrainDef curTer = map.terrainGrid.TerrainAt(c);
            if (curTer != null && (curTer.defName == "GCF_Terrain_SkyChasm" || curTer.passability == Traversability.Impassable))
            {
                map.terrainGrid.SetTerrain(c, flagstone);
            }
            scratch.Clear();
            scratch.AddRange(c.GetThingList(map));
            for (int i = 0; i < scratch.Count; i++)
            {
                Thing t = scratch[i];
                if (t is not Pawn && !t.Destroyed && (t.def.passability != Traversability.Standable || t.def.category == ThingCategory.Item))
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }
        return summitCenter;
    }

    private static Pawn SpawnKiiroValorHero(
        Map map,
        Faction faction,
        IntVec3 spawnPos,
        string preferredKindName,
        string fallbackBackstoryDefName,
        string weaponDefName,
        QualityCategory quality,
        Pawn targetBoss)
    {
        Pawn? hero = null;
        PawnKindDef? prefKind = DefDatabase<PawnKindDef>.GetNamedSilentFail(preferredKindName);
        PawnKindDef fallbackKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Kiiro_Colonist")
            ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("KiiroValor_GuildGuard")
            ?? PawnKindDefOf.Colonist;

        if (prefKind != null)
        {
            try
            {
                hero = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    prefKind,
                    faction,
                    PawnGenerationContext.PlayerStarter,
                    map.Tile,
                    forceGenerateNewPawn: true));
            }
            catch
            {
                hero = null;
            }
        }

        if (hero == null)
        {
            hero = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                fallbackKind,
                faction,
                PawnGenerationContext.PlayerStarter,
                map.Tile,
                forceGenerateNewPawn: true));
        }

        if (hero.Faction != faction)
        {
            hero.SetFaction(faction);
        }

        if (hero.story != null)
        {
            BackstoryDef? heroBs = DefDatabase<BackstoryDef>.GetNamedSilentFail(fallbackBackstoryDefName);
            if (heroBs != null && preferredKindName == "Kiiro_Colonist")
            {
                hero.story.Adulthood = heroBs;
            }
            if (hero.WorkTagIsDisabled(WorkTags.Violent))
            {
                BackstoryDef? validChild = DefDatabase<BackstoryDef>.AllDefsListForReading
                    .FirstOrDefault(b => b.slot == BackstorySlot.Childhood && (b.workDisables & WorkTags.Violent) == 0);
                BackstoryDef? validAdult = DefDatabase<BackstoryDef>.AllDefsListForReading
                    .FirstOrDefault(b => b.slot == BackstorySlot.Adulthood && (b.workDisables & WorkTags.Violent) == 0);
                if (validChild != null) hero.story.Childhood = validChild;
                if (validAdult != null) hero.story.Adulthood = validAdult;
                hero.story.traits?.allTraits?.RemoveAll(t => (t.def.disabledWorkTags & WorkTags.Violent) != 0);
                hero.Notify_DisabledWorkTypesChanged();
            }

            TraitDef? valorTrait = DefDatabase<TraitDef>.GetNamedSilentFail("KiiroValor_Trait_ValorHero")
                ?? DefDatabase<TraitDef>.GetNamedSilentFail("KittyValor");
            if (valorTrait != null && hero.story.traits != null && !hero.story.traits.HasTrait(valorTrait))
            {
                hero.story.traits.GainTrait(new Trait(valorTrait, 0, forced: true));
            }
            TraitDef? toughTrait = DefDatabase<TraitDef>.GetNamedSilentFail("Tough");
            if (toughTrait != null && hero.story.traits != null && !hero.story.traits.HasTrait(toughTrait))
            {
                hero.story.traits.GainTrait(new Trait(toughTrait, 0, forced: true));
            }
        }

        // Force Melee = 20 with Major Passion + Archotech limbs so the 5 Valor heroes close distance briskly after supersonic dashes
        SkillRecord? melee = hero.skills?.GetSkill(SkillDefOf.Melee);
        if (melee != null)
        {
            melee.Level = 20;
            melee.passion = Passion.Major;
            melee.xpSinceLastLevel = 1000f;
        }
        if (hero.health?.hediffSet != null && hero.RaceProps?.body != null)
        {
            HediffDef? archArm = DefDatabase<HediffDef>.GetNamedSilentFail("ArchotechArm")
                ?? DefDatabase<HediffDef>.GetNamedSilentFail("BionicArm");
            HediffDef? archLeg = DefDatabase<HediffDef>.GetNamedSilentFail("ArchotechLeg")
                ?? DefDatabase<HediffDef>.GetNamedSilentFail("BionicLeg");
            if (archArm != null)
            {
                foreach (BodyPartRecord shoulder in hero.RaceProps.body.AllParts.Where(p => p.def == BodyPartDefOf.Shoulder).ToList())
                {
                    if (!hero.health.hediffSet.PartIsMissing(shoulder))
                    {
                        hero.health.AddHediff(archArm, shoulder);
                    }
                }
            }
            if (archLeg != null)
            {
                foreach (BodyPartRecord leg in hero.RaceProps.body.AllParts.Where(p => p.def == BodyPartDefOf.Leg).ToList())
                {
                    if (!hero.health.hediffSet.PartIsMissing(leg))
                    {
                        hero.health.AddHediff(archLeg, leg);
                    }
                }
            }
        }

        // Equip iconic Kiiro Valor adventure outfit + inner Plasteel FlakVest (keeping cute Kiiro ears visible while surviving Elder Dragon AoE!)
        if (hero.apparel != null)
        {
            string[] armorDefNames =
            [
                "KiiroValor_Apparel_SolenaAdventureOutfit",
                "Apparel_FlakVest",
                "Apparel_FlakPants"
            ];
            foreach (string adName in armorDefNames)
            {
                ThingDef? armorDef = DefDatabase<ThingDef>.GetNamedSilentFail(adName);
                if (armorDef == null) continue;
                try
                {
                    ThingDef? stuff = armorDef.MadeFromStuff ? ThingDefOf.Plasteel : null;
                    Apparel ap = (Apparel)ThingMaker.MakeThing(armorDef, stuff);
                    ap.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Legendary, ArtGenerationContext.Colony);
                    if (ApparelUtility.HasPartsToWear(hero, armorDef))
                    {
                        hero.apparel.Wear(ap, dropReplacedApparel: false);
                    }
                }
                catch
                {
                }
            }
        }

        // Equip signature MH weapon
        ThingDef? wDef = DefDatabase<ThingDef>.GetNamedSilentFail(weaponDefName);
        if (wDef != null && hero.equipment != null)
        {
            hero.equipment.DestroyAllEquipment();
            ThingDef? wStuff = wDef.MadeFromStuff ? ThingDefOf.Plasteel : null;
            ThingWithComps weapon = (ThingWithComps)ThingMaker.MakeThing(wDef, wStuff);
            weapon.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Colony);
            hero.equipment.AddEquipment(weapon);
        }

        IntVec3 cell = CellFinder.StandableCellNear(spawnPos, map, 4f);
        GenSpawn.Spawn(hero, cell.IsValid ? cell : spawnPos, map, Rot4.East);

        if (hero.playerSettings != null)
        {
            hero.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
        }
        if (hero.drafter != null)
        {
            hero.drafter.Drafted = true;
        }
        if (hero.mindState != null)
        {
            hero.mindState.canFleeIndividual = false;
            hero.mindState.enemyTarget = targetBoss;
        }
        Job atk = JobMaker.MakeJob(JobDefOf.AttackMelee, targetBoss);
        atk.playerForced = true;
        atk.expiryInterval = 60000;
        atk.checkOverrideOnExpire = false;
        hero.jobs?.StartJob(atk, JobCondition.InterruptForced);

        return hero;
    }

    [Tool("gcfagent/stage_valstrax_ruined_pinnacle_hunt", Description = "Stage 5 Melee-20 Kiiro Valor Heroes wielding all 5 MHWilds weapons against GCF_Valstrax atop the Ruined Pinnacle summit with custom Boss Health Bar and audio cue telemetry.")]
    public Task<object> StageValstraxRuinedPinnacleHunt(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "MH weapon quality: Awful, Poor, Normal, Good, Excellent, Masterwork, Legendary.")] string weaponQuality = "Legendary",
        [ToolParameter(Description = "Horizontal spawn separation in cells on the Northern Summit.")] int spawnSeparation = 16,
        [ToolParameter(Description = "Enable scripted Valstrax skill progression triggers during real combat.")] bool enableSkillDirector = true,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            EnsureValstraxRecorderHook();
            GiantArenaService.StopArena(restoreSettings: false);

            Map map = EnsureRuinedPinnacleBattleMap();
            Current.Game.CurrentMap = map;
            IntVec3 summitCenter = PrepareRuinedPinnacleSummitArena(map, out CellRect summitInner);

            // Clear all existing pawns on the summit map before spawning fresh actors
            foreach (Pawn old in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (!old.Destroyed) old.Destroy(DestroyMode.Vanish);
            }
            foreach (Thing corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).ToList())
            {
                if (!corpse.Destroyed) corpse.Destroy(DestroyMode.Vanish);
            }

            Faction facPlayers = Faction.OfPlayerSilentFail
                ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.Ancients)
                ?? Find.FactionManager.AllFactionsListForReading.First();
            Faction facHostile = Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile)
                ?? Faction.OfMechanoids
                ?? Find.FactionManager.AllFactionsListForReading.First(f => f != facPlayers);
            facPlayers.TryAffectGoodwillWith(facHostile, -200, canSendMessage: false, canSendHostilityLetter: false);

            PawnKindDef valKind = DefDatabase<PawnKindDef>.GetNamed("GCF_Valstrax");
            Pawn valstrax = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                valKind,
                facHostile,
                PawnGenerationContext.NonPlayer,
                map.Tile,
                forceGenerateNewPawn: true));
            IntVec3 bossPos = new IntVec3(summitCenter.x + spawnSeparation / 2, 0, summitCenter.z);
            GenSpawn.Spawn(valstrax, bossPos, map, Rot4.West);
            GiantFastLookup.TryGetRig(valstrax)?.RebuildFootprintNow(notifyMap: true);

            // Delay ultimate CometAmbush by ~25s so Phase 1 showcases ground Dragonheart ignition, Wing Thrust, Spin Slash & Dragon Cannon first!
            Ability? cometAb = valstrax.abilities?.abilities?.FirstOrDefault(a => a?.def?.defName == "GCF_Ability_Valstrax_CometAmbush");
            cometAb?.StartCooldown(1500);

            if (!Enum.TryParse(weaponQuality, ignoreCase: true, out QualityCategory qCat))
            {
                qCat = QualityCategory.Legendary;
            }

            (string kindName, string bsName, string weaponDef, int zOffset)[] heroSpecs =
            [
                ("Kiiro_Valor", "kiiro_war_king",          "MHWilds_Weapon_GreatSword",  -4),
                ("Kiiro_Valor", "kiiro_war_veteran",       "MHWilds_Weapon_LongSword",   -2),
                ("Kiiro_Valor", "kiiro_strange_overcomer", "MHWilds_Weapon_ChargeBlade",  0),
                ("Kiiro_Valor", "kiiro_war_king",          "MHWilds_Weapon_DualBlades",  +2),
                ("Kiiro_Valor", "kiiro_war_veteran",       "MHWilds_Weapon_Gunlance",    +4)
            ];

            var heroes = new List<Pawn>(5);
            var heroSummaries = new List<object>(5);
            int heroX = summitCenter.x - spawnSeparation / 2;
            foreach (var hs in heroSpecs)
            {
                Pawn h = SpawnKiiroValorHero(
                    map,
                    facPlayers,
                    new IntVec3(heroX, 0, summitCenter.z + hs.zOffset),
                    hs.kindName,
                    hs.bsName,
                    hs.weaponDef,
                    qCat,
                    valstrax);
                heroes.Add(h);
                heroSummaries.Add(new
                {
                    id = h.thingIDNumber,
                    name = h.LabelShortCap,
                    kind = h.kindDef?.defName,
                    adultTitle = h.story?.Adulthood?.TitleCapFor(h.gender),
                    melee = h.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0,
                    weapon = h.equipment?.Primary?.LabelCap ?? "None",
                    weaponDef = h.equipment?.Primary?.def?.defName ?? "None"
                });
            }

            // Reset audio & choreography telemetry
            ValstraxCueLog.Clear();
            ValstraxActiveLoops.Clear();
            ValstraxHuntEventLog.Clear();
            ValstraxObservedPartBreaks.Clear();
            lastValstraxDragonState = "Calm";
            lastValstraxActionClip = string.Empty;
            lastValstraxDead = false;
            lastValstraxAirborne = false;
            lastValstraxHitCueFrame = -999;
            lastValstraxInjuryCount = 0;
            valstraxKoTimeSec = -1f;
            castIntakeTriggered = false;
            castSpinSlashTriggered = false;
            castDragonCannonTriggered = false;
            castWingThrustTriggered = false;
            castCometTriggered = false;
            valstraxGifSkillChainMode = string.Empty;
            valstraxSkillDirectorEnabled = enableSkillDirector;
            valstraxCueTrackingEnabled = true;

            var arenaCfg = new GiantArenaMatchConfig
            {
                Preset = "RuinedPinnacle",
                SideALabel = "绮罗勇者讨伐队",
                SideBLabel = "银翼的凶星·天彗龙",
                StartPaused = true,
                LockNoonClearWeather = true,
                CleanVideoMode = true,
                CinematicHudOnly = true,
                EnableVersusHud = false,
                HideDevToolbar = true
            };

            GiantArenaMatchState st = GiantArenaService.BindCustomMapEncounter(
                map,
                summitCenter,
                summitInner,
                heroes,
                facPlayers,
                new List<Pawn> { valstrax },
                facHostile,
                arenaCfg,
                out string bindErr);

            MaintainKiiroHeroEngagement(map, valstrax);
            GiantArenaService.FrameMatchCamera(1.0f, 8.5f, 15f, 28f);

            return new
            {
                ok = string.IsNullOrEmpty(bindErr),
                error = bindErr,
                mapSize = map.Size.ToString(),
                summitCenter = summitCenter.ToString(),
                matchId = st.MatchId,
                heroes = heroSummaries,
                valstraxId = valstrax.thingIDNumber,
                valstraxLabel = valstrax.LabelCap
            };
        }, cancellationToken);

    [Tool("gcfagent/dryrun_valstrax_hunt", Description = "Fast-step ticks of a staged Ruined Pinnacle hunt without video encoding to verify battle duration, part breaks, and winner.")]
    public Task<object> DryRunValstraxHunt(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Max ticks to simulate (e.g. 7200 = 120s).")] int maxTicks = 7200,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            Map? map = Find.CurrentMap;
            TickManager? tm = Find.TickManager;
            if (map == null || tm == null)
            {
                return new { ok = false, error = "No active map" };
            }
            int stepped = 0;
            int cannonWindupFrames = 0;
            float cannonMinTick = 999f;
            float cannonMaxTick = -1f;
            int weaponSheathedFrames = 0;
            int movingFacingMismatchFrames = 0;
            int stationaryRotationFlipFrames = 0;
            int airSwingFrames = 0;
            var lastStationaryRotByPawn = new Dictionary<int, (Rot4 rot, int changeFrame)>();
            var airSwingSamples = new List<string>(24);

            MethodInfo? puaIsPlayingMethod = GenTypes.GetTypeInAnyAssembly("PUABodyAnimation.PUAAnimTracker")
                ?.GetMethod("IsPlaying", BindingFlags.Public | BindingFlags.Static);

            for (int f = 0; f < maxTicks; f++)
            {
                tm.DoSingleTick();
                OnValstraxRecorderFrame(f, f / 60f, map);
                GiantArenaService.SyncPawnTweenersForLockstepFrame(map, 1);
                stepped++;

                Pawn? val = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p?.def?.defName == "GCF_Valstrax");
                CompGiantRig? vRig = val != null ? GiantFastLookup.TryGetRig(val) : null;
                if (vRig != null && vRig.TryGetActiveActionClipAndTick(out string clip, out float cTick)
                    && clip.IndexOf("DragonCannon", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (cTick < 55f) cannonWindupFrames++;
                    if (cTick < cannonMinTick) cannonMinTick = cTick;
                    if (cTick > cannonMaxTick) cannonMaxTick = cTick;
                }

                IReadOnlyList<Pawn> allPawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < allPawns.Count; i++)
                {
                    Pawn hp = allPawns[i];
                    if (hp == null || hp == val || hp.Dead || hp.Downed || !hp.Spawned || hp.RaceProps?.Humanlike != true)
                    {
                        continue;
                    }
                    if (!PawnRenderUtility.CarryWeaponOpenly(hp))
                    {
                        weaponSheathedFrames++;
                    }

                    bool isMoving = hp.pather != null && hp.pather.Moving;
                    bool isPuaPlaying = puaIsPlayingMethod != null && (bool)(puaIsPlayingMethod.Invoke(null, new object[] { hp }) ?? false);

                    if (!isMoving && !isPuaPlaying && !(hp.stances?.curStance is Stance_Busy))
                    {
                        if (lastStationaryRotByPawn.TryGetValue(hp.thingIDNumber, out var prevRot))
                        {
                            if (prevRot.rot != hp.Rotation)
                            {
                                if (f - prevRot.changeFrame <= 15)
                                {
                                    stationaryRotationFlipFrames++;
                                }
                                lastStationaryRotByPawn[hp.thingIDNumber] = (hp.Rotation, f);
                            }
                        }
                        else
                        {
                            lastStationaryRotByPawn[hp.thingIDNumber] = (hp.Rotation, f);
                        }
                    }
                    else
                    {
                        lastStationaryRotByPawn.Remove(hp.thingIDNumber);
                    }

                    if (val != null && val.Spawned && vRig != null && isPuaPlaying)
                    {
                        bool touchingHull = GiantHullInteractionUtility.IsPawnTouchingGiantHitbox(hp, val);
                        float minHitCellDist = 999f;
                        List<IntVec3> hits = vRig.Footprint.hitCellPositions;
                        if (hits != null && hits.Count > 0)
                        {
                            for (int hIdx = 0; hIdx < hits.Count; hIdx++)
                            {
                                float dCell = hp.Position.DistanceTo(hits[hIdx]);
                                if (dCell < minHitCellDist) minHitCellDist = dCell;
                            }
                        }
                        else
                        {
                            minHitCellDist = (val.DrawPos - hp.DrawPos).MagnitudeHorizontal();
                        }

                        bool valAir = vRig.IsCurrentlyAirborne || GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(hp, val);
                        if (valAir || (!touchingHull && minHitCellDist > 2.6f))
                        {
                            airSwingFrames++;
                            if (airSwingSamples.Count < 24 && (airSwingFrames == 1 || f % 30 == 0))
                            {
                                string activeMove = string.Empty;
                                ThingComp? mhComp = hp.equipment?.Primary?.AllComps?.FirstOrDefault(c => c.GetType().Name == "CompMHWeapon");
                                if (mhComp != null)
                                {
                                    activeMove = mhComp.GetType().GetField("ActiveMoveDefName")?.GetValue(mhComp) as string ?? string.Empty;
                                }
                                string valTer = val.Position.InBounds(map) ? (map.terrainGrid.TerrainAt(val.Position)?.defName ?? "?") : "OOB";
                                airSwingSamples.Add(
                                    $"t={f / 60f:F2}s hero={hp.LabelShort} pos={hp.Position} mov={isMoving} job={hp.CurJobDef?.defName} move={activeMove} | valPos={val.Position} valDraw=({val.DrawPos.x:F1},{val.DrawPos.z:F1}) ter={valTer} air={valAir} touch={touchingHull} minHitDist={minHitCellDist:F2} centerDist={(val.DrawPos - hp.DrawPos).MagnitudeHorizontal():F2}");
                            }
                        }
                    }

                    if (hp.pather != null && hp.pather.Moving && hp.pather.curPath != null && hp.pather.curPath.NodesLeftCount >= 1
                        && !(hp.stances?.stunner?.Stunned == true && hp.stances.stunner.DisableRotation)
                        && !(hp.stances?.curStance is Stance_Busy busy && busy.focusTarg.IsValid))
                    {
                        IntVec3 d = hp.pather.nextCell - hp.Position;
                        if (d != IntVec3.Zero)
                        {
                            Rot4 expected = d.x > 0 ? Rot4.East : (d.x < 0 ? Rot4.West : (d.z > 0 ? Rot4.North : Rot4.South));
                            if (hp.Rotation != expected)
                            {
                                movingFacingMismatchFrames++;
                            }
                        }
                    }
                }

                GiantArenaMatchState cur = GiantArenaService.GetMatchState();
                if (cur.Status is "Finished" or "Draw")
                {
                    break;
                }
            }
            int wingSlashHitCueCount = ValstraxCueLog.Count(c => c.Cue == "wingslash_hit");
            GiantArenaMatchState fin = GiantArenaService.GetMatchState();
            return new
            {
                ok = true,
                steppedTicks = stepped,
                durationSec = Math.Round(stepped / 60.0, 2),
                status = fin.Status,
                winnerSide = fin.WinnerSide,
                sideAAlive = fin.SideAAlive,
                sideAHp = Math.Round(fin.SideATotalHealthPercent, 3),
                sideBHp = Math.Round(fin.SideBTotalHealthPercent, 3),
                cannonWindupFrames,
                cannonMinTick = cannonMinTick < 900f ? Math.Round(cannonMinTick, 2) : -1.0,
                cannonMaxTick = Math.Round(cannonMaxTick, 2),
                weaponSheathedFrames,
                movingFacingMismatchFrames,
                stationaryRotationFlipFrames,
                airSwingFrames,
                wingSlashHitCueCount,
                airSwingSamples,
                partBreaks = ValstraxObservedPartBreaks.ToList(),
                events = ValstraxHuntEventLog.ToList()
            };
        }, cancellationToken);

    [Tool("gcfagent/stage_valstrax_skill_gif", Description = "Stage a focused Valstrax ability showcase on the Ruined Pinnacle summit for Steam Workshop GIF recording (modes: intake_spinslash, cannon_wingthrust, comet_ambush).")]
    public Task<object> StageValstraxSkillGif(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Showcase mode: intake_spinslash, cannon_wingthrust, or comet_ambush.")] string mode = "intake_spinslash",
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            EnsureValstraxRecorderHook();
            GiantArenaService.StopArena(restoreSettings: false);

            Map map = EnsureRuinedPinnacleBattleMap();
            Current.Game.CurrentMap = map;
            IntVec3 summitCenter = PrepareRuinedPinnacleSummitArena(map, out CellRect summitInner);

            foreach (Pawn old in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (!old.Destroyed) old.Destroy(DestroyMode.Vanish);
            }
            foreach (Thing corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).ToList())
            {
                if (!corpse.Destroyed) corpse.Destroy(DestroyMode.Vanish);
            }

            Faction facPlayers = Faction.OfPlayerSilentFail
                ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.Ancients)
                ?? Find.FactionManager.AllFactionsListForReading.First();
            Faction facHostile = Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile)
                ?? Faction.OfMechanoids
                ?? Find.FactionManager.AllFactionsListForReading.First(f => f != facPlayers);
            facPlayers.TryAffectGoodwillWith(facHostile, -200, canSendMessage: false, canSendHostilityLetter: false);

            PawnKindDef valKind = DefDatabase<PawnKindDef>.GetNamed("GCF_Valstrax");
            Pawn valstrax = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                valKind,
                facHostile,
                PawnGenerationContext.NonPlayer,
                map.Tile,
                forceGenerateNewPawn: true));

            int sep = mode == "intake_spinslash" ? 7 : 15;
            IntVec3 bossPos = new IntVec3(summitCenter.x + sep / 2, 0, summitCenter.z);
            GenSpawn.Spawn(valstrax, bossPos, map, Rot4.West);
            GiantFastLookup.TryGetRig(valstrax)?.RebuildFootprintNow(notifyMap: true);

            ThingComp? energyComp = FindValstraxEnergyComp(valstrax);
            if (mode != "intake_spinslash" && energyComp != null)
            {
                energyComp.GetType().GetMethod("ActivateDragonEnergy")?.Invoke(energyComp, new object[] { 3600 });
            }

            var targets = new List<Pawn>(3);
            (string kindName, string weaponDef, int zOff)[] dummySpecs =
            [
                ("Kiiro_Valor", "MHWilds_Weapon_GreatSword",  -2),
                ("Kiiro_Valor", "MHWilds_Weapon_LongSword",    0),
                ("Kiiro_Valor", "MHWilds_Weapon_ChargeBlade", +2)
            ];
            foreach (var ds in dummySpecs)
            {
                Pawn h = SpawnKiiroValorHero(
                    map,
                    facPlayers,
                    new IntVec3(summitCenter.x - sep / 2, 0, summitCenter.z + ds.zOff),
                    ds.kindName,
                    "kiiro_war_veteran",
                    ds.weaponDef,
                    QualityCategory.Normal,
                    valstrax);
                targets.Add(h);
            }

            ValstraxCueLog.Clear();
            ValstraxActiveLoops.Clear();
            ValstraxHuntEventLog.Clear();
            ValstraxObservedPartBreaks.Clear();
            lastValstraxDragonState = GetValstraxDragonStateName(energyComp);
            lastValstraxActionClip = string.Empty;
            lastValstraxDead = false;
            lastValstraxAirborne = false;
            lastValstraxHitCueFrame = -999;
            lastValstraxInjuryCount = 0;
            valstraxKoTimeSec = -1f;
            castIntakeTriggered = false;
            castSpinSlashTriggered = false;
            castDragonCannonTriggered = false;
            castWingThrustTriggered = false;
            castCometTriggered = false;
            valstraxSkillDirectorEnabled = false;
            valstraxGifSkillChainMode = mode;
            valstraxCueTrackingEnabled = true;

            var arenaCfg = new GiantArenaMatchConfig
            {
                Preset = "RuinedPinnacle",
                SideALabel = "绮罗勇者",
                SideBLabel = "银翼的凶星·天彗龙",
                StartPaused = true,
                LockNoonClearWeather = true,
                CleanVideoMode = true,
                CinematicHudOnly = true,
                EnableVersusHud = false,
                HideDevToolbar = true
            };

            GiantArenaMatchState st = GiantArenaService.BindCustomMapEncounter(
                map,
                summitCenter,
                summitInner,
                targets,
                facPlayers,
                new List<Pawn> { valstrax },
                facHostile,
                arenaCfg,
                out string bindErr);

            float rootSz = mode == "comet_ambush" ? 21f : 15.5f;
            GiantArenaService.FrameMatchCamera(1.0f, 8.0f, rootSz, 26f);

            return new
            {
                ok = string.IsNullOrEmpty(bindErr),
                error = bindErr,
                mode,
                matchId = st.MatchId,
                valstraxId = valstrax.thingIDNumber
            };
        }, cancellationToken);

    [Tool("gcfagent/export_valstrax_audio_cues", Description = "Export recorded millisecond-accurate Valstrax audio cues, active Dragonheart loop intervals, part breaks, and KO time to a JSON file.")]
    public Task<object> ExportValstraxAudioCues(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Destination JSON file path.")] string outputPath,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaRecordingStatus rec = GiantArenaVideoRecorder.GetStatus();
            float endSec = rec.RecordedSeconds;
            if (ValstraxActiveLoops.Count > 0 && ValstraxActiveLoops[^1].EndSec < 0f)
            {
                ValstraxActiveLoops[^1].EndSec = valstraxKoTimeSec > 0f ? valstraxKoTimeSec : endSec;
            }

            var payload = new
            {
                recordedSeconds = endSec,
                framesCaptured = rec.FramesCaptured,
                fps = rec.Fps,
                winnerSide = rec.WinnerSide,
                koTimeSec = valstraxKoTimeSec,
                cues = ValstraxCueLog,
                activeLoops = ValstraxActiveLoops,
                battleEvents = ValstraxHuntEventLog
            };

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json, Encoding.UTF8);

            return new
            {
                ok = true,
                outputPath,
                cueCount = ValstraxCueLog.Count,
                activeLoopCount = ValstraxActiveLoops.Count,
                koTimeSec = valstraxKoTimeSec,
                winnerSide = rec.WinnerSide,
                recordedSeconds = endSec,
                battleEvents = ValstraxHuntEventLog
            };
        }, cancellationToken);
}
