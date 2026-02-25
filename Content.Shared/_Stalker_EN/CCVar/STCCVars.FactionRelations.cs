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

    /// <summary>
    ///     Per-pair cooldown in seconds between faction relation changes. Default 300 (5 minutes).
    /// </summary>
    public static readonly CVarDef<int> FactionRelationsCooldownSeconds =
        CVarDef.Create("stalkeren.faction_relations.cooldown_seconds", 300, CVar.SERVERONLY);

    /// <summary>
    ///     Maximum character length for custom proposal/announcement messages.
    /// </summary>
    public static readonly CVarDef<int> FactionRelationsCustomMessageMaxLength =
        CVarDef.Create("stalkeren.faction_relations.custom_message_max_length", 250,
            CVar.SERVERONLY | CVar.REPLICATED);

    /// <summary>
    ///     Prototype ID for the fee configuration used for faction relation changes.
    /// </summary>
    public static readonly CVarDef<string> FactionRelationsFeePrototype =
        CVarDef.Create("stalkeren.faction_relations.fee_prototype", "Default", CVar.SERVERONLY);

    /// <summary>
    ///     Fraction of the fee refunded when a proposal is rejected by the target (0.0 to 1.0).
    /// </summary>
    public static readonly CVarDef<float> FactionRelationsRefundOnRejection =
        CVarDef.Create("stalkeren.faction_relations.refund_on_rejection", 0.75f, CVar.SERVERONLY);

    /// <summary>
    ///     Fraction of the fee refunded when a proposal is cancelled by the proposer (0.0 to 1.0).
    /// </summary>
    public static readonly CVarDef<float> FactionRelationsRefundOnCancellation =
        CVarDef.Create("stalkeren.faction_relations.refund_on_cancellation", 0.50f, CVar.SERVERONLY);

    /// <summary>
    ///     Fraction of the fee refunded when a proposal expires or is superseded (0.0 to 1.0).
    /// </summary>
    public static readonly CVarDef<float> FactionRelationsRefundOnExpiration =
        CVarDef.Create("stalkeren.faction_relations.refund_on_expiration", 0.75f, CVar.SERVERONLY);

    /// <summary>
    ///     Time in seconds before a pending proposal expires. 0 disables time-based expiration.
    ///     All proposals also expire on round restart regardless of this value.
    /// </summary>
    public static readonly CVarDef<int> FactionRelationsProposalExpirationSeconds =
        CVarDef.Create("stalkeren.faction_relations.proposal_expiration_seconds", 2700, CVar.SERVERONLY);
}
