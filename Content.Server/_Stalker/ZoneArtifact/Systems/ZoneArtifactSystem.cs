using Content.Server._Stalker.ZoneArtifact.Components.Detector;
using Content.Shared._Stalker.ZoneAnomaly.Components;
using Content.Shared._Stalker.ZoneArtifact.Components; // ST14-EN: Moved artis to shared
using Content.Shared._Stalker.ZoneArtifact.Events;
using Content.Shared.Interaction;

namespace Content.Server._Stalker.ZoneArtifact.Systems;

public sealed class ZoneArtifactSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ZoneArtifactComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnInteractUsing(Entity<ZoneArtifactComponent> artifact, ref InteractUsingEvent args)
    {
        if (!TryComp<ZoneArtifactDetectorTargetComponent>(artifact, out var target))
            return;

        if (!TryComp<ZoneAnomalyDetectorComponent>(args.Used, out var detector))
            return;

        if (!TryComp<ZoneAnomalyDetectorArtifactActivatorComponent>(args.Used, out var activator))
            return;

        if (!detector.Enabled)
            return;

        if (!target.Detectable)
            return;

        if (target.DetectedLevel > activator.Level)
            return;

        var ev = new ZoneArtifactActivatedEvent(args.Target);
        RaiseLocalEvent(args.Target, ev);
    }

    public EntityUid? SpawnAnomaly(Entity<ZoneArtifactComponent> artifact)
    {
        if (artifact.Comp.Anomaly is not { } anomaly)
            return null;

        return Spawn(anomaly, Transform(artifact).Coordinates);
    }
}
