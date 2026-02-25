using Content.Server._Stalker.Teeth;
using Content.Server._Stalker.ZoneArtifact.Components.Spawner;
using Content.Server._Stalker_EN.Emission;
using Content.Server.Chat.Managers;
using Content.Shared._Stalker.Teeth;
using Content.Shared._Stalker.ZoneArtifact.Components;
using Content.Shared._Stalker_EN.RitualChasm;
using Content.Shared.Chat;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._Stalker_EN.RitualChasm;

public sealed class RitualChasmSystem : SharedRitualChasmSystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;
    [Dependency] private readonly EmissionLightningSystem _emissionLightningSystem = default!;
    [Dependency] private readonly TeethPullingSystem _teethPullingSystem = default!;
    [Dependency] private readonly ActorSystem _actorSystem = default!;

    protected override void PunishEntity(EntityUid uid)
    {
        EnsureComp<DontStartCollideWithRitualChasmOnceComponent>(uid); // So that it doesnt trigger twice or something
        _emissionLightningSystem.SpawnLightningImmediately(
            "EmissionLightningEffect",
            _transformSystem.GetMapCoordinates(uid),
            _emissionLightningSystem.LightningTargetPredicate,
            boltRange: 6f,
            boltCount: 6
        );
    }

    private bool TryGetArtifactTier(EntityUid uid, out int tier)
    {
        if (MetaData(uid).EntityPrototype is not { } artifactProtoId)
            goto fail;

        if (!TryComp<ZoneArtifactComponent>(uid, out var artifactComponent) ||
            !_prototypeManager.TryIndex(artifactComponent.Anomaly, out var anomalyPrototype) ||
            !anomalyPrototype.TryGetComponent<ZoneArtifactSpawnerComponent>(out var spawnerComponent, EntityManager.ComponentFactory))
            goto fail;

        foreach (var spawnerEntry in spawnerComponent.Artifacts)
        {
            if (spawnerEntry.PrototypeId is not { } entryProtoId ||
                entryProtoId != artifactProtoId)
                continue;

            tier = spawnerEntry.Tier;
            return true;
        }

    fail:
        tier = -1;
        return false;
    }

    private int GetArtifactReward(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity)
    {
        if (!TryGetArtifactTier(uid, out var tier))
            return 0;

        return (int)(ritualChasmEntity.Comp.RewardedPerTier * tier); // floored
    }

    private int StealTeeth(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity)
    {
        if (!TryComp<TeethPullComponent>(uid, out var teethPullComponent) ||
            teethPullComponent.TeethCount <= 0)
            return 0;

        var taken = teethPullComponent.TeethCount;
        teethPullComponent.TeethCount = 0;
        _teethPullingSystem.EnsureAccent((uid, teethPullComponent));

        return (int)(ritualChasmEntity.Comp.RewardedPerTooth * taken); // floored
    }

    protected override void HandleReturnedEntity(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity)
    {
        var outDirection = -GetUnitVectorFrom(ritualChasmEntity.Owner, uid);

        var initialTime = GameTiming.CurTime + FallTime;
        var accumulatedTime = TimeSpan.Zero;

        var rewardedEntityCount = GetArtifactReward(uid, ritualChasmEntity) + StealTeeth(uid, ritualChasmEntity);
        for (; rewardedEntityCount != 0; --rewardedEntityCount)
        {
            var spawnedUid = Spawn(ritualChasmEntity.Comp.RewardedEntityProtoId);
            EnsureComp<DontStartCollideWithRitualChasmOnceComponent>(spawnedUid);
            ritualChasmEntity.Comp.EntitiesPendingThrowBack.Add(spawnedUid);

            ritualChasmEntity.Comp.ThrowBackQueue.Enqueue((
                spawnedUid, // spawn in nullspace for now, will be teleported back to chasm LATER
                outDirection + RobustRandom.NextVector2(0.1f), // add some HARDCODED randomization to throw direction
                initialTime + accumulatedTime
            ));
            PhysicsSystem.SetAngularVelocity(spawnedUid, RobustRandom.NextFloat(-15f, 15f)); // ALSO hardcoded :joy:

            accumulatedTime += TimeSpan.FromSeconds(0.5f); // ST14-EN: Arbitrary delay between each throw back, can be tweaked later
        }

        return;
    }

    protected override void DoLocalAnnouncement(EntityUid uid, string message)
    {
        base.DoLocalAnnouncement(uid, message);
        if (!_actorSystem.TryGetSession(uid, out var session))
            return;

        var wrappedMessage = Loc.GetString("chat-manager-server-wrap-message", ("message", FormattedMessage.EscapeText(message)));
        _chatManager.ChatMessageToOne(ChatChannel.Server, message, wrappedMessage, default, false, session!.Channel, colorOverride: Color.Sienna);
    }
}
