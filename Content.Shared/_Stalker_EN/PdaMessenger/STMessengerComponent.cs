using Robust.Shared.GameObjects;

namespace Content.Shared._Stalker_EN.PdaMessenger;

/// <summary>
/// Marker component for the stalker messenger cartridge.
/// All mutable server state lives in the server-only STMessengerServerComponent.
/// This shared component exists for YAML prototype registration and the UIFragment system.
/// </summary>
[RegisterComponent]
public sealed partial class STMessengerComponent : Component;
