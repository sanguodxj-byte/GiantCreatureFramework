using System.Text.RegularExpressions;
using GiantCreatureFramework.Agent.Imaging;

namespace GiantCreatureFramework.Agent.Authoring;

/// <summary>
/// Enforces 100% verbatim retention (append-only, zero deletion/rewriting) of the built-in
/// Style 1 four-paragraph English image prompt from sprite_prompts_and_matting.md, and blocks
/// all linework poison words and third-party IP names in user-authored slot descriptions.
/// </summary>
public static class BuiltInPromptGuard
{
    public const string HeaderPrefix =
        "Strict 90-degree overhead orthographic top-down modular part sheet on a SOLID PURE FLAT #00FF00 background with wide green spacing between parts and zero cast shadows.";

    public const string HeaderComponentAnchor =
        "separated components all strictly facing UP toward 12 o'clock:";

    public const string Part1Marker = "(Part 1)";

    public const string ColorPalettePrefix = "Color palette: primary surfaces in ";
    public const string ColorSteppingThrough = " stepping through ";
    public const string ColorTo = " to ";
    public const string ColorSecondary = ", secondary structural blocks in ";
    public const string ColorAccent = ", and accent details in ";
    public const string ColorDarkestClamp = "; darkest value anywhere clamped at ";
    public const string ColorHighlightCap = ", no bright specular rim highlights, no glossy lip-gloss highlights.";

    public const string StyleModule1Verbatim =
        "Art medium and brushwork: clean lineless vector-and-airbrush industrial sci-fi illustration with zero lineart layer, zero wireframe, and zero brush or sponge grain—combining smooth curved cylindrical shells, circular rings, and rounded-arc armor cowlings shaded with continuous soft cylindrical airbrush gradients alongside rich multi-layered overlapping chamfered armor plates and stepped mechanical collars with crisp edge-to-edge tonal steps (never simplistic empty pills, and strictly NO triangular or diamond low-poly crystal tessellation), meeting neighboring shapes and #00FF00 directly edge-to-edge.";

    public const string FooterNoText = "No text.";

    private static readonly Regex ComponentCountRegex = new(
        @"Strict 90-degree overhead orthographic top-down modular part sheet on a SOLID PURE FLAT #00FF00 background with wide green spacing between parts and zero cast shadows\.\s+(?<count>[1-9][0-9]*)\s+separated components all strictly facing UP toward 12 o'clock:",
        RegexOptions.Compiled);

    private static readonly Regex ColorPaletteLineRegex = new(
        @"Color palette: primary surfaces in (?<light>#[0-9A-Fa-f]{6}[^,;\r\n]*?) stepping through (?<mid>#[0-9A-Fa-f]{6}[^,;\r\n]*?) to (?<shadow>#[0-9A-Fa-f]{6}[^,;\r\n]*?), secondary structural blocks in (?<struct>#[0-9A-Fa-f]{6}[^,;\r\n]*?), and accent details in (?<accent>#[0-9A-Fa-f]{6}[^,;\r\n]*?); darkest value anywhere clamped at (?<darkest>#[0-9A-Fa-f]{6})[^.\r\n]*?, no bright specular rim highlights, no glossy lip-gloss highlights\.",
        RegexOptions.Compiled);

    private static readonly (string DisplayTerm, Regex Pattern)[] ForbiddenTerms =
    [
        // Third-party game / mod / character IP names
        ("RimWorld", new Regex(@"\bRimWorld\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Monster Hunter", new Regex(@"\bMonster\s+Hunter\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Rathalos", new Regex(@"\bRathalos\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Valstrax", new Regex(@"\bValstrax\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("天彗龙", new Regex(@"天彗龙", RegexOptions.Compiled)),
        ("Milira", new Regex(@"\bMilira\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Monolyn", new Regex(@"\bMonolyn\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DMS", new Regex(@"\bDMS\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("UFHI", new Regex(@"\bUFHI\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Wolfein", new Regex(@"\bWolfein\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        // Built-in anti-example creature identifiers
        ("MechScorpion", new Regex(@"\bMechScorpion\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AbyssalDragonBeast", new Regex(@"\bAbyssalDragonBeast\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("MechColossus", new Regex(@"\bMechColossus\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("ProtossCarrier", new Regex(@"\bProtossCarrier\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("PanzerIV", new Regex(@"\bPanzer\s*IV\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        // Linework / contour / seam poison terms from sprite_prompts_and_matting.md
        ("2D sprite", new Regex(@"\b2D\s+sprite\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("cel-shaded", new Regex(@"\bcel[-\s]?shaded\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("3D CAD model", new Regex(@"\b3D\s+CAD\s+model\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("wireframe", new Regex(@"\bwireframe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("outer contour", new Regex(@"\bouter\s+contour\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("black", new Regex(@"\bblack\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("lines", new Regex(@"\blines\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("panel recesses", new Regex(@"\bpanel\s+recesses\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("seam", new Regex(@"\bseam\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("outlines", new Regex(@"\boutlines\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("2px highlight strips", new Regex(@"\b2px\s+highlight\s+strips\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("thin trim ribbons", new Regex(@"\bthin\s+trim\s+ribbons\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("inlay strips", new Regex(@"\binlay\s+strips\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("access hatches", new Regex(@"\baccess\s+hatches\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("slits", new Regex(@"\bslits\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("color-field painting", new Regex(@"\bcolor[-\s]?field\s+painting\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("flat solid patches", new Regex(@"\bflat\s+solid\s+patches\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("paper-cutout", new Regex(@"\bpaper[-\s]?cutout\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    public static void ValidateOrThrow(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", HeaderPrefix));
        }

        var normalized = prompt.Replace("\r\n", "\n").Trim();

        // 1. Validate Section 1 verbatim header + concrete integer <N> + (Part 1)
        if (!normalized.Contains(HeaderPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", HeaderPrefix));
        }
        var headerMatch = ComponentCountRegex.Match(normalized);
        if (!headerMatch.Success)
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", "<N> " + HeaderComponentAnchor));
        }
        var part1Idx = normalized.IndexOf(Part1Marker, headerMatch.Index + headerMatch.Length, StringComparison.Ordinal);
        if (part1Idx < 0)
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", Part1Marker));
        }

        // 2. Validate Section 2 Color palette verbatim frame & darkest hex luminance >= 40
        foreach (var requiredColorSegment in new[]
        {
            ColorPalettePrefix,
            ColorSteppingThrough,
            ColorSecondary,
            ColorAccent,
            ColorDarkestClamp,
            ColorHighlightCap
        })
        {
            if (!normalized.Contains(requiredColorSegment, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", requiredColorSegment.Trim()));
            }
        }

        var colorMatch = ColorPaletteLineRegex.Match(normalized);
        if (!colorMatch.Success || colorMatch.Index <= part1Idx)
        {
            throw new InvalidOperationException(AgentProtocol.Format(
                "Authoring_PromptMissingBuiltin",
                "Color palette: primary surfaces in <LIGHT_HEX> stepping through <MID_HEX> to <SHADOW_HEX>, secondary structural blocks in <STRUCT_HEX>, and accent details in <ACCENT_HEX>; darkest value anywhere clamped at <DARKEST_HEX_LUM_GE_40>, no bright specular rim highlights, no glossy lip-gloss highlights."));
        }

        var darkestHex = colorMatch.Groups["darkest"].Value;
        var (dr, dg, db) = BoneWarper.ParseHexColor(darkestHex);
        var darkestLum = 0.299 * dr + 0.587 * dg + 0.114 * db;
        if (darkestLum < 39.5)
        {
            throw new InvalidOperationException(AgentProtocol.Format(
                "Authoring_PromptMissingBuiltin",
                $"darkest value {darkestHex} has luminance {darkestLum:F1} < 40 (must be >= #3E4248)"));
        }

        // 3. Validate Section 3 Style Module 1 100% verbatim retention
        var styleIdx = normalized.IndexOf(StyleModule1Verbatim, StringComparison.Ordinal);
        if (styleIdx < 0 || styleIdx <= colorMatch.Index)
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", StyleModule1Verbatim));
        }

        // 4. Validate Section 4 "No text." ending
        var noTextIdx = normalized.IndexOf(FooterNoText, styleIdx + StyleModule1Verbatim.Length, StringComparison.Ordinal);
        if (noTextIdx < 0)
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptMissingBuiltin", FooterNoText));
        }

        // 5. Strip verbatim built-in segments before scanning user-added slot descriptions for forbidden terms
        var userAddedText = normalized
            .Replace(HeaderPrefix, " ", StringComparison.Ordinal)
            .Replace(HeaderComponentAnchor, " ", StringComparison.Ordinal)
            .Replace(ColorPalettePrefix, " ", StringComparison.Ordinal)
            .Replace(ColorSteppingThrough, " ", StringComparison.Ordinal)
            .Replace(ColorSecondary, " ", StringComparison.Ordinal)
            .Replace(ColorAccent, " ", StringComparison.Ordinal)
            .Replace(ColorDarkestClamp, " ", StringComparison.Ordinal)
            .Replace(ColorHighlightCap, " ", StringComparison.Ordinal)
            .Replace(StyleModule1Verbatim, " ", StringComparison.Ordinal)
            .Replace(FooterNoText, " ", StringComparison.Ordinal);

        foreach (var (displayTerm, pattern) in ForbiddenTerms)
        {
            if (pattern.IsMatch(userAddedText))
            {
                throw new InvalidOperationException(AgentProtocol.Format("Authoring_PromptForbiddenTerm", displayTerm));
            }
        }
    }
}
