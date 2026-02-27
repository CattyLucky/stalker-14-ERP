using Robust.Shared.GameStates;
using Robust.Shared.Network;

namespace Content.Shared._Stalker_EN.PDA;

/// <summary>
/// Allows a PDA to be password-protected. The PDA owner auto-bypasses the lock.
/// Other characters must enter the correct password to access the PDA UI.
/// Once unlocked, the PDA stays unlocked for that session (tracked by <see cref="NetUserId"/>).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
[Access(typeof(SharedSTPdaPasswordSystem))]
public sealed partial class STPdaPasswordComponent : Component
{
    /// <summary>Whether a password has been set on this PDA.</summary>
    [DataField, AutoNetworkedField]
    public bool IsLocked;

    /// <summary>
    /// The password string (plain text — in-game security only).
    /// Server-only; not auto-networked to prevent client-side password leaking.
    /// </summary>
    [ViewVariables]
    public string? Password;

    /// <summary>
    /// Sessions that have successfully unlocked this PDA during this round.
    /// Uses <see cref="NetUserId"/> to avoid EntityUid recycling issues.
    /// </summary>
    [ViewVariables]
    public HashSet<NetUserId> UnlockedBy = new();

    /// <summary>
    /// Next allowed password attempt time (absolute simulation time).
    /// Prevents brute-force password guessing.
    /// </summary>
    [AutoPausedField, ViewVariables]
    public TimeSpan NextAttemptTime;

    /// <summary>Cooldown between password attempts.</summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan AttemptCooldown = TimeSpan.FromSeconds(2);
}
