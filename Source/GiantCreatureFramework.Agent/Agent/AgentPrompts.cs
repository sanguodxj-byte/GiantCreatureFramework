using System.Collections.ObjectModel;
using System.Xml.Linq;

namespace GiantCreatureFramework.Agent;

/// <summary>Editable instruction sections shared by every provider.</summary>
public enum AgentPromptSection
{
    Main,
    OutputFormat,
    Persona,
    PersonaBoundary,
    Submission,
    Evidence,
    MissingSubmission,
    CitationRepair,
    Synthesis,
    ImageQuestion,
    ToolDiscovery
}

public sealed record AgentPromptDefinition(AgentPromptSection Section, string Label, string Description)
{
    public string DefaultText => AgentSkillStore.Current.Prompts[Section];
}

/// <summary>Prompt definitions backed by 1.7/AgentSkills/prompts.xml; zero hardcoded prompt literals in C#.</summary>
public static class AgentPrompts
{
    public static IReadOnlyList<AgentPromptDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new AgentPromptDefinition(AgentPromptSection.Main, "GCFAgent_PromptSectionLabel_Main", "GCFAgent_PromptSectionDesc_Main"),
        new AgentPromptDefinition(AgentPromptSection.OutputFormat, "GCFAgent_PromptSectionLabel_OutputFormat", "GCFAgent_PromptSectionDesc_OutputFormat"),
        new AgentPromptDefinition(AgentPromptSection.Persona, "GCFAgent_PromptSectionLabel_Persona", "GCFAgent_PromptSectionDesc_Persona"),
        new AgentPromptDefinition(AgentPromptSection.PersonaBoundary, "GCFAgent_PromptSectionLabel_PersonaBoundary", "GCFAgent_PromptSectionDesc_PersonaBoundary"),
        new AgentPromptDefinition(AgentPromptSection.Submission, "GCFAgent_PromptSectionLabel_Submission", "GCFAgent_PromptSectionDesc_Submission"),
        new AgentPromptDefinition(AgentPromptSection.Evidence, "GCFAgent_PromptSectionLabel_Evidence", "GCFAgent_PromptSectionDesc_Evidence"),
        new AgentPromptDefinition(AgentPromptSection.MissingSubmission, "GCFAgent_PromptSectionLabel_MissingSubmission", "GCFAgent_PromptSectionDesc_MissingSubmission"),
        new AgentPromptDefinition(AgentPromptSection.CitationRepair, "GCFAgent_PromptSectionLabel_CitationRepair", "GCFAgent_PromptSectionDesc_CitationRepair"),
        new AgentPromptDefinition(AgentPromptSection.Synthesis, "GCFAgent_PromptSectionLabel_Synthesis", "GCFAgent_PromptSectionDesc_Synthesis"),
        new AgentPromptDefinition(AgentPromptSection.ImageQuestion, "GCFAgent_PromptSectionLabel_ImageQuestion", "GCFAgent_PromptSectionDesc_ImageQuestion"),
        new AgentPromptDefinition(AgentPromptSection.ToolDiscovery, "GCFAgent_PromptSectionLabel_ToolDiscovery", "GCFAgent_PromptSectionDesc_ToolDiscovery"),
    });

    public static AgentPromptDefinition Definition(AgentPromptSection section) =>
        Definitions.Single(d => d.Section == section);

    public static XElement WriteOverrides(IReadOnlyDictionary<string, string> values) => new("PromptOverrides",
        new XElement("keys", values.Keys.Select(k => new XElement("li", k))),
        new XElement("values", values.Values.Select(v => new XElement("li", v))));

    public static Dictionary<string, string> ReadOverrides(XElement? element)
    {
        if (element == null) return new(StringComparer.Ordinal);
        var keys = element.Element("keys")?.Elements("li").Select(e => e.Value).ToArray() ?? [];
        var values = element.Element("values")?.Elements("li").Select(e => e.Value).ToArray() ?? [];
        if (keys.Length != values.Length) throw new InvalidDataException(UiText.Defer("GCFAgent_PromptOverridesMismatch"));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Length; i++) result.Add(keys[i], values[i]);
        return result;
    }
}

public sealed class AgentPromptSnapshot
{
    private readonly IReadOnlyDictionary<AgentPromptSection, string> _values;

    public AgentPromptSnapshot(IReadOnlyDictionary<AgentPromptSection, string>? values = null)
    {
        _values = new ReadOnlyDictionary<AgentPromptSection, string>(AgentPrompts.Definitions.ToDictionary(
            d => d.Section,
            d => values != null && values.TryGetValue(d.Section, out var value) ? value : d.DefaultText));
    }

    public string this[AgentPromptSection section] => _values[section];

    public string ComposeSystemPrompt()
    {
        var parts = new List<string>
        {
            AgentProtocol.Get("Security_ImmutableNoUserImg2Img"),
            this[AgentPromptSection.Main],
            this[AgentPromptSection.OutputFormat]
        };
        if (!string.IsNullOrWhiteSpace(this[AgentPromptSection.Persona]))
        {
            parts.Add(this[AgentPromptSection.Persona]);
            parts.Add(this[AgentPromptSection.PersonaBoundary]);
        }
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
