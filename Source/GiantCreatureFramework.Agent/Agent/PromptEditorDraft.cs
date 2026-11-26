namespace GiantCreatureFramework.Agent;

/// <summary>Edits stay local to the dialog until explicitly saved. Only edited sections are applied.</summary>
public sealed class PromptEditorDraft(AgentPromptSnapshot original)
{
    private readonly Dictionary<AgentPromptSection, string> _edits = new();
    private readonly HashSet<AgentPromptSection> _resets = new();
    public bool IsDirty => _edits.Count > 0 || _resets.Count > 0;
    public string Get(AgentPromptSection section) => _resets.Contains(section) ? AgentPrompts.Definition(section).DefaultText
        : _edits.TryGetValue(section, out var text) ? text : original[section];
    public void Edit(AgentPromptSection section, string text) { _resets.Remove(section); _edits[section] = text; }
    public void Reset(AgentPromptSection section) { _edits.Remove(section); _resets.Add(section); }
    public AgentPromptSnapshot Snapshot() => new(AgentPrompts.Definitions.ToDictionary(d => d.Section, d => Get(d.Section)));
    public void Apply(Action<AgentPromptSection, string> set, Action<AgentPromptSection> reset)
    {
        foreach (var section in _resets) reset(section);
        foreach (var pair in _edits) set(pair.Key, pair.Value);
    }
}
