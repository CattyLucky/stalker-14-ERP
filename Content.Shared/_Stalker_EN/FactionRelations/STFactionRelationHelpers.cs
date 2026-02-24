namespace Content.Shared._Stalker_EN.FactionRelations;

/// <summary>
/// Shared helpers for faction relation pair normalization.
/// </summary>
public static class STFactionRelationHelpers
{
    /// <summary>
    /// Normalizes a faction pair so that FactionA is always alphabetically first.
    /// </summary>
    public static (string, string) NormalizePair(string a, string b)
    {
        return string.Compare(a, b, StringComparison.Ordinal) <= 0 ? (a, b) : (b, a);
    }
}
