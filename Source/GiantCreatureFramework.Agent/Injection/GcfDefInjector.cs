using System.Reflection;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// Adds or replaces Defs under the agent content root (or a single isolated creature workspace)
/// in the running game. When live/dead pawns already reference a replaced BodyDef:
/// - If total BodyPartRecord count is unchanged, remaps existing Pawn Hediff part references by new index;
/// - If total BodyPartRecord count changed, aborts and returns Authoring_BodyPartCountChanged.
/// </summary>
public static class GcfDefInjector
{
    private sealed record HediffPartSnapshot(Pawn Pawn, Hediff Hediff, int PartIndex, BodyPartRecord OriginalPart);

    private sealed class Batch
    {
        public readonly List<(Def Def, string Source)> Parsed = new();
        public readonly List<(Def Def, string Source)> Pending = new();
        public readonly List<Def> Added = new();
        public readonly List<(Def Fresh, Def Existing, string Source)> Replacements = new();
        public readonly List<(Def Existing, Def Backup)> Backups = new();
        public readonly List<ThingDef> Corpses = new();
        public readonly Dictionary<string, string> FileHashes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<BodyDef, List<HediffPartSnapshot>> BodyHediffSnapshots = new(ReferenceEqualityComparer.Instance);
        public IEnumerable<Def> Live => Added.Concat(Backups.Select(b => b.Existing));
    }

    private static readonly MethodInfo? GenerateCorpse = AccessTools.Method(typeof(ThingDefGenerator_Corpses), "GenerateCorpseDef");
    private static readonly MethodInfo? GenerateUnnaturalCorpse = AccessTools.Method(typeof(ThingDefGenerator_Corpses), "GenerateUnnaturalCorpseDef");
    private static readonly FieldInfo? TakenHashes = AccessTools.Field(typeof(ShortHashGiver), "takenHashesPerDeftype");
    private static readonly FieldInfo? PackDefs = AccessTools.Field(typeof(ModContentPack), "defs");
    private static readonly FieldInfo? HediffPartField = AccessTools.Field(typeof(Hediff), "part");

    private static readonly HashSet<Def> Injected = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> UncommittedTextures = new(StringComparer.Ordinal);
    private static Dictionary<string, string>? _committedHashes;
    private static ParentNodeLocator? _parents;

    public static InjectionReport? LastReport { get; private set; }
    public static bool Running { get; private set; }

    public static bool IsInjectedAtRuntime(Def def) => Injected.Contains(def);
    public static bool IsAgentInjected(Def def) => Injected.Contains(def) || AgentContentRoot.WasLoadedAtStartup(def);

    /// <summary>Warms rigs and masks of custom creatures that were loaded from disk during game startup.</summary>
    public static void WarmStartupCreatures()
    {
        var owner = AgentContentRoot.Owner;
        if (owner == null || !UnityData.IsInMainThread)
        {
            return;
        }

        AgentContentRoot.EnsureLoadFolder();
        var startupReport = new InjectionReport { DryRun = false };
        GcfRuntimeTextures.Sync(owner, AgentContentRoot.AllTextureRoots(), startupReport);

        foreach (var rig in DefDatabase<GiantRigDef>.AllDefsListForReading)
        {
            if (rig != null && AgentContentRoot.WasLoadedAtStartup(rig))
            {
                rig.BuildTopology();
                Warm(rig);
            }
        }
        GiantFastLookup.BuildDefShortHashTable();
    }

    /// <summary>Fire-and-forget form for callers that cannot await; the outcome arrives in LastReport.</summary>
    public static void StartInjection(bool dryRun, string? creatureName = null) => _ = InjectAsync(dryRun, creatureName);

    public static Task<InjectionReport> InjectAsync(bool dryRun = false, string? creatureName = null)
    {
        var report = new InjectionReport { DryRun = dryRun };
        var owner = AgentContentRoot.Owner;
        if (!UnityData.IsInMainThread || owner == null || Running)
        {
            report.Error(!UnityData.IsInMainThread ? "not_main_thread" : owner == null ? "not_registered" : "injection_running");
            report.RolledBack = true;
            return Task.FromResult(report);
        }

        if (!string.IsNullOrWhiteSpace(creatureName) && !AgentContentRoot.IsValidCreatureName(creatureName))
        {
            report.Error("invalid_creature_name", creatureName, AgentProtocol.Get("Authoring_InvalidCreatureName"));
            report.RolledBack = true;
            return Task.FromResult(report);
        }

        Running = true;
        var completion = new TaskCompletionSource<InjectionReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete()
        {
            Running = false;
            LastReport = report;
            completion.TrySetResult(report);
        }

        LongEventHandler.QueueLongEvent(() =>
        {
            Batch? batch = null;
            try
            {
                batch = Run(owner, dryRun, creatureName?.Trim(), report);
            }
            catch (Exception ex)
            {
                report.Error("exception", "", ex.ToString());
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    if (batch != null && report.Committed)
                    {
                        Finish(batch, report);
                    }
                }
                catch (Exception ex)
                {
                    report.Error("exception", "", ex.ToString());
                }
                Complete();
            });
        }, "LoadingDefs", doAsynchronously: false, exceptionHandler: ex =>
        {
            report.Error("exception", "", ex.ToString());
            Complete();
        });
        return completion.Task;
    }

    private static Batch? Run(ModContentPack owner, bool dryRun, string? creatureName, InjectionReport report)
    {
        if (DirectXmlCrossRefLoader.LoadingInProgress)
        {
            report.Error("loader_busy");
            report.RolledBack = true;
            return null;
        }

        AgentContentRoot.EnsureLoadFolder(creatureName);
        if (dryRun)
        {
            var validatedTextures = GcfRuntimeTextures.ValidateOnly(owner, AgentContentRoot.AllTextureRoots(creatureName), report);
            report.Textures.AddRange(validatedTextures);
        }
        else
        {
            var newlySynced = GcfRuntimeTextures.Sync(owner, AgentContentRoot.AllTextureRoots(creatureName), report);
            foreach (var tex in newlySynced)
            {
                UncommittedTextures.Add(tex);
            }
            report.Textures.AddRange(UncommittedTextures.OrderBy(t => t, StringComparer.Ordinal));
        }

        var batch = new Batch();
        using (var capture = ScopedLogCapture.Begin())
        {
            bool Clean() => report.Errors.Count == 0 && capture.Errors.Count == 0;
            try
            {
                Parse(owner, creatureName, dryRun, batch, report);
                if (Clean()) Classify(batch, report);
                if (Clean())
                {
                    foreach (var (def, source) in batch.Pending)
                    {
                        AddToDatabase(def);
                        owner.AddDef(def, source);
                        batch.Added.Add(def);
                    }
                    ResolveCrossReferences();
                }
                if (Clean()) ApplyReplacements(batch);
                if (Clean())
                {
                    PrepareRaces(batch, report);
                    ResolveCrossReferences();
                }
                if (Clean()) ResolveReferences(batch, report);
                if (Clean()) RemapExistingPawnBodyParts(batch, report);
                if (Clean()) CollectConfigErrors(batch, report);
            }
            catch (Exception ex)
            {
                report.Error("exception", "", ex.ToString());
            }
            finally
            {
                XmlInheritance.Clear();
                DirectXmlCrossRefLoader.Clear();
            }
            foreach (var text in capture.Errors) report.Error("log_error", "", text);
            foreach (var text in capture.Warnings) report.Warning("log_warning", "", text);
        }

        if (report.Errors.Count > 0 || dryRun)
        {
            try
            {
                Rollback(batch);
            }
            catch (Exception ex)
            {
                report.Error("rollback_failed", "", ex.ToString());
            }
            report.RolledBack = true;
            return batch;
        }

        AssignShortHashes(batch);
        UncommittedTextures.Clear();
        foreach (var def in batch.Added)
        {
            Injected.Add(def);
        }
        GiantFastLookup.BuildDefShortHashTable();
        var committed = CommittedHashes();
        foreach (var pair in batch.FileHashes)
        {
            committed[pair.Key] = pair.Value;
        }
        return batch;
    }

    private static Dictionary<string, string> CommittedHashes() =>
        _committedHashes ??= new Dictionary<string, string>(AgentContentRoot.StartupFileHashes, StringComparer.OrdinalIgnoreCase);

    private static void Parse(ModContentPack owner, string? creatureName, bool dryRun, Batch batch, InjectionReport report)
    {
        var committed = CommittedHashes();
        var nodes = new List<(XmlNode Node, LoadableXmlAsset Asset, bool Changed)>();
        var forceReparseCreature = dryRun && !string.IsNullOrWhiteSpace(creatureName);

        foreach (var file in AgentContentRoot.DefFiles(creatureName))
        {
            var asset = new LoadableXmlAsset(new FileInfo(file), owner);
            var root = asset.xmlDoc?.DocumentElement;
            if (root == null)
            {
                report.Error("xml_unreadable", file);
                continue;
            }
            var hash = AgentContentRoot.HashOf(file);
            var changed = forceReparseCreature || !committed.TryGetValue(file, out var known) || known != hash;
            if (changed)
            {
                batch.FileHashes[file] = hash;
            }
            foreach (XmlNode node in root.ChildNodes)
            {
                if (node.NodeType == XmlNodeType.Element)
                {
                    nodes.Add((node, asset, changed));
                }
            }
        }
        if (!nodes.Any(n => n.Changed))
        {
            return;
        }

        var ownNames = nodes.Select(n => n.Node.Attributes?["Name"]?.Value).Where(n => n != null).ToHashSet(StringComparer.Ordinal);
        var parents = new Dictionary<string, XmlNode>(StringComparer.Ordinal);
        var wanted = new Queue<string>(nodes.Select(n => n.Node.Attributes?["ParentName"]?.Value).Where(n => n != null && !ownNames.Contains(n))!);
        _parents ??= new ParentNodeLocator(PreferredParentPacks(owner), AgentContentRoot.RootPath);
        while (wanted.Count > 0)
        {
            var name = wanted.Dequeue();
            if (parents.ContainsKey(name))
            {
                continue;
            }
            var parent = _parents.Find(name);
            if (parent == null)
            {
                report.Error("parent_not_found", name);
                continue;
            }
            parents[name] = parent;
            var grandparent = parent.Attributes?["ParentName"]?.Value;
            if (grandparent != null && !ownNames.Contains(grandparent))
            {
                wanted.Enqueue(grandparent);
            }
        }
        if (report.Errors.Count > 0)
        {
            return;
        }

        XmlInheritance.Clear();
        foreach (var parent in parents.Values)
        {
            XmlInheritance.TryRegister(parent, null);
        }
        foreach (var (node, _, _) in nodes)
        {
            XmlInheritance.TryRegister(node, owner);
        }
        XmlInheritance.Resolve();

        var legacy = GenCommandLine.CommandLineArgPassed("legacy-xml-deserializer");
        foreach (var (node, asset, changed) in nodes)
        {
            if (!changed || IsAbstract(node) || !RequirementsMet(node))
            {
                continue;
            }
            var def = legacy ? DirectXmlLoader.DefFromNode(node, asset) : DirectXmlToObjectNew.DefFromNodeNew(node, asset);
            if (def == null)
            {
                report.Error("def_parse_failed", asset.name + ":" + node.Name + ":" + (node["defName"]?.InnerText ?? ""));
                continue;
            }
            batch.Parsed.Add((def, asset.name));
        }
    }

    private static IReadOnlyCollection<ModContentPack> PreferredParentPacks(ModContentPack owner)
    {
        var packs = new List<ModContentPack> { owner };
        var framework = LoadedModManager.RunningModsListForReading
            .FirstOrDefault(m => m.assemblies.loadedAssemblies.Contains(typeof(GiantRigDef).Assembly));
        if (framework != null && framework != owner)
        {
            packs.Add(framework);
        }
        return packs;
    }

    private static bool IsAbstract(XmlNode node) =>
        string.Equals(node.Attributes?["Abstract"]?.Value, "true", StringComparison.InvariantCultureIgnoreCase);

    private static bool RequirementsMet(XmlNode node)
    {
        var all = node.Attributes?["MayRequire"]?.Value.ToLower();
        if (all != null && !ModLister.AllModsActiveNoSuffix(all.Split(',')))
        {
            return false;
        }
        var any = node.Attributes?["MayRequireAnyOf"]?.Value.ToLower().Split(',');
        return any.NullOrEmpty() || ModLister.AnyModActiveNoSuffix(any);
    }

    private static List<BodyPartRecord> FlattenBodyParts(BodyDef body)
    {
        if (body.AllParts is { Count: > 0 } all)
        {
            return all;
        }

        var list = new List<BodyPartRecord>();
        void Walk(BodyPartRecord? part)
        {
            if (part == null) return;
            list.Add(part);
            if (part.parts == null) return;
            for (var i = 0; i < part.parts.Count; i++)
            {
                Walk(part.parts[i]);
            }
        }
        Walk(body.corePart);
        return list;
    }

    private static void Classify(Batch batch, InjectionReport report)
    {
        var seen = new HashSet<(Type, string)>();
        foreach (var (def, source) in batch.Parsed)
        {
            var label = Describe(def);
            if (string.IsNullOrWhiteSpace(def.defName) || def.defName == "UnnamedDef")
            {
                report.Error("def_unnamed", source + ":" + def.GetType().Name);
                continue;
            }
            var databaseType = DatabaseTypeOf(def.GetType());
            if (!seen.Add((databaseType, def.defName)))
            {
                report.Error("def_duplicate", label);
                continue;
            }
            var existing = GenDefDatabase.GetDefSilentFail(databaseType, def.defName, specialCaseForSoundDefs: false);
            if (existing == null)
            {
                batch.Pending.Add((def, source));
                report.Added.Add(label);
            }
            else if (existing.GetType() == def.GetType() && (Injected.Contains(existing) || AgentContentRoot.WasLoadedAtStartup(existing)))
            {
                batch.Replacements.Add((def, existing, source));
                report.Replaced.Add(label);
            }
            else
            {
                report.Error("def_collision", label, existing.modContentPack?.PackageIdPlayerFacing ?? "");
            }
        }

        // Check existing live/dead pawns using any replaced BodyDef:
        // If total part count is unchanged, snapshot Hediff part indices for remapping after ResolveReferences;
        // If total part count changed, fail with Authoring_BodyPartCountChanged.
        var bodyReplacements = batch.Replacements
            .Where(r => r.Existing is BodyDef && r.Fresh is BodyDef)
            .Select(r => (Fresh: (BodyDef)r.Fresh, Existing: (BodyDef)r.Existing))
            .ToList();

        if (bodyReplacements.Count > 0 && Current.ProgramState == ProgramState.Playing)
        {
            var allPawns = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead;
            foreach (var (freshBody, existingBody) in bodyReplacements)
            {
                var usingPawns = allPawns.Where(p => ReferenceEquals(p?.RaceProps?.body, existingBody)).ToList();
                if (usingPawns.Count == 0)
                {
                    continue;
                }

                var oldParts = FlattenBodyParts(existingBody);
                var newParts = FlattenBodyParts(freshBody);
                if (!BodyPartIndexRemapper.TryBuildIndexRemap(oldParts, newParts, existingBody.defName, out _, out var errMsg))
                {
                    report.Error("body_part_count_changed", Describe(existingBody), errMsg ?? "");
                    continue;
                }

                var snapshots = new List<HediffPartSnapshot>();
                foreach (var pawn in usingPawns)
                {
                    var hediffs = pawn.health?.hediffSet?.hediffs;
                    if (hediffs == null)
                    {
                        continue;
                    }
                    foreach (var hediff in hediffs)
                    {
                        if (hediff?.Part == null)
                        {
                            continue;
                        }
                        var idx = oldParts.IndexOf(hediff.Part);
                        if (idx < 0 && hediff.Part.Index >= 0 && hediff.Part.Index < oldParts.Count)
                        {
                            idx = hediff.Part.Index;
                        }
                        if (idx >= 0 && idx < newParts.Count)
                        {
                            snapshots.Add(new HediffPartSnapshot(pawn, hediff, idx, hediff.Part));
                        }
                    }
                }
                batch.BodyHediffSnapshots[existingBody] = snapshots;
            }
        }
    }

    private static void ApplyReplacements(Batch batch)
    {
        foreach (var (fresh, existing, source) in batch.Replacements)
        {
            var backup = (Def)Activator.CreateInstance(existing.GetType())!;
            Gen.MemberwiseShallowCopy(existing, backup);
            batch.Backups.Add((existing, backup));

            Gen.MemberwiseShallowCopy(fresh, existing);
            existing.shortHash = backup.shortHash;
            existing.index = backup.index;
            existing.debugRandomId = backup.debugRandomId;
            existing.modContentPack = backup.modContentPack;
            existing.fileName = source;
            existing.ClearCachedData();
        }
    }

    private static void RemapExistingPawnBodyParts(Batch batch, InjectionReport report)
    {
        foreach (var (bodyDef, snapshots) in batch.BodyHediffSnapshots)
        {
            var newParts = FlattenBodyParts(bodyDef);
            var touchedPawns = new HashSet<Pawn>(ReferenceEqualityComparer.Instance);
            foreach (var snap in snapshots)
            {
                if ((uint)snap.PartIndex >= (uint)newParts.Count)
                {
                    report.Error(
                        "body_part_count_changed",
                        Describe(bodyDef),
                        AgentProtocol.Format("Authoring_BodyPartCountChanged", bodyDef.defName, snap.PartIndex + 1, newParts.Count));
                    return;
                }

                SetHediffPart(snap.Hediff, newParts[snap.PartIndex]);
                touchedPawns.Add(snap.Pawn);
            }

            foreach (var pawn in touchedPawns)
            {
                pawn.health?.hediffSet?.DirtyCache();
            }
        }
    }

    private static void SetHediffPart(Hediff hediff, BodyPartRecord newPart)
    {
        if (HediffPartField != null)
        {
            HediffPartField.SetValue(hediff, newPart);
        }
        else
        {
            hediff.Part = newPart;
        }
    }

    private static void PrepareRaces(Batch batch, InjectionReport report)
    {
        foreach (var def in batch.Live.OfType<ThingDef>().Where(d => d.category == ThingCategory.Pawn && d.race != null).ToList())
        {
            var race = def.race;
            if (race.hasMeat && race.hasCorpse && race.useMeatFrom == null && race.specificMeatDef == null)
            {
                if (race.IsFlesh)
                {
                    report.Error("meat_source_required", Describe(def));
                    continue;
                }
                race.meatDef = ThingDefOf.Steel;
            }

            if (!race.hasCorpse)
            {
                continue;
            }
            if (race.linkedCorpseKind != null && race.linkedCorpseKind != def)
            {
                race.corpseDef = race.linkedCorpseKind.race.corpseDef;
                continue;
            }
            if (!batch.Added.Contains(def))
            {
                RelinkCorpses(def);
                continue;
            }
            if (DefDatabase<ThingDef>.GetNamedSilentFail("Corpse_" + def.defName) != null || GenerateCorpse == null)
            {
                report.Error(GenerateCorpse == null ? "corpse_generator_unavailable" : "def_collision", "ThingDef:Corpse_" + def.defName);
                continue;
            }

            var corpse = (ThingDef)GenerateCorpse.Invoke(null, [def, false, null])!;
            race.corpseDef = corpse;
            AddImplied(corpse, batch, report);
            if (ModsConfig.AnomalyActive && race.hasUnnaturalCorpse && GenerateUnnaturalCorpse != null)
            {
                AddImplied((ThingDef)GenerateUnnaturalCorpse.Invoke(null, [false, def])!, batch, report);
            }
        }
    }

    private static void AddImplied(ThingDef def, Batch batch, InjectionReport report)
    {
        def.generated = true;
        def.ResolveDefNameHash();
        def.modContentPack?.AddDef(def, "ImpliedDefs");
        def.PostLoad();
        DefDatabase<ThingDef>.Add(def);
        batch.Added.Add(def);
        batch.Corpses.Add(def);
        report.Implied.Add(Describe(def));
    }

    private static void RelinkCorpses(ThingDef raceDef)
    {
        var corpse = DefDatabase<ThingDef>.GetNamedSilentFail("Corpse_" + raceDef.defName);
        if (corpse != null)
        {
            raceDef.race.corpseDef = corpse;
            corpse.race = raceDef.race;
        }
        var unnatural = DefDatabase<ThingDef>.GetNamedSilentFail("UnnaturalCorpse_" + raceDef.defName);
        if (unnatural != null)
        {
            raceDef.race.unnaturalCorpseDef = unnatural;
            unnatural.race = raceDef.race;
        }
    }

    private static void ResolveCrossReferences()
    {
        try
        {
            DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences(FailMode.LogErrors);
        }
        finally
        {
            DirectXmlCrossRefLoader.Clear();
        }
    }

    private static void ResolveReferences(Batch batch, InjectionReport report)
    {
        var live = batch.Live.ToList();
        foreach (var def in live.Where(d => d is not ThingDef).Concat(live.Where(d => d is ThingDef)))
        {
            try
            {
                def.ResolveReferences();
            }
            catch (Exception ex)
            {
                report.Error("resolve_failed", Describe(def), ex.ToString());
            }
        }
    }

    private static void CollectConfigErrors(Batch batch, InjectionReport report)
    {
        foreach (var def in batch.Live)
        {
            try
            {
                foreach (var error in def.ConfigErrors())
                {
                    report.Error("config_error", Describe(def), error);
                }
            }
            catch (Exception ex)
            {
                report.Error("config_check_failed", Describe(def), ex.ToString());
            }
        }
    }

    private static void Rollback(Batch batch)
    {
        foreach (var (_, snapshots) in batch.BodyHediffSnapshots)
        {
            var touched = new HashSet<Pawn>(ReferenceEqualityComparer.Instance);
            foreach (var snap in snapshots)
            {
                SetHediffPart(snap.Hediff, snap.OriginalPart);
                touched.Add(snap.Pawn);
            }
            foreach (var pawn in touched)
            {
                pawn.health?.hediffSet?.DirtyCache();
            }
        }

        for (var i = batch.Backups.Count - 1; i >= 0; i--)
        {
            var (existing, backup) = batch.Backups[i];
            Gen.MemberwiseShallowCopy(backup, existing);
            existing.ClearCachedData();
            if (existing is ThingDef { race: not null } raceDef)
            {
                RelinkCorpses(raceDef);
            }
        }
        for (var i = batch.Added.Count - 1; i >= 0; i--)
        {
            var def = batch.Added[i];
            RemoveFromDatabase(def);
            if (def.modContentPack != null && PackDefs?.GetValue(def.modContentPack) is List<Def> packDefs)
            {
                packDefs.RemoveAll(d => ReferenceEquals(d, def));
            }
        }
    }

    private static void AssignShortHashes(Batch batch)
    {
        var registry = TakenHashes?.GetValue(null) as Dictionary<Type, HashSet<ushort>>;
        foreach (var group in batch.Added.Where(d => d.shortHash == 0).GroupBy(d => DatabaseTypeOf(d.GetType())))
        {
            HashSet<ushort>? taken = null;
            registry?.TryGetValue(group.Key, out taken);
            if (taken == null)
            {
                taken = GenDefDatabase.GetAllDefsInDatabaseForDef(group.Key).Select(d => d.shortHash).ToHashSet();
                if (registry != null)
                {
                    registry[group.Key] = taken;
                }
            }
            foreach (var def in group)
            {
                var hash = (ushort)(GenText.StableStringHash(def.defName) % 65535);
                while (hash == 0 || taken.Contains(hash))
                {
                    hash++;
                }
                def.shortHash = hash;
                taken.Add(hash);
            }
            DatabaseOf(group.Key).GetMethod("InitializeShortHashDictionary")!.Invoke(null, null);
        }
    }

    private static void Finish(Batch batch, InjectionReport report)
    {
        var categories = new HashSet<ThingCategoryDef>();
        foreach (var corpse in batch.Corpses)
        {
            foreach (var category in corpse.thingCategories ?? [])
            {
                if (!category.childThingDefs.Contains(corpse))
                {
                    category.childThingDefs.Add(corpse);
                }
                categories.Add(category);
                categories.UnionWith(category.Parents);
            }
        }
        foreach (var category in categories)
        {
            category.ResolveReferences();
        }
        if (batch.Corpses.Count > 0)
        {
            StorageSettings.ResetStaticData();
        }
        if (batch.Added.OfType<ThingDef>().Any())
        {
            AnimalPenUtility.ResetStaticData();
            GenLabel.ClearCache();
        }
        GiantFastLookup.BuildDefShortHashTable();

        var changedTextures = report.Textures.ToHashSet(StringComparer.Ordinal);
        var replacedRigs = batch.Backups.Select(b => b.Existing).OfType<GiantRigDef>().ToHashSet();
        var refresh = new HashSet<GiantRigDef>(batch.Added.OfType<GiantRigDef>().Concat(replacedRigs));
        if (changedTextures.Count > 0)
        {
            refresh.UnionWith(DefDatabase<GiantRigDef>.AllDefsListForReading.Where(rig =>
                rig.SortedNodes.Any(n =>
                    changedTextures.Contains(n.texPath ?? "") ||
                    changedTextures.Contains(n.glowTexPath ?? "") ||
                    changedTextures.Contains(n.silhouetteTexPath ?? "")) ||
                (rig.skinnedMeshes != null && rig.skinnedMeshes.Any(sm =>
                    sm != null && (
                        changedTextures.Contains(sm.texPath ?? "") ||
                        changedTextures.Contains(sm.glowTexPath ?? "") ||
                        changedTextures.Contains(sm.silhouetteTexPath ?? ""))))));
        }
        foreach (var rig in refresh)
        {
            Warm(rig);
        }

        var stale = refresh.Except(batch.Added.OfType<GiantRigDef>()).ToHashSet();
        var replacedBodies = batch.Backups.Select(b => b.Existing).OfType<BodyDef>().ToHashSet();
        if ((stale.Count > 0 || replacedBodies.Count > 0) && Current.ProgramState == ProgramState.Playing && Find.Maps != null)
        {
            static void RefreshPawnRig(Pawn? pawn, HashSet<GiantRigDef> staleRigs, HashSet<BodyDef> bodies)
            {
                if (pawn == null) return;
                var comp = pawn.TryGetComp<CompGiantRig>();
                if (comp?.RigDef != null && (staleRigs.Contains(comp.RigDef) || (pawn.RaceProps?.body != null && bodies.Contains(pawn.RaceProps.body))))
                {
                    comp.SwitchRig(comp.RigDef);
                    comp.RebuildFootprintNow(notifyMap: true);
                    pawn.Drawer?.renderer?.SetAllGraphicsDirty();
                }
            }

            foreach (var map in Find.Maps.ToList())
            {
                foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
                {
                    RefreshPawnRig(pawn, stale, replacedBodies);
                }

                var corpses = map.listerThings?.ThingsInGroup(ThingRequestGroup.Corpse);
                if (corpses != null)
                {
                    foreach (var thing in corpses.ToList())
                    {
                        if (thing is Corpse corpse && corpse.InnerPawn != null)
                        {
                            RefreshPawnRig(corpse.InnerPawn, stale, replacedBodies);
                        }
                    }
                }
            }
        }
    }

    internal static void Warm(GiantRigDef rig)
    {
        foreach (var node in rig.SortedNodes)
        {
            if (!string.IsNullOrEmpty(node.texPath))
            {
                GiantMaterialCache.WarmOnMainThread(node.texPath, Color.white);
                GiantAlphaMaskCache.GetOrLoad(node.texPath, node.alphaThreshold);
            }
            if (!string.IsNullOrEmpty(node.glowTexPath))
            {
                GiantMaterialCache.WarmOnMainThread(node.glowTexPath, node.glowColor, ShaderDatabase.TransparentPostLight);
            }
            if (!string.IsNullOrEmpty(node.silhouetteTexPath))
            {
                GiantMaterialCache.WarmOnMainThread(node.silhouetteTexPath, Color.white, ShaderDatabase.Cutout);
            }
        }
        if (rig.skinnedMeshes != null)
        {
            foreach (var sm in rig.skinnedMeshes)
            {
                if (sm == null) continue;
                if (!string.IsNullOrEmpty(sm.texPath))
                {
                    GiantMaterialCache.WarmOnMainThread(sm.texPath, Color.white, sm.shaderTypeDef?.Shader);
                    GiantAlphaMaskCache.GetOrLoad(sm.texPath, sm.alphaThreshold);
                }
                if (!string.IsNullOrEmpty(sm.glowTexPath))
                {
                    GiantMaterialCache.WarmOnMainThread(sm.glowTexPath, sm.glowColor, ShaderDatabase.TransparentPostLight);
                }
                if (!string.IsNullOrEmpty(sm.silhouetteTexPath))
                {
                    GiantMaterialCache.WarmOnMainThread(sm.silhouetteTexPath, Color.white, ShaderDatabase.Cutout);
                }
            }
        }
    }

    private static string Describe(Def def) => def.GetType().Name + ":" + def.defName;

    private static Type DatabaseTypeOf(Type defType)
    {
        var result = defType;
        for (var current = defType; current != null && current != typeof(Def); current = current.BaseType)
        {
            if (!current.IsAbstract)
            {
                result = current;
            }
        }
        return result;
    }

    private static Type DatabaseOf(Type databaseType) => typeof(DefDatabase<>).MakeGenericType(databaseType);

    private static void AddToDatabase(Def def)
    {
        var databaseType = DatabaseTypeOf(def.GetType());
        DatabaseOf(databaseType).GetMethod("Add", [databaseType])!.Invoke(null, [def]);
    }

    private static void RemoveFromDatabase(Def def)
    {
        var databaseType = DatabaseTypeOf(def.GetType());
        AccessTools.Method(DatabaseOf(databaseType), "Remove", [databaseType])!.Invoke(null, [def]);
    }
}
