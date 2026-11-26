using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace GiantCreatureFramework.Agent;

public sealed record CapabilityGroup(string Id, string Purpose, IReadOnlyList<string> Tools);
public sealed record ManualSummary(string Name, string Summary, string FilePath);

/// <summary>
/// Loads and validates all model-visible prompts, protocol markers, tool catalog/schemas,
/// and runtime authoring manuals from 1.7/AgentSkills on disk. C# retains only key names.
/// </summary>
public sealed class AgentSkillBundle
{
    public required IReadOnlyDictionary<AgentPromptSection, string> Prompts { get; init; }
    public required IReadOnlyDictionary<string, string> Protocol { get; init; }
    public required IReadOnlyList<CapabilityGroup> Capabilities { get; init; }
    public required IReadOnlyDictionary<string, string> ToolPurposes { get; init; }
    public required IReadOnlyDictionary<string, JsonObject> ToolSchemas { get; init; }
    public required IReadOnlyList<ManualSummary> Manuals { get; init; }
}

public static class AgentSkillStore
{
    private static readonly object Sync = new();
    private static AgentSkillBundle? _current;

    public static AgentSkillBundle Current
    {
        get
        {
            lock (Sync)
            {
                return _current ??= LoadFromDirectory(LocateDefaultSkillsDirectory());
            }
        }
    }

    public static void InitializeFromModRoot(string modRootDir)
    {
        var candidate = Path.Combine(modRootDir, "1.7", "AgentSkills");
        if (!Directory.Exists(candidate))
        {
            candidate = Path.Combine(modRootDir, "AgentSkills");
        }
        var bundle = LoadFromDirectory(candidate);
        lock (Sync)
        {
            _current = bundle;
        }
    }

    public static void SetForTesting(AgentSkillBundle? bundle)
    {
        lock (Sync)
        {
            _current = bundle;
        }
    }

    public static string Protocol(string key) => AgentProtocol.Get(key);

    public static AgentSkillBundle LoadFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"AgentSkills directory not found: '{directory}'.");
        }

        // 1. Validate prompts.xml (every AgentPromptSection element must exist)
        var promptsPath = Path.Combine(directory, "prompts.xml");
        if (!File.Exists(promptsPath))
        {
            throw new FileNotFoundException("Missing prompts.xml in AgentSkills directory.", promptsPath);
        }
        var promptsDoc = XDocument.Load(promptsPath);
        var promptsRoot = promptsDoc.Root ?? throw new InvalidDataException("prompts.xml has no root element.");
        var prompts = new Dictionary<AgentPromptSection, string>();
        foreach (var section in Enum.GetValues<AgentPromptSection>())
        {
            var el = promptsRoot.Element(section.ToString())
                ?? throw new InvalidDataException($"Missing required prompt key '{section}' in prompts.xml.");
            prompts[section] = el.Value.Replace("\r\n", "\n");
        }

        // 2. Validate protocol.xml (every key in AgentProtocol.RequiredKeys must exist)
        var protocolPath = Path.Combine(directory, "protocol.xml");
        if (!File.Exists(protocolPath))
        {
            throw new FileNotFoundException("Missing protocol.xml in AgentSkills directory.", protocolPath);
        }
        var protocolDoc = XDocument.Load(protocolPath);
        var protocolRoot = protocolDoc.Root ?? throw new InvalidDataException("protocol.xml has no root element.");
        var protocol = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in protocolRoot.Elements())
        {
            protocol[el.Name.LocalName] = el.Value.Replace("\r\n", "\n");
        }
        foreach (var requiredKey in AgentProtocol.RequiredKeys)
        {
            if (!protocol.TryGetValue(requiredKey, out var val) || string.IsNullOrEmpty(val))
            {
                throw new InvalidDataException($"Missing or empty required protocol key '{requiredKey}' in protocol.xml.");
            }
        }

        // 3. Validate tools.json
        var toolsPath = Path.Combine(directory, "tools.json");
        if (!File.Exists(toolsPath))
        {
            throw new FileNotFoundException("Missing tools.json in AgentSkills directory.", toolsPath);
        }
        var toolsRoot = JsonNode.Parse(File.ReadAllText(toolsPath)) as JsonObject
            ?? throw new InvalidDataException("tools.json must contain a JSON object.");

        var capabilities = new List<CapabilityGroup>();
        if (toolsRoot["capabilities"] is JsonArray capsArray)
        {
            foreach (var item in capsArray.OfType<JsonObject>())
            {
                var id = item["id"]?.GetValue<string>() ?? throw new InvalidDataException("Capability missing id.");
                var purpose = item["purpose"]?.GetValue<string>() ?? throw new InvalidDataException($"Capability '{id}' missing purpose.");
                var toolList = item["tools"]?.AsArray().Select(t => t!.GetValue<string>()).ToArray()
                    ?? throw new InvalidDataException($"Capability '{id}' missing tools array.");
                capabilities.Add(new CapabilityGroup(id, purpose, toolList));
            }
        }

        var purposes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (toolsRoot["purposes"] is JsonObject purposesObj)
        {
            foreach (var kv in purposesObj)
            {
                var val = kv.Value?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(val))
                {
                    throw new InvalidDataException($"Tool purpose for '{kv.Key}' in tools.json cannot be empty.");
                }
                purposes[kv.Key] = val;
            }
        }

        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (toolsRoot["schemas"] is JsonArray schemasArray)
        {
            foreach (var item in schemasArray.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>() ?? throw new InvalidDataException("Schema entry missing name.");
                var desc = item["description"]?.GetValue<string>() ?? throw new InvalidDataException($"Schema '{name}' missing description.");
                var parameters = item["parameters"] as JsonObject ?? throw new InvalidDataException($"Schema '{name}' missing parameters object.");
                schemas[name] = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = desc,
                        ["parameters"] = parameters.DeepClone()
                    }
                };
            }
        }

        // 4. Validate manuals/index.json
        var manualsDir = Path.Combine(directory, "manuals");
        var manualsIndex = Path.Combine(manualsDir, "index.json");
        if (!File.Exists(manualsIndex))
        {
            throw new FileNotFoundException("Missing manuals/index.json in AgentSkills directory.", manualsIndex);
        }
        var indexRoot = JsonNode.Parse(File.ReadAllText(manualsIndex)) as JsonObject
            ?? throw new InvalidDataException("manuals/index.json must contain a JSON object.");
        var manuals = new List<ManualSummary>();
        if (indexRoot["manuals"] is JsonArray manualsArray)
        {
            foreach (var entry in manualsArray.OfType<JsonObject>())
            {
                var name = entry["name"]?.GetValue<string>() ?? throw new InvalidDataException("Manual entry missing name.");
                var summary = entry["summary"]?.GetValue<string>() ?? throw new InvalidDataException($"Manual '{name}' missing summary.");
                var mdPath = Path.Combine(manualsDir, name + ".md");
                if (!File.Exists(mdPath))
                {
                    throw new FileNotFoundException($"Manual file '{name}.md' listed in index.json does not exist.", mdPath);
                }
                manuals.Add(new ManualSummary(name, summary, mdPath));
            }
        }

        return new AgentSkillBundle
        {
            Prompts = new ReadOnlyDictionary<AgentPromptSection, string>(prompts),
            Protocol = new ReadOnlyDictionary<string, string>(protocol),
            Capabilities = capabilities.AsReadOnly(),
            ToolPurposes = new ReadOnlyDictionary<string, string>(purposes),
            ToolSchemas = new ReadOnlyDictionary<string, JsonObject>(schemas),
            Manuals = manuals.AsReadOnly()
        };
    }

    public static string LocateDefaultSkillsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var direct = Path.Combine(dir.FullName, "1.7", "AgentSkills");
            if (Directory.Exists(direct))
            {
                return direct;
            }
            var sibling = Path.Combine(dir.FullName, "AgentSkills");
            if (Directory.Exists(sibling))
            {
                return sibling;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate 1.7/AgentSkills from AppContext.BaseDirectory.");
    }
}

public static class AgentProtocol
{
    public static readonly IReadOnlyList<string> RequiredKeys =
    [
        "LlmClient_VisionFallbackPrompt",
        "LlmClient_VisionFallbackNotice",
        "ChatWindow_ScreenshotDefaultPrompt",
        "LlmClient_AbortedToolResult",
        "TurnRunner_SubmitAlone",
        "TurnRunner_NoPendingDraft",
        "TurnRunner_RepairArgsObject",
        "TurnRunner_EvidenceIdsArray",
        "TurnRunner_SynthesisOnly",
        "TurnRunner_BudgetExhausted",
        "TurnRunner_SynthesisReasonLimit",
        "TurnRunner_SynthesisReasonTimeout",
        "TurnRunner_IncompleteTailError",
        "TurnRunner_CompactNote",
        "TurnRunner_HistoryEvidenceMarker",
        "TurnRunner_HistoryEvidenceFolded",
        "TurnRunner_AttachedImageIntro",
        "Evidence_Redacted",
        "Evidence_BudgetTruncatedNote",
        "Evidence_OmittedSuffix",
        "Evidence_ArgsMustBeObject",
        "Evidence_FoldRequiresNotesAndIds",
        "Evidence_FoldInvalidId",
        "Evidence_ReadNotFound",
        "Evidence_PathOrPathsExclusive",
        "Evidence_PathsMustBeStringArray",
        "Evidence_AllPathsFailed",
        "Evidence_PathMustBePointer",
        "Evidence_PathMissing",
        "Evidence_ArrayPagingOnlyOnArray",
        "Evidence_ArrayPagingOutOfRange",
        "Evidence_LinePagingOutOfRange",
        "Evidence_FoldedArgsMarker",
        "Evidence_DraftRetainedMarker",
        "Evidence_HistoricalIndexNote",
        "Evidence_ShallowSummaryFull",
        "History_ScreenshotPrefix",
        "History_AssistantCallsLabel",
        "History_CallFailedNote",
        "History_CallArchivedNote",
        "History_SnapshotNote",
        "History_IncompleteTurnNotice",
        "History_LacksRuntimeEvidenceNotice",
        "History_SessionArchiveNote",
        "History_SessionBudgetArchivedNote",
        "Contract_SubmitDesc",
        "Contract_EvidenceIdsDesc",
        "Contract_ArgsMustBeObject",
        "Contract_EmptyAnswer",
        "Contract_InvalidKind",
        "Contract_EvidenceIdsArray",
        "Contract_GroundedRequiresEvidence",
        "Contract_InvalidEvidencePrefix",
        "Contract_RepairDesc",
        "Discovery_UnknownToolsPrefix",
        "Discovery_GroupsPrefix",
        "Discovery_SelectedToolsDesc",
        "Discovery_ReplaceDesc",
        "Discovery_OtherGroupPurpose",
        "Validation_ArgsMustBeObject",
        "Validation_MissingParamPrefix",
        "Validation_UnknownParamPrefix",
        "Validation_InvalidTypeOrRangePrefix",
        "Validation_InvalidEnumPrefix",
        "Validation_MissingArrayItemsPrefix",
        "Registry_UnknownToolPrefix",
        "Tool_ReadEvidence_Desc",
        "Tool_ReadEvidence_PathDesc",
        "Tool_ReadEvidence_PathsDesc",
        "Tool_ReadEvidence_StartIndexDesc",
        "Tool_ReadEvidence_MaxItemsDesc",
        "Tool_FoldEvidence_Desc",
        "Probe_DuplicateDefName",
        "Probe_AmbiguousLabel",
        "Probe_InvalidRelativePath",
        "Probe_TraversalForbidden",
        "Probe_XmlOnly",
        "Probe_NotInLoadFolder",
        "Probe_ShadowedFile",
        "Probe_ReparsePointForbidden",
        "Probe_XmlTooLarge",
        "Probe_LineTooLong",
        "Probe_ModXmlNote",
        "Probe_MapSwitched",
        "Probe_BudgetTimeout",
        "Probe_EmptyQuery",
        "Probe_DisclosureLevel1",
        "Probe_DisclosureLevel2",
        "Probe_DisclosureLevel3",
        "Probe_EmptyDefName",
        "Probe_DefNotFound",
        "Probe_ExpandHint",
        "Probe_MissingField",
        "Probe_EmptyKeyword",
        "Probe_InvalidXmlScope",
        "Probe_ModNotFound",
        "Probe_ExactModNotFound",
        "Probe_XmlSearchLimitations",
        "Probe_FoldedLiSummary",
        "Probe_TruncatedTail",
        "Probe_AntiExampleBlocked",
        "Authoring_InvalidCreatureName",
        "Authoring_PathOutOfBounds",
        "Authoring_ReparseForbidden",
        "Authoring_XmlOnlyInDefs",
        "Authoring_PngOnly",
        "Authoring_ManualNotFound",
        "Authoring_ImageEndpointMissing",
        "Authoring_ImageHttpError",
        "Authoring_ImageEmptyData",
        "Authoring_BodyPartCountChanged",
        "Authoring_SpawnFailed",
        "Authoring_PromptMissingBuiltin",
        "Authoring_PromptForbiddenTerm",
        "Authoring_InteriorBlackExceeded",
        "Security_ImmutableNoUserImg2Img",
        "Authoring_UserImg2ImgForbidden",
        "Authoring_UserImg2ImgRefusalReply",
        "Authoring_ExternalReferenceBlocked"
    ];

    public static string Get(string key)
    {
        if (AgentSkillStore.Current.Protocol.TryGetValue(key, out var value))
        {
            return value;
        }
        throw new KeyNotFoundException($"Protocol key '{key}' is not defined in protocol.xml.");
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);
}
