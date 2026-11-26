using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GiantCreatureFramework.Agent.Authoring;

/// <summary>
/// Pure-C# helper shared by <c>Dialog_ModManual</c> (Human IMGUI RichText formatting)
/// and <c>AuthoringToolProvider</c> (<c>list_manuals</c> / <c>load_manual</c> Agent access),
/// keeping zero Unity/Verse dependencies so automated unit tests can verify both directly.
/// </summary>
public static class ModManualCatalog
{
    public const string ArchetypeManualName = "gcf_archetype_and_def_manual";
    public const string ArchetypeManualFileName = "GiantCreatureFramework_Manual.md";

    private static readonly Regex BoldTagRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);

    /// <summary>
    /// Converts raw <c>&lt;</c> / <c>&gt;</c> into fullwidth <c>＜</c> / <c>＞</c> BEFORE injecting
    /// RimWorld IMGUI RichText tags (<c>&lt;b&gt;</c>, <c>&lt;color&gt;</c>), guaranteeing XML tags
    /// such as <c>&lt;skinnedMeshes&gt;</c> or <c>&lt;isTurret&gt;</c> are never swallowed by Unity RichText.
    /// </summary>
    public static string EscapeRichText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("<", "＜", StringComparison.Ordinal)
            .Replace(">", "＞", StringComparison.Ordinal);
    }

    public static string FormatInlineRichText(string? input, bool wrapBold = false)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var normalized = input.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase);
        var escaped = EscapeRichText(normalized);
        var s = BoldTagRegex.Replace(escaped, "<b>$1</b>");
        s = InlineCodeRegex.Replace(s, "<color=#7DD3FC>$1</color>");
        return wrapBold ? "<b>" + s + "</b>" : s;
    }

    public static string ResolveTopLevelManualPath(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            return preferredPath;
        }

        // 1. Derive from AgentSkillStore manual paths (<ModRoot>\1.7\AgentSkills\manuals\*.md)
        var skillManual = AgentSkillStore.Current.Manuals.FirstOrDefault();
        if (skillManual != null && !string.IsNullOrWhiteSpace(skillManual.FilePath))
        {
            var dirFromSkill = new FileInfo(skillManual.FilePath).Directory;
            while (dirFromSkill != null)
            {
                var candidate = Path.Combine(dirFromSkill.FullName, "Docs", ArchetypeManualFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dirFromSkill = dirFromSkill.Parent;
            }
        }

        // 2. Walk upward from AppContext.BaseDirectory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Docs", ArchetypeManualFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return string.Empty;
    }

    public static IReadOnlyList<ManualSummary> GetEffectiveManuals(string? preferredTopLevelPath = null)
    {
        var baseManuals = AgentSkillStore.Current.Manuals;
        var extraPath = ResolveTopLevelManualPath(preferredTopLevelPath);
        if (string.IsNullOrEmpty(extraPath) || !File.Exists(extraPath))
        {
            return baseManuals;
        }

        var combined = new List<ManualSummary>(baseManuals.Count + 1);
        combined.AddRange(baseManuals);
        combined.Add(new ManualSummary(
            ArchetypeManualName,
            "框架能力与制作总览：骨架、蒙皮 <skinnedMeshes>、回转 <isTurret>、座舱、阶段、部位武器、水面/舰船 <aquaticLocomotion>、飞行、机库、世界旅行及工具；完整字段见 framework_capabilities_and_fields",
            extraPath));
        return combined;
    }

    public static string ListManualsJson(string? preferredTopLevelPath = null)
    {
        var manuals = GetEffectiveManuals(preferredTopLevelPath);
        return JsonSerializer.Serialize(new
        {
            manualCount = manuals.Count,
            manuals = manuals.Select(m => new
            {
                name = m.Name,
                summary = m.Summary
            }).ToArray()
        }, Evidence.UnescapedJsonOptions);
    }

    public static string LoadManualJson(string? requestedName, string? preferredTopLevelPath = null)
    {
        var rawName = (requestedName ?? string.Empty).Trim();
        if (rawName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            rawName = rawName[..^3];
        }
        if (string.Equals(rawName, "GiantCreatureFramework_Manual", StringComparison.OrdinalIgnoreCase))
        {
            rawName = ArchetypeManualName;
        }

        var manual = GetEffectiveManuals(preferredTopLevelPath)
            .FirstOrDefault(m => string.Equals(m.Name, rawName, StringComparison.OrdinalIgnoreCase));
        if (manual == null)
        {
            throw new ArgumentException(AgentProtocol.Format("Authoring_ManualNotFound", rawName));
        }

        var content = File.ReadAllText(manual.FilePath, Encoding.UTF8).Replace("\r\n", "\n");
        return JsonSerializer.Serialize(new
        {
            manualName = manual.Name,
            summary = manual.Summary,
            charCount = content.Length,
            content
        }, Evidence.UnescapedJsonOptions);
    }
}
