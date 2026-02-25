using Robust.Shared.Utility;

namespace Content.Client._Stalker_EN.FactionRelations;

/// <summary>
/// Shared RSI paths and state names for faction patch icons used in multiple UIs.
/// </summary>
public static class STFactionPatchIcons
{
    private static readonly ResPath PatchRsiPath = new("/Textures/_Stalker/Icons/Patches/band.rsi");
    private static readonly ResPath PatchRsiPathEN = new("/Textures/_Stalker_EN/Icons/Patches/band.rsi");

    /// <summary>
    /// Maps faction relation names to their patch RSI paths and state names.
    /// </summary>
    public static readonly Dictionary<string, (ResPath Rsi, string State)> PatchStates = new()
    {
        ["Loners"] = (PatchRsiPath, "stalker"),
        ["Freedom"] = (PatchRsiPath, "freedom"),
        ["Bandits"] = (PatchRsiPath, "band"),
        ["Duty"] = (PatchRsiPath, "dolg"),
        ["Ecologist"] = (PatchRsiPath, "ecologist"),
        ["Neutrals"] = (PatchRsiPath, "ne"),
        ["Mercenaries"] = (PatchRsiPath, "merc"),
        ["Military"] = (PatchRsiPath, "army"),
        ["Monolith"] = (PatchRsiPath, "monolith"),
        ["Clear Sky"] = (PatchRsiPath, "cn"),
        ["Renegades"] = (PatchRsiPath, "rene"),
        ["Rookies"] = (PatchRsiPathEN, "rookie"),
    };
}
