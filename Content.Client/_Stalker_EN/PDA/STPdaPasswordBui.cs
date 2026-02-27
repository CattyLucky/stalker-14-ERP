using Content.Shared._Stalker_EN.PDA;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client._Stalker_EN.PDA;

/// <summary>
/// Client BUI for the PDA password lock prompt.
/// Shows either a password entry screen or owner settings depending on state.
/// </summary>
public sealed class STPdaPasswordBui : BoundUserInterface
{
    [ViewVariables]
    private STPdaPasswordWindow? _window;

    public STPdaPasswordBui(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<STPdaPasswordWindow>();

        _window.OnPasswordSubmit += password =>
        {
            SendMessage(new STPdaPasswordSubmitMessage(password));
        };

        _window.OnPasswordSet += password =>
        {
            SendMessage(new STPdaPasswordSetMessage(password));
        };
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not STPdaPasswordUiState cast || _window == null)
            return;

        _window.UpdateState(cast);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _window?.Close();
    }
}
