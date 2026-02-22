using System.Numerics;
using Content.Shared.ActionBlocker;
using Content.Shared.Chasm;
using Content.Shared.Throwing;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared._Stalker_EN.RitualChasm;

public abstract class SharedRitualChasmSystem : EntitySystem
{
    [Dependency] protected readonly IGameTiming GameTiming = default!;
    [Dependency] private readonly IRobustRandom _robustRandom = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly EntityWhitelistSystem _entityWhitelistSystem = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlockerSystem = default!;
    [Dependency] private readonly ThrowingSystem _throwingSystem = default!;

    protected static readonly TimeSpan FallTime = TimeSpan.FromSeconds(0.5d);
    private EntityQuery<CreatedByRitualChasmComponent> _createdByRitualChasmQuery;

    private HashSet<EntityUid> _exitPoints = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RitualChasmExitPointComponent, ComponentStartup>(OnExitStartup);
        SubscribeLocalEvent<RitualChasmExitPointComponent, ComponentShutdown>(OnExitShutdown);

        SubscribeLocalEvent<RitualChasmComponent, ComponentShutdown>(OnChasmShutdown);
        SubscribeLocalEvent<RitualChasmComponent, StartCollideEvent>(OnCollide);

        _createdByRitualChasmQuery = GetEntityQuery<CreatedByRitualChasmComponent>();
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

            if (ritualChasmComponent.ThrowBackStack.Count == 0 &&
                ritualChasmComponent.ThrowBackStack.Peek().Item3 >= GameTiming.CurTime)
            {
                var (maybeUid, direction, _) = ritualChasmComponent.ThrowBackStack.Pop();
                if (maybeUid is not { } uid)
                    continue;

                ritualChasmComponent.EntitiesPendingThrowBack.Remove(uid);
                _transformSystem.SetCoordinates(uid, Transform(ritualChasmUid).Coordinates);
                _throwingSystem.TryThrow(uid, direction, baseThrowSpeed: ritualChasmComponent.ThrowForce);
                _audioSystem.PlayPvs(ritualChasmComponent.ThrowSound, uid);
            }

            if (ritualChasmComponent.FallStack.Count == 0 &&
                ritualChasmComponent.FallStack.Peek().Item2 >= GameTiming.CurTime)
            {
                var (uid, _) = ritualChasmComponent.FallStack.Pop();

                if (!_entityWhitelistSystem.IsWhitelistPass(ritualChasmComponent.RelocatableEntities, uid))
                {
                    PredictedQueueDel(uid);
                    continue;
                }

                _audioSystem.PlayGlobal(ritualChasmComponent.RelocateSound, uid);
                if (_exitPoints.Count == 0)
                {
                    PredictedQueueDel(uid);
                    Log.Error($"Entity {ToPrettyString(uid)} being sacrificed to ritual chasm was deleted, as no exit points existed");
                    continue;
                }

                _transformSystem.SetCoordinates(uid, Transform(_robustRandom.Pick(_exitPoints)).Coordinates);
            }
        }
    }

    private void OnCollide(Entity<RitualChasmComponent> entity, ref StartCollideEvent args)
    {
        // already doomed
        if (HasComp<ChasmFallingComponent>(args.OtherEntity))
            return;

        OnHit(entity, args.OtherEntity);
    }

    private void OnHit(Entity<RitualChasmComponent> entity, EntityUid fallingUid)
    {
        if (_createdByRitualChasmQuery.HasComponent(entity) ||
            (MetaData(fallingUid).EntityPrototype is { } fallingEntProto && fallingEntProto == entity.Comp.RewardedEntityProtoId))
        {
            // throw back at equal speed but not too slow
            if (TryComp<PhysicsComponent>(fallingUid, out var physicsComponent))
                _throwingSystem.TryThrow(entity, -GetUnitVectorFrom(entity.Owner, fallingUid), baseThrowSpeed: Math.Max(physicsComponent.LinearVelocity.Length(), 8f));

            return;
        }

        if (_entityWhitelistSystem.IsWhitelistPass(entity.Comp.PunishedEntities, fallingUid))
        {
            PunishEntity(fallingUid);
            _throwingSystem.TryThrow(entity, -GetUnitVectorFrom(entity.Owner, fallingUid), baseThrowSpeed: entity.Comp.ThrowForce * 2f);

            return;
        }

        MakeEntityEternallyFall(fallingUid, entity);
    }

    protected Vector2 GetUnitVectorFrom(EntityUid from, EntityUid to)
        => Vector2.Normalize(_transformSystem.GetWorldPosition(from) - _transformSystem.GetWorldPosition(to));

    private void MakeEntityEternallyFall(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity)
    {
        var fallingComponent = EntityManager.ComponentFactory.GetComponent<ChasmFallingComponent>();
        fallingComponent.NextDeletionTime = TimeSpan.MaxValue;
        fallingComponent.DeletionTime = TimeSpan.MaxValue;
        fallingComponent.AnimationTime = FallTime;

        AddComp(uid, fallingComponent);
        _actionBlockerSystem.UpdateCanMove(uid);

        ritualChasmEntity.Comp.FallStack.Push((uid, GameTiming.CurTime + FallTime));
        HandleReturnedEntity(uid, ritualChasmEntity);
    }

    protected abstract void PunishEntity(EntityUid uid);

    protected abstract void HandleReturnedEntity(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity);
}
