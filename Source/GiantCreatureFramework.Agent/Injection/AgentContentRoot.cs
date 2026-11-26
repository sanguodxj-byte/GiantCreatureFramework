using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// Disk root of agent-authored creatures. Both the shared root and per-creature isolated
/// workspaces (Creatures/&lt;creatureName&gt;) are appended to the owning mod's load folders, so
/// content already on disk at startup goes through the normal mod loading pipeline even when
/// the authoring terminal is disabled, and content written during play is found by texture lookups.
/// </summary>
public static class AgentContentRoot
{
    private static readonly Regex ValidCreatureDirRegex = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedSubdirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Defs",
        "Textures",
        "Work",
        "Patches",
        "Languages"
    };

    private static readonly HashSet<(string Element, string DefName)> StartupDefNames = new();
    private static readonly Dictionary<string, string> StartupHashes = new(StringComparer.OrdinalIgnoreCase);

    public static ModContentPack? Owner { get; private set; }

    public static string RootPath => Path.Combine(GenFilePaths.ConfigFolderPath, "GiantCreatureFramework", "Creatures");
    public static string DefsPath => Path.Combine(RootPath, "Defs");
    public static string TexturesPath => Path.Combine(RootPath, "Textures");

    /// <summary>Content hash of every Def file that the startup load read from this root, keyed by full path.</summary>
    public static IReadOnlyDictionary<string, string> StartupFileHashes => StartupHashes;

    public static bool IsValidCreatureName(string? creatureName) =>
        !string.IsNullOrWhiteSpace(creatureName)
        && ValidCreatureDirRegex.IsMatch(creatureName)
        && !ReservedSubdirs.Contains(creatureName);

    public static string GetCreatureRoot(string creatureName)
    {
        if (!IsValidCreatureName(creatureName))
        {
            throw new ArgumentException(AgentProtocol.Get("Authoring_InvalidCreatureName"), nameof(creatureName));
        }
        return Path.Combine(RootPath, creatureName.Trim());
    }

    public static string GetCreatureDefsPath(string creatureName) => Path.Combine(GetCreatureRoot(creatureName), "Defs");
    public static string GetCreatureTexturesPath(string creatureName) => Path.Combine(GetCreatureRoot(creatureName), "Textures");

    public static IEnumerable<string> CreatureDirectories()
    {
        if (!Directory.Exists(RootPath))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(RootPath).OrderBy(d => d, StringComparer.Ordinal))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            if (!IsValidCreatureName(info.Name))
            {
                continue;
            }
            yield return dir;
        }
    }

    public static IEnumerable<string> AllTextureRoots(string? creatureName = null)
    {
        if (!string.IsNullOrWhiteSpace(creatureName))
        {
            yield return GetCreatureTexturesPath(creatureName.Trim());
            yield break;
        }

        if (Directory.Exists(TexturesPath))
        {
            yield return TexturesPath;
        }

        foreach (var dir in CreatureDirectories())
        {
            var texDir = Path.Combine(dir, "Textures");
            if (Directory.Exists(texDir))
            {
                yield return texDir;
            }
        }
    }

    public static bool HasExistingAuthoredContentOnDisk()
    {
        if (!Directory.Exists(RootPath))
        {
            return false;
        }
        if (CreatureDirectories().Any())
        {
            return true;
        }
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        if (Directory.Exists(DefsPath) && Directory.EnumerateFiles(DefsPath, "*.xml", enumOpts).Any())
        {
            return true;
        }
        if (Directory.Exists(TexturesPath) && Directory.EnumerateFiles(TexturesPath, "*.png", enumOpts).Any())
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Must run in the mod class constructor: that is after load folders are initialised and
    /// before Defs and textures are read, which is the only window in which the root and
    /// per-creature folders can still join the startup load.
    /// </summary>
    public static void Register(ModContentPack owner)
    {
        Owner = owner;
        StartupDefNames.Clear();
        StartupHashes.Clear();
        if (!GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal && !HasExistingAuthoredContentOnDisk())
        {
            return;
        }
        EnsureLoadFolder();
        InvalidateStartupTexturePrefetch(owner);
        GcfRuntimeTextures.SeedFromDisk(AllTextureRoots());
        foreach (var file in DefFiles())
        {
            try
            {
                StartupHashes[file] = HashOf(file);
                foreach (var element in XDocument.Load(file).Root?.Elements() ?? [])
                {
                    var defName = element.Element("defName")?.Value;
                    if (!string.IsNullOrWhiteSpace(defName))
                    {
                        var trimmedName = defName.Trim();
                        StartupDefNames.Add((element.Name.LocalName, trimmedName));
                        var classAttr = element.Attribute("Class")?.Value?.Trim();
                        if (!string.IsNullOrWhiteSpace(classAttr))
                        {
                            StartupDefNames.Add((classAttr, trimmedName));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable files are reported by the game's own loader; here they only stay out of the ownership set.
            }
        }
    }

    public static void EnsureLoadFolder(string? creatureName = null)
    {
        var folders = Owner?.foldersToLoadDescendingOrder;
        if (folders == null)
        {
            return;
        }

        AddLoadFolderIfMissing(folders, RootPath);

        if (!string.IsNullOrWhiteSpace(creatureName))
        {
            var creatureDir = GetCreatureRoot(creatureName.Trim());
            AddLoadFolderIfMissing(folders, creatureDir);
            return;
        }

        foreach (var dir in CreatureDirectories())
        {
            AddLoadFolderIfMissing(folders, dir);
        }
    }

    private static void AddLoadFolderIfMissing(List<string> folders, string dir)
    {
        var normalized = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < folders.Count; i++)
        {
            var existing = folders[i];
            if (!string.IsNullOrWhiteSpace(existing) &&
                string.Equals(
                    Path.GetFullPath(existing).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        folders.Add(normalized);
    }

    /// <summary>
    /// RimWorld 1.7 LoadedModManager.LoadModContent() runs PrefetchPaths() before CreateModClasses()
    /// constructs mod classes, caching a texture/audio scan of foldersToLoadDescendingOrder before
    /// Config/GiantCreatureFramework/Creatures folders are appended. Removing the stale prefetch entry
    /// lets ReloadAll() scan the newly registered creature folders natively during startup.
    /// </summary>
    private static void InvalidateStartupTexturePrefetch(ModContentPack owner)
    {
        try
        {
            if (AccessTools.Field(typeof(ModContentLoader<Texture2D>), "prefetchScans")?.GetValue(null)
                is ConcurrentDictionary<ModContentPack, Dictionary<string, FileInfo>> texScans)
            {
                texScans.TryRemove(owner, out _);
            }
            if (AccessTools.Field(typeof(ModContentLoader<AudioClip>), "prefetchScans")?.GetValue(null)
                is ConcurrentDictionary<ModContentPack, Dictionary<string, FileInfo>> audioScans)
            {
                audioScans.TryRemove(owner, out _);
            }
        }
        catch
        {
            // Non-fatal: WarmStartupCreatures() still syncs any missing textures on startup.
        }
    }

    /// <summary>
    /// Returns every Def XML file under Config/GiantCreatureFramework/Creatures that was not already
    /// included in loadedAssets (for example, when two creature folders both contain Defs/Rig.xml and
    /// RimWorld's DirectXmlLoader relative-path dictionary dropped the second creature's file).
    /// </summary>
    internal static List<string> FindUnloadedStartupDefFiles(IEnumerable<LoadableXmlAsset>? loadedAssets)
    {
        var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (loadedAssets != null)
        {
            foreach (var asset in loadedAssets)
            {
                if (asset != null && !string.IsNullOrWhiteSpace(asset.FullFilePath))
                {
                    loadedPaths.Add(Path.GetFullPath(asset.FullFilePath));
                }
            }
        }

        var missing = new List<string>();
        foreach (var file in DefFiles())
        {
            var full = Path.GetFullPath(file);
            if (loadedPaths.Add(full))
            {
                missing.Add(full);
            }
        }
        return missing;
    }

    public static IEnumerable<string> DefFiles(string? creatureName = null)
    {
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        if (!string.IsNullOrWhiteSpace(creatureName))
        {
            var cDefs = GetCreatureDefsPath(creatureName.Trim());
            return Directory.Exists(cDefs)
                ? Directory.EnumerateFiles(cDefs, "*.xml", enumOpts).OrderBy(f => f, StringComparer.Ordinal)
                : [];
        }

        var results = new List<string>();
        if (Directory.Exists(DefsPath))
        {
            results.AddRange(Directory.EnumerateFiles(DefsPath, "*.xml", enumOpts));
        }

        foreach (var dir in CreatureDirectories())
        {
            var cDefs = Path.Combine(dir, "Defs");
            if (Directory.Exists(cDefs))
            {
                results.AddRange(Directory.EnumerateFiles(cDefs, "*.xml", enumOpts));
            }
        }

        return results.OrderBy(f => f, StringComparer.Ordinal);
    }

    public static string HashOf(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>True when a Def already in the database was read from this root during startup.</summary>
    public static bool WasLoadedAtStartup(Def def)
    {
        var type = def.GetType();
        return def.modContentPack == Owner
            && (StartupDefNames.Contains((type.Name, def.defName)) || StartupDefNames.Contains((type.FullName ?? type.Name, def.defName)));
    }
}

/// <summary>
/// Prevents RimWorld's DirectXmlLoader relative-path deduplication across foldersToLoadDescendingOrder
/// from silently dropping identically named Def XML files (such as Defs/Rig.xml or Defs/Bodies.xml)
/// stored in different Config/GiantCreatureFramework/Creatures/&lt;creatureName&gt; workspaces at startup.
/// </summary>
[HarmonyPatch(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder))]
internal static class Patch_DirectXmlLoader_XmlAssetsInModFolder
{
    [HarmonyPostfix]
    public static void Postfix(
        ModContentPack mod,
        string folderPath,
        List<string>? foldersToLoadDebug,
        ref LoadableXmlAsset[] __result)
    {
        if (mod == null || !ReferenceEquals(mod, AgentContentRoot.Owner) || foldersToLoadDebug != null)
        {
            return;
        }

        var normalizedFolder = (folderPath ?? string.Empty).Trim('/', '\\');
        if (!string.Equals(normalizedFolder, "Defs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var extraFiles = AgentContentRoot.FindUnloadedStartupDefFiles(__result);
        if (extraFiles.Count == 0)
        {
            return;
        }

        var merged = new List<LoadableXmlAsset>((__result?.Length ?? 0) + extraFiles.Count);
        if (__result != null)
        {
            merged.AddRange(__result);
        }

        foreach (var file in extraFiles)
        {
            merged.Add(new LoadableXmlAsset(new FileInfo(file), mod));
        }

        __result = merged.ToArray();
    }
}
