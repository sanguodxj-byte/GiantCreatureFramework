using System.Text.Json;

namespace GiantCreatureFramework.Agent;

/// <summary>Symmetric streaming persistence; a failed read never authorizes overwriting the archive.</summary>
public static class SessionArchive
{
    public static async Task<List<AgentSession>> ReadAsync(string path)
    {
        if (!File.Exists(path)) return new();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var sessions = await JsonSerializer.DeserializeAsync<List<AgentSession>>(stream, Evidence.UnescapedJsonOptions).ConfigureAwait(false)
            ?? throw new InvalidDataException(UiText.Get("GCFAgent_ArchiveFileEmpty"));
        if (sessions.Any(s => s == null || s.Messages == null || s.Messages.Any(m => m == null || m.ToolCalls == null)))
            throw new InvalidDataException(UiText.Get("GCFAgent_ArchiveStructureInvalid"));
        return sessions;
    }

    public static async Task WriteAsync(string path, IReadOnlyList<AgentSession> sessions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, sessions, Evidence.UnescapedJsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
