using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._Stalker_EN.FactionRelations;

/// <summary>
/// Prototype defining fees for faction relation changes.
/// Each entry maps a (from, to) relation transition to a rouble cost.
/// </summary>
[Prototype("stFactionRelationFees")]
public sealed class STFactionRelationFeePrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    public string ID { get; } = string.Empty;

    /// <summary>
    /// Fee entries mapping transition types to costs in roubles.
    /// </summary>
    [DataField(required: true)]
    public List<STFactionRelationFeeEntry> Fees { get; } = new();
}

/// <summary>
/// A single fee entry for a faction relation transition.
/// </summary>
[DataDefinition, Serializable, NetSerializable]
public sealed partial class STFactionRelationFeeEntry
{
    /// <summary>
    /// The current relation type before the change.
    /// </summary>
    [DataField(required: true)]
    public STFactionRelationType From { get; set; }

    /// <summary>
    /// The desired relation type after the change.
    /// </summary>
    [DataField(required: true)]
    public STFactionRelationType To { get; set; }

    /// <summary>
    /// Cost in roubles for the initiating faction's leader.
    /// </summary>
    [DataField(required: true)]
    public int Cost { get; set; }
}
