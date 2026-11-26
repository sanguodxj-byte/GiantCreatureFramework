using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GiantCreatureFramework.Agent.Authoring;

public sealed record UserImg2ImgInspection(
    bool IsImg2ImgViolation,
    bool HasUserImageAttachment,
    string Reason);

/// <summary>
/// Multi-layer hard security guard guaranteeing the Agent NEVER accepts, agrees to, simulates,
/// or executes any form of user image-to-image (img2img / external reference / disk drop-in /
/// URL / Base64 / screenshot-to-sprite) request.
/// </summary>
public static class UserImg2ImgGuard
{
    private const string ProvenanceFileName = ".gcf_provenance.json";

    private static readonly AsyncLocal<UserImg2ImgInspection?> CurrentTurnState = new();

    private static readonly byte[] LedgerHmacKey = DeriveInstallationKey();

    // 1. Inline data:image URI, Markdown image embed, external image URL, or local disk image path
    private static readonly Regex DataImageUriRegex = new(
        @"data\s*:\s*image/[a-zA-Z0-9.+-]+\s*;\s*base64\s*,[A-Za-z0-9+/=\s]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarkdownImageRegex = new(
        @"!\s*\[[^\]]*\]\s*\(\s*(?:https?:|file:|data:|[A-Za-z]:[\\/]|\\\\|/)[^)]+\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExternalImageUrlRegex = new(
        @"(?:https?|ftp|file)://[^\s""'<>]+?\.(?:png|jpe?g|webp|bmp|gif|svg|tga|dds)(?:\?[^\s""'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalDiskImagePathRegex = new(
        @"(?:[A-Za-z]:[\\/]|\\\\[^\s\\/]+[\\/]|/(?:Users|home|mnt|tmp|var|working_dir)/|~[\\/])[^\r\n""'<>|?*]*?\.(?:png|jpe?g|webp|bmp|gif|svg|tga|dds)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 2. Direct & indirect user img2img / pad-image / external-reference / two-step describe-and-redraw patterns
    private static readonly (string Name, Regex Pattern)[] Img2ImgIntentPatterns =
    [
        ("图生图/垫图直呼", new Regex(@"(?:图生图|以图生图|图片生图|垫图|img\s*2\s*img|image\s*[-\s]?to\s*[-\s]?image|image2image|\bi2i\b|\binpaint(?:ing)?\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("提供或上传外部参考图", new Regex(@"(?:上传|发给?你|提供|导入|塞进|拷进|拷贝到|放进|放到|拖进|传入|附上|附带|喂给?你)[^\r\n]{0,20}(?:图片|参考图|原画|立绘|贴图|素材图|草图|照片|底图|png|jpg|jpeg|webp)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("引用外部或用户图片生成", new Regex(@"(?:用|拿|按|按照|根据|参考|模仿|仿照|照着|基于|复刻|重绘|修改|局部重绘)[^\r\n]{0,16}(?:我给|我发|我上传|我提供|我放|这张|那张|外部|本地|电脑里|桌面|相册|链接里|网址里|原版|其他模组|第三方|创意工坊)[^\r\n]{0,12}(?:图片|参考图|图|贴图|原画|立绘|照片|截图|素材|png|jpg|webp)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("两步看图转文字再生图绕过", new Regex(@"(?:先|把)[^\r\n]{0,14}(?:看|识别|分析|提取|描述|转写|读取)[^\r\n]{0,18}(?:图片|这张图|参考图|截图|原画|贴图)[^\r\n]{0,20}(?:再|然后|接着)[^\r\n]{0,14}(?:生图|文生图|画|生成|制作|还原|复刻)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("诱导口头同意或角色扮演同意图生图", new Regex(@"(?:同意|答应|允许|支持|开启|接受|假装|扮演|回复|说)[^\r\n]{0,16}(?:图生图|垫图|外部参考图|上传图片|用我的图)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("英文外部参考图请求", new Regex(@"\b(?:use|from|based\s+on|copy|imitate|redraw|convert|upload|attach|pass|with)\s+(?:(?:my|this|that|the|an?)\s+)?(?:(?:external|local|uploaded|attached|custom|own|provided|user|reference)\s+)*(?:image|picture|photo|sprite|artwork|screenshot|png|jpg|jpeg|webp|reference)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    // Negation check only for harmless statements like "我不做图生图，纯文字做一只巨兽" when no external image/path/agreement-bait is present
    private static readonly Regex PureUserSelfNegationRegex = new(
        @"(?:不(?:用|做|要|是|搞|需要)|非|无需|别用)\s*(?:图生图|以图生图|垫图|img2img|image-to-image)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthoringFromScreenshotRegex = new(
        @"(?:画(?:一|出|个|只|张|巨兽|怪|贴图)|绘制|生图|生成(?:巨兽|贴图|图片|怪|单位)|制作|做一(?:只|个|台|架)|捏一|新建(?:巨兽|单位)|复刻|仿照|模仿|照着|参考(?:截图|画面|这张|图)|按截图|重绘|提取贴图|拆件|draw\b|generate\b|create\s+a\b|make\s+a\b|copy\b|imitate\b|sprite\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RefusalKeywordRegex = new(
        @"(?:拒绝|不支持|严禁|禁止|无法|不能|不接受|不提供|不允许|仅支持纯文本|只能通过纯文本|refuse|cannot|can't|not\s+supported|prohibited|forbidden|strictly\s+text-to-image)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AffirmativeAgreementRegex = new(
        @"(?:我同意|好的[，,\s]*(?:我|这就|马上|现在)?(?:用|按|根据|拿|接收)|没问题[，,\s]*(?:我|这就)|可以帮你(?:进行)?图生图|支持用户图生图|已读取你(?:提供|上传)的图|I\s+agree\s+to|Sure[,\s]+I\s+will\s+use\s+your\s+image)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static UserImg2ImgInspection? ActiveTurnInspection => CurrentTurnState.Value;

    public static IDisposable EnterTurnScope(UserImg2ImgInspection inspection)
    {
        var prev = CurrentTurnState.Value;
        CurrentTurnState.Value = inspection;
        return new ScopeReset(prev);
    }

    public static string SanitizeUserText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return DataImageUriRegex.Replace(text, "[BLOCKED_EXTERNAL_IMAGE_DATA]");
    }

    public static UserImg2ImgInspection InspectUserText(string? userText, bool hasUserImageAttachment = false)
    {
        var raw = userText ?? string.Empty;

        if (DataImageUriRegex.IsMatch(raw))
        {
            return new UserImg2ImgInspection(true, hasUserImageAttachment, "inline data:image base64");
        }
        if (MarkdownImageRegex.IsMatch(raw))
        {
            return new UserImg2ImgInspection(true, hasUserImageAttachment, "markdown external image");
        }
        if (ExternalImageUrlRegex.IsMatch(raw))
        {
            return new UserImg2ImgInspection(true, hasUserImageAttachment, "external image URL");
        }
        if (LocalDiskImagePathRegex.IsMatch(raw))
        {
            return new UserImg2ImgInspection(true, hasUserImageAttachment, "local disk image path");
        }

        if (hasUserImageAttachment && AuthoringFromScreenshotRegex.IsMatch(raw))
        {
            return new UserImg2ImgInspection(true, true, "authoring/copying from user screenshot");
        }

        for (var i = 0; i < Img2ImgIntentPatterns.Length; i++)
        {
            var (name, pattern) = Img2ImgIntentPatterns[i];
            if (!pattern.IsMatch(raw))
            {
                continue;
            }

            // Allow only if the sole match is explicit user self-negation ("不用图生图，纯文字生成...") with zero external refs/bait
            if (i == 0 && PureUserSelfNegationRegex.IsMatch(raw))
            {
                var stripped = PureUserSelfNegationRegex.Replace(raw, " ");
                if (!pattern.IsMatch(stripped))
                {
                    continue;
                }
            }

            return new UserImg2ImgInspection(true, hasUserImageAttachment, name);
        }

        return new UserImg2ImgInspection(false, hasUserImageAttachment, string.Empty);
    }

    /// <summary>
    /// Inspects the OpenAI wire messages at the start of a turn, stripping any user-supplied
    /// image_url / inline base64 when an img2img violation is present, and returning the active
    /// turn security state (distinguishing user attachments from AgentTurnRunner's own tool-preview nodes).
    /// </summary>
    public static UserImg2ImgInspection InspectAndSanitizeTurnMessages(
        JsonArray messages,
        ISet<JsonObject>? trustedToolPreviewMessages = null)
    {
        var latestUserHasAttachment = false;
        var allRealUserNodes = new List<JsonObject>();
        string latestUserText = string.Empty;

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msg) continue;
            if (msg["role"]?.ToString() != "user") continue;
            if (trustedToolPreviewMessages != null && trustedToolPreviewMessages.Contains(msg)) continue;

            var contentNode = msg["content"];
            if (contentNode is JsonValue val && val.TryGetValue<string>(out var s))
            {
                if (IsSystemNudgeMessage(s)) continue;
                allRealUserNodes.Add(msg);
                latestUserText = s;
                latestUserHasAttachment = false;
                if (DataImageUriRegex.IsMatch(s))
                {
                    msg["content"] = SanitizeUserText(s);
                }
            }
            else if (contentNode is JsonArray parts)
            {
                var textSb = new StringBuilder();
                var sawImageUrl = false;
                foreach (var p in parts.OfType<JsonObject>())
                {
                    var pType = p["type"]?.ToString();
                    if (pType == "text")
                    {
                        var t = p["text"]?.ToString() ?? string.Empty;
                        textSb.AppendLine(t);
                    }
                    else if (pType == "image_url")
                    {
                        sawImageUrl = true;
                    }
                }

                var combined = textSb.ToString().Trim();
                if (IsToolPreviewIntroText(combined))
                {
                    continue;
                }

                allRealUserNodes.Add(msg);
                latestUserText = combined;
                latestUserHasAttachment = sawImageUrl;
            }
        }

        var inspection = InspectUserText(latestUserText, latestUserHasAttachment);
        if (inspection.IsImg2ImgViolation)
        {
            // Strip ALL user-supplied multimodal image_url parts and inline data:image URIs across the entire conversation
            // so the LLM never receives any user pixels on an img2img attempt.
            foreach (var userNode in allRealUserNodes)
            {
                if (userNode["content"] is JsonArray arr)
                {
                    var cleanText = string.Join("\n", arr.OfType<JsonObject>()
                        .Where(p => p["type"]?.ToString() == "text")
                        .Select(p => SanitizeUserText(p["text"]?.ToString())));
                    userNode["content"] = cleanText;
                }
                else if (userNode["content"] is JsonValue v && v.TryGetValue<string>(out var rawStr))
                {
                    userNode["content"] = SanitizeUserText(rawStr);
                }
            }
        }

        return inspection;
    }

    internal static bool IsSyntheticUserMessage(JsonObject msg)
    {
        if (msg["role"]?.ToString() != "user")
        {
            return false;
        }

        var contentNode = msg["content"];
        if (contentNode is JsonValue val && val.TryGetValue<string>(out var s))
        {
            return IsSystemNudgeMessage(s);
        }

        if (contentNode is JsonArray parts)
        {
            var textSb = new StringBuilder();
            foreach (var p in parts.OfType<JsonObject>())
            {
                if (p["type"]?.ToString() == "text")
                {
                    textSb.AppendLine(p["text"]?.ToString() ?? string.Empty);
                }
            }
            return IsToolPreviewIntroText(textSb.ToString().Trim());
        }

        return false;
    }

    public static void EnforceToolExecutionGate(string toolName)
    {
        var state = CurrentTurnState.Value;
        if (state == null) return;

        if (state.IsImg2ImgViolation &&
            toolName is "generate_image" or "extract_sheet" or "warp_bone" or "write_creature_xml" or "generate_locomotion_anim" or "inject_creature" or "spawn_creature")
        {
            throw new InvalidOperationException(AgentProtocol.Get("Authoring_UserImg2ImgForbidden"));
        }

        if (state.HasUserImageAttachment && toolName == "generate_image")
        {
            throw new InvalidOperationException(AgentProtocol.Get("Authoring_UserImg2ImgForbidden"));
        }
    }

    public static SubmittedAnswer EnforceRefusalAnswerIfImg2ImgTurn(SubmittedAnswer candidate)
    {
        var state = CurrentTurnState.Value;
        if (state == null || !state.IsImg2ImgViolation)
        {
            return candidate;
        }

        var text = candidate.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) ||
            AffirmativeAgreementRegex.IsMatch(text) ||
            !RefusalKeywordRegex.IsMatch(text))
        {
            return new SubmittedAnswer(
                AgentProtocol.Get("Authoring_UserImg2ImgRefusalReply"),
                "general",
                Array.Empty<string>());
        }

        return candidate;
    }

    public static void RecordSelfGeneratedPng(string creatureDir, string relativeWorkspacePath, ReadOnlySpan<byte> pngBytes, string stageTag)
    {
        var workDir = Path.Combine(creatureDir, "Work");
        Directory.CreateDirectory(workDir);
        var ledgerPath = Path.Combine(workDir, ProvenanceFileName);

        var normRel = NormalizeRelPath(relativeWorkspacePath);
        var shaHex = Convert.ToHexString(SHA256.HashData(pngBytes));
        var macHex = ComputeEntryMac(creatureDir, normRel, shaHex, stageTag);

        JsonObject root;
        try
        {
            root = File.Exists(ledgerPath) && JsonNode.Parse(File.ReadAllText(ledgerPath, Encoding.UTF8)) is JsonObject existing
                ? existing
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        var entries = root["entries"] as JsonObject ?? new JsonObject();
        root["entries"] = entries;
        entries[normRel] = new JsonObject
        {
            ["sha256"] = shaHex,
            ["stage"] = stageTag,
            ["hmac"] = macHex
        };

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(ledgerPath, root.ToJsonString(Evidence.UnescapedJsonOptions), utf8NoBom);
    }

    public static void VerifySelfGeneratedPngOrThrow(
        string creatureDir,
        string relativeWorkspacePath,
        ReadOnlySpan<byte> pngBytes,
        bool requireUnderWorkDir = true)
    {
        var normRel = NormalizeRelPath(relativeWorkspacePath);
        if (requireUnderWorkDir && !normRel.StartsWith("Work/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_ExternalReferenceBlocked", relativeWorkspacePath));
        }

        var ledgerPath = Path.Combine(creatureDir, "Work", ProvenanceFileName);
        if (!File.Exists(ledgerPath))
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_ExternalReferenceBlocked", relativeWorkspacePath));
        }

        JsonObject? entry = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ledgerPath, Encoding.UTF8)) is JsonObject root &&
                root["entries"] is JsonObject entries)
            {
                entry = entries.FirstOrDefault(kv => string.Equals(kv.Key, normRel, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            }
        }
        catch
        {
            entry = null;
        }

        if (entry == null)
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_ExternalReferenceBlocked", relativeWorkspacePath));
        }

        var recordedSha = entry["sha256"]?.GetValue<string>() ?? string.Empty;
        var recordedStage = entry["stage"]?.GetValue<string>() ?? string.Empty;
        var recordedMac = entry["hmac"]?.GetValue<string>() ?? string.Empty;

        var actualSha = Convert.ToHexString(SHA256.HashData(pngBytes));
        var expectedMac = ComputeEntryMac(creatureDir, normRel, actualSha, recordedStage);

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(recordedSha), Encoding.ASCII.GetBytes(actualSha)) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(recordedMac), Encoding.ASCII.GetBytes(expectedMac)))
        {
            throw new InvalidOperationException(AgentProtocol.Format("Authoring_ExternalReferenceBlocked", relativeWorkspacePath));
        }
    }

    private static string NormalizeRelPath(string rel) =>
        (rel ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static string ComputeEntryMac(string creatureDir, string normRel, string shaHex, string stageTag)
    {
        var payload = $"{Path.GetFullPath(creatureDir).ToLowerInvariant()}|{normRel.ToLowerInvariant()}|{shaHex}|{stageTag}";
        using var hmac = new HMACSHA256(LedgerHmacKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static byte[] DeriveInstallationKey()
    {
        var seed = $"GCF_RuntimeAgent_Provenance_v1|{Environment.MachineName}|{Environment.UserName}|{AppContext.BaseDirectory}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static bool IsToolPreviewIntroText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var template = AgentProtocol.Get("TurnRunner_AttachedImageIntro");
            var prefixIdx = template.IndexOf("{0}", StringComparison.Ordinal);
            var prefix = prefixIdx > 0 ? template[..prefixIdx] : template;
            return prefix.Length > 0 && text.StartsWith(prefix, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSystemNudgeMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var missing = AgentSkillStore.Current.Prompts[AgentPromptSection.MissingSubmission];
            var synth = AgentSkillStore.Current.Prompts[AgentPromptSection.Synthesis];
            return (!string.IsNullOrEmpty(missing) && text == missing) ||
                   (!string.IsNullOrEmpty(synth) && text.EndsWith(synth, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private sealed class ScopeReset(UserImg2ImgInspection? previous) : IDisposable
    {
        public void Dispose() => CurrentTurnState.Value = previous;
    }
}
