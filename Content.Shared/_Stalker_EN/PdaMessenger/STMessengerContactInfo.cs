using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_EN.PdaMessenger;

/// <summary>
/// Contact metadata sent to the client for rendering contact rows with PDA IDs.
/// </summary>
[Serializable, NetSerializable]
public sealed class STMessengerContactInfo
{
    /// <summary>
    /// The contact's in-game character name.
    /// </summary>
    public readonly string CharacterName;

    /// <summary>
    /// The contact's PDA messenger ID (e.g. "472-819"), or null if unknown.
    /// </summary>
    public readonly string? MessengerId;

    /// <summary>
    /// The contact's last-known faction name, or null if unknown.
    /// </summary>
    public readonly string? FactionName;

    public STMessengerContactInfo(string characterName, string? messengerId, string? factionName)
    {
        CharacterName = characterName;
        MessengerId = messengerId;
        FactionName = factionName;
    }
}
