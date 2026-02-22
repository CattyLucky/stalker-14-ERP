using Robust.Shared.Prototypes;

namespace Content.Shared._Stalker.ZoneArtifact.Components; // ST14-EN: Moved to shared

[RegisterComponent]
public sealed partial class ZoneArtifactComponent : Component
{
    [DataField]
    [Access] // ST14-EN; read-only as this should only set via YAML. This is used
    public EntProtoId? Anomaly;
}
