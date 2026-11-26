using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using GiantCreatureFramework.Agent.Injection;
using RimWorld;
using Verse;

namespace GiantCreatureFramework.Agent.Probes;

/// <summary>
/// Blocks reading or copying built-in GiantCreatureFramework sample giants (which are counter-examples),
/// while allowing vanilla/DLC Defs, GCF base infrastructure Defs, and user-authored creatures in Creatures/.
/// </summary>
public static class CounterExampleGuard
{
    public const string GcfPackageId = "sanguo.giantcreatureframework";

    private static readonly string[] BuiltInGiantTokens =
    [
        "AbyssalDragonBeast",
        "DragonBeast",
        "Behemoth",
        "MechColossus",
        "MechScorpion",
        "PanzerIV",
        "ProtossCarrier",
        "CarrierInterceptor",
        "Rathalos",
        "Valstrax",
        "渊骸龙兽",
        "荒原巨兽",
        "贝希摩斯",
        "机械巨像",
        "反重力核心堡垒",
        "机械巨蝎",
        "四号坦克",
        "神族航母",
        "神族拦截机",
        "雄火龙",
        "天彗龙"
    ];

    private static readonly string[] BuiltInCounterExampleDirs =
    [
        "Defs/AbilityDefs/",
        "Defs/Bodies/",
        "Defs/GiantAnimDefs/",
        "Defs/GiantRigDefs/",
        "Defs/ThingDefs_Races/",
        "Defs/ThinkTreeDefs/"
    ];

    public static bool ContainsCounterExampleToken(string? text, out string matched)
    {
        matched = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var token in BuiltInGiantTokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matched = token;
                return true;
            }
        }
        return false;
    }

    public static bool IsUserAuthoredDef(Def def) =>
        AgentContentRoot.WasLoadedAtStartup(def) || GcfDefInjector.IsInjectedAtRuntime(def);

    public static bool IsForbiddenCounterExampleDef(Def? def)
    {
        if (def == null || IsUserAuthoredDef(def))
        {
            return false;
        }

        if (ContainsCounterExampleToken(def.defName, out _) ||
            ContainsCounterExampleToken(def.label, out _) ||
            ContainsCounterExampleToken(def.fileName, out _))
        {
            var pkg = def.modContentPack?.PackageIdPlayerFacing ?? def.modContentPack?.PackageId ?? string.Empty;
            if (string.IsNullOrEmpty(pkg) || pkg.Contains("giantcreatureframework", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var packId = def.modContentPack?.PackageIdPlayerFacing ?? def.modContentPack?.PackageId ?? string.Empty;
        if (!packId.Contains("giantcreatureframework", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (def is GiantRigDef or GiantAnimDef or BodyDef or BodyPartDef or PawnKindDef or AbilityDef or ThinkTreeDef)
        {
            return true;
        }

        if (def is ThingDef td && (td.race != null || td.projectile != null || td.IsWeapon || td.defName.StartsWith("Corpse_GCF_", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static bool IsForbiddenCounterExampleModFile(string packageId, string relativeXmlPath)
    {
        if (!packageId.Contains("giantcreatureframework", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = (relativeXmlPath ?? string.Empty).Replace('\\', '/');
        if (ContainsCounterExampleToken(normalized, out _))
        {
            return true;
        }

        foreach (var dir in BuiltInCounterExampleDirs)
        {
            if (normalized.Contains(dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public static class FuzzySearchMatcher
{
    public sealed class PreparedQuery
    {
        public string RawQuery { get; init; } = string.Empty;
        public string[] ExplicitTokens { get; init; } = Array.Empty<string>();
        public string[] NormalizedTokens { get; init; } = Array.Empty<string>();
        public string[] Subgrams { get; init; } = Array.Empty<string>();
        public bool IsWildcard { get; init; }
    }

    private static readonly List<string> EmptyTerms = new(0);

    public static PreparedQuery Prepare(string? rawQuery)
    {
        var q = (rawQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q) || q == "*" || string.Equals(q, "all", StringComparison.OrdinalIgnoreCase))
        {
            return new PreparedQuery { RawQuery = q, IsWildcard = true };
        }

        var explicitList = q
            .Split([',', ';', '|', '，', '；', '、', '/', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part =>
            {
                var trimmed = part.Trim();
                if (trimmed.Contains(' '))
                {
                    var spaceParts = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    return new[] { trimmed }.Concat(spaceParts);
                }
                return new[] { trimmed };
            })
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var normList = explicitList
            .Select(NormalizeText)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var subgrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var norm in normList)
        {
            var cjkChars = norm.Where(IsCjk).ToArray();
            if (cjkChars.Length >= 3)
            {
                var cjkStr = new string(cjkChars);
                for (var len = Math.Min(3, cjkStr.Length - 1); len >= 2; len--)
                {
                    for (var idx = 0; idx <= cjkStr.Length - len; idx++)
                    {
                        subgrams.Add(cjkStr.Substring(idx, len));
                    }
                }
            }
            else if (cjkChars.Length == 2)
            {
                foreach (var ch in cjkChars)
                {
                    subgrams.Add(ch.ToString());
                }
            }

            var asciiChars = norm.Where(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')).ToArray();
            if (asciiChars.Length >= 5)
            {
                var asciiStr = new string(asciiChars);
                subgrams.Add(asciiStr[..4]);
                if (asciiStr.Length >= 6)
                {
                    subgrams.Add(asciiStr[..5]);
                }
            }
        }

        return new PreparedQuery
        {
            RawQuery = q,
            ExplicitTokens = explicitList,
            NormalizedTokens = normList,
            Subgrams = subgrams.Except(normList, StringComparer.OrdinalIgnoreCase).ToArray(),
            IsWildcard = false
        };
    }

    public static int ScoreDef(Def def, PreparedQuery query, out List<string> matchedTerms)
    {
        if (query.IsWildcard)
        {
            matchedTerms = EmptyTerms;
            return 1;
        }

        List<string>? terms = null;
        var defName = def.defName ?? string.Empty;
        var label = def.label ?? string.Empty;
        var desc = def.description ?? string.Empty;
        var modName = def.modContentPack?.Name ?? string.Empty;
        var pkgId = def.modContentPack?.PackageIdPlayerFacing ?? def.modContentPack?.PackageId ?? string.Empty;

        var normDefName = NormalizeText(defName);
        var normLabel = NormalizeText(label);

        var score = 0;
        var hasContentMatch = false;
        var hasModMatch = false;

        foreach (var token in query.ExplicitTokens)
        {
            var tokenScore = 0;
            if (string.Equals(defName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, token, StringComparison.OrdinalIgnoreCase))
            {
                tokenScore = 120;
                hasContentMatch = true;
            }
            else if (defName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                tokenScore = 75;
                hasContentMatch = true;
            }
            else if (label.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                tokenScore = 70;
                hasContentMatch = true;
            }
            else if (desc.Length > 0 && token.Length >= 2 && desc.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                tokenScore = 35;
                hasContentMatch = true;
            }
            else if (modName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                     pkgId.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                tokenScore = 25;
                hasModMatch = true;
            }

            if (tokenScore > 0)
            {
                score += tokenScore;
                (terms ??= new List<string>(4)).Add(token);
            }
        }

        foreach (var normToken in query.NormalizedTokens)
        {
            if (normToken.Length < 2) continue;
            if (string.Equals(normDefName, normToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normLabel, normToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 95;
                hasContentMatch = true;
                terms ??= new List<string>(4);
                if (!terms.Contains(normToken)) terms.Add(normToken);
            }
            else if (normDefName.Contains(normToken, StringComparison.OrdinalIgnoreCase) ||
                     normLabel.Contains(normToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 55;
                hasContentMatch = true;
                terms ??= new List<string>(4);
                if (!terms.Contains(normToken)) terms.Add(normToken);
            }
        }

        var subHitCount = 0;
        foreach (var sub in query.Subgrams)
        {
            if (sub.Length >= 2)
            {
                if (normLabel.Contains(sub, StringComparison.OrdinalIgnoreCase) ||
                    normDefName.Contains(sub, StringComparison.OrdinalIgnoreCase))
                {
                    score += sub.Length >= 3 ? 32 : 18;
                    subHitCount++;
                    hasContentMatch = true;
                    terms ??= new List<string>(4);
                    if (terms.Count < 5 && !terms.Contains(sub)) terms.Add(sub);
                }
                else if (sub.Length >= 3 &&
                         (modName.Contains(sub, StringComparison.OrdinalIgnoreCase) ||
                          pkgId.Contains(sub, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 14;
                    subHitCount++;
                    hasModMatch = true;
                    terms ??= new List<string>(4);
                    if (terms.Count < 5 && !terms.Contains(sub)) terms.Add(sub);
                }
            }
            else if (sub.Length == 1 && score == 0 && query.Subgrams.Length <= 3 &&
                     normLabel.Contains(sub, StringComparison.OrdinalIgnoreCase))
            {
                score += 6;
                subHitCount++;
            }
        }

        if (subHitCount >= 2) score += subHitCount * 12;
        if (hasContentMatch && hasModMatch) score += 160;

        matchedTerms = terms ?? EmptyTerms;
        return score;
    }

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var buf = new char[text.Length];
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c) || IsCjk(c))
            {
                buf[count++] = c;
            }
        }
        return count == 0 ? string.Empty : new string(buf, 0, count);
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
}

public static class RuntimeReadProbes
{
    public sealed record ActiveModSnapshot(
        int LoadOrderIndex,
        string Name,
        string PackageId,
        string RootDir,
        IReadOnlyList<string> LoadFolders);

    private static readonly Lazy<List<Type>> DefTypes = new(() => typeof(Def).AllSubclasses().ToList());

    public static string SearchDefsJson(string query, string? defTypeFilter = null, int limit = 20)
    {
        var kw = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(kw))
        {
            return JsonSerializer.Serialize(new { error = AgentProtocol.Get("Probe_EmptyQuery") }, Evidence.UnescapedJsonOptions);
        }

        if (CounterExampleGuard.ContainsCounterExampleToken(kw, out var forbiddenKw))
        {
            return JsonSerializer.Serialize(new
            {
                error = AgentProtocol.Format("Probe_AntiExampleBlocked", forbiddenKw)
            }, Evidence.UnescapedJsonOptions);
        }

        var prepared = FuzzySearchMatcher.Prepare(kw);
        var maxResults = Math.Clamp(limit, 1, 60);
        var matchedDefs = new List<(Def Def, int Score, bool IsUserOrThirdParty, List<string> Terms)>();
        var seenDefs = new HashSet<Def>();
        var defTypes = GetTargetDefTypes(defTypeFilter);
        var partialScanDueToBudget = false;

        try
        {
            foreach (var defType in defTypes)
            {
                ProbeExecution.Check();
                IEnumerable<Def> defs;
                try
                {
                    defs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
                {
                    continue;
                }

                var idx = 0;
                foreach (var def in defs)
                {
                    if ((idx++ & 63) == 0) ProbeExecution.Check();
                    if (def == null || !seenDefs.Add(def) || !MatchesThingCategoryFilter(def, defTypeFilter))
                    {
                        continue;
                    }

                    if (CounterExampleGuard.IsForbiddenCounterExampleDef(def))
                    {
                        continue;
                    }

                    var score = FuzzySearchMatcher.ScoreDef(def, prepared, out var matchedTerms);
                    if (score > 0)
                    {
                        var isNonCore = CounterExampleGuard.IsUserAuthoredDef(def) ||
                                        (def.modContentPack != null && !def.modContentPack.IsOfficialMod && !def.modContentPack.IsCoreMod);
                        matchedDefs.Add((def, score, isNonCore, matchedTerms));
                        if (prepared.IsWildcard && matchedDefs.Count >= maxResults)
                        {
                            break;
                        }
                    }
                }

                if (prepared.IsWildcard && matchedDefs.Count >= maxResults)
                {
                    break;
                }
            }
        }
        catch (TimeoutException) when (matchedDefs.Count > 0)
        {
            partialScanDueToBudget = true;
        }

        var orderedMatches = matchedDefs
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.IsUserOrThirdParty)
            .ThenBy(x => x.Def.defName?.Length ?? 99)
            .ToList();

        var results = new List<object>(Math.Min(maxResults, orderedMatches.Count));
        for (var idx = 0; idx < orderedMatches.Count && results.Count < maxResults; idx++)
        {
            var entry = orderedMatches[idx];
            var def = entry.Def;
            var briefTag = def is ThingDef td
                ? (td.IsRangedWeapon ? "RangedWeapon" : td.IsMeleeWeapon ? "MeleeWeapon" : td.IsApparel ? "Apparel" : td.building != null ? "Building" : td.race != null ? "Pawn" : "Item")
                : def.GetType().Name;
            results.Add(new
            {
                defName = def.defName ?? string.Empty,
                label = def.label ?? string.Empty,
                defType = def.GetType().Name,
                categoryTag = briefTag,
                sourceMod = CounterExampleGuard.IsUserAuthoredDef(def)
                    ? "CreatureWorkspace"
                    : (def.modContentPack?.Name ?? "Core / Unknown"),
                matchScore = entry.Score,
                matchedTerms = entry.Terms.ToArray()
            });
        }

        return JsonSerializer.Serialize(new
        {
            disclosureStage = AgentProtocol.Get("Probe_DisclosureLevel1"),
            query = kw,
            parsedQueryTokens = prepared.ExplicitTokens,
            defTypeFilter = defTypeFilter ?? "All",
            totalMatched = matchedDefs.Count,
            returnedCount = results.Count,
            possiblyTruncated = matchedDefs.Count > maxResults || partialScanDueToBudget,
            defs = results
        }, Evidence.UnescapedJsonOptions);
    }

    public static string GetDefDetailsJson(string defName, string? defType = null, string? fields = null)
    {
        var cleanName = (defName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return JsonSerializer.Serialize(new { error = AgentProtocol.Get("Probe_EmptyDefName") }, Evidence.UnescapedJsonOptions);
        }

        if (CounterExampleGuard.ContainsCounterExampleToken(cleanName, out var forbiddenToken))
        {
            return JsonSerializer.Serialize(new
            {
                error = AgentProtocol.Format("Probe_AntiExampleBlocked", forbiddenToken)
            }, Evidence.UnescapedJsonOptions);
        }

        Def? matchedDef = null;
        var defTypes = GetTargetDefTypes(defType);
        foreach (var t in defTypes)
        {
            ProbeExecution.Check();
            try
            {
                matchedDef = GenDefDatabase.GetDefSilentFail(t, cleanName, specialCaseForSoundDefs: false);
                if (matchedDef != null)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
            {
                // Ignore
            }
        }

        if (matchedDef == null)
        {
            var exactCandidates = new List<Def>();
            foreach (var t in defTypes)
            {
                ProbeExecution.Check();
                try
                {
                    exactCandidates.AddRange(GenDefDatabase.GetAllDefsInDatabaseForDef(t).Budgeted()
                        .Where(d => d != null &&
                                    !CounterExampleGuard.IsForbiddenCounterExampleDef(d) &&
                                    (string.Equals(d.defName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(d.label, cleanName, StringComparison.OrdinalIgnoreCase))));
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
                {
                    // Ignore malformed or incompatible DefDatabase types from third-party mods
                }
            }
            matchedDef = ExactDefinitionLookup.Resolve(exactCandidates.Distinct(), cleanName, d => d.defName, d => d.label);
        }

        if (matchedDef != null && CounterExampleGuard.IsForbiddenCounterExampleDef(matchedDef))
        {
            return JsonSerializer.Serialize(new
            {
                error = AgentProtocol.Format("Probe_AntiExampleBlocked", matchedDef.defName)
            }, Evidence.UnescapedJsonOptions);
        }

        if (matchedDef == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = AgentProtocol.Format("Probe_DefNotFound", cleanName)
            }, Evidence.UnescapedJsonOptions);
        }

        if (string.IsNullOrWhiteSpace(fields))
        {
            var outline = BuildNonEmptyFieldOutline(matchedDef);
            return JsonSerializer.Serialize(new
            {
                disclosureStage = AgentProtocol.Get("Probe_DisclosureLevel2"),
                defName = matchedDef.defName,
                label = matchedDef.label ?? string.Empty,
                defType = matchedDef.GetType().Name,
                sourceMod = CounterExampleGuard.IsUserAuthoredDef(matchedDef)
                    ? "CreatureWorkspace"
                    : (matchedDef.modContentPack?.Name ?? "Core / Unknown"),
                packageId = matchedDef.modContentPack?.PackageIdPlayerFacing ?? matchedDef.modContentPack?.PackageId ?? string.Empty,
                description = Truncate((matchedDef.description ?? string.Empty).StripTags(), 240),
                nonEmptyOutline = outline
            }, Evidence.UnescapedJsonOptions);
        }

        var reflectedFields = ExtractDefFields(matchedDef, fields);
        var hasMissingField = reflectedFields.Values.Any(v => v != null && v.GetType().GetProperty("error") != null);
        var extraFieldNames = hasMissingField
            ? matchedDef.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .Where(n => !Evidence.IsSensitiveName(n) && !reflectedFields.ContainsKey(n))
                .Take(60)
                .ToArray()
            : null;

        return JsonSerializer.Serialize(new
        {
            disclosureStage = AgentProtocol.Format("Probe_DisclosureLevel3", fields),
            defName = matchedDef.defName,
            label = matchedDef.label ?? string.Empty,
            defType = matchedDef.GetType().Name,
            sourceMod = CounterExampleGuard.IsUserAuthoredDef(matchedDef)
                ? "CreatureWorkspace"
                : (matchedDef.modContentPack?.Name ?? "Core / Unknown"),
            packageId = matchedDef.modContentPack?.PackageIdPlayerFacing ?? matchedDef.modContentPack?.PackageId ?? string.Empty,
            availableFields = extraFieldNames,
            runtimeMergedFields = reflectedFields
        }, Evidence.UnescapedJsonOptions);
    }

    public static List<ActiveModSnapshot> CaptureActiveModsOnMainThread()
    {
        var mods = LoadedModManager.RunningModsListForReading;
        var list = new List<ActiveModSnapshot>(mods.Count);
        var agentRoot = Path.GetFullPath(AgentContentRoot.RootPath);
        for (var i = 0; i < mods.Count; i++)
        {
            ProbeExecution.Check();
            var mod = mods[i];
            var folders = (mod.foldersToLoadDescendingOrder ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f) &&
                            !Path.GetFullPath(f).StartsWith(agentRoot, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            list.Add(new ActiveModSnapshot(i, mod.Name, mod.PackageIdPlayerFacing, mod.RootDir, folders));
        }
        return list;
    }

    public static string SearchModXmlAndPatchesOnDiskJson(
        IReadOnlyList<ActiveModSnapshot> mods,
        string keyword,
        string scope = "all",
        int maxMatches = 25,
        string? modFilter = null,
        int startLine = 0,
        int maxXmlScanFiles = 50_000)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException(AgentProtocol.Get("Probe_EmptyKeyword"));
        }

        var kw = keyword.Trim();
        if (CounterExampleGuard.ContainsCounterExampleToken(kw, out var forbiddenKw))
        {
            return JsonSerializer.Serialize(new
            {
                error = AgentProtocol.Format("Probe_AntiExampleBlocked", forbiddenKw)
            }, Evidence.UnescapedJsonOptions);
        }

        var areas = scope.ToLowerInvariant() switch
        {
            "all" => new[] { "Defs", "Patches", "Languages" },
            "defs" => new[] { "Defs" },
            "patches" => new[] { "Patches" },
            "languages" => new[] { "Languages" },
            _ => throw new ArgumentException(AgentProtocol.Get("Probe_InvalidXmlScope"))
        };

        var cap = Math.Clamp(maxMatches, 1, 40);
        var scanBudget = Math.Clamp(maxXmlScanFiles, 1_000, 100_000);
        IReadOnlyList<ActiveModSnapshot> targetMods = mods;
        if (!string.IsNullOrWhiteSpace(modFilter))
        {
            var filtered = ModSearchScope.Resolve(mods, modFilter, m => m.PackageId, m => m.Name);
            if (filtered.Count == 0)
            {
                return JsonSerializer.Serialize(new { error = AgentProtocol.Get("Probe_ModNotFound"), modFilter }, Evidence.UnescapedJsonOptions);
            }
            targetMods = filtered;
        }

        var hits = new List<object>();
        var skipped = new List<string>();
        var scanned = 0;
        var limited = false;

        foreach (var mod in targetMods)
        {
            ProbeExecution.Check();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var loadFolder in mod.LoadFolders)
            {
                ProbeExecution.Check();
                foreach (var area in areas)
                {
                    ProbeExecution.Check();
                    var folder = Path.Combine(loadFolder, area);
                    if (!Directory.Exists(folder)) continue;
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(folder, "*.xml", options))
                        {
                            ProbeExecution.Check();
                            if (++scanned > scanBudget) { limited = true; goto Finished; }
                            var key = area + "/" + Path.GetRelativePath(folder, file);
                            if (!seen.Add(key)) continue;

                            var relXmlPath = Path.GetRelativePath(mod.RootDir, file);
                            if (CounterExampleGuard.IsForbiddenCounterExampleModFile(mod.PackageId, relXmlPath))
                            {
                                continue;
                            }

                            try
                            {
                                if (new FileInfo(file).Length > 1_000_000) { skipped.Add(key + ": size limit"); continue; }
                                var pathMatchesKeyword = relXmlPath.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                                         Path.GetFileName(file).Contains(kw, StringComparison.OrdinalIgnoreCase);
                                var lines = File.ReadAllLines(file);
                                if (lines.Length == 0) continue;

                                if (pathMatchesKeyword && startLine > 0)
                                {
                                    var sliceStart = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                                    hits.Add(new
                                    {
                                        modLoadOrder = mod.LoadOrderIndex,
                                        modName = mod.Name,
                                        packageId = mod.PackageId,
                                        relativeXmlPath = relXmlPath,
                                        lineNumber = sliceStart + 1,
                                        totalLines = lines.Length,
                                        xmlSnippet = BuildSmartXmlSnippet(lines, sliceStart, 65, out _)
                                    });
                                    if (hits.Count >= cap) { limited = true; goto Finished; }
                                    continue;
                                }

                                var fileHits = 0;
                                var searchFromLine = startLine > 0 ? Math.Clamp(startLine - 1, 0, lines.Length - 1) : 0;
                                for (var line = searchFromLine; line < lines.Length; line++)
                                {
                                    ProbeExecution.Check();
                                    if (!lines[line].Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;
                                    var start = Math.Max(0, line - 4);
                                    var snippet = BuildSmartXmlSnippet(lines, start, 65, out var lastConsumed);
                                    hits.Add(new
                                    {
                                        modLoadOrder = mod.LoadOrderIndex,
                                        modName = mod.Name,
                                        packageId = mod.PackageId,
                                        relativeXmlPath = relXmlPath,
                                        lineNumber = line + 1,
                                        totalLines = lines.Length,
                                        xmlSnippet = snippet
                                    });
                                    fileHits++;
                                    if (hits.Count >= cap) { limited = true; goto Finished; }
                                    if (fileHits >= 3) break;
                                    line = Math.Max(line, lastConsumed);
                                }

                                if (fileHits == 0 && pathMatchesKeyword)
                                {
                                    var sliceStart = startLine > 0 ? Math.Clamp(startLine - 1, 0, lines.Length - 1) : 0;
                                    hits.Add(new
                                    {
                                        modLoadOrder = mod.LoadOrderIndex,
                                        modName = mod.Name,
                                        packageId = mod.PackageId,
                                        relativeXmlPath = relXmlPath,
                                        lineNumber = sliceStart + 1,
                                        totalLines = lines.Length,
                                        xmlSnippet = BuildSmartXmlSnippet(lines, sliceStart, 65, out _)
                                    });
                                }
                            }
                            catch (IOException ex) { skipped.Add(key + ": " + ex.GetType().Name); }
                            catch (UnauthorizedAccessException) { skipped.Add(key + ": access denied"); }
                            if (hits.Count >= cap) { limited = true; goto Finished; }
                        }
                    }
                    catch (IOException ex) { skipped.Add(area + ": " + ex.GetType().Name); }
                    catch (UnauthorizedAccessException) { skipped.Add(area + ": access denied"); }
                }
            }
        }

    Finished:
        return JsonSerializer.Serialize(new
        {
            keyword = kw,
            scope,
            modFilter,
            startLine = startLine > 0 ? startLine : (int?)null,
            scannedFiles = scanned,
            maxXmlScanBudget = scanBudget,
            truncated = limited,
            searchedModCount = targetMods.Count,
            skippedFiles = skipped.Take(20),
            matchedFilesCount = hits.Count,
            matches = hits,
            evidenceKind = "activeLoadFolderTextMatch",
            limitations = AgentProtocol.Get("Probe_XmlSearchLimitations")
        }, Evidence.UnescapedJsonOptions);
    }

    public static string BuildSmartXmlSnippet(string[] lines, int startLineIndex, int maxOutputLines, out int lastConsumedLineIndex)
    {
        var output = new List<string>(maxOutputLines);
        var i = Math.Clamp(startLineIndex, 0, Math.Max(0, lines.Length - 1));
        lastConsumedLineIndex = i;

        while (i < lines.Length && output.Count < maxOutputLines)
        {
            var trimmed = lines[i].AsSpan().TrimStart();
            if (trimmed.StartsWith("<li>", StringComparison.OrdinalIgnoreCase))
            {
                var runEnd = i + 1;
                while (runEnd < lines.Length && lines[runEnd].AsSpan().TrimStart().StartsWith("<li>", StringComparison.OrdinalIgnoreCase))
                {
                    runEnd++;
                }

                var runLen = runEnd - i;
                if (runLen > 25)
                {
                    output.Add($"{i + 1}: {lines[i]}");
                    output.Add($"{i + 2}: {lines[i + 1]}");
                    output.Add(AgentProtocol.Format("Probe_FoldedLiSummary", runLen - 4, i + 3, runEnd - 2));
                    output.Add($"{runEnd - 1}: {lines[runEnd - 2]}");
                    output.Add($"{runEnd}: {lines[runEnd - 1]}");
                    lastConsumedLineIndex = runEnd - 1;
                    i = runEnd;
                    continue;
                }
            }

            output.Add($"{i + 1}: {lines[i]}");
            lastConsumedLineIndex = i;
            i++;
        }

        return string.Join("\n", output);
    }

    private static bool MatchesThingCategoryFilter(Def def, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || def is not ThingDef td) return true;
        var f = filter.Trim();
        if (f.Equals("Weapon", StringComparison.OrdinalIgnoreCase) || f.Equals("Weapons", StringComparison.OrdinalIgnoreCase)) return td.IsWeapon;
        if (f.Equals("RangedWeapon", StringComparison.OrdinalIgnoreCase) || f.Equals("Ranged", StringComparison.OrdinalIgnoreCase)) return td.IsRangedWeapon;
        if (f.Equals("MeleeWeapon", StringComparison.OrdinalIgnoreCase) || f.Equals("Melee", StringComparison.OrdinalIgnoreCase)) return td.IsMeleeWeapon;
        if (f.Equals("Apparel", StringComparison.OrdinalIgnoreCase)) return td.IsApparel;
        if (f.Equals("Building", StringComparison.OrdinalIgnoreCase)) return td.building != null;
        if (f.Equals("Pawn", StringComparison.OrdinalIgnoreCase) || f.Equals("Race", StringComparison.OrdinalIgnoreCase)) return td.race != null;
        return true;
    }

    private static List<Type> GetTargetDefTypes(string? filter)
    {
        var allDefTypes = DefTypes.Value;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            if (f.Equals("Weapon", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Weapons", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("RangedWeapon", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Ranged", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("MeleeWeapon", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Melee", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Apparel", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Building", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Pawn", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Race", StringComparison.OrdinalIgnoreCase))
            {
                return [typeof(ThingDef)];
            }

            return allDefTypes
                .Where(t => string.Equals(t.Name, f, StringComparison.OrdinalIgnoreCase) ||
                            t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var priority = new[]
        {
            typeof(ThingDef),
            typeof(BodyDef),
            typeof(BodyPartDef),
            typeof(PawnKindDef),
            typeof(ThinkTreeDef),
            typeof(AbilityDef),
            typeof(DamageDef),
            typeof(HediffDef),
            typeof(GiantRigDef),
            typeof(GiantAnimDef)
        };
        return priority.Concat(allDefTypes.Except(priority)).ToList();
    }

    private static Dictionary<string, string> BuildNonEmptyFieldOutline(Def def)
    {
        var outline = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in def.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            ProbeExecution.Check();
            if (Evidence.IsSensitiveName(field.Name)) continue;
            if (field.Name is "defName" or "label" or "description" or "modContentPack" or "fileName" or "shortHash" or "index" or "debugRandomId")
                continue;

            object? val;
            try { val = field.GetValue(def); } catch { continue; }
            if (val == null) continue;

            switch (val)
            {
                case string s when string.IsNullOrWhiteSpace(s):
                case bool b when !b:
                case int i when i == 0 || i == -1:
                case float f when Math.Abs(f) < 0.0001f || Math.Abs(f + 1f) < 0.0001f:
                    continue;
                case string s:
                    outline[field.Name] = $"string: \"{Truncate(s, 48)}\"";
                    break;
                case ValueType vt:
                    outline[field.Name] = $"{field.FieldType.Name}: {vt}";
                    break;
                case Def d:
                    outline[field.Name] = $"{d.GetType().Name}: {d.defName} ({d.label})";
                    break;
                case Type t:
                    outline[field.Name] = $"Type: {t.FullName}";
                    break;
                case ICollection col:
                {
                    if (col.Count == 0) continue;
                    var sample = new List<string>();
                    foreach (var item in col)
                    {
                        if (item == null) continue;
                        sample.Add(SummarizeOutlineItem(item));
                        if (sample.Count >= 5) break;
                    }
                    var suffix = col.Count > sample.Count ? ", ..." : "";
                    outline[field.Name] = $"Count={col.Count} [{string.Join(", ", sample)}{suffix}]";
                    break;
                }
                default:
                    outline[field.Name] = AgentProtocol.Format("Probe_ExpandHint", val.GetType().Name, field.Name);
                    break;
            }
        }
        return outline;
    }

    private static string SummarizeOutlineItem(object item) => item switch
    {
        Def d => d.defName ?? d.GetType().Name,
        StatModifier sm => $"{sm.stat?.defName}={sm.value}",
        CompProperties cp => cp.compClass?.Name ?? cp.GetType().Name,
        VerbProperties vp => vp.label ?? vp.verbClass?.Name ?? "Verb",
        Tool tool => tool.label ?? "Tool",
        _ => item.ToString() ?? item.GetType().Name
    };

    private static Dictionary<string, object?> ExtractDefFields(Def def, string? requestedFields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fields = def.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        var requested = string.IsNullOrWhiteSpace(requestedFields)
            ? null
            : requestedFields.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).Take(12).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (requested != null)
        {
            foreach (var name in requested)
            {
                if (!fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    dict[name] = new { error = AgentProtocol.Get("Probe_MissingField") };
                }
            }
        }

        foreach (var field in fields)
        {
            ProbeExecution.Check();
            if (requested != null && !requested.Contains(field.Name)) continue;
            if (Evidence.IsSensitiveName(field.Name)) continue;
            if (field.Name is "defName" or "label" or "description" or "modContentPack" or "fileName" or "shortHash" or "index")
                continue;

            try
            {
                dict[field.Name] = FormatFieldValue(field.GetValue(def), depth: 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
            {
                dict[field.Name] = new { error = ex.Message };
            }
        }

        return dict;
    }

    private static object? FormatFieldValue(object? val, int depth)
    {
        if (val == null || depth > 2)
        {
            return val == null ? null : new { omitted = true, reason = "depthLimit" };
        }

        switch (val)
        {
            case string s:
                return string.IsNullOrWhiteSpace(s) ? null : Truncate(s, 200);
            case bool b:
                return b;
            case int i:
                return i;
            case float f:
                return float.IsFinite(f) ? Math.Round(f, 3) : f.ToString(CultureInfo.InvariantCulture);
            case Enum e:
                return e.ToString();
            case Def d:
                return $"{d.defName} ({d.GetType().Name})";
            case Type t:
                return t.FullName;
            case StatModifier sm:
                return new
                {
                    stat = sm.stat?.defName ?? "null",
                    value = float.IsFinite(sm.value) ? (object)sm.value : sm.value.ToString(CultureInfo.InvariantCulture)
                };
            case ThingDefCountClass tc:
                return new { thingDef = tc.thingDef?.defName ?? "null", count = tc.count };
            case CompProperties or DefModExtension or VerbProperties or Tool or HediffStage or RaceProperties or ProjectileProperties:
                return ExtractNestedObjectFields(val, depth + 1);
            case IEnumerable enumerable and not string:
            {
                var items = new List<object>();
                foreach (var item in enumerable)
                {
                    ProbeExecution.Check();
                    var sub = FormatFieldValue(item, depth + 1);
                    if (sub != null) items.Add(sub);
                    if (items.Count >= 24) break;
                }
                if (items.Count >= 24) items.Add(new { omitted = true, reason = "itemLimit" });
                return items;
            }
            default:
                if (depth <= 1 && !val.GetType().IsPrimitive &&
                    val.GetType().Namespace is "RimWorld" or "Verse" or "GiantCreatureFramework")
                {
                    return ExtractNestedObjectFields(val, depth + 1);
                }
                return val.ToString();
        }
    }

    private static Dictionary<string, object?> ExtractNestedObjectFields(object obj, int depth)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_class"] = obj.GetType().FullName
        };

        if (depth > 3) return dict;

        foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            ProbeExecution.Check();
            if (Evidence.IsSensitiveName(f.Name) || f.Name.StartsWith('<')) continue;
            try
            {
                dict[f.Name] = FormatFieldValue(f.GetValue(obj), depth);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
            {
                dict[f.Name] = new { error = ex.Message };
            }
        }
        return dict;
    }

    private static string Truncate(string value, int maxChars) =>
        string.IsNullOrEmpty(value) || value.Length <= maxChars ? value : value[..maxChars] + "...";
}
