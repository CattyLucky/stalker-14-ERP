using Robust.Shared.Configuration;

namespace Content.Shared._Stalker_EN.CCVar;

public sealed partial class STCCVars
{
    /// <summary>
    ///     Discord webhook URL for messenger channel message notifications.
    ///     DMs are never sent to Discord (privacy).
    /// </summary>
    public static readonly CVarDef<string> MessengerDiscordWebhook =
        CVarDef.Create("stalkeren.messenger.discord_webhook", string.Empty,
            CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>
    ///     Maximum message content length in characters.
    /// </summary>
    public static readonly CVarDef<int> MessengerMaxMessageLength =
        CVarDef.Create("stalkeren.messenger.max_message_length", 500, CVar.SERVER | CVar.REPLICATED);
}
