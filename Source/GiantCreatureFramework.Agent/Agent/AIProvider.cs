using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GiantCreatureFramework.Agent;

/// <summary>
/// Supported AI service providers, aligned with RimTalk &amp; RimTalk-ExpandMemory.
/// </summary>
public enum AIProvider
{
    Google,
    OpenAI,
    DeepSeek,
    Grok,
    GLM,
    GLMCoding,
    AlibabaIntl,
    AlibabaCN,
    OpenRouter,
    Claude,
    Moonshot,
    OpenCodeGo,
    Player2,
    Local,
    Custom,
    None
}

public static class AIProviderExtensions
{
    public static string Label(this AIProvider provider) => provider switch
    {
        AIProvider.Google => "Google (Gemini)",
        AIProvider.OpenAI => "OpenAI",
        AIProvider.DeepSeek => UiText.Get("GCFAgent_ProviderLabel_DeepSeek"),
        AIProvider.Grok => "Grok (xAI)",
        AIProvider.GLM => UiText.Get("GCFAgent_ProviderLabel_GLM"),
        AIProvider.GLMCoding => UiText.Get("GCFAgent_ProviderLabel_GLMCoding"),
        AIProvider.AlibabaIntl => UiText.Get("GCFAgent_ProviderLabel_AlibabaIntl"),
        AIProvider.AlibabaCN => UiText.Get("GCFAgent_ProviderLabel_AlibabaCN"),
        AIProvider.OpenRouter => "OpenRouter",
        AIProvider.Claude => "Claude (Anthropic)",
        AIProvider.Moonshot => UiText.Get("GCFAgent_ProviderLabel_Moonshot"),
        AIProvider.OpenCodeGo => "OpenCode Go",
        AIProvider.Player2 => UiText.Get("GCFAgent_ProviderLabel_Player2"),
        AIProvider.Local => "Local (Ollama / LM Studio / vLLM)",
        AIProvider.Custom => UiText.Get("GCFAgent_ProviderLabel_Custom"),
        AIProvider.None => UiText.Get("GCFAgent_ProviderLabel_Disabled"),
        _ => provider.ToString()
    };

    public static string ShortLabel(this AIProvider provider) => provider switch
    {
        AIProvider.Google => "Gemini",
        AIProvider.OpenAI => "OpenAI",
        AIProvider.DeepSeek => "DeepSeek",
        AIProvider.Grok => "Grok",
        AIProvider.GLM => "GLM",
        AIProvider.GLMCoding => "GLM-Code",
        AIProvider.AlibabaIntl => "Qwen-Intl",
        AIProvider.AlibabaCN => UiText.Get("GCFAgent_ProviderShortLabel_AlibabaCN"),
        AIProvider.OpenRouter => "OpenRouter",
        AIProvider.Claude => "Claude",
        AIProvider.Moonshot => "Kimi",
        AIProvider.OpenCodeGo => "OpenCode",
        AIProvider.Player2 => "Player2",
        AIProvider.Local => "Local",
        AIProvider.Custom => "Custom",
        AIProvider.None => "None",
        _ => provider.ToString()
    };

    public static bool RequiresApiKey(this AIProvider provider) =>
        provider != AIProvider.Local && provider != AIProvider.Player2 && provider != AIProvider.None;

    public static bool UsesCustomUrl(this AIProvider provider) =>
        provider == AIProvider.Custom || provider == AIProvider.Local;
}

/// <summary>
/// Single provider row configuration supporting multi-provider fallback chain (aligned with RimTalk-ExpandMemory ApiConfig).
/// </summary>
public sealed class ApiConfigEntry
{
    public bool IsEnabled { get; set; } = true;
    public AIProvider Provider { get; set; } = AIProvider.Player2;
    public string ApiKey { get; set; } = string.Empty;
    public string CustomUrl { get; set; } = string.Empty;
    public string SelectedModel { get; set; } = string.Empty;
    public string CustomModelName { get; set; } = string.Empty;

    public ApiConfigEntry Clone() => new()
    {
        IsEnabled = IsEnabled,
        Provider = Provider,
        ApiKey = ApiKey,
        CustomUrl = CustomUrl,
        SelectedModel = SelectedModel,
        CustomModelName = CustomModelName
    };

    public string GetResolvedEndpoint() =>
        AIProviderRegistry.GetEndpointUrl(Provider, CustomUrl);

    public string GetResolvedModel()
    {
        if (Provider == AIProvider.Player2)
        {
            return "Player2";
        }

        if (!string.IsNullOrWhiteSpace(CustomModelName) &&
            (Provider == AIProvider.Custom ||
             Provider == AIProvider.Local ||
             string.IsNullOrWhiteSpace(SelectedModel) ||
             string.Equals(SelectedModel, "Custom", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(SelectedModel, "Choose Model", StringComparison.OrdinalIgnoreCase)))
        {
            return CustomModelName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(SelectedModel) &&
            !string.Equals(SelectedModel, "Custom", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(SelectedModel, "Choose Model", StringComparison.OrdinalIgnoreCase))
        {
            return SelectedModel.Trim();
        }

        return (CustomModelName ?? string.Empty).Trim();
    }

    public bool IsMinimallyValid()
    {
        if (!IsEnabled || Provider == AIProvider.None)
        {
            return false;
        }

        if (Provider == AIProvider.Player2)
        {
            // Player2 can authenticate automatically via local desktop app or web device token
            return true;
        }

        var endpoint = GetResolvedEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.IsWellFormedUriString(endpoint, UriKind.Absolute))
        {
            return false;
        }

        if (Provider == AIProvider.Local || Provider == AIProvider.Custom)
        {
            return !string.IsNullOrWhiteSpace(GetResolvedModel());
        }

        return !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(GetResolvedModel());
    }
}

/// <summary>
/// Central registry for AI provider endpoints, model discovery URLs, and extra headers.
/// Mirrors RimTalk-ExpandMemory's AIProviderRegistry.
/// </summary>
public static class AIProviderRegistry
{
    private sealed record ProviderMetadata(
        string EndpointUrl,
        string? ListModelsUrl,
        IReadOnlyDictionary<string, string>? ExtraHeaders = null);

    private static readonly Dictionary<AIProvider, ProviderMetadata> Metadata = new()
    {
        [AIProvider.Google] = new(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            "https://generativelanguage.googleapis.com/v1beta/openai/models"),
        [AIProvider.OpenAI] = new(
            "https://api.openai.com/v1/chat/completions",
            "https://api.openai.com/v1/models"),
        [AIProvider.DeepSeek] = new(
            "https://api.deepseek.com/v1/chat/completions",
            "https://api.deepseek.com/models"),
        [AIProvider.Grok] = new(
            "https://api.x.ai/v1/chat/completions",
            "https://api.x.ai/v1/models"),
        [AIProvider.GLM] = new(
            "https://api.z.ai/api/paas/v4/chat/completions",
            "https://api.z.ai/api/paas/v4/models"),
        [AIProvider.GLMCoding] = new(
            "https://api.z.ai/api/coding/paas/v4/chat/completions",
            "https://api.z.ai/api/coding/paas/v4/models"),
        [AIProvider.AlibabaIntl] = new(
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions",
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/models"),
        [AIProvider.AlibabaCN] = new(
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            "https://dashscope.aliyuncs.com/compatible-mode/v1/models"),
        [AIProvider.OpenRouter] = new(
            "https://openrouter.ai/api/v1/chat/completions",
            "https://openrouter.ai/api/v1/models",
            new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "https://github.com/sanguodxj-byte/RimTalk-ExpandMemory",
                ["X-Title"] = "GiantCreatureFramework"
            }),
        [AIProvider.Claude] = new(
            "https://api.anthropic.com/v1/chat/completions",
            "https://api.anthropic.com/v1/models",
            new Dictionary<string, string>
            {
                ["anthropic-version"] = "2023-06-01"
            }),
        [AIProvider.Moonshot] = new(
            "https://api.moonshot.ai/v1/chat/completions",
            "https://api.moonshot.ai/v1/models"),
        [AIProvider.OpenCodeGo] = new(
            "https://opencode.ai/inference/go/openai/v1/chat/completions",
            null,
            new Dictionary<string, string>
            {
                ["x-opencode-session"] = Guid.NewGuid().ToString()
            }),
        [AIProvider.Player2] = new(
            "https://api.player2.game/v1/chat/completions",
            null,
            new Dictionary<string, string>
            {
                ["player2-game-key"] = Player2Support.GameClientId
            }),
        [AIProvider.Local] = new(
            "http://127.0.0.1:11434/v1/chat/completions",
            "http://127.0.0.1:11434/v1/models"),
        [AIProvider.Custom] = new(
            string.Empty,
            null)
    };

    private static readonly Dictionary<string, List<string>> ModelListCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static IReadOnlyList<AIProvider> ConfigurableProviders { get; } = new[]
    {
        AIProvider.Google,
        AIProvider.OpenAI,
        AIProvider.DeepSeek,
        AIProvider.AlibabaCN,
        AIProvider.AlibabaIntl,
        AIProvider.GLM,
        AIProvider.GLMCoding,
        AIProvider.Moonshot,
        AIProvider.Claude,
        AIProvider.OpenRouter,
        AIProvider.Grok,
        AIProvider.OpenCodeGo,
        AIProvider.Player2,
        AIProvider.Local,
        AIProvider.Custom
    };

    public static string GetEndpointUrl(AIProvider provider, string? customUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(customUrl))
        {
            return NormalizeChatCompletionsEndpoint(customUrl);
        }

        if (provider == AIProvider.Local)
        {
            return "http://127.0.0.1:11434/v1/chat/completions";
        }

        if (provider == AIProvider.Custom)
        {
            return string.Empty;
        }

        return Metadata.TryGetValue(provider, out var meta) ? meta.EndpointUrl : string.Empty;
    }

    public static string? GetListModelsUrl(AIProvider provider, string? customUrl = null)
    {
        if (provider.UsesCustomUrl() || !string.IsNullOrWhiteSpace(customUrl))
        {
            var baseEndpoint = GetEndpointUrl(provider, customUrl);
            return DeriveModelsUrlFromEndpoint(baseEndpoint);
        }

        return Metadata.TryGetValue(provider, out var meta) ? meta.ListModelsUrl : null;
    }

    public static IReadOnlyDictionary<string, string>? GetExtraHeaders(AIProvider provider) =>
        Metadata.TryGetValue(provider, out var meta) ? meta.ExtraHeaders : null;

    public static AIProvider InferProviderFromEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return AIProvider.Custom;
        }

        var trimmed = endpoint.Trim();
        foreach (var kv in Metadata)
        {
            if (kv.Key == AIProvider.Custom || kv.Key == AIProvider.Local || string.IsNullOrEmpty(kv.Value.EndpointUrl))
            {
                continue;
            }

            if (string.Equals(trimmed, kv.Value.EndpointUrl, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Key;
            }
        }

        if (trimmed.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase)) return AIProvider.Google;
        if (trimmed.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)) return AIProvider.OpenAI;
        if (trimmed.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase)) return AIProvider.DeepSeek;
        if (trimmed.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase)) return AIProvider.Grok;
        if (trimmed.Contains("api.z.ai/api/coding", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("open.bigmodel.cn/api/coding", StringComparison.OrdinalIgnoreCase)) return AIProvider.GLMCoding;
        if (trimmed.Contains("api.z.ai", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase)) return AIProvider.GLM;
        if (trimmed.Contains("dashscope-intl.aliyuncs.com", StringComparison.OrdinalIgnoreCase)) return AIProvider.AlibabaIntl;
        if (trimmed.Contains("dashscope.aliyuncs.com", StringComparison.OrdinalIgnoreCase)) return AIProvider.AlibabaCN;
        if (trimmed.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)) return AIProvider.OpenRouter;
        if (trimmed.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase)) return AIProvider.Claude;
        if (trimmed.Contains("api.moonshot.ai", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("api.moonshot.cn", StringComparison.OrdinalIgnoreCase)) return AIProvider.Moonshot;
        if (trimmed.Contains("opencode.ai", StringComparison.OrdinalIgnoreCase)) return AIProvider.OpenCodeGo;
        if (trimmed.Contains("player2.game", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(":4315", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(":4316", StringComparison.OrdinalIgnoreCase)) return AIProvider.Player2;
        if (trimmed.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("localhost", StringComparison.OrdinalIgnoreCase)) return AIProvider.Local;

        return AIProvider.Custom;
    }

    public static string NormalizeChatCompletionsEndpoint(string? rawUrl)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        url = url.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/completions", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/v4", StringComparison.OrdinalIgnoreCase))
        {
            return url + "/chat/completions";
        }

        return url + "/v1/chat/completions";
    }

    public static string? DeriveModelsUrlFromEndpoint(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return null;
        }

        var trimmed = endpointUrl.Trim().TrimEnd('/');
        var idx = trimmed.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            return trimmed[..idx] + "/models";
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/models";
        }

        return trimmed + "/v1/models";
    }

    public static IReadOnlyList<string> GetCachedModels(AIProvider provider, string? customUrl = null)
    {
        var cacheKey = $"{provider}:{customUrl ?? string.Empty}";
        lock (CacheLock)
        {
            if (ModelListCache.TryGetValue(cacheKey, out var cached) && cached.Count > 0)
            {
                return cached;
            }
        }

        return Array.Empty<string>();
    }

    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        AIProvider provider,
        string apiKey,
        string? customUrl = null,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default)
    {
        var modelsUrl = GetListModelsUrl(provider, customUrl);
        if (string.IsNullOrWhiteSpace(modelsUrl))
        {
            return Array.Empty<string>();
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60))
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        var extraHeaders = GetExtraHeaders(provider);
        if (extraHeaders != null)
        {
            foreach (var kv in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {TruncateText(json, 200)}");
        }

        var models = ParseModelListJson(json);
        if (models.Count > 0)
        {
            var cacheKey = $"{provider}:{customUrl ?? string.Empty}";
            lock (CacheLock)
            {
                ModelListCache[cacheKey] = models;
            }
        }

        return models;
    }

    public static List<string> ParseModelListJson(string json)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataArr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String)
                    {
                        var id = StripModelsPrefix(idEl.GetString());
                        if (!string.IsNullOrWhiteSpace(id) && !results.Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            results.Add(id);
                        }
                    }
                }
            }
            else if (root.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsArr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var raw = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString()
                            : (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null);
                        var id = StripModelsPrefix(raw);
                        if (!string.IsNullOrWhiteSpace(id) && !results.Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            results.Add(id);
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Scans common local LLM server ports (Ollama, LM Studio, vLLM, llama.cpp, KoboldCPP, TextGenWebUI, Jan).
    /// </summary>
    public static async Task<List<(string EndpointUrl, string ModelName, string ServiceName)>> ScanLocalServicesAsync(CancellationToken cancellationToken = default)
    {
        var found = new List<(string EndpointUrl, string ModelName, string ServiceName)>();
        var candidates = new (int Port, string CheckPath, string CompletionPath, string Name)[]
        {
            (11434, "/api/tags", "/v1/chat/completions", "Ollama"),
            (1234, "/v1/models", "/v1/chat/completions", "LM Studio"),
            (8000, "/v1/models", "/v1/chat/completions", "vLLM"),
            (8080, "/v1/models", "/v1/chat/completions", "llama.cpp / LocalAI"),
            (5001, "/v1/models", "/v1/chat/completions", "KoboldCPP"),
            (5000, "/v1/models", "/v1/chat/completions", "TextGenWebUI / TabbyAPI"),
            (1337, "/v1/models", "/v1/chat/completions", "Jan")
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
        foreach (var c in candidates)
        {
            try
            {
                var checkUrl = $"http://127.0.0.1:{c.Port}{c.CheckPath}";
                using var resp = await client.GetAsync(checkUrl, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var models = ParseModelListJson(body);
                var firstModel = models.Count > 0 ? models[0] : "local-model";
                var endpoint = $"http://127.0.0.1:{c.Port}{c.CompletionPath}";
                found.Add((endpoint, firstModel, c.Name));
            }
            catch
            {
                // Port not active, continue scanning
            }
        }

        return found;
    }

    public static async Task<(string EndpointUrl, string ModelName, string ServiceName)?> ProbeLocalServiceAsync(CancellationToken cancellationToken = default)
    {
        var all = await ScanLocalServicesAsync(cancellationToken).ConfigureAwait(false);
        return all.Count > 0 ? all[0] : null;
    }

    private static string StripModelsPrefix(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var trimmed = id.Trim();
        return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? trimmed["models/".Length..]
            : trimmed;
    }

    private static string TruncateText(string text, int maxLen) =>
        string.IsNullOrEmpty(text) || text.Length <= maxLen ? text : text[..maxLen] + "...";
}

/// <summary>
/// Player2 local client detection and web device authentication helper (aligned with RimTalk-ExpandMemory).
/// </summary>
public static class Player2Support
{
    public const string GameClientId = "01a05876-8d6b-7376-92ef-0e51b45d130c";
    public const string RemoteBaseUrl = "https://api.player2.game/v1";
    public const string RemoteChatEndpoint = "https://api.player2.game/v1/chat/completions";
    public const string WebsiteUrl = "https://player2.game";

    private static readonly object StateLock = new();
    private static CancellationTokenSource? _authCts;
    private static bool _isAuthenticating;
    private static string _approvalUrl = string.Empty;
    private static string _authStatusText = string.Empty;

    private static bool _localAppDetected;
    private static string _localAppEndpoint = string.Empty;
    private static string _localAppApiKey = string.Empty;
    private static DateTime _lastLocalCheckUtc = DateTime.MinValue;
    private static bool _localCheckInProgress;
    private static readonly HttpClient LocalProbeClient = CreateLocalProbeClient();

    private static HttpClient CreateLocalProbeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("player2-game-key", GameClientId);
        return client;
    }

    public static void InvalidateLocalKeyCache()
    {
        lock (StateLock)
        {
            _localAppApiKey = string.Empty;
            _lastLocalCheckUtc = DateTime.MinValue;
        }
    }

    public static bool IsAuthenticating
    {
        get { lock (StateLock) return _isAuthenticating; }
    }

    public static string ApprovalUrl
    {
        get { lock (StateLock) return _approvalUrl; }
    }

    public static string AuthStatusText
    {
        get { lock (StateLock) return _authStatusText; }
    }

    public static (bool IsRunning, string Endpoint) GetLocalAppStatusCached(int cacheSeconds = 8)
    {
        bool shouldTriggerCheck = false;
        lock (StateLock)
        {
            if (!_localCheckInProgress && (DateTime.UtcNow - _lastLocalCheckUtc).TotalSeconds >= cacheSeconds)
            {
                _localCheckInProgress = true;
                shouldTriggerCheck = true;
            }
        }

        if (shouldTriggerCheck)
        {
            _ = RefreshLocalAppStatusAsync();
        }

        lock (StateLock)
        {
            return (_localAppDetected, _localAppEndpoint);
        }
    }

    public static async Task<(bool IsRunning, string Endpoint)> RefreshLocalAppStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (StateLock)
        {
            _localCheckInProgress = true;
        }

        try
        {
            var local = await TryGetLocalConnectionAsync(cancellationToken).ConfigureAwait(false);
            lock (StateLock)
            {
                _localAppDetected = local.HasValue;
                _localAppEndpoint = local?.EndpointUrl ?? string.Empty;
                _lastLocalCheckUtc = DateTime.UtcNow;
                return (_localAppDetected, _localAppEndpoint);
            }
        }
        finally
        {
            lock (StateLock)
            {
                _localCheckInProgress = false;
            }
        }
    }

    public static async Task<(string EndpointUrl, string ApiKey)?> TryGetLocalConnectionAsync(
        CancellationToken cancellationToken = default,
        bool forceRefreshKey = false)
    {
        string cachedEndpoint;
        string cachedKey;
        DateTime lastCheck;
        lock (StateLock)
        {
            cachedEndpoint = _localAppEndpoint;
            cachedKey = _localAppApiKey;
            lastCheck = _lastLocalCheckUtc;
        }

        if (!forceRefreshKey &&
            !string.IsNullOrWhiteSpace(cachedEndpoint) &&
            !string.IsNullOrWhiteSpace(cachedKey) &&
            (DateTime.UtcNow - lastCheck).TotalSeconds < 30)
        {
            return (cachedEndpoint, cachedKey);
        }

        var candidateBaseUrls = GetCandidateBaseUrls();
        foreach (var baseUrl in candidateBaseUrls)
        {
            try
            {
                using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                healthCts.CancelAfter(TimeSpan.FromSeconds(2));
                using var healthResp = await LocalProbeClient.GetAsync($"{baseUrl}/health", healthCts.Token).ConfigureAwait(false);
                if (!healthResp.IsSuccessStatusCode)
                {
                    continue;
                }

                var chatEndpoint = $"{baseUrl}/chat/completions";
                if (!forceRefreshKey &&
                    !string.IsNullOrWhiteSpace(cachedKey) &&
                    string.Equals(cachedEndpoint, chatEndpoint, StringComparison.OrdinalIgnoreCase))
                {
                    lock (StateLock)
                    {
                        _localAppDetected = true;
                        _localAppEndpoint = chatEndpoint;
                        _lastLocalCheckUtc = DateTime.UtcNow;
                    }
                    return (chatEndpoint, cachedKey);
                }

                using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                loginCts.CancelAfter(TimeSpan.FromSeconds(4));
                using var loginReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/login/web/{GameClientId}")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                using var loginResp = await LocalProbeClient.SendAsync(loginReq, loginCts.Token).ConfigureAwait(false);
                if (loginResp.IsSuccessStatusCode)
                {
                    var body = await loginResp.Content.ReadAsStringAsync(loginCts.Token).ConfigureAwait(false);
                    var key = ParseP2KeyFromJson(body);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        lock (StateLock)
                        {
                            _localAppDetected = true;
                            _localAppEndpoint = chatEndpoint;
                            _localAppApiKey = key;
                            _lastLocalCheckUtc = DateTime.UtcNow;
                        }

                        return (chatEndpoint, key);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(cachedKey))
                {
                    lock (StateLock)
                    {
                        _localAppDetected = true;
                        _localAppEndpoint = chatEndpoint;
                        _lastLocalCheckUtc = DateTime.UtcNow;
                    }
                    return (chatEndpoint, cachedKey);
                }
            }
            catch
            {
                // Try next local URL
            }
        }

        if (!forceRefreshKey && !string.IsNullOrWhiteSpace(cachedEndpoint) && !string.IsNullOrWhiteSpace(cachedKey))
        {
            return (cachedEndpoint, cachedKey);
        }

        lock (StateLock)
        {
            _localAppDetected = false;
            _localAppEndpoint = string.Empty;
            _lastLocalCheckUtc = DateTime.UtcNow;
        }

        return null;
    }

    public static async Task<(string EndpointUrl, string ApiKey, bool IsLocal)?> ResolvePlayer2ConnectionAsync(
        string? configuredApiKey,
        CancellationToken cancellationToken = default)
    {
        // 1. Check local Player2 desktop app first (cached p2Key, auto-login only on initial connect or key refresh)
        var local = await TryGetLocalConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (local.HasValue)
        {
            return (local.Value.EndpointUrl, local.Value.ApiKey, true);
        }

        // 2. Fallback to remote Player2 cloud with configured key
        if (!string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return (RemoteChatEndpoint, configuredApiKey.Trim(), false);
        }

        return null;
    }

    public static void StartAuth(
        Action<string>? onSuccess = null,
        Action<string>? onStatus = null,
        Action<string>? onError = null)
    {
        CancellationToken token;
        lock (StateLock)
        {
            if (_isAuthenticating)
            {
                return;
            }

            _authCts?.Cancel();
            _authCts?.Dispose();
            _authCts = new CancellationTokenSource();
            token = _authCts.Token;
            _isAuthenticating = true;
            _approvalUrl = string.Empty;
            _authStatusText = UiText.Get("GCFAgent_Player2AuthRequestingCode");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var p2Key = await StartWebDeviceLoginAsync(status =>
                {
                    lock (StateLock)
                    {
                        _authStatusText = status;
                    }
                    onStatus?.Invoke(status);
                }, token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(p2Key))
                {
                    lock (StateLock)
                    {
                        _authStatusText = UiText.Get("GCFAgent_Player2AuthSuccess");
                    }
                    onSuccess?.Invoke(p2Key);
                }
                else if (!token.IsCancellationRequested)
                {
                    var msg = UiText.Get("GCFAgent_Player2AuthTimeout");
                    lock (StateLock)
                    {
                        _authStatusText = msg;
                    }
                    onError?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                lock (StateLock)
                {
                    _authStatusText = UiText.Get("GCFAgent_Player2AuthCancelled");
                }
            }
            catch (Exception ex)
            {
                lock (StateLock)
                {
                    _authStatusText = UiText.Format("GCFAgent_Player2AuthFailed", ex.Message);
                }
                onError?.Invoke(ex.Message);
            }
            finally
            {
                lock (StateLock)
                {
                    _isAuthenticating = false;
                    _approvalUrl = string.Empty;
                }
            }
        });
    }

    public static void CancelAuth()
    {
        lock (StateLock)
        {
            bool wasActive = _isAuthenticating || _authCts != null;
            _authCts?.Cancel();
            _authCts?.Dispose();
            _authCts = null;
            _isAuthenticating = false;
            _approvalUrl = string.Empty;
            if (wasActive)
            {
                _authStatusText = UiText.Get("GCFAgent_Player2AuthCancelledShort");
            }
        }
    }

    public static void ReopenApprovalPage()
    {
        string url;
        lock (StateLock)
        {
            url = _approvalUrl;
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            OpenUrlInBrowser(url);
        }
    }

    public static void OpenWebsite() => OpenUrlInBrowser(WebsiteUrl);

    public static async Task<string?> StartWebDeviceLoginAsync(
        Action<string>? onStatusUpdate = null,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("player2-game-key", GameClientId);

        var payload = JsonSerializer.Serialize(new { client_id = GameClientId });
        using var resp = await client.PostAsync(
            $"{RemoteBaseUrl}/login/device/new",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(UiText.Format("GCFAgent_Player2DeviceRequestFailed", ((int)resp.StatusCode).ToString()));
        }

        var (deviceCode, userCode, verificationUri, intervalSec, expiresInSec) = ParseDeviceCodeResponse(body);
        if (string.IsNullOrWhiteSpace(deviceCode) || !IsSafeBrowserUrl(verificationUri, out var validatedUri) || validatedUri == null)
        {
            throw new InvalidOperationException(UiText.Get("GCFAgent_Player2DeviceInvalidResponse"));
        }

        lock (StateLock)
        {
            _approvalUrl = validatedUri.AbsoluteUri;
        }

        OpenUrlInBrowser(validatedUri.AbsoluteUri);

        var codeHint = string.IsNullOrWhiteSpace(userCode) ? string.Empty : " " + UiText.Format("GCFAgent_Player2AuthCodeHint", userCode);
        onStatusUpdate?.Invoke(UiText.Format("GCFAgent_Player2AuthPageOpened", codeHint));

        var deadline = DateTime.UtcNow.AddSeconds(expiresInSec);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSec), cancellationToken).ConfigureAwait(false);
            var pollPayload = JsonSerializer.Serialize(new
            {
                client_id = GameClientId,
                device_code = deviceCode,
                grant_type = "urn:ietf:params:oauth:grant-type:device_code"
            });

            using var pollResp = await client.PostAsync(
                $"{RemoteBaseUrl}/login/device/token",
                new StringContent(pollPayload, Encoding.UTF8, "application/json"),
                cancellationToken).ConfigureAwait(false);

            var pollBody = await pollResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!pollResp.IsSuccessStatusCode)
            {
                // HTTP 400 = authorization_pending, continue polling
                continue;
            }

            var p2Key = ParseP2KeyFromJson(pollBody);
            if (!string.IsNullOrWhiteSpace(p2Key))
            {
                return p2Key;
            }
        }

        return null;
    }

    public static (string DeviceCode, string UserCode, string VerificationUri, int IntervalSeconds, int ExpiresInSeconds) ParseDeviceCodeResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (string.Empty, string.Empty, string.Empty, 5, 120);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty, string.Empty, 5, 120);
        }

        var deviceCode = ReadJsonString(root, "deviceCode", "device_code");
        var userCode = ReadJsonString(root, "userCode", "user_code");
        var verificationUriComplete = ReadJsonString(root, "verificationUriComplete", "verification_uri_complete");
        var verificationUri = ReadJsonString(root, "verificationUri", "verification_uri");
        var effectiveUri = !string.IsNullOrWhiteSpace(verificationUriComplete) ? verificationUriComplete : verificationUri;

        var intervalSec = ReadJsonInt(root, "interval", "interval") ?? 5;
        var expiresInSec = ReadJsonInt(root, "expiresIn", "expires_in") ?? 600;

        return (
            deviceCode,
            userCode,
            effectiveUri,
            Math.Clamp(intervalSec, 2, 30),
            Math.Clamp(expiresInSec, 30, 900));
    }

    public static string ParseP2KeyFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var key = ReadJsonString(root, "p2Key", "p2_key");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key.Trim();
                }
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return string.Empty;
    }

    public static List<string> GetCandidateBaseUrls()
    {
        var urls = new List<string>();
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var portFile = Path.Combine(appData, "game.player2.client", "api.port");
            if (File.Exists(portFile))
            {
                var raw = File.ReadAllText(portFile).Trim();
                if (int.TryParse(raw, out var customPort) && customPort > 0)
                {
                    AddCandidatePortUrls(urls, customPort);
                }
            }
        }
        catch
        {
            // Ignore file read errors
        }

        AddCandidatePortUrls(urls, 4315);
        AddCandidatePortUrls(urls, 4316);
        return urls;
    }

    private static void AddCandidatePortUrls(List<string> urls, int port)
    {
        var loopback = $"http://127.0.0.1:{port}/v1";
        var localhost = $"http://localhost:{port}/v1";
        if (!urls.Contains(loopback, StringComparer.OrdinalIgnoreCase)) urls.Add(loopback);
        if (!urls.Contains(localhost, StringComparer.OrdinalIgnoreCase)) urls.Add(localhost);
    }

    private static string ReadJsonString(JsonElement root, string camelName, string snakeName)
    {
        if (root.TryGetProperty(camelName, out var el1) && el1.ValueKind == JsonValueKind.String)
        {
            var s1 = el1.GetString();
            if (!string.IsNullOrWhiteSpace(s1)) return s1.Trim();
        }

        if (!string.Equals(camelName, snakeName, StringComparison.Ordinal) &&
            root.TryGetProperty(snakeName, out var el2) && el2.ValueKind == JsonValueKind.String)
        {
            var s2 = el2.GetString();
            if (!string.IsNullOrWhiteSpace(s2)) return s2.Trim();
        }

        return string.Empty;
    }

    private static int? ReadJsonInt(JsonElement root, string camelName, string snakeName)
    {
        if (root.TryGetProperty(camelName, out var el1) && el1.TryGetInt32(out var v1) && v1 > 0)
        {
            return v1;
        }

        if (!string.Equals(camelName, snakeName, StringComparison.Ordinal) &&
            root.TryGetProperty(snakeName, out var el2) && el2.TryGetInt32(out var v2) && v2 > 0)
        {
            return v2;
        }

        return null;
    }

    internal static bool IsSafeBrowserUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static void OpenUrlInBrowser(string url)
    {
        if (!IsSafeBrowserUrl(url, out var uri) || uri == null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Browser launch best-effort
        }
    }
}

