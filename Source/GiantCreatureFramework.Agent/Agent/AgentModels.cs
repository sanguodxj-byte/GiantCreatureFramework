using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GiantCreatureFramework.Agent;

public enum ChatRole { User, Assistant, System }

public sealed record TokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    bool IsEstimated = false,
    int CachedPromptTokens = 0,
    int PeakContextTokens = 0,
    int LlmCallCount = 1)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, true, 0, 0, 0);

    public int UncachedPromptTokens => Math.Max(0, PromptTokens - CachedPromptTokens);

    public double CacheHitRate =>
        PromptTokens > 0 ? Math.Clamp((double)CachedPromptTokens / PromptTokens, 0.0, 1.0) : 0.0;

    public int PrefixReuseTokens =>
        PeakContextTokens > 0 && PromptTokens > PeakContextTokens ? PromptTokens - PeakContextTokens : 0;

    public double PrefixReuseRate =>
        PromptTokens > 0 ? Math.Clamp((double)PrefixReuseTokens / PromptTokens, 0.0, 1.0) : 0.0;

    public TokenUsage Add(TokenUsage? other)
    {
        if (other == null || (other.PromptTokens <= 0 && other.CompletionTokens <= 0 && other.TotalTokens <= 0))
        {
            return this;
        }

        var p = PromptTokens + Math.Max(0, other.PromptTokens);
        var c = CompletionTokens + Math.Max(0, other.CompletionTokens);
        var t = TotalTokens + Math.Max(0, other.TotalTokens);
        if (t == 0 && (p > 0 || c > 0)) t = p + c;

        var cached = CachedPromptTokens + Math.Clamp(other.CachedPromptTokens, 0, Math.Max(0, other.PromptTokens));
        var otherPeak = other.PeakContextTokens > 0 ? other.PeakContextTokens : other.PromptTokens;
        var peak = Math.Max(PeakContextTokens, otherPeak);
        var calls = LlmCallCount + Math.Max(1, other.LlmCallCount);

        var hasUsage = PromptTokens > 0 || CompletionTokens > 0 || TotalTokens > 0;
        return new TokenUsage(p, c, t, (hasUsage && IsEstimated) || other.IsEstimated, cached, peak, calls);
    }
}

public static class TokenEstimator
{
    private static readonly char[] PunctuationTrimChars =
        ['!', '?', '.', ',', ':', ';', '"', '\'', '(', ')', '[', ']', '{', '}'];

    /// <summary>
    /// Estimates the token count for a given text string using RimTalk's hybrid word/character approach
    /// (<c>CommonUtil.EstimateTokenCount</c>), accounting for word boundaries, subword length, and punctuation.
    /// Inline base64 image data URLs (<c>data:image/...</c>) are stripped first and counted as vision tokens.
    /// </summary>
    public static int EstimateTokens(string? text) => EstimateTokenCount(text);

    public static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cleanInput = StripInlineDataImageUrls(text, out var imageTokens);

        // Remove extra whitespace and normalize (RimTalk CommonUtil.EstimateTokenCount)
        var normalizedText = Regex.Replace(cleanInput.Trim(), @"\s+", " ");
        if (normalizedText.Length == 0)
        {
            return imageTokens;
        }

        var totalTokens = 0.0;
        var words = normalizedText.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            // Clean word of leading/trailing punctuation for length calculation
            var cleanWord = word.Trim(PunctuationTrimChars);

            if (cleanWord.Length == 0)
            {
                // Pure punctuation word
                totalTokens += 1.0;
            }
            else if (cleanWord.Length <= 3)
            {
                // Short words are usually 1 token, plus punctuation if present
                totalTokens += 1.0;
                if (cleanWord.Length != word.Length) totalTokens += 0.5; // Attached punctuation
            }
            else if (cleanWord.Length <= 6)
            {
                // Medium words: roughly 1-1.5 tokens
                totalTokens += 1.0;
                if (cleanWord.Length > 4) totalTokens += 0.5;
                if (cleanWord.Length != word.Length) totalTokens += 0.5; // Attached punctuation
            }
            else
            {
                // Long words: modern tokenizers break these into subwords
                // Estimate ~3.5 characters per token for long sequences
                totalTokens += Math.Max(1.0, Math.Ceiling(cleanWord.Length / 3.5));
                if (cleanWord.Length != word.Length) totalTokens += 0.5; // Attached punctuation
            }
        }

        // Add small overhead for special tokens and formatting, but less aggressive
        totalTokens += Math.Max(1.0, totalTokens * 0.02);

        // Round up and convert to int
        return imageTokens + Math.Max(1, (int)Math.Ceiling(totalTokens));
    }

    private static string StripInlineDataImageUrls(string text, out int imageTokens)
    {
        imageTokens = 0;
        if (text.IndexOf("data:image/", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var sb = new StringBuilder(Math.Min(text.Length, 4096));
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == 'd' &&
                i + 11 < text.Length &&
                string.CompareOrdinal(text, i, "data:image/", 0, 11) == 0)
            {
                imageTokens += 512;
                var endQuote = text.IndexOf('"', i + 11);
                if (endQuote > i)
                {
                    i = endQuote;
                    continue;
                }
                break;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    public static string FormatCompact(int tokens)
    {
        if (tokens <= 0) return "0";
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F2}M";
        if (tokens >= 10_000) return $"{tokens / 1_000.0:F1}k";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F2}k";
        return tokens.ToString("N0");
    }
}

public sealed class ToolCallRecord
{
    public string CallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
    [JsonIgnore] public bool IsExpanded { get; set; }
}

public sealed class ChatMessage
{
    public ChatRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    [JsonIgnore] public string ReasoningContent { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<ToolCallRecord> ToolCalls { get; set; } = new();
    public bool IsError { get; set; }
    public bool LacksRuntimeEvidence { get; set; }
    public string EvidenceNotice { get; set; } = string.Empty;
    public string AnswerKind { get; set; } = "";
    public List<string> CitedEvidenceIds { get; set; } = new();
    public List<AnswerAttemptRecord> AnswerAttempts { get; set; } = new();
    public ModelIdentity? ModelIdentity { get; set; }
    public int UsageSchemaVersion { get; set; }
    public int PromptTokens { get; set; }
    public int CachedPromptTokens { get; set; }
    public int PeakContextTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int LlmRounds { get; set; }
    public bool IsEstimatedTokens { get; set; }
    public bool HasScreenshot { get; set; }
    [JsonIgnore] public string? ImageDataUrl { get; set; }
    [JsonIgnore] public object? PreviewTexture { get; set; }
    [JsonIgnore] public int UncachedPromptTokens => Math.Max(0, PromptTokens - CachedPromptTokens);
    [JsonIgnore] public double CacheHitRate => PromptTokens > 0 ? Math.Clamp((double)CachedPromptTokens / PromptTokens, 0.0, 1.0) : 0.0;
    [JsonIgnore] public int PrefixReuseTokens => PeakContextTokens > 0 && PromptTokens > PeakContextTokens ? PromptTokens - PeakContextTokens : 0;
    [JsonIgnore] public double PrefixReuseRate => PromptTokens > 0 ? Math.Clamp((double)PrefixReuseTokens / PromptTokens, 0.0, 1.0) : 0.0;
    [JsonIgnore] public bool IsStreaming { get; set; }
    [JsonIgnore] public bool ShowEvidence { get; set; }
}

/// <summary>Local diagnostic record; failed answer drafts are never replayed as established facts.</summary>
public sealed class AnswerAttemptRecord
{
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string Error { get; set; } = "";
    public bool Accepted { get; set; }
}

/// <summary>A request owns its cancellation source until its own completion.</summary>
public sealed class AgentTurn : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public CancellationTokenSource Cancellation { get; } = new();
    public void Dispose() => Cancellation.Dispose();
}

/// <summary>Conversation data is independent of the window and bound to one save.</summary>
public sealed class AgentSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ScopeId { get; set; } = "menu";
    public string Title { get; set; } = UiText.KeySessionTitleNew;
    /// <summary>Null means the player has not chosen yet; existing archives retain their history until a choice is made.</summary>
    public bool? UseConversationHistory { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();
    public int TotalPromptTokens { get; set; }
    public int TotalCachedPromptTokens { get; set; }
    public int TotalPrefixReuseTokens { get; set; }
    public int PeakContextTokens { get; set; }
    public int TotalCompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    [JsonIgnore] public int TotalUncachedPromptTokens => Math.Max(0, TotalPromptTokens - TotalCachedPromptTokens);
    [JsonIgnore] public int EffectiveNetTokens => TotalUncachedPromptTokens + TotalCompletionTokens;
    [JsonIgnore] public double CacheHitRate => TotalPromptTokens > 0 ? Math.Clamp((double)TotalCachedPromptTokens / TotalPromptTokens, 0.0, 1.0) : 0.0;
    [JsonIgnore] public double PrefixReuseRate => TotalPromptTokens > 0 ? Math.Clamp((double)TotalPrefixReuseTokens / TotalPromptTokens, 0.0, 1.0) : 0.0;
    [JsonIgnore] public string StatusText { get; private set; } = UiText.KeySessionStatusReady;
    /// <summary>True while StatusText describes a failed condition; set where the status is written so the badge never inspects display text.</summary>
    [JsonIgnore] public bool StatusIsError { get; private set; }
    [JsonIgnore] public AgentTurn? ActiveTurn { get; private set; }
    [JsonIgnore] public bool IsBusy { get { lock (SyncRoot) return ActiveTurn != null; } }
    [JsonIgnore] public object SyncRoot { get; } = new();

    public void RecalculateTokenTotals()
    {
        var hasAssistantUsage = false;
        foreach (var msg in Messages)
        {
            if (msg.Role == ChatRole.Assistant && (msg.PromptTokens > 0 || msg.TotalTokens > 0))
            {
                hasAssistantUsage = true;
                break;
            }
        }

        var p = 0;
        var cached = 0;
        var peak = 0;
        var c = 0;
        var t = 0;
        var prevAssistantPeak = 0;
        foreach (var msg in Messages)
        {
            if (hasAssistantUsage && msg.Role == ChatRole.User)
            {
                continue;
            }

            var msgPeak = msg.PeakContextTokens > 0 ? msg.PeakContextTokens : msg.PromptTokens;

            p += Math.Max(0, msg.PromptTokens);
            cached += Math.Clamp(msg.CachedPromptTokens, 0, Math.Max(0, msg.PromptTokens));
            if (msgPeak > peak) peak = msgPeak;
            if (msg.Role == ChatRole.Assistant && msgPeak > 0)
            {
                prevAssistantPeak = msgPeak;
            }
            c += Math.Max(0, msg.CompletionTokens);
            t += Math.Max(0, msg.TotalTokens);
        }

        TotalPromptTokens = p;
        TotalCachedPromptTokens = cached;
        PeakContextTokens = peak;
        TotalPrefixReuseTokens = peak > 0 && p > peak ? p - peak : 0;
        TotalCompletionTokens = c;
        TotalTokens = t;
    }

    public AgentTurn? TryBeginTurn()
    {
        lock (SyncRoot)
        {
            if (ActiveTurn != null) return null;
            return ActiveTurn = new AgentTurn();
        }
    }

    /// <summary>Writes StatusText and its error flag together so the pair cannot drift apart.</summary>
    public void SetStatus(string text, bool isError)
    {
        StatusText = text;
        StatusIsError = isError;
    }

    public void Cancel()
    {
        lock (SyncRoot)
        {
            if (ActiveTurn == null) return;
            SetStatus(UiText.Get(UiText.KeySessionStatusAborting), isError: false);
            ActiveTurn.Cancellation.Cancel();
        }
    }

    public bool Owns(AgentTurn turn) { lock (SyncRoot) return ReferenceEquals(ActiveTurn, turn); }

    public void Complete(AgentTurn turn, string status, bool isError = false)
    {
        lock (SyncRoot)
        {
            if (!ReferenceEquals(ActiveTurn, turn)) return;
            ActiveTurn = null;
            SetStatus(status, isError);
        }
    }
}
