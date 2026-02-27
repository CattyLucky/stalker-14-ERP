using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_EN.PDA;

/// <summary>UI key for the PDA password prompt interface.</summary>
[Serializable, NetSerializable]
public enum STPdaPasswordUiKey
{
    Key,
}

/// <summary>Client sends this to attempt unlocking a password-protected PDA.</summary>
[Serializable, NetSerializable]
public sealed class STPdaPasswordSubmitMessage : BoundUserInterfaceMessage
{
    public readonly string Password;

    public STPdaPasswordSubmitMessage(string password)
    {
        Password = password;
    }
}

/// <summary>Client sends this to set or change the PDA password (owner only).</summary>
[Serializable, NetSerializable]
public sealed class STPdaPasswordSetMessage : BoundUserInterfaceMessage
{
    /// <summary>The new password. Null or empty to remove the password lock.</summary>
    public readonly string? NewPassword;

    public STPdaPasswordSetMessage(string? newPassword)
    {
        NewPassword = newPassword;
    }
}

/// <summary>Owner requests to open password settings from PDA settings page.</summary>
[Serializable, NetSerializable]
public sealed class STPdaPasswordOpenSettingsMessage : BoundUserInterfaceMessage;

/// <summary>Server sends this to the password prompt UI to indicate state.</summary>
[Serializable, NetSerializable]
public sealed class STPdaPasswordUiState : BoundUserInterfaceState
{
    /// <summary>Whether the last password attempt was incorrect.</summary>
    public readonly bool WrongPassword;

    /// <summary>Whether this is the owner viewing password settings (can change/remove password).</summary>
    public readonly bool IsOwner;

    /// <summary>Whether the PDA currently has a password set.</summary>
    public readonly bool HasPassword;

    public STPdaPasswordUiState(bool wrongPassword, bool isOwner, bool hasPassword)
    {
        WrongPassword = wrongPassword;
        IsOwner = isOwner;
        HasPassword = hasPassword;
    }
}
