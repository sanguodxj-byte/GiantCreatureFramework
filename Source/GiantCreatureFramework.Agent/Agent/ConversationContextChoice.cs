namespace GiantCreatureFramework.Agent;

/// <summary>Resolves the per-conversation choice before a player submits a follow-up.</summary>
public static class ConversationContextChoice
{
    /// <summary>Returns false when a follow-up needs the player's choice. Does not append messages or start a request.</summary>
    public static bool PrepareSend(AgentSession session, bool askForNewConversation, bool defaultUseHistory)
    {
        lock (session.SyncRoot)
        {
            if (session.UseConversationHistory.HasValue) return true;
            if (!askForNewConversation)
            {
                session.UseConversationHistory = defaultUseHistory;
                return true;
            }
            return !session.Messages.Any(m => m.Role == ChatRole.User);
        }
    }
}
