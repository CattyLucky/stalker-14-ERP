using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_EN.PdaMessenger;

/// <summary>
/// A single messenger message with optional reply context.
/// Sent from server to client as part of <see cref="STMessengerChat.Messages"/>.
/// </summary>
[Serializable, NetSerializable]
public sealed class STMessengerMessage
{
    /// <summary>
    /// Auto-incremented ID unique within its chat.
    /// </summary>
    public readonly uint Id;

    /// <summary>
    /// Display name of the sender — character name for DMs, possibly an anonymous pseudonym for channels.
    /// </summary>
    public readonly string Sender;

    /// <summary>
    /// Message body text.
    /// </summary>
    public readonly string Content;

    /// <summary>
    /// ID of message being replied to, if any.
    /// </summary>
    public readonly uint? ReplyToId;

    /// <summary>
    /// Short preview of the replied-to message, truncated to ~50 chars.
    /// </summary>
    public readonly string? ReplySnippet;

    /// <summary>
    /// Server CurTime when the message was sent.
    /// </summary>
    public readonly TimeSpan Timestamp;

    /// <summary>
    /// Faction name of the sender, if known and non-anonymous. Null for anonymous, DM, or unknown faction.
    /// </summary>
    public readonly string? SenderFaction;

    public STMessengerMessage(
        uint id,
        string sender,
        string content,
        TimeSpan timestamp,
        uint? replyToId = null,
        string? replySnippet = null,
        string? senderFaction = null)
    {
        Id = id;
        Sender = sender;
        Content = content;
        Timestamp = timestamp;
        ReplyToId = replyToId;
        ReplySnippet = replySnippet;
        SenderFaction = senderFaction;
    }
}
