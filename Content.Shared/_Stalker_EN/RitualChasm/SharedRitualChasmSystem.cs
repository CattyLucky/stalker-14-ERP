using System.Numerics;
using Content.Shared.ActionBlocker;
using Content.Shared.Chasm;
using Content.Shared.Flash;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
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
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedStunSystem _stunSystem = default!;
    [Dependency] private readonly SharedFlashSystem _flashSystem = default!;
    [Dependency] private readonly EntityWhitelistSystem _entityWhitelistSystem = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlockerSystem = default!;
    [Dependency] private readonly ThrowingSystem _throwingSystem = default!;
    [Dependency] private readonly PullingSystem _pullingSystem = default!;

    protected static readonly TimeSpan FallTime = TimeSpan.FromSeconds(3.5d);

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
                var (netUid, _) = ritualChasmComponent.FallQueue.Dequeue();
                var uid = GetEntity(netUid);

                if (!_entityWhitelistSystem.IsWhitelistPass(ritualChasmComponent.RelocatableEntities, uid))
                {
                    PredictedQueueDel(uid);
                    continue;
                }

                _audioSystem.PlayGlobal(ritualChasmComponent.RelocateSound, Filter.Broadcast(), true);
                if (_exitPoints.Count == 0)
                {
                    PredictedQueueDel(uid);
                    Log.Error($"Entity {ToPrettyString(uid)} being sacrificed to ritual chasm was deleted, as no exit points existed. MAP THEM!!!");
                    continue;
                }

                RemComp<ChasmFallingComponent>(uid);
                _actionBlockerSystem.UpdateCanMove(uid);

                StopPulling(uid);
                _transformSystem.SetCoordinates(uid, Transform(RobustRandom.Pick(_exitPoints)).Coordinates);

                // play only for the relocated
                _audioSystem.PlayGlobal(ritualChasmComponent.RelocatedLocalSound, uid);
                _popupSystem.PopupClient(ritualChasmComponent.RelocatedLocalPopup, uid, uid, PopupType.LargeCaution);

                _stunSystem.AddKnockdownTime(uid, ritualChasmComponent.RelocatedStunDuration);
                _flashSystem.Flash(uid, null, null, ritualChasmComponent.RelocatedStunDuration, 0f, displayPopup: false);
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
        Dirty(entity);
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

        ritualChasmEntity.Comp.FallQueue.Enqueue((GetNetEntity(uid), GameTiming.CurTime + FallTime));
        HandleReturnedEntity(uid, ritualChasmEntity);

        PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero);
        _transformSystem.SetCoordinates(uid, Transform(ritualChasmEntity.Owner).Coordinates);

        _audioSystem.PlayPvs(ritualChasmEntity.Comp.FallSound, ritualChasmEntity.Owner);
    }

    protected abstract void PunishEntity(EntityUid uid);

    protected abstract void HandleReturnedEntity(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity);
}
