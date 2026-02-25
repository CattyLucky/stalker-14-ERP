using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._Stalker_EN.FactionRelations;

/// <summary>
/// Prototype defining the default faction relations matrix.
/// Provides the list of factions and their default pairwise relations.
/// DB overrides take precedence over these defaults.
/// </summary>
[Prototype("stFactionRelationDefaults")]
public sealed class STFactionRelationDefaultsPrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    public string ID { get; } = string.Empty;

    /// <summary>
    /// Ordered list of faction display IDs for the matrix.
    /// </summary>
    [DataField(required: true)]
    public List<string> Factions { get; } = new();

    /// <summary>
    /// Default pairwise relations. Omitted pairs default to Neutral.
    /// </summary>
    [DataField(required: true)]
    public List<STFactionRelationDefault> Relations { get; } = new();

    /// <summary>
    /// Maps STBandPrototype names to faction relation names.
    /// E.g. "Dolg" -> "Duty", "Stalker" -> "Loners".
    /// Used to resolve a player's band into their faction relation identity.
    /// </summary>
    [DataField]
    public Dictionary<string, string> BandMapping { get; } = new();
}

/// <summary>
/// A single default faction relation entry defined in YAML.
/// </summary>
[DataDefinition]
public sealed partial class STFactionRelationDefault
{
    [DataField(required: true)]
    public string FactionA { get; set; } = string.Empty;

    [DataField(required: true)]
    public string FactionB { get; set; } = string.Empty;

    [DataField(required: true)]
    public STFactionRelationType Relation { get; set; }
}
