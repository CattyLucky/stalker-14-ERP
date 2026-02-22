using Content.Server._Stalker.ZoneArtifact.Effects.Components;
using Content.Server._Stalker.ZoneArtifact.Systems;
using Content.Shared._Stalker.ZoneArtifact.Components; // ST14-EN: Moved artis to shared
using Content.Shared._Stalker.ZoneArtifact.Events;
using Robust.Shared.Spawners;

namespace Content.Server._Stalker.ZoneArtifact.Effects.Systems;

public sealed class SpawnAnomalyZoneArtifactSystem : EntitySystem
{
    [Dependency] private readonly ZoneArtifactSystem _zoneArtifact = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SpawnAnomalyZoneArtifactComponent, ZoneArtifactActivatedEvent>(OnActivated);
    }

    private void OnActivated(Entity<SpawnAnomalyZoneArtifactComponent> effect, ref ZoneArtifactActivatedEvent args)
    {
        if (!TryComp<ZoneArtifactComponent>(effect, out var artifact))
            return;

        var ent = _zoneArtifact.SpawnAnomaly((effect, artifact));
        if (ent is { } anomaly)
        {
            var timedDespawn = EnsureComp<TimedDespawnComponent>(anomaly);
            timedDespawn.Lifetime = 18000;
            Dirty(anomaly, timedDespawn);
        }

        QueueDel(effect);
    }
}
