using Content.Server.Chat.Systems;
using Content.Server.DoAfter;
using Content.Shared.IdentityManagement;
using Content.Server.Popups;
using Content.Shared.Popups;
using Content.Server.Speech.Components;
using Content.Shared._Stalker.RespawnContainer;
using Content.Shared._Stalker.Teeth;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Robust.Server.Audio;
using Robust.Shared.Timing;
using Content.Shared.Speech.Components;

namespace Content.Server._Stalker.Teeth;

public sealed class TeethPullingSystem : EntitySystem
{
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly PopupSystem _popupSystem = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly SharedRespawnContainerSystem _respawnCont = default!;
    private string _respawnRecord = "teethCount";
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TeethPullComponent, InteractUsingEvent>(OnInteracted);
        SubscribeLocalEvent<TeethPullComponent, TeethPulledEvent>(OnPulled);
        SubscribeLocalEvent<TeethPullComponent, RespawnGotTransferredEvent>(OnTransfer);
    }

    private void OnTransfer(Entity<TeethPullComponent> entity, ref RespawnGotTransferredEvent args)
    {
        if (entity.Comp.ReviveTime == null || entity.Comp.ReviveTime > _timing.CurTime || entity.Comp.TeethCount > 0)
            return;

        // fill up teeth on respawn transfer
        _respawnCont.TrySetData(entity, _respawnRecord, entity.Comp.InitialTeeth);
    }
    private void OnInteracted(Entity<TeethPullComponent> entity, ref InteractUsingEvent args)
    {
        // Check for active do afters
        if (HasComp<ActiveDoAfterComponent>(args.User))
            return;

        // retrieve data from RespawnContainerComp
        var teethCount = _respawnCont.EnsureData(entity, _respawnRecord, entity.Comp.TeethCount);

        if (!_tags.HasTag(args.Used, entity.Comp.PullingItemTag) || teethCount <= 0)
            return;

        // We don't want to pull our teeth, lol
        if (args.Target == args.User)
            return;

        var doAfterArgs = new DoAfterArgs(EntityManager, args.User, entity.Comp.PullTime, new TeethPulledEvent(),
            args.Target, args.Target, args.Used)
        {
            BreakOnHandChange = true,
            BlockDuplicate = true,
            DuplicateCondition = DuplicateConditions.All,
        };

        _doAfter.TryStartDoAfter(doAfterArgs);
        // Caution Block
        var userName = Identity.Entity(args.User, EntityManager);
        var targetName = Identity.Entity(args.Target, EntityManager);
        _popupSystem.PopupEntity(Loc.GetString("user-teeth-pulling-try", ("target", targetName)), args.User, args.User);
        _popupSystem.PopupEntity(Loc.GetString("teeth-pulling-try", ("user", userName)), args.User, args.Target, PopupType.LargeCaution);
        // Caution Block
    }

    private void OnPulled(EntityUid uid, TeethPullComponent comp, TeethPulledEvent args)
    {
        if (args.Cancelled)
            return;
        Pull((uid, comp));
    }

    private void Scream(Entity<TeethPullComponent> ent)
    {
        if (!TryComp<VocalComponent>(ent, out var comp))
            return;
        _chat.TryEmoteWithChat(ent.Owner, comp.ScreamId);
    }

    private void Pull(Entity<TeethPullComponent> ent)
    {
        var comp = ent.Comp;

        comp.Pulled = true; // for future, probably

        if (!_respawnCont.TryGetData<int>(ent.Owner, _respawnRecord, out var teethCount))
            return;

        _respawnCont.TrySetData(ent.Owner, _respawnRecord, teethCount - 1);
        comp.TeethCount--;

        // Set revive time, to fill up teeth on respawn transfer
        if (comp.TeethCount <= 0)
            comp.ReviveTime = _timing.CurTime + TimeSpan.FromMinutes(30);


        if (comp.PullSound != null)
            _audio.PlayPvs(comp.PullSound, Transform(ent).Coordinates);

        Scream(ent);
        Spawn(comp.TeethProto, Transform(ent).Coordinates);
        EnsureAccent(ent);
    }

    public /* ST14-EN RitualChasm: made this public */ void EnsureAccent(Entity<TeethPullComponent> ent)
    {
        // Get registration of our component and return if that comp is already on our ent
        var compType = _componentFactory.GetRegistration(ent.Comp.AccentComp).Type;
        if (HasComp(ent, compType))
            return;

        // Apply our component
        var accentComponent = (Component)_componentFactory.GetComponent(compType);
        AddComp(ent, accentComponent);
    }
}
