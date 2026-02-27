using Content.Shared._Stalker_EN.PdaMessenger;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Content.Server._Stalker_EN.PdaMessenger;

/// <summary>
/// Server-side messenger state for a PDA cartridge.
/// Not networked — client receives data via <see cref="STMessengerUiState"/> through the BUI system.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
[Access(typeof(STMessengerSystem))]
public sealed partial class STMessengerServerComponent : Component
{
    /// <summary>
    /// This PDA's unique messenger ID (e.g. "472-819"). Loaded from DB on spawn.
    /// </summary>
    [ViewVariables]
    public string MessengerId = string.Empty;

    /// <summary>
    /// Character name of the PDA's original owner.
    /// Stored as string so it survives entity deletion (e.g. body cleanup after death).
    /// Used for sender identity on all outgoing messages.
    /// </summary>
    [ViewVariables]
    public string OwnerCharacterName = string.Empty;

    /// <summary>
    /// Contacts loaded from DB for this character.
    /// Key = contact character name, Value = last-known faction name (null if unknown).
    /// </summary>
    [ViewVariables]
    public Dictionary<string, string?> Contacts = new();

    /// <summary>
    /// Channels the player has muted (suppresses ringer notification).
    /// </summary>
    [DataField]
    public HashSet<ProtoId<STMessengerChannelPrototype>> MutedChannels = new();

    /// <summary>
    /// Per-channel last-seen message ID for unread tracking.
    /// Key = chat ID (channel proto ID or "dm:{name}"), Value = last seen message ID.
    /// </summary>
    [ViewVariables]
    public Dictionary<string, uint> LastSeenMessageId = new();

    /// <summary>
    /// Minimum time between messages for this PDA.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan SendCooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Next allowed send time (absolute simulation time). Paused when entity is paused.
    /// </summary>
    [AutoPausedField]
    [ViewVariables]
    public TimeSpan NextSendTime;

    /// <summary>
    /// Rate-limits contact add/remove operations to prevent DB flooding.
    /// </summary>
    [AutoPausedField]
    [ViewVariables]
    public TimeSpan NextInteractionTime;
}
