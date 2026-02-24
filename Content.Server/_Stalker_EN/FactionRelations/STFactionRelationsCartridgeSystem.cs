using Content.Server.CartridgeLoader;
using Content.Server.Chat.Systems;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Content.Shared._Stalker_EN.CCVar;
using Content.Shared._Stalker_EN.FactionRelations;
using Content.Shared.CartridgeLoader;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Stalker_EN.FactionRelations;

/// <summary>
/// Server system for the faction relations PDA cartridge program.
/// Loads relation overrides from the database and merges them with YAML defaults.
/// </summary>
public sealed class STFactionRelationsCartridgeSystem : EntitySystem
{
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoaderSystem = default!;
    [Dependency] private readonly ChatSystem _chatSystem = default!;
    [Dependency] private readonly DiscordWebhook _discord = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IServerDbManager _dbManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private static readonly Color AllianceColor = Color.FromHex("#2d7019");
    private static readonly Color NeutralColor = Color.FromHex("#b8a900");
    private static readonly Color HostileColor = Color.FromHex("#c87000");
    private static readonly Color WarColor = Color.FromHex("#a01000");

    /// <summary>
    /// In-memory cache of DB overrides. Key: normalized (factionA, factionB) pair.
    /// </summary>
    private readonly Dictionary<(string, string), STFactionRelationType> _dbOverrides = new();

    /// <summary>
    /// Cached lookup from the defaults prototype for O(1) relation queries.
    /// Rebuilt on initialization and prototype reload.
    /// </summary>
    private Dictionary<(string, string), STFactionRelationType> _defaultsCache = new();

    /// <summary>
    /// Cached ordered list of faction IDs from the defaults prototype.
    /// </summary>
    private List<string>? _cachedFactionIds;

    private bool _cacheReady;
    private WebhookIdentifier? _webhookIdentifier;

    private const int BroadcastVariants = 4;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STFactionRelationsCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        RebuildDefaultsCache();
        LoadFromDatabaseAsync();

        _config.OnValueChanged(STCCVars.FactionRelationsWebhook, value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
                _discord.GetWebhook(value, data => _webhookIdentifier = data.ToIdentifier());
            else
                _webhookIdentifier = null;
        }, true);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<STFactionRelationDefaultsPrototype>())
            RebuildDefaultsCache();
    }

    /// <summary>
    /// Rebuilds the cached defaults dictionary from the YAML prototype.
    /// </summary>
    private void RebuildDefaultsCache()
    {
        _defaultsCache = new Dictionary<(string, string), STFactionRelationType>();
        _cachedFactionIds = null;

        if (!_protoManager.TryIndex<STFactionRelationDefaultsPrototype>("Default", out var proto))
            return;

        _cachedFactionIds = proto.Factions;

        foreach (var rel in proto.Relations)
        {
            var key = STFactionRelationHelpers.NormalizePair(rel.FactionA, rel.FactionB);
            _defaultsCache[key] = rel.Relation;
        }
    }

    private async void LoadFromDatabaseAsync()
    {
        try
        {
            var relations = await _dbManager.GetAllStalkerFactionRelationsAsync();
            foreach (var rel in relations)
            {
                var key = STFactionRelationHelpers.NormalizePair(rel.FactionA, rel.FactionB);
                _dbOverrides[key] = (STFactionRelationType) rel.RelationType;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load faction relations from database: {e}");
        }
        finally
        {
            _cacheReady = true;
        }
    }

    private void OnUiReady(EntityUid uid, STFactionRelationsCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        var state = BuildUiState();
        _cartridgeLoaderSystem.UpdateCartridgeUiState(args.Loader, state);
    }

    /// <summary>
    /// Sets a faction relation, updating both in-memory cache and database.
    /// Broadcasts the change in-game and to Discord if the relation actually changed.
    /// </summary>
    public void SetRelation(string factionA, string factionB, STFactionRelationType type)
    {
        if (factionA == factionB)
            return;

        var oldRelation = GetRelation(factionA, factionB);

        var key = STFactionRelationHelpers.NormalizePair(factionA, factionB);
        _dbOverrides[key] = type;
        SaveRelationAsync(key.Item1, key.Item2, (int) type);

        if (oldRelation != type)
        {
            BroadcastRelationChange(factionA, factionB, type);
            SendDiscordRelationChange(factionA, factionB, oldRelation, type);
        }

        BroadcastUiUpdate();
    }

    /// <summary>
    /// Clears all DB overrides and reverts to YAML defaults.
    /// </summary>
    public void ResetAllRelations()
    {
        _dbOverrides.Clear();
        ClearRelationsAsync();
        BroadcastUiUpdate();
    }

    /// <summary>
    /// Gets the current relation between two factions.
    /// </summary>
    public STFactionRelationType GetRelation(string factionA, string factionB)
    {
        var key = STFactionRelationHelpers.NormalizePair(factionA, factionB);

        if (_dbOverrides.TryGetValue(key, out var overrideType))
            return overrideType;

        if (_defaultsCache.TryGetValue(key, out var defaultType))
            return defaultType;

        return STFactionRelationType.Neutral;
    }

    /// <summary>
    /// Gets the cached ordered list of faction display IDs from the defaults prototype.
    /// </summary>
    public List<string>? GetFactionIds()
    {
        return _cachedFactionIds;
    }

    private async void SaveRelationAsync(string factionA, string factionB, int relationType)
    {
        try
        {
            await _dbManager.SetStalkerFactionRelationAsync(factionA, factionB, relationType);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save faction relation to database: {e}");
        }
    }

    private async void ClearRelationsAsync()
    {
        try
        {
            await _dbManager.ClearAllStalkerFactionRelationsAsync();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to clear faction relations from database: {e}");
        }
    }

    /// <summary>
    /// Resolves a band prototype name (e.g. "Dolg") to a faction relation name (e.g. "Duty")
    /// using the defaults prototype's BandMapping.
    /// </summary>
    public string? GetBandFactionName(string bandName)
    {
        if (!_protoManager.TryIndex<STFactionRelationDefaultsPrototype>("Default", out var proto))
            return null;

        return proto.BandMapping.GetValueOrDefault(bandName);
    }

    /// <summary>
    /// Pushes the current faction relations state to loaders that have a faction relations cartridge installed.
    /// </summary>
    private void BroadcastUiUpdate()
    {
        var state = BuildUiState();
        var query = AllEntityQuery<STFactionRelationsCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out _, out _, out var cartComp))
        {
            if (cartComp.LoaderUid is not { } loaderUid)
                continue;

            if (!TryComp<CartridgeLoaderComponent>(loaderUid, out var loaderComp))
                continue;

            _cartridgeLoaderSystem.UpdateCartridgeUiState(loaderUid, state, loader: loaderComp);
        }
    }

    public STFactionRelationsUiState BuildUiState()
    {
        if (!_protoManager.TryIndex<STFactionRelationDefaultsPrototype>("Default", out var defaults))
            return new STFactionRelationsUiState(new List<string>(), new List<STFactionRelationEntry>());

        var factions = defaults.Factions;
        var entries = new List<STFactionRelationEntry>();

        // Build the full relation list: defaults merged with DB overrides
        for (var i = 0; i < factions.Count; i++)
        {
            for (var j = i + 1; j < factions.Count; j++)
            {
                var key = STFactionRelationHelpers.NormalizePair(factions[i], factions[j]);
                STFactionRelationType relation;

                if (_dbOverrides.TryGetValue(key, out var overrideType))
                    relation = overrideType;
                else
                    _defaultsCache.TryGetValue(key, out relation);

                // Only send non-neutral entries to save bandwidth
                if (relation != STFactionRelationType.Neutral)
                    entries.Add(new STFactionRelationEntry(key.Item1, key.Item2, relation));
            }
        }

        return new STFactionRelationsUiState(factions, entries);
    }

    /// <summary>
    /// Broadcasts a faction relation change to all in-game players via chat.
    /// Picks a random lore-friendly message variant.
    /// </summary>
    private void BroadcastRelationChange(string factionA, string factionB, STFactionRelationType newRelation)
    {
        var relationKey = newRelation.ToString().ToLowerInvariant();
        var variant = _random.Next(1, BroadcastVariants + 1);
        var message = Loc.GetString($"st-faction-relations-{relationKey}-{variant}",
            ("factionA", factionA),
            ("factionB", factionB));

        var color = GetRelationColor(newRelation);
        var filter = Filter.Empty().AddWhere(_gameTicker.UserHasJoinedGame);

        _chatSystem.DispatchFilteredAnnouncement(
            filter,
            message,
            sender: "S.T.A.L.K.E.R Network",
            playSound: false,
            colorOverride: color);
    }

    /// <summary>
    /// Sends a faction relation change notification to the configured Discord webhook.
    /// </summary>
    private async void SendDiscordRelationChange(
        string factionA,
        string factionB,
        STFactionRelationType oldRelation,
        STFactionRelationType newRelation)
    {
        if (_webhookIdentifier is null)
            return;

        try
        {
            var payload = new WebhookPayload
            {
                Embeds = new List<WebhookEmbed>
                {
                    new()
                    {
                        Title = "Faction Relations Changed",
                        Description = $"**{factionA}** ↔ **{factionB}**",
                        Color = GetRelationDiscordColor(newRelation),
                        Fields = new List<WebhookEmbedField>
                        {
                            new()
                            {
                                Name = "Old Relation",
                                Value = oldRelation.ToString(),
                                Inline = true,
                            },
                            new()
                            {
                                Name = "New Relation",
                                Value = newRelation.ToString(),
                                Inline = true,
                            },
                        },
                    },
                },
            };

            await _discord.CreateMessage(_webhookIdentifier.Value, payload);
        }
        catch (Exception e)
        {
            Log.Error($"Error sending faction relation change to Discord:\n{e}");
        }
    }

    private static Color GetRelationColor(STFactionRelationType type)
    {
        return type switch
        {
            STFactionRelationType.Alliance => AllianceColor,
            STFactionRelationType.Neutral => NeutralColor,
            STFactionRelationType.Hostile => HostileColor,
            STFactionRelationType.War => WarColor,
            _ => Color.White,
        };
    }

    private static int GetRelationDiscordColor(STFactionRelationType type)
    {
        return type switch
        {
            STFactionRelationType.Alliance => 0x2d7019,
            STFactionRelationType.Neutral => 0xb8a900,
            STFactionRelationType.Hostile => 0xc87000,
            STFactionRelationType.War => 0xa01000,
            _ => 0xFFFFFF,
        };
    }
}
