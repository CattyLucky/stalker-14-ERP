using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_EN.PdaMessenger;

/// <summary>
/// Contact metadata sent to the client for rendering contact rows with faction patches and PDA IDs.
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
    /// RSI state name in band.rsi for the contact's faction patch (e.g. "dolg", "stalker"), or null if unknown.
    /// </summary>
    public readonly string? BandIcon;

    public STMessengerContactInfo(string characterName, string? messengerId, string? bandIcon)
    {
        CharacterName = characterName;
        MessengerId = messengerId;
        BandIcon = bandIcon;
    }
}
