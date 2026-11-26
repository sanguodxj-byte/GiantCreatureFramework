using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GiantCreatureFramework.Agent;

/// <summary>
/// Represents a clickable, underlined mod/game term span within a RichText-formatted string.
/// <paramref name="RichTextStart"/> and <paramref name="RichTextEnd"/> are character offsets
/// directly into the final RichText string passed to <c>Widgets.Label</c> and <c>IGuiBackend.TextSelectionRects</c>.
/// </summary>
public readonly record struct InlineTermSpan(string TermText, int RichTextStart, int RichTextEnd);

/// <summary>
/// Parses agent term tags (e.g. <c>&lt;term&gt;名词&lt;/term&gt;</c>), converts inline Markdown to RimWorld RichText,
/// computes exact character spans for underline rendering and hit-testing, and appends clicked terms to the input buffer.
/// </summary>
public static class TermTagFormatter
{
    public const string BulletPlaceholder = "\uE001";
    public const string TermHighlightColorHex = "#38BDF8";

    private const char TermPlaceholderPrefix = '\uE010';
    private const char TermPlaceholderSuffix = '\uE011';

    private static readonly Regex BacktickWrappedTermRegex = new(
        @"`\s*(<\s*(?:term|u|noun|tag)\b[^>]*>[^\r\n`]+?</\s*(?:term|u|noun|tag)\s*>)\s*`",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClosedTermTagRegex = new(
        @"<\s*(term|u|noun|tag)\b[^>]*>([^\r\n]+?)</\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnclosedStreamingTermTagRegex = new(
        @"<\s*(?:term|u|noun|tag)\b[^>]*>([^\r\n<>]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingPartialTermTagRegex = new(
        @"</?\s*(?:t(?:e(?:r(?:m)?)?)?|n(?:o(?:u(?:n)?)?)?|u|t(?:a(?:g)?)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InnerHtmlTagRegex = new(
        @"</?\s*(?:b|i|u|term|noun|tag|color|size)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BoldMarkdownRegex = new(
        @"\*\*(.+?)\*\*",
        RegexOptions.Compiled);

    private static readonly Regex InlineCodeMarkdownRegex = new(
        @"`([^`\n]+)`",
        RegexOptions.Compiled);

    public static string EscapeRichText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("<", "＜").Replace(">", "＞");
    }

    public static string CleanTermText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = InnerHtmlTagRegex.Replace(raw, string.Empty);
        s = s.Replace("**", string.Empty).Replace("`", string.Empty);
        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        return s.Trim();
    }

    /// <summary>
    /// Strips term wrapper tags (<c>&lt;term&gt;...&lt;/term&gt;</c> etc.) from full message text while preserving inner terms.
    /// </summary>
    public static string StripTermTags(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var s = BacktickWrappedTermRegex.Replace(text, "$1");
        s = ClosedTermTagRegex.Replace(s, m => CleanTermText(m.Groups[2].Value));
        s = TrailingPartialTermTagRegex.Replace(s, string.Empty);
        s = UnclosedStreamingTermTagRegex.Replace(s, m => CleanTermText(m.Groups[1].Value));
        return s;
    }

    /// <summary>
    /// Appends a clicked term to the existing composer input buffer without replacing existing text.
    /// </summary>
    public static string AppendTermToInput(string? currentInput, string? term)
    {
        var existing = currentInput ?? string.Empty;
        var cleanTerm = CleanTermText(term);
        if (string.IsNullOrEmpty(cleanTerm))
        {
            return existing;
        }

        return existing + cleanTerm;
    }

    public static string FormatInlineMarkdown(string? text, bool wrapBold = false)
        => FormatInlineMarkdown(text, out _, wrapBold);

    public static string FormatInlineMarkdown(string? text, out List<InlineTermSpan> terms, bool wrapBold = false)
    {
        terms = new List<InlineTermSpan>();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var extractedTerms = new List<string>();
        var working = BacktickWrappedTermRegex.Replace(text, "$1");

        working = ClosedTermTagRegex.Replace(working, match =>
        {
            var cleaned = CleanTermText(match.Groups[2].Value);
            if (string.IsNullOrEmpty(cleaned))
            {
                return string.Empty;
            }

            var idx = extractedTerms.Count;
            extractedTerms.Add(cleaned);
            return $"{TermPlaceholderPrefix}{idx}{TermPlaceholderSuffix}";
        });

        working = TrailingPartialTermTagRegex.Replace(working, string.Empty);

        working = UnclosedStreamingTermTagRegex.Replace(working, match =>
        {
            var cleaned = CleanTermText(match.Groups[1].Value);
            if (string.IsNullOrEmpty(cleaned))
            {
                return string.Empty;
            }

            var idx = extractedTerms.Count;
            extractedTerms.Add(cleaned);
            return $"{TermPlaceholderPrefix}{idx}{TermPlaceholderSuffix}";
        });

        var escaped = EscapeRichText(working);
        var s = BoldMarkdownRegex.Replace(escaped, "<b>$1</b>");
        s = InlineCodeMarkdownRegex.Replace(s, "<color=#7DD3FC>$1</color>");
        if (s.Contains(BulletPlaceholder, StringComparison.Ordinal))
        {
            s = s.Replace(BulletPlaceholder, "<color=#60A5FA><b>•</b></color> ");
        }

        if (wrapBold)
        {
            s = "<b>" + s + "</b>";
        }

        if (extractedTerms.Count == 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + extractedTerms.Count * 24);
        var cursor = 0;
        const string colorOpen = "<color=" + TermHighlightColorHex + ">";
        const string colorClose = "</color>";

        while (cursor < s.Length)
        {
            var startMarker = s.IndexOf(TermPlaceholderPrefix, cursor);
            if (startMarker < 0)
            {
                sb.Append(s, cursor, s.Length - cursor);
                break;
            }

            if (startMarker > cursor)
            {
                sb.Append(s, cursor, startMarker - cursor);
            }

            var endMarker = s.IndexOf(TermPlaceholderSuffix, startMarker + 1);
            if (endMarker < 0)
            {
                sb.Append(s, startMarker, s.Length - startMarker);
                break;
            }

            var idxSpan = s.AsSpan(startMarker + 1, endMarker - startMarker - 1);
            if (int.TryParse(idxSpan, out var termIndex) && termIndex >= 0 && termIndex < extractedTerms.Count)
            {
                var rawTerm = extractedTerms[termIndex];
                var displayTerm = EscapeRichText(rawTerm);
                sb.Append(colorOpen);
                var richStart = sb.Length;
                sb.Append(displayTerm);
                var richEnd = sb.Length;
                sb.Append(colorClose);
                terms.Add(new InlineTermSpan(rawTerm, richStart, richEnd));
            }

            cursor = endMarker + 1;
        }

        return sb.ToString();
    }
}
