using System.Text.RegularExpressions;
using System.Xml;
using Verse;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// Finds abstract XML parent nodes (ParentName targets) in the Def files of the official content
/// and of the framework itself. The game discards its inheritance registry after startup, so a
/// node parsed during play has to bring its parents along. Third-party mods are not searched:
/// reading every active mod's XML per injection is what a full reload does and is too slow.
/// </summary>
internal sealed class ParentNodeLocator(IReadOnlyCollection<ModContentPack> preferredPacks, string excludedRoot)
{
    private static readonly Regex NameAttribute = new("\\sName\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private readonly Dictionary<string, XmlDocument?> _documents = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? _filesByName;

    public XmlNode? Find(string name)
    {
        _filesByName ??= BuildIndex();
        if (!_filesByName.TryGetValue(name, out var files))
        {
            return null;
        }
        foreach (var file in files)
        {
            if (!_documents.TryGetValue(file, out var document))
            {
                document = new LoadableXmlAsset(new FileInfo(file), null).xmlDoc;
                _documents[file] = document;
            }
            if (document?.DocumentElement == null)
            {
                continue;
            }
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType == XmlNodeType.Element && node.Attributes?["Name"]?.Value == name)
                {
                    return node;
                }
            }
        }
        return null;
    }

    internal static bool IsExcludedFolder(string folder, string excludedRoot)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(excludedRoot))
        {
            return false;
        }
        var normalizedExcluded = Path.GetFullPath(excludedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedFolder, normalizedExcluded, StringComparison.OrdinalIgnoreCase) ||
               normalizedFolder.StartsWith(normalizedExcluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Candidate order mirrors the game's parent choice for a late-loading mod: its own nodes
    // first, then the latest-loaded official content that defines the name.
    private Dictionary<string, List<string>> BuildIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var packs = preferredPacks.Concat(LoadedModManager.RunningModsListForReading
            .Where(m => (m.IsOfficialMod || m.IsCoreMod) && !preferredPacks.Contains(m))
            .OrderByDescending(m => m.loadOrder));
        foreach (var pack in packs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in pack.foldersToLoadDescendingOrder)
            {
                var defs = Path.Combine(folder, "Defs");
                if (IsExcludedFolder(folder, excludedRoot) || !Directory.Exists(defs))
                {
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(defs, "*.xml", SearchOption.AllDirectories))
                {
                    // A file shadowed by a higher-priority load folder is never read by the game.
                    if (!seen.Add(Path.GetRelativePath(folder, file)))
                    {
                        continue;
                    }
                    foreach (Match match in NameAttribute.Matches(File.ReadAllText(file)))
                    {
                        var name = match.Groups[1].Value;
                        if (!index.TryGetValue(name, out var files))
                        {
                            index[name] = files = new List<string>();
                        }
                        if (!files.Contains(file))
                        {
                            files.Add(file);
                        }
                    }
                }
            }
        }
        return index;
    }
}
