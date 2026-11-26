using System.Reflection;
using HarmonyLib;
using KTrie;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// Makes PNG files written under the agent content root (and per-creature Textures folders)
/// visible to the game's path-based texture lookup without a restart, and invalidates material
/// and alpha-mask caches via public GiantCreatureFramework APIs.
/// </summary>
[StaticConstructorOnStartup]
public static class GcfRuntimeTextures
{
    private static readonly Dictionary<string, (long Length, DateTime WriteUtc, string FullPath)> Stamps = new(StringComparer.Ordinal);
    // A replaced texture may still be bound to materials of pawns that have not rebuilt their
    // graphics yet, so it is destroyed one sync later instead of immediately.
    private static readonly List<Texture2D> Retired = new();

    private static readonly FieldInfo? TrieField = AccessTools.Field(typeof(ModContentHolder<Texture2D>), "contentListTrie");
    private static readonly FieldInfo? GraphicsField = AccessTools.Field(typeof(GraphicDatabase), "allGraphics");
    private static readonly FieldInfo? MaterialsField = AccessTools.Field(typeof(MaterialPool), "matDictionary");
    private static readonly FieldInfo? MaterialsReverseField = AccessTools.Field(typeof(MaterialPool), "matDictionaryReverse");

    public static void SeedFromDisk(string texturesRoot) => SeedFromDisk([texturesRoot]);

    /// <summary>Records files that exist at startup; the normal mod content load reads those, so the first sync must not load them again.</summary>
    public static void SeedFromDisk(IEnumerable<string> texturesRoots)
    {
        Stamps.Clear();
        foreach (var root in texturesRoots)
        {
            foreach (var (key, file) in Enumerate(root))
            {
                var (len, utc) = StampOf(file);
                Stamps[key] = (len, utc, Path.GetFullPath(file));
            }
        }
    }

    public static List<string> Sync(ModContentPack owner, string texturesRoot, InjectionReport report) =>
        Sync(owner, [texturesRoot], report);

    /// <summary>
    /// Validates new or modified PNG files off-holder for dry-run checks without mutating
    /// ModContentHolder&lt;Texture2D&gt;, invalidating live caches, or destroying Retired textures.
    /// </summary>
    public static List<string> ValidateOnly(ModContentPack owner, IEnumerable<string> texturesRoots, InjectionReport report)
    {
        var pending = new List<string>();
        var holder = owner.GetContentHolder<Texture2D>();

        foreach (var root in texturesRoots)
        {
            foreach (var (key, file) in Enumerate(root))
            {
                var fullPath = Path.GetFullPath(file);
                var (len, utc) = StampOf(file);
                if (Stamps.TryGetValue(key, out var known) &&
                    known.Length == len &&
                    known.WriteUtc == utc &&
                    string.Equals(known.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    holder.contentList.ContainsKey(key))
                {
                    continue;
                }

                Texture2D? texture = null;
                try
                {
                    texture = Texture2D.LoadFromFile(file);
                    if (texture == null)
                    {
                        report.Error("texture_unreadable", key);
                        continue;
                    }
                    pending.Add(key);
                }
                catch (Exception ex)
                {
                    report.Error("texture_unreadable", key, ex.Message);
                }
                finally
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                }
            }
        }

        return pending;
    }

    /// <returns>Texture paths (relative, no extension, forward slashes) that were added or replaced.</returns>
    public static List<string> Sync(ModContentPack owner, IEnumerable<string> texturesRoots, InjectionReport report)
    {
        foreach (var texture in Retired)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
            }
        }
        Retired.Clear();

        var changed = new List<string>();
        var holder = owner.GetContentHolder<Texture2D>();
        var trie = TrieField?.GetValue(holder) as StringTrieSet;

        foreach (var root in texturesRoots)
        {
            foreach (var (key, file) in Enumerate(root))
            {
                var fullPath = Path.GetFullPath(file);
                var (len, utc) = StampOf(file);
                if (Stamps.TryGetValue(key, out var known) &&
                    known.Length == len &&
                    known.WriteUtc == utc &&
                    string.Equals(known.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    holder.contentList.ContainsKey(key))
                {
                    continue;
                }

                Texture2D? texture;
                try
                {
                    texture = Texture2D.LoadFromFile(file);
                    if (texture != null)
                    {
                        texture.name = Path.GetFileNameWithoutExtension(file);
                        texture.filterMode = FilterMode.Trilinear;
                        texture.anisoLevel = 2;
                        texture.Apply(updateMipmaps: true, makeNoLongerReadable: false);
                    }
                }
                catch (Exception ex)
                {
                    report.Error("texture_unreadable", key, ex.Message);
                    continue;
                }
                if (texture == null)
                {
                    report.Error("texture_unreadable", key);
                    continue;
                }

                Invalidate(key, holder.contentList.TryGetValue(key, out var previous) ? previous : null);
                if (previous != null)
                {
                    Retired.Add(previous);
                }

                holder.contentList[key] = texture;
                if (trie != null && !trie.Contains(key))
                {
                    trie.Add(key);
                }
                Stamps[key] = (len, utc, fullPath);
                changed.Add(key);
            }
        }

        return changed;
    }

    private static IEnumerable<(string Key, string File)> Enumerate(string texturesRoot)
    {
        if (!Directory.Exists(texturesRoot))
        {
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(texturesRoot, "*.png", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(texturesRoot, file);
            yield return (Path.ChangeExtension(relative, null).Replace('\\', '/'), file);
        }
    }

    private static (long Length, DateTime WriteUtc) StampOf(string file)
    {
        var info = new FileInfo(file);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static void Invalidate(string key, Texture2D? previous)
    {
        GiantMaterialCache.Invalidate(key);
        GiantAlphaMaskCache.Invalidate(key);

        if (GraphicsField?.GetValue(null) is Dictionary<GraphicRequest, Graphic> graphics)
        {
            foreach (var request in graphics.Keys.Where(r => r.path == key).ToList())
            {
                graphics.Remove(request);
            }
        }

        if (previous != null && MaterialsField?.GetValue(null) is Dictionary<MaterialRequest, Material> materials)
        {
            var reverse = MaterialsReverseField?.GetValue(null) as Dictionary<Material, MaterialRequest>;
            foreach (var pair in materials.Where(p => ReferenceEquals(p.Key.mainTex, previous)).ToList())
            {
                materials.Remove(pair.Key);
                reverse?.Remove(pair.Value);
            }
        }
    }
}
