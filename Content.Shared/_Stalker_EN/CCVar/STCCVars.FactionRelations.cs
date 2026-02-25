using Robust.Shared.Configuration;

namespace Content.Shared._Stalker_EN.CCVar;

// CVars for faction relations notifications

public sealed partial class STCCVars
{
    /// <summary>
    ///     Discord webhook URL for faction relation change notifications.
    /// </summary>
    public static readonly CVarDef<string> FactionRelationsWebhook =
        CVarDef.Create("stalkeren.faction_relations.discord_webhook", string.Empty,
            CVar.SERVERONLY | CVar.CONFIDENTIAL);
}
