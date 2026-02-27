using Content.Server.Database;
using Content.Shared._Stalker_EN.PDA;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.PDA;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.PDA;

/// <summary>
/// Server system for PDA password lock functionality.
/// Intercepts PDA UI open attempts and shows a password prompt for non-owners.
/// Persists passwords to the database so they survive entity deletion (e.g. personal stash).
/// </summary>
public sealed class STPdaPasswordSystem : SharedSTPdaPasswordSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    /// <summary>Maximum password length to prevent abuse.</summary>
    private const int MaxPasswordLength = 32;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STPdaPasswordComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
        SubscribeLocalEvent<STPdaPasswordComponent, BoundUIOpenedEvent>(OnBuiOpened);
        SubscribeLocalEvent<STPdaPasswordComponent, STPdaPasswordSubmitMessage>(OnPasswordSubmit);
        SubscribeLocalEvent<STPdaPasswordComponent, STPdaPasswordSetMessage>(OnPasswordSet);
        SubscribeLocalEvent<STPdaPasswordComponent, STPdaPasswordOpenSettingsMessage>(OnOpenSettings);
        SubscribeLocalEvent<STPdaPasswordComponent, GotEquippedEvent>(OnPdaPasswordEquipped);
    }

    /// <summary>
    /// When a PDA with a password component is equipped in the ID slot,
    /// attempt to reload the password from DB (handles stash store/retrieve).
    /// </summary>
    private void OnPdaPasswordEquipped(Entity<STPdaPasswordComponent> ent, ref GotEquippedEvent args)
    {
        if (!args.SlotFlags.HasFlag(SlotFlags.IDCARD))
            return;

        // Already has a password loaded — don't overwrite
        if (ent.Comp.Password is not null)
            return;

        var charName = MetaData(args.Equipee).EntityName;
        LoadPasswordAsync(ent.Owner, ent.Comp, charName);
    }

    /// <summary>
    /// Asynchronously loads a password from the database and applies it to the component.
    /// </summary>
    private async void LoadPasswordAsync(EntityUid uid, STPdaPasswordComponent comp, string charName)
    {
        try
        {
            var record = await _db.GetStalkerPdaPasswordAsync(charName);

            if (Deleted(uid) || !TryComp<STPdaPasswordComponent>(uid, out var currentComp))
                return;

            // Component may have been replaced or password set manually while we awaited
            if (currentComp.Password is not null)
                return;

            if (record is null)
                return;

            currentComp.Password = record.Password;
            currentComp.IsLocked = true;
            Dirty(uid, currentComp);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load PDA password for {charName}: {ex}");
        }
    }

    private void OnOpenAttempt(Entity<STPdaPasswordComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || !ent.Comp.IsLocked)
            return;

        if (TryComp<PdaComponent>(ent, out var pda) && pda.PdaOwner == args.User)
            return;

        if (TryComp<ActorComponent>(args.User, out var actor) &&
            ent.Comp.UnlockedBy.Contains(actor.PlayerSession.UserId))
            return;

        args.Cancel();

        if (args.Silent)
            return;

        if (actor != null)
        {
            _ui.SetUiState(ent.Owner, STPdaPasswordUiKey.Key,
                new STPdaPasswordUiState(false, false, true));
            _ui.TryOpenUi(ent.Owner, STPdaPasswordUiKey.Key, args.User);
        }
    }

    private void OnBuiOpened(Entity<STPdaPasswordComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (args.UiKey is not STPdaPasswordUiKey)
            return;

        var isOwner = TryComp<PdaComponent>(ent, out var pda) && pda.PdaOwner == args.Actor;
        _ui.SetUiState(ent.Owner, STPdaPasswordUiKey.Key,
            new STPdaPasswordUiState(false, isOwner, ent.Comp.IsLocked));
    }

    private void OnPasswordSubmit(Entity<STPdaPasswordComponent> ent, ref STPdaPasswordSubmitMessage args)
    {
        if (!ent.Comp.IsLocked || ent.Comp.Password == null)
            return;

        // Rate-limit password attempts to prevent brute-forcing
        if (ent.Comp.NextAttemptTime > _timing.CurTime)
            return;

        ent.Comp.NextAttemptTime = _timing.CurTime + ent.Comp.AttemptCooldown;

        var actor = args.Actor;
        if (!TryComp<ActorComponent>(actor, out var actorComp))
            return;

        if (args.Password == ent.Comp.Password)
        {
            ent.Comp.UnlockedBy.Add(actorComp.PlayerSession.UserId);

            _ui.CloseUi(ent.Owner, STPdaPasswordUiKey.Key, actor);
            _ui.TryOpenUi(ent.Owner, PdaUiKey.Key, actor);
        }
        else
        {
            _ui.SetUiState(ent.Owner, STPdaPasswordUiKey.Key,
                new STPdaPasswordUiState(true, false, true));
        }
    }

    private void OnPasswordSet(Entity<STPdaPasswordComponent> ent, ref STPdaPasswordSetMessage args)
    {
        var actor = args.Actor;

        if (!TryComp<PdaComponent>(ent, out var pda) || pda.PdaOwner != actor)
        {
            _popup.PopupEntity(Loc.GetString("st-pda-password-not-owner"), ent, actor);
            return;
        }

        // Resolve character name for DB persistence
        var charName = MetaData(actor).EntityName;

        if (string.IsNullOrWhiteSpace(args.NewPassword))
        {
            ent.Comp.Password = null;
            ent.Comp.IsLocked = false;
            ent.Comp.UnlockedBy.Clear();
            Dirty(ent);
            _popup.PopupEntity(Loc.GetString("st-pda-password-removed"), ent, actor);

            RemovePasswordAsync(charName);
        }
        else
        {
            var password = args.NewPassword.Trim();
            if (password.Length > MaxPasswordLength)
                password = password[..MaxPasswordLength];

            ent.Comp.Password = password;
            ent.Comp.IsLocked = true;
            ent.Comp.UnlockedBy.Clear();
            Dirty(ent);
            _popup.PopupEntity(Loc.GetString("st-pda-password-set"), ent, actor);

            SavePasswordAsync(charName, password);
        }

        _ui.SetUiState(ent.Owner, STPdaPasswordUiKey.Key,
            new STPdaPasswordUiState(false, true, ent.Comp.IsLocked));
    }

    private void OnOpenSettings(Entity<STPdaPasswordComponent> ent, ref STPdaPasswordOpenSettingsMessage args)
    {
        var actor = args.Actor;

        if (!TryComp<PdaComponent>(ent, out var pda) || pda.PdaOwner != actor)
            return;

        _ui.SetUiState(ent.Owner, STPdaPasswordUiKey.Key,
            new STPdaPasswordUiState(false, true, ent.Comp.IsLocked));
        _ui.TryOpenUi(ent.Owner, STPdaPasswordUiKey.Key, actor);
    }

    /// <summary>
    /// Persists a password to the database for the given character.
    /// </summary>
    private async void SavePasswordAsync(string charName, string password)
    {
        try
        {
            await _db.SetStalkerPdaPasswordAsync(charName, password);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save PDA password for {charName}: {ex}");
        }
    }

    /// <summary>
    /// Removes a password from the database for the given character.
    /// </summary>
    private async void RemovePasswordAsync(string charName)
    {
        try
        {
            await _db.RemoveStalkerPdaPasswordAsync(charName);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to remove PDA password for {charName}: {ex}");
        }
    }
}
