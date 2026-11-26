using System.Text;
using System.Text.Json;
using GiantCreatureFramework;
using RimBridgeServer.Sdk;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework.Agent.BridgeTools;

public sealed partial class InjectionVerificationTools
{
    private sealed class RathalosAudioCueEntry
    {
        public int Frame { get; set; }
        public float TimeSec { get; set; }
        public string Cue { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
        public string Detail { get; set; } = string.Empty;
    }

    private static readonly List<RathalosAudioCueEntry> RathalosCueLog = new();
    private static readonly List<string> RathalosHuntEventLog = new();
    private static readonly HashSet<string> RathalosObservedPartBreaks = new(StringComparer.Ordinal);
    private static readonly HashSet<int> RathalosSeenProjectileIds = new();
    private static readonly Dictionary<string, int> RathalosLastGunCueFrame = new(StringComparer.Ordinal);

    private static bool rathalosRecorderHookRegistered;
    private static bool rathalosCueTrackingEnabled;
    private static bool rathalosDirectorEnabled;
    private static string lastRathalosActionClip = string.Empty;
    private static string lastRathalosCurJob = string.Empty;
    private static bool lastRathalosDead;
    private static bool lastRathalosQuestSuccess;
    private static float rathalosKoTimeSec = -1f;
    private static int trackedQuestId = -1;

    private static bool rathCastRoar;
    private static bool rathCastFireball;
    private static bool rathCastCharge;
    private static bool rathCastFanFlame;
    private static bool rathCastLiftoff;
    private static bool rathCastTalon;

    private static void EnsureRathalosRecorderHook()
    {
        if (rathalosRecorderHookRegistered)
        {
            return;
        }
        rathalosRecorderHookRegistered = true;
        GiantArenaVideoRecorder.OnRecorderFrameStepped += OnRathalosRecorderFrame;
    }

    private static void AddRathalosCue(int frame, float timeSec, string cue, float vol = 1f, string detail = "")
    {
        RathalosCueLog.Add(new RathalosAudioCueEntry
        {
            Frame = frame,
            TimeSec = (float)Math.Round(timeSec, 3),
            Cue = cue,
            Volume = vol,
            Detail = detail
        });
    }

    private static bool TryCastRathalosAbility(Pawn rathalos, string abilityDefName, LocalTargetInfo target)
    {
        if (rathalos?.abilities?.abilities == null || rathalos.Dead || rathalos.Downed || !rathalos.Spawned)
        {
            return false;
        }
        if (rathalos.stances?.curStance is Stance_Warmup)
        {
            return false;
        }
        CompGiantRig? rig = GiantFastLookup.TryGetRig(rathalos);
        if (rig != null && rig.TryGetActiveActionClipAndTick(out string active, out _) && !string.IsNullOrEmpty(active))
        {
            return false;
        }
        Ability? ab = rathalos.abilities.abilities.FirstOrDefault(a => a?.def?.defName == abilityDefName);
        if (ab == null)
        {
            return false;
        }
        ab.ResetCooldown();
        if (ab.GizmoDisabled(out _))
        {
            return false;
        }
        if (!ab.verb.CanHitTarget(target))
        {
            return false;
        }
        ab.QueueCastingJob(target, LocalTargetInfo.Invalid);
        return true;
    }

    private static Pawn? PickBestColonyTarget(Map map, Pawn rathalos, bool closest)
    {
        var candidates = map.mapPawns.FreeColonistsSpawned
            .Where(p => p != null && !p.Dead && !p.Downed)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        return closest
            ? candidates.OrderBy(p => p.Position.DistanceToSquared(rathalos.Position)).First()
            : candidates.OrderByDescending(p => p.Position.DistanceToSquared(rathalos.Position)).First();
    }

    private static void OnRathalosRecorderFrame(int frame, float timeSec, Map? map)
    {
        if (!rathalosCueTrackingEnabled || map == null)
        {
            return;
        }

        if (frame == 2)
        {
            AddRathalosCue(frame, timeSec, "quest_accepted", 0.90f, "King of the Skies accepted");
            AddRathalosCue(frame + 12, timeSec + 0.20f, "threat_letter", 0.95f, "Rathalos Assault letter");
            AddRathalosCue(frame + 24, timeSec + 0.40f, "call", 0.90f, "Rathalos arrival call");
        }

        Pawn? rathalos = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p?.def?.defName == "GCF_Rathalos")
            ?? map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)
                .OfType<Corpse>()
                .Select(c => c.InnerPawn)
                .FirstOrDefault(p => p?.def?.defName == "GCF_Rathalos");
        if (rathalos == null)
        {
            return;
        }

        // Track quest completion
        if (!lastRathalosQuestSuccess && trackedQuestId >= 0 && Find.QuestManager != null)
        {
            Quest? q = Find.QuestManager.QuestsListForReading.FirstOrDefault(x => x.id == trackedQuestId);
            if (q != null && q.State == QuestState.EndedSuccess)
            {
                lastRathalosQuestSuccess = true;
                AddRathalosCue(frame, timeSec, "quest_succeeded", 0.95f, "King of the Skies completed");
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 任务「空之王者」完成！");
            }
        }

        // Scan newly spawned projectiles for authentic gunshot / turret / fireball audio cues
        if (!rathalos.Dead)
        {
            List<Thing> projs = map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
            for (int i = 0; i < projs.Count; i++)
            {
                Thing t = projs[i];
                if (t == null || !RathalosSeenProjectileIds.Add(t.thingIDNumber))
                {
                    continue;
                }
                string defName = t.def?.defName ?? string.Empty;
                string cueKey = defName switch
                {
                    var s when s.Contains("Autocannon", StringComparison.OrdinalIgnoreCase) => "autocannon",
                    var s when s.Contains("MiniTurret", StringComparison.OrdinalIgnoreCase) => "miniturret",
                    var s when s.Contains("Charge", StringComparison.OrdinalIgnoreCase) => "charge_rifle",
                    var s when s.Contains("Sniper", StringComparison.OrdinalIgnoreCase) => "sniper_rifle",
                    var s when s.Contains("Minigun", StringComparison.OrdinalIgnoreCase) => "minigun",
                    var s when s.Contains("Assault", StringComparison.OrdinalIgnoreCase) => "assault_rifle",
                    _ => "assault_rifle"
                };
                int minGap = cueKey is "minigun" or "miniturret" ? 8 : 6;
                if (!RathalosLastGunCueFrame.TryGetValue(cueKey, out int lastF) || frame - lastF >= minGap)
                {
                    float vol = cueKey == "autocannon" ? 0.65f : 0.42f;
                    AddRathalosCue(frame, timeSec, cueKey, vol, defName);
                    RathalosLastGunCueFrame[cueKey] = frame;
                }
            }
        }

        bool isDeadNow = rathalos.Dead;
        if (isDeadNow && !lastRathalosDead)
        {
            lastRathalosDead = true;
            rathalosKoTimeSec = timeSec;
            AddRathalosCue(frame, timeSec, "death", 1.0f, "Rathalos slain");
            RathalosHuntEventLog.Add($"[{timeSec:F1}s] 讨伐成功！雄火龙倒下并掉落逆鳞！");
            return;
        }
        if (isDeadNow)
        {
            return;
        }

        CompGiantRig? rig = GiantFastLookup.TryGetRig(rathalos);
        string activeClip = string.Empty;
        if (rig != null)
        {
            rig.TryGetActiveActionClipAndTick(out activeClip, out _);
        }
        if (!string.IsNullOrEmpty(activeClip) && !string.Equals(activeClip, lastRathalosActionClip, StringComparison.Ordinal))
        {
            if (activeClip.Contains("DragonCharge", StringComparison.OrdinalIgnoreCase))
            {
                AddRathalosCue(frame, timeSec, "dragon_charge", 0.95f, activeClip);
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 释放「红莲突进」");
            }
            else if (activeClip.Contains("FanFlameSweep", StringComparison.OrdinalIgnoreCase))
            {
                AddRathalosCue(frame, timeSec, "fan_flame", 0.98f, activeClip);
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 释放「扇形龙炎横扫」");
            }
            else if (activeClip.Contains("FireballLiftoff", StringComparison.OrdinalIgnoreCase))
            {
                AddRathalosCue(frame, timeSec, "fireball_blast", 0.98f, activeClip);
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 释放「爆炎升空」");
            }
            else if (activeClip.Contains("Fireball", StringComparison.OrdinalIgnoreCase))
            {
                AddRathalosCue(frame, timeSec, "fireball_blast", 0.95f, activeClip);
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 释放「火球吐息」");
            }
            else if (activeClip.Contains("TalonStrike", StringComparison.OrdinalIgnoreCase))
            {
                AddRathalosCue(frame, timeSec, "talon_strike", 0.95f, activeClip);
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 释放「毒爪俯冲」");
            }
            else if (activeClip.Contains("Roar", StringComparison.OrdinalIgnoreCase))
            {
                AddRathalosCue(frame, timeSec, "apex_roar", 1.0f, activeClip);
                RathalosHuntEventLog.Add($"[{timeSec:F1}s] 释放「王者咆哮」");
            }
            lastRathalosActionClip = activeClip;
        }
        else if (string.IsNullOrEmpty(activeClip))
        {
            lastRathalosActionClip = string.Empty;
        }

        // Also detect job-based ability starts if clip starts slightly after warmup
        string curJob = rathalos.CurJobDef?.defName ?? string.Empty;
        if (curJob != lastRathalosCurJob)
        {
            lastRathalosCurJob = curJob;
        }

        // Track part breaks (Webbing / Scale drops)
        if (rathalos.health?.hediffSet != null)
        {
            foreach (Hediff_MissingPart mp in rathalos.health.hediffSet.GetMissingPartsCommonAncestors())
            {
                string key = mp.Part?.LabelCap ?? mp.LabelCap;
                if (!string.IsNullOrEmpty(key) && RathalosObservedPartBreaks.Add(key))
                {
                    AddRathalosCue(frame, timeSec, "pain", 0.92f, "Part broken: " + key);
                    RathalosHuntEventLog.Add($"[{timeSec:F1}s] 部位破坏：{key}");
                }
            }
        }

        // Keep drafted colonists actively engaging Rathalos if idle
        if (frame % 20 == 0)
        {
            foreach (Pawn col in map.mapPawns.FreeColonistsSpawned)
            {
                if (col == null || col.Dead || col.Downed || !col.Drafted)
                {
                    continue;
                }
                if (col.jobs?.curJob == null || col.jobs.curJob.def == JobDefOf.Wait_Combat || col.jobs.curJob.def == JobDefOf.Wait)
                {
                    bool isMelee = col.equipment?.Primary?.def?.IsMeleeWeapon ?? false;
                    if (!isMelee)
                    {
                        Job shoot = JobMaker.MakeJob(JobDefOf.AttackStatic, rathalos);
                        shoot.expiryInterval = 600;
                        col.jobs?.StartJob(shoot, JobCondition.InterruptForced);
                    }
                    else if (rathalos.Position.DistanceTo(col.Position) <= 45f)
                    {
                        Job melee = JobMaker.MakeJob(JobDefOf.AttackMelee, rathalos);
                        melee.expiryInterval = 600;
                        col.jobs?.StartJob(melee, JobCondition.InterruptForced);
                    }
                }
            }
        }

        // Subtle showcase ability director so viewers see Rathalos's signature moveset during the colony battle
        if (rathalosDirectorEnabled && rathalos.Spawned && rig != null)
        {
            Pawn? closeTarget = PickBestColonyTarget(map, rathalos, closest: true);
            Pawn? farTarget = PickBestColonyTarget(map, rathalos, closest: false);
            float distToColony = closeTarget != null ? rathalos.Position.DistanceTo(closeTarget.Position) : 99f;

            if (!rathCastFireball && timeSec >= 3.2f && farTarget != null && distToColony <= 36f)
            {
                if (TryCastRathalosAbility(rathalos, "GCF_Ability_Rathalos_FireballBreath", new LocalTargetInfo(farTarget.Position)))
                {
                    rathCastFireball = true;
                }
            }
            else if (!rathCastCharge && timeSec >= 8.5f && closeTarget != null && distToColony >= 7f && distToColony <= 34f)
            {
                if (TryCastRathalosAbility(rathalos, "GCF_Ability_Rathalos_DragonCharge", new LocalTargetInfo(closeTarget)))
                {
                    rathCastCharge = true;
                }
            }
            else if (!rathCastRoar && timeSec >= 13.5f && distToColony <= 11.5f)
            {
                if (TryCastRathalosAbility(rathalos, "GCF_Ability_Rathalos_ApexRoar", new LocalTargetInfo(rathalos)))
                {
                    rathCastRoar = true;
                }
            }
            else if (!rathCastFanFlame && timeSec >= 18.5f && closeTarget != null && distToColony <= 18f)
            {
                if (TryCastRathalosAbility(rathalos, "GCF_Ability_Rathalos_FanFlameSweep", new LocalTargetInfo(closeTarget)))
                {
                    rathCastFanFlame = true;
                }
            }
            else if (!rathCastLiftoff && timeSec >= 24.0f && closeTarget != null && distToColony <= 12f && !rig.IsCurrentlyAirborne)
            {
                if (TryCastRathalosAbility(rathalos, "GCF_Ability_Rathalos_FireballLiftoff", new LocalTargetInfo(closeTarget.Position)))
                {
                    rathCastLiftoff = true;
                }
            }
            else if (!rathCastTalon && timeSec >= 29.0f && closeTarget != null && rig.IsCurrentlyAirborne && distToColony <= 20f)
            {
                if (TryCastRathalosAbility(rathalos, "GCF_Ability_Rathalos_TalonStrike", new LocalTargetInfo(closeTarget)))
                {
                    rathCastTalon = true;
                }
            }
        }
    }

    private static Thing? SpawnColonyBuilding(Map map, ThingDef? def, ThingDef? stuff, IntVec3 pos, Rot4 rot, Faction faction)
    {
        if (def == null || !pos.InBounds(map))
        {
            return null;
        }
        ThingDef? resolvedStuff = def.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(def)) : null;
        Thing t = ThingMaker.MakeThing(def, resolvedStuff);
        t.SetFactionDirect(faction);
        Thing spawned = GenSpawn.Spawn(t, pos, map, rot);
        if (spawned.TryGetComp<CompPowerBattery>() is CompPowerBattery bat)
        {
            bat.AddEnergy(600f);
        }
        return spawned;
    }

    private static void ClearRectForColony(Map map, CellRect rect)
    {
        var scratch = new List<Thing>(16);
        foreach (IntVec3 c in rect)
        {
            if (!c.InBounds(map)) continue;
            map.roofGrid?.SetRoof(c, null);
            map.fogGrid?.Unfog(c);
            scratch.Clear();
            scratch.AddRange(c.GetThingList(map));
            for (int i = 0; i < scratch.Count; i++)
            {
                Thing t = scratch[i];
                if (t is Pawn) continue;
                if (!t.Destroyed && t.def != null && t.def.destroyable)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }

    private static void EquipColonyDefender(
        Pawn p,
        string weaponDefName,
        string helmetDefName,
        string armorDefName,
        QualityCategory weaponQuality,
        bool addShieldBelt)
    {
        if (p.story != null && p.WorkTagIsDisabled(WorkTags.Violent))
        {
            BackstoryDef? validChild = DefDatabase<BackstoryDef>.AllDefsListForReading
                .FirstOrDefault(b => b.slot == BackstorySlot.Childhood && (b.workDisables & WorkTags.Violent) == 0);
            BackstoryDef? validAdult = DefDatabase<BackstoryDef>.AllDefsListForReading
                .FirstOrDefault(b => b.slot == BackstorySlot.Adulthood && (b.workDisables & WorkTags.Violent) == 0);
            if (validChild != null) p.story.Childhood = validChild;
            if (validAdult != null) p.story.Adulthood = validAdult;
            p.story.traits?.allTraits?.RemoveAll(t => (t.def.disabledWorkTags & WorkTags.Violent) != 0);
            p.Notify_DisabledWorkTypesChanged();
        }

        SkillRecord? shoot = p.skills?.GetSkill(SkillDefOf.Shooting);
        if (shoot != null) { shoot.Level = Math.Max(shoot.Level, 18); shoot.passion = Passion.Major; }
        SkillRecord? melee = p.skills?.GetSkill(SkillDefOf.Melee);
        if (melee != null) { melee.Level = Math.Max(melee.Level, 19); melee.passion = Passion.Major; }

        if (p.apparel != null)
        {
            p.apparel.DestroyAll();
            void WearOne(string defName, ThingDef? stuff, QualityCategory apQuality)
            {
                ThingDef? d = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (d == null) return;
                ThingDef? st = d.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(d)) : null;
                Apparel ap = (Apparel)ThingMaker.MakeThing(d, st);
                ap.TryGetComp<CompQuality>()?.SetQuality(apQuality, ArtGenerationContext.Colony);
                if (ApparelUtility.HasPartsToWear(p, d))
                {
                    p.apparel.Wear(ap, dropReplacedApparel: false);
                }
            }

            WearOne("Apparel_Pants", DefDatabase<ThingDef>.GetNamedSilentFail("DevilstrandCloth") ?? DefDatabase<ThingDef>.GetNamedSilentFail("Synthread"), QualityCategory.Excellent);
            WearOne("Apparel_BasicShirt", DefDatabase<ThingDef>.GetNamedSilentFail("DevilstrandCloth") ?? DefDatabase<ThingDef>.GetNamedSilentFail("Synthread"), QualityCategory.Excellent);
            WearOne(armorDefName, ThingDefOf.Plasteel, QualityCategory.Masterwork);
            WearOne(helmetDefName, ThingDefOf.Plasteel, QualityCategory.Masterwork);
            if (addShieldBelt)
            {
                WearOne("Apparel_ShieldBelt", null, QualityCategory.Masterwork);
            }
        }

        ThingDef? wDef = DefDatabase<ThingDef>.GetNamedSilentFail(weaponDefName);
        if (wDef != null && p.equipment != null)
        {
            p.equipment.DestroyAllEquipment();
            ThingDef? wStuff = wDef.MadeFromStuff ? ThingDefOf.Plasteel : null;
            ThingWithComps w = (ThingWithComps)ThingMaker.MakeThing(wDef, wStuff);
            w.TryGetComp<CompQuality>()?.SetQuality(weaponQuality, ArtGenerationContext.Colony);
            p.equipment.AddEquipment(w);
        }
    }

    [Tool("gcfagent/stage_rathalos_colony_hunt", Description = "Build a simulated RimWorld colony with bastion/turrets/defenders, trigger Day-7 King of the Skies (GCF_Quest_RathalosHunt), accept it to summon the manhunting Rathalos raid, and bind native 60fps capture with audio cue telemetry.")]
    public Task<object> StageRathalosColonyHunt(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Enable subtle ability director so Rathalos showcases all signature skills during the defense.")] bool enableDirector = true,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            EnsureRathalosRecorderHook();
            GiantArenaService.StopArena(restoreSettings: false);

            Map map = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active player home map.");
            Current.Game.CurrentMap = map;
            Faction facPlayer = Faction.OfPlayer;
            Faction facHostile = Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile)
                ?? Faction.OfMechanoids
                ?? Find.FactionManager.AllFactionsListForReading.First(f => f != facPlayer);
            facPlayer.TryAffectGoodwillWith(facHostile, -200, canSendMessage: false, canSendHostilityLetter: false);

            IntVec3 center = map.Center;
            int cx = center.x;
            int cz = center.z;

            // 1. Clear compound + eastern battlefield lane while keeping outer trees/rocks natural
            ClearRectForColony(map, new CellRect(cx - 24, cz - 17, 68, 35));
            TerrainDef? tileGranite = DefDatabase<TerrainDef>.GetNamedSilentFail("TileGranite") ?? TerrainDefOf.PavedTile;
            TerrainDef? flagstone = DefDatabase<TerrainDef>.GetNamedSilentFail("FlagstoneGranite") ?? TerrainDefOf.PavedTile;
            TerrainDef? woodFloor = DefDatabase<TerrainDef>.GetNamedSilentFail("WoodPlankFloor") ?? TerrainDefOf.PavedTile;
            TerrainDef? soil = DefDatabase<TerrainDef>.GetNamedSilentFail("Soil") ?? TerrainDefOf.Soil;

            // Ensure combat lane has firm walkable soil
            for (int x = cx + 5; x <= cx + 42; x++)
            {
                for (int z = cz - 14; z <= cz + 14; z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map)) continue;
                    TerrainDef t = map.terrainGrid.TerrainAt(c);
                    if (t == null || t.passability == Traversability.Impassable || t.IsWater)
                    {
                        map.terrainGrid.SetTerrain(c, soil);
                    }
                }
            }

            // 2. Colony workshop & barracks building (cx-20..cx-6, cz-8..cz+8)
            ThingDef graniteBlocks = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
            for (int x = cx - 20; x <= cx - 6; x++)
            {
                for (int z = cz - 8; z <= cz + 8; z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    map.terrainGrid.SetTerrain(c, (x < cx - 13) ? woodFloor : tileGranite);
                    bool isBorder = x == cx - 20 || x == cx - 6 || z == cz - 8 || z == cz + 8 || x == cx - 13;
                    if (isBorder)
                    {
                        if (x == cx - 6 && (z == cz - 3 || z == cz + 3))
                        {
                            SpawnColonyBuilding(map, ThingDefOf.Door, ThingDefOf.Steel, c, Rot4.East, facPlayer);
                        }
                        else if (x == cx - 13 && z == cz)
                        {
                            SpawnColonyBuilding(map, ThingDefOf.Door, ThingDefOf.WoodLog, c, Rot4.East, facPlayer);
                        }
                        else
                        {
                            SpawnColonyBuilding(map, ThingDefOf.Wall, graniteBlocks, c, Rot4.North, facPlayer);
                        }
                    }
                    else
                    {
                        map.roofGrid?.SetRoof(c, RoofDefOf.RoofConstructed);
                    }
                }
            }

            // Interior colony furniture (8 beds, research, smithy, shelves, lamps, recreation)
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 19, 0, cz - 6), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 19, 0, cz - 4), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 19, 0, cz - 2), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 19, 0, cz + 2), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 19, 0, cz + 4), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 19, 0, cz + 6), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 15, 0, cz - 6), Rot4.West, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.Bed, ThingDefOf.WoodLog, new IntVec3(cx - 15, 0, cz + 6), Rot4.West, facPlayer);
            SpawnColonyBuilding(map, DefDatabase<ThingDef>.GetNamedSilentFail("Table2x2c"), ThingDefOf.WoodLog, new IntVec3(cx - 16, 0, cz), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, DefDatabase<ThingDef>.GetNamedSilentFail("SimpleResearchBench"), ThingDefOf.Steel, new IntVec3(cx - 10, 0, cz - 6), Rot4.South, facPlayer);
            SpawnColonyBuilding(map, DefDatabase<ThingDef>.GetNamedSilentFail("ElectricSmithy"), ThingDefOf.Steel, new IntVec3(cx - 10, 0, cz + 5), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, DefDatabase<ThingDef>.GetNamedSilentFail("Shelf"), ThingDefOf.WoodLog, new IntVec3(cx - 8, 0, cz - 6), Rot4.West, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.StandingLamp, null, new IntVec3(cx - 15, 0, cz - 3), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, ThingDefOf.StandingLamp, null, new IntVec3(cx - 9, 0, cz), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, DefDatabase<ThingDef>.GetNamedSilentFail("HorseshoesPin"), ThingDefOf.Steel, new IntVec3(cx - 3, 0, cz + 9), Rot4.North, facPlayer);

            // Stock colony food & medicine respecting stackLimit so zero RimWorld warnings/errors fire
            ThingDef mealDef = DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack") ?? ThingDefOf.MealSimple;
            Thing meals = ThingMaker.MakeThing(mealDef);
            meals.stackCount = Math.Min(10, mealDef.stackLimit);
            GenSpawn.Spawn(meals, new IntVec3(cx - 8, 0, cz - 6), map);
            Thing meds = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
            meds.stackCount = Math.Min(25, ThingDefOf.MedicineIndustrial.stackLimit);
            GenSpawn.Spawn(meds, new IntVec3(cx - 8, 0, cz - 5), map);

            if (Find.ResearchManager != null && Find.ResearchManager.GetProject() == null)
            {
                ResearchProjectDef? proj = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .FirstOrDefault(r => !r.IsFinished && r.CanStartNow);
                if (proj != null)
                {
                    Find.ResearchManager.SetCurrentProject(proj);
                }
            }

            // Courtyard paving between workshop and bastion
            for (int x = cx - 5; x <= cx + 4; x++)
            {
                for (int z = cz - 11; z <= cz + 11; z++)
                {
                    map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), flagstone);
                }
            }

            // Power grid: 4 SolarGenerators + 4 Batteries + Conduits under generators, batteries, poppers, and turrets
            ThingDef? solarDef = DefDatabase<ThingDef>.GetNamedSilentFail("SolarGenerator");
            ThingDef? batDef = DefDatabase<ThingDef>.GetNamedSilentFail("Battery");
            SpawnColonyBuilding(map, solarDef, null, new IntVec3(cx - 18, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, solarDef, null, new IntVec3(cx - 12, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, solarDef, null, new IntVec3(cx - 6, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, solarDef, null, new IntVec3(cx, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, batDef, null, new IntVec3(cx - 3, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, batDef, null, new IntVec3(cx - 1, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, batDef, null, new IntVec3(cx + 1, 0, cz - 14), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, batDef, null, new IntVec3(cx + 3, 0, cz - 14), Rot4.North, facPlayer);

            for (int x = cx - 18; x <= cx + 4; x++)
            {
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(x, 0, cz - 14), Rot4.North, facPlayer);
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(x, 0, cz - 12), Rot4.North, facPlayer);
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(x, 0, cz + 12), Rot4.North, facPlayer);
            }
            for (int z = cz - 14; z <= cz + 12; z++)
            {
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(cx - 4, 0, z), Rot4.North, facPlayer);
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(cx - 1, 0, z), Rot4.North, facPlayer);
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(cx + 1, 0, z), Rot4.North, facPlayer);
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(cx + 3, 0, z), Rot4.North, facPlayer);
            }
            for (int z = cz - 14; z <= cz - 3; z++)
            {
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(cx - 15, 0, z), Rot4.North, facPlayer);
                SpawnColonyBuilding(map, ThingDefOf.PowerConduit, null, new IntVec3(cx - 9, 0, z), Rot4.North, facPlayer);
            }

            // Rice crop patch on north courtyard
            ThingDef? riceDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice");
            if (riceDef != null)
            {
                for (int x = cx - 20; x <= cx - 11; x++)
                {
                    for (int z = cz + 11; z <= cz + 14; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        map.terrainGrid.SetTerrain(c, soil);
                        Plant plant = (Plant)ThingMaker.MakeThing(riceDef);
                        plant.Growth = 0.85f;
                        GenSpawn.Spawn(plant, c, map);
                    }
                }
            }

            // 3. Staggered North/South Defensive Bastion + Open Center Killzone + Firefoam Poppers + Turrets
            ThingDef? barricadeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Barricade") ?? ThingDefOf.Sandbags;
            ThingDef? sandbagDef = DefDatabase<ThingDef>.GetNamedSilentFail("Sandbags") ?? barricadeDef;
            ThingDef? clothDef = DefDatabase<ThingDef>.GetNamedSilentFail("Cloth");
            for (int z = cz - 11; z <= cz + 11; z++)
            {
                bool inWing = Math.Abs(z - cz) >= 5;
                if (inWing && (z == cz - 11 || z == cz - 7 || z == cz + 7 || z == cz + 11))
                {
                    SpawnColonyBuilding(map, ThingDefOf.Wall, graniteBlocks, new IntVec3(cx + 3, 0, z), Rot4.North, facPlayer);
                }
                if (Math.Abs(z - cz) >= 2)
                {
                    SpawnColonyBuilding(map, barricadeDef, ThingDefOf.Steel, new IntVec3(cx + 4, 0, z), Rot4.North, facPlayer);
                    SpawnColonyBuilding(map, sandbagDef, clothDef, new IntVec3(cx + 5, 0, z), Rot4.North, facPlayer);
                }
            }

            ThingDef? miniTurret = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_MiniTurret");
            ThingDef? autoTurret = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_Autocannon");
            ThingDef? sniperTurret = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_Sniper");
            ThingDef? firefoamPopper = DefDatabase<ThingDef>.GetNamedSilentFail("FirefoamPopper");

            // Forward Mini-Turrets along flanks
            SpawnColonyBuilding(map, miniTurret, ThingDefOf.Steel, new IntVec3(cx + 3, 0, cz - 10), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, miniTurret, ThingDefOf.Steel, new IntVec3(cx + 3, 0, cz - 6), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, miniTurret, ThingDefOf.Steel, new IntVec3(cx + 3, 0, cz + 6), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, miniTurret, ThingDefOf.Steel, new IntVec3(cx + 3, 0, cz + 10), Rot4.East, facPlayer);

            // Autocannons with unobstructed eastern sightlines & safe minRange setback
            SpawnColonyBuilding(map, autoTurret, ThingDefOf.Steel, new IntVec3(cx - 1, 0, cz - 10), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, autoTurret, ThingDefOf.Steel, new IntVec3(cx - 1, 0, cz - 6), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, autoTurret, ThingDefOf.Steel, new IntVec3(cx - 1, 0, cz + 6), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, autoTurret, ThingDefOf.Steel, new IntVec3(cx - 1, 0, cz + 10), Rot4.East, facPlayer);

            // Uranium Slug Turrets (60% AP) on rear flanks covering the entire killzone
            SpawnColonyBuilding(map, sniperTurret, ThingDefOf.Plasteel, new IntVec3(cx - 4, 0, cz - 11), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, sniperTurret, ThingDefOf.Plasteel, new IntVec3(cx - 4, 0, cz - 7), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, sniperTurret, ThingDefOf.Plasteel, new IntVec3(cx - 4, 0, cz + 7), Rot4.East, facPlayer);
            SpawnColonyBuilding(map, sniperTurret, ThingDefOf.Plasteel, new IntVec3(cx - 4, 0, cz + 11), Rot4.East, facPlayer);

            // Firefoam Poppers to counter Rathalos flame sweeps
            SpawnColonyBuilding(map, firefoamPopper, null, new IntVec3(cx + 1, 0, cz - 8), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, firefoamPopper, null, new IntVec3(cx + 1, 0, cz), Rot4.North, facPlayer);
            SpawnColonyBuilding(map, firefoamPopper, null, new IntVec3(cx + 1, 0, cz + 8), Rot4.North, facPlayer);

            map.powerNetManager?.UpdatePowerNetsAndConnections_First();

            // 4. Colony Defenders (keep existing colonists + reinforce to 12)
            var existingColonists = map.mapPawns.FreeColonistsSpawned.ToList();
            while (existingColonists.Count < 12)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    PawnKindDefOf.Colonist,
                    facPlayer,
                    PawnGenerationContext.PlayerStarter,
                    map.Tile,
                    forceGenerateNewPawn: true));
                GenSpawn.Spawn(extra, new IntVec3(cx, 0, cz), map, Rot4.East);
                existingColonists.Add(extra);
            }

            (string weapon, string helmet, string armor, QualityCategory q, bool shield, int xOff, int zOff)[] slots =
            [
                // North firing wing
                ("Gun_ChargeRifle",      "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Legendary,  false, 2, -11),
                ("Gun_ChargeRifle",      "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Masterwork, false, 3, -9),
                ("Gun_ChargeLance",      "Apparel_ArmorHelmetRecon",      "Apparel_PowerArmor",             QualityCategory.Legendary,  false, 2, -8),
                ("Gun_Minigun",          "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Masterwork, false, 3, -5),
                // South firing wing
                ("Gun_ChargeRifle",      "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Legendary,  false, 2,  11),
                ("Gun_ChargeRifle",      "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Masterwork, false, 3,  9),
                ("Gun_ChargeLance",      "Apparel_ArmorHelmetRecon",      "Apparel_PowerArmor",             QualityCategory.Legendary,  false, 2,  8),
                ("Gun_ChargeRifle",      "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Masterwork, false, 3,  5),
                // Center Overwatch Charge Lance
                ("Gun_ChargeLance",      "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Legendary,  false, 2,  0),
                // Vanguard Melee Hunters wielding Rathalos Dragonscale Swords & Shield Belts
                ("GCF_DragonscaleSword", "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Legendary,  true,  7, -3),
                ("GCF_DragonscaleSword", "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Legendary,  true,  8,  0),
                ("GCF_DragonscaleSword", "Apparel_ArmorHelmetCataphract", "Apparel_ArmorCataphractPhoenix", QualityCategory.Legendary,  true,  7,  3)
            ];

            var defenders = new List<Pawn>(slots.Length);
            for (int i = 0; i < slots.Length; i++)
            {
                Pawn col = existingColonists[i];
                col.health?.Reset();
                var s = slots[i];
                EquipColonyDefender(col, s.weapon, s.helmet, s.armor, s.q, s.shield);
                col.Position = new IntVec3(cx + s.xOff, 0, cz + s.zOff);
                col.Rotation = Rot4.East;
                col.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
                if (col.drafter != null)
                {
                    col.drafter.Drafted = true;
                }
                if (col.mindState != null)
                {
                    col.mindState.canFleeIndividual = false;
                }
                defenders.Add(col);
            }

            // Clear setup messages & tutorial helper before real Day-7 quest triggers
            Prefs.AdaptiveTrainingEnabled = false;
            Prefs.ShowRealtimeClock = false;
            Prefs.DevMode = false;
            if (Find.PlaySettings != null)
            {
                Find.PlaySettings.showLearningHelper = false;
            }
            if (Find.LetterStack != null)
            {
                var oldLetters = Find.LetterStack.LettersListForReading.ToList();
                for (int li = 0; li < oldLetters.Count; li++)
                {
                    Find.LetterStack.RemoveLetter(oldLetters[li]);
                }
            }
            Messages.Clear();

            // 5. Trigger real Day-7 King of the Skies quest (GCF_Quest_RathalosHunt) and accept it!
            Find.TickManager.DebugSetTicksGame(420090);
            for (int t = 0; t < 20; t++)
            {
                Find.TickManager.DoSingleTick();
            }

            QuestScriptDef questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            Quest? quest = Find.QuestManager.QuestsListForReading
                .LastOrDefault(q => q.root == questDef && q.State == QuestState.NotYetAccepted)
                ?? Find.QuestManager.QuestsListForReading.LastOrDefault(q => q.root == questDef);
            if (quest == null)
            {
                throw new InvalidOperationException("Failed to trigger GCF_Quest_RathalosHunt on Day 7.");
            }
            if (quest.State == QuestState.NotYetAccepted)
            {
                quest.Accept(defenders[0]);
            }
            trackedQuestId = quest.id;

            Pawn? rathalos = map.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p?.def?.defName == "GCF_Rathalos" && !p.Dead)
                ?? throw new InvalidOperationException("QuestPart_SpawnManhunterRathalosRaid did not spawn GCF_Rathalos.");

            // Place quest Rathalos on eastern approach lane facing the colony bastion
            IntVec3 rathStart = new IntVec3(cx + 32, 0, cz);
            rathalos.Position = rathStart;
            rathalos.Rotation = Rot4.West;
            rathalos.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
            GiantFastLookup.TryGetRig(rathalos)?.RebuildFootprintNow(notifyMap: true);

            // Reset showcase telemetry
            RathalosCueLog.Clear();
            RathalosHuntEventLog.Clear();
            RathalosObservedPartBreaks.Clear();
            RathalosSeenProjectileIds.Clear();
            RathalosLastGunCueFrame.Clear();
            foreach (Thing p in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
            {
                if (p != null) RathalosSeenProjectileIds.Add(p.thingIDNumber);
            }
            lastRathalosActionClip = string.Empty;
            lastRathalosCurJob = string.Empty;
            lastRathalosDead = false;
            lastRathalosQuestSuccess = false;
            rathalosKoTimeSec = -1f;
            rathCastRoar = false;
            rathCastFireball = false;
            rathCastCharge = false;
            rathCastFanFlame = false;
            rathCastLiftoff = false;
            rathCastTalon = false;
            rathalosDirectorEnabled = enableDirector;
            rathalosCueTrackingEnabled = true;

            // Bind encounter for native 60fps capture while keeping full colony UI & disabling dev toolbar
            var arenaCfg = new GiantArenaMatchConfig
            {
                Preset = "CustomColony",
                SideALabel = "殖民地防线",
                SideBLabel = "空之王者·雄火龙",
                StartPaused = true,
                LockNoonClearWeather = true,
                CleanVideoMode = false,
                CinematicHudOnly = false,
                EnableVersusHud = false,
                HideDevToolbar = true
            };

            GiantArenaMatchState st = GiantArenaService.BindCustomMapEncounter(
                map,
                new IntVec3(cx + 12, 0, cz),
                CellRect.WholeMap(map).ContractedBy(2),
                defenders,
                facPlayer,
                new List<Pawn> { rathalos },
                facHostile,
                arenaCfg,
                out string bindErr);

            // Ensure colonist bar stays visible & cleanly frame the bastion + incoming wyvern
            if (Find.PlaySettings != null)
            {
                Find.PlaySettings.showColonistBar = true;
            }
            GiantArenaService.FrameMatchCamera(1.0f, 8.5f, 18f, 34f);

            // Order defenders to engage immediately
            for (int i = 0; i < defenders.Count; i++)
            {
                bool isMelee = defenders[i].equipment?.Primary?.def?.IsMeleeWeapon ?? false;
                Job job = JobMaker.MakeJob(isMelee ? JobDefOf.AttackMelee : JobDefOf.AttackStatic, rathalos);
                job.expiryInterval = 600;
                defenders[i].jobs?.StartJob(job, JobCondition.InterruptForced);
            }

            return new
            {
                ok = string.IsNullOrEmpty(bindErr),
                error = bindErr,
                questId = quest.id,
                questState = quest.State.ToString(),
                rathalosId = rathalos.thingIDNumber,
                defenders = defenders.Count,
                matchId = st.MatchId
            };
        }, cancellationToken);

    [Tool("gcfagent/export_rathalos_audio_cues", Description = "Export millisecond-accurate Rathalos colony battle audio cues and battle log to JSON.")]
    public Task<object> ExportRathalosAudioCues(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Destination JSON path.")] string outputPath,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaRecordingStatus rec = GiantArenaVideoRecorder.GetStatus();
            var payload = new
            {
                recordedSeconds = rec.RecordedSeconds,
                framesCaptured = rec.FramesCaptured,
                fps = rec.Fps,
                winnerSide = rec.WinnerSide,
                koTimeSec = rathalosKoTimeSec,
                questSucceeded = lastRathalosQuestSuccess,
                cues = RathalosCueLog,
                battleEvents = RathalosHuntEventLog,
                partBreaks = RathalosObservedPartBreaks.ToArray()
            };
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return new
            {
                ok = true,
                outputPath,
                cueCount = RathalosCueLog.Count,
                koTimeSec = rathalosKoTimeSec,
                questSucceeded = lastRathalosQuestSuccess,
                recordedSeconds = rec.RecordedSeconds,
                battleEvents = RathalosHuntEventLog,
                partBreaks = RathalosObservedPartBreaks.ToArray()
            };
        }, cancellationToken);
}
