using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_EN.PdaMessenger;

/// <summary>
/// Full UI state for the messenger cartridge, sent via the CartridgeLoader BUI system.
/// Messages are only included for the chat the client is currently viewing (lazy loading).
/// </summary>
[Serializable, NetSerializable]
public sealed class STMessengerUiState : BoundUserInterfaceState
{
    /// <summary>
    /// This PDA's unique messenger ID (displayed in UI header).
    /// </summary>
    public readonly string MessengerId;

    /// <summary>
    /// Public channel chats with per-PDA unread/mute state.
    /// Messages only populated for the currently viewed chat.
    /// </summary>
    public readonly List<STMessengerChat> Channels;

    /// <summary>
    /// DM conversations for this PDA.
    /// Messages only populated for the currently viewed chat.
    /// </summary>
    public readonly List<STMessengerChat> DirectMessages;

    /// <summary>
    /// This PDA's contact list with faction patch and PDA ID metadata.
    /// </summary>
    public readonly List<STMessengerContactInfo> Contacts;

    public STMessengerUiState(
        string messengerId,
        List<STMessengerChat> channels,
        List<STMessengerChat> directMessages,
        List<STMessengerContactInfo> contacts)
    {
        MessengerId = messengerId;
        Channels = channels;
        DirectMessages = directMessages;
        Contacts = contacts;
    }
}
