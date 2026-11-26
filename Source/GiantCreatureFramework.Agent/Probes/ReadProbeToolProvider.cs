using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent.Probes;

/// <summary>
/// Exposes the 5 read-only runtime probe tools:
/// search_defs, get_def_details, search_mod_xml_and_patches, read_mod_file, get_recent_errors.
/// </summary>
public sealed class ReadProbeToolProvider : IAgentToolProvider
{
    private static readonly string[] ToolNames =
    [
        "search_defs",
        "get_def_details",
        "search_mod_xml_and_patches",
        "read_mod_file",
        "get_recent_errors"
    ];

    public IReadOnlyList<JsonObject> GetSchemas()
    {
        var schemas = AgentSkillStore.Current.ToolSchemas;
        var list = new List<JsonObject>(ToolNames.Length);
        foreach (var name in ToolNames)
        {
            if (!schemas.TryGetValue(name, out var schema))
            {
                throw new KeyNotFoundException($"Missing tool schema '{name}' in tools.json.");
            }
            list.Add((JsonObject)schema.DeepClone());
        }
        return list;
    }

    public bool CanExecute(string toolName) => ToolNames.Contains(toolName, StringComparer.Ordinal);

    public string? GetDisplayLabelKey(string toolName) => toolName switch
    {
        "search_defs" => "GCFAgent_ToolLabel_SearchDefs",
        "get_def_details" => "GCFAgent_ToolLabel_DefDetails",
        "search_mod_xml_and_patches" => "GCFAgent_ToolLabel_SearchModXml",
        "read_mod_file" => "GCFAgent_ToolLabel_ReadModFile",
        "get_recent_errors" => "GCFAgent_ToolLabel_RecentErrors",
        _ => null
    };

    public async Task<string> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var settings = GiantCreatureAgentMod.Settings;
        var mainBudget = settings.MainThreadProbeBudgetMs;
        var bgBudget = settings.BackgroundProbeBudgetMs;

        switch (toolName)
        {
            case "search_defs":
            {
                var query = GetString(arguments, "query");
                var defTypeFilter = GetOptionalString(arguments, "defTypeFilter");
                var limit = GetInt(arguments, "limit", 20);
                return await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: true, mainBudget, bgBudget);
                    return RuntimeReadProbes.SearchDefsJson(query, defTypeFilter, limit);
                }, cancellationToken).ConfigureAwait(false);
            }

            case "get_def_details":
            {
                var defName = GetString(arguments, "defName");
                var defType = GetOptionalString(arguments, "defType");
                var fields = GetOptionalString(arguments, "fields");
                return await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: true, mainBudget, bgBudget);
                    return RuntimeReadProbes.GetDefDetailsJson(defName, defType, fields);
                }, cancellationToken).ConfigureAwait(false);
            }

            case "search_mod_xml_and_patches":
            {
                var keyword = GetString(arguments, "keyword");
                var scope = GetOptionalString(arguments, "scope") ?? "all";
                var modFilter = GetOptionalString(arguments, "modFilter");
                var startLine = GetInt(arguments, "startLine", 0);
                var maxMatches = GetInt(arguments, "maxMatches", 25);

                var mods = await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: true, mainBudget, bgBudget);
                    return RuntimeReadProbes.CaptureActiveModsOnMainThread();
                }, cancellationToken).ConfigureAwait(false);

                return await Task.Run(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: false, mainBudget, bgBudget);
                    return RuntimeReadProbes.SearchModXmlAndPatchesOnDiskJson(
                        mods, keyword, scope, maxMatches, modFilter, startLine, settings.MaxXmlScanFiles);
                }, cancellationToken).ConfigureAwait(false);
            }

            case "read_mod_file":
            {
                var packageId = GetString(arguments, "packageId").Trim();
                var relativeXmlPath = GetString(arguments, "relativeXmlPath").Trim();
                var startLine = GetInt(arguments, "startLine", 1);
                var maxLines = GetInt(arguments, "maxLines", 80);

                if (CounterExampleGuard.IsForbiddenCounterExampleModFile(packageId, relativeXmlPath))
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = AgentProtocol.Format("Probe_AntiExampleBlocked", relativeXmlPath)
                    }, Evidence.UnescapedJsonOptions);
                }

                var mods = await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: true, mainBudget, bgBudget);
                    return RuntimeReadProbes.CaptureActiveModsOnMainThread();
                }, cancellationToken).ConfigureAwait(false);

                var exactMod = mods.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
                if (exactMod == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = AgentProtocol.Get("Probe_ExactModNotFound"),
                        packageId
                    }, Evidence.UnescapedJsonOptions);
                }

                return await Task.Run(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: false, mainBudget, bgBudget);
                    return ModFileReader.Read(exactMod.RootDir, exactMod.LoadFolders, relativeXmlPath, startLine, maxLines, cancellationToken, exactMod.PackageId);
                }, cancellationToken).ConfigureAwait(false);
            }

            case "get_recent_errors":
            {
                var maxCount = GetInt(arguments, "maxCount", 12);
                var keyword = GetOptionalString(arguments, "keyword");
                return await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    using var exec = new ProbeExecution(cancellationToken, mainThread: true, mainBudget, bgBudget);
                    return RuntimeLogCapture.GetRecentErrorsJson(maxCount, keyword);
                }, cancellationToken).ConfigureAwait(false);
            }

            default:
                throw new ArgumentException(AgentProtocol.Get("Registry_UnknownToolPrefix") + toolName);
        }
    }

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int GetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var el) && el.TryGetInt32(out var val) ? val : fallback;
}
