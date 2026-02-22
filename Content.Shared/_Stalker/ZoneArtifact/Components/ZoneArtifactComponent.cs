using Robust.Shared.Prototypes;

namespace Content.Shared._Stalker.ZoneArtifact.Components;
// ST14-EN: MOVED TS TO SHARED FROM SERVER LIKE A GOD

[RegisterComponent, Access] // ST14-EN: don't allow setting this outside of yaml o algo
public sealed partial class ZoneArtifactComponent : Component
{
    [DataField]
    public EntProtoId? Anomaly;
}
