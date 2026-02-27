using System.Numerics;
using Content.Shared._Stalker.Dizzy;
using Content.Shared.ActionBlocker;
using Content.Shared.Chasm;
using Content.Shared.Chat;
using Content.Shared.Flash;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared._Stalker_EN.RitualChasm;

public abstract class SharedRitualChasmSystem : EntitySystem
{
    [Dependency] protected readonly IGameTiming GameTiming = default!;
    [Dependency] protected readonly IRobustRandom RobustRandom = default!;
    [Dependency] protected readonly SharedPhysicsSystem PhysicsSystem = default!;
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly SharedPointLightSystem _pointLightSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedStunSystem _stunSystem = default!;
    [Dependency] private readonly SharedFlashSystem _flashSystem = default!;
    [Dependency] private readonly SharedDizzySystem _dizzySystem = default!;
    [Dependency] private readonly SharedChatSystem _chatSystem = default!;
    [Dependency] private readonly MobStateSystem _mobStateSystem = default!;
    [Dependency] private readonly EntityWhitelistSystem _entityWhitelistSystem = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlockerSystem = default!;
    [Dependency] private readonly ThrowingSystem _throwingSystem = default!;
    [Dependency] private readonly PullingSystem _pullingSystem = default!;

    protected static readonly TimeSpan FallTime = TimeSpan.FromSeconds(1.9d);

    private HashSet<EntityUid> _exitPoints = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RitualChasmExitPointComponent, ComponentStartup>(OnExitStartup);
        SubscribeLocalEvent<RitualChasmExitPointComponent, ComponentShutdown>(OnExitShutdown);

        SubscribeLocalEvent<RitualChasmComponent, ComponentShutdown>(OnChasmShutdown);
        SubscribeLocalEvent<RitualChasmComponent, StartCollideEvent>(OnChasmStartCollide);
        SubscribeLocalEvent<RitualChasmComponent, EndCollideEvent>(OnChasmEndCollide);
    }

    private void OnExitStartup(Entity<RitualChasmExitPointComponent> entity, ref ComponentStartup _)
        => _exitPoints.Add(entity.Owner);

    private void OnExitShutdown(Entity<RitualChasmExitPointComponent> entity, ref ComponentShutdown _)
        => _exitPoints.Remove(entity.Owner);

    private void OnChasmShutdown(Entity<RitualChasmComponent> entity, ref ComponentShutdown args)
    {
        // dont let entities accumulate in nullspace for free
        foreach (var uid in entity.Comp.EntitiesPendingThrowBack)
            PredictedQueueDel(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<RitualChasmComponent>();
        while (query.MoveNext(out var ritualChasmUid, out var ritualChasmComponent))
        {
            // These both assume that fall time is always the same

            if (ritualChasmComponent.ThrowBackQueue.Count != 0 &&
                ritualChasmComponent.ThrowBackQueue.Peek().Item3 < GameTiming.CurTime)
            {
                var (maybeUid, direction, _) = ritualChasmComponent.ThrowBackQueue.Dequeue();
                if (maybeUid is not { } uid)
                    continue;

                ritualChasmComponent.EntitiesPendingThrowBack.Remove(uid);
                _transformSystem.SetCoordinates(uid, Transform(ritualChasmUid).Coordinates);
                _throwingSystem.TryThrow(uid, direction, baseThrowSpeed: ritualChasmComponent.ThrowForce);
                _audioSystem.PlayPvs(ritualChasmComponent.ThrowSound, uid);
            }

            if (ritualChasmComponent.FallQueue.Count != 0 &&
                ritualChasmComponent.FallQueue.Peek().Item2 < GameTiming.CurTime)
            {
                var (uid, _) = ritualChasmComponent.FallQueue.Dequeue();

                if (!_entityWhitelistSystem.IsWhitelistPass(ritualChasmComponent.RelocatableEntities, uid))
                {
                    QueueDel(uid);
                    continue;
                }

                _audioSystem.PlayGlobal(ritualChasmComponent.RelocateSound, Filter.Broadcast(), true);
                if (_exitPoints.Count == 0)
                {
                    QueueDel(uid);
                    Log.Error($"Entity {ToPrettyString(uid)} being sacrificed to ritual chasm was deleted, as no exit points existed. MAP THEM!!!");
                    continue;
                }

                RemComp<ChasmFallingComponent>(uid);
                _actionBlockerSystem.UpdateCanMove(uid);

                StopPulling(uid);
                _transformSystem.SetCoordinates(uid, Transform(RobustRandom.Pick(_exitPoints)).Coordinates);

                // play only for the relocated
                _audioSystem.PlayGlobal(ritualChasmComponent.RelocatedLocalSound, uid);

                var popupLoc = Loc.GetString(ritualChasmComponent.RelocatedLocalPopup);
                _popupSystem.PopupEntity(popupLoc, uid, uid, PopupType.LargeCaution);
                DoLocalAnnouncement(uid, popupLoc);

                _stunSystem.TryKnockdown(uid, ritualChasmComponent.RelocatedStunDuration);
                _dizzySystem.TryApplyDizziness(uid, (float)ritualChasmComponent.RelocatedFlashDuration.TotalSeconds);
                _flashSystem.Flash(uid, null, null, ritualChasmComponent.RelocatedFlashDuration, 0f, displayPopup: false);
            }
        }
    }

    private void StopPulling(EntityUid uid)
    {
        if (TryComp<PullableComponent>(uid, out var pullableComponent) && _pullingSystem.IsPulled(uid, pullableComponent))
            _pullingSystem.TryStopPull(uid, pullableComponent);

        if (TryComp<PullerComponent>(uid, out var pullerComponent) && TryComp<PullableComponent>(pullerComponent.Pulling, out var pullable))
            _pullingSystem.TryStopPull(pullerComponent.Pulling.Value, pullable);
    }

    private void OnChasmStartCollide(Entity<RitualChasmComponent> entity, ref StartCollideEvent args)
    {
        if (HasComp<DontStartCollideWithRitualChasmOnceComponent>(args.OtherEntity))
            return;

        // already doomed
        if (HasComp<ChasmFallingComponent>(args.OtherEntity))
            return;

        OnHit(entity, args.OtherEntity);
    }

    private void OnChasmEndCollide(Entity<RitualChasmComponent> entity, ref EndCollideEvent args)
    {
        if (TryComp<DontStartCollideWithRitualChasmOnceComponent>(args.OtherEntity, out var dontCollideComponent))
            RemComp(args.OtherEntity, dontCollideComponent);
    }

    private void OnHit(Entity<RitualChasmComponent> entity, EntityUid fallingUid)
    {
        if (_entityWhitelistSystem.IsWhitelistPass(entity.Comp.PunishedEntities, fallingUid))
        {
            PunishEntity(fallingUid);

            _audioSystem.PlayPvs(entity.Comp.ThrowSound, entity.Owner);
            _throwingSystem.TryThrow(fallingUid, -GetUnitVectorFrom(entity.Owner, fallingUid), baseThrowSpeed: entity.Comp.ThrowForce);

            return;
        }

        MakeEntityEternallyFall(fallingUid, entity);
    }

    protected Vector2 GetUnitVectorFrom(EntityUid from, EntityUid to)
    {
        var vector = _transformSystem.GetWorldPosition(from) - _transformSystem.GetWorldPosition(to);

        Vector2Helpers.Normalize(ref vector);
        return vector;
    }

    private void MakeEntityEternallyFall(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity)
    {
        var fallingComponent = EntityManager.ComponentFactory.GetComponent<ChasmFallingComponent>();
        fallingComponent.NextDeletionTime = TimeSpan.MaxValue;
        fallingComponent.DeletionTime = TimeSpan.MaxValue;
        fallingComponent.AnimationTime = FallTime;

        AddComp(uid, fallingComponent);
        _actionBlockerSystem.UpdateCanMove(uid);

        if (_netManager.IsServer) // i really hate prediction
            ritualChasmEntity.Comp.FallQueue.Enqueue((uid, GameTiming.CurTime + FallTime));

        HandleReturnedEntity(uid, ritualChasmEntity);

        PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero);
        _transformSystem.SetCoordinates(uid, Transform(ritualChasmEntity.Owner).Coordinates);

        _audioSystem.PlayPredicted(ritualChasmEntity.Comp.FallSound, ritualChasmEntity.Owner, uid);

        _pointLightSystem.RemoveLightDeferred(uid);
        _stunSystem.TryKnockdown(uid, FallTime);
        if (ritualChasmEntity.Comp.FallEmote is { } fallEmote &&
            _mobStateSystem.IsAlive(uid))
            _chatSystem.TryEmoteWithChat(uid, fallEmote);
    }

    protected abstract void PunishEntity(EntityUid uid);

    protected abstract void HandleReturnedEntity(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity);

    protected virtual void DoLocalAnnouncement(EntityUid uid, string message) { }
}
