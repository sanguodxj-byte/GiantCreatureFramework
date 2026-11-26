using System.Text;
using System.Text.Json;

namespace GiantCreatureFramework.Agent.Probes;

/// <summary>
/// Pure-C# guard preventing runtime probes from exposing the 7 built-in GiantCreatureFramework
/// anti-example giant creatures (their Defs, labels, and XML files), while keeping vanilla/DLC
/// Defs, framework shared infrastructure XMLs, and user/agent workspace creatures accessible.
/// </summary>
public static class BuiltInGiantAntiExampleGuard
{
    private static readonly string[] BuiltInGiantTokens =
    [
        "MechScorpion",
        "AbyssalDragonBeast",
        "Behemoth",
        "MechColossus",
        "ProtossCarrier",
        "CarrierInterceptor",
        "PanzerIV",
        "Rathalos",
        "Valstrax"
    ];

    private static readonly string[] BuiltInGiantLabels =
    [
        "机械巨蝎",
        "渊骸龙兽",
        "巨兽比蒙",
        "机械巨像",
        "神族航母",
        "航母拦截机",
        "四号坦克",
        "雄火龙",
        "天彗龙"
    ];

    public static bool IsBuiltInAntiExampleQuery(string? text, out string matchedToken)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            matchedToken = string.Empty;
            return false;
        }

        foreach (var token in BuiltInGiantTokens)
        {
            if (raw.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedToken = token;
                return true;
            }
        }

        foreach (var label in BuiltInGiantLabels)
        {
            if (raw.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                matchedToken = label;
                return true;
            }
        }

        matchedToken = string.Empty;
        return false;
    }

    public static bool IsBuiltInAntiExampleDef(
        string? defName,
        string? label,
        string? packageId,
        string? fileName,
        bool isAgentAuthored)
    {
        if (isAgentAuthored)
        {
            return false;
        }

        var isFrameworkPack = !string.IsNullOrWhiteSpace(packageId)
            && packageId.Contains("giantcreatureframework", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(defName))
        {
            foreach (var token in BuiltInGiantTokens)
            {
                if (defName.Contains(token, StringComparison.OrdinalIgnoreCase) &&
                    (isFrameworkPack || defName.StartsWith("GCF_", StringComparison.OrdinalIgnoreCase) ||
                     defName.StartsWith("Corpse_GCF_", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        if (isFrameworkPack && !string.IsNullOrWhiteSpace(fileName))
        {
            foreach (var token in BuiltInGiantTokens)
            {
                if (fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (isFrameworkPack && !string.IsNullOrWhiteSpace(label))
        {
            foreach (var cjk in BuiltInGiantLabels)
            {
                if (label.Contains(cjk, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsBuiltInAntiExampleXmlPath(string? packageIdOrRoot, string? relativeOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFullPath))
        {
            return false;
        }

        var normalizedPath = relativeOrFullPath.Replace('\\', '/');
        if (normalizedPath.Contains("/Creatures/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isFramework = string.IsNullOrWhiteSpace(packageIdOrRoot)
            || packageIdOrRoot.Contains("giantcreatureframework", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("GiantCreatureFramework", StringComparison.OrdinalIgnoreCase);

        if (!isFramework)
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        foreach (var token in BuiltInGiantTokens)
        {
            if (fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Read a known active XML file, never arbitrary configuration, credentials, shadowed versions, or built-in anti-example giants.</summary>
public static class ModFileReader
{
    public static string Read(
        string rootDirectory,
        IReadOnlyList<string> loadFolders,
        string relativePath,
        int startLine = 1,
        int maxLines = 80,
        CancellationToken cancellationToken = default,
        string? packageId = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new ArgumentException(AgentProtocol.Get("Probe_InvalidRelativePath"));
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is ".." or "."))
            throw new ArgumentException(AgentProtocol.Get("Probe_TraversalForbidden"));
        relativePath = string.Join("/", segments);

        if (BuiltInGiantAntiExampleGuard.IsBuiltInAntiExampleXmlPath(packageId ?? rootDirectory, relativePath))
            throw new InvalidOperationException(AgentProtocol.Format("Probe_AntiExampleBlocked", Path.GetFileName(relativePath)));

        var root = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsUnder(fullPath, root) || !fullPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(AgentProtocol.Get("Probe_XmlOnly"));

        var areas = new[] { "Defs", "Patches", "Languages" };
        string? activePath = null;
        foreach (var folder in loadFolders)
        {
            foreach (var area in areas)
            {
                var areaRoot = Path.GetFullPath(Path.Combine(folder, area));
                if (!IsUnder(fullPath, areaRoot)) continue;
                var contentRelative = Path.GetRelativePath(Path.GetFullPath(folder), fullPath);
                activePath = loadFolders.Select(f => Path.GetFullPath(Path.Combine(f, contentRelative))).FirstOrDefault(File.Exists);
                break;
            }
            if (activePath != null) break;
        }
        if (activePath == null || !File.Exists(fullPath))
            throw new FileNotFoundException(AgentProtocol.Get("Probe_NotInLoadFolder"), relativePath);
        if (!string.Equals(fullPath, activePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(AgentProtocol.Get("Probe_ShadowedFile"));

        for (var current = fullPath; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(AgentProtocol.Get("Probe_ReparsePointForbidden"));
        }
        if (new FileInfo(fullPath).Length > 16_000_000)
            throw new InvalidDataException(AgentProtocol.Get("Probe_XmlTooLarge"));

        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, 160);
        using var reader = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var output = new StringBuilder();
        var lineNumber = 0;
        var returned = 0;
        var nextLine = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lineNumber < startLine) continue;
            if (returned >= maxLines || (returned > 0 && output.Length + line.Length + 16 > 5000))
            {
                nextLine = lineNumber;
                break;
            }
            if (line.Length > 5000)
                throw new InvalidDataException(AgentProtocol.Format("Probe_LineTooLong", lineNumber));
            output.Append(lineNumber).Append(": ").AppendLine(line);
            returned++;
        }

        return JsonSerializer.Serialize(new
        {
            relativeXmlPath = relativePath,
            startLine,
            returnedLines = returned,
            nextLine = nextLine > 0 ? (int?)nextLine : null,
            eof = nextLine == 0,
            evidenceKind = "activeModXmlFile",
            sourceText = output.ToString(),
            note = AgentProtocol.Get("Probe_ModXmlNote")
        }, Evidence.UnescapedJsonOptions);
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
