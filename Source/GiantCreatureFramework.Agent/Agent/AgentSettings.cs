using System.Globalization;
using System.Xml.Linq;
using Verse;

namespace GiantCreatureFramework.Agent;

/// <summary>
/// Settings for the GCF runtime authoring terminal: chat provider chain, separate image
/// generation endpoint/key/model, budgets, prompt overrides, and session retention.
/// Stored in Config/GiantCreatureFramework/AgentSettings.xml.
/// </summary>
public sealed class AgentSettings
{
    // === Primary chat connection fields (synced with ApiConfigs) ===
    public string ApiEndpoint = string.Empty;
    public string ApiKey = string.Empty;
    public string ModelName = string.Empty;
    public AIProvider SelectedProvider = AIProvider.Custom;
    public List<ApiConfigEntry> ApiConfigs = new();

    // === Separate image generation endpoint configuration (Step 4) ===
    public string ImageApiEndpoint = "http://127.0.0.1:2048/v1";
    public string ImageApiKey = string.Empty;
    public string ImageModelName = "gemini-3-pro-image";

    // === Generation parameters & unrestricted defaults ===
    public float Temperature = 0.2f;
    public int MaxContextChars = 0;
    public int SessionTokenBudget = 0;
    public int MaxPromptChars = 0;
    public int MaxAnswerChars = 0;
    public int MaxReasoningChars = 0;
    public int MaxResponseChars = 0;
    public int MaxToolRounds = 0;
    public int MaxTotalToolCalls = 0;
    public int MaxToolCallsPerRound = 0;
    public int HistoricalToolCompactChars = 2_000;
    public int MaxHistoryEvidenceChars = 0;

    // === Network & probe budgets ===
    public int RequestTimeoutSeconds = 180;
    public int StreamIdleTimeoutSeconds = 60;
    public int ImageRequestTimeoutSeconds = 300;
    public int MainThreadProbeBudgetMs = 5_000;
    public int BackgroundProbeBudgetMs = 30_000;
    public int MaxXmlScanFiles = 50_000;

    // === Session retention & conversation history ===
    public int MaxSessionsPerSave = 0;
    public int MaxSessionMessages = 0;
    public int MaxSessionRetentionChars = 0;
    public int RecentFullEvidenceMessages = 0;
    public bool AskConversationHistory = true;
    public bool DefaultConversationHistory = true;

    // === Prompt overrides & avatar ===
    public string CustomSystemPrompt = string.Empty;
    public string PersonaPrompt = string.Empty;
    public string CustomAvatarPath = string.Empty;
    public Dictionary<string, string> PromptOverrides = new(StringComparer.Ordinal);

    private string _lastSyncedEndpoint = string.Empty;
    private string _lastSyncedKey = string.Empty;
    private string _lastSyncedModel = string.Empty;

    public string ConfigurationError { get; private set; } = string.Empty;

    public static string UserConfigFilePath =>
        Path.Combine(GenFilePaths.ConfigFolderPath, "GiantCreatureFramework", "AgentSettings.xml");

    public string GetEffectiveSystemPrompt() => CapturePrompts().ComposeSystemPrompt();

    public string GetPrompt(AgentPromptSection section)
    {
        PromptOverrides ??= new(StringComparer.Ordinal);
        if (section == AgentPromptSection.Main && !string.IsNullOrWhiteSpace(CustomSystemPrompt)) return CustomSystemPrompt;
        if (section == AgentPromptSection.Persona) return PersonaPrompt ?? "";
        return PromptOverrides.TryGetValue(section.ToString(), out var value) ? value : AgentPrompts.Definition(section).DefaultText;
    }

    public bool IsPromptCustomized(AgentPromptSection section) =>
        PromptOverrides.ContainsKey(section.ToString())
        || (section == AgentPromptSection.Main && !string.IsNullOrWhiteSpace(CustomSystemPrompt))
        || (section == AgentPromptSection.Persona && !string.IsNullOrWhiteSpace(PersonaPrompt));

    public void SetPrompt(AgentPromptSection section, string text)
    {
        PromptOverrides[section.ToString()] = text;
        if (section == AgentPromptSection.Main) CustomSystemPrompt = text;
        if (section == AgentPromptSection.Persona) PersonaPrompt = text;
    }

    public void ResetPrompt(AgentPromptSection section)
    {
        PromptOverrides.Remove(section.ToString());
        if (section == AgentPromptSection.Main) CustomSystemPrompt = "";
        if (section == AgentPromptSection.Persona) PersonaPrompt = "";
    }

    public AgentPromptSnapshot CapturePrompts() =>
        new(AgentPrompts.Definitions.ToDictionary(d => d.Section, d => GetPrompt(d.Section)));

    public void ClampAllBudgets()
    {
        Temperature = float.IsFinite(Temperature) ? Math.Clamp(Temperature, 0f, 1.5f) : 0.2f;
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds > 0 ? RequestTimeoutSeconds : 180, 15, 600);
        StreamIdleTimeoutSeconds = Math.Clamp(StreamIdleTimeoutSeconds > 0 ? StreamIdleTimeoutSeconds : 60, 5, 300);
        ImageRequestTimeoutSeconds = Math.Clamp(ImageRequestTimeoutSeconds > 0 ? ImageRequestTimeoutSeconds : 300, 30, 900);
        if (MainThreadProbeBudgetMs <= 1_500) MainThreadProbeBudgetMs = 5_000;
        MainThreadProbeBudgetMs = Math.Clamp(MainThreadProbeBudgetMs, 1_000, 30_000);
        if (BackgroundProbeBudgetMs <= 15_000) BackgroundProbeBudgetMs = 30_000;
        BackgroundProbeBudgetMs = Math.Clamp(BackgroundProbeBudgetMs, 5_000, 120_000);
        MaxXmlScanFiles = Math.Clamp(MaxXmlScanFiles > 0 ? MaxXmlScanFiles : 50_000, 1_000, 100_000);
    }

    public void EnsureApiConfigsInitialized()
    {
        if (!string.Equals(ApiEndpoint ?? string.Empty, _lastSyncedEndpoint, StringComparison.Ordinal) ||
            !string.Equals(ApiKey ?? string.Empty, _lastSyncedKey, StringComparison.Ordinal) ||
            !string.Equals(ModelName ?? string.Empty, _lastSyncedModel, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(ApiEndpoint) || !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(ModelName))
            {
                var inferred = AIProviderRegistry.InferProviderFromEndpoint(ApiEndpoint);
                var existing = ApiConfigs.FirstOrDefault(c => c.IsEnabled) ?? ApiConfigs.FirstOrDefault();
                if (existing == null)
                {
                    existing = new ApiConfigEntry();
                    ApiConfigs.Insert(0, existing);
                }

                existing.IsEnabled = true;
                existing.Provider = inferred;
                existing.ApiKey = (ApiKey ?? string.Empty).Trim();
                existing.CustomUrl = inferred.UsesCustomUrl()
                    ? (ApiEndpoint ?? string.Empty).Trim()
                    : (string.Equals(AIProviderRegistry.GetEndpointUrl(inferred), (ApiEndpoint ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : (ApiEndpoint ?? string.Empty).Trim());
                if (!string.IsNullOrWhiteSpace(existing.CustomUrl) && !inferred.UsesCustomUrl())
                {
                    existing.Provider = AIProvider.Custom;
                }
                existing.SelectedModel = (ModelName ?? string.Empty).Trim();
                SelectedProvider = existing.Provider;
            }

            _lastSyncedEndpoint = ApiEndpoint ?? string.Empty;
            _lastSyncedKey = ApiKey ?? string.Empty;
            _lastSyncedModel = ModelName ?? string.Empty;
        }

        if (ApiConfigs.Count == 0)
        {
            var initialProvider = SelectedProvider is AIProvider.None or AIProvider.Custom &&
                                  string.IsNullOrWhiteSpace(ApiEndpoint) &&
                                  string.IsNullOrWhiteSpace(ApiKey) &&
                                  string.IsNullOrWhiteSpace(ModelName)
                ? AIProvider.Player2
                : (SelectedProvider == AIProvider.None ? AIProvider.Player2 : SelectedProvider);

            ApiConfigs.Add(new ApiConfigEntry
            {
                IsEnabled = true,
                Provider = initialProvider,
                ApiKey = (ApiKey ?? string.Empty).Trim(),
                CustomUrl = (ApiEndpoint ?? string.Empty).Trim(),
                SelectedModel = (ModelName ?? string.Empty).Trim(),
                CustomModelName = (ModelName ?? string.Empty).Trim()
            });
            SyncActiveConfigToPrimaryFields();
        }
    }

    public void SyncActiveConfigToPrimaryFields()
    {
        var active = GetEffectiveActiveConfig();
        if (active != null)
        {
            SelectedProvider = active.Provider;
            ApiEndpoint = active.GetResolvedEndpoint();
            ApiKey = active.ApiKey ?? string.Empty;
            ModelName = active.GetResolvedModel();
        }

        _lastSyncedEndpoint = ApiEndpoint ?? string.Empty;
        _lastSyncedKey = ApiKey ?? string.Empty;
        _lastSyncedModel = ModelName ?? string.Empty;
    }

    public ApiConfigEntry? GetEffectiveActiveConfig() =>
        ApiConfigs.FirstOrDefault(c => c.IsMinimallyValid())
        ?? ApiConfigs.FirstOrDefault(c => c.IsEnabled)
        ?? ApiConfigs.FirstOrDefault();

    public void LoadFromUserConfigIfPresent()
    {
        try
        {
            var path = UserConfigFilePath;
            if (!File.Exists(path))
            {
                EnsureApiConfigsInitialized();
                ClampAllBudgets();
                return;
            }

            var doc = XDocument.Load(path);
            var root = doc.Root?.Element("ModSettings") ?? doc.Root;
            if (root == null)
            {
                EnsureApiConfigsInitialized();
                ClampAllBudgets();
                return;
            }

            ApiEndpoint = root.Element("ApiEndpoint")?.Value ?? ApiEndpoint;
            ApiKey = root.Element("ApiKey")?.Value ?? ApiKey;
            ModelName = root.Element("ModelName")?.Value ?? ModelName;
            ImageApiEndpoint = root.Element("ImageApiEndpoint")?.Value ?? ImageApiEndpoint;
            ImageApiKey = root.Element("ImageApiKey")?.Value ?? ImageApiKey;
            ImageModelName = root.Element("ImageModelName")?.Value ?? ImageModelName;
            CustomSystemPrompt = root.Element("CustomSystemPrompt")?.Value ?? CustomSystemPrompt;
            PersonaPrompt = root.Element("PersonaPrompt")?.Value ?? PersonaPrompt;
            CustomAvatarPath = root.Element("CustomAvatarPath")?.Value ?? CustomAvatarPath;
            PromptOverrides = AgentPrompts.ReadOverrides(root.Element("PromptOverrides"));
            if (Enum.TryParse<AIProvider>(root.Element("SelectedProvider")?.Value, true, out var prov))
                SelectedProvider = prov;

            var apiConfigsEl = root.Element("ApiConfigs");
            if (apiConfigsEl != null)
            {
                var loadedConfigs = new List<ApiConfigEntry>();
                foreach (var li in apiConfigsEl.Elements("li"))
                {
                    loadedConfigs.Add(new ApiConfigEntry
                    {
                        IsEnabled = !bool.TryParse(li.Element("IsEnabled")?.Value, out var en) || en,
                        Provider = Enum.TryParse<AIProvider>(li.Element("Provider")?.Value, true, out var p) ? p : AIProvider.Custom,
                        ApiKey = li.Element("ApiKey")?.Value ?? string.Empty,
                        CustomUrl = li.Element("CustomUrl")?.Value ?? string.Empty,
                        SelectedModel = li.Element("SelectedModel")?.Value ?? string.Empty,
                        CustomModelName = li.Element("CustomModelName")?.Value ?? string.Empty
                    });
                }
                if (loadedConfigs.Count > 0) ApiConfigs = loadedConfigs;
            }

            ReadFloat(root, "Temperature", ref Temperature);
            ReadInt(root, "RequestTimeoutSeconds", ref RequestTimeoutSeconds);
            ReadInt(root, "StreamIdleTimeoutSeconds", ref StreamIdleTimeoutSeconds);
            ReadInt(root, "ImageRequestTimeoutSeconds", ref ImageRequestTimeoutSeconds);
            ReadInt(root, "MainThreadProbeBudgetMs", ref MainThreadProbeBudgetMs);
            ReadInt(root, "BackgroundProbeBudgetMs", ref BackgroundProbeBudgetMs);
            ReadInt(root, "MaxXmlScanFiles", ref MaxXmlScanFiles);
            ReadInt(root, "MaxSessionsPerSave", ref MaxSessionsPerSave);
            ReadInt(root, "MaxSessionMessages", ref MaxSessionMessages);
            ReadInt(root, "MaxSessionRetentionChars", ref MaxSessionRetentionChars);
            ReadInt(root, "RecentFullEvidenceMessages", ref RecentFullEvidenceMessages);
            if (bool.TryParse(root.Element("AskConversationHistory")?.Value, out var askHistory))
                AskConversationHistory = askHistory;
            if (bool.TryParse(root.Element("DefaultConversationHistory")?.Value, out var defaultHistory))
                DefaultConversationHistory = defaultHistory;

            _lastSyncedEndpoint = ApiEndpoint ?? string.Empty;
            _lastSyncedKey = ApiKey ?? string.Empty;
            _lastSyncedModel = ModelName ?? string.Empty;
            EnsureApiConfigsInitialized();
            SyncActiveConfigToPrimaryFields();
            ClampAllBudgets();
            ConfigurationError = string.Empty;
        }
        catch (Exception ex)
        {
            ConfigurationError = UiText.Defer("GCFAgent_ConfigReadFailed", ex.Message);
        }
    }

    public void SaveToUserConfig() => TrySaveToUserConfig();

    public bool TrySaveToUserConfig()
    {
        try
        {
            foreach (var cfg in ApiConfigs)
            {
                if (!string.IsNullOrWhiteSpace(cfg.CustomUrl))
                {
                    cfg.CustomUrl = AIProviderRegistry.NormalizeChatCompletionsEndpoint(cfg.CustomUrl);
                }
            }

            ClampAllBudgets();
            SyncActiveConfigToPrimaryFields();

            var conn = CaptureConnection();
            try
            {
                Validate(conn);
                ConfigurationError = string.Empty;
            }
            catch (Exception vex)
            {
                ConfigurationError = vex.Message;
            }

            var path = UserConfigFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var apiConfigsEl = new XElement("ApiConfigs");
            foreach (var cfg in ApiConfigs)
            {
                apiConfigsEl.Add(new XElement("li",
                    new XElement("IsEnabled", cfg.IsEnabled),
                    new XElement("Provider", cfg.Provider.ToString()),
                    new XElement("ApiKey", cfg.ApiKey ?? string.Empty),
                    new XElement("CustomUrl", cfg.CustomUrl ?? string.Empty),
                    new XElement("SelectedModel", cfg.SelectedModel ?? string.Empty),
                    new XElement("CustomModelName", cfg.CustomModelName ?? string.Empty)));
            }

            var doc = new XDocument(
                new XElement("SettingsBlock",
                    new XElement("ModSettings",
                        new XAttribute("Class", "GiantCreatureFramework.Agent.AgentSettings"),
                        new XElement("ApiEndpoint", ApiEndpoint ?? string.Empty),
                        new XElement("ApiKey", ApiKey ?? string.Empty),
                        new XElement("ModelName", ModelName ?? string.Empty),
                        new XElement("SelectedProvider", SelectedProvider.ToString()),
                        new XElement("ImageApiEndpoint", ImageApiEndpoint ?? string.Empty),
                        new XElement("ImageApiKey", ImageApiKey ?? string.Empty),
                        new XElement("ImageModelName", ImageModelName ?? string.Empty),
                        new XElement("CustomSystemPrompt", CustomSystemPrompt ?? string.Empty),
                        new XElement("PersonaPrompt", PersonaPrompt ?? string.Empty),
                        new XElement("CustomAvatarPath", CustomAvatarPath ?? string.Empty),
                        AgentPrompts.WriteOverrides(PromptOverrides),
                        apiConfigsEl,
                        new XElement("Temperature", Temperature.ToString(CultureInfo.InvariantCulture)),
                        new XElement("RequestTimeoutSeconds", RequestTimeoutSeconds),
                        new XElement("StreamIdleTimeoutSeconds", StreamIdleTimeoutSeconds),
                        new XElement("ImageRequestTimeoutSeconds", ImageRequestTimeoutSeconds),
                        new XElement("MainThreadProbeBudgetMs", MainThreadProbeBudgetMs),
                        new XElement("BackgroundProbeBudgetMs", BackgroundProbeBudgetMs),
                        new XElement("MaxXmlScanFiles", MaxXmlScanFiles),
                        new XElement("MaxSessionsPerSave", MaxSessionsPerSave),
                        new XElement("MaxSessionMessages", MaxSessionMessages),
                        new XElement("MaxSessionRetentionChars", MaxSessionRetentionChars),
                        new XElement("RecentFullEvidenceMessages", RecentFullEvidenceMessages),
                        new XElement("AskConversationHistory", AskConversationHistory),
                        new XElement("DefaultConversationHistory", DefaultConversationHistory))));

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                doc.Save(tempPath);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            ConfigurationError = UiText.Defer("GCFAgent_ConfigSaveFailed", ex.Message);
            return false;
        }
    }

    public ModelConnection CaptureConnection()
    {
        var chain = CaptureConnectionChain();
        return chain.Count > 0 ? chain[0] : BuildConnectionFromEntry(null);
    }

    public IReadOnlyList<ModelConnection> CaptureConnectionChain()
    {
        ClampAllBudgets();
        EnsureApiConfigsInitialized();

        var list = new List<ModelConnection>();
        foreach (var cfg in ApiConfigs)
        {
            if (cfg.IsMinimallyValid())
            {
                var conn = BuildConnectionFromEntry(cfg);
                if (!list.Any(existing =>
                    existing.Provider == conn.Provider &&
                    string.Equals(existing.Endpoint, conn.Endpoint, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Model, conn.Model, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(conn);
                }
            }
        }

        if (list.Count == 0)
        {
            var fallbackCfg = ApiConfigs.FirstOrDefault(c => c.IsEnabled) ?? ApiConfigs.FirstOrDefault();
            list.Add(BuildConnectionFromEntry(fallbackCfg));
        }

        return list;
    }

    private ModelConnection BuildConnectionFromEntry(ApiConfigEntry? entry)
    {
        var provider = entry?.Provider ?? SelectedProvider;
        var endpoint = entry != null ? entry.GetResolvedEndpoint() : (ApiEndpoint ?? string.Empty).Trim();
        var apiKey = entry != null ? (entry.ApiKey ?? string.Empty).Trim() : (ApiKey ?? string.Empty).Trim();
        var model = entry != null ? entry.GetResolvedModel() : (ModelName ?? string.Empty).Trim();

        return new ModelConnection(
            Endpoint: endpoint,
            ApiKey: apiKey,
            Model: model,
            Temperature: Temperature,
            MaxRounds: MaxToolRounds,
            TimeoutSeconds: RequestTimeoutSeconds,
            IdleSeconds: StreamIdleTimeoutSeconds,
            Provider: provider,
            MaxOutputTokens: 0,
            MaxContextChars: MaxContextChars,
            ContextReserveChars: 0,
            MaxTotalToolCalls: MaxTotalToolCalls,
            MaxToolCallsPerRound: MaxToolCallsPerRound,
            MaxToolResultChars: 0,
            HistoricalToolCompactChars: HistoricalToolCompactChars,
            MaxResponseChars: MaxResponseChars,
            ExtraHeaders: AIProviderRegistry.GetExtraHeaders(provider));
    }

    public static void Validate(ModelConnection connection)
    {
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(UiText.Defer("GCFAgent_ValidateEndpointRequired"));
        }

        if (connection.Provider != AIProvider.Player2 && string.IsNullOrWhiteSpace(connection.Model))
        {
            throw new ArgumentException(UiText.Defer("GCFAgent_ValidateModelRequired"));
        }
    }

    private static void ReadInt(XElement parent, string name, ref int target)
    {
        if (int.TryParse(parent.Element(name)?.Value, out var val)) target = val;
    }

    private static void ReadFloat(XElement parent, string name, ref float target)
    {
        if (float.TryParse(parent.Element(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) target = val;
    }
}
