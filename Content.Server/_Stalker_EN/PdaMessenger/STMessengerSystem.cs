using Content.Server.Administration.Logs;
using Content.Server.CartridgeLoader;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.PDA;
using Content.Server.PDA.Ringer;
using Content.Shared._Stalker.Bands;
using Content.Shared._Stalker_EN.CCVar;
using Content.Shared._Stalker_EN.PdaMessenger;
using Content.Shared.CartridgeLoader;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.PDA;
using Content.Shared.PDA.Ringer;
using Content.Shared.StatusIcon;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Stalker_EN.PdaMessenger;

/// <summary>
/// Server system for the stalker messenger PDA cartridge.
/// Handles message routing, contacts, muting, unread tracking, DB persistence,
/// Discord webhook notifications, and round lifecycle cleanup.
/// </summary>
public sealed partial class STMessengerSystem : EntitySystem
{
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private readonly DiscordWebhook _discord = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PdaSystem _pda = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly RingerSystem _ringer = default!;

    private const int MaxChannelMessages = 200;
    private const int MaxDmMessages = 100;
    private const int MaxContacts = 50;
    private const int MaxRetryCollision = 10;
    private const int MaxPseudonymSuffix = 999;
    private static readonly TimeSpan InteractionCooldown = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Maps character name → anonymous pseudonym for the current round.
    /// Cleared on round restart so each round gets fresh pseudonyms.
    /// </summary>
    private readonly Dictionary<string, string> _anonymousPseudonyms = new();

    /// <summary>
    /// Global set of all pseudonyms in use this round to prevent collisions.
    /// </summary>
    private readonly HashSet<string> _usedPseudonyms = new();

    /// <summary>
    /// Lore-friendly callsign pool for anonymous channel messages.
    /// Hardcoded English — these are Zone callsigns, not translatable UI strings (EN-fork only).
    /// </summary>
    private static readonly string[] AnonymousNames =
    {
        "Shadow", "Phantom", "Wanderer", "Ghost", "Nomad",
        "Spectre", "Drifter", "Raven", "Stray", "Whisper",
        "Jackal", "Viper", "Crow", "Haze", "Ember",
        "Frost", "Ash", "Lynx", "Bolt", "Thorn",
    };

    /// <summary>
    /// PDAs with messenger cartridge currently active (UI open). Receive broadcast updates.
    /// </summary>
    private readonly HashSet<EntityUid> _activeLoaders = new();

    /// <summary>
    /// Per-loader currently viewed chat ID for lazy message loading.
    /// Null = main page (no chat open).
    /// </summary>
    private readonly Dictionary<EntityUid, string?> _viewedChat = new();

    /// <summary>
    /// Server-side channel message storage. Key = channel prototype ID.
    /// </summary>
    private readonly Dictionary<string, List<STMessengerMessage>> _channelChats = new();

    /// <summary>
    /// Server-side DM message storage. Key = normalized "charA:charB" (alphabetical).
    /// </summary>
    private readonly Dictionary<string, List<STMessengerMessage>> _dmChats = new();

    /// <summary>
    /// Per-chat auto-increment message ID counter. Key = chat ID.
    /// </summary>
    private readonly Dictionary<string, uint> _nextMessageId = new();

    /// <summary>
    /// Maps messenger ID ("XXX-XXX") -> character name. Bulk-loaded at system init.
    /// </summary>
    private readonly Dictionary<string, string> _messengerIdCache = new();

    /// <summary>
    /// Maps character name -> their PDA's cartridge EntityUid (for O(1) DM recipient lookup).
    /// Updated on <see cref="PlayerSpawnCompleteEvent"/>, cleaned up on entity deletion.
    /// </summary>
    private readonly Dictionary<string, EntityUid> _characterToPda = new();

    /// <summary>
    /// Maps character name -> band status icon RSI state (e.g. "dolg", "stalker").
    /// Populated on player spawn from <see cref="BandsComponent.BandStatusIcon"/>.
    /// Persists across rounds (like <see cref="_characterToMessengerId"/>) so contacts
    /// from previous rounds still display faction patches. Updated when a player spawns again.
    /// </summary>
    private readonly Dictionary<string, string> _characterToBandIcon = new();

    /// <summary>
    /// Reverse lookup: character name -> messenger ID ("XXX-XXX").
    /// Populated alongside <see cref="_messengerIdCache"/> and persists across rounds.
    /// </summary>
    private readonly Dictionary<string, string> _characterToMessengerId = new();

    /// <summary>
    /// Cached set of all PDAs that have a messenger cartridge.
    /// Avoids full entity query in <see cref="NotifyChannelRecipients"/>.
    /// Stores (CartridgeUid, PdaUid) — resolve components via TryComp to avoid stale references.
    /// </summary>
    private readonly Dictionary<EntityUid, (EntityUid Cartridge, EntityUid Pda)>
        _messengerPdas = new();

    /// <summary>
    /// Channel prototypes sorted by <see cref="STMessengerChannelPrototype.SortOrder"/>.
    /// Cached to avoid sorting + prototype lookups on every <see cref="BuildUiState"/> call.
    /// </summary>
    private List<STMessengerChannelPrototype> _sortedChannels = new();

    private WebhookIdentifier? _webhookIdentifier;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STMessengerComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<STMessengerComponent, CartridgeActivatedEvent>(OnCartridgeActivated);
        SubscribeLocalEvent<STMessengerComponent, CartridgeDeactivatedEvent>(OnCartridgeDeactivated);
        SubscribeLocalEvent<STMessengerComponent, CartridgeMessageEvent>(OnMessage);
        SubscribeLocalEvent<STMessengerServerComponent, EntityTerminatingEvent>(OnMessengerTerminating);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PdaComponent, GotEquippedEvent>(OnPdaEquipped);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        CacheSortedChannels();

        foreach (var proto in _sortedChannels)
        {
            _channelChats.TryAdd(proto.ID, new List<STMessengerMessage>());
        }

        LoadMessengerIdCacheAsync();

        _config.OnValueChanged(STCCVars.MessengerDiscordWebhook, OnWebhookChanged, true);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _config.UnsubValueChanged(STCCVars.MessengerDiscordWebhook, OnWebhookChanged);
    }

    private void OnWebhookChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _discord.GetWebhook(value, data => _webhookIdentifier = data.ToIdentifier());
        else
            _webhookIdentifier = null;
    }

    #region Cartridge Events

    private void OnUiReady(Entity<STMessengerComponent> ent, ref CartridgeUiReadyEvent args)
    {
        if (!TryComp<STMessengerServerComponent>(ent, out var server))
            return;

        UpdateUiState(ent, args.Loader, server);
    }

    private void OnCartridgeActivated(Entity<STMessengerComponent> ent, ref CartridgeActivatedEvent args)
    {
        _activeLoaders.Add(args.Loader);
    }

    private void OnCartridgeDeactivated(Entity<STMessengerComponent> ent, ref CartridgeDeactivatedEvent args)
    {
        _activeLoaders.Remove(args.Loader);
        _viewedChat.Remove(args.Loader);
    }

    private void OnMessengerTerminating(Entity<STMessengerServerComponent> ent, ref EntityTerminatingEvent args)
    {
        // Guard against race: only remove if this entity is still the registered PDA for this character
        if (!string.IsNullOrEmpty(ent.Comp.OwnerCharacterName))
        {
            var name = ent.Comp.OwnerCharacterName;

            if (_characterToPda.TryGetValue(name, out var existing) && existing == ent.Owner)
                _characterToPda.Remove(name);

            _characterToBandIcon.Remove(name);
        }

        // The loader is the PDA entity that owns this cartridge
        if (TryComp<TransformComponent>(ent, out var xform) && xform.ParentUid.IsValid())
        {
            _activeLoaders.Remove(xform.ParentUid);
            _viewedChat.Remove(xform.ParentUid);
            _messengerPdas.Remove(xform.ParentUid);
        }
    }

    private void OnMessage(Entity<STMessengerComponent> ent, ref CartridgeMessageEvent args)
    {
        if (!TryComp<STMessengerServerComponent>(ent, out var server))
            return;

        switch (args)
        {
            case STMessengerSendEvent send:
                OnSendMessage(ent, server, send, args);
                break;
            case STMessengerAddContactEvent add:
                OnAddContact(ent, server, add, args);
                break;
            case STMessengerRemoveContactEvent remove:
                OnRemoveContact(ent, server, remove, args);
                break;
            case STMessengerToggleMuteEvent mute:
                OnToggleMute(ent, server, mute, args);
                break;
            case STMessengerMarkReadEvent markRead:
                OnMarkRead(server, markRead);
                break;
            case STMessengerViewChatEvent viewChat:
                OnViewChat(args.LoaderUid, viewChat);
                break;
        }
    }

    #endregion

    #region Message Handling

    private void OnSendMessage(
        Entity<STMessengerComponent> ent,
        STMessengerServerComponent server,
        STMessengerSendEvent send,
        CartridgeMessageEvent args)
    {
        if (server.NextSendTime > _timing.CurTime)
            return;

        server.NextSendTime = _timing.CurTime + server.SendCooldown;

        var content = send.Content.Trim();
        if (string.IsNullOrEmpty(content))
            return;

        var maxLen = _config.GetCVar(STCCVars.MessengerMaxMessageLength);
        if (content.Length > maxLen)
            content = content[..maxLen];

        // Resolve sender name from stored owner name (survives entity deletion)
        var senderName = server.OwnerCharacterName;
        if (string.IsNullOrEmpty(senderName))
            return;

        var loaderUid = GetEntity(args.LoaderUid);
        var chatId = send.TargetChatId;
        var isDm = chatId.StartsWith(STMessengerChat.DmChatPrefix, StringComparison.Ordinal);

        // Determine display name: anonymous pseudonym for channels, real name for DMs
        var displayName = (send.IsAnonymous && !isDm)
            ? GetOrCreatePseudonym(senderName)
            : senderName;

        string? replySnippet = null;
        if (send.ReplyToId is { } replyId)
        {
            replySnippet = FindReplySnippet(chatId, isDm, senderName, replyId);
        }

        List<STMessengerMessage> chatMessages;
        int maxMessages;
        string storageKey;

        if (isDm)
        {
            var contactName = chatId[STMessengerChat.DmChatPrefix.Length..];

            // Only allow DMs to contacts (prevents unbounded DM chat creation)
            if (!server.Contacts.Contains(contactName))
                return;

            storageKey = NormalizeDmKey(senderName, contactName);
            chatMessages = _dmChats.GetOrNew(storageKey);
            maxMessages = MaxDmMessages;
        }
        else
        {
            // Validate that the channel prototype exists to prevent clients from polluting storage
            if (!_protoManager.HasIndex<STMessengerChannelPrototype>(chatId))
                return;

            storageKey = chatId;
            chatMessages = _channelChats.GetOrNew(storageKey);
            maxMessages = MaxChannelMessages;
        }

        _nextMessageId.TryAdd(storageKey, 0);
        var msgId = ++_nextMessageId[storageKey];

        var message = new STMessengerMessage(
            msgId,
            displayName,
            content,
            _timing.CurTime,
            send.ReplyToId,
            replySnippet);

        chatMessages.Add(message);

        // Mark sender's own message as read
        server.LastSeenMessageId[chatId] = msgId;

        if (chatMessages.Count > maxMessages)
            chatMessages.RemoveRange(0, chatMessages.Count - maxMessages);

        // Admin log — include anonymous pseudonym so admins can trace abuse
        if (send.IsAnonymous && !isDm)
        {
            _adminLogger.Add(LogType.PdaMessage, LogImpact.Medium,
                $"{ToPrettyString(args.Actor):player} sent anonymous message " +
                $"(as \"{displayName}\") to {chatId}: {content}");
        }
        else
        {
            _adminLogger.Add(LogType.PdaMessage, LogImpact.Medium,
                $"{ToPrettyString(args.Actor):player} sent message to {chatId}: {content}");
        }

        if (isDm)
        {
            // DM: auto-add sender to recipient's contacts so they can reply
            var contactName = chatId[STMessengerChat.DmChatPrefix.Length..];
            if (_characterToPda.TryGetValue(contactName, out var recipientPdaUid)
                && _cartridgeLoader.TryGetProgram<STMessengerServerComponent>(
                    recipientPdaUid, out _, out var recipientServer)
                && recipientServer.Contacts.Add(senderName))
            {
                AddContactAsync(recipientServer.OwnerCharacterName, senderName);
            }

            NotifyDmRecipient(contactName, server);
        }
        else
        {
            SendDiscordWebhook(chatId, displayName, content);
            NotifyChannelRecipients(chatId, server);
        }

        BroadcastUiUpdate(chatId);
    }

    private string? FindReplySnippet(string chatId, bool isDm, string senderName, uint replyId)
    {
        List<STMessengerMessage>? messages = null;

        if (isDm)
        {
            var contactName = chatId[STMessengerChat.DmChatPrefix.Length..];
            var key = NormalizeDmKey(senderName, contactName);
            _dmChats.TryGetValue(key, out messages);
        }
        else
        {
            _channelChats.TryGetValue(chatId, out messages);
        }

        if (messages is null)
            return null;

        foreach (var msg in messages)
        {
            if (msg.Id != replyId)
                continue;

            return msg.Content.Length > STMessengerChat.MaxReplySnippetLength
                ? msg.Content[..STMessengerChat.MaxReplySnippetLength] + "..."
                : msg.Content;
        }

        return null;
    }

    private void NotifyDmRecipient(string contactName, STMessengerServerComponent senderServer)
    {
        if (!_characterToPda.TryGetValue(contactName, out var recipientPdaUid))
            return;

        if (!TryComp<CartridgeLoaderComponent>(recipientPdaUid, out _))
            return;

        // Play ringer if not muted (DMs are never muted via channel mute, so always ring)
        if (TryComp<RingerComponent>(recipientPdaUid, out var ringer))
            _ringer.RingerPlayRingtone((recipientPdaUid, ringer));
    }

    private void NotifyChannelRecipients(string channelId, STMessengerServerComponent senderServer)
    {
        // Use cached messenger PDAs instead of full entity query
        foreach (var (pdaUid, (cartridgeUid, _)) in _messengerPdas)
        {
            if (!TryComp<STMessengerServerComponent>(cartridgeUid, out var server))
                continue;

            if (server.MutedChannels.Contains(channelId))
                continue;

            if (TryComp<RingerComponent>(pdaUid, out var ringer))
                _ringer.RingerPlayRingtone((pdaUid, ringer));
        }
    }

    #endregion

    #region Contacts

    private void OnAddContact(
        Entity<STMessengerComponent> ent,
        STMessengerServerComponent server,
        STMessengerAddContactEvent add,
        CartridgeMessageEvent args)
    {
        if (string.IsNullOrWhiteSpace(add.MessengerId))
            return;

        if (server.NextInteractionTime > _timing.CurTime)
            return;

        server.NextInteractionTime = _timing.CurTime + InteractionCooldown;

        if (!_messengerIdCache.TryGetValue(add.MessengerId, out var contactName))
            return;

        if (contactName == server.OwnerCharacterName)
            return;

        if (server.Contacts.Count >= MaxContacts)
            return;

        if (!server.Contacts.Add(contactName))
            return;

        AddContactAsync(server.OwnerCharacterName, contactName);

        BroadcastUiUpdate();
    }

    private void OnRemoveContact(
        Entity<STMessengerComponent> ent,
        STMessengerServerComponent server,
        STMessengerRemoveContactEvent remove,
        CartridgeMessageEvent args)
    {
        if (server.NextInteractionTime > _timing.CurTime)
            return;

        server.NextInteractionTime = _timing.CurTime + InteractionCooldown;

        if (!server.Contacts.Remove(remove.ContactName))
            return;

        RemoveContactAsync(server.OwnerCharacterName, remove.ContactName);

        var loaderUid = GetEntity(args.LoaderUid);
        UpdateUiState(ent, loaderUid, server);
    }

    #endregion

    #region Mute / Mark Read / View Chat

    private void OnToggleMute(
        Entity<STMessengerComponent> ent,
        STMessengerServerComponent server,
        STMessengerToggleMuteEvent mute,
        CartridgeMessageEvent args)
    {
        if (!server.MutedChannels.Add(mute.ChannelId))
            server.MutedChannels.Remove(mute.ChannelId);

        var loaderUid = GetEntity(args.LoaderUid);
        UpdateUiState(ent, loaderUid, server);
    }

    private void OnMarkRead(STMessengerServerComponent server, STMessengerMarkReadEvent markRead)
    {
        server.LastSeenMessageId[markRead.ChatId] = markRead.LastSeenMessageId;
    }

    private void OnViewChat(NetEntity loaderNetUid, STMessengerViewChatEvent viewChat)
    {
        var loaderUid = GetEntity(loaderNetUid);
        _viewedChat[loaderUid] = viewChat.ChatId;

        if (_cartridgeLoader.TryGetProgram<STMessengerComponent>(loaderUid, out var progUid, out _)
            && TryComp<STMessengerServerComponent>(progUid.Value, out var server))
        {
            if (viewChat.ChatId is not null)
                MarkChatAsRead(viewChat.ChatId, server);

            UpdateUiState((progUid.Value, Comp<STMessengerComponent>(progUid.Value)), loaderUid, server);
        }
    }

    private void MarkChatAsRead(string chatId, STMessengerServerComponent server)
    {
        var isDm = chatId.StartsWith(STMessengerChat.DmChatPrefix, StringComparison.Ordinal);
        List<STMessengerMessage>? messages = null;

        if (isDm)
        {
            var contactName = chatId[STMessengerChat.DmChatPrefix.Length..];
            var dmKey = NormalizeDmKey(server.OwnerCharacterName, contactName);
            _dmChats.TryGetValue(dmKey, out messages);
        }
        else
        {
            _channelChats.TryGetValue(chatId, out messages);
        }

        if (messages is { Count: > 0 })
            server.LastSeenMessageId[chatId] = messages[^1].Id;
    }

    #endregion

    #region UI State

    private void UpdateUiState(Entity<STMessengerComponent> ent, EntityUid loaderUid, STMessengerServerComponent server)
    {
        var state = BuildUiState(loaderUid, server);
        _cartridgeLoader.UpdateCartridgeUiState(loaderUid, state);
    }

    private STMessengerUiState BuildUiState(EntityUid loaderUid, STMessengerServerComponent server)
    {
        _viewedChat.TryGetValue(loaderUid, out var viewedChatId);

        // Use pre-sorted channel cache to avoid per-call prototype lookups and sorting
        var channels = new List<STMessengerChat>(_sortedChannels.Count);
        foreach (var proto in _sortedChannels)
        {
            List<STMessengerMessage>? messages = null;
            if (viewedChatId == proto.ID && _channelChats.TryGetValue(proto.ID, out var channelMessages))
                messages = new List<STMessengerMessage>(channelMessages);

            var unread = CountUnread(proto.ID, channelMessages: _channelChats.GetValueOrDefault(proto.ID), server);
            var isMuted = server.MutedChannels.Contains(proto.ID);

            channels.Add(new STMessengerChat(
                proto.ID,
                Loc.GetString(proto.Name),
                isDirect: false,
                unread,
                isMuted,
                messages));
        }

        var directMessages = new List<STMessengerChat>(server.Contacts.Count);
        foreach (var contactName in server.Contacts)
        {
            var dmKey = NormalizeDmKey(server.OwnerCharacterName, contactName);
            var dmChatId = STMessengerChat.DmChatPrefix + contactName;

            List<STMessengerMessage>? messages = null;
            if (viewedChatId == dmChatId && _dmChats.TryGetValue(dmKey, out var dmMessages))
                messages = new List<STMessengerMessage>(dmMessages);

            var unread = CountUnread(dmChatId, channelMessages: _dmChats.GetValueOrDefault(dmKey), server);

            directMessages.Add(new STMessengerChat(
                dmChatId,
                contactName,
                isDirect: true,
                unread,
                isMuted: false,
                messages));
        }

        var contactInfos = new List<STMessengerContactInfo>();
        foreach (var contactName in server.Contacts)
        {
            contactInfos.Add(new STMessengerContactInfo(
                contactName,
                _characterToMessengerId.GetValueOrDefault(contactName),
                _characterToBandIcon.GetValueOrDefault(contactName)));
        }

        return new STMessengerUiState(
            server.MessengerId,
            channels,
            directMessages,
            contactInfos);
    }

    private int CountUnread(string chatId, List<STMessengerMessage>? channelMessages, STMessengerServerComponent server)
    {
        if (channelMessages is null || channelMessages.Count == 0)
            return 0;

        if (!server.LastSeenMessageId.TryGetValue(chatId, out var lastSeen))
            return channelMessages.Count;

        // Iterate from end — messages are ordered by ID, so we can stop early
        var count = 0;
        for (var i = channelMessages.Count - 1; i >= 0; i--)
        {
            if (channelMessages[i].Id <= lastSeen)
                break;

            count++;
        }

        return count;
    }

    /// <summary>
    /// Broadcasts UI updates to active loaders. When <paramref name="changedChatId"/> is set,
    /// only loaders viewing the main page or the relevant chat are updated.
    /// </summary>
    private void BroadcastUiUpdate(string? changedChatId = null)
    {
        foreach (var loaderUid in _activeLoaders)
        {
            if (!TryComp<CartridgeLoaderComponent>(loaderUid, out _))
                continue;

            if (!_cartridgeLoader.TryGetProgram<STMessengerComponent>(
                    loaderUid, out var progUid, out var messengerComp))
                continue;

            if (!TryComp<STMessengerServerComponent>(progUid.Value, out var server))
                continue;

            // Skip viewers looking at a different chat — they'll update when they navigate
            if (changedChatId is not null
                && _viewedChat.TryGetValue(loaderUid, out var viewedChat)
                && viewedChat != changedChatId)
                continue;

            UpdateUiState((progUid.Value, messengerComp), loaderUid, server);
        }
    }

    #endregion

    #region Player Spawn & Data Loading

    /// <summary>
    /// Handles PDA being equipped in the ID slot — reloads messenger data from DB
    /// when a fresh PDA is equipped (e.g. after personal stash store/retrieve).
    /// </summary>
    private void OnPdaEquipped(Entity<PdaComponent> ent, ref GotEquippedEvent args)
    {
        if (!args.SlotFlags.HasFlag(SlotFlags.IDCARD))
            return;

        if (!_cartridgeLoader.TryGetProgram<STMessengerComponent>(ent.Owner, out var progUid, out _))
            return;

        if (!TryComp<STMessengerServerComponent>(progUid.Value, out var server))
            return;

        // Already claimed — don't overwrite (e.g. looted PDA with someone else's data)
        if (!string.IsNullOrEmpty(server.OwnerCharacterName))
            return;

        var charName = MetaData(args.Equipee).EntityName;
        InitializeMessengerForPda(ent.Owner, progUid.Value, server, charName, args.Equipee);
    }

    private void OnPlayerSpawned(PlayerSpawnCompleteEvent args)
    {
        if (!_inventory.TryGetSlotEntity(args.Mob, "id", out var idEntity))
            return;

        if (!TryComp<PdaComponent>(idEntity, out var pdaComp))
            return;

        if (!_cartridgeLoader.TryGetProgram<STMessengerComponent>(idEntity.Value, out var progUid, out _))
            return;

        if (!TryComp<STMessengerServerComponent>(progUid.Value, out var server))
            return;

        // Guard: OwnerCharacterName is set synchronously by InitializeMessengerForPda,
        // so if GotEquippedEvent already fired (e.g. during loadout equip), skip to avoid double-loading.
        if (!string.IsNullOrEmpty(server.OwnerCharacterName))
            return;

        var charName = args.Profile.Name;
        InitializeMessengerForPda(idEntity.Value, progUid.Value, server, charName, args.Mob);
    }

    /// <summary>
    /// Shared logic for initializing a messenger PDA for a character.
    /// Updates caches synchronously, then starts async DB loads for messenger ID and contacts.
    /// Called from both <see cref="OnPlayerSpawned"/> and <see cref="OnPdaEquipped"/>.
    /// </summary>
    private void InitializeMessengerForPda(
        EntityUid pdaUid,
        EntityUid cartridgeUid,
        STMessengerServerComponent server,
        string charName,
        EntityUid mobUid)
    {
        server.OwnerCharacterName = charName;

        _characterToPda[charName] = pdaUid;
        _messengerPdas[pdaUid] = (cartridgeUid, pdaUid);

        // Resolve the JobIconPrototype to get the actual RSI state name,
        // since BandStatusIcon is a prototype ID that may differ from the RSI state
        // (e.g. "seraphim" prototype → "seraph" RSI state).
        if (TryComp<BandsComponent>(mobUid, out var bands)
            && _protoManager.TryIndex<JobIconPrototype>(bands.BandStatusIcon, out var jobIcon)
            && jobIcon.Icon is SpriteSpecifier.Rsi rsi)
        {
            _characterToBandIcon[charName] = rsi.RsiState;

            // Refresh open messenger UIs so other players see this player's faction icon immediately
            BroadcastUiUpdate();
        }

        LoadOrGenerateMessengerIdAsync(cartridgeUid, charName);
        LoadContactsAsync(cartridgeUid, charName);
    }

    #endregion

    #region Round Lifecycle

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _activeLoaders.Clear();
        _viewedChat.Clear();
        _channelChats.Clear();
        _dmChats.Clear();
        _nextMessageId.Clear();
        _characterToPda.Clear();
        // Do NOT clear _characterToBandIcon — band icons persist across rounds like _characterToMessengerId,
        // so contacts from previous rounds still show faction patches. Updated when players spawn again.
        _messengerPdas.Clear();
        _anonymousPseudonyms.Clear();
        _usedPseudonyms.Clear();
        // Do NOT clear _messengerIdCache or _characterToMessengerId — IDs persist across rounds

        foreach (var proto in _sortedChannels)
        {
            _channelChats.TryAdd(proto.ID, new List<STMessengerMessage>());
        }
    }

    #endregion

    #region Discord Webhook

    private void SendDiscordWebhook(string channelId, string senderName, string content)
    {
        if (_webhookIdentifier is not { } identifier)
            return;

        var channelName = channelId;
        if (_protoManager.TryIndex<STMessengerChannelPrototype>(channelId, out var proto))
            channelName = Loc.GetString(proto.Name);

        var payload = new WebhookPayload
        {
            Content = $"[{channelName}] {senderName}: {content}",
        };

        _discord.CreateMessage(identifier, payload);
    }

    #endregion

    #region Helpers

    private void CacheSortedChannels()
    {
        _sortedChannels = new List<STMessengerChannelPrototype>(
            _protoManager.EnumeratePrototypes<STMessengerChannelPrototype>());
        _sortedChannels.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.ByType.ContainsKey(typeof(STMessengerChannelPrototype)))
            CacheSortedChannels();
    }

    /// <summary>
    /// Returns a stable anonymous pseudonym for the given character name.
    /// The same character always gets the same pseudonym within a round.
    /// Pseudonyms are cleared on round restart.
    /// </summary>
    private string GetOrCreatePseudonym(string charName)
    {
        if (_anonymousPseudonyms.TryGetValue(charName, out var existing))
            return existing;

        for (var attempt = 0; attempt < MaxRetryCollision; attempt++)
        {
            var name = _random.Pick(AnonymousNames);
            var suffix = _random.Next(1, MaxPseudonymSuffix + 1);
            var pseudonym = $"{name}-{suffix}";

            if (_usedPseudonyms.Contains(pseudonym))
                continue;

            _usedPseudonyms.Add(pseudonym);
            _anonymousPseudonyms[charName] = pseudonym;
            return pseudonym;
        }

        // Fallback: use name + charName hash; bitwise AND avoids OverflowException on int.MinValue
        var hashSuffix = (charName.GetHashCode() & 0x7FFFFFFF) % (MaxPseudonymSuffix + 1);
        var fallback = $"{_random.Pick(AnonymousNames)}-{hashSuffix}";

        while (_usedPseudonyms.Contains(fallback))
            fallback += "X";

        _usedPseudonyms.Add(fallback);
        _anonymousPseudonyms[charName] = fallback;
        return fallback;
    }

    /// <summary>
    /// Normalize DM key to ensure both directions map to the same storage.
    /// Uses alphabetical ordering (same pattern as STFactionRelationHelpers.NormalizePair).
    /// </summary>
    private static string NormalizeDmKey(string nameA, string nameB)
    {
        return string.Compare(nameA, nameB, StringComparison.Ordinal) < 0
            ? string.Concat(nameA, ":", nameB)
            : string.Concat(nameB, ":", nameA);
    }

    /// <summary>
    /// Generate a random unique messenger ID in "XXX-XXX" format.
    /// </summary>
    private static string GenerateMessengerId(IRobustRandom random)
    {
        var part1 = random.Next(100, 1000);
        var part2 = random.Next(100, 1000);
        return $"{part1}-{part2}";
    }

    #endregion
}
