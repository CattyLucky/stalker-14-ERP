using Content.Server.Administration;
using Content.Shared._Stalker_EN.FactionRelations;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace Content.Server._Stalker_EN.FactionRelations.Commands;

/// <summary>
/// Admin command to view and modify faction relations.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class STFactionRelationCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public string Command => "st_factionrelation";

    public string Description => "Admin command to view and modify faction relations.";

    public string Help => "Usage:\n" +
                          "st_factionrelation set <factionA> <factionB> <alliance|neutral|hostile|war>\n" +
                          "st_factionrelation get <factionA> <factionB>\n" +
                          "st_factionrelation reset";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteLine(Help);
            return;
        }

        var system = _entityManager.System<STFactionRelationsCartridgeSystem>();

        switch (args[0])
        {
            case "set":
                HandleSet(shell, args, system);
                break;
            case "get":
                HandleGet(shell, args, system);
                break;
            case "reset":
                HandleReset(shell, system);
                break;
            default:
                shell.WriteLine(Help);
                break;
        }
    }

    private void HandleSet(IConsoleShell shell, string[] args, STFactionRelationsCartridgeSystem system)
    {
        if (args.Length != 4)
        {
            shell.WriteLine("Usage: st_factionrelation set <factionA> <factionB> <alliance|neutral|hostile|war>");
            return;
        }

        var factionA = args[1];
        var factionB = args[2];
        var relationStr = args[3].ToLowerInvariant();

        var factions = system.GetFactionIds();
        if (factions == null)
        {
            shell.WriteError("Failed to load faction defaults prototype.");
            return;
        }

        if (!factions.Contains(factionA))
        {
            shell.WriteError($"Unknown faction: '{factionA}'. Valid factions: {string.Join(", ", factions)}");
            return;
        }

        if (!factions.Contains(factionB))
        {
            shell.WriteError($"Unknown faction: '{factionB}'. Valid factions: {string.Join(", ", factions)}");
            return;
        }

        if (factionA == factionB)
        {
            shell.WriteError("Cannot set relation between a faction and itself.");
            return;
        }

        if (!TryParseRelation(relationStr, out var relation))
        {
            shell.WriteError($"Unknown relation type: '{relationStr}'. Valid types: alliance, neutral, hostile, war");
            return;
        }

        system.SetRelation(factionA, factionB, relation);
        shell.WriteLine($"Set relation {factionA} <-> {factionB} to {relation}");
    }

    private void HandleGet(IConsoleShell shell, string[] args, STFactionRelationsCartridgeSystem system)
    {
        if (args.Length != 3)
        {
            shell.WriteLine("Usage: st_factionrelation get <factionA> <factionB>");
            return;
        }

        var factionA = args[1];
        var factionB = args[2];

        var relation = system.GetRelation(factionA, factionB);
        shell.WriteLine($"{factionA} <-> {factionB}: {relation}");
    }

    private static void HandleReset(IConsoleShell shell, STFactionRelationsCartridgeSystem system)
    {
        system.ResetAllRelations();
        shell.WriteLine("All faction relation overrides cleared. Reverted to YAML defaults.");
    }

    private static bool TryParseRelation(string str, out STFactionRelationType relation)
    {
        relation = str switch
        {
            "alliance" => STFactionRelationType.Alliance,
            "neutral" => STFactionRelationType.Neutral,
            "hostile" => STFactionRelationType.Hostile,
            "war" => STFactionRelationType.War,
            _ => STFactionRelationType.Neutral,
        };

        return str is "alliance" or "neutral" or "hostile" or "war";
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromOptions(new[] { "set", "get", "reset" });
        }

        if (args[0] is "set" or "get")
        {
            if (args.Length is 2 or 3)
            {
                var system = _entityManager.System<STFactionRelationsCartridgeSystem>();
                var factions = system.GetFactionIds();
                if (factions != null)
                    return CompletionResult.FromOptions(factions);
            }

            if (args[0] == "set" && args.Length == 4)
            {
                return CompletionResult.FromOptions(new[] { "alliance", "neutral", "hostile", "war" });
            }
        }

        return CompletionResult.Empty;
    }
}
