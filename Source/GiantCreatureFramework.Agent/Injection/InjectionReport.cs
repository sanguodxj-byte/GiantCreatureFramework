using System.Text.Encodings.Web;
using System.Text.Json;

namespace GiantCreatureFramework.Agent.Injection;

/// <summary>
/// One finding of an injection. Code is a stable identifier; Detail carries only text produced
/// by the game itself (log lines, exception messages, config errors), never wording authored here.
/// </summary>
public sealed record InjectionIssue(string Code, string Subject, string Detail);

/// <summary>Outcome of one injection run over the whole agent content root.</summary>
public sealed class InjectionReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public bool DryRun { get; init; }
    /// <summary>True when every database change of this run was undone, either because it was a dry run or because it failed.</summary>
    public bool RolledBack { get; set; }
    public bool Committed => !RolledBack && Errors.Count == 0;
    public List<string> Added { get; } = new();
    public List<string> Replaced { get; } = new();
    public List<string> Implied { get; } = new();
    public List<string> Textures { get; } = new();
    public List<InjectionIssue> Errors { get; } = new();
    public List<InjectionIssue> Warnings { get; } = new();

    public void Error(string code, string subject = "", string detail = "") => Errors.Add(new(code, subject, detail));
    public void Warning(string code, string subject = "", string detail = "") => Warnings.Add(new(code, subject, detail));

    public string ToJson() => JsonSerializer.Serialize(new
    {
        committed = Committed, dryRun = DryRun, rolledBack = RolledBack,
        added = Added, replaced = Replaced, implied = Implied, textures = Textures,
        errors = Errors, warnings = Warnings
    }, JsonOptions);
}
