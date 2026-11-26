using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework.Agent.UI;

/// <summary>
/// 游戏内模组开发与工具说明书阅读器窗口。
/// 采用安全尖括号转义富文本管线（零 XML 标签吞字）、有序/多级缩进列表渲染、全书实时搜索及一键复制原始 XML 代码。
/// </summary>
public sealed class Dialog_ModManual : Window
{
    private enum BlockKind
    {
        H1,
        H2,
        H3,
        Paragraph,
        Bullet,
        OrderedItem,
        Code,
        Table
    }

    private sealed class ManualBlock
    {
        public BlockKind Kind;
        public string Text = string.Empty;        // RichText-safe formatted string for UI display
        public string PlainSearchText = string.Empty; // Unformatted raw text for accurate keyword search
        public string RawCode = string.Empty;     // Raw ASCII code for clipboard copy
        public string Language = string.Empty;
        public string Marker = string.Empty;      // "•", "◦", or "1.", "2.", etc.
        public int IndentLevel;
        public List<string[]> TableRows = new();      // RichText-safe formatted table cells
        public List<string[]> RawTableRows = new();   // Raw table cells for search matching
        public int ChapterIndex = -1;
    }

    private sealed class ManualChapter
    {
        public string Title = string.Empty;
        public string FormattedHeading = string.Empty;
        public int StartBlockIndex;
    }

    private static Dialog_ModManual? _activeInstance;
    private static readonly Regex BoldTagRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"^(\s*)(\d+\.)\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new(@"^(\s*)[-*]\s+(.+)$", RegexOptions.Compiled);

    private readonly List<ManualBlock> _blocks = new();
    private readonly List<ManualChapter> _chapters = new();
    private string _rawMarkdown = string.Empty;
    private string _loadedPath = string.Empty;
    private string _searchQuery = string.Empty;
    private int _selectedChapter = -1;
    private int _lastMatchCount;
    private Vector2 _navScroll = Vector2.zero;
    private Vector2 _bodyScroll = Vector2.zero;
    private float _cachedContentHeight = 600f;

    public override Vector2 InitialSize => new(1140f, 800f);
    protected override float Margin => 0f;

    public static bool IsWindowOpen =>
        _activeInstance != null && Find.WindowStack != null && Find.WindowStack.IsOpen(_activeInstance);

    public Dialog_ModManual()
    {
        draggable = true;
        resizeable = true;
        doCloseX = false;
        doCloseButton = false;
        doWindowBackground = false;
        drawShadow = false;
        closeOnAccept = false;
        closeOnCancel = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        preventCameraMotion = false;

        ReloadManualFromDisk();
    }

    public static void OpenWindow()
    {
        if (Find.WindowStack == null)
        {
            return;
        }

        if (_activeInstance != null && Find.WindowStack.IsOpen(_activeInstance))
        {
            _activeInstance.ReloadManualFromDisk();
            Find.WindowStack.TryRemove(_activeInstance, doCloseSound: false);
            Find.WindowStack.Add(_activeInstance);
            return;
        }

        _activeInstance = new Dialog_ModManual();
        Find.WindowStack.Add(_activeInstance);
    }

    public void ReloadManualFromDisk()
    {
        _blocks.Clear();
        _chapters.Clear();
        _rawMarkdown = string.Empty;
        _loadedPath = ResolveManualFilePath();

        if (!string.IsNullOrEmpty(_loadedPath) && File.Exists(_loadedPath))
        {
            try
            {
                _rawMarkdown = File.ReadAllText(_loadedPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _rawMarkdown = UiText.Format("GCFAgent_ManualReadFailed", ex.Message);
            }
        }
        else
        {
            _rawMarkdown = UiText.Get("GCFAgent_ManualFileMissing");
        }

        ParseMarkdown(_rawMarkdown);
        if (_selectedChapter >= _chapters.Count)
        {
            _selectedChapter = -1;
        }
    }

    public static string ResolveManualFilePath()
    {
        var candidates = new List<string>();
        var mods = LoadedModManager.RunningModsListForReading;
        if (mods != null)
        {
            var gcfMod = mods.FirstOrDefault(m =>
                string.Equals(m.PackageId, "sanguo.giantcreatureframework", StringComparison.OrdinalIgnoreCase) ||
                m.PackageIdPlayerFacing.Equals("sanguo.giantcreatureframework", StringComparison.OrdinalIgnoreCase));
            if (gcfMod != null && !string.IsNullOrEmpty(gcfMod.RootDir))
            {
                candidates.Add(Path.Combine(gcfMod.RootDir, "Docs", "GiantCreatureFramework_Manual.md"));
            }

            var rcMod = mods.FirstOrDefault(m =>
                string.Equals(m.PackageId, "sanguodxj.rimcopilot", StringComparison.OrdinalIgnoreCase));
            if (rcMod != null && !string.IsNullOrEmpty(rcMod.RootDir))
            {
                candidates.Add(Path.Combine(rcMod.RootDir, "docs", "GiantCreatureFramework_Manual.md"));
            }
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return candidates.FirstOrDefault() ?? string.Empty;
    }

    public override void DoWindowContents(Rect inRect)
    {
        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevColor = GUI.color;
        var prevWrap = Text.WordWrap;

        try
        {
            ModernUiKit.DrawRoundedCard(inRect, ModernUiKit.BgWindow, ModernUiKit.BorderWindow, radius: 12, borderThickness: 1);

            var headerRect = new Rect(inRect.x + 1f, inRect.y + 1f, inRect.width - 2f, 50f);
            ModernUiKit.DrawRoundedRect(headerRect, ModernUiKit.BgHeader, radius: 11);
            ModernUiKit.DrawHorizontalHairline(inRect.x + 1f, headerRect.yMax, inRect.width - 2f, ModernUiKit.BorderSubtle);

            ModernUiKit.DrawSingleLineLabel(
                new Rect(headerRect.x + 18f, headerRect.y + 6f, headerRect.width - 340f, 22f),
                UiText.Get("GCFAgent_ManualWindowTitle"),
                GameFont.Medium,
                ModernUiKit.TextBright);

            var subText = string.IsNullOrEmpty(_loadedPath)
                ? UiText.Get("GCFAgent_ManualWindowSubtitle")
                : $"{UiText.Get("GCFAgent_ManualWindowSubtitle")}  ·  {Path.GetFileName(_loadedPath)}";
            ModernUiKit.DrawSingleLineLabel(
                new Rect(headerRect.x + 18f, headerRect.y + 28f, headerRect.width - 340f, 18f),
                subText,
                GameFont.Tiny,
                ModernUiKit.TextMuted);

            var closeRect = new Rect(headerRect.xMax - 38f, headerRect.y + 11f, 28f, 28f);
            if (ModernUiKit.DrawCloseIconButton(closeRect, radius: 7))
            {
                Close();
                return;
            }

            var copyAllRect = new Rect(closeRect.x - 98f, headerRect.y + 11f, 90f, 28f);
            if (ModernUiKit.DrawButton(copyAllRect, UiText.Get("GCFAgent_ManualCopyAll"), ModernButtonVariant.Secondary, radius: 7, active: false, font: GameFont.Tiny))
            {
                GUIUtility.systemCopyBuffer = _rawMarkdown;
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }

            var reloadRect = new Rect(copyAllRect.x - 86f, headerRect.y + 11f, 78f, 28f);
            if (ModernUiKit.DrawButton(reloadRect, UiText.Get("GCFAgent_ManualReload"), ModernButtonVariant.Pill, radius: 7, active: false, font: GameFont.Tiny))
            {
                ReloadManualFromDisk();
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            }

            var bodyTop = headerRect.yMax + 1f;
            var bodyHeight = inRect.height - bodyTop - 1f;
            const float navWidth = 264f;

            var navRect = new Rect(inRect.x + 1f, bodyTop, navWidth, bodyHeight);
            ModernUiKit.DrawRoundedRect(navRect, ModernUiKit.BgSidebar, radius: 10);
            ModernUiKit.DrawVerticalHairline(navRect.xMax, bodyTop, bodyHeight, ModernUiKit.BorderSubtle);

            var contentRect = new Rect(navRect.xMax + 1f, bodyTop, inRect.width - navWidth - 3f, bodyHeight);
            // Draw body before or alongside nav; _lastMatchCount updates continuously
            DrawManualBody(contentRect);
            DrawLeftNav(navRect);
        }
        finally
        {
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            GUI.color = prevColor;
            Text.WordWrap = prevWrap;
        }
    }

    private void DrawLeftNav(Rect rect)
    {
        var curY = rect.y + 12f;
        var searchRect = new Rect(rect.x + 12f, curY, rect.width - 24f, 30f);
        var nextQuery = ModernUiKit.DrawModernTextField(
            searchRect,
            _searchQuery,
            UiText.Get("GCFAgent_ManualSearchPlaceholder"),
            radius: 7,
            controlName: "gcf_manual_search_input");
        if (!string.Equals(nextQuery, _searchQuery, StringComparison.Ordinal))
        {
            _searchQuery = nextQuery;
            _bodyScroll = Vector2.zero;
        }
        curY += 36f;

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            var clearLabel = $"{UiText.Get("GCFAgent_ManualClearSearch")} ({_lastMatchCount})";
            var clearSearchRect = new Rect(rect.x + 12f, curY, rect.width - 24f, 24f);
            if (ModernUiKit.DrawButton(clearSearchRect, clearLabel, ModernButtonVariant.Ghost, radius: 6, active: false, font: GameFont.Tiny))
            {
                _searchQuery = string.Empty;
                _bodyScroll = Vector2.zero;
            }
            curY += 30f;
        }

        ModernUiKit.DrawSingleLineLabel(
            new Rect(rect.x + 14f, curY, rect.width - 28f, 18f),
            UiText.Get("GCFAgent_ManualChaptersHeader"),
            GameFont.Tiny,
            ModernUiKit.TextMuted);
        curY += 22f;

        var listRect = new Rect(rect.x + 8f, curY, rect.width - 16f, rect.height - (curY - rect.y) - 10f);
        var totalRows = _chapters.Count + 1;
        const float rowH = 38f;
        var viewRect = new Rect(0f, 0f, listRect.width - 14f, Mathf.Max(listRect.height, totalRows * rowH));

        Widgets.BeginScrollView(listRect, ref _navScroll, viewRect);

        DrawChapterRow(new Rect(0f, 0f, viewRect.width, 34f), -1, UiText.Get("GCFAgent_ManualAllChapters"));
        for (var i = 0; i < _chapters.Count; i++)
        {
            DrawChapterRow(new Rect(0f, (i + 1) * rowH, viewRect.width, 34f), i, _chapters[i].Title);
        }

        Widgets.EndScrollView();
    }

    private void DrawChapterRow(Rect rowRect, int chapterIdx, string title)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(_searchQuery);
        var isSelected = !hasSearch && _selectedChapter == chapterIdx;
        var hovered = Mouse.IsOver(rowRect);

        if (isSelected)
        {
            ModernUiKit.DrawRoundedCard(
                rowRect,
                new Color(0.086f, 0.125f, 0.204f, 1f),
                new Color(0.176f, 0.259f, 0.416f, 1f),
                radius: 7,
                borderThickness: 1);
            Widgets.DrawBoxSolid(
                new Rect(rowRect.x + 4f, rowRect.y + 7f, 3f, rowRect.height - 14f),
                ModernUiKit.AccentPrimaryHover);
        }
        else if (hovered)
        {
            ModernUiKit.DrawRoundedRect(rowRect, new Color(0.067f, 0.086f, 0.129f, 1f), radius: 7);
        }

        var color = isSelected ? ModernUiKit.TextBright : (hovered ? ModernUiKit.TextPrimary : ModernUiKit.TextSecondary);
        ModernUiKit.DrawSingleLineLabel(
            new Rect(rowRect.x + 12f, rowRect.y, rowRect.width - 18f, rowRect.height),
            title,
            GameFont.Tiny,
            color,
            TextAnchor.MiddleLeft,
            title);

        if (Widgets.ButtonInvisible(rowRect))
        {
            _selectedChapter = chapterIdx;
            _searchQuery = string.Empty;
            _bodyScroll = Vector2.zero;
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
        }
    }

    private void DrawManualBody(Rect outerRect)
    {
        var pad = 16f;
        var scrollOuter = new Rect(outerRect.x + pad, outerRect.y + 10f, outerRect.width - pad * 2f, outerRect.height - 20f);
        var contentW = Mathf.Max(320f, scrollOuter.width - 18f);
        var viewRect = new Rect(0f, 0f, contentW, Mathf.Max(scrollOuter.height, _cachedContentHeight));

        var filter = _searchQuery.Trim();
        var hasFilter = filter.Length > 0;

        Widgets.BeginScrollView(scrollOuter, ref _bodyScroll, viewRect);
        var curY = 4f;
        var matchCount = 0;
        var lastEmittedSearchChapter = -2;

        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            if (!hasFilter)
            {
                if (_selectedChapter >= 0 && block.ChapterIndex != _selectedChapter)
                {
                    continue;
                }
                curY = DrawBlock(block, curY, contentW);
                matchCount++;
                continue;
            }

            if (!BlockMatchesFilter(block, filter))
            {
                continue;
            }

            // When searching across all chapters, show the parent H2 chapter banner once per chapter for clear context
            if (block.Kind != BlockKind.H2 &&
                block.ChapterIndex >= 0 &&
                block.ChapterIndex < _chapters.Count &&
                lastEmittedSearchChapter != block.ChapterIndex)
            {
                lastEmittedSearchChapter = block.ChapterIndex;
                curY = DrawChapterBanner(_chapters[block.ChapterIndex].FormattedHeading, curY, contentW);
            }
            else if (block.Kind == BlockKind.H2)
            {
                lastEmittedSearchChapter = block.ChapterIndex;
            }

            matchCount++;
            curY = DrawBlock(block, curY, contentW);
        }

        _lastMatchCount = matchCount;
        if (matchCount == 0)
        {
            ModernUiKit.DrawSingleLineLabel(
                new Rect(8f, 18f, contentW - 16f, 26f),
                UiText.Get("GCFAgent_ManualNoMatch"),
                GameFont.Small,
                ModernUiKit.TextMuted);
            curY = 60f;
        }

        _cachedContentHeight = curY + 28f;
        Widgets.EndScrollView();
    }

    private static float DrawChapterBanner(string formattedHeading, float curY, float width)
    {
        curY += 6f;
        var h = MeasureWrappedHeight(formattedHeading, width - 14f, GameFont.Medium) + 4f;
        Widgets.DrawBoxSolid(new Rect(0f, curY + 3f, 3f, Mathf.Max(14f, h - 6f)), ModernUiKit.AccentPrimaryHover);
        DrawWrappedLabel(new Rect(10f, curY, width - 14f, h), formattedHeading, GameFont.Medium, ModernUiKit.AccentCyan);
        curY += h + 4f;
        ModernUiKit.DrawHorizontalHairline(0f, curY, width, ModernUiKit.BorderSubtle);
        return curY + 10f;
    }

    private static bool BlockMatchesFilter(ManualBlock block, string filter)
    {
        if (!string.IsNullOrEmpty(block.PlainSearchText) &&
            block.PlainSearchText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        for (var r = 0; r < block.RawTableRows.Count; r++)
        {
            var row = block.RawTableRows[r];
            for (var c = 0; c < row.Length; c++)
            {
                if (row[c].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static float DrawBlock(ManualBlock block, float curY, float width)
    {
        switch (block.Kind)
        {
            case BlockKind.H1:
            {
                var h = MeasureWrappedHeight(block.Text, width, GameFont.Medium) + 6f;
                DrawWrappedLabel(new Rect(0f, curY, width, h), block.Text, GameFont.Medium, ModernUiKit.TextBright);
                curY += h + 6f;
                ModernUiKit.DrawHorizontalHairline(0f, curY, width, ModernUiKit.BorderCard);
                return curY + 12f;
            }
            case BlockKind.H2:
            {
                return DrawChapterBanner(block.Text, curY, width);
            }
            case BlockKind.H3:
            {
                curY += 6f;
                var h = MeasureWrappedHeight(block.Text, width - 12f, GameFont.Small) + 2f;
                Widgets.DrawBoxSolid(new Rect(0f, curY + 3f, 2f, Mathf.Max(12f, h - 5f)), ModernUiKit.AccentCyan);
                DrawWrappedLabel(new Rect(10f, curY, width - 12f, h), block.Text, GameFont.Small, ModernUiKit.TextBright);
                return curY + h + 6f;
            }
            case BlockKind.Bullet:
            {
                var indentX = block.IndentLevel * 18f;
                var bulletW = Mathf.Max(80f, width - indentX - 18f);
                var h = MeasureWrappedHeight(block.Text, bulletW, GameFont.Small);
                var markerColor = block.IndentLevel == 0 ? ModernUiKit.AccentCyan : ModernUiKit.TextSecondary;
                ModernUiKit.DrawSingleLineLabel(new Rect(indentX + 2f, curY, 14f, 20f), block.Marker, GameFont.Small, markerColor);
                DrawWrappedLabel(new Rect(indentX + 18f, curY, bulletW, h + 2f), block.Text, GameFont.Small, ModernUiKit.TextPrimary);
                return curY + h + 5f;
            }
            case BlockKind.OrderedItem:
            {
                var indentX = block.IndentLevel * 18f;
                var textW = Mathf.Max(80f, width - indentX - 26f);
                var h = MeasureWrappedHeight(block.Text, textW, GameFont.Small);
                ModernUiKit.DrawSingleLineLabel(new Rect(indentX, curY, 24f, 20f), block.Marker, GameFont.Small, ModernUiKit.AccentCyan);
                DrawWrappedLabel(new Rect(indentX + 26f, curY, textW, h + 2f), block.Text, GameFont.Small, ModernUiKit.TextPrimary);
                return curY + h + 6f;
            }
            case BlockKind.Code:
            {
                var safeCode = block.Text;
                var textW = Mathf.Max(60f, width - 24f);
                var codeH = MeasureWrappedHeight(safeCode, textW, GameFont.Tiny) + 16f;
                var headerH = 26f;
                var totalH = headerH + codeH;
                var cardRect = new Rect(0f, curY, width, totalH);
                ModernUiKit.DrawRoundedCard(cardRect, ModernUiKit.BgCodeBody, ModernUiKit.BorderCard, radius: 8, borderThickness: 1);

                var hdrRect = new Rect(1f, curY + 1f, width - 2f, headerH);
                ModernUiKit.DrawRoundedRect(hdrRect, ModernUiKit.BgCodeHeader, radius: 7);
                ModernUiKit.DrawHorizontalHairline(1f, hdrRect.yMax, width - 2f, ModernUiKit.BorderSubtle);

                var langLabel = string.IsNullOrEmpty(block.Language) ? "XML / CODE" : block.Language.ToUpperInvariant();
                ModernUiKit.DrawSingleLineLabel(
                    new Rect(12f, hdrRect.y, 180f, headerH),
                    langLabel,
                    GameFont.Tiny,
                    ModernUiKit.AccentCyan);

                var copyRect = new Rect(width - 84f, hdrRect.y + 3f, 74f, 20f);
                if (ModernUiKit.DrawButton(copyRect, UiText.Get("GCFAgent_CommonCopyCode"), ModernButtonVariant.Pill, radius: 5, active: false, font: GameFont.Tiny))
                {
                    GUIUtility.systemCopyBuffer = block.RawCode;
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }

                DrawWrappedLabel(
                    new Rect(12f, hdrRect.yMax + 8f, textW, codeH - 8f),
                    safeCode,
                    GameFont.Tiny,
                    new Color(0.82f, 0.90f, 0.99f, 1f));
                return curY + totalH + 10f;
            }
            case BlockKind.Table:
            {
                return DrawTableBlock(block, curY, width);
            }
            default:
            {
                var h = MeasureWrappedHeight(block.Text, width, GameFont.Small);
                DrawWrappedLabel(new Rect(0f, curY, width, h + 2f), block.Text, GameFont.Small, ModernUiKit.TextPrimary);
                return curY + h + 8f;
            }
        }
    }

    private static float DrawTableBlock(ManualBlock block, float curY, float width)
    {
        if (block.TableRows.Count == 0)
        {
            return curY;
        }

        var colCount = block.TableRows.Max(r => r.Length);
        if (colCount <= 0)
        {
            return curY;
        }

        var weights = new float[colCount];
        for (var c = 0; c < colCount; c++)
        {
            weights[c] = c == 0 ? 1.05f : (colCount >= 4 && c == 1 ? 0.80f : 1.45f);
        }
        var weightSum = weights.Sum();
        var colWidths = new float[colCount];
        for (var c = 0; c < colCount; c++)
        {
            colWidths[c] = (width - 4f) * (weights[c] / weightSum);
        }

        var totalH = 4f;
        var rowHeights = new float[block.TableRows.Count];
        for (var r = 0; r < block.TableRows.Count; r++)
        {
            var row = block.TableRows[r];
            var rowH = 26f;
            for (var c = 0; c < colCount; c++)
            {
                var cellText = c < row.Length ? row[c] : string.Empty;
                var cellH = MeasureWrappedHeight(cellText, Mathf.Max(36f, colWidths[c] - 16f), GameFont.Tiny) + 12f;
                if (cellH > rowH)
                {
                    rowH = cellH;
                }
            }
            rowHeights[r] = rowH;
            totalH += rowH;
        }

        var tableRect = new Rect(0f, curY, width, totalH);
        ModernUiKit.DrawRoundedCard(tableRect, ModernUiKit.BgTableRowEven, ModernUiKit.BorderCard, radius: 8, borderThickness: 1);

        var drawY = curY + 2f;
        for (var r = 0; r < block.TableRows.Count; r++)
        {
            var row = block.TableRows[r];
            var rowH = rowHeights[r];
            var isHeader = r == 0;
            var rowRect = new Rect(2f, drawY, width - 4f, rowH);

            if (isHeader)
            {
                ModernUiKit.DrawRoundedRect(rowRect, ModernUiKit.BgTableHeader, radius: 6);
            }
            else if (r % 2 == 1)
            {
                Widgets.DrawBoxSolid(rowRect, ModernUiKit.BgTableRowOdd);
            }

            if (r > 0)
            {
                ModernUiKit.DrawHorizontalHairline(rowRect.x, drawY, rowRect.width, ModernUiKit.BorderSubtle);
            }

            var colX = 2f;
            for (var c = 0; c < colCount; c++)
            {
                var cw = colWidths[c];
                if (c > 0)
                {
                    ModernUiKit.DrawVerticalHairline(colX, drawY, rowH, ModernUiKit.BorderSubtle);
                }

                var cellText = c < row.Length ? row[c] : string.Empty;
                DrawWrappedLabel(
                    new Rect(colX + 8f, drawY + 6f, Mathf.Max(24f, cw - 16f), rowH - 8f),
                    cellText,
                    GameFont.Tiny,
                    isHeader ? ModernUiKit.AccentCyan : ModernUiKit.TextPrimary);
                colX += cw;
            }

            drawY += rowH;
        }

        return curY + totalH + 12f;
    }

    private static float MeasureWrappedHeight(string text, float width, GameFont font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 18f;
        }

        var prevFont = Text.Font;
        var prevWrap = Text.WordWrap;
        Text.Font = font;
        Text.WordWrap = true;
        try
        {
            return Mathf.Max(18f, Text.CalcHeight(text, Mathf.Max(32f, width)));
        }
        finally
        {
            Text.Font = prevFont;
            Text.WordWrap = prevWrap;
        }
    }

    private static void DrawWrappedLabel(Rect rect, string text, GameFont font, Color color)
    {
        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevWrap = Text.WordWrap;
        var prevColor = GUI.color;

        Text.Font = font;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.WordWrap = true;
        GUI.color = color;
        Widgets.Label(rect, text);

        Text.Font = prevFont;
        Text.Anchor = prevAnchor;
        Text.WordWrap = prevWrap;
        GUI.color = prevColor;
    }

    private void ParseMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var curChapter = -1;
        var i = 0;

        while (i < lines.Length)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed == "---")
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var lang = trimmed.Substring(3).Trim();
                i++;
                var codeSb = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (codeSb.Length > 0)
                    {
                        codeSb.Append('\n');
                    }
                    codeSb.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length)
                {
                    i++;
                }
                var rawCode = codeSb.ToString();
                _blocks.Add(new ManualBlock
                {
                    Kind = BlockKind.Code,
                    Language = lang,
                    RawCode = rawCode,
                    Text = EscapeRichText(rawCode),
                    PlainSearchText = rawCode,
                    ChapterIndex = curChapter
                });
                continue;
            }

            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                var formattedRows = new List<string[]>();
                var rawRows = new List<string[]>();
                while (i < lines.Length && lines[i].Trim().StartsWith("|", StringComparison.Ordinal))
                {
                    var rowLine = lines[i].Trim();
                    i++;
                    if (IsTableSeparatorRow(rowLine))
                    {
                        continue;
                    }
                    var isHeader = formattedRows.Count == 0;
                    ParseTableRow(rowLine, isHeader, out var fmtCells, out var plainCells);
                    formattedRows.Add(fmtCells);
                    rawRows.Add(plainCells);
                }
                if (formattedRows.Count > 0)
                {
                    _blocks.Add(new ManualBlock
                    {
                        Kind = BlockKind.Table,
                        TableRows = formattedRows,
                        RawTableRows = rawRows,
                        ChapterIndex = curChapter
                    });
                }
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                var h1Raw = trimmed.Substring(2).Trim();
                _blocks.Add(new ManualBlock
                {
                    Kind = BlockKind.H1,
                    Text = FormatInlineRichText(h1Raw, wrapBold: true),
                    PlainSearchText = StripMarkdownFormatting(h1Raw),
                    ChapterIndex = curChapter
                });
                i++;
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var h2Raw = trimmed.Substring(3).Trim();
                var title = StripMarkdownFormatting(h2Raw);
                var formattedH2 = FormatInlineRichText(h2Raw, wrapBold: true);
                curChapter = _chapters.Count;
                _chapters.Add(new ManualChapter
                {
                    Title = title,
                    FormattedHeading = formattedH2,
                    StartBlockIndex = _blocks.Count
                });
                _blocks.Add(new ManualBlock
                {
                    Kind = BlockKind.H2,
                    Text = formattedH2,
                    PlainSearchText = title,
                    ChapterIndex = curChapter
                });
                i++;
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                var h3Raw = trimmed.Substring(4).Trim();
                _blocks.Add(new ManualBlock
                {
                    Kind = BlockKind.H3,
                    Text = FormatInlineRichText(h3Raw, wrapBold: true),
                    PlainSearchText = StripMarkdownFormatting(h3Raw),
                    ChapterIndex = curChapter
                });
                i++;
                continue;
            }

            var orderedMatch = OrderedListRegex.Match(rawLine);
            if (orderedMatch.Success)
            {
                var indent = Mathf.Clamp(orderedMatch.Groups[1].Value.Replace("\t", "  ").Length / 2, 0, 3);
                var numMarker = orderedMatch.Groups[2].Value;
                var itemBody = orderedMatch.Groups[3].Value.Trim();
                _blocks.Add(new ManualBlock
                {
                    Kind = BlockKind.OrderedItem,
                    IndentLevel = indent,
                    Marker = numMarker,
                    Text = FormatInlineRichText(itemBody),
                    PlainSearchText = numMarker + " " + StripMarkdownFormatting(itemBody),
                    ChapterIndex = curChapter
                });
                i++;
                continue;
            }

            var unorderedMatch = UnorderedListRegex.Match(rawLine);
            if (unorderedMatch.Success)
            {
                var indent = Mathf.Clamp(unorderedMatch.Groups[1].Value.Replace("\t", "  ").Length / 2, 0, 3);
                var itemBody = unorderedMatch.Groups[2].Value.Trim();
                _blocks.Add(new ManualBlock
                {
                    Kind = BlockKind.Bullet,
                    IndentLevel = indent,
                    Marker = indent == 0 ? "•" : "◦",
                    Text = FormatInlineRichText(itemBody),
                    PlainSearchText = StripMarkdownFormatting(itemBody),
                    ChapterIndex = curChapter
                });
                i++;
                continue;
            }

            _blocks.Add(new ManualBlock
            {
                Kind = BlockKind.Paragraph,
                Text = FormatInlineRichText(trimmed),
                PlainSearchText = StripMarkdownFormatting(trimmed),
                ChapterIndex = curChapter
            });
            i++;
        }
    }

    private static bool IsTableSeparatorRow(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch != '|' && ch != '-' && ch != ':' && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }
        return line.Contains('-');
    }

    private static void ParseTableRow(string line, bool isHeader, out string[] formattedCells, out string[] plainCells)
    {
        var inner = line.Trim();
        if (inner.StartsWith("|", StringComparison.Ordinal))
        {
            inner = inner.Substring(1);
        }
        if (inner.EndsWith("|", StringComparison.Ordinal))
        {
            inner = inner.Substring(0, inner.Length - 1);
        }

        var parts = inner.Split('|');
        formattedCells = new string[parts.Length];
        plainCells = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var raw = parts[i].Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase).Trim();
            plainCells[i] = StripMarkdownFormatting(raw);
            formattedCells[i] = FormatInlineRichText(raw, wrapBold: isHeader);
        }
    }

    public static string EscapeRichText(string? input)
        => Authoring.ModManualCatalog.EscapeRichText(input);

    public static string FormatInlineRichText(string input, bool wrapBold = false)
        => Authoring.ModManualCatalog.FormatInlineRichText(input, wrapBold);

    private static string StripMarkdownFormatting(string input)
    {
        var s = input.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase);
        s = BoldTagRegex.Replace(s, "$1");
        s = InlineCodeRegex.Replace(s, "$1");
        return s;
    }
}
