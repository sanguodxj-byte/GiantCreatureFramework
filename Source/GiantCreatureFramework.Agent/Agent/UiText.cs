namespace GiantCreatureFramework.Agent;

/// <summary>
/// Single access point for player-visible keyed text. All keys live in
/// GiantCreatureFramework_Agent_Keys.xml and carry the GCFAgent_ prefix.
/// </summary>
public static class UiText
{
    public const string KeyPrefix = "GCFAgent_";
    public const char DeferredSeparator = '\u001f';

    public const string KeySessionTitleNew = KeyPrefix + "SessionTitleNew";
    public const string KeySessionStatusReady = KeyPrefix + "SessionStatusReady";
    public const string KeySessionStatusAborting = KeyPrefix + "SessionStatusAborting";
    public const string KeySessionStatusAborted = KeyPrefix + "SessionStatusAborted";
    public const string KeySessionStatusTimeout = KeyPrefix + "SessionStatusTimeout";
    public const string KeySessionStatusRequestFailed = KeyPrefix + "SessionStatusRequestFailed";

    public static Func<string, string> Resolver { get; set; } = static key => key;

    public static string Get(string key) => Resolver(key);

    public static string Format(string key, params object?[] args) =>
        string.Format(Resolver(key), args.Select(a => (object)Render(a?.ToString())).ToArray());

    public static string Render(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return text ?? "";
        }
        var parts = text.Split(DeferredSeparator);
        var translated = Resolver(parts[0]);
        return parts.Length == 1
            ? translated
            : string.Format(translated, parts.Skip(1).Select(p => (object)Render(p)).ToArray());
    }

    public static string Defer(string key, params object?[] args) =>
        args.Length == 0 ? key : key + DeferredSeparator + string.Join(DeferredSeparator.ToString(), args.Select(a => a?.ToString()));
}
