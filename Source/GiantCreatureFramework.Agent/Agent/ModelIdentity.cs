namespace GiantCreatureFramework.Agent;

/// <summary>Non-secret identity of the connection actually used by an assistant reply.</summary>
public sealed record ModelIdentity(AIProvider Provider, string RequestedModel, string ResponseModel = "", bool IsFallback = false)
{
    public string Label => $"{Provider.ShortLabel()}: {RequestedModel}";
    public string Details => UiText.Format("GCFAgent_ModelIdentityRequested", Label)
        + (string.IsNullOrEmpty(ResponseModel) ? "" : "\n" + UiText.Format("GCFAgent_ModelIdentityResponse", ResponseModel))
        + (IsFallback ? "\n" + UiText.Get("GCFAgent_ModelIdentityFallback") : "");
}

/// <summary>Shared by both model controls so a running request cannot be mislabeled by later settings changes.</summary>
public static class ModelIdentityDisplay
{
    public static ModelIdentity? CurrentRequest(AgentSession session)
    {
        lock (session.SyncRoot)
            return session.IsBusy ? session.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.ModelIdentity : null;
    }

    public static ModelIdentity? LastReply(AgentSession session)
    {
        lock (session.SyncRoot)
            return session.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.ModelIdentity;
    }
}
