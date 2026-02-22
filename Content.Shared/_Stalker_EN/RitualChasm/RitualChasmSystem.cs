using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Events;
using Content.Shared.StepTrigger.Systems;
using Content.Shared.Weapons.Misc;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Content.Shared._Stalker.ZoneArtifact.Components;
using Robust.Shared.Player;
using Content.Shared._Stalker.Teeth;

namespace Content.Shared.RitualChasm;

/// <summary>
///     Handles making entities fall into chasms when stepped on.
/// </summary>
public sealed class RitualChasmSystem : EntitySystem
{

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedGrapplingGunSystem _grapple = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RitualChasmComponent, StepTriggeredOffEvent>(OnStepTriggered);
        SubscribeLocalEvent<RitualChasmComponent, StepTriggerAttemptEvent>(OnStepTriggerAttempt);
        SubscribeLocalEvent<RitualChasmFallingComponent, UpdateCanMoveEvent>(OnUpdateCanMove);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // predict queuedels on client ✅

        var query = EntityQueryEnumerator<RitualChasmFallingComponent>();
        while (query.MoveNext(out var uid, out var chasm))
        {
            if (_timing.CurTime < chasm.NextDeletionTime)
                continue;

            PredictedQueueDel(uid);
        }
    }

    private void OnStepTriggered(EntityUid uid, RitualChasmComponent component, ref StepTriggeredOffEvent args)
    {
        // already doomed
        if (HasComp<RitualChasmFallingComponent>(args.Tripper))
            return;

        if (HasComp<ZoneArtifactComponent>(args.Tripper) || HasComp<TeethPullComponent>(args.Tripper))
        {
            GiveReward(uid, component, args.Tripper);
        }

        StartFalling(uid, component, args.Tripper);
    }

    public void GiveReward(EntityUid chasm, RitualChasmComponent component, EntityUid tripper, bool playSound = true)
    {


    }
    public void StartFalling(EntityUid chasm, RitualChasmComponent component, EntityUid tripper, bool playSound = true)
    {
        var falling = AddComp<RitualChasmFallingComponent>(tripper);


        falling.NextDeletionTime = _timing.CurTime + falling.DeletionTime;
        _blocker.UpdateCanMove(tripper);

        if (playSound)
            _audio.PlayPredicted(component.FallingSound, chasm, tripper);
    }

    private void OnStepTriggerAttempt(EntityUid uid, RitualChasmComponent component, ref StepTriggerAttemptEvent args)
    {
        if (_grapple.IsEntityHooked(args.Tripper))
        {
            args.Cancelled = true;
            return;
        }

        args.Continue = true;
    }

    private void OnUpdateCanMove(EntityUid uid, RitualChasmFallingComponent component, UpdateCanMoveEvent args)
    {
        args.Cancel();
    }
}
