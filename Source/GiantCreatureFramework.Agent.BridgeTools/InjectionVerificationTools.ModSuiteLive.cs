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
    private static readonly string[] AllGcfPackageIds =
    [
        "sanguo.giantcreatureframework",
        "sanguo.giantcreatureframework.rathalos",
        "sanguo.giantcreatureframework.valstrax",
        "sanguo.giantcreatureframework.mechcolossus",
        "sanguo.giantcreatureframework.panzeriv",
        "sanguo.giantcreatureframework.protosscarrier"
    ];

    private static readonly string[] AllGcfGiantKindNames =
    [
        "GCF_MechScorpion",
        "GCF_Rathalos",
        "GCF_Valstrax",
        "GCF_MechColossus",
        "GCF_PanzerIV",
        "GCF_ProtossCarrier"
    ];

    private static void BoostHeadlessTickThroughput()
    {
        try { Prefs.RunInBackground = true; } catch { }
        try { Prefs.AutomaticPauseMode = AutomaticPauseMode.Never; } catch { }
        try { Application.targetFrameRate = 120; } catch { }
        try
        {
            if (Faction.OfPlayerSilentFail != null && !Faction.OfPlayer.HasName)
            {
                NamePlayerFactionDialogUtility.Named("猎人公会");
            }
            var homeSettlement = Find.WorldObjects?.Settlements?.FirstOrDefault(s => s.Faction == Faction.OfPlayerSilentFail);
            if (homeSettlement != null && !homeSettlement.namedByPlayer)
            {
                NamePlayerSettlementDialogUtility.Named(homeSettlement, "星辰营地");
            }
        }
        catch { }
        CleanseUiForCapture(hideHud: false);
        if (Find.TickManager != null)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }
    }

    private static Pawn GenerateCapableTestColonist(Map map, IntVec3 spawnCell, bool spawnOnMap = true)
    {
        Pawn? chosen = null;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                PawnKindDefOf.Colonist,
                Faction.OfPlayer,
                context: PawnGenerationContext.PlayerStarter,
                tile: map.Tile,
                forceGenerateNewPawn: true,
                mustBeCapableOfViolence: true));

            if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting) &&
                !p.WorkTagIsDisabled(WorkTags.Intellectual) &&
                !p.WorkTagIsDisabled(WorkTags.Animals) &&
                !p.WorkTagIsDisabled(WorkTags.ManualDumb))
            {
                chosen = p;
                break;
            }
            p.Destroy(DestroyMode.Vanish);
        }

        chosen ??= PawnGenerator.GeneratePawn(new PawnGenerationRequest(
            PawnKindDefOf.Colonist,
            Faction.OfPlayer,
            tile: map.Tile,
            forceGenerateNewPawn: true));

        if (chosen.skills != null)
        {
            var craft = chosen.skills.GetSkill(SkillDefOf.Crafting);
            if (craft != null) craft.Level = 20;
            var intel = chosen.skills.GetSkill(SkillDefOf.Intellectual);
            if (intel != null) intel.Level = 20;
            var animals = chosen.skills.GetSkill(SkillDefOf.Animals);
            if (animals != null) animals.Level = 20;
        }

        if (spawnOnMap)
        {
            GenSpawn.Spawn(chosen, spawnCell, map, Rot4.South, WipeMode.Vanish);
        }
        chosen.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
        chosen.workSettings?.SetPriority(WorkTypeDefOf.Crafting, 1);
        if (chosen.needs?.food != null) chosen.needs.food.CurLevel = chosen.needs.food.MaxLevel;
        if (chosen.needs?.rest != null) chosen.needs.rest.CurLevel = chosen.needs.rest.MaxLevel;
        return chosen;
    }

    private static int CountThingOnMap(Map map, ThingDef? def)
    {
        if (map == null || def == null) return 0;
        var list = map.listerThings.ThingsOfDef(def);
        int sum = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Destroyed) sum += list[i].stackCount;
        }
        return sum;
    }

    [Tool("gcfagent/rathalos_stage1_raid_and_drops", Description = "Stage 1 of live [GCF]Rathalos verification: Day-7 King of the Skies quest trigger, accept quest, manhunting raid Rathalos (untamable), wing break -> Webbing, tail/horn break -> Scale, kill -> Plate, quest success, zero Guild drop-pod rewards.")]
    public async Task<object> RathalosStage1RaidAndDrops(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        var baseline = await ctx.MainThread.InvokeAsync(() =>
        {
            CleanseUiForCapture(hideHud: false);
            BoostHeadlessTickThroughput();
            var homeMap = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active player home map.");
            Current.Game.CurrentMap = homeMap;

            var rubyDef = DefDatabase<ThingDef>.GetNamedSilentFail("GCF_RathalosRuby");
            var scaleDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosScale");
            var webbingDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosWebbing");
            var plateDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosPlate");
            var intelDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosIntel");
            var recipeDef = DefDatabase<RecipeDef>.GetNamed("GCF_Make_RathalosIntel");

            int silverBefore = CountThingOnMap(homeMap, ThingDefOf.Silver);
            int glitterMedBefore = CountThingOnMap(homeMap, ThingDefOf.MedicineUltratech);

            // 420096 is >= 420000 (Day 7) and (420096 & 255) == 0, so setting 420090 + 16 ticks hits the exact natural Day-7 check!
            Find.TickManager.DebugSetTicksGame(420090);
            BoostHeadlessTickThroughput();

            return new
            {
                homeMap,
                rubyRemoved = rubyDef == null,
                itemLabels = new
                {
                    scale = scaleDef.label,
                    webbing = webbingDef.label,
                    plate = plateDef.label,
                    intel = intelDef.label,
                    recipe = recipeDef.label
                },
                itemIconsValid = new
                {
                    scale = scaleDef.uiIcon != null && scaleDef.uiIcon != BaseContent.BadTex,
                    webbing = webbingDef.uiIcon != null && webbingDef.uiIcon != BaseContent.BadTex,
                    plate = plateDef.uiIcon != null && plateDef.uiIcon != BaseContent.BadTex,
                    intel = intelDef.uiIcon != null && intelDef.uiIcon != BaseContent.BadTex
                },
                silverBefore,
                glitterMedBefore
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(16, null, cancellationToken).ConfigureAwait(false);

        var questAccepted = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = baseline.homeMap;
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var quest = Find.QuestManager.QuestsListForReading
                .LastOrDefault(q => q.root == questDef)
                ?? throw new InvalidOperationException("GCF_Quest_RathalosHunt did not fire at Day 7.");

            string questName = quest.name.StripTags();
            string questDesc = quest.description.ToString().StripTags();
            string stateBeforeAccept = quest.State.ToString();
            bool questAvailableLetterFound = Find.LetterStack.LettersListForReading
                .Any(l => l.Label.ToString().StripTags().Contains("空之王者") || l.Label.ToString().StripTags().Contains("King of the Skies"));

            var accepter = homeMap.mapPawns.FreeColonistsSpawned.FirstOrDefault()
                           ?? GenerateCapableTestColonist(homeMap, homeMap.Center);
            quest.Accept(accepter);

            return new
            {
                quest,
                accepter,
                questId = quest.id,
                questName,
                questDescriptionPreview = questDesc.Length > 180 ? questDesc[..180] + "..." : questDesc,
                stateBeforeAccept,
                stateAfterAccept = quest.State.ToString(),
                questAvailableLetterFound
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(20, null, cancellationToken).ConfigureAwait(false);

        var raidAndWingBreak = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = baseline.homeMap;
            var raidRathalos = homeMap.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p.def.defName == "GCF_Rathalos" && !p.Dead)
                ?? throw new InvalidOperationException("Raid GCF_Rathalos did not spawn after accepting King of the Skies.");

            string mentalState = raidRathalos.MentalStateDef?.defName ?? "null";
            bool inAggroMentalState = raidRathalos.InAggroMentalState;
            bool canTameRaid = TameUtility.CanTame(raidRathalos);
            bool designatorTameAcceptedOnRaid = new Designator_Tame().CanDesignateThing(raidRathalos).Accepted;
            bool threatLetterReceived = Find.LetterStack.LettersListForReading
                .Any(l => l.def == LetterDefOf.ThreatBig &&
                          (l.Label.ToString().StripTags().Contains("空之王者") || l.Label.ToString().StripTags().Contains("雄火龙")));

            var rig = GiantFastLookup.TryGetRig(raidRathalos) ?? throw new InvalidOperationException("Missing CompGiantRig on raid Rathalos.");
            rig.RebuildFootprintNow(notifyMap: true);

            var wingPart = raidRathalos.health.hediffSet.GetNotMissingParts()
                .FirstOrDefault(p => p.def.defName == "GCF_Part_Rathalos_WingMembrane"
                                  || p.def.defName == "GCF_Part_Rathalos_WingArm")
                ?? throw new InvalidOperationException("Could not find Rathalos wing BodyPartRecord.");

            raidRathalos.TakeDamage(new DamageInfo(DamageDefOf.SurgicalCut, 40f, 99f, -1f, questAccepted.accepter, wingPart));
            if (!raidRathalos.health.hediffSet.PartIsMissing(wingPart))
            {
                raidRathalos.health.AddHediff(HediffDefOf.MissingBodyPart, wingPart);
            }

            return new
            {
                raidRathalos,
                raidRathalosId = raidRathalos.ThingID,
                spawnPos = raidRathalos.Position.ToString(),
                mentalState,
                inAggroMentalState,
                canTameRaid,
                designatorTameAcceptedOnRaid,
                threatLetterReceived,
                brokenWingPartDef = wingPart.def.defName,
                brokenWingPartLabel = wingPart.Label,
                wingMissingNow = raidRathalos.health.hediffSet.PartIsMissing(wingPart)
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(20, null, cancellationToken).ConfigureAwait(false);

        var otherPartBreak = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = baseline.homeMap;
            var webbingDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosWebbing");
            int webbingCountAfterWingBreak = CountThingOnMap(homeMap, webbingDef);

            var raidRathalos = raidAndWingBreak.raidRathalos;
            var otherPart = raidRathalos.health.hediffSet.GetNotMissingParts()
                .FirstOrDefault(p => p.def.defName == "GCF_Part_Rathalos_TailClub"
                                  || p.def.defName == "GCF_Part_Rathalos_HindTalon")
                ?? throw new InvalidOperationException("Could not find non-wing Rathalos BodyPartRecord.");

            // Sever non-wing tail/hind-talon cleanly without lethal torso/skull overflow so OnBoneSevered fires while alive
            raidRathalos.TakeDamage(new DamageInfo(DamageDefOf.SurgicalCut, 40f, 99f, -1f, questAccepted.accepter, otherPart));
            if (!raidRathalos.health.hediffSet.PartIsMissing(otherPart))
            {
                raidRathalos.health.AddHediff(HediffDefOf.MissingBodyPart, otherPart);
            }

            return new
            {
                webbingCountAfterWingBreak,
                brokenOtherPartDef = otherPart.def.defName,
                brokenOtherPartLabel = otherPart.Label,
                otherPartMissingNow = raidRathalos.health.hediffSet.PartIsMissing(otherPart)
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(20, null, cancellationToken).ConfigureAwait(false);

        var killStep = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = baseline.homeMap;
            var scaleDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosScale");
            int scaleCountAfterOtherBreak = CountThingOnMap(homeMap, scaleDef);

            if (!raidAndWingBreak.raidRathalos.Dead)
            {
                raidAndWingBreak.raidRathalos.Kill(new DamageInfo(DamageDefOf.Bullet, 9999f, 99f, -1f, questAccepted.accepter));
            }
            return new { scaleCountAfterOtherBreak, deadNow = raidAndWingBreak.raidRathalos.Dead };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(20, null, cancellationToken).ConfigureAwait(false);

        var postKill = await ctx.MainThread.InvokeAsync(() =>
        {
            var homeMap = baseline.homeMap;
            var scaleDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosScale");
            var webbingDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosWebbing");
            var plateDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosPlate");
            var dropPodIncomingDef = DefDatabase<ThingDef>.GetNamedSilentFail("DropPodIncoming");
            var activeDropPodDef = DefDatabase<ThingDef>.GetNamedSilentFail("ActiveDropPod")
                                ?? DefDatabase<ThingDef>.GetNamedSilentFail("ActiveTransporter");

            int webbingCount = CountThingOnMap(homeMap, webbingDef);
            int scaleCount = CountThingOnMap(homeMap, scaleDef);
            int plateCount = CountThingOnMap(homeMap, plateDef);
            int silverAfter = CountThingOnMap(homeMap, ThingDefOf.Silver);
            int glitterMedAfter = CountThingOnMap(homeMap, ThingDefOf.MedicineUltratech);
            int dropPodsOnMap = CountThingOnMap(homeMap, dropPodIncomingDef) + CountThingOnMap(homeMap, activeDropPodDef);

            return new
            {
                huntQuestStateAfterKill = questAccepted.quest.State.ToString(),
                webbingCount,
                scaleCount,
                plateCount,
                silverDelta = silverAfter - baseline.silverBefore,
                glitterMedDelta = glitterMedAfter - baseline.glitterMedBefore,
                dropPodsOnMap,
                zeroGuildRewardDropPods = (silverAfter - baseline.silverBefore == 0) &&
                                          (glitterMedAfter - baseline.glitterMedBefore == 0) &&
                                          dropPodsOnMap == 0
            };
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            ok = baseline.rubyRemoved &&
                 questAccepted.questName == "空之王者" &&
                 raidAndWingBreak.mentalState == "ManhunterPermanent" &&
                 !raidAndWingBreak.canTameRaid &&
                 !raidAndWingBreak.designatorTameAcceptedOnRaid &&
                 postKill.webbingCount >= 1 &&
                 postKill.scaleCount >= 1 &&
                 postKill.plateCount >= 1 &&
                 postKill.huntQuestStateAfterKill == "EndedSuccess" &&
                 postKill.zeroGuildRewardDropPods,
            defsAndLocalization = new
            {
                baseline.rubyRemoved,
                baseline.itemLabels,
                baseline.itemIconsValid
            },
            day7QuestTrigger = new
            {
                questAccepted.questId,
                questAccepted.questName,
                questAccepted.questDescriptionPreview,
                questAccepted.stateBeforeAccept,
                questAccepted.stateAfterAccept,
                questAccepted.questAvailableLetterFound
            },
            colonyRaidAndDrops = new
            {
                raidAndWingBreak.raidRathalosId,
                raidAndWingBreak.spawnPos,
                raidAndWingBreak.mentalState,
                raidAndWingBreak.inAggroMentalState,
                raidAndWingBreak.canTameRaid,
                raidAndWingBreak.designatorTameAcceptedOnRaid,
                raidAndWingBreak.threatLetterReceived,
                raidAndWingBreak.brokenWingPartDef,
                raidAndWingBreak.brokenWingPartLabel,
                raidAndWingBreak.wingMissingNow,
                otherPartBreak.webbingCountAfterWingBreak,
                otherPartBreak.brokenOtherPartDef,
                otherPartBreak.brokenOtherPartLabel,
                otherPartBreak.otherPartMissingNow,
                killStep.scaleCountAfterOtherBreak,
                killStep.deadNow,
                postKill.huntQuestStateAfterKill,
                postKill.webbingCount,
                postKill.scaleCount,
                postKill.plateCount,
                postKill.silverDelta,
                postKill.glitterMedDelta,
                postKill.dropPodsOnMap,
                postKill.zeroGuildRewardDropPods
            }
        };
    }

    [Tool("gcfagent/rathalos_stage2_craft_and_read", Description = "Stage 2 of live [GCF]Rathalos verification: colonist crafts GCF_RathalosIntel from dropped Rathalos material at a CraftingSpot, then reads it via CompUsable to spawn the Ancient Forest site on the world map.")]
    public async Task<object> RathalosStage2CraftAndRead(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        var setup = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active player home map.");
            Current.Game.CurrentMap = homeMap;

            var scaleDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosScale");
            var webbingDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosWebbing");
            var plateDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosPlate");

            int totalTrophiesBeforeCraft = CountThingOnMap(homeMap, webbingDef)
                                         + CountThingOnMap(homeMap, scaleDef)
                                         + CountThingOnMap(homeMap, plateDef);

            var padCenter = CellFinder.StandableCellNear(homeMap.Center, homeMap, 12f);
            if (!padCenter.IsValid) padCenter = homeMap.Center;
            foreach (var c in CellRect.CenteredOn(padCenter, 9, 9).ClipInsideMap(homeMap))
            {
                homeMap.roofGrid?.SetRoof(c, null);
                foreach (var t in c.GetThingList(homeMap).ToList())
                {
                    if (t is not Pawn && t.def.destroyable && !t.Destroyed &&
                        t.def != scaleDef && t.def != webbingDef && t.def != plateDef)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }
                homeMap.terrainGrid?.SetTerrain(c, TerrainDefOf.Soil);
                homeMap.fogGrid?.Unfog(c);
            }

            var droppedScale = homeMap.listerThings.ThingsOfDef(scaleDef).FirstOrDefault(t => !t.Destroyed)
                ?? homeMap.listerThings.ThingsOfDef(webbingDef).FirstOrDefault(t => !t.Destroyed)
                ?? homeMap.listerThings.ThingsOfDef(plateDef).FirstOrDefault(t => !t.Destroyed)
                ?? throw new InvalidOperationException("No dropped Rathalos material found on home map.");

            string usedIngredientDef = droppedScale.def.defName;
            droppedScale.DeSpawn(DestroyMode.Vanish);
            GenPlace.TryPlaceThing(droppedScale, padCenter + new IntVec3(1, 0, 0), homeMap, ThingPlaceMode.Direct);
            droppedScale.SetForbidden(false, warnOnFail: false);

            var craftingSpotDef = DefDatabase<ThingDef>.GetNamed("CraftingSpot");
            var craftingSpot = (Building_WorkTable)ThingMaker.MakeThing(craftingSpotDef);
            craftingSpot.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(craftingSpot, padCenter, homeMap, Rot4.South, WipeMode.Vanish);

            var recipeDef = DefDatabase<RecipeDef>.GetNamed("GCF_Make_RathalosIntel");
            var bill = (Bill_Production)recipeDef.MakeNewBill();
            bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
            bill.repeatCount = 1;
            craftingSpot.billStack.AddBill(bill);

            var crafter = GenerateCapableTestColonist(homeMap, padCenter + new IntVec3(0, 0, -1));
            var wg = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("DoBillsUseCraftingSpot").Worker;
            var craftJob = wg.JobOnThing(crafter, craftingSpot, forced: true);
            bool craftJobStarted = craftJob != null && crafter.jobs.TryTakeOrderedJob(craftJob, JobTag.Misc);

            return new
            {
                homeMap,
                craftingSpot,
                crafter,
                wg,
                usedIngredientDef,
                craftJobStarted,
                totalTrophiesBeforeCraft
            };
        }, cancellationToken).ConfigureAwait(false);

        int craftTicksElapsed = 0;
        for (int step = 0; step < 16; step++)
        {
            await ctx.MainThread.InvokeAsync(() => { BoostHeadlessTickThroughput(); return true; }, cancellationToken).ConfigureAwait(false);
            await ctx.Game.RunForTicksAsync(200, null, cancellationToken).ConfigureAwait(false);
            craftTicksElapsed += 200;

            bool crafted = await ctx.MainThread.InvokeAsync(() =>
            {
                var intelDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosIntel");
                if (CountThingOnMap(setup.homeMap, intelDef) > 0)
                {
                    return true;
                }
                if (setup.crafter.CurJobDef != JobDefOf.DoBill && setup.craftingSpot.billStack.AnyShouldDoNow)
                {
                    var retryJob = setup.wg.JobOnThing(setup.crafter, setup.craftingSpot, forced: true);
                    if (retryJob != null)
                    {
                        setup.crafter.jobs.TryTakeOrderedJob(retryJob, JobTag.Misc);
                    }
                }
                return false;
            }, cancellationToken).ConfigureAwait(false);
            if (crafted) break;
        }

        var readOrder = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = setup.homeMap;
            var scaleDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosScale");
            var webbingDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosWebbing");
            var plateDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosPlate");
            var intelDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosIntel");

            var intelThing = homeMap.listerThings.ThingsOfDef(intelDef).FirstOrDefault(t => !t.Destroyed)
                ?? throw new InvalidOperationException("Colonist did not finish crafting GCF_RathalosIntel.");
            intelThing.SetForbidden(false, warnOnFail: false);
            homeMap.reservationManager.ReleaseAllForTarget(intelThing);

            int totalTrophiesAfterCraft = CountThingOnMap(homeMap, webbingDef)
                                        + CountThingOnMap(homeMap, scaleDef)
                                        + CountThingOnMap(homeMap, plateDef);

            var usable = intelThing.TryGetComp<CompUsable>()
                ?? throw new InvalidOperationException("GCF_RathalosIntel missing CompUsable.");
            var canUse = usable.CanBeUsedBy(setup.crafter, forced: true);
            var floatOpt = usable.CompFloatMenuOptions(setup.crafter).FirstOrDefault();
            string floatMenuLabel = floatOpt?.Label ?? "";
            bool floatMenuDisabled = floatOpt?.Disabled ?? true;

            usable.TryStartUseJob(setup.crafter, LocalTargetInfo.Invalid, forced: true);

            return new
            {
                intelThing,
                craftedIntelCount = CountThingOnMap(homeMap, intelDef),
                trophiesConsumed = setup.totalTrophiesBeforeCraft - totalTrophiesAfterCraft,
                craftTicksElapsed,
                canUseAccepted = canUse.Accepted,
                floatMenuLabel,
                floatMenuDisabled,
                useJobCurDef = setup.crafter.CurJobDef?.defName ?? "null"
            };
        }, cancellationToken).ConfigureAwait(false);

        for (int rStep = 0; rStep < 5; rStep++)
        {
            await ctx.MainThread.InvokeAsync(() => { BoostHeadlessTickThroughput(); return true; }, cancellationToken).ConfigureAwait(false);
            await ctx.Game.RunForTicksAsync(120, null, cancellationToken).ConfigureAwait(false);
            bool consumed = await ctx.MainThread.InvokeAsync(() => readOrder.intelThing.Destroyed, cancellationToken).ConfigureAwait(false);
            if (consumed) break;
        }

        var postRead = await ctx.MainThread.InvokeAsync(() =>
        {
            var afQuestDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_AncientForest");
            var afSitePartDef = DefDatabase<SitePartDef>.GetNamed("GCF_SitePart_AncientForest");

            var afQuest = Find.QuestManager.QuestsListForReading
                .LastOrDefault(q => q.root == afQuestDef)
                ?? throw new InvalidOperationException("Reading GCF_RathalosIntel did not generate GCF_Quest_AncientForest.");

            var site = Find.WorldObjects.Sites
                .LastOrDefault(s => s.parts != null && s.parts.Any(p => p.def == afSitePartDef))
                ?? throw new InvalidOperationException("Ancient Forest Site world object was not spawned.");

            bool discoveryLetterReceived = Find.LetterStack.LettersListForReading
                .Any(l => l.Label.ToString().StripTags().Contains("古代树森林") || l.Label.ToString().StripTags().Contains("Ancient Forest"));

            string siteBiome = Find.WorldGrid[site.Tile]?.PrimaryBiome?.defName ?? "unknown";

            return new
            {
                intelConsumed = readOrder.intelThing.Destroyed,
                afQuestId = afQuest.id,
                afQuestName = afQuest.name.StripTags(),
                afQuestState = afQuest.State.ToString(),
                siteLabel = site.Label,
                siteTileId = site.Tile.tileId,
                siteBiome,
                discoveryLetterReceived
            };
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            ok = readOrder.craftedIntelCount >= 1 &&
                 readOrder.trophiesConsumed == 1 &&
                 postRead.intelConsumed &&
                 postRead.afQuestName == "古代树森林" &&
                 postRead.siteLabel.Contains("古代树森林"),
            setup.usedIngredientDef,
            setup.craftJobStarted,
            readOrder.craftedIntelCount,
            readOrder.trophiesConsumed,
            readOrder.craftTicksElapsed,
            readOrder.canUseAccepted,
            readOrder.floatMenuLabel,
            readOrder.floatMenuDisabled,
            readOrder.useJobCurDef,
            postRead.intelConsumed,
            postRead.afQuestId,
            postRead.afQuestName,
            postRead.afQuestState,
            postRead.siteLabel,
            postRead.siteTileId,
            postRead.siteBiome,
            postRead.discoveryLetterReceived
        };
    }

    [Tool("gcfagent/rathalos_stage3_ancient_forest_tame", Description = "Stage 3 of live [GCF]Rathalos verification: caravan enters Ancient Forest world site, verifies zero unrequested supply items, verifies wild Rathalos is calm & tamable, designates & tames it, and verifies Ancient Forest quest completion.")]
    public async Task<object> RathalosStage3AncientForestTame(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        var enter = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active player home map.");
            var afQuestDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_AncientForest");
            var afSitePartDef = DefDatabase<SitePartDef>.GetNamed("GCF_SitePart_AncientForest");

            var afQuest = Find.QuestManager.QuestsListForReading
                .LastOrDefault(q => q.root == afQuestDef)
                ?? throw new InvalidOperationException("GCF_Quest_AncientForest not found.");

            var site = Find.WorldObjects.Sites
                .LastOrDefault(s => s.parts != null && s.parts.Any(p => p.def == afSitePartDef))
                ?? throw new InvalidOperationException("Ancient Forest Site not found on world map.");

            var tamer = GenerateCapableTestColonist(homeMap, homeMap.Center, spawnOnMap: false);
            var caravan = CaravanMaker.MakeCaravan([tamer], Faction.OfPlayer, site.Tile, addToWorldPawnsIfNotAlready: true);
            new CaravanArrivalAction_VisitSite(site).Arrived(caravan);

            return new { homeMap, afQuest, site, tamer };
        }, cancellationToken).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            bool ready = await ctx.MainThread.InvokeAsync(() =>
                !LongEventHandler.AnyEventNowOrWaiting && enter.site.HasMap,
                cancellationToken).ConfigureAwait(false);
            if (ready) break;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        await ctx.MainThread.InvokeAsync(() => { BoostHeadlessTickThroughput(); return true; }, cancellationToken).ConfigureAwait(false);
        await ctx.Game.RunForTicksAsync(20, null, cancellationToken).ConfigureAwait(false);

        var inspectAndTame = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var siteMap = enter.site.Map ?? throw new InvalidOperationException("Ancient Forest site map was not generated.");
            Current.Game.CurrentMap = siteMap;

            var kibbleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Kibble");
            var survivalMealDef = DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack");
            var indMedDef = DefDatabase<ThingDef>.GetNamedSilentFail("MedicineIndustrial");

            int kibbleCount = CountThingOnMap(siteMap, kibbleDef);
            int survivalMealCount = CountThingOnMap(siteMap, survivalMealDef);
            int indMedCount = CountThingOnMap(siteMap, indMedDef);
            int silverCount = CountThingOnMap(siteMap, ThingDefOf.Silver);

            var wildRathalos = siteMap.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p.def.defName == "GCF_Rathalos" && !p.Dead)
                ?? throw new InvalidOperationException("Wild GCF_Rathalos was not spawned in Ancient Forest.");

            string factionBeforeTame = wildRathalos.Faction?.Name ?? "Wild(null)";
            string mentalState = wildRathalos.MentalStateDef?.defName ?? "null";
            bool inAggroMentalState = wildRathalos.InAggroMentalState;
            bool canTameWild = TameUtility.CanTame(wildRathalos);
            bool designatorTameAccepted = new Designator_Tame().CanDesignateThing(wildRathalos).Accepted;

            siteMap.designationManager.AddDesignation(new Designation(wildRathalos, DesignationDefOf.Tame));
            bool hasTameDesignation = siteMap.designationManager.DesignationOn(wildRathalos, DesignationDefOf.Tame) != null;

            InteractionWorker_RecruitAttempt.DoRecruit(enter.tamer, wildRathalos, useAudiovisualEffects: true);

            return new
            {
                siteMapSize = siteMap.Size.ToString(),
                zeroArtificialSupplies = (kibbleCount == 0 && survivalMealCount == 0 && indMedCount == 0 && silverCount == 0),
                supplyCounts = new { kibbleCount, survivalMealCount, indMedCount, silverCount },
                wildRathalos,
                wildRathalosId = wildRathalos.ThingID,
                wildRathalosPos = wildRathalos.Position.ToString(),
                factionBeforeTame,
                mentalState,
                inAggroMentalState,
                canTameWild,
                designatorTameAccepted,
                hasTameDesignation
            };
        }, cancellationToken).ConfigureAwait(false);

        // Step 260 ticks so CompRathalosHuntTarget.CompTickRare (250-tick interval) fires and marks Ancient Forest quest EndedSuccess
        await ctx.Game.RunForTicksAsync(260, null, cancellationToken).ConfigureAwait(false);

        var postTame = await ctx.MainThread.InvokeAsync(() =>
        {
            var tamed = inspectAndTame.wildRathalos;
            bool tamedIsPlayerFaction = tamed.Faction == Faction.OfPlayer;
            string tamedFactionName = tamed.Faction?.Name ?? "null";
            string afQuestStateAfterTame = enter.afQuest.State.ToString();

            Current.Game.CurrentMap = enter.homeMap;
            CleanseUiForCapture(hideHud: false);

            return new
            {
                tamedIsPlayerFaction,
                tamedFactionName,
                afQuestStateAfterTame
            };
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            ok = inspectAndTame.zeroArtificialSupplies &&
                 inspectAndTame.mentalState == "null" &&
                 !inspectAndTame.inAggroMentalState &&
                 inspectAndTame.canTameWild &&
                 inspectAndTame.designatorTameAccepted &&
                 inspectAndTame.hasTameDesignation &&
                 postTame.tamedIsPlayerFaction &&
                 postTame.afQuestStateAfterTame == "EndedSuccess",
            inspectAndTame.siteMapSize,
            inspectAndTame.zeroArtificialSupplies,
            inspectAndTame.supplyCounts,
            inspectAndTame.wildRathalosId,
            inspectAndTame.wildRathalosPos,
            inspectAndTame.factionBeforeTame,
            inspectAndTame.mentalState,
            inspectAndTame.inAggroMentalState,
            inspectAndTame.canTameWild,
            inspectAndTame.designatorTameAccepted,
            inspectAndTame.hasTameDesignation,
            postTame.tamedIsPlayerFaction,
            postTame.tamedFactionName,
            postTame.afQuestStateAfterTame
        };
    }

    [Tool("gcfagent/verify_all_submods_live", Description = "Audit full-DLC framework default-off switches, zero trader/loot/biome pollution across all loaded DLCs, zero Def.ConfigErrors(), and live spawn + 120-tick runtime verification of all 6 GCF giants.")]
    public async Task<object> VerifyAllSubmodsLive(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        var auditAndSpawn = await ctx.MainThread.InvokeAsync(() =>
        {
            CleanseUiForCapture(hideHud: false);
            BoostHeadlessTickThroughput();
            var map = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active map.");
            Current.Game.CurrentMap = map;

            var pkgSet = new HashSet<string>(AllGcfPackageIds, StringComparer.OrdinalIgnoreCase);

            var s = GiantCreatureFrameworkMod.Settings;
            var defaultSettings = new
            {
                s.drawCollisionCells,
                s.drawHitCells,
                s.drawSkeleton,
                s.enableDebugPanel,
                s.enableAuthoringTerminal
            };

            var configErrors = new List<string>();
            var gcfThingDefs = new List<ThingDef>();
            foreach (var td in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                var pkg = td.modContentPack?.PackageIdPlayerFacing;
                if (pkg != null && pkgSet.Contains(pkg))
                {
                    gcfThingDefs.Add(td);
                    foreach (var err in td.ConfigErrors())
                    {
                        configErrors.Add($"ThingDef({td.defName}): {err}");
                    }
                }
            }

            foreach (var kdName in AllGcfGiantKindNames)
            {
                var kd = DefDatabase<PawnKindDef>.GetNamedSilentFail(kdName);
                if (kd == null)
                {
                    configErrors.Add($"Missing PawnKindDef: {kdName}");
                    continue;
                }
                foreach (var err in kd.ConfigErrors()) configErrors.Add($"PawnKindDef({kd.defName}): {err}");
                if (kd.race?.race?.body != null)
                {
                    foreach (var err in kd.race.race.body.ConfigErrors())
                    {
                        configErrors.Add($"BodyDef({kd.race.race.body.defName}): {err}");
                    }
                }
                var rigDef = kd.race?.comps?.OfType<CompProperties_GiantRig>().FirstOrDefault()?.rigDef;
                if (rigDef == null)
                {
                    configErrors.Add($"Missing GiantRigDef on {kdName}");
                }
                else
                {
                    foreach (var err in rigDef.ConfigErrors())
                    {
                        configErrors.Add($"GiantRigDef({rigDef.defName}): {err}");
                    }
                }
            }

            var itemViolations = new List<string>();
            var traderLeaks = new List<string>();
            var gcfItemAndPawnDefs = gcfThingDefs
                .Where(d => d.category == ThingCategory.Item || d.category == ThingCategory.Pawn)
                .ToList();

            foreach (var d in gcfThingDefs.Where(x => x.category == ThingCategory.Item && !x.IsCorpse))
            {
                if (d.tradeability != Tradeability.None || d.genericMarketSellable || !d.preventSpawningInResourcePod)
                {
                    itemViolations.Add($"{d.defName}(tradeability={d.tradeability}, market={d.genericMarketSellable}, noPod={d.preventSpawningInResourcePod})");
                }
            }

            foreach (var trader in DefDatabase<TraderKindDef>.AllDefsListForReading)
            {
                if (trader.stockGenerators == null) continue;
                for (int i = 0; i < trader.stockGenerators.Count; i++)
                {
                    var sg = trader.stockGenerators[i];
                    if (sg == null) continue;
                    for (int j = 0; j < gcfItemAndPawnDefs.Count; j++)
                    {
                        if (sg.HandlesThingDef(gcfItemAndPawnDefs[j]))
                        {
                            traderLeaks.Add($"{trader.defName}->{gcfItemAndPawnDefs[j].defName}({sg.GetType().Name})");
                        }
                    }
                }
            }

            var biomeLeaks = new List<string>();
            foreach (var biome in DefDatabase<BiomeDef>.AllDefsListForReading)
            {
                foreach (var kdName in AllGcfGiantKindNames)
                {
                    var kd = DefDatabase<PawnKindDef>.GetNamedSilentFail(kdName);
                    if (kd == null) continue;
                    if (biome.CommonalityOfAnimal(kd) > 0f ||
                        biome.CommonalityOfPollutionAnimal(kd) > 0f ||
                        biome.CommonalityOfCoastalAnimal(kd) > 0f)
                    {
                        biomeLeaks.Add($"{biome.defName}->{kdName}");
                    }
                }
            }

            var center = map.Center;
            var spawnedGiants = new List<Pawn>();
            int idx = 0;
            foreach (var kdName in AllGcfGiantKindNames)
            {
                var kd = DefDatabase<PawnKindDef>.GetNamed(kdName);
                int gx = Mathf.Clamp(center.x - 30 + idx * 12, 16, map.Size.x - 16);
                int gz = Mathf.Clamp(center.z + 18, 16, map.Size.z - 16);
                var spawnCell = new IntVec3(gx, 0, gz);
                foreach (var c in CellRect.CenteredOn(spawnCell, 9, 9).ClipInsideMap(map))
                {
                    map.roofGrid?.SetRoof(c, null);
                    map.fogGrid?.Unfog(c);
                }

                var gp = PawnGenerator.GeneratePawn(kd, Faction.OfPlayer);
                GenSpawn.Spawn(gp, spawnCell, map, Rot4.South, WipeMode.Vanish);
                var rig = GiantFastLookup.TryGetRig(gp);
                rig?.RebuildFootprintNow(notifyMap: true);
                if (rig?.FlightLocomotion != null && rig.RigDef.ActiveFlightLocomotion != null)
                {
                    rig.FlightLocomotion.TryCommandTakeoff(rig, out _);
                }
                else
                {
                    var dest = new IntVec3(gx, 0, Mathf.Clamp(gz - 5, 12, map.Size.z - 12));
                    gp.jobs?.StartJob(JobMaker.MakeJob(JobDefOf.Goto, dest), JobCondition.InterruptForced);
                }
                spawnedGiants.Add(gp);
                idx++;
            }

            return new
            {
                map,
                defaultSettings,
                gcfThingDefCount = gcfThingDefs.Count,
                configErrors,
                itemViolations,
                traderCountChecked = DefDatabase<TraderKindDef>.AllDefsListForReading.Count,
                traderLeaks,
                biomeCountChecked = DefDatabase<BiomeDef>.AllDefsListForReading.Count,
                biomeLeaks,
                spawnedGiants
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(120, null, cancellationToken).ConfigureAwait(false);

        var postTickSnapshots = await ctx.MainThread.InvokeAsync(() =>
        {
            var list = new List<object>();
            foreach (var gp in auditAndSpawn.spawnedGiants)
            {
                var rig = GiantFastLookup.TryGetRig(gp);
                var cockpit = GiantFastLookup.TryGetCockpit(gp);
                var fl = rig?.FlightLocomotion;
                int missingTextures = rig?.RigDef?.SortedNodes
                    .Count(n => !string.IsNullOrEmpty(n.texPath) && ContentFinder<Texture2D>.Get(n.texPath, false) == null) ?? -1;

                list.Add(new
                {
                    kind = gp.kindDef.defName,
                    label = gp.LabelShort,
                    spawned = gp.Spawned,
                    dead = gp.Dead,
                    nodeCount = rig?.RigDef?.SortedNodes.Length ?? 0,
                    missingTextures,
                    animState = rig?.CurrentAnimState.ToString() ?? "null",
                    flightState = fl?.FlightState.ToString() ?? "GroundedOnly",
                    altitude01 = fl?.CurrentAltitude01 ?? 0f,
                    seatCount = cockpit?.SeatCount ?? 0,
                    abilityCount = gp.abilities?.abilities?.Count ?? 0
                });

                if (gp.Spawned && !gp.Destroyed)
                {
                    gp.DeSpawn(DestroyMode.Vanish);
                }
            }
            GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            return list;
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            ok = auditAndSpawn.configErrors.Count == 0 &&
                 auditAndSpawn.itemViolations.Count == 0 &&
                 auditAndSpawn.traderLeaks.Count == 0 &&
                 auditAndSpawn.biomeLeaks.Count == 0 &&
                 postTickSnapshots.Count == 6,
            auditAndSpawn.defaultSettings,
            auditAndSpawn.gcfThingDefCount,
            configErrorCount = auditAndSpawn.configErrors.Count,
            configErrors = auditAndSpawn.configErrors,
            itemViolationCount = auditAndSpawn.itemViolations.Count,
            itemViolations = auditAndSpawn.itemViolations,
            auditAndSpawn.traderCountChecked,
            traderLeakCount = auditAndSpawn.traderLeaks.Count,
            traderLeaks = auditAndSpawn.traderLeaks,
            auditAndSpawn.biomeCountChecked,
            biomeLeakCount = auditAndSpawn.biomeLeaks.Count,
            biomeLeaks = auditAndSpawn.biomeLeaks,
            liveGiantResults = postTickSnapshots
        };
    }

    [Tool("gcfagent/verify_dragonscale_sword_live", Description = "Verify GCF_DragonscaleSword texture, 1.2x Monosword live stats, trader/loot isolation, and live smithy crafting from 2x Scale + 2x Webbing + 1x Plate.")]
    public async Task<object> VerifyDragonscaleSwordLive(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        var setup = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var map = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active player home map.");
            Current.Game.CurrentMap = map;

            var swordDef = DefDatabase<ThingDef>.GetNamed("GCF_DragonscaleSword");
            var monoDef = DefDatabase<ThingDef>.GetNamed("MeleeWeapon_MonoSword");
            var makeRecipeDef = DefDatabase<RecipeDef>.GetNamed("Make_GCF_DragonscaleSword");
            var scaleDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosScale");
            var webbingDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosWebbing");
            var plateDef = DefDatabase<ThingDef>.GetNamed("GCF_RathalosPlate");

            var configErrors = swordDef.ConfigErrors().Concat(makeRecipeDef.ConfigErrors()).ToArray();
            bool uiIconValid = swordDef.uiIcon != null && swordDef.uiIcon != BaseContent.BadTex;

            // Compare Normal quality stats directly via RimWorld StatExtension.GetStatValue
            var tmpDragon = (ThingWithComps)ThingMaker.MakeThing(swordDef);
            tmpDragon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
            var tmpMono = (ThingWithComps)ThingMaker.MakeThing(monoDef);
            tmpMono.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);

            var avgApStat = DefDatabase<StatDef>.GetNamed("MeleeWeapon_AverageArmorPenetration");
            float dragonDps = tmpDragon.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS);
            float monoDps = tmpMono.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS);
            float dragonAp = tmpDragon.GetStatValue(avgApStat);
            float monoAp = tmpMono.GetStatValue(avgApStat);
            int dragonHp = tmpDragon.MaxHitPoints;
            int monoHp = tmpMono.MaxHitPoints;
            float dragonVal = tmpDragon.GetStatValue(StatDefOf.MarketValue);
            float monoVal = tmpMono.GetStatValue(StatDefOf.MarketValue);
            tmpDragon.Destroy(DestroyMode.Vanish);
            tmpMono.Destroy(DestroyMode.Vanish);

            var traderLeaks = new List<string>();
            foreach (var trader in DefDatabase<TraderKindDef>.AllDefsListForReading)
            {
                if (trader.stockGenerators == null) continue;
                foreach (var sg in trader.stockGenerators)
                {
                    if (sg != null && sg.HandlesThingDef(swordDef))
                    {
                        traderLeaks.Add($"{trader.defName}({sg.GetType().Name})");
                    }
                }
            }

            // Stage clean 11x11 smithing pad
            var pad = CellFinder.StandableCellNear(map.Center, map, 12f);
            if (!pad.IsValid) pad = map.Center;
            foreach (var c in CellRect.CenteredOn(pad, 11, 11).ClipInsideMap(map))
            {
                map.roofGrid?.SetRoof(c, null);
                foreach (var t in c.GetThingList(map).ToList())
                {
                    if (t is not Pawn && t.def.destroyable && !t.Destroyed)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }
                map.terrainGrid?.SetTerrain(c, TerrainDefOf.Soil);
                map.fogGrid?.Unfog(c);
            }

            // Clear any preexisting Rathalos materials on map so ingredient consumption math is exact
            foreach (var td in new[] { scaleDef, webbingDef, plateDef, swordDef })
            {
                foreach (var existing in map.listerThings.ThingsOfDef(td).ToList())
                {
                    if (!existing.Destroyed) existing.Destroy(DestroyMode.Vanish);
                }
            }

            var smithyDef = DefDatabase<ThingDef>.GetNamed("FueledSmithy");
            var smithy = (Building_WorkTable)ThingMaker.MakeThing(smithyDef);
            smithy.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(smithy, pad, map, Rot4.South, WipeMode.Vanish);
            smithy.TryGetComp<CompRefuelable>()?.Refuel(50f);

            var scales = ThingMaker.MakeThing(scaleDef);
            scales.stackCount = 2;
            GenPlace.TryPlaceThing(scales, pad + new IntVec3(-2, 0, -1), map, ThingPlaceMode.Direct);

            var webbings = ThingMaker.MakeThing(webbingDef);
            webbings.stackCount = 2;
            GenPlace.TryPlaceThing(webbings, pad + new IntVec3(-1, 0, -2), map, ThingPlaceMode.Direct);

            var plate = ThingMaker.MakeThing(plateDef);
            plate.stackCount = 1;
            GenPlace.TryPlaceThing(plate, pad + new IntVec3(1, 0, -2), map, ThingPlaceMode.Direct);

            var bill = (Bill_Production)makeRecipeDef.MakeNewBill();
            bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
            bill.repeatCount = 1;
            smithy.billStack.AddBill(bill);

            var smith = GenerateCapableTestColonist(map, smithy.InteractionCell);
            var wgDef = DefDatabase<WorkGiverDef>.AllDefsListForReading
                .First(w => w.fixedBillGiverDefs != null && w.fixedBillGiverDefs.Contains(smithyDef));
            var wg = (WorkGiver_Scanner)wgDef.Worker;
            var job = wg.JobOnThing(smith, smithy, forced: true);
            bool jobStarted = job != null && smith.jobs.TryTakeOrderedJob(job, JobTag.Misc);

            return new
            {
                map,
                swordDef,
                scaleDef,
                webbingDef,
                plateDef,
                smithy,
                smith,
                wg,
                swordLabel = swordDef.label,
                recipeLabel = makeRecipeDef.label,
                uiIconValid,
                configErrors,
                traderLeaks,
                isolation = new
                {
                    tradeability = swordDef.tradeability.ToString(),
                    genericMarketSellable = swordDef.genericMarketSellable,
                    preventSpawningInResourcePod = swordDef.preventSpawningInResourcePod
                },
                statComparison = new
                {
                    dragonDps,
                    monoDps,
                    dpsRatio = dragonDps / monoDps,
                    dragonAp,
                    monoAp,
                    apRatio = dragonAp / monoAp,
                    dragonHp,
                    monoHp,
                    hpRatio = (float)dragonHp / monoHp,
                    dragonVal,
                    monoVal,
                    valRatio = dragonVal / monoVal
                },
                jobStarted
            };
        }, cancellationToken).ConfigureAwait(false);

        int forgeTicks = 0;
        for (int step = 0; step < 45; step++)
        {
            await ctx.MainThread.InvokeAsync(() =>
            {
                BoostHeadlessTickThroughput();
                if (setup.smith.needs?.food != null) setup.smith.needs.food.CurLevel = setup.smith.needs.food.MaxLevel;
                if (setup.smith.needs?.rest != null) setup.smith.needs.rest.CurLevel = setup.smith.needs.rest.MaxLevel;
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await ctx.Game.RunForTicksAsync(400, null, cancellationToken).ConfigureAwait(false);
            forgeTicks += 400;

            bool finished = await ctx.MainThread.InvokeAsync(() =>
            {
                if (CountThingOnMap(setup.map, setup.swordDef) > 0)
                {
                    return true;
                }
                if (setup.smith.CurJobDef != JobDefOf.DoBill && setup.smithy.billStack.AnyShouldDoNow)
                {
                    var uftDef = DefDatabase<ThingDef>.GetNamed("GCF_UnfinishedDragonWeapon");
                    foreach (var uft in setup.map.listerThings.ThingsOfDef(uftDef))
                    {
                        setup.map.reservationManager.ReleaseAllForTarget(uft);
                    }
                    var retry = setup.wg.JobOnThing(setup.smith, setup.smithy, forced: true);
                    if (retry != null)
                    {
                        setup.smith.jobs.TryTakeOrderedJob(retry, JobTag.Misc);
                    }
                }
                return false;
            }, cancellationToken).ConfigureAwait(false);
            if (finished) break;
        }

        var result = await ctx.MainThread.InvokeAsync(() =>
        {
            var forgedSword = (ThingWithComps?)setup.map.listerThings.ThingsOfDef(setup.swordDef).FirstOrDefault(t => !t.Destroyed)
                ?? throw new InvalidOperationException("Colonist did not finish forging GCF_DragonscaleSword at FueledSmithy.");

            int scalesLeft = CountThingOnMap(setup.map, setup.scaleDef);
            int webbingsLeft = CountThingOnMap(setup.map, setup.webbingDef);
            int platesLeft = CountThingOnMap(setup.map, setup.plateDef);
            string quality = forgedSword.TryGetComp<CompQuality>()?.Quality.ToString() ?? "None";

            forgedSword.DeSpawn(DestroyMode.Vanish);
            setup.smith.equipment?.DestroyAllEquipment();
            setup.smith.equipment?.AddEquipment(forgedSword);
            bool equippedOnColonist = setup.smith.equipment?.Primary == forgedSword;

            return new
            {
                forgedCount = 1,
                forgedQuality = quality,
                forgeTicks,
                scalesLeft,
                webbingsLeft,
                platesLeft,
                exactIngredientsConsumed = (scalesLeft == 0 && webbingsLeft == 0 && platesLeft == 0),
                equippedOnColonist,
                equippedPrimaryDef = setup.smith.equipment?.Primary?.def?.defName ?? "null"
            };
        }, cancellationToken).ConfigureAwait(false);

        bool ratiosExact = Mathf.Abs(setup.statComparison.dpsRatio - 1.2f) < 0.01f &&
                           Mathf.Abs(setup.statComparison.apRatio - 1.2f) < 0.01f &&
                           Mathf.Abs(setup.statComparison.hpRatio - 1.2f) < 0.01f &&
                           Mathf.Abs(setup.statComparison.valRatio - 1.2f) < 0.01f;

        return new
        {
            ok = setup.uiIconValid &&
                 setup.configErrors.Length == 0 &&
                 setup.traderLeaks.Count == 0 &&
                 ratiosExact &&
                 result.exactIngredientsConsumed &&
                 result.equippedOnColonist,
            setup.swordLabel,
            setup.recipeLabel,
            setup.uiIconValid,
            setup.configErrors,
            setup.traderLeaks,
            setup.isolation,
            setup.statComparison,
            craftingResult = result
        };
    }

    [Tool("gcfagent/verify_rathalos_10day_repeat_live", Description = "Verify King of the Skies 10-day duration (offer & active timeout) and 10-day repeat re-issuance after completion.")]
    public async Task<object> VerifyRathalos10DayRepeatLive(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        // 1. Trigger first King of the Skies on Day 7
        var armDay7 = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var homeMap = Find.AnyPlayerHomeMap ?? Find.CurrentMap ?? throw new InvalidOperationException("No active player home map.");
            Current.Game.CurrentMap = homeMap;
            Find.TickManager.DebugSetTicksGame(GenDate.TicksPerDay * 7 - 8);
            return homeMap;
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(32, null, cancellationToken).ConfigureAwait(false);

        // 2. Inspect Quest #1 10-day offer expiry, accept it, verify 10-day active timeout, and slay raid Rathalos
        var quest1Check = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var q1 = Find.QuestManager.QuestsListForReading.LastOrDefault(q => q.root == questDef)
                ?? throw new InvalidOperationException("Initial Day-7 King of the Skies did not fire.");

            int offerExpiryTicks = q1.TicksUntilExpiry;
            float offerExpiryDays = offerExpiryTicks / (float)GenDate.TicksPerDay;

            var accepter = armDay7.mapPawns.FreeColonistsSpawned.FirstOrDefault()
                           ?? GenerateCapableTestColonist(armDay7, armDay7.Center);
            q1.Accept(accepter);

            var activeDelayPart = q1.PartsListForReading.OfType<QuestPart_Delay>().FirstOrDefault(d => d.isBad);
            int activeTimeoutTicks = activeDelayPart?.delayTicks ?? -1;

            var raidRathalos = armDay7.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p.def.defName == "GCF_Rathalos" && !p.Dead)
                ?? throw new InvalidOperationException("Raid #1 Rathalos did not spawn.");
            raidRathalos.Kill(new DamageInfo(DamageDefOf.Bullet, 9999f, 99f, -1f, accepter));
            int completionTick = Find.TickManager.TicksGame;

            return new
            {
                q1,
                q1Id = q1.id,
                q1Name = q1.name.StripTags(),
                offerExpiryTicks,
                offerExpiryDays,
                activeTimeoutTicks,
                activeTimeoutDays = activeTimeoutTicks / (float)GenDate.TicksPerDay,
                q1StateAfterKill = q1.State.ToString(),
                completionTick
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(32, null, cancellationToken).ConfigureAwait(false);

        // 3. Jump to completionTick + 10 days - 3000 ticks (9.95 days later): verify NO early repeat!
        await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            Find.TickManager.DebugSetTicksGame(quest1Check.completionTick + GenDate.TicksPerDay * 10 - 3000);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(96, null, cancellationToken).ConfigureAwait(false);

        var pre10DayCheck = await ctx.MainThread.InvokeAsync(() =>
        {
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            int totalHuntQuests = Find.QuestManager.QuestsListForReading.Count(q => q.root == questDef);
            return new { totalHuntQuestsAt9_99Days = totalHuntQuests };
        }, cancellationToken).ConfigureAwait(false);

        // 4. Advance across exact +10.0 days (completionTick + 600,000): verify Quest #2 fires organically!
        await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            Find.TickManager.DebugSetTicksGame(quest1Check.completionTick + GenDate.TicksPerDay * 10 - 8);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(96, null, cancellationToken).ConfigureAwait(false);

        var quest2Check = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var allHunts = Find.QuestManager.QuestsListForReading.Where(q => q.root == questDef).ToList();
            var q2 = allHunts.LastOrDefault(q => q.id != quest1Check.q1Id)
                ?? throw new InvalidOperationException("Repeat King of the Skies did not fire 10 days after completion.");

            int q2OfferExpiryTicks = q2.TicksUntilExpiry;
            float q2OfferExpiryDays = q2OfferExpiryTicks / (float)GenDate.TicksPerDay;
            string q2StateInitial = q2.State.ToString();

            // Advance +10 days + 128 ticks without accepting q2 to verify 10-day offer expiration!
            Find.TickManager.DebugSetTicksGame(q2.acceptanceExpireTick + 64);

            return new
            {
                q2,
                totalHuntQuestsAt10Days = allHunts.Count,
                q2Id = q2.id,
                q2Name = q2.name.StripTags(),
                q2StateInitial,
                q2OfferExpiryTicks,
                q2OfferExpiryDays
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(32, null, cancellationToken).ConfigureAwait(false);

        var expiryCheck = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            string q2StateAfter10DaysUnaccepted = quest2Check.q2.State.ToString();
            bool q2Dismissed = quest2Check.q2.dismissed;
            return new
            {
                q2StateAfter10DaysUnaccepted,
                q2Dismissed
            };
        }, cancellationToken).ConfigureAwait(false);

        // Run 96 ticks so SyncTrackedQuestState sees q2 EndedOfferExpired (not dismissed!) and schedules +10-day repeat!
        await ctx.Game.RunForTicksAsync(96, null, cancellationToken).ConfigureAwait(false);

        // 5. Advance another +10 days after q2's automatic expiration -> Quest #3 MUST fire (auto-expiration is unrestricted!)
        await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            Find.TickManager.DebugSetTicksGame(quest2Check.q2.acceptanceExpireTick + GenDate.TicksPerDay * 10 + 128);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(96, null, cancellationToken).ConfigureAwait(false);

        var quest3AndOpenUi = await ctx.MainThread.InvokeAsync(() =>
        {
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var allHunts = Find.QuestManager.QuestsListForReading.Where(q => q.root == questDef).ToList();
            var q3 = allHunts.LastOrDefault(q => q.id != quest1Check.q1Id && q.id != quest2Check.q2Id)
                ?? throw new InvalidOperationException("Quest #3 did not re-issue 10 days after Quest #2 automatic expiration!");

            // Open Quests tab and select q3 so the external test can click the real UI Dismiss button while paused!
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Quests, playSound: false);
            if (MainButtonDefOf.Quests.TabWindow is MainTabWindow_Quests questsWin)
            {
                questsWin.Select(q3);
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            return new
            {
                totalHuntQuestsAfterAutoExpireRepeat = allHunts.Count,
                q3Id = q3.id,
                q3Name = q3.name.StripTags(),
                q3StateInitial = q3.State.ToString(),
                q3DismissedBeforeClick = q3.dismissed
            };
        }, cancellationToken).ConfigureAwait(false);

        bool q1Duration10Days = Mathf.Abs(quest1Check.offerExpiryDays - 10f) < 0.05f &&
                                quest1Check.activeTimeoutTicks == GenDate.TicksPerDay * 10;
        bool q2Duration10Days = Mathf.Abs(quest2Check.q2OfferExpiryDays - 10f) < 0.05f;

        return new
        {
            ok = quest1Check.q1StateAfterKill == "EndedSuccess" &&
                 q1Duration10Days &&
                 pre10DayCheck.totalHuntQuestsAt9_99Days == 1 &&
                 quest2Check.totalHuntQuestsAt10Days == 2 &&
                 expiryCheck.q2StateAfter10DaysUnaccepted == "EndedOfferExpired" &&
                 quest3AndOpenUi.totalHuntQuestsAfterAutoExpireRepeat == 3 &&
                 quest3AndOpenUi.q3StateInitial == "NotYetAccepted",
            quest1 = new
            {
                quest1Check.q1Id,
                quest1Check.q1Name,
                quest1Check.offerExpiryTicks,
                quest1Check.offerExpiryDays,
                quest1Check.activeTimeoutTicks,
                quest1Check.activeTimeoutDays,
                quest1Check.q1StateAfterKill,
                quest1Check.completionTick
            },
            pre10DayCheck,
            quest2 = new
            {
                quest2Check.totalHuntQuestsAt10Days,
                quest2Check.q2Id,
                quest2Check.q2Name,
                quest2Check.q2StateInitial,
                quest2Check.q2OfferExpiryTicks,
                quest2Check.q2OfferExpiryDays,
                expiryCheck.q2StateAfter10DaysUnaccepted,
                expiryCheck.q2Dismissed
            },
            quest3AutoExpireRepeat = quest3AndOpenUi
        };
    }

    [Tool("gcfagent/verify_rathalos_dismiss_stop", Description = "Verify after clicking the Quests UI Dismiss button once on Quest #3 that RathalosPermanentlyDismissed is latched immediately (even while paused and even if later un-dismissed) and no Quest #4 ever fires across +35 days.")]
    public async Task<object> VerifyRathalosNeverReturnsAfterDismiss(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        var immediateState = await ctx.MainThread.InvokeAsync(() =>
        {
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var q3 = Find.QuestManager.QuestsListForReading.Last(q => q.root == questDef);
            var progComp = Current.Game.components.First(c => c.GetType().Name == "GameComponent_RathalosProgression");
            bool latchedImmediatelyWhilePaused = (bool)(progComp.GetType().GetProperty("RathalosPermanentlyDismissed")?.GetValue(progComp) ?? false);
            int nextRepeatTickAfterDismiss = (int)(progComp.GetType().GetProperty("NextRathalosRepeatTick")?.GetValue(progComp) ?? -999);
            bool q3DismissedAfterUiClick = q3.dismissed;

            // Even if player later clicks "Resume / UnDismiss" (q3.dismissed = false), once manually dismissed once it must NEVER re-issue!
            q3.dismissed = false;
            Find.MainTabsRoot.EscapeCurrentTab(playSound: false);

            int startTick = Find.TickManager.TicksGame;
            return new
            {
                q3Id = q3.id,
                q3DismissedAfterUiClick,
                latchedImmediatelyWhilePaused,
                nextRepeatTickAfterDismiss,
                startTick
            };
        }, cancellationToken).ConfigureAwait(false);

        // Advance +12 days, +22 days, and +35 days and run ticks at each milestone
        foreach (int addDays in new[] { 12, 22, 35 })
        {
            await ctx.MainThread.InvokeAsync(() =>
            {
                BoostHeadlessTickThroughput();
                Find.TickManager.DebugSetTicksGame(immediateState.startTick + GenDate.TicksPerDay * addDays);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await ctx.Game.RunForTicksAsync(128, null, cancellationToken).ConfigureAwait(false);
        }

        var post35Days = await ctx.MainThread.InvokeAsync(() =>
        {
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var allHunts = Find.QuestManager.QuestsListForReading.Where(q => q.root == questDef).ToList();
            var progComp = Current.Game.components.First(c => c.GetType().Name == "GameComponent_RathalosProgression");
            bool stillPermanentlyDismissed = (bool)(progComp.GetType().GetProperty("RathalosPermanentlyDismissed")?.GetValue(progComp) ?? false);
            int nextRepeatTick = (int)(progComp.GetType().GetProperty("NextRathalosRepeatTick")?.GetValue(progComp) ?? -999);

            return new
            {
                totalHuntQuestsAfterPlus35Days = allHunts.Count,
                stillPermanentlyDismissed,
                nextRepeatTick
            };
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            ok = immediateState.q3DismissedAfterUiClick &&
                 immediateState.latchedImmediatelyWhilePaused &&
                 immediateState.nextRepeatTickAfterDismiss == -1 &&
                 post35Days.totalHuntQuestsAfterPlus35Days == 3 &&
                 post35Days.stillPermanentlyDismissed &&
                 post35Days.nextRepeatTick == -1,
            immediateState,
            post35Days
        };
    }

    [Tool("gcfagent/verify_rathalos_bgm_live", Description = "Verify end-to-end live in RimWorld 1.7 that King of the Skies (空之王者) replaces Proof of a Hero with Roar / Rathalos (咆哮 / 雄火龙), does not play while NotYetAccepted, plays immediately on quest acceptance (even while paused), and fades out cleanly when Rathalos is slain.")]
    public async Task<object> VerifyRathalosBgmLive(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        // Let at least a couple of frames run so MusicManagerPlay initializes its audioSource & transitions
        await ctx.Game.RunForTicksAsync(32, null, cancellationToken).ConfigureAwait(false);

        var preQuest = await ctx.MainThread.InvokeAsync(() =>
        {
            BoostHeadlessTickThroughput();
            bool proofOfAHeroRemoved = DefDatabase<SongDef>.GetNamedSilentFail("GCF_Song_ProofOfAHero") == null;
            var songDef = DefDatabase<SongDef>.GetNamed("GCF_Song_RathalosRoar");
            var seqDef = DefDatabase<MusicSequenceDef>.GetNamed("GCF_Sequence_RathalosHunt");
            var transDef = DefDatabase<MusicTransitionDef>.GetNamed("GCF_Transition_RathalosHunt");

            Find.TickManager.DebugSetTicksGame(GenDate.TicksPerDay * 7 + 10);
            return new
            {
                proofOfAHeroRemoved,
                songDefName = songDef.defName,
                songLabel = songDef.LabelCap.ToString(),
                clipLoaded = songDef.clip != null,
                clipLengthSec = songDef.clip != null ? (float)Math.Round(songDef.clip.length, 2) : 0f,
                clipChannels = songDef.clip != null ? songDef.clip.channels : 0,
                clipFrequency = songDef.clip != null ? songDef.clip.frequency : 0,
                playOnMap = songDef.playOnMap,
                commonality = songDef.commonality,
                seqDefName = seqDef.defName,
                transDefName = transDef.defName,
                musicDisabled = Find.MusicManagerPlay.disabled,
                initialSongBeforeDay7 = Find.MusicManagerPlay.CurrentSong?.defName ?? "None"
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(128, null, cancellationToken).ConfigureAwait(false);

        var offerAndAccept = await ctx.MainThread.InvokeAsync(() =>
        {
            Find.TickManager.Pause();
            var questDef = DefDatabase<QuestScriptDef>.GetNamed("GCF_Quest_RathalosHunt");
            var songDef = DefDatabase<SongDef>.GetNamed("GCF_Song_RathalosRoar");
            var quest = Find.QuestManager.QuestsListForReading.Last(q => q.root == questDef);

            string songWhileNotYetAccepted = Find.MusicManagerPlay.CurrentSong?.defName ?? "None";
            bool playingRoarBeforeAccept = Find.MusicManagerPlay.CurrentSong == songDef;

            // Accept quest while PAUSED — Roar / Rathalos must start immediately on this frame!
            var map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            var colonist = map.mapPawns.FreeColonistsSpawned.First();
            quest.Accept(colonist);

            string songImmediatelyAfterAccept = Find.MusicManagerPlay.CurrentSong?.defName ?? "None";
            string stateAfterAccept = Find.MusicManagerPlay.State.ToString();
            bool isPlayingAfterAccept = Find.MusicManagerPlay.IsPlaying;
            string triggeredTransitionName = Find.MusicManagerPlay.TriggeredTransition?.def?.defName ?? "None";

            return new
            {
                questId = quest.id,
                questName = quest.name,
                songWhileNotYetAccepted,
                playingRoarBeforeAccept,
                songImmediatelyAfterAccept,
                stateAfterAccept,
                isPlayingAfterAccept,
                triggeredTransitionName
            };
        }, cancellationToken).ConfigureAwait(false);

        await ctx.Game.RunForTicksAsync(64, null, cancellationToken).ConfigureAwait(false);

        var killAndFadeout = await ctx.MainThread.InvokeAsync(() =>
        {
            var songDef = DefDatabase<SongDef>.GetNamed("GCF_Song_RathalosRoar");
            string songDuringBattle = Find.MusicManagerPlay.CurrentSong?.defName ?? "None";
            float songTimeDuringBattle = Find.MusicManagerPlay.SongTime;

            var map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            var rathalos = map.mapPawns.AllPawnsSpawned.First(p => p.def.defName == "GCF_Rathalos" && !p.Dead);
            rathalos.Kill(null, null);

            string stateImmediatelyAfterKill = Find.MusicManagerPlay.State.ToString();
            float fadeoutDurationAfterKill = Find.MusicManagerPlay.FadeoutDuration;
            string triggeredTransitionAfterKill = Find.MusicManagerPlay.TriggeredTransition?.def?.defName ?? "None";

            return new
            {
                stillPlayingRoarDuringBattle = Find.MusicManagerPlay.CurrentSong == songDef || songDuringBattle == "GCF_Song_RathalosRoar",
                songDuringBattle,
                songTimeDuringBattle = (float)Math.Round(songTimeDuringBattle, 3),
                stateImmediatelyAfterKill,
                fadeoutDurationAfterKill,
                triggeredTransitionAfterKill
            };
        }, cancellationToken).ConfigureAwait(false);

        bool ok = preQuest.proofOfAHeroRemoved &&
                  preQuest.clipLoaded &&
                  preQuest.clipLengthSec > 160f &&
                  preQuest.clipChannels == 2 &&
                  !preQuest.playOnMap &&
                  preQuest.commonality == 0f &&
                  preQuest.songLabel == "咆哮 / 雄火龙" &&
                  !offerAndAccept.playingRoarBeforeAccept &&
                  offerAndAccept.songImmediatelyAfterAccept == "GCF_Song_RathalosRoar" &&
                  offerAndAccept.stateAfterAccept == "Sequence" &&
                  offerAndAccept.isPlayingAfterAccept &&
                  offerAndAccept.triggeredTransitionName == "GCF_Transition_RathalosHunt" &&
                  killAndFadeout.songDuringBattle == "GCF_Song_RathalosRoar" &&
                  killAndFadeout.stateImmediatelyAfterKill == "Fadeout" &&
                  Math.Abs(killAndFadeout.fadeoutDurationAfterKill - 3f) < 0.01f &&
                  killAndFadeout.triggeredTransitionAfterKill == "None";

        return new
        {
            ok,
            preQuest,
            offerAndAccept,
            killAndFadeout
        };
    }

    private static readonly Dictionary<int, List<string>> RecordedFighterMoves = new Dictionary<int, List<string>>();
    private static readonly List<string> RecordedRathalosClips = new List<string>();
    private static readonly List<string> RecordedBattleEvents = new List<string>();
    private static HashSet<string> LastRathalosMissingParts = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Dictionary<int, bool> LastFighterDowned = new Dictionary<int, bool>();
    private static readonly Dictionary<int, bool> LastFighterDead = new Dictionary<int, bool>();

    [Tool("gcfagent/stage_kiiro_trio_hunt", Description = "Stage 3 ordinary Kiiro_Colonist fighters wielding Dual Blades, Long Sword, and Gunlance against 1 GCF_Rathalos in Giant Arena.")]
    public Task<object> StageKiiroTrioHunt(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Preset arena name (Plains, Colosseum, DesertRuins).")] string preset = "Plains",
        [ToolParameter(Description = "Spawn separation in cells.")] int spawnSeparation = 22,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            RecordedFighterMoves.Clear();
            RecordedRathalosClips.Clear();
            RecordedBattleEvents.Clear();
            LastRathalosMissingParts.Clear();
            LastFighterDowned.Clear();
            LastFighterDead.Clear();

            var cfg = new GiantArenaMatchConfig
            {
                Preset = preset,
                SideAKind = "Kiiro_Colonist",
                SideACount = 3,
                SideALabel = "普通绮罗狩猎小队",
                SideBKind = "GCF_Rathalos",
                SideBCount = 1,
                SideBLabel = "空之王者·雄火龙",
                SpawnSeparation = spawnSeparation,
                ArenaHalfWidth = 42,
                ArenaHalfHeight = 34,
                StartPaused = true,
                LockNoonClearWeather = true,
                CleanVideoMode = true,
                CinematicHudOnly = true,
                EnableVersusHud = true,
                HideDevToolbar = true
            };

            GiantArenaMatchState state = GiantArenaService.SetupMatch(cfg, out string error);
            if (!string.IsNullOrEmpty(error))
            {
                return new { ok = false, error, state };
            }

            Map map = Find.CurrentMap;
            Pawn rathalos = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.def.defName == "GCF_Rathalos" && !p.Dead);
            var kiiros = map.mapPawns.AllPawnsSpawned
                .Where(p => p.RaceProps?.Humanlike == true && !p.Dead)
                .OrderBy(p => p.Position.z)
                .ToList();

            for (int i = 0; i < kiiros.Count; i++)
            {
                Pawn kp = kiiros[i];
                if (kp.story != null && kp.WorkTagIsDisabled(WorkTags.Violent))
                {
                    var validChild = DefDatabase<BackstoryDef>.AllDefsListForReading.FirstOrDefault(b => b.slot == BackstorySlot.Childhood && (b.workDisables & WorkTags.Violent) == 0);
                    var validAdult = DefDatabase<BackstoryDef>.AllDefsListForReading.FirstOrDefault(b => b.slot == BackstorySlot.Adulthood && (b.workDisables & WorkTags.Violent) == 0);
                    if (validChild != null) kp.story.Childhood = validChild;
                    if (validAdult != null) kp.story.Adulthood = validAdult;
                    kp.story.traits?.allTraits?.RemoveAll(t => (t.def.disabledWorkTags & WorkTags.Violent) != 0);
                    kp.Notify_DisabledWorkTypesChanged();
                }
            }

            string[] weaponDefNames =
            {
                "MHWilds_Weapon_DualBlades",
                "MHWilds_Weapon_LongSword",
                "MHWilds_Weapon_Gunlance"
            };

            var roster = new List<object>();
            for (int i = 0; i < kiiros.Count && i < weaponDefNames.Length; i++)
            {
                Pawn fighter = kiiros[i];
                ThingDef wDef = DefDatabase<ThingDef>.GetNamed(weaponDefNames[i]);
                fighter.equipment?.DestroyAllEquipment();
                ThingWithComps weapon = (ThingWithComps)ThingMaker.MakeThing(wDef);
                weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
                fighter.equipment?.AddEquipment(weapon);

                if (fighter.mindState != null)
                {
                    fighter.mindState.canFleeIndividual = false;
                    if (rathalos != null)
                    {
                        fighter.mindState.enemyTarget = rathalos;
                    }
                }
                if (rathalos != null)
                {
                    var job = JobMaker.MakeJob(JobDefOf.AttackMelee, rathalos);
                    job.expiryInterval = 30000;
                    job.checkOverrideOnExpire = false;
                    fighter.jobs?.StartJob(job, JobCondition.InterruptForced);
                }

                RecordedFighterMoves[fighter.thingIDNumber] = new List<string>();
                LastFighterDowned[fighter.thingIDNumber] = false;
                LastFighterDead[fighter.thingIDNumber] = false;

                int meleeLv = fighter.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? -1;
                var apparels = fighter.apparel?.WornApparel?.Select(a => a.LabelCap).ToList() ?? new List<string>();
                roster.Add(new
                {
                    id = fighter.thingIDNumber,
                    name = fighter.Name?.ToStringShort ?? fighter.LabelShort,
                    race = fighter.def.defName,
                    kind = fighter.kindDef?.defName,
                    age = fighter.ageTracker?.AgeBiologicalYears ?? 0,
                    violentDisabled = fighter.WorkTagIsDisabled(WorkTags.Violent),
                    meleeSkill = meleeLv,
                    weaponDef = wDef.defName,
                    weaponLabel = weapon.LabelCap,
                    apparel = apparels,
                    maxHpScale = fighter.HealthScale
                });
            }

            GiantArenaService.FrameMatchCamera(1.0f, 9.0f, 15f, 34f);

            return new
            {
                ok = true,
                matchId = state.MatchId,
                preset = state.Preset,
                kiiroRoster = roster,
                rathalos = rathalos == null ? null : new
                {
                    id = rathalos.thingIDNumber,
                    label = rathalos.LabelCap,
                    bodySize = rathalos.BodySize,
                    healthScale = rathalos.HealthScale
                }
            };
        }, cancellationToken);

    [Tool("gcfagent/stage_kiiro_gs_gl_hunt", Description = "Stage 2 Kiiro_Colonist hunters wielding Greatsword (MHWilds_Weapon_GreatSword) and Gunlance (MHWilds_Weapon_Gunlance) against 1 GCF_Rathalos in Giant Arena.")]
    public Task<object> StageKiiroGreatSwordGunlanceHunt(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Preset arena name (Plains, Colosseum, DesertRuins).")] string preset = "Plains",
        [ToolParameter(Description = "Spawn separation in cells.")] int spawnSeparation = 14,
        [ToolParameter(Description = "Prevent Rathalos from taking off so grounded hit reactions stay in tight close-up.")] bool groundOnly = true,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            RecordedFighterMoves.Clear();
            RecordedRathalosClips.Clear();
            RecordedBattleEvents.Clear();
            LastRathalosMissingParts.Clear();
            LastFighterDowned.Clear();
            LastFighterDead.Clear();

            var cfg = new GiantArenaMatchConfig
            {
                Preset = preset,
                SideAKind = "Kiiro_Colonist",
                SideACount = 2,
                SideALabel = "绮罗大剑·铳枪狩猎组",
                SideBKind = "GCF_Rathalos",
                SideBCount = 1,
                SideBLabel = "空之王者·雄火龙",
                SpawnSeparation = spawnSeparation,
                ArenaHalfWidth = 36,
                ArenaHalfHeight = 28,
                StartPaused = true,
                LockNoonClearWeather = true,
                CleanVideoMode = true,
                CinematicHudOnly = true,
                EnableVersusHud = true,
                HideDevToolbar = true
            };

            GiantArenaMatchState state = GiantArenaService.SetupMatch(cfg, out string error);
            if (!string.IsNullOrEmpty(error))
            {
                return new { ok = false, error, state };
            }

            Map map = Find.CurrentMap;
            Pawn rathalos = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.def.defName == "GCF_Rathalos" && !p.Dead);
            var kiiros = map.mapPawns.AllPawnsSpawned
                .Where(p => p.RaceProps?.Humanlike == true && !p.Dead)
                .OrderBy(p => p.Position.z)
                .ToList();

            string[] weaponDefNames =
            {
                "MHWilds_Weapon_GreatSword",
                "MHWilds_Weapon_Gunlance"
            };

            var roster = new List<object>();
            for (int i = 0; i < kiiros.Count && i < weaponDefNames.Length; i++)
            {
                Pawn fighter = kiiros[i];
                if (fighter.story != null && fighter.WorkTagIsDisabled(WorkTags.Violent))
                {
                    var validChild = DefDatabase<BackstoryDef>.AllDefsListForReading.FirstOrDefault(b => b.slot == BackstorySlot.Childhood && (b.workDisables & WorkTags.Violent) == 0);
                    var validAdult = DefDatabase<BackstoryDef>.AllDefsListForReading.FirstOrDefault(b => b.slot == BackstorySlot.Adulthood && (b.workDisables & WorkTags.Violent) == 0);
                    if (validChild != null) fighter.story.Childhood = validChild;
                    if (validAdult != null) fighter.story.Adulthood = validAdult;
                    fighter.story.traits?.allTraits?.RemoveAll(t => (t.def.disabledWorkTags & WorkTags.Violent) != 0);
                    fighter.Notify_DisabledWorkTypesChanged();
                }

                SkillRecord meleeSkill = fighter.skills?.GetSkill(SkillDefOf.Melee);
                if (meleeSkill != null && meleeSkill.Level < 16)
                {
                    meleeSkill.Level = 16;
                }

                ThingDef wDef = DefDatabase<ThingDef>.GetNamed(weaponDefNames[i]);
                fighter.equipment?.DestroyAllEquipment();
                ThingWithComps weapon = (ThingWithComps)ThingMaker.MakeThing(wDef);
                weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Masterwork, ArtGenerationContext.Colony);
                fighter.equipment?.AddEquipment(weapon);

                if (fighter.mindState != null)
                {
                    fighter.mindState.canFleeIndividual = false;
                    if (rathalos != null)
                    {
                        fighter.mindState.enemyTarget = rathalos;
                    }
                }
                if (rathalos != null)
                {
                    var job = JobMaker.MakeJob(JobDefOf.AttackMelee, rathalos);
                    job.expiryInterval = 30000;
                    job.checkOverrideOnExpire = false;
                    fighter.jobs?.StartJob(job, JobCondition.InterruptForced);
                }

                RecordedFighterMoves[fighter.thingIDNumber] = new List<string>();
                LastFighterDowned[fighter.thingIDNumber] = false;
                LastFighterDead[fighter.thingIDNumber] = false;

                roster.Add(new
                {
                    id = fighter.thingIDNumber,
                    name = fighter.Name?.ToStringShort ?? fighter.LabelShort,
                    race = fighter.def.defName,
                    weaponDef = wDef.defName,
                    weaponLabel = weapon.LabelCap,
                    meleeLevel = fighter.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? -1
                });
            }

            CompGiantRig rigComp = rathalos?.GetComp<CompGiantRig>();
            if (groundOnly && rathalos?.abilities?.abilities != null)
            {
                // Put flight-takeoff abilities on brief cooldown so grounded close-up combat starts immediately.
                foreach (Ability ab in rathalos.abilities.abilities)
                {
                    if (ab?.def?.defName != null && ab.def.defName.IndexOf("Takeoff", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ab.StartCooldown(1200);
                    }
                }
            }

            return new
            {
                ok = true,
                matchId = state.MatchId,
                preset = state.Preset,
                kiiroRoster = roster,
                rathalos = rathalos == null ? null : new
                {
                    id = rathalos.thingIDNumber,
                    label = rathalos.LabelCap,
                    hitReactionEligible = rigComp?.IsAnimalHitReactionEligible ?? false,
                    hasHitReactionRuntime = rigComp?.HitReactionRuntime != null
                }
            };
        }, cancellationToken);

    [Tool("gcfagent/get_rathalos_hit_reaction", Description = "Inspect live CompGiantRig.HitReactionRuntime state and missing parts on the spawned GCF_Rathalos.")]
    public Task<object> GetRathalosHitReactionState(
        IRimBridgeContext ctx,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            Map map = Find.CurrentMap;
            Pawn rathalos = map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(p => p.def.defName == "GCF_Rathalos");
            CompGiantRig rigComp = rathalos?.GetComp<CompGiantRig>();
            GiantHitReactionRuntime hr = rigComp?.HitReactionRuntime;
            if (rathalos == null || rigComp == null)
            {
                return new { ok = false, error = "No GCF_Rathalos found on current map." };
            }
            return new
            {
                ok = true,
                id = rathalos.thingIDNumber,
                hpPct = rathalos.health?.summaryHealth?.SummaryHealthPercent ?? 0f,
                dead = rathalos.Dead,
                downed = rathalos.Downed,
                flying = rigComp.IsCurrentlyAirborne,
                missingParts = rigComp.CountSeveredBones(),
                eligible = rigComp.IsAnimalHitReactionEligible,
                active = hr?.IsActive ?? false,
                lastTier = hr?.LastTriggeredTier.ToString() ?? "None",
                rootOffsetX = hr?.CurrentRootOffsetLocal.x ?? 0f,
                rootOffsetY = hr?.CurrentRootOffsetLocal.y ?? 0f,
                rootAngleDeg = hr?.CurrentRootAngleDeg ?? 0f,
                poiseRatio = hr?.CurrentPoiseRatio ?? 0f,
                rapidHitHeat = hr?.CurrentRapidHitHeat ?? 0f
            };
        }, cancellationToken);

    [Tool("gcfagent/probe_kiiro_hunt_tick", Description = "Step the active Kiiro Trio vs Rathalos match for a given number of ticks while recording every MH weapon combo move, Rathalos ability/flight clip, part break, and casualty.")]
    public async Task<object> ProbeKiiroHuntTick(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Total simulation ticks to advance (in 12-tick telemetry slices).")] int totalTicks = 360,
        CancellationToken cancellationToken = default)
    {
        int remaining = Math.Max(12, totalTicks);
        while (remaining > 0)
        {
            int step = Math.Min(12, remaining);
            remaining -= step;
            await ctx.Game.RunForTicksAsync(step, null, cancellationToken).ConfigureAwait(false);

            bool finished = await ctx.MainThread.InvokeAsync(() =>
            {
                Map map = Find.CurrentMap;
                if (map == null) return true;

                GiantArenaMatchState st = GiantArenaService.GetMatchState();
                int elapsed = st.ElapsedTicks;
                float sec = (float)Math.Round(elapsed / 60f, 1);

                // Sample Kiiro moves & casualties
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p?.RaceProps?.Humanlike != true) continue;
                    if (!RecordedFighterMoves.TryGetValue(p.thingIDNumber, out var moveList))
                    {
                        moveList = new List<string>();
                        RecordedFighterMoves[p.thingIDNumber] = moveList;
                    }

                    var mhComp = p.equipment?.Primary?.AllComps?.FirstOrDefault(c => c.GetType().Name == "CompMHWeapon");
                    if (mhComp != null)
                    {
                        string activeMove = mhComp.GetType().GetField("ActiveMoveDefName")?.GetValue(mhComp) as string;
                        if (!string.IsNullOrEmpty(activeMove) && (moveList.Count == 0 || moveList[moveList.Count - 1] != activeMove))
                        {
                            moveList.Add(activeMove);
                        }
                    }

                    bool wasDown = LastFighterDowned.TryGetValue(p.thingIDNumber, out bool d) && d;
                    bool wasDead = LastFighterDead.TryGetValue(p.thingIDNumber, out bool k) && k;
                    if (p.Dead && !wasDead)
                    {
                        LastFighterDead[p.thingIDNumber] = true;
                        RecordedBattleEvents.Add($"[{sec:F1}s] {p.LabelShort} 阵亡！");
                    }
                    else if (p.Downed && !wasDown)
                    {
                        LastFighterDowned[p.thingIDNumber] = true;
                        string topHediffs = string.Join(",", p.health.hediffSet.hediffs.TakeLast(3).Select(h => h.LabelCap));
                        RecordedBattleEvents.Add($"[{sec:F1}s] {p.LabelShort} 重伤倒地 ({topHediffs})");
                    }
                }

                // also check corpses if a Kiiro died and despawned into a Corpse!
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                {
                    if (t is Corpse corpse && corpse.InnerPawn != null && LastFighterDead.TryGetValue(corpse.InnerPawn.thingIDNumber, out bool alreadyDead) && !alreadyDead)
                    {
                        LastFighterDead[corpse.InnerPawn.thingIDNumber] = true;
                        RecordedBattleEvents.Add($"[{sec:F1}s] {corpse.InnerPawn.LabelShort} 阵亡！");
                    }
                }

                // Sample Rathalos clips & part breaks
                var rathSnap = st.Fighters.FirstOrDefault(f => f.KindDefName == "GCF_Rathalos");
                if (rathSnap != null)
                {
                    if (!string.IsNullOrEmpty(rathSnap.ActiveClip) &&
                        (RecordedRathalosClips.Count == 0 || RecordedRathalosClips[RecordedRathalosClips.Count - 1] != rathSnap.ActiveClip))
                    {
                        RecordedRathalosClips.Add($"{sec:F1}s:{rathSnap.ActiveClip}{(rathSnap.IsFlying ? "(Air)" : "")}");
                    }
                    foreach (string mp in rathSnap.MissingParts)
                    {
                        if (LastRathalosMissingParts.Add(mp))
                        {
                            RecordedBattleEvents.Add($"[{sec:F1}s] 雄火龙部位破坏/断肢：{mp}");
                        }
                    }
                }

                GiantArenaService.FrameMatchCamera(0.35f, 8.5f, 15f, 34f);
                return st.Status == "Finished" || st.Status == "Draw";
            }, cancellationToken).ConfigureAwait(false);

            if (finished)
            {
                break;
            }
        }

        return await ctx.MainThread.InvokeAsync<object>(() =>
        {
            Map map = Find.CurrentMap;
            GiantArenaMatchState st = GiantArenaService.GetMatchState();
            var kiiroDetails = new List<object>();

            foreach (var f in st.Fighters.Where(x => x.KindDefName != "GCF_Rathalos"))
            {
                Pawn livePawn = map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(p => p.thingIDNumber == f.PawnThingId);
                if (livePawn == null)
                {
                    livePawn = map?.listerThings?.ThingsInGroup(ThingRequestGroup.Corpse)
                        ?.OfType<Corpse>()
                        ?.FirstOrDefault(c => c.InnerPawn?.thingIDNumber == f.PawnThingId)?.InnerPawn;
                }

                string mhSummary = "";
                var mhComp = livePawn?.equipment?.Primary?.AllComps?.FirstOrDefault(c => c.GetType().Name == "CompMHWeapon");
                if (mhComp != null)
                {
                    var ct = mhComp.GetType();
                    string kind = ct.GetProperty("Kind")?.GetValue(mhComp)?.ToString() ?? "";
                    if (kind == "DualBlades")
                    {
                        mhSummary = $"demon={ct.GetField("InDemonMode")?.GetValue(mhComp)},arch={ct.GetField("ArchdemonMode")?.GetValue(mhComp)},gauge={ct.GetField("DemonGauge")?.GetValue(mhComp):F0}%";
                    }
                    else if (kind == "LongSword")
                    {
                        mhSummary = $"spiritLv={ct.GetField("SpiritLevel")?.GetValue(mhComp)},gauge={ct.GetField("SpiritGauge")?.GetValue(mhComp):F0}%";
                    }
                    else if (kind == "Gunlance")
                    {
                        mhSummary = $"shells={ct.GetField("Shells")?.GetValue(mhComp)},stakes={ct.GetField("Wyrmstakes")?.GetValue(mhComp)},wf={ct.GetField("WyvernFireCharges")?.GetValue(mhComp):F1}";
                    }
                }

                var hediffs = livePawn?.health?.hediffSet?.hediffs
                    ?.Select(h => $"{h.Part?.Label ?? "全身"}:{h.LabelCap}")
                    .Take(10)
                    .ToList() ?? new List<string>();

                RecordedFighterMoves.TryGetValue(f.PawnThingId, out var moves);
                kiiroDetails.Add(new
                {
                    f.PawnThingId,
                    f.Label,
                    f.Dead,
                    f.Downed,
                    hpPct = Math.Round(f.HealthPercent * 100f, 1),
                    weapon = livePawn?.equipment?.Primary?.LabelCap ?? "Dropped/None",
                    mhState = mhSummary,
                    distinctMoveCount = moves?.Distinct().Count() ?? 0,
                    totalMovesCast = moves?.Count ?? 0,
                    moveSequence = moves ?? new List<string>(),
                    injuries = hediffs
                });
            }

            Pawn rathLive = map?.mapPawns?.AllPawnsSpawned?.FirstOrDefault(p => p.def.defName == "GCF_Rathalos")
                ?? map?.listerThings?.ThingsInGroup(ThingRequestGroup.Corpse)?.OfType<Corpse>()?.FirstOrDefault(c => c.InnerPawn?.def?.defName == "GCF_Rathalos")?.InnerPawn;
            var rathHediffs = rathLive?.health?.hediffSet?.hediffs
                ?.GroupBy(h => h.def.label)
                .Select(g => $"{g.Key}x{g.Count()}(totalDmg={g.Sum(x => x.Severity):F1})")
                .ToList() ?? new List<string>();

            var rathSnap = st.Fighters.FirstOrDefault(x => x.KindDefName == "GCF_Rathalos");

            return new
            {
                matchId = st.MatchId,
                status = st.Status,
                winnerSide = st.WinnerSide,
                elapsedTicks = st.ElapsedTicks,
                elapsedSeconds = st.ElapsedSeconds,
                sideAAlive = st.SideAAlive,
                sideBAlive = st.SideBAlive,
                kiiros = kiiroDetails,
                rathalos = rathSnap == null ? null : new
                {
                    rathSnap.Label,
                    rathSnap.Dead,
                    rathSnap.Downed,
                    hpPct = Math.Round(rathSnap.HealthPercent * 100f, 1),
                    rathSnap.IsFlying,
                    rathSnap.ActiveClip,
                    rathSnap.MissingPartCount,
                    rathSnap.MissingParts,
                    woundBreakdown = rathHediffs,
                    clipTimeline = RecordedRathalosClips
                },
                battleEvents = RecordedBattleEvents
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    [Tool("gcfagent/inspect_giant_blood_live", Description = "Inspect live GCF wound & bloodstain overlay materials, active per-bone decal specs, ground Filth_Blood splatters, MechScorpion non-organic scar isolation, and optionally frame a close-up camera shot.")]
    public Task<object> InspectGiantBloodLive(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Center and zoom camera onto the primary giant for close-up visual inspection.")] bool zoomToGiant = true,
        [ToolParameter(Description = "Camera orthographic rootSize when zoomToGiant is true.")] float cameraRootSize = 11.5f,
        [ToolParameter(Description = "Also spawn/test a temporary GCF_MechScorpion at map edge to verify non-organic battle scars & zero red blood.")] bool verifyMechIsolation = false,
        [ToolParameter(Description = "Arm humanlike arena fighters with GCF_MeleeWeapon_DragonScaleSword and Melee 16.")] bool armHumanSlashers = false,
        [ToolParameter(Description = "Optional number of live Cut/Bomb hits to apply directly to the primary giant.")] int applyExtraHits = 0,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            Map map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            if (map == null)
            {
                return new { ok = false, error = "No active map" };
            }

            if (armHumanSlashers)
            {
                ThingDef swordDef = DefDatabase<ThingDef>.GetNamedSilentFail("GCF_MeleeWeapon_DragonScaleSword")
                    ?? DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_MonoSword");
                if (swordDef != null)
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    {
                        if (p?.RaceProps?.Humanlike == true && !p.Dead)
                        {
                            p.equipment?.DestroyAllEquipment();
                            ThingWithComps w = (ThingWithComps)ThingMaker.MakeThing(swordDef);
                            p.equipment?.AddEquipment(w);
                            if (p.skills != null)
                            {
                                p.skills.GetSkill(SkillDefOf.Melee).Level = 16;
                            }
                        }
                    }
                }
            }

            Material matSlash = GiantWoundBloodSystem.ResolveDecalMaterialOnMainThread(GiantWoundVisualKind.OrganicBloodSlash, 0);
            Material matSoak = GiantWoundBloodSystem.ResolveDecalMaterialOnMainThread(GiantWoundVisualKind.OrganicBloodSoak, 0);
            Material matScar = GiantWoundBloodSystem.ResolveDecalMaterialOnMainThread(GiantWoundVisualKind.MechanoidBattleScar, 0);
            Material matBandage = GiantWoundBloodSystem.ResolveDecalMaterialOnMainThread(GiantWoundVisualKind.OrganicBandaged, 0);

            Pawn giant = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.def.defName == "GCF_Rathalos")
                ?? map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Select(c => c.InnerPawn).FirstOrDefault(p => p?.def?.defName == "GCF_Rathalos")
                ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => GiantFastLookup.TryGetRig(p) != null);

            if (giant != null && !giant.Dead && applyExtraHits > 0 && GiantFastLookup.TryGetRig(giant) is CompGiantRig hitComp)
            {
                BoneNodeDef[] hNodes = hitComp.RigDef.SortedNodes;
                for (int k = 0; k < applyExtraHits; k++)
                {
                    int bIdx = (k * 3) % hNodes.Length;
                    BodyPartRecord hitPart = hitComp.GetBodyPartForBone(bIdx);
                    DamageDef dDef = (k == applyExtraHits - 1) ? DamageDefOf.Bomb : DamageDefOf.Cut;
                    giant.TakeDamage(new DamageInfo(dDef, 26f, 1.8f, -1f, null, hitPart));
                }
            }

            object giantReport = null;
            if (giant != null && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp)
            {
                var specs = comp.GetOrRefreshWoundDecalSpecs(forceRefresh: true);
                BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
                var byKind = specs.GroupBy(s => s.kind.ToString()).ToDictionary(g => g.Key, g => g.Count());
                var sampleBones = specs
                    .Select(s => $"{nodes[s.boneIndex].nodeId}:{s.kind}(sz={s.baseWorldSize.x:F2})")
                    .Take(16)
                    .ToList();

                if (zoomToGiant && Find.CameraDriver != null)
                {
                    Vector3 focusPos = giant.Spawned ? giant.DrawPos : (giant.Corpse != null ? giant.Corpse.DrawPos : giant.PositionHeld.ToVector3Shifted());
                    Find.CameraDriver.SetRootPosAndSize(focusPos, cameraRootSize);
                }

                giantReport = new
                {
                    defName = giant.def.defName,
                    dead = giant.Dead,
                    downed = giant.Downed,
                    airborne = comp.IsCurrentlyAirborne,
                    hediffInjuryCount = giant.health?.hediffSet?.hediffs?.OfType<Hediff_Injury>().Count() ?? 0,
                    missingPartCount = giant.health?.hediffSet?.hediffs?.OfType<Hediff_MissingPart>().Count() ?? 0,
                    bleedRateTotal = (float)Math.Round(giant.health?.hediffSet?.BleedRateTotal ?? 0f, 3),
                    decalCount = specs.Count,
                    decalsByKind = byKind,
                    sampleBoneDecals = sampleBones
                };
            }

            int mapBloodFilthThings = map.listerThings.ThingsOfDef(ThingDefOf.Filth_Blood)?.Count ?? 0;

            object mechReport = null;
            if (verifyMechIsolation)
            {
                PawnKindDef scorpKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("GCF_MechScorpion");
                if (scorpKind != null)
                {
                    IntVec3 testCell = new IntVec3(Mathf.Clamp(map.Center.x - 28, 8, map.Size.x - 8), 0, Mathf.Clamp(map.Center.z + 20, 8, map.Size.z - 8));
                    int bloodBefore = GiantBloodFilthUtility.TotalSpawnedGiantBloodFilthCount;
                    Pawn scorp = PawnGenerator.GeneratePawn(scorpKind, Faction.OfMechanoids);
                    GenSpawn.Spawn(scorp, testCell, map);
                    scorp.TakeDamage(new DamageInfo(DamageDefOf.Cut, 35f, 1.5f));
                    scorp.TakeDamage(new DamageInfo(DamageDefOf.Bomb, 40f, 1.5f));
                    CompGiantRig scorpComp = GiantFastLookup.TryGetRig(scorp);
                    var mSpecs = scorpComp?.GetOrRefreshWoundDecalSpecs(forceRefresh: true);
                    int bloodDelta = GiantBloodFilthUtility.TotalSpawnedGiantBloodFilthCount - bloodBefore;
                    mechReport = new
                    {
                        decalCount = mSpecs?.Count ?? 0,
                        allMechanoidScars = mSpecs != null && mSpecs.Count > 0 && mSpecs.All(s => s.kind == GiantWoundVisualKind.MechanoidBattleScar),
                        spawnedBloodFilthDelta = bloodDelta
                    };
                    if (!scorp.Destroyed)
                    {
                        scorp.Destroy(DestroyMode.Vanish);
                    }
                }
            }

            return new
            {
                ok = matSlash?.mainTexture != null && matSoak?.mainTexture != null && matScar?.mainTexture != null && matBandage?.mainTexture != null,
                loadedTextures = new
                {
                    slash = matSlash?.mainTexture?.name,
                    soak = matSoak?.mainTexture?.name,
                    scar = matScar?.mainTexture?.name,
                    bandage = matBandage?.mainTexture?.name
                },
                mapBloodFilthThings,
                totalGiantBloodFilthSpawned = GiantBloodFilthUtility.TotalSpawnedGiantBloodFilthCount,
                giantReport,
                mechReport
            };
        }, cancellationToken);
}
