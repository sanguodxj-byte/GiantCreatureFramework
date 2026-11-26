using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GiantCreatureFramework.Agent.Authoring;

public sealed record WorkspaceCreatureEntry(
    string CreatureName,
    int DefFileCount,
    int TextureFileCount,
    int WorkFileCount);

public sealed record WorkspaceFileListing(
    string CreatureName,
    string RootPath,
    IReadOnlyList<string> DefFiles,
    IReadOnlyList<string> TextureFiles,
    IReadOnlyList<string> WorkFiles);

/// <summary>
/// Pure-C# sandboxed file workspace manager for Config/GiantCreatureFramework/Creatures/&lt;creatureName&gt;.
/// Enforces strict creatureName syntax, path containment, ReparsePoint rejection, Defs/*.xml root checks,
/// and .png extension checks.
/// </summary>
public sealed class CreatureWorkspace
{
    private static readonly Regex CreatureNameRegex = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Defs",
        "Textures",
        "Work",
        "Patches",
        "Languages"
    };

    private readonly string _creaturesRoot;

    public CreatureWorkspace(string creaturesRoot)
    {
        if (string.IsNullOrWhiteSpace(creaturesRoot))
        {
            throw new ArgumentException("Creatures root path cannot be empty.", nameof(creaturesRoot));
        }
        _creaturesRoot = Path.GetFullPath(creaturesRoot);
    }

    public string CreaturesRoot => _creaturesRoot;

    public static void ValidateCreatureName(string? creatureName)
    {
        if (string.IsNullOrWhiteSpace(creatureName) ||
            !CreatureNameRegex.IsMatch(creatureName.Trim()) ||
            ReservedNames.Contains(creatureName.Trim()))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_InvalidCreatureName"));
        }
    }

    public string GetCreatureDir(string creatureName, bool createIfMissing = false)
    {
        ValidateCreatureName(creatureName);
        var clean = creatureName.Trim();
        var dir = Path.GetFullPath(Path.Combine(_creaturesRoot, clean));
        EnsureInsideRoot(dir, _creaturesRoot);
        EnsureNoReparsePoints(dir, _creaturesRoot);

        if (createIfMissing)
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "Defs"));
            Directory.CreateDirectory(Path.Combine(dir, "Textures"));
            Directory.CreateDirectory(Path.Combine(dir, "Work"));
            EnsureNoReparsePoints(dir, _creaturesRoot);
        }

        return dir;
    }

    public IReadOnlyList<WorkspaceCreatureEntry> ListAllCreatures()
    {
        if (!Directory.Exists(_creaturesRoot))
        {
            return Array.Empty<WorkspaceCreatureEntry>();
        }

        var result = new List<WorkspaceCreatureEntry>();
        foreach (var dir in Directory.EnumerateDirectories(_creaturesRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            if (!CreatureNameRegex.IsMatch(info.Name) || ReservedNames.Contains(info.Name))
            {
                continue;
            }

            result.Add(new WorkspaceCreatureEntry(
                info.Name,
                CountFilesSafe(Path.Combine(dir, "Defs"), "*.xml"),
                CountFilesSafe(Path.Combine(dir, "Textures"), "*.png"),
                CountFilesSafe(Path.Combine(dir, "Work"), "*")));
        }

        return result;
    }

    public WorkspaceFileListing ListCreatureFiles(string creatureName)
    {
        var creatureDir = GetCreatureDir(creatureName, createIfMissing: false);
        if (!Directory.Exists(creatureDir))
        {
            return new WorkspaceFileListing(
                creatureName.Trim(),
                creatureDir,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        return new WorkspaceFileListing(
            creatureName.Trim(),
            creatureDir,
            EnumerateRelativeFiles(creatureDir, "Defs", "*.xml"),
            EnumerateRelativeFiles(creatureDir, "Textures", "*.png"),
            EnumerateRelativeFiles(creatureDir, "Work", "*"));
    }

    public string ResolveWorkspacePath(string creatureName, string relativePath, bool createParentDirs = false)
    {
        var creatureDir = GetCreatureDir(creatureName, createIfMissing: createParentDirs);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
        }

        var full = Path.GetFullPath(Path.Combine(creatureDir, Path.Combine(segments)));
        EnsureInsideRoot(full, creatureDir);
        EnsureNoReparsePoints(full, _creaturesRoot);

        if (createParentDirs)
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
                EnsureNoReparsePoints(parent, _creaturesRoot);
            }
        }

        return full;
    }

    public string ResolveDefsXmlPath(string creatureName, string relativeXmlPath, bool createParentDirs = false)
    {
        if (string.IsNullOrWhiteSpace(relativeXmlPath))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_XmlOnlyInDefs"));
        }

        var normalized = relativeXmlPath.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("Defs/", StringComparison.OrdinalIgnoreCase) && !normalized.Contains('/'))
        {
            normalized = "Defs/" + normalized;
        }

        if (!normalized.StartsWith("Defs/", StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_XmlOnlyInDefs"));
        }

        var full = ResolveWorkspacePath(creatureName, normalized, createParentDirs);
        var defsDir = Path.Combine(GetCreatureDir(creatureName, createIfMissing: createParentDirs), "Defs");
        EnsureInsideRoot(full, defsDir);
        return full;
    }

    public string ResolvePngPath(string creatureName, string relativePngPath, bool requireUnderTextures = false, bool createParentDirs = false)
    {
        if (string.IsNullOrWhiteSpace(relativePngPath) ||
            !relativePngPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PngOnly"));
        }

        var full = ResolveWorkspacePath(creatureName, relativePngPath, createParentDirs);
        if (requireUnderTextures)
        {
            var texDir = Path.Combine(GetCreatureDir(creatureName, createIfMissing: createParentDirs), "Textures");
            if (!IsInside(full, texDir))
            {
                throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
            }
        }

        return full;
    }

    public string ReadCreatureXmlWithLineNumbers(string creatureName, string relativeXmlPath, out string canonicalRelativePath, out int lineCount)
    {
        var full = ResolveDefsXmlPath(creatureName, relativeXmlPath, createParentDirs: false);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"XML file not found: {relativeXmlPath}", full);
        }

        var creatureDir = GetCreatureDir(creatureName, createIfMissing: false);
        canonicalRelativePath = Path.GetRelativePath(creatureDir, full).Replace('\\', '/');
        var lines = File.ReadAllLines(full, Encoding.UTF8);
        lineCount = lines.Length;
        var sb = new StringBuilder(lines.Length * 48);
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(i + 1).Append(": ").AppendLine(lines[i]);
        }
        return sb.ToString();
    }

    public (string FullPath, string RelativePath, int DefElementCount) WriteCreatureXml(string creatureName, string relativeXmlPath, string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            throw new ArgumentException("xmlContent cannot be empty.");
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Invalid XML syntax: {ex.Message}", ex);
        }

        if (doc.Root == null || !string.Equals(doc.Root.Name.LocalName, "Defs", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Root XML element must be <Defs>.");
        }

        // Also guard against copying built-in anti-example defNames into a custom creature file
        foreach (var el in doc.Root.Elements())
        {
            var dn = el.Element("defName")?.Value;
            if (Probes.BuiltInGiantAntiExampleGuard.IsBuiltInAntiExampleQuery(dn, out var antiToken))
            {
                throw new InvalidOperationException(AgentProtocol.Format("Probe_AntiExampleBlocked", antiToken));
            }
        }

        var full = ResolveDefsXmlPath(creatureName, relativeXmlPath, createParentDirs: true);
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(full, xmlContent.Replace("\r\n", "\n"), utf8NoBom);

        var creatureDir = GetCreatureDir(creatureName, createIfMissing: false);
        var rel = Path.GetRelativePath(creatureDir, full).Replace('\\', '/');
        var count = doc.Root.Elements().Count();
        return (full, rel, count);
    }

    public string ToRelativeWorkspacePath(string creatureName, string fullPath)
    {
        var creatureDir = GetCreatureDir(creatureName, createIfMissing: false);
        EnsureInsideRoot(fullPath, creatureDir);
        return Path.GetRelativePath(creatureDir, fullPath).Replace('\\', '/');
    }

    public string ToRimWorldTexPath(string creatureName, string fullTexturePngPath)
    {
        var texRoot = Path.Combine(GetCreatureDir(creatureName, createIfMissing: false), "Textures");
        EnsureInsideRoot(fullTexturePngPath, texRoot);
        var rel = Path.GetRelativePath(texRoot, fullTexturePngPath);
        return Path.ChangeExtension(rel, null).Replace('\\', '/');
    }

    private static int CountFilesSafe(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(dir, pattern, opts)
            .Count(f => !Path.GetFileName(f).StartsWith(".gcf_", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> EnumerateRelativeFiles(string creatureDir, string subFolder, string pattern)
    {
        var target = Path.Combine(creatureDir, subFolder);
        if (!Directory.Exists(target)) return Array.Empty<string>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(target, pattern, opts)
            .Where(f => !Path.GetFileName(f).StartsWith(".gcf_", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(creatureDir, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string ResolveSandboxedTexturePng(
        string texturesDir,
        string texRel,
        bool allowBaseNameFallback = false,
        bool requireExists = true)
    {
        if (string.IsNullOrWhiteSpace(texturesDir) || string.IsNullOrWhiteSpace(texRel))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
        }

        var trimmed = texRel.Trim();
        if (Path.IsPathRooted(trimmed) ||
            trimmed.Contains(':') ||
            trimmed.StartsWith("\\\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
        }

        var segments = trimmed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
        }

        var last = segments[^1];
        if (!last.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            segments[^1] = last + ".png";
        }

        var normalizedRoot = Path.GetFullPath(texturesDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var requiredPrefix = normalizedRoot + Path.DirectorySeparatorChar;

        string ValidateCandidate(string rawPath)
        {
            var full = Path.GetFullPath(rawPath);
            if (!full.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
            }
            EnsureNoReparsePoints(full, normalizedRoot);
            return full;
        }

        var direct = ValidateCandidate(Path.Combine(normalizedRoot, Path.Combine(segments)));
        if (File.Exists(direct))
        {
            return direct;
        }

        if (allowBaseNameFallback && segments.Length > 1)
        {
            var baseOnly = ValidateCandidate(Path.Combine(normalizedRoot, segments[^1]));
            if (File.Exists(baseOnly))
            {
                return baseOnly;
            }
        }

        if (requireExists)
        {
            throw new FileNotFoundException($"Missing texture PNG for '{texRel}' under {texturesDir}", direct);
        }

        return direct;
    }

    private static bool IsInside(string fullPath, string rootDir)
    {
        var normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(fullPath);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureInsideRoot(string fullPath, string rootDir)
    {
        if (!IsInside(fullPath, rootDir))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_PathOutOfBounds"));
        }
    }

    private static void EnsureNoReparsePoints(string targetPath, string stopAtRoot)
    {
        var normalizedStop = Path.GetFullPath(stopAtRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var cur = Path.GetFullPath(targetPath);
             !string.IsNullOrEmpty(cur) && cur.Length >= normalizedStop.Length;
             cur = Path.GetDirectoryName(cur)!)
        {
            if (File.Exists(cur) || Directory.Exists(cur))
            {
                var attrs = File.GetAttributes(cur);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(AgentProtocol.Get("Authoring_ReparseForbidden"));
                }
            }
            if (string.Equals(cur.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedStop, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }
}
