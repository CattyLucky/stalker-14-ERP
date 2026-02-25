using Content.Server._Stalker_EN.Currency;
using Content.Server.CartridgeLoader;
using Content.Server.Chat.Systems;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Content.Shared._Stalker_EN.CCVar;
using Content.Shared._Stalker_EN.FactionRelations;
using Content.Shared.CartridgeLoader;
using Content.Shared.GameTicking;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.FactionRelations;

/// <summary>
/// Server system for the faction relations PDA cartridge program.
/// Manages relation overrides, bilateral proposals, fees, cooldowns, and announcements.
/// </summary>
public sealed class STFactionRelationsCartridgeSystem : EntitySystem
{
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoaderSystem = default!;
    [Dependency] private readonly ChatSystem _chatSystem = default!;
    [Dependency] private readonly STCurrencySystem _currency = default!;
    [Dependency] private readonly DiscordWebhook _discord = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IServerDbManager _dbManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private static readonly ProtoId<STFactionRelationDefaultsPrototype> DefaultsProtoId = "Default";

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

    /// <summary>
    /// Cached display names from the defaults prototype (e.g. "ClearSky" → "Clear Sky").
    /// </summary>
    private Dictionary<string, string>? _cachedDisplayNames;

    /// <summary>
    /// In-memory cache of pending bilateral proposals. Key: (initiatingFaction, targetFaction).
    /// </summary>
    private readonly Dictionary<(string, string), STFactionRelationProposalData> _pendingProposals = new();

    /// <summary>
    /// Per-pair cooldown tracker. Key: normalized pair. Value: absolute expiry time.
    /// Not persisted across restarts.
    /// </summary>
    private readonly Dictionary<(string, string), TimeSpan> _pairCooldowns = new();

    /// <summary>
    /// Cached fee lookup. Key: (fromRelation, toRelation). Value: cost in roubles.
    /// </summary>
    private Dictionary<(STFactionRelationType, STFactionRelationType), int> _feeCache = new();

    /// <summary>
    /// Maps alias factions to their primary. E.g. "Rookies" -> "Loners".
    /// Alias factions share the primary's relations and are hidden from UIs.
    /// </summary>
    private Dictionary<string, string> _aliasToPrimary = new();

    /// <summary>
    /// Maps primary factions to their alias list. E.g. "Loners" -> ["Rookies", "Neutrals"].
    /// </summary>
    private Dictionary<string, List<string>> _primaryToAliases = new();

    /// <summary>
    /// Factions that cannot be targeted by player-initiated relation changes.
    /// </summary>
    private HashSet<string> _cachedRestrictedFactions = new();

    /// <summary>
    /// Tracks loaders (PDAs) that currently have the faction relations cartridge active.
    /// Only these receive broadcast UI updates.
    /// </summary>
    private readonly HashSet<EntityUid> _activeLoaders = new();

    private bool _cacheReady;
    private WebhookIdentifier? _webhookIdentifier;

    private const int BroadcastVariants = 4;

    /// <summary>
    /// Next time to check for expired proposals. Avoids per-tick iteration.
    /// </summary>
    private TimeSpan _nextExpirationCheck;

    private static readonly TimeSpan ExpirationCheckInterval = TimeSpan.FromSeconds(30);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STFactionRelationsCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<STFactionRelationsCartridgeComponent, CartridgeActivatedEvent>(OnCartridgeActivated);
        SubscribeLocalEvent<STFactionRelationsCartridgeComponent, CartridgeDeactivatedEvent>(OnCartridgeDeactivated);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);

        RebuildDefaultsCache();
        RebuildFeeCache();
        LoadFromDatabaseAsync();
        LoadProposalsFromDatabaseAsync();

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

        if (args.WasModified<STFactionRelationFeePrototype>())
            RebuildFeeCache();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_cacheReady)
            return;

        if (_timing.CurTime < _nextExpirationCheck)
            return;

        _nextExpirationCheck = _timing.CurTime + ExpirationCheckInterval;
        CheckExpiredProposals();
    }

    /// <summary>
    /// Checks all pending proposals for time-based expiration and processes them.
    /// </summary>
    private void CheckExpiredProposals()
    {
        var expirationSeconds = _config.GetCVar(STCCVars.FactionRelationsProposalExpirationSeconds);
        if (expirationSeconds <= 0)
            return;

        var expirationSpan = TimeSpan.FromSeconds(expirationSeconds);
        var now = DateTime.UtcNow;
        var expired = new List<(string, string)>();

        foreach (var (key, proposal) in _pendingProposals)
        {
            if (now - proposal.CreatedAt > expirationSpan)
                expired.Add(key);
        }

        if (expired.Count == 0)
            return;

        var refundRate = _config.GetCVar(STCCVars.FactionRelationsRefundOnExpiration);

        foreach (var key in expired)
        {
            if (!_pendingProposals.TryGetValue(key, out var proposal))
                continue;

            DepositRefund(proposal.InitiatingFaction, proposal.FeePaid, refundRate,
                $"Proposal to {proposal.TargetFaction} expired");

            _pendingProposals.Remove(key);
            DeleteProposalAsync(key.Item1, key.Item2);

            BroadcastRelationAnnouncement(
                proposal.InitiatingFaction,
                proposal.TargetFaction,
                proposal.ProposedRelation,
                STFactionRelationAnnouncementKind.ProposalExpired,
                proposal.CustomMessage);
        }

        BroadcastUiUpdate();
    }

    /// <summary>
    /// Expires all pending proposals on round restart with partial refunds.
    /// </summary>
    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        if (_pendingProposals.Count == 0)
            return;

        var refundRate = _config.GetCVar(STCCVars.FactionRelationsRefundOnExpiration);

        foreach (var (_, proposal) in _pendingProposals)
        {
            DepositRefund(proposal.InitiatingFaction, proposal.FeePaid, refundRate,
                $"Proposal to {proposal.TargetFaction} expired (round ended)");
        }

        _pendingProposals.Clear();
        ClearProposalsAsync();
    }

    #region Cache Building

    /// <summary>
    /// Rebuilds the cached defaults dictionary from the YAML prototype.
    /// Also builds alias maps and restricted faction set.
    /// </summary>
    private void RebuildDefaultsCache()
    {
        _defaultsCache = new Dictionary<(string, string), STFactionRelationType>();
        _aliasToPrimary = new Dictionary<string, string>();
        _primaryToAliases = new Dictionary<string, List<string>>();
        _cachedRestrictedFactions = new HashSet<string>();
        _cachedFactionIds = null;

        if (!_protoManager.TryIndex(DefaultsProtoId, out var proto))
            return;

        // Build alias maps from faction groups
        foreach (var (primary, aliases) in proto.FactionGroups)
        {
            _primaryToAliases[primary] = aliases;
            foreach (var alias in aliases)
            {
                _aliasToPrimary[alias] = primary;
            }
        }

        // Build restricted faction set
        _cachedRestrictedFactions = new HashSet<string>(proto.RestrictedFactions);

        // Keep full faction list — filtering for specific UIs is done at the caller level
        _cachedFactionIds = proto.Factions;
        _cachedDisplayNames = proto.DisplayNames;

        foreach (var rel in proto.Relations)
        {
            var key = STFactionRelationHelpers.NormalizePair(rel.FactionA, rel.FactionB);
            _defaultsCache[key] = rel.Relation;
        }
    }

    /// <summary>
    /// Rebuilds the fee lookup from the fee prototype.
    /// </summary>
    private void RebuildFeeCache()
    {
        _feeCache = new Dictionary<(STFactionRelationType, STFactionRelationType), int>();

        var protoId = _config.GetCVar(STCCVars.FactionRelationsFeePrototype);
        if (!_protoManager.TryIndex<STFactionRelationFeePrototype>(protoId, out var proto))
            return;

        foreach (var fee in proto.Fees)
        {
            _feeCache[(fee.From, fee.To)] = fee.Cost;
        }
    }

    #endregion

    #region Database Loading

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

    private async void LoadProposalsFromDatabaseAsync()
    {
        try
        {
            var proposals = await _dbManager.GetAllStalkerFactionRelationProposalsAsync();
            foreach (var p in proposals)
            {
                var key = (p.InitiatingFaction, p.TargetFaction);
                _pendingProposals[key] = new STFactionRelationProposalData(
                    p.InitiatingFaction,
                    p.TargetFaction,
                    (STFactionRelationType) p.ProposedRelationType,
                    p.CustomMessage,
                    p.CreatedAt,
                    p.FeePaid);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load faction relation proposals from database: {e}");
        }
    }

    #endregion

    #region UI

    private void OnUiReady(EntityUid uid, STFactionRelationsCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        var state = BuildUiState();
        _cartridgeLoaderSystem.UpdateCartridgeUiState(args.Loader, state);
    }

    private void OnCartridgeActivated(EntityUid uid, STFactionRelationsCartridgeComponent component, CartridgeActivatedEvent args)
    {
        _activeLoaders.Add(args.Loader);
    }

    private void OnCartridgeDeactivated(EntityUid uid, STFactionRelationsCartridgeComponent component, CartridgeDeactivatedEvent args)
    {
        _activeLoaders.Remove(args.Loader);
    }

    /// <summary>
    /// Pushes the current faction relations state to loaders that have the faction relations cartridge active.
    /// Only iterates loaders tracked via activation events rather than querying all entities.
    /// </summary>
    private void BroadcastUiUpdate()
    {
        if (_activeLoaders.Count == 0)
            return;

        var state = BuildUiState();
        foreach (var loaderUid in _activeLoaders)
        {
            if (!TryComp<CartridgeLoaderComponent>(loaderUid, out var loaderComp))
                continue;

            _cartridgeLoaderSystem.UpdateCartridgeUiState(loaderUid, state, loader: loaderComp);
        }
    }

    public STFactionRelationsUiState BuildUiState()
    {
        if (!_protoManager.TryIndex(DefaultsProtoId, out var defaults))
            return new STFactionRelationsUiState(new List<string>(), new List<STFactionRelationEntry>());

        var factions = defaults.Factions;
        var entries = new List<STFactionRelationEntry>();

        // Build the full relation list using GetRelation() for proper alias resolution
        for (var i = 0; i < factions.Count; i++)
        {
            for (var j = i + 1; j < factions.Count; j++)
            {
                var relation = GetRelation(factions[i], factions[j]);

                // Only send non-neutral entries to save bandwidth
                if (relation != STFactionRelationType.Neutral)
                {
                    var key = STFactionRelationHelpers.NormalizePair(factions[i], factions[j]);
                    entries.Add(new STFactionRelationEntry(key.Item1, key.Item2, relation));
                }
            }
        }

        return new STFactionRelationsUiState(factions, entries);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Gets the current relation between two factions. Resolves aliases to their primaries.
    /// </summary>
    public STFactionRelationType GetRelation(string factionA, string factionB)
    {
        var resolvedA = ResolvePrimary(factionA);
        var resolvedB = ResolvePrimary(factionB);
        var key = STFactionRelationHelpers.NormalizePair(resolvedA, resolvedB);

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

    /// <summary>
    /// Gets the cached display names dictionary from the defaults prototype.
    /// </summary>
    public Dictionary<string, string>? GetDisplayNames()
    {
        return _cachedDisplayNames;
    }

    /// <summary>
    /// Resolves a band prototype name (e.g. "Dolg") to a faction relation name (e.g. "Duty").
    /// </summary>
    public string? GetBandFactionName(string bandName)
    {
        if (!_protoManager.TryIndex(DefaultsProtoId, out var proto))
            return null;

        return proto.BandMapping.GetValueOrDefault(bandName);
    }

    /// <summary>
    /// Gets the fee for a given relation transition.
    /// </summary>
    public int GetFee(STFactionRelationType from, STFactionRelationType to)
    {
        return _feeCache.GetValueOrDefault((from, to), 0);
    }

    /// <summary>
    /// Gets the fee entries list for sending to clients.
    /// </summary>
    public List<STFactionRelationFeeEntry> GetFeeEntries()
    {
        var protoId = _config.GetCVar(STCCVars.FactionRelationsFeePrototype);
        if (!_protoManager.TryIndex<STFactionRelationFeePrototype>(protoId, out var proto))
            return new List<STFactionRelationFeeEntry>();

        return proto.Fees;
    }

    /// <summary>
    /// Gets pending proposals involving a faction (both incoming and outgoing).
    /// </summary>
    public (List<STFactionRelationProposalData> Incoming, List<STFactionRelationProposalData> Outgoing) GetProposalsForFaction(string faction)
    {
        var incoming = new List<STFactionRelationProposalData>();
        var outgoing = new List<STFactionRelationProposalData>();

        foreach (var proposal in _pendingProposals.Values)
        {
            if (proposal.TargetFaction == faction)
                incoming.Add(proposal);
            else if (proposal.InitiatingFaction == faction)
                outgoing.Add(proposal);
        }

        return (incoming, outgoing);
    }

    /// <summary>
    /// Gets the remaining cooldown in seconds for a faction pair. Returns 0 if not on cooldown.
    /// Resolves aliases to their primaries.
    /// </summary>
    public float GetCooldownRemaining(string factionA, string factionB)
    {
        var resolvedA = ResolvePrimary(factionA);
        var resolvedB = ResolvePrimary(factionB);
        var key = STFactionRelationHelpers.NormalizePair(resolvedA, resolvedB);

        if (_pairCooldowns.TryGetValue(key, out var expiry) && expiry > _timing.CurTime)
            return (float) (expiry - _timing.CurTime).TotalSeconds;

        return 0f;
    }

    /// <summary>
    /// Resolves a faction alias to its primary. Returns the input if not an alias.
    /// </summary>
    public string ResolvePrimary(string faction)
    {
        return _aliasToPrimary.GetValueOrDefault(faction, faction);
    }

    /// <summary>
    /// Returns true if the faction is an alias of another faction.
    /// </summary>
    public bool IsAlias(string faction)
    {
        return _aliasToPrimary.ContainsKey(faction);
    }

    /// <summary>
    /// Returns true if the resolved faction is restricted (admin-only changes).
    /// </summary>
    public bool IsFactionRestricted(string faction)
    {
        var resolved = ResolvePrimary(faction);
        return _cachedRestrictedFactions.Contains(resolved);
    }

    #endregion

    #region Relation Changes

    /// <summary>
    /// Attempts a faction relation change. Handles fee deduction, cooldown checking,
    /// and determines whether the change is instant (escalation) or creates a proposal (cooperation).
    /// Only the initiator pays the fee.
    /// </summary>
    public STFactionRelationChangeResult TryChangeRelation(
        EntityUid initiatorUid,
        string initiatingFaction,
        string targetFaction,
        STFactionRelationType proposedRelation,
        string? customMessage)
    {
        // Resolve aliases to primaries
        initiatingFaction = ResolvePrimary(initiatingFaction);
        targetFaction = ResolvePrimary(targetFaction);

        // Validate factions
        var factionIds = GetFactionIds();
        if (factionIds == null || !factionIds.Contains(initiatingFaction) || !factionIds.Contains(targetFaction))
            return STFactionRelationChangeResult.InvalidFaction;

        if (initiatingFaction == targetFaction)
            return STFactionRelationChangeResult.InvalidFaction;

        // Reject player changes targeting restricted factions
        if (IsFactionRestricted(targetFaction))
            return STFactionRelationChangeResult.RestrictedFaction;

        var currentRelation = GetRelation(initiatingFaction, targetFaction);
        if (currentRelation == proposedRelation)
            return STFactionRelationChangeResult.SameRelation;

        // Check cooldown
        var key = STFactionRelationHelpers.NormalizePair(initiatingFaction, targetFaction);
        if (_pairCooldowns.TryGetValue(key, out var expiry) && expiry > _timing.CurTime)
            return STFactionRelationChangeResult.OnCooldown;

        // Look up fee and deduct from initiator
        var fee = GetFee(currentRelation, proposedRelation);
        if (fee > 0)
        {
            var balance = _currency.CountRoubles(initiatorUid);
            if (balance < fee)
                return STFactionRelationChangeResult.InsufficientFunds;

            if (!_currency.TryDeductRoubles(initiatorUid, fee))
                return STFactionRelationChangeResult.InsufficientFunds;
        }

        // Trim custom message
        var maxLen = _config.GetCVar(STCCVars.FactionRelationsCustomMessageMaxLength);
        if (customMessage != null && customMessage.Length > maxLen)
            customMessage = customMessage[..maxLen];

        // Set cooldown
        var cooldownSeconds = _config.GetCVar(STCCVars.FactionRelationsCooldownSeconds);
        _pairCooldowns[key] = _timing.CurTime + TimeSpan.FromSeconds(cooldownSeconds);

        // Determine if this requires bilateral confirmation
        if (STFactionRelationHelpers.RequiresConfirmation(currentRelation, proposedRelation))
        {
            // Create proposal
            var proposalKey = (initiatingFaction, targetFaction);
            var data = new STFactionRelationProposalData(
                initiatingFaction,
                targetFaction,
                proposedRelation,
                customMessage,
                DateTime.UtcNow,
                fee);

            _pendingProposals[proposalKey] = data;
            SaveProposalAsync(initiatingFaction, targetFaction, (int) proposedRelation, customMessage, fee);

            BroadcastRelationAnnouncement(
                initiatingFaction,
                targetFaction,
                proposedRelation,
                STFactionRelationAnnouncementKind.ProposalSent,
                customMessage);

            BroadcastUiUpdate();
            return STFactionRelationChangeResult.ProposalCreated;
        }

        // Unilateral escalation -- apply immediately
        SetRelation(initiatingFaction, targetFaction, proposedRelation,
            broadcast: true,
            customMessage: customMessage,
            kind: STFactionRelationAnnouncementKind.DirectChange);

        return STFactionRelationChangeResult.Success;
    }

    /// <summary>
    /// Accepts a pending proposal targeting the accepting faction.
    /// The acceptor pays nothing -- only the initiator paid when proposing.
    /// </summary>
    public STFactionRelationChangeResult AcceptProposal(
        EntityUid acceptorUid,
        string acceptingFaction,
        string initiatingFaction)
    {
        var proposalKey = (initiatingFaction, acceptingFaction);
        if (!_pendingProposals.TryGetValue(proposalKey, out var proposal))
            return STFactionRelationChangeResult.ProposalNotFound;

        // Remove proposal
        _pendingProposals.Remove(proposalKey);
        DeleteProposalAsync(initiatingFaction, acceptingFaction);

        // Apply the relation change
        SetRelation(initiatingFaction, acceptingFaction, proposal.ProposedRelation,
            broadcast: true,
            customMessage: proposal.CustomMessage,
            kind: STFactionRelationAnnouncementKind.ProposalAccepted);

        return STFactionRelationChangeResult.ProposalAccepted;
    }

    /// <summary>
    /// Rejects a pending proposal targeting the rejecting faction.
    /// A partial refund is deposited to the initiator's claimable funds.
    /// </summary>
    public STFactionRelationChangeResult RejectProposal(
        string rejectingFaction,
        string initiatingFaction)
    {
        var proposalKey = (initiatingFaction, rejectingFaction);
        if (!_pendingProposals.TryGetValue(proposalKey, out var proposal))
            return STFactionRelationChangeResult.ProposalNotFound;

        // Deposit partial refund for the initiator
        var refundRate = _config.GetCVar(STCCVars.FactionRelationsRefundOnRejection);
        DepositRefund(proposal.InitiatingFaction, proposal.FeePaid, refundRate,
            $"Proposal to {rejectingFaction} rejected");

        // Remove proposal
        _pendingProposals.Remove(proposalKey);
        DeleteProposalAsync(initiatingFaction, rejectingFaction);

        BroadcastRelationAnnouncement(
            initiatingFaction,
            rejectingFaction,
            proposal.ProposedRelation,
            STFactionRelationAnnouncementKind.ProposalRejected,
            proposal.CustomMessage);

        BroadcastUiUpdate();
        return STFactionRelationChangeResult.ProposalRejected;
    }

    /// <summary>
    /// Cancels an outgoing proposal. A partial refund is deposited to the initiator's claimable funds.
    /// </summary>
    public void CancelProposal(string initiatingFaction, string targetFaction)
    {
        var proposalKey = (initiatingFaction, targetFaction);
        if (!_pendingProposals.TryGetValue(proposalKey, out var proposal))
            return;

        // Deposit partial refund for the initiator (lower rate for voluntary cancellation)
        var refundRate = _config.GetCVar(STCCVars.FactionRelationsRefundOnCancellation);
        DepositRefund(proposal.InitiatingFaction, proposal.FeePaid, refundRate,
            $"Proposal to {targetFaction} cancelled");

        _pendingProposals.Remove(proposalKey);
        DeleteProposalAsync(initiatingFaction, targetFaction);
        BroadcastUiUpdate();
    }

    /// <summary>
    /// Sets a faction relation directly. Used by admin commands and internally after confirmation.
    /// </summary>
    public void SetRelation(
        string factionA,
        string factionB,
        STFactionRelationType type,
        bool broadcast = true,
        string? customMessage = null,
        STFactionRelationAnnouncementKind kind = STFactionRelationAnnouncementKind.DirectChange)
    {
        // Resolve aliases so DB stores under the primary faction
        factionA = ResolvePrimary(factionA);
        factionB = ResolvePrimary(factionB);

        if (factionA == factionB)
            return;

        var oldRelation = GetRelation(factionA, factionB);

        var key = STFactionRelationHelpers.NormalizePair(factionA, factionB);
        _dbOverrides[key] = type;
        SaveRelationAsync(key.Item1, key.Item2, (int) type);

        if (oldRelation != type)
        {
            if (broadcast)
                BroadcastRelationAnnouncement(factionA, factionB, type, kind, customMessage);

            SendDiscordRelationChange(factionA, factionB, oldRelation, type);
        }

        // Remove any pending proposals between these factions since the relation just changed
        RemoveProposalsBetween(factionA, factionB);

        BroadcastUiUpdate();
    }

    /// <summary>
    /// Clears all DB overrides and reverts to YAML defaults.
    /// Also clears all pending proposals.
    /// </summary>
    public void ResetAllRelations()
    {
        _dbOverrides.Clear();
        _pendingProposals.Clear();
        _pairCooldowns.Clear();
        ClearRelationsAsync();
        ClearProposalsAsync();
        BroadcastUiUpdate();
    }

    #endregion

    #region Database Persistence

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

    private async void SaveProposalAsync(string initiatingFaction, string targetFaction, int proposedRelationType, string? customMessage, int feePaid)
    {
        try
        {
            await _dbManager.SetStalkerFactionRelationProposalAsync(initiatingFaction, targetFaction, proposedRelationType, customMessage, feePaid);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save faction relation proposal to database: {e}");
        }
    }

    /// <summary>
    /// Deposits a partial refund into the claimable funds pool for a faction.
    /// The faction leader can claim these funds at the Igor NPC.
    /// </summary>
    private void DepositRefund(string faction, int feePaid, float refundRate, string reason)
    {
        if (feePaid <= 0 || refundRate <= 0f)
            return;

        var refundAmount = (int) Math.Round(feePaid * Math.Clamp(refundRate, 0f, 1f));
        if (refundAmount <= 0)
            return;

        DepositClaimableFundsAsync(faction, refundAmount, reason);
        Log.Info($"Faction relations: deposited {refundAmount} RU refund for {faction} ({reason}).");
    }

    private async void DepositClaimableFundsAsync(string faction, int amount, string reason)
    {
        try
        {
            await _dbManager.AddStalkerFactionClaimableFundsAsync(faction, amount, reason);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to deposit claimable funds for {faction}: {e}");
        }
    }

    private async void DeleteProposalAsync(string initiatingFaction, string targetFaction)
    {
        try
        {
            await _dbManager.DeleteStalkerFactionRelationProposalAsync(initiatingFaction, targetFaction);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to delete faction relation proposal from database: {e}");
        }
    }

    private async void ClearProposalsAsync()
    {
        try
        {
            await _dbManager.ClearAllStalkerFactionRelationProposalsAsync();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to clear faction relation proposals from database: {e}");
        }
    }

    /// <summary>
    /// Removes any pending proposals between two factions (in either direction).
    /// Deposits partial refunds since the proposals became moot due to a direct relation change.
    /// </summary>
    private void RemoveProposalsBetween(string factionA, string factionB)
    {
        var keyAB = (factionA, factionB);
        var keyBA = (factionB, factionA);
        var refundRate = _config.GetCVar(STCCVars.FactionRelationsRefundOnExpiration);

        if (_pendingProposals.TryGetValue(keyAB, out var proposalAB))
        {
            DepositRefund(proposalAB.InitiatingFaction, proposalAB.FeePaid, refundRate,
                $"Proposal to {factionB} superseded by direct relation change");
            _pendingProposals.Remove(keyAB);
            DeleteProposalAsync(factionA, factionB);
        }

        if (_pendingProposals.TryGetValue(keyBA, out var proposalBA))
        {
            DepositRefund(proposalBA.InitiatingFaction, proposalBA.FeePaid, refundRate,
                $"Proposal to {factionA} superseded by direct relation change");
            _pendingProposals.Remove(keyBA);
            DeleteProposalAsync(factionB, factionA);
        }
    }

    #endregion

    #region Announcements

    /// <summary>
    /// Broadcasts a faction relation announcement to all in-game players.
    /// Picks a random lore-friendly message variant based on the announcement kind.
    /// Appends custom message when provided.
    /// </summary>
    private void BroadcastRelationAnnouncement(
        string factionA,
        string factionB,
        STFactionRelationType relation,
        STFactionRelationAnnouncementKind kind,
        string? customMessage = null)
    {
        var relationKey = GetRelationKey(relation);
        var kindPrefix = kind switch
        {
            STFactionRelationAnnouncementKind.DirectChange => "",
            STFactionRelationAnnouncementKind.ProposalSent => "proposal-",
            STFactionRelationAnnouncementKind.ProposalAccepted => "accepted-",
            STFactionRelationAnnouncementKind.ProposalRejected => "rejected-",
            STFactionRelationAnnouncementKind.ProposalExpired => "expired-",
            _ => "",
        };

        var variant = _random.Next(1, BroadcastVariants + 1);
        var displayA = STFactionRelationHelpers.GetDisplayName(factionA, _cachedDisplayNames);
        var displayB = STFactionRelationHelpers.GetDisplayName(factionB, _cachedDisplayNames);
        var message = Loc.GetString($"st-faction-relations-{kindPrefix}{relationKey}-{variant}",
            ("factionA", displayA),
            ("factionB", displayB));

        // Append custom message if provided
        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            var maxLen = _config.GetCVar(STCCVars.FactionRelationsCustomMessageMaxLength);
            var trimmed = customMessage.Length > maxLen ? customMessage[..maxLen] : customMessage;
            var suffix = Loc.GetString("st-faction-relations-custom-message", ("message", trimmed));
            message = $"{message} {suffix}";
        }

        var color = GetRelationColor(relation);
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
                        Description = $"**{STFactionRelationHelpers.GetDisplayName(factionA, _cachedDisplayNames)}** ↔ **{STFactionRelationHelpers.GetDisplayName(factionB, _cachedDisplayNames)}**",
                        Color = GetRelationDiscordColor(newRelation),
                        Fields = new List<WebhookEmbedField>
                        {
                            new()
                            {
                                Name = "Old Relation",
                                Value = GetRelationDisplayString(oldRelation),
                                Inline = true,
                            },
                            new()
                            {
                                Name = "New Relation",
                                Value = GetRelationDisplayString(newRelation),
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

    #endregion

    #region Helpers

    private static Color GetRelationColor(STFactionRelationType type)
    {
        return STFactionRelationColors.GetColor(type) ?? Color.White;
    }

    /// <summary>
    /// Maps a relation type to its lowercase locale key fragment.
    /// </summary>
    private static string GetRelationKey(STFactionRelationType type) => type switch
    {
        STFactionRelationType.Alliance => "alliance",
        STFactionRelationType.Neutral => "neutral",
        STFactionRelationType.Hostile => "hostile",
        STFactionRelationType.War => "war",
        _ => "neutral",
    };

    /// <summary>
    /// Maps a relation type to a display string for external integrations (e.g. Discord).
    /// </summary>
    private static string GetRelationDisplayString(STFactionRelationType type) => type switch
    {
        STFactionRelationType.Alliance => "Alliance",
        STFactionRelationType.Neutral => "Neutral",
        STFactionRelationType.Hostile => "Hostile",
        STFactionRelationType.War => "War",
        _ => "Neutral",
    };

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

    #endregion
}

/// <summary>
/// Internal data record for a pending faction relation proposal.
/// </summary>
public sealed record STFactionRelationProposalData(
    string InitiatingFaction,
    string TargetFaction,
    STFactionRelationType ProposedRelation,
    string? CustomMessage,
    DateTime CreatedAt,
    int FeePaid);
