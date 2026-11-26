using System;
using System.Collections.Generic;
using System.IO;
using GiantCreatureFramework.Agent;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.UI;

public enum ModernButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger,
    Pill,
    AccentPill
}

/// <summary>
/// Grok / Hermes Desktop 风格现代矢量 UI 渲染套件。
/// 基于 SDF (有符号距离场) 动态生成抗锯齿九宫格圆角纹理，彻底替代原版生硬直角矩形与原生输入框边框。
/// </summary>
[StaticConstructorOnStartup]
public static class ModernUiKit
{
    // 核心配色系统 (Grok Obsidian + Hermes Slate)
    public static readonly Color BgWindow = new(0.043f, 0.051f, 0.075f, 0.985f);       // #0B0D13
    public static readonly Color BgSidebar = new(0.031f, 0.039f, 0.059f, 0.99f);       // #080A0F
    public static readonly Color BgHeader = new(0.051f, 0.063f, 0.090f, 1f);           // #0D1017
    public static readonly Color BgDrawer = new(0.059f, 0.075f, 0.106f, 1f);           // #0F131B
    public static readonly Color BgCardAssistant = new(0.063f, 0.078f, 0.114f, 1f);    // #10141D
    public static readonly Color BgCardAssistantHeader = new(0.078f, 0.098f, 0.141f, 1f); // #141924
    public static readonly Color BgBubbleUser = new(0.102f, 0.141f, 0.220f, 1f);       // #1A2438
    public static readonly Color BgComposer = new(0.071f, 0.090f, 0.133f, 1f);         // #121722
    public static readonly Color BgInputWell = new(0.035f, 0.043f, 0.067f, 1f);        // #090B11
    public static readonly Color BgCodeBody = new(0.027f, 0.035f, 0.051f, 1f);         // #07090D
    public static readonly Color BgCodeHeader = new(0.067f, 0.082f, 0.118f, 1f);       // #11151E
    public static readonly Color BgTableHeader = new(0.086f, 0.110f, 0.161f, 1f);      // #161C29
    public static readonly Color BgTableRowEven = new(0.047f, 0.059f, 0.086f, 1f);     // #0C0F16
    public static readonly Color BgTableRowOdd = new(0.063f, 0.078f, 0.114f, 1f);      // #10141D

    public static readonly Color BorderWindow = new(0.149f, 0.184f, 0.255f, 1f);       // #262F41
    public static readonly Color BorderSubtle = new(0.118f, 0.145f, 0.204f, 1f);       // #1E2534
    public static readonly Color BorderCard = new(0.133f, 0.169f, 0.239f, 1f);         // #222B3D
    public static readonly Color BorderUserBubble = new(0.188f, 0.263f, 0.408f, 1f);   // #304368
    public static readonly Color BorderInput = new(0.153f, 0.192f, 0.275f, 1f);        // #273146
    public static readonly Color BorderInputFocus = new(0.231f, 0.510f, 0.965f, 1f);   // #3B82F6

    public static readonly Color AccentPrimary = new(0.145f, 0.388f, 0.922f, 1f);      // #2563EB
    public static readonly Color AccentPrimaryHover = new(0.231f, 0.510f, 0.965f, 1f); // #3B82F6
    public static readonly Color AccentPrimaryBorder = new(0.376f, 0.647f, 0.980f, 1f);// #60A5FA
    public static readonly Color AccentCyan = new(0.380f, 0.710f, 0.992f, 1f);         // #61B5FD
    public static readonly Color AccentEmerald = new(0.063f, 0.725f, 0.506f, 1f);      // #10B981
    public static readonly Color AccentAmber = new(0.961f, 0.620f, 0.043f, 1f);        // #F59E0B
    public static readonly Color AccentDanger = new(0.863f, 0.149f, 0.149f, 1f);       // #DC2626

    public static readonly Color TextBright = new(0.961f, 0.969f, 0.988f, 1f);         // #F5F7FC
    public static readonly Color TextPrimary = new(0.886f, 0.910f, 0.945f, 1f);        // #E2E8F1
    public static readonly Color TextSecondary = new(0.580f, 0.639f, 0.722f, 1f);      // #94A3B8
    public static readonly Color TextMuted = new(0.392f, 0.455f, 0.545f, 1f);          // #64748B

    private static readonly Dictionary<int, GUIStyle> FillStyles = new();
    private static readonly Dictionary<(int radius, int thickness), GUIStyle> BorderStyles = new();
    private static readonly Dictionary<(int w, int h, int radius, int thickness), (Texture2D Tex, int LastFrame)> SingleQuadOutlineTextures = new();
    private static readonly List<Texture2D> RetiredOutlineTextures = new();
    private static int _lastOutlineCleanupFrame = -1;
    private static GUIStyle? _shadowStyle;
    private static Texture2D? _circleTex;
    private static Texture2D? _brandLogoTex;
    private static Texture2D? _customAvatarTex;
    private static string _cachedCustomAvatarPath = string.Empty;
    private static long _cachedCustomAvatarTicks = -1;
    private static bool _cachedCustomAvatarFailed;

    public static void InvalidateAvatarCache()
    {
        _cachedCustomAvatarPath = string.Empty;
        _cachedCustomAvatarTicks = -1;
        _cachedCustomAvatarFailed = false;
        if (_customAvatarTex != null)
        {
            UnityEngine.Object.Destroy(_customAvatarTex);
            _customAvatarTex = null;
        }
    }

    public static Texture2D BrandLogoTexture
    {
        get
        {
            var customPath = (GiantCreatureAgentMod.Settings?.CustomAvatarPath ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            {
                long ticks = -1;
                try
                {
                    ticks = File.GetLastWriteTimeUtc(customPath).Ticks;
                    if (string.Equals(_cachedCustomAvatarPath, customPath, StringComparison.OrdinalIgnoreCase) &&
                        _cachedCustomAvatarTicks == ticks)
                    {
                        if (_customAvatarTex != null)
                        {
                            return _customAvatarTex;
                        }
                        if (_cachedCustomAvatarFailed)
                        {
                            goto DefaultLogo;
                        }
                    }

                    var bytes = File.ReadAllBytes(customPath);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, bytes))
                    {
                        ApplySoftRoundedCornerMask(tex);
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = FilterMode.Bilinear;
                        if (_customAvatarTex != null)
                        {
                            UnityEngine.Object.Destroy(_customAvatarTex);
                        }
                        _customAvatarTex = tex;
                        _cachedCustomAvatarPath = customPath;
                        _cachedCustomAvatarTicks = ticks;
                        _cachedCustomAvatarFailed = false;
                        return _customAvatarTex;
                    }
                    UnityEngine.Object.Destroy(tex);
                    if (_customAvatarTex != null)
                    {
                        UnityEngine.Object.Destroy(_customAvatarTex);
                        _customAvatarTex = null;
                    }
                    _cachedCustomAvatarPath = customPath;
                    _cachedCustomAvatarTicks = ticks;
                    _cachedCustomAvatarFailed = true;
                }
                catch
                {
                    if (_customAvatarTex != null)
                    {
                        UnityEngine.Object.Destroy(_customAvatarTex);
                        _customAvatarTex = null;
                    }
                    _cachedCustomAvatarPath = customPath;
                    _cachedCustomAvatarTicks = ticks;
                    _cachedCustomAvatarFailed = true;
                    // Fall back to default logo texture if custom image cannot be decoded
                }
            }

        DefaultLogo:
            if (_brandLogoTex == null)
            {
                _brandLogoTex = ContentFinder<Texture2D>.Get("UI/Icons/GCF_Agent_Logo", reportFailure: false);
            }
            return _brandLogoTex ?? BaseContent.WhiteTex;
        }
    }

    private static void ApplySoftRoundedCornerMask(Texture2D tex)
    {
        var w = tex.width;
        var h = tex.height;
        if (w < 8 || h < 8 || w > 2048 || h > 2048)
        {
            return;
        }

        var pixels = tex.GetPixels();
        var halfW = w * 0.5f;
        var halfH = h * 0.5f;
        var radius = Mathf.Min(w, h) * 0.1875f;
        var bx = halfW - radius - 0.5f;
        var by = halfH - radius - 0.5f;

        for (var y = 0; y < h; y++)
        {
            var py = Mathf.Abs(y + 0.5f - halfH);
            var qy = py - by;
            for (var x = 0; x < w; x++)
            {
                var px = Mathf.Abs(x + 0.5f - halfW);
                var qx = px - bx;
                if (qx <= -1f && qy <= -1f)
                {
                    continue;
                }

                var dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                           + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                var maskAlpha = Mathf.Clamp01(0.5f - dist);
                var idx = y * w + x;
                var c = pixels[idx];
                c.a = Mathf.Min(c.a, maskAlpha);
                pixels[idx] = c;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
    }

    public static void DrawBrandAvatar(Rect rect, bool interactive = false)
    {
        var tex = BrandLogoTexture;
        var prev = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true);
        GUI.color = prev;
        if (interactive)
        {
            TooltipHandler.TipRegion(rect, UiText.Get("GCFAgent_BrandAvatarTip"));
            if (Widgets.ButtonInvisible(rect))
            {
                Dialog_PersonaAndAvatar.Open();
            }
        }
    }

    public static void DrawDropShadow(Rect rect, float spread = 10f, float alpha = 0.45f)
    {
        if (rect.width <= 4f || rect.height <= 4f) return;
        var shadowRect = new Rect(rect.x - spread, rect.y - spread + 3f, rect.width + spread * 2f, rect.height + spread * 2f);
        var style = GetShadowStyle();
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, alpha);
        GUI.Box(shadowRect, GUIContent.none, style);
        GUI.color = prev;
    }

    public static void DrawRoundedRect(Rect rect, Color fillColor, int radius = 8)
    {
        if (rect.width <= 0.5f || rect.height <= 0.5f || fillColor.a <= 0.001f)
        {
            return;
        }

        if (Event.current != null && Event.current.type != EventType.Repaint)
        {
            return;
        }

        var maxRadius = Mathf.Min(rect.width, rect.height) * 0.5f;
        var r = Mathf.Clamp(radius, 0f, maxRadius);
        var backend = UnityStubs.Gui.GuiBackend.Current;
        if (backend != null)
        {
            if (r <= 0.5f)
            {
                backend.FillRect(rect, fillColor);
            }
            else
            {
                backend.FillRectRounded(rect, fillColor, r);
            }
            return;
        }

        // Fallback if backend is unavailable
        var fallbackMaxRadius = Mathf.FloorToInt(maxRadius) - 2;
        if (radius <= 1 || rect.width < 8f || rect.height < 8f || fallbackMaxRadius < 2)
        {
            var prevSolid = GUI.color;
            Widgets.DrawBoxSolid(rect, fillColor);
            GUI.color = prevSolid;
            return;
        }

        var clampedR = Mathf.Clamp(radius, 2, Mathf.Min(16, fallbackMaxRadius));
        var style = GetFillStyle(clampedR);
        var prev = GUI.color;
        GUI.color = fillColor;
        GUI.Box(rect, GUIContent.none, style);
        GUI.color = prev;
    }

    public static void DrawRoundedOutline(Rect rect, Color borderColor, int radius = 8, int thickness = 1)
    {
        if (rect.width <= 1f || rect.height <= 1f || borderColor.a <= 0.001f || thickness <= 0)
        {
            return;
        }

        if (Event.current != null && Event.current.type != EventType.Repaint)
        {
            return;
        }

        var maxRadius = Mathf.FloorToInt(Mathf.Min(rect.width, rect.height) * 0.5f) - 1;
        if (radius <= 1 || rect.width < 8f || rect.height < 8f || maxRadius < 2)
        {
            DrawSolidOutline(rect, borderColor, Mathf.Max(1, thickness));
            return;
        }

        var r = Mathf.Clamp(radius, 2, Mathf.Min(16, maxRadius));
        var w = Mathf.RoundToInt(rect.width);
        var h = Mathf.RoundToInt(rect.height);

        // Single-quad SDF texture avoids GUIStyle 9-slice sub-rect rounding seams in ImGuiGuiBackend.AddImageRect
        if (w >= 8 && h >= 8 && w <= 640 && h <= 240)
        {
            var outlineTex = GetSingleQuadOutlineTexture(w, h, r, Mathf.Max(1, thickness));
            var prevTexColor = GUI.color;
            GUI.color = borderColor;
            GUI.DrawTexture(rect, outlineTex, ScaleMode.StretchToFill, true);
            GUI.color = prevTexColor;
            return;
        }

        var style = GetBorderStyle(r, thickness);
        var prev = GUI.color;
        GUI.color = borderColor;
        GUI.Box(rect, GUIContent.none, style);
        GUI.color = prev;
    }

    public static void DrawSolidOutline(Rect rect, Color color, int thickness = 1)
    {
        var t = Mathf.Max(1f, thickness);
        var prev = GUI.color;
        Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, t), color);
        Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - t, rect.width, t), color);
        Widgets.DrawBoxSolid(new Rect(rect.x, rect.y + t, t, Mathf.Max(0f, rect.height - t * 2f)), color);
        Widgets.DrawBoxSolid(new Rect(rect.xMax - t, rect.y + t, t, Mathf.Max(0f, rect.height - t * 2f)), color);
        GUI.color = prev;
    }

    public static void DrawRoundedCard(Rect rect, Color fillColor, Color borderColor, int radius = 10, int borderThickness = 1)
    {
        if (rect.width <= 0.5f || rect.height <= 0.5f)
        {
            return;
        }

        var hasBorder = borderColor.a > 0.001f && borderThickness > 0;
        if (!hasBorder)
        {
            DrawRoundedRect(rect, fillColor, radius);
            return;
        }

        // For opaque / near-opaque fills (buttons, cards, input wells, bubbles, windows),
        // render both border and fill via ImGui's native vector FillRectRounded so zero 9-slice tile seams can ever occur.
        var backend = UnityStubs.Gui.GuiBackend.Current;
        if (backend != null && fillColor.a >= 0.90f && rect.width > borderThickness * 2f && rect.height > borderThickness * 2f)
        {
            if (Event.current != null && Event.current.type != EventType.Repaint)
            {
                return;
            }

            var t = Mathf.Max(1f, borderThickness);
            var maxR = Mathf.Min(rect.width, rect.height) * 0.5f;
            var outerR = Mathf.Clamp(radius, 0f, maxR);
            var innerR = Mathf.Max(0f, outerR - t);
            var effectiveBorder = borderColor.a >= 0.99f
                ? new Color(borderColor.r, borderColor.g, borderColor.b, fillColor.a)
                : new Color(
                    Mathf.Lerp(fillColor.r, borderColor.r, borderColor.a),
                    Mathf.Lerp(fillColor.g, borderColor.g, borderColor.a),
                    Mathf.Lerp(fillColor.b, borderColor.b, borderColor.a),
                    fillColor.a);

            if (outerR <= 0.5f)
            {
                backend.FillRect(rect, effectiveBorder);
            }
            else
            {
                backend.FillRectRounded(rect, effectiveBorder, outerR);
            }

            var innerRect = new Rect(rect.x + t, rect.y + t, rect.width - t * 2f, rect.height - t * 2f);
            if (innerR <= 0.5f)
            {
                backend.FillRect(innerRect, fillColor);
            }
            else
            {
                backend.FillRectRounded(innerRect, fillColor, innerR);
            }
            return;
        }

        DrawRoundedRect(rect, fillColor, radius);
        DrawRoundedOutline(rect, borderColor, radius, borderThickness);
    }

    public static void DrawCircle(Rect rect, Color color)
    {
        if (_circleTex == null)
        {
            _circleTex = CreateCircleTexture(32);
        }

        var prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, _circleTex, ScaleMode.ScaleToFit, true);
        GUI.color = prev;
    }

    public static void DrawHorizontalHairline(float x, float y, float width, Color color)
    {
        var prev = GUI.color;
        Widgets.DrawBoxSolid(new Rect(x, y, width, 1f), color);
        GUI.color = prev;
    }

    public static void DrawVerticalHairline(float x, float y, float height, Color color)
    {
        var prev = GUI.color;
        Widgets.DrawBoxSolid(new Rect(x, y, 1f, height), color);
        GUI.color = prev;
    }

    public static string TruncateSingleLine(string? text, float maxWidth, GameFont font = GameFont.Small)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 8f)
        {
            return string.Empty;
        }

        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        var prevFont = Text.Font;
        var prevWrap = Text.WordWrap;
        Text.Font = font;
        Text.WordWrap = false;
        try
        {
            if (Text.CalcSize(clean).x <= maxWidth)
            {
                return clean;
            }

            const string ellipsis = "...";
            var ellipsisW = Text.CalcSize(ellipsis).x;
            if (ellipsisW >= maxWidth)
            {
                return ellipsis;
            }

            var lo = 0;
            var hi = clean.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                var candidate = clean.Substring(0, mid) + ellipsis;
                if (Text.CalcSize(candidate).x <= maxWidth)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo > 0 ? clean.Substring(0, lo) + ellipsis : ellipsis;
        }
        finally
        {
            Text.Font = prevFont;
            Text.WordWrap = prevWrap;
        }
    }

    public static void DrawSingleLineLabel(
        Rect rect,
        string? text,
        GameFont font,
        Color color,
        TextAnchor anchor = TextAnchor.MiddleLeft,
        string? tooltip = null)
    {
        if (rect.width <= 2f || rect.height <= 2f)
        {
            return;
        }

        var raw = text ?? string.Empty;
        var fitted = TruncateSingleLine(raw, rect.width - 2f, font);

        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevWrap = Text.WordWrap;
        var prevColor = GUI.color;

        Text.Font = font;
        Text.Anchor = anchor;
        Text.WordWrap = false;
        GUI.color = color;
        Widgets.Label(rect, fitted);

        Text.Font = prevFont;
        Text.Anchor = prevAnchor;
        Text.WordWrap = prevWrap;
        GUI.color = prevColor;

        if (!string.IsNullOrEmpty(tooltip))
        {
            TooltipHandler.TipRegion(rect, tooltip);
        }
        else if (!string.Equals(fitted, raw, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(raw))
        {
            TooltipHandler.TipRegion(rect, raw);
        }
    }

    public static bool DrawModernCheckbox(Rect rect, string label, ref bool value, string? tooltip = null, GameFont font = GameFont.Small)
    {
        var hovered = Mouse.IsOver(rect);
        var boxSize = 18f;
        var boxY = rect.y + (rect.height - boxSize) * 0.5f;
        var boxRect = new Rect(rect.x + 2f, boxY, boxSize, boxSize);

        var fill = value
            ? (hovered ? AccentPrimaryHover : AccentPrimary)
            : (hovered ? new Color(0.110f, 0.141f, 0.208f, 1f) : BgInputWell);
        var border = value
            ? AccentPrimaryBorder
            : (hovered ? AccentPrimaryHover : BorderInput);

        DrawRoundedCard(boxRect, fill, border, 5, 1);
        if (value)
        {
            DrawSingleLineLabel(boxRect, "✓", GameFont.Tiny, TextBright, TextAnchor.MiddleCenter);
        }

        var labelRect = new Rect(boxRect.xMax + 8f, rect.y, Mathf.Max(10f, rect.width - boxSize - 12f), rect.height);
        DrawSingleLineLabel(labelRect, label, font, hovered ? TextBright : TextPrimary, TextAnchor.MiddleLeft, tooltip);

        if (Widgets.ButtonInvisible(rect))
        {
            value = !value;
            return true;
        }

        return false;
    }

    public static int DrawBudgetStepperInt(
        Rect rect,
        string title,
        string subtitle,
        int value,
        int min,
        int max,
        int step,
        string unit = "",
        string? zeroLabel = null)
    {
        var hovered = Mouse.IsOver(rect);
        DrawRoundedCard(
            rect,
            hovered ? new Color(0.067f, 0.086f, 0.125f, 1f) : BgTableRowEven,
            hovered ? BorderCard : BorderSubtle,
            7,
            1);

        var controlsWidth = Mathf.Clamp(rect.width * 0.46f, 180f, 270f);
        var leftWidth = Mathf.Max(80f, rect.width - controlsWidth - 24f);

        var titleRect = new Rect(rect.x + 10f, rect.y + 4f, leftWidth, 20f);
        var subRect = new Rect(rect.x + 10f, rect.y + 22f, leftWidth, 18f);
        DrawSingleLineLabel(titleRect, title, GameFont.Small, TextPrimary, TextAnchor.MiddleLeft, subtitle);
        DrawSingleLineLabel(subRect, subtitle, GameFont.Tiny, TextMuted, TextAnchor.MiddleLeft);

        var rightStart = rect.xMax - controlsWidth - 8f;
        var valText = value == 0 && !string.IsNullOrEmpty(zeroLabel)
            ? zeroLabel!
            : (string.IsNullOrEmpty(unit) ? value.ToString("N0") : $"{value:N0} {unit}");
        var badgeW = 78f;
        var badgeRect = new Rect(rightStart, rect.y + (rect.height - 24f) * 0.5f, badgeW, 24f);
        DrawRoundedCard(badgeRect, BgInputWell, BorderInput, 5, 1);
        DrawSingleLineLabel(badgeRect, valText, GameFont.Tiny, AccentCyan, TextAnchor.MiddleCenter);

        var minusRect = new Rect(badgeRect.xMax + 6f, rect.y + (rect.height - 24f) * 0.5f, 24f, 24f);
        if (DrawButton(minusRect, "-", ModernButtonVariant.Secondary, 5, false, GameFont.Tiny))
        {
            value = Math.Clamp(value - step, min, max);
        }

        var sliderW = Mathf.Max(46f, rect.xMax - 8f - 24f - 6f - (minusRect.xMax + 6f));
        var sliderRect = new Rect(minusRect.xMax + 6f, rect.y + (rect.height - 20f) * 0.5f, sliderW, 20f);
        var floatVal = DrawModernSlider(sliderRect, value, min, max);
        var snapped = step > 1 ? Mathf.RoundToInt(floatVal / step) * step : Mathf.RoundToInt(floatVal);
        if (Mathf.Abs(floatVal - value) > 0.01f)
        {
            value = Math.Clamp(snapped, min, max);
        }

        var plusRect = new Rect(sliderRect.xMax + 6f, rect.y + (rect.height - 24f) * 0.5f, 24f, 24f);
        if (DrawButton(plusRect, "+", ModernButtonVariant.Secondary, 5, false, GameFont.Tiny))
        {
            value = Math.Clamp(value + step, min, max);
        }

        return Math.Clamp(value, min, max);
    }

    public static float DrawBudgetStepperFloat(
        Rect rect,
        string title,
        string subtitle,
        float value,
        float min,
        float max,
        float step)
    {
        var hovered = Mouse.IsOver(rect);
        DrawRoundedCard(
            rect,
            hovered ? new Color(0.067f, 0.086f, 0.125f, 1f) : BgTableRowEven,
            hovered ? BorderCard : BorderSubtle,
            7,
            1);

        var controlsWidth = Mathf.Clamp(rect.width * 0.46f, 180f, 270f);
        var leftWidth = Mathf.Max(80f, rect.width - controlsWidth - 24f);

        var titleRect = new Rect(rect.x + 10f, rect.y + 4f, leftWidth, 20f);
        var subRect = new Rect(rect.x + 10f, rect.y + 22f, leftWidth, 18f);
        DrawSingleLineLabel(titleRect, title, GameFont.Small, TextPrimary, TextAnchor.MiddleLeft, subtitle);
        DrawSingleLineLabel(subRect, subtitle, GameFont.Tiny, TextMuted, TextAnchor.MiddleLeft);

        var rightStart = rect.xMax - controlsWidth - 8f;
        var badgeW = 78f;
        var badgeRect = new Rect(rightStart, rect.y + (rect.height - 24f) * 0.5f, badgeW, 24f);
        DrawRoundedCard(badgeRect, BgInputWell, BorderInput, 5, 1);
        DrawSingleLineLabel(badgeRect, $"{value:F2}", GameFont.Tiny, AccentCyan, TextAnchor.MiddleCenter);

        var minusRect = new Rect(badgeRect.xMax + 6f, rect.y + (rect.height - 24f) * 0.5f, 24f, 24f);
        if (DrawButton(minusRect, "-", ModernButtonVariant.Secondary, 5, false, GameFont.Tiny))
        {
            value = (float)Math.Round(Mathf.Clamp(value - step, min, max), 2);
        }

        var sliderW = Mathf.Max(46f, rect.xMax - 8f - 24f - 6f - (minusRect.xMax + 6f));
        var sliderRect = new Rect(minusRect.xMax + 6f, rect.y + (rect.height - 20f) * 0.5f, sliderW, 20f);
        value = DrawModernSlider(sliderRect, value, min, max);

        var plusRect = new Rect(sliderRect.xMax + 6f, rect.y + (rect.height - 24f) * 0.5f, 24f, 24f);
        if (DrawButton(plusRect, "+", ModernButtonVariant.Secondary, 5, false, GameFont.Tiny))
        {
            value = (float)Math.Round(Mathf.Clamp(value + step, min, max), 2);
        }

        return Mathf.Clamp(value, min, max);
    }

    public static bool DrawButton(
        Rect rect,
        string label,
        ModernButtonVariant variant = ModernButtonVariant.Secondary,
        int radius = 8,
        bool active = false,
        GameFont font = GameFont.Small)
    {
        var hovered = Mouse.IsOver(rect);
        Color fill;
        Color border;
        Color textColor;

        switch (variant)
        {
            case ModernButtonVariant.Primary:
                fill = hovered ? AccentPrimaryHover : AccentPrimary;
                border = hovered ? new Color(0.576f, 0.773f, 0.992f, 1f) : AccentPrimaryBorder;
                textColor = TextBright;
                break;

            case ModernButtonVariant.Danger:
                fill = hovered ? new Color(0.360f, 0.118f, 0.145f, 1f) : new Color(0.247f, 0.086f, 0.110f, 1f);
                border = hovered ? new Color(0.937f, 0.267f, 0.267f, 1f) : new Color(0.600f, 0.145f, 0.165f, 1f);
                textColor = new Color(0.992f, 0.722f, 0.722f, 1f);
                break;

            case ModernButtonVariant.AccentPill:
                fill = active
                    ? new Color(0.118f, 0.227f, 0.412f, 1f)
                    : (hovered ? new Color(0.110f, 0.165f, 0.271f, 1f) : new Color(0.082f, 0.122f, 0.200f, 1f));
                border = active
                    ? AccentPrimaryHover
                    : (hovered ? new Color(0.231f, 0.420f, 0.745f, 1f) : new Color(0.165f, 0.255f, 0.416f, 1f));
                textColor = active ? TextBright : AccentCyan;
                break;

            case ModernButtonVariant.Pill:
                fill = active
                    ? new Color(0.118f, 0.200f, 0.345f, 1f)
                    : (hovered ? new Color(0.125f, 0.157f, 0.224f, 1f) : new Color(0.082f, 0.102f, 0.149f, 1f));
                border = active
                    ? AccentPrimaryHover
                    : (hovered ? new Color(0.235f, 0.294f, 0.412f, 1f) : new Color(0.153f, 0.192f, 0.271f, 1f));
                textColor = active ? TextBright : (hovered ? TextBright : TextSecondary);
                break;

            case ModernButtonVariant.Ghost:
                fill = hovered ? new Color(0.125f, 0.153f, 0.216f, 1f) : Color.clear;
                border = hovered ? BorderCard : Color.clear;
                textColor = hovered ? TextBright : TextSecondary;
                break;

            default:
                fill = active
                    ? new Color(0.118f, 0.200f, 0.345f, 1f)
                    : (hovered ? new Color(0.129f, 0.161f, 0.227f, 1f) : new Color(0.090f, 0.114f, 0.165f, 1f));
                border = active
                    ? AccentPrimaryHover
                    : (hovered ? new Color(0.247f, 0.310f, 0.435f, 1f) : new Color(0.165f, 0.208f, 0.294f, 1f));
                textColor = hovered || active ? TextBright : TextPrimary;
                break;
        }

        if (fill.a > 0.001f || border.a > 0.001f)
        {
            DrawRoundedCard(rect, fill, border, radius, 1);
        }

        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevWrap = Text.WordWrap;
        var prevColor = GUI.color;

        var fitted = TruncateSingleLine(label, Mathf.Max(8f, rect.width - 6f), font);
        Text.Font = font;
        Text.Anchor = TextAnchor.MiddleCenter;
        Text.WordWrap = false;
        GUI.color = textColor;
        Widgets.Label(rect, fitted);

        Text.Font = prevFont;
        Text.Anchor = prevAnchor;
        Text.WordWrap = prevWrap;
        GUI.color = prevColor;

        return Widgets.ButtonInvisible(rect);
    }

    public static bool DrawCloseIconButton(Rect rect, int radius = 7)
    {
        var hovered = Mouse.IsOver(rect);
        var fill = hovered ? AccentDanger : new Color(0.086f, 0.106f, 0.153f, 1f);
        var border = hovered ? new Color(0.961f, 0.353f, 0.353f, 1f) : new Color(0.153f, 0.192f, 0.271f, 1f);
        DrawRoundedCard(rect, fill, border, radius, 1);

        var prevFont = Text.Font;
        var prevAnchor = Text.Anchor;
        var prevWrap = Text.WordWrap;
        var prevColor = GUI.color;

        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.MiddleCenter;
        Text.WordWrap = false;
        GUI.color = hovered ? TextBright : TextSecondary;
        Widgets.Label(rect, "×");

        Text.Font = prevFont;
        Text.Anchor = prevAnchor;
        Text.WordWrap = prevWrap;
        GUI.color = prevColor;

        return Widgets.ButtonInvisible(rect);
    }

    private static Texture2D? _cameraIconTex;

    public static bool DrawCameraIconButton(Rect rect, int radius = 8, bool disabled = false, string? tooltip = null)
    {
        var hovered = !disabled && Mouse.IsOver(rect);
        var pressed = hovered && Input.GetMouseButton(0);

        if (hovered)
        {
            var fill = pressed
                ? new Color(0.102f, 0.145f, 0.227f, 1f)
                : new Color(0.118f, 0.157f, 0.239f, 0.9f);
            var border = new Color(0.220f, 0.310f, 0.460f, 1f);
            DrawRoundedCard(rect, fill, border, radius, 1);
        }

        var iconSize = Mathf.Min(rect.width, rect.height) - 8f;
        iconSize = Mathf.Clamp(iconSize, 14f, 20f);
        var iconRect = new Rect(
            rect.x + (rect.width - iconSize) * 0.5f,
            rect.y + (rect.height - iconSize) * 0.5f,
            iconSize,
            iconSize);

        var prevColor = GUI.color;
        GUI.color = disabled
            ? TextMuted
            : (hovered ? AccentCyan : TextSecondary);
        GUI.DrawTexture(iconRect, GetCameraIconTexture(), ScaleMode.ScaleToFit, true);
        GUI.color = prevColor;

        if (!string.IsNullOrEmpty(tooltip))
        {
            TooltipHandler.TipRegion(rect, tooltip);
        }

        return !disabled && Widgets.ButtonInvisible(rect);
    }

    private static Texture2D GetCameraIconTexture()
    {
        if (_cameraIconTex != null)
        {
            return _cameraIconTex;
        }

        const int size = 48;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "GCFAgent_CameraIcon",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var pixels = new Color[size * size];
        const float aa = 1.25f / size;

        static float SdfRoundedBox(float px, float py, float cx, float cy, float hx, float hy, float r)
        {
            var qx = Mathf.Abs(px - cx) - (hx - r);
            var qy = Mathf.Abs(py - cy) - (hy - r);
            var outDist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
            var inDist = Mathf.Min(Mathf.Max(qx, qy), 0f);
            return outDist + inDist - r;
        }

        for (var y = 0; y < size; y++)
        {
            var v = 1f - (y + 0.5f) / size; // 0 = top, 1 = bottom
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size;

                // Camera main body + top viewfinder hump
                var dBody = SdfRoundedBox(u, v, 0.50f, 0.56f, 0.36f, 0.24f, 0.08f);
                var dPrism = SdfRoundedBox(u, v, 0.50f, 0.28f, 0.16f, 0.09f, 0.045f);
                var dOuter = Mathf.Min(dBody, dPrism);
                var bodyAlpha = Mathf.Clamp01(0.5f - dOuter / aa);

                // Circular lens cutout + inner solid lens core
                var ldx = u - 0.50f;
                var ldy = v - 0.56f;
                var lDist = Mathf.Sqrt(ldx * ldx + ldy * ldy);
                var lensCutout = Mathf.Clamp01(0.5f - (lDist - 0.165f) / aa);
                var lensInner = Mathf.Clamp01(0.5f - (lDist - 0.095f) / aa);

                // Top-right flash dot cutout
                var fdx = u - 0.73f;
                var fdy = v - 0.42f;
                var fDist = Mathf.Sqrt(fdx * fdx + fdy * fdy);
                var flashCutout = Mathf.Clamp01(0.5f - (fDist - 0.036f) / aa);

                var alpha = Mathf.Clamp01(bodyAlpha - lensCutout + lensInner - flashCutout * 0.85f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(false, false);
        _cameraIconTex = tex;
        return tex;
    }

    private static bool IsImeComposing() => !string.IsNullOrEmpty(Input.compositionString);

    public static string DrawModernTextField(Rect rect, string value, string placeholder = "", int radius = 7, string? controlName = null)
    {
        var effectiveName = string.IsNullOrEmpty(controlName)
            ? $"rc_tf_{(int)rect.x}_{(int)rect.y}_{(int)rect.width}"
            : controlName!;
        var focused = string.Equals(GUI.GetNameOfFocusedControl(), effectiveName, StringComparison.Ordinal);
        var hovered = Mouse.IsOver(rect);
        var border = focused
            ? BorderInputFocus
            : (hovered ? new Color(0.247f, 0.325f, 0.475f, 1f) : BorderInput);
        DrawRoundedCard(rect, BgInputWell, border, radius, 1);

        var inner = new Rect(rect.x + 10f, rect.y + 1f, Mathf.Max(8f, rect.width - 20f), rect.height - 2f);
        var showPlaceholder = string.IsNullOrEmpty(value) &&
                              !string.IsNullOrEmpty(placeholder) &&
                              !(focused && IsImeComposing());
        if (showPlaceholder && (Event.current == null || Event.current.type == EventType.Repaint))
        {
            DrawSingleLineLabel(inner, placeholder, GameFont.Tiny, TextMuted, TextAnchor.MiddleLeft);
        }

        GUI.SetNextControlName(effectiveName);
        return InvokeCleanTextField(inner, value ?? string.Empty, multiline: false);
    }

    public static string DrawModernPasswordField(Rect rect, string value, int radius = 7, string? controlName = null)
    {
        var effectiveName = string.IsNullOrEmpty(controlName)
            ? $"rc_pw_{(int)rect.x}_{(int)rect.y}_{(int)rect.width}"
            : controlName!;
        var focused = string.Equals(GUI.GetNameOfFocusedControl(), effectiveName, StringComparison.Ordinal);
        var hovered = Mouse.IsOver(rect);
        var border = focused
            ? BorderInputFocus
            : (hovered ? new Color(0.247f, 0.325f, 0.475f, 1f) : BorderInput);
        DrawRoundedCard(rect, BgInputWell, border, radius, 1);

        var inner = new Rect(rect.x + 10f, rect.y + 1f, Mathf.Max(8f, rect.width - 20f), rect.height - 2f);
        GUI.SetNextControlName(effectiveName);
        return InvokeCleanPasswordField(inner, value ?? string.Empty);
    }

    public static string DrawBareComposerInput(
        Rect rect,
        string value,
        string placeholder = "",
        string? controlName = null,
        bool multiline = true)
    {
        var focused = !string.IsNullOrEmpty(controlName) &&
                      string.Equals(GUI.GetNameOfFocusedControl(), controlName, StringComparison.Ordinal);
        var showPlaceholder = string.IsNullOrEmpty(value) &&
                              !string.IsNullOrEmpty(placeholder) &&
                              !((focused || string.IsNullOrEmpty(controlName)) && IsImeComposing());
        if (showPlaceholder && (Event.current == null || Event.current.type == EventType.Repaint))
        {
            var phRect = multiline
                ? new Rect(rect.x + 2f, rect.y + 4f, Mathf.Max(8f, rect.width - 4f), 22f)
                : rect;
            DrawSingleLineLabel(
                phRect,
                placeholder,
                GameFont.Small,
                TextMuted,
                multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft);
        }

        if (!string.IsNullOrEmpty(controlName))
        {
            GUI.SetNextControlName(controlName);
        }

        return InvokeCleanTextField(rect, value ?? string.Empty, multiline: multiline);
    }

    public static string DrawBareTextArea(Rect rect, string value, string? controlName = null)
    {
        if (!string.IsNullOrEmpty(controlName))
        {
            GUI.SetNextControlName(controlName);
        }

        return InvokeCleanTextField(rect, value ?? string.Empty, multiline: true);
    }

    public static string DrawModernTextArea(Rect rect, string value, string placeholder = "", int radius = 10, string? controlName = null)
    {
        var effectiveName = string.IsNullOrEmpty(controlName)
            ? $"rc_ta_{(int)rect.x}_{(int)rect.y}_{(int)rect.width}"
            : controlName!;
        var focused = string.Equals(GUI.GetNameOfFocusedControl(), effectiveName, StringComparison.Ordinal);
        var hovered = Mouse.IsOver(rect);
        var border = focused
            ? BorderInputFocus
            : (hovered ? new Color(0.255f, 0.353f, 0.533f, 1f) : BorderInput);
        DrawRoundedCard(rect, BgInputWell, border, radius, 1);

        var inner = new Rect(rect.x + 10f, rect.y + 6f, Mathf.Max(8f, rect.width - 20f), Mathf.Max(8f, rect.height - 12f));
        var showPlaceholder = string.IsNullOrEmpty(value) &&
                              !string.IsNullOrEmpty(placeholder) &&
                              !(focused && IsImeComposing());
        if (showPlaceholder && (Event.current == null || Event.current.type == EventType.Repaint))
        {
            DrawSingleLineLabel(
                new Rect(inner.x + 2f, inner.y + 2f, inner.width - 4f, 22f),
                placeholder,
                GameFont.Tiny,
                TextMuted,
                TextAnchor.UpperLeft);
        }

        GUI.SetNextControlName(effectiveName);
        return InvokeCleanTextField(inner, value ?? string.Empty, multiline: true);
    }

    public static float DrawModernSlider(Rect rect, float value, float min, float max)
    {
        var trackH = 6f;
        var trackY = rect.y + (rect.height - trackH) * 0.5f;
        var trackRect = new Rect(rect.x, trackY, rect.width, trackH);

        Widgets.DrawBoxSolid(trackRect, BgInputWell);
        DrawSolidOutline(trackRect, BorderInput, 1);

        var t = Mathf.Clamp01((value - min) / Math.Max(0.0001f, max - min));
        var fillW = Mathf.Max(2f, rect.width * t);
        Widgets.DrawBoxSolid(new Rect(rect.x, trackY, fillW, trackH), AccentPrimaryHover);

        var thumbSize = 14f;
        var thumbX = Mathf.Clamp(rect.x + rect.width * t - thumbSize * 0.5f, rect.x, rect.xMax - thumbSize);
        var thumbRect = new Rect(thumbX, rect.y + (rect.height - thumbSize) * 0.5f, thumbSize, thumbSize);
        DrawCircle(thumbRect, TextBright);

        if (Mouse.IsOver(rect) && Event.current.type is EventType.MouseDown or EventType.MouseDrag && Event.current.button == 0)
        {
            var mouseT = Mathf.Clamp01((Event.current.mousePosition.x - rect.x) / Math.Max(1f, rect.width));
            value = Mathf.Lerp(min, max, mouseT);
            Event.current.Use();
        }

        return (float)Math.Round(value, 2);
    }

    private static string InvokeCleanTextField(Rect rect, string value, bool multiline)
    {
        var prevFont = Text.Font;
        var prevColor = GUI.color;
        Text.Font = GameFont.Small;

        // UnityStubs.dll (RimWorld 1.7) checks `if (style.normal.background != null)` in DrawTextFieldChrome
        // and draws a hardcoded square FillRect + DrawBorder whenever normal.background is non-null (even ClearTex!).
        // It also sets ReadOnlyId when multiline && (style.name == "textfield" || style.name == "textarea") && normal.background == null.
        // Setting a custom style.name and null backgrounds completely disables the inner square chrome while keeping full editability.
        var baseStyle = multiline ? Text.CurTextAreaStyle : Text.CurTextFieldStyle;
        var style = new GUIStyle(baseStyle)
        {
            name = "RimCopilotCleanInput",
            alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft,
            wordWrap = multiline,
            clipping = TextClipping.Clip,
            border = new RectOffset(0, 0, 0, 0),
            padding = multiline ? new RectOffset(2, 2, 4, 4) : new RectOffset(2, 2, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            contentOffset = Vector2.zero
        };

        style.normal.background = null;
        style.hover.background = null;
        style.focused.background = null;
        style.active.background = null;
        style.onNormal.background = null;
        style.onHover.background = null;
        style.onFocused.background = null;
        style.onActive.background = null;

        style.normal.textColor = TextBright;
        style.hover.textColor = TextBright;
        style.focused.textColor = TextBright;
        style.active.textColor = TextBright;

        try
        {
            GUI.color = Color.white;
            return multiline
                ? GUI.TextArea(rect, value, style)
                : GUI.TextField(rect, value, style);
        }
        finally
        {
            Text.Font = prevFont;
            GUI.color = prevColor;
        }
    }

    private static string InvokeCleanPasswordField(Rect rect, string value)
    {
        var prevFont = Text.Font;
        var prevColor = GUI.color;
        Text.Font = GameFont.Small;

        var style = new GUIStyle(Text.CurTextFieldStyle)
        {
            name = "RimCopilotCleanPassword",
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Clip,
            border = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(2, 2, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            contentOffset = Vector2.zero
        };

        style.normal.background = null;
        style.hover.background = null;
        style.focused.background = null;
        style.active.background = null;
        style.onNormal.background = null;
        style.onHover.background = null;
        style.onFocused.background = null;
        style.onActive.background = null;

        style.normal.textColor = TextBright;
        style.hover.textColor = TextBright;
        style.focused.textColor = TextBright;
        style.active.textColor = TextBright;

        try
        {
            GUI.color = Color.white;
            return GUI.PasswordField(rect, value, '*', style);
        }
        finally
        {
            Text.Font = prevFont;
            GUI.color = prevColor;
        }
    }

    private static GUIStyle GetFillStyle(int radius)
    {
        if (FillStyles.TryGetValue(radius, out var existing) && existing.normal.background != null)
        {
            return existing;
        }

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var half = size * 0.5f;
        var r = (float)radius;
        var bx = half - r - 1f;
        var by = half - r - 1f;

        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var px = Mathf.Abs(x + 0.5f - half);
                var py = Mathf.Abs(y + 0.5f - half);
                var qx = px - bx;
                var qy = py - by;
                var dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                           + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                var alpha = Mathf.Clamp01(0.5f - dist);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();

        var border = Mathf.Clamp(radius + 1, 2, 20);
        var style = new GUIStyle
        {
            border = new RectOffset(border, border, border, border),
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0)
        };
        style.normal.background = tex;
        FillStyles[radius] = style;
        return style;
    }

    private static GUIStyle GetBorderStyle(int radius, int thickness)
    {
        var key = (radius, thickness);
        if (BorderStyles.TryGetValue(key, out var existing) && existing.normal.background != null)
        {
            return existing;
        }

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var half = size * 0.5f;
        var r = (float)radius;
        var bx = half - r - 1f;
        var by = half - r - 1f;
        var t = Mathf.Max(1f, thickness);

        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var px = Mathf.Abs(x + 0.5f - half);
                var py = Mathf.Abs(y + 0.5f - half);
                var qx = px - bx;
                var qy = py - by;
                var dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                           + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;

                var outerAlpha = Mathf.Clamp01(0.5f - dist);
                var innerAlpha = Mathf.Clamp01(0.5f - (dist + t));
                var ringAlpha = Mathf.Clamp01(outerAlpha - innerAlpha);
                pixels[y * size + x] = new Color(1f, 1f, 1f, ringAlpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();

        var border = Mathf.Clamp(radius + 1, 2, 20);
        var style = new GUIStyle
        {
            border = new RectOffset(border, border, border, border),
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0)
        };
        style.normal.background = tex;
        BorderStyles[key] = style;
        return style;
    }

    private static Texture2D GetSingleQuadOutlineTexture(int width, int height, int radius, int thickness)
    {
        var frame = Time.frameCount;
        if (RetiredOutlineTextures.Count > 0 &&
            Event.current?.type != EventType.Repaint &&
            frame != _lastOutlineCleanupFrame)
        {
            for (var i = 0; i < RetiredOutlineTextures.Count; i++)
            {
                if (RetiredOutlineTextures[i] != null)
                {
                    UnityEngine.Object.Destroy(RetiredOutlineTextures[i]);
                }
            }
            RetiredOutlineTextures.Clear();
            _lastOutlineCleanupFrame = frame;
        }

        var key = (width, height, radius, thickness);
        if (SingleQuadOutlineTextures.TryGetValue(key, out var existing) && existing.Tex != null)
        {
            SingleQuadOutlineTextures[key] = (existing.Tex, frame);
            return existing.Tex;
        }

        if (SingleQuadOutlineTextures.Count >= 96)
        {
            var staleEntries = SingleQuadOutlineTextures
                .Where(kv => kv.Value.LastFrame != frame)
                .OrderBy(kv => kv.Value.LastFrame)
                .Take(Math.Max(1, SingleQuadOutlineTextures.Count - 64))
                .ToList();
            foreach (var kv in staleEntries)
            {
                SingleQuadOutlineTextures.Remove(kv.Key);
                if (kv.Value.Tex != null)
                {
                    RetiredOutlineTextures.Add(kv.Value.Tex);
                }
            }
        }

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var halfW = width * 0.5f;
        var halfH = height * 0.5f;
        var r = (float)radius;
        var bx = halfW - r - 0.5f;
        var by = halfH - r - 0.5f;
        var t = Mathf.Max(1f, thickness);

        var pixels = new Color[width * height];
        for (var y = 0; y < height; y++)
        {
            var py = Mathf.Abs(y + 0.5f - halfH);
            var qy = py - by;
            for (var x = 0; x < width; x++)
            {
                var px = Mathf.Abs(x + 0.5f - halfW);
                var qx = px - bx;
                if (qx < -(t + 1.5f) && qy < -(t + 1.5f))
                {
                    pixels[y * width + x] = Color.clear;
                    continue;
                }

                var dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                           + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                var outerAlpha = Mathf.Clamp01(0.5f - dist);
                var innerAlpha = Mathf.Clamp01(0.5f - (dist + t));
                var ringAlpha = Mathf.Clamp01(outerAlpha - innerAlpha);
                pixels[y * width + x] = new Color(1f, 1f, 1f, ringAlpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        SingleQuadOutlineTextures[key] = (tex, frame);
        return tex;
    }

    private static GUIStyle GetShadowStyle()
    {
        if (_shadowStyle != null && _shadowStyle.normal.background != null)
        {
            return _shadowStyle;
        }

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var half = size * 0.5f;
        var r = 10f;
        var spread = 12f;
        var bx = half - r - spread;
        var by = half - r - spread;

        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var px = Mathf.Abs(x + 0.5f - half);
                var py = Mathf.Abs(y + 0.5f - half);
                var qx = px - bx;
                var qy = py - by;
                var dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                           + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                var norm = Mathf.Clamp01(1f - Mathf.Max(0f, dist) / spread);
                var soft = norm * norm * (3f - 2f * norm);
                pixels[y * size + x] = new Color(1f, 1f, 1f, soft);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();

        _shadowStyle = new GUIStyle
        {
            border = new RectOffset(24, 24, 24, 24),
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0)
        };
        _shadowStyle.normal.background = tex;
        return _shadowStyle;
    }

    private static Texture2D CreateCircleTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var half = size * 0.5f;
        var r = half - 1.2f;
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5f - half;
                var dy = y + 0.5f - half;
                var d = Mathf.Sqrt(dx * dx + dy * dy) - r;
                var a = Mathf.Clamp01(0.5f - d);
                pixels[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }
}
