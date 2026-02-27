using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_EN.PdaMessenger;

/// <summary>
/// Metadata for a channel or DM conversation, with optional messages for the currently viewed chat.
/// When the client is not viewing this chat, <see cref="Messages"/> is empty to save bandwidth.
/// </summary>
[Serializable, NetSerializable]
public sealed class STMessengerChat
{
    /// <summary>
    /// Prefix for DM chat IDs. Format: "dm:{characterName}".
    /// </summary>
    public const string DmChatPrefix = "dm:";

    /// <summary>
    /// Maximum length of a reply snippet before truncation (shared between server and client).
    /// </summary>
    public const int MaxReplySnippetLength = 50;

    /// <summary>
    /// Channel prototype ID or "dm:{charName}" for DMs.
    /// </summary>
    public readonly string Id;

    /// <summary>
    /// Localized channel name or contact character name.
    /// </summary>
    public readonly string DisplayName;

    /// <summary>
    /// True for DM conversations, false for public channels.
    /// </summary>
    public readonly bool IsDirect;

    /// <summary>
    /// Messages since last seen (per-PDA tracking).
    /// </summary>
    public readonly int UnreadCount;

    /// <summary>
    /// Whether this channel is muted for this PDA (suppresses ringer).
    /// </summary>
    public readonly bool IsMuted;

    /// <summary>
    /// Messages — only populated for the chat the client is currently viewing. Empty for other chats.
    /// </summary>
    public readonly IReadOnlyList<STMessengerMessage> Messages;

    public STMessengerChat(
        string id,
        string displayName,
        bool isDirect,
        int unreadCount,
        bool isMuted,
        IReadOnlyList<STMessengerMessage>? messages = null)
    {
        Id = id;
        DisplayName = displayName;
        IsDirect = isDirect;
        UnreadCount = unreadCount;
        IsMuted = isMuted;
        Messages = messages ?? Array.Empty<STMessengerMessage>();
    }
}
