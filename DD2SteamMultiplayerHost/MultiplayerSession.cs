using System;
using System.Collections.Generic;
using System.Linq;
using DD2SteamMultiplayerHost.Adapter;
using DD2SteamMultiplayerHost.Protocol;
using Steamworks;

namespace DD2SteamMultiplayerHost
{
    internal sealed class MultiplayerSession : IDisposable
    {
        private const string ProtocolName = "dd2-steam-mp-host";
        private const double ProtocolPerfSlowThresholdMs = 4.0;
        private static readonly bool ProtocolPerfLoggingEnabled = false;
        private static readonly TimeSpan AutoResyncStaleThreshold = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan AutoResyncCooldown = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ProtocolPerfSummaryInterval = TimeSpan.FromSeconds(60);
        internal const string VoteKeyLoot = "loot";
        internal const string VoteKeyRoute = "route";
        internal const string VoteKeyStory = "story";
        internal const string VoteKeyHeroReady = "hero_ready";
        internal const string VoteKeyInnBiome = "inn_biome";
        internal const string VoteKeyInnEmbark = "inn_embark";
        internal const string VoteKeyEmbarkContinue = "embark_continue";
        internal const string VoteKeyAltarEmbark = "altar_embark";
        internal const string VoteKeyGameResults = "game_results";
        internal const string VoteKeyConfessionChoice = "confession_choice";
        internal const string VoteKeyLairDecision = "lair_decision";
        internal const string VoteKeyConfirmationDialog = "confirmation_dialog";
        internal const string VoteKeyMainMenu = "main_menu";
        internal const string PvpModeExpeditionEnemyPilot = "expedition_enemy_pilot";

        private readonly SteamLobbyClient _lobby;
        private readonly SteamMessageTransport _transport;
        private readonly ILootActionAdapter _lootActionAdapter;
        private readonly IGameResultsActionAdapter _gameResultsActionAdapter;
        private readonly IRouteChoiceAdapter _routeChoiceAdapter;
        private readonly IHeroSelectAdapter _heroSelectAdapter;
        private readonly IHeroLoadoutAdapter _heroLoadoutAdapter;
        private readonly IMainMenuActionAdapter _mainMenuActionAdapter;
        private readonly IStoryChoiceAdapter _storyChoiceAdapter;
        private readonly IInnActionAdapter _innActionAdapter;
        private readonly IEmbarkActionAdapter _embarkActionAdapter;
        private readonly IAltarActionAdapter _altarActionAdapter;
        private readonly IConfessionChoiceAdapter _confessionChoiceAdapter;
        private readonly ILairDecisionAdapter _lairDecisionAdapter;
        private readonly IConfirmationDialogAdapter _confirmationDialogAdapter;
        private readonly IStoreActionAdapter _storeActionAdapter;
        private readonly IStagecoachActionAdapter _stagecoachActionAdapter;
        private readonly Dictionary<int, HeroSlotAssignmentPayload> _heroSlots = new Dictionary<int, HeroSlotAssignmentPayload>();
        private PvpModeStatePayload _pvpModeState;
        private readonly TurnCommandCoordinator _turnCoordinator = new TurnCommandCoordinator();
        private readonly Random _voteRandom = new Random();
        private readonly Dictionary<ulong, VoteRecord<LootActionRequestPayload>> _lootActionVotes =
            new Dictionary<ulong, VoteRecord<LootActionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<RouteChoiceRequestPayload>> _routeChoiceVotes =
            new Dictionary<ulong, VoteRecord<RouteChoiceRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<StoryChoiceRequestPayload>> _storyChoiceVotes =
            new Dictionary<ulong, VoteRecord<StoryChoiceRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<InnActionRequestPayload>> _innBiomeVotes =
            new Dictionary<ulong, VoteRecord<InnActionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<InnActionRequestPayload>> _innEmbarkVotes =
            new Dictionary<ulong, VoteRecord<InnActionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<EmbarkActionRequestPayload>> _embarkContinueVotes =
            new Dictionary<ulong, VoteRecord<EmbarkActionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<AltarActionRequestPayload>> _altarEmbarkVotes =
            new Dictionary<ulong, VoteRecord<AltarActionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<GameResultsActionRequestPayload>> _gameResultsVotes =
            new Dictionary<ulong, VoteRecord<GameResultsActionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<ConfessionChoiceRequestPayload>> _confessionChoiceVotes =
            new Dictionary<ulong, VoteRecord<ConfessionChoiceRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<LairDecisionRequestPayload>> _lairDecisionVotes =
            new Dictionary<ulong, VoteRecord<LairDecisionRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<ConfirmationDialogRequestPayload>> _confirmationDialogVotes =
            new Dictionary<ulong, VoteRecord<ConfirmationDialogRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<HeroSelectRequestPayload>> _heroSelectReadyVotes =
            new Dictionary<ulong, VoteRecord<HeroSelectRequestPayload>>();
        private readonly Dictionary<ulong, VoteRecord<MainMenuActionRequestPayload>> _mainMenuActionVotes =
            new Dictionary<ulong, VoteRecord<MainMenuActionRequestPayload>>();
        private readonly Dictionary<string, VoteStatusPayload> _voteStatuses =
            new Dictionary<string, VoteStatusPayload>(StringComparer.Ordinal);
        private readonly Dictionary<string, ProtocolPerfStats> _protocolPerfStats =
            new Dictionary<string, ProtocolPerfStats>(StringComparer.Ordinal);
        private DateTime _nextProtocolPerfSummaryUtc = DateTime.MinValue;
        private CombatSnapshotPayload _latestCombatSnapshot;
        private BattleResultPayload _latestBattleResult;
        private LootWindowSnapshotPayload _latestLootWindowSnapshot;
        private GameResultsSnapshotPayload _latestGameResultsSnapshot;
        private RouteChoiceSnapshotPayload _latestRouteChoiceSnapshot;
        private HeroSelectSnapshotPayload _latestHeroSelectSnapshot;
        private HeroLoadoutSnapshotPayload _latestHeroLoadoutSnapshot;
        private RunStateSnapshotPayload _latestRunStateSnapshot;
        private ExpeditionOverviewSnapshotPayload _latestExpeditionOverviewSnapshot;
        private MainMenuSnapshotPayload _latestMainMenuSnapshot;
        private StoryChoiceSnapshotPayload _latestStoryChoiceSnapshot;
        private InnSnapshotPayload _latestInnSnapshot;
        private EmbarkSnapshotPayload _latestEmbarkSnapshot;
        private AltarSnapshotPayload _latestAltarSnapshot;
        private ConfessionChoiceSnapshotPayload _latestConfessionChoiceSnapshot;
        private LairDecisionSnapshotPayload _latestLairDecisionSnapshot;
        private ConfirmationDialogSnapshotPayload _latestConfirmationDialogSnapshot;
        private StoreSnapshotPayload _latestStoreSnapshot;
        private StagecoachSnapshotPayload _latestStagecoachSnapshot;
        private DamageMeterSnapshotPayload _latestDamageMeterSnapshot;
        private CurrentInteractionSnapshotPayload _latestCurrentInteractionSnapshot;
        private string _lastLoggedCombatSnapshotDigest;
        private string _lastLoggedBattleResultDigest;
        private string _lastLoggedLootWindowDigest;
        private string _lastLoggedGameResultsDigest;
        private string _lastLoggedRouteChoiceDigest;
        private string _lastLoggedHeroSelectDigest;
        private string _lastLoggedHeroLoadoutDigest;
        private string _lastLoggedRunStateDigest;
        private string _lastLoggedExpeditionOverviewDigest;
        private string _lastLoggedMainMenuDigest;
        private string _lastLoggedStoryChoiceDigest;
        private string _lastLoggedInnDigest;
        private string _lastLoggedEmbarkDigest;
        private string _lastLoggedAltarDigest;
        private string _lastLoggedConfessionChoiceDigest;
        private string _lastLoggedLairDecisionDigest;
        private string _lastLoggedConfirmationDialogDigest;
        private string _lastLoggedStoreDigest;
        private string _lastLoggedStagecoachDigest;
        private string _lastLoggedDamageMeterDigest;
        private string _lastLoggedCurrentInteractionDigest;
        private string _lastLoggedPvpModeDigest;
        private string _lootVoteDigest;
        private string _routeChoiceVoteDigest;
        private string _storyChoiceVoteDigest;
        private string _heroSelectReadyVoteDigest;
        private string _innVoteDigest;
        private string _embarkVoteDigest;
        private string _altarVoteDigest;
        private string _gameResultsVoteDigest;
        private string _confessionChoiceVoteDigest;
        private string _lairDecisionVoteDigest;
        private string _confirmationDialogVoteDigest;
        private string _mainMenuVoteDigest;
        private string _lootResolvedDigest;
        private string _routeChoiceResolvedDigest;
        private string _storyChoiceResolvedDigest;
        private string _heroSelectReadyResolvedDigest;
        private string _innBiomeResolvedDigest;
        private string _innEmbarkResolvedDigest;
        private string _embarkContinueResolvedDigest;
        private string _altarEmbarkResolvedDigest;
        private string _gameResultsResolvedDigest;
        private string _confessionChoiceResolvedDigest;
        private string _lairDecisionResolvedDigest;
        private string _confirmationDialogResolvedDigest;
        private string _mainMenuResolvedDigest;
        private bool _choiceOverruleEnabled;
        private int _choiceOverruleLimitPerMap = 1;
        private int _choiceOverruleRemaining;
        private string _choiceOverruleMapKey;
        private DateTime _lastHostStateReceivedUtc = DateTime.MinValue;
        private DateTime _nextAutoResyncUtc = DateTime.MinValue;
        private long _sequence;

        private sealed class ProtocolPerfStats
        {
            public int Calls;
            public int SlowCalls;
            public int MaxChars;
            public int TotalSent;
            public double TotalMs;
            public double MaxMs;
        }

        public MultiplayerSession(
            SteamLobbyClient lobby,
            SteamMessageTransport transport,
            ICombatCommandAdapter combatAdapter,
            ILootActionAdapter lootActionAdapter,
            IGameResultsActionAdapter gameResultsActionAdapter,
            IRouteChoiceAdapter routeChoiceAdapter,
            IHeroSelectAdapter heroSelectAdapter,
            IHeroLoadoutAdapter heroLoadoutAdapter,
            IMainMenuActionAdapter mainMenuActionAdapter,
            IStoryChoiceAdapter storyChoiceAdapter,
            IInnActionAdapter innActionAdapter,
            IEmbarkActionAdapter embarkActionAdapter,
            IAltarActionAdapter altarActionAdapter,
            IConfessionChoiceAdapter confessionChoiceAdapter,
            ILairDecisionAdapter lairDecisionAdapter,
            IConfirmationDialogAdapter confirmationDialogAdapter,
            IStoreActionAdapter storeActionAdapter,
            IStagecoachActionAdapter stagecoachActionAdapter)
        {
            _lobby = lobby;
            _transport = transport;
            _lootActionAdapter = lootActionAdapter;
            _gameResultsActionAdapter = gameResultsActionAdapter;
            _routeChoiceAdapter = routeChoiceAdapter;
            _heroSelectAdapter = heroSelectAdapter;
            _heroLoadoutAdapter = heroLoadoutAdapter;
            _mainMenuActionAdapter = mainMenuActionAdapter;
            _storyChoiceAdapter = storyChoiceAdapter;
            _innActionAdapter = innActionAdapter;
            _embarkActionAdapter = embarkActionAdapter;
            _altarActionAdapter = altarActionAdapter;
            _confessionChoiceAdapter = confessionChoiceAdapter;
            _lairDecisionAdapter = lairDecisionAdapter;
            _confirmationDialogAdapter = confirmationDialogAdapter;
            _storeActionAdapter = storeActionAdapter;
            _stagecoachActionAdapter = stagecoachActionAdapter;
            _transport.MessageReceived += OnMessageReceived;
            _lobby.LobbyReady += OnLobbyReady;
            _lobby.MemberStateChanged += OnMemberStateChanged;
            _turnCoordinator.CommandReady += combatAdapter.Execute;
            _turnCoordinator.TurnCleared += OnTurnCleared;
        }

        public void Dispose()
        {
            _transport.MessageReceived -= OnMessageReceived;
            _lobby.LobbyReady -= OnLobbyReady;
            _lobby.MemberStateChanged -= OnMemberStateChanged;
            _turnCoordinator.TurnCleared -= OnTurnCleared;
        }

        public void SendChat(string text)
        {
            Broadcast(MultiplayerMessageType.Chat, new ChatPayload(text), true);
        }

        public void AssignHeroSlot(int slot, string memberQuery)
        {
            if (!_lobby.IsHost)
            {
                HostLog.Write("[protocol] Only the lobby owner can assign hero slots.");
                return;
            }

            if (slot < 1 || slot > 4)
            {
                HostLog.Write("[protocol] Hero slot must be 1-4.");
                return;
            }

            CSteamID member;
            if (!_lobby.TryFindMember(memberQuery, out member))
            {
                HostLog.Write("[protocol] Could not find lobby member: " + memberQuery + ".");
                return;
            }

            if (IsPvpEnemyController(member.m_SteamID))
            {
                HostLog.Write("[protocol] Cannot assign hero slot " + slot +
                    " to the active PVP enemy pilot " + _lobby.GetPersonaName(member) +
                    "/" + member.m_SteamID + ".");
                return;
            }

            AssignHeroSlotToMember(slot, member, true);
            BroadcastLobbyState();
        }

        public void AutoAssignHeroSlots(bool replaceExisting)
        {
            if (!_lobby.IsHost)
            {
                HostLog.Write("[protocol] Only the lobby owner can auto-assign hero slots.");
                return;
            }

            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[protocol] Auto slot assignment ignored; no active lobby.");
                return;
            }

            List<CSteamID> members = GetSlotAssignmentMembers();
            if (members.Count == 0)
            {
                HostLog.Write("[protocol] Auto slot assignment ignored; lobby has no members.");
                return;
            }

            if (replaceExisting)
            {
                _heroSlots.Clear();
            }
            else
            {
                int removedEnemySlots = RemovePvpEnemyHeroSlots();
                if (removedEnemySlots > 0)
                {
                    HostLog.Write("[pvp] Removed " + removedEnemySlots +
                        " hero slot(s) owned by the active PVP enemy pilot before autofill.");
                }
            }

            int assigned = 0;
            for (int slot = 1; slot <= 4; slot++)
            {
                if (!replaceExisting && _heroSlots.ContainsKey(slot))
                {
                    continue;
                }

                CSteamID member = members[(slot - 1) % members.Count];
                AssignHeroSlotToMember(slot, member, true);
                assigned++;
            }

            BroadcastLobbyState();
            HostLog.Write("[protocol] Auto " + (replaceExisting ? "replaced" : "filled") +
                " hero slots: assigned=" + assigned +
                ", members=" + members.Count + ".");
        }

        public bool TryGetHeroSlotOwner(int slot, out HeroSlotAssignmentPayload owner)
        {
            return _heroSlots.TryGetValue(slot, out owner);
        }

        public bool TryGetPvpModeState(out PvpModeStatePayload state)
        {
            state = _pvpModeState;
            return state != null;
        }

        public bool TryGetPvpEnemyController(out HeroSlotAssignmentPayload owner)
        {
            owner = null;
            PvpModeStatePayload state = _pvpModeState;
            if (state == null ||
                !state.Enabled ||
                state.EnemyControllerSteamId == 0UL)
            {
                return false;
            }

            owner = new HeroSlotAssignmentPayload(0, state.EnemyControllerSteamId, state.EnemyControllerName);
            return true;
        }

        public bool ChoiceOverruleEnabled
        {
            get { return _choiceOverruleEnabled; }
        }

        public int ChoiceOverruleLimitPerMap
        {
            get { return _choiceOverruleLimitPerMap; }
        }

        public int ChoiceOverruleRemaining
        {
            get
            {
                EnsureChoiceOverruleMapState();
                return _choiceOverruleRemaining;
            }
        }

        public string ChoiceOverruleMapKey
        {
            get
            {
                EnsureChoiceOverruleMapState();
                return _choiceOverruleMapKey;
            }
        }

        public void SetChoiceOverruleOptions(bool enabled, int limitPerMap)
        {
            if (!_lobby.IsHost)
            {
                HostLog.Write("[overrule] Only the lobby owner can change choice overrule settings.");
                return;
            }

            _choiceOverruleEnabled = enabled;
            _choiceOverruleLimitPerMap = Math.Max(0, Math.Min(20, limitPerMap));
            _choiceOverruleMapKey = null;
            EnsureChoiceOverruleMapState();
            ApplyChoiceOverruleState(_latestRouteChoiceSnapshot);
            ApplyChoiceOverruleState(_latestStoryChoiceSnapshot);
            ApplyChoiceOverruleState(_latestExpeditionOverviewSnapshot);
            BroadcastChoiceOverruleSnapshots();
            PublishCurrentInteractionSnapshot();
            HostLog.Write("[overrule] enabled=" + _choiceOverruleEnabled +
                ", limitPerMap=" + _choiceOverruleLimitPerMap +
                ", remaining=" + _choiceOverruleRemaining +
                ", map=" + (_choiceOverruleMapKey ?? "[none]") + ".");
        }

        public void SetPvpEnemyPilot(bool enabled, string memberQuery)
        {
            if (!_lobby.IsHost)
            {
                HostLog.Write("[pvp] Only the lobby owner can change PVP mode.");
                return;
            }

            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[pvp] PVP mode ignored; no active lobby.");
                return;
            }

            if (!enabled)
            {
                ApplyLocalPvpModeState(CreatePvpModeState(false, CSteamID.Nil, "disabled"));
                Broadcast(MultiplayerMessageType.PvpModeState, _pvpModeState, true);
                RefreshLootVoteEligibility();
                PublishCurrentInteractionSnapshot();
                return;
            }

            CSteamID member;
            if (string.IsNullOrWhiteSpace(memberQuery))
            {
                member = _lobby.GetMembers()
                    .FirstOrDefault(candidate => candidate != SteamUser.GetSteamID());
                if (!member.IsValid())
                {
                    member = SteamUser.GetSteamID();
                }
            }
            else if (!_lobby.TryFindMember(memberQuery, out member))
            {
                HostLog.Write("[pvp] Could not find lobby member: " + memberQuery + ".");
                return;
            }

            ApplyLocalPvpModeState(CreatePvpModeState(true, member, "host"));
            int removedSlots = RemoveHeroSlotsForSteamId(member.m_SteamID);
            if (removedSlots > 0)
            {
                HostLog.Write("[pvp] Removed " + removedSlots +
                    " hero slot(s) from enemy pilot " + _lobby.GetPersonaName(member) +
                    "/" + member.m_SteamID + ".");
            }

            Broadcast(MultiplayerMessageType.PvpModeState, _pvpModeState, true);
            if (removedSlots > 0)
            {
                BroadcastLobbyState();
            }

            RefreshLootVoteEligibility();
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetPendingTurn(
            out TurnPromptPayload prompt,
            out HeroSlotAssignmentPayload owner,
            out string skillId,
            out string targetGuid,
            out bool isPass)
        {
            return _turnCoordinator.TryGetPendingTurn(out prompt, out owner, out skillId, out targetGuid, out isPass);
        }

        public void SendTurnPrompt(
            int round,
            int turn,
            int heroSlot,
            string actorGuid,
            string actorName,
            IList<TurnSkillOptionPayload> skillOptions = null)
        {
            SendTurnPrompt(
                round,
                turn,
                heroSlot,
                actorGuid,
                actorName,
                0,
                heroSlot - 1,
                "hero",
                null,
                skillOptions);
        }

        public void SendTurnPrompt(
            int round,
            int turn,
            int heroSlot,
            string actorGuid,
            string actorName,
            int teamIndex,
            int teamPosition,
            string controlRole,
            HeroSlotAssignmentPayload ownerOverride,
            IList<TurnSkillOptionPayload> skillOptions = null)
        {
            if (!_lobby.IsHost)
            {
                HostLog.Write("[protocol] Only the lobby owner can send turn prompts.");
                return;
            }

            HeroSlotAssignmentPayload owner = ownerOverride;
            if (owner == null)
            {
                _heroSlots.TryGetValue(heroSlot, out owner);
            }

            TurnPromptPayload payload = new TurnPromptPayload(
                round,
                turn,
                heroSlot,
                actorGuid,
                actorName,
                teamIndex,
                teamPosition,
                controlRole,
                owner == null ? 0UL : owner.SteamId,
                owner == null ? null : owner.Name,
                skillOptions);
            _turnCoordinator.StartTurn(payload, owner);
            Broadcast(MultiplayerMessageType.TurnPrompt, payload, true);
            PublishCurrentInteractionSnapshot();
        }

        public void ChooseSkill(int heroSlot, string actorGuid, string skillId)
        {
            SendToHostOrEcho(MultiplayerMessageType.ChooseSkill, new ChooseSkillPayload(heroSlot, actorGuid, skillId));
        }

        public void ChooseTarget(int heroSlot, string actorGuid, string targetGuid)
        {
            SendToHostOrEcho(MultiplayerMessageType.ChooseTarget, new ChooseTargetPayload(heroSlot, actorGuid, targetGuid));
        }

        public void PassTurn(int heroSlot, string actorGuid)
        {
            SendToHostOrEcho(MultiplayerMessageType.PassTurn, new PassTurnPayload(heroSlot, actorGuid));
        }

        public void SendStateDigest(string label, string digest)
        {
            Broadcast(MultiplayerMessageType.StateDigest, new StateDigestPayload(label, digest), true);
        }

        public void SendCombatSnapshot(CombatSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost)
            {
                return;
            }

            if (snapshot == null)
            {
                return;
            }

            _latestCombatSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.CombatSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestCombatSnapshot(out CombatSnapshotPayload snapshot)
        {
            snapshot = _latestCombatSnapshot;
            return snapshot != null;
        }

        public void SendBattleResult(BattleResultPayload payload)
        {
            if (!_lobby.IsHost || payload == null)
            {
                return;
            }

            _latestBattleResult = payload;
            Broadcast(MultiplayerMessageType.BattleResult, payload, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestBattleResult(out BattleResultPayload payload)
        {
            payload = _latestBattleResult;
            return payload != null;
        }

        public void SendLootWindowSnapshot(LootWindowSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncLootVoteContext(snapshot);
            _latestLootWindowSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.LootWindowSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestLootWindowSnapshot(out LootWindowSnapshotPayload snapshot)
        {
            snapshot = _latestLootWindowSnapshot;
            return snapshot != null;
        }

        public void RequestLootTakeAll()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.LootActionRequest,
                new LootActionRequestPayload(Guid.NewGuid().ToString("N"), "take_all", null, 0));
        }

        public void RequestLootTakeItem(LootItemSnapshotPayload item)
        {
            if (item == null)
            {
                return;
            }

            RequestLootTakeSelected(new[] { item });
        }

        public void RequestLootTakeSelected(IEnumerable<LootItemSnapshotPayload> items)
        {
            List<LootActionItemPayload> selectedItems = (items ?? Array.Empty<LootItemSnapshotPayload>())
                .Where(item => item != null && item.InventoryIndex >= 0)
                .Select(item => new LootActionItemPayload(item.ItemId, item.Quantity, item.InventoryIndex))
                .ToList();

            if (selectedItems.Count == 0)
            {
                HostLog.Write("[loot-vote] No selected loot items to vote for.");
                return;
            }

            SendToHostOrEcho(
                MultiplayerMessageType.LootActionRequest,
                new LootActionRequestPayload(Guid.NewGuid().ToString("N"), "take_selected", selectedItems));
        }

        public void RequestLootDiscardAll()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.LootActionRequest,
                new LootActionRequestPayload(Guid.NewGuid().ToString("N"), "discard_all", null, 0));
        }

        public void SendGameResultsSnapshot(GameResultsSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncGameResultsVoteContext(snapshot);
            _latestGameResultsSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.GameResultsSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestGameResultsSnapshot(out GameResultsSnapshotPayload snapshot)
        {
            snapshot = _latestGameResultsSnapshot;
            return snapshot != null;
        }

        public void RequestGameResultsContinue()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.GameResultsActionRequest,
                new GameResultsActionRequestPayload(Guid.NewGuid().ToString("N"), "continue"));
        }

        public void SendRouteChoiceSnapshot(RouteChoiceSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncRouteChoiceVoteContext(snapshot);
            ApplyChoiceOverruleState(snapshot);
            _latestRouteChoiceSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.RouteChoiceSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestRouteChoiceSnapshot(out RouteChoiceSnapshotPayload snapshot)
        {
            snapshot = _latestRouteChoiceSnapshot;
            return snapshot != null;
        }

        public void RequestRouteChoice(int optionIndex, bool isForced = false)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.RouteChoiceRequest,
                new RouteChoiceRequestPayload(Guid.NewGuid().ToString("N"), optionIndex, isForced));
        }

        public void SendHeroSelectSnapshot(HeroSelectSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            ApplyHeroSlotOwners(snapshot);
            SyncHeroSelectReadyVoteContext(snapshot);
            _latestHeroSelectSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.HeroSelectSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestHeroSelectSnapshot(out HeroSelectSnapshotPayload snapshot)
        {
            snapshot = _latestHeroSelectSnapshot;
            return snapshot != null;
        }

        public void RequestHeroSelectAssign(int slotIndex, string actorGuid)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroSelectRequest,
                new HeroSelectRequestPayload(Guid.NewGuid().ToString("N"), "assign", slotIndex, actorGuid));
        }

        public void RequestHeroSelectClear(int slotIndex)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroSelectRequest,
                new HeroSelectRequestPayload(Guid.NewGuid().ToString("N"), "clear_slot", slotIndex, null));
        }

        public void RequestHeroSelectConfirm()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroSelectRequest,
                new HeroSelectRequestPayload(Guid.NewGuid().ToString("N"), "confirm", -1, null));
        }

        public void RequestHeroSelectReady()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroSelectRequest,
                new HeroSelectRequestPayload(Guid.NewGuid().ToString("N"), "ready", -1, null));
        }

        public void RequestHeroSelectPath(int slotIndex, string actorGuid, string pathId)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroSelectRequest,
                new HeroSelectRequestPayload(Guid.NewGuid().ToString("N"), "set_path", slotIndex, actorGuid, pathId));
        }

        public void SendHeroLoadoutSnapshot(HeroLoadoutSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            ApplyHeroLoadoutOwners(snapshot);
            _latestHeroLoadoutSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.HeroLoadoutSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestHeroLoadoutSnapshot(out HeroLoadoutSnapshotPayload snapshot)
        {
            snapshot = _latestHeroLoadoutSnapshot;
            return snapshot != null;
        }

        public void RequestHeroLoadoutSkill(int heroSlot, string actorGuid, string skillId, bool equip)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroLoadoutRequest,
                new HeroLoadoutRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "set_skill",
                    heroSlot,
                    actorGuid,
                    skillId,
                    equip));
        }

        public void RequestHeroMasterSkill(int heroSlot, string actorGuid, string skillId)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.HeroLoadoutRequest,
                new HeroLoadoutRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "master_skill",
                    heroSlot,
                    actorGuid,
                    skillId,
                    false));
        }

        public void RequestHeroEquipItem(
            int heroSlot,
            string actorGuid,
            string itemKind,
            int sourceInventoryIndex,
            int targetInventoryIndex,
            string itemId)
        {
            HeroLoadoutRequestPayload payload = new HeroLoadoutRequestPayload(
                Guid.NewGuid().ToString("N"),
                "equip_item",
                heroSlot,
                actorGuid,
                null,
                false)
            {
                ItemKind = itemKind,
                SourceInventoryIndex = sourceInventoryIndex,
                TargetInventoryIndex = targetInventoryIndex,
                ItemId = itemId,
            };
            SendToHostOrEcho(MultiplayerMessageType.HeroLoadoutRequest, payload);
        }

        public void RequestHeroUnequipItem(
            int heroSlot,
            string actorGuid,
            string itemKind,
            int targetInventoryIndex,
            string itemId)
        {
            HeroLoadoutRequestPayload payload = new HeroLoadoutRequestPayload(
                Guid.NewGuid().ToString("N"),
                "unequip_item",
                heroSlot,
                actorGuid,
                null,
                false)
            {
                ItemKind = itemKind,
                SourceInventoryIndex = -1,
                TargetInventoryIndex = targetInventoryIndex,
                ItemId = itemId,
            };
            SendToHostOrEcho(MultiplayerMessageType.HeroLoadoutRequest, payload);
        }

        public void RequestHeroUseRestItem(
            int heroSlot,
            string actorGuid,
            int sourceInventoryIndex,
            string itemId,
            IEnumerable<string> targetActorGuids)
        {
            HeroLoadoutRequestPayload payload = new HeroLoadoutRequestPayload(
                Guid.NewGuid().ToString("N"),
                "use_rest_item",
                heroSlot,
                actorGuid,
                null,
                false)
            {
                ItemKind = "rest",
                SourceInventoryIndex = sourceInventoryIndex,
                TargetInventoryIndex = -1,
                ItemId = itemId,
                TargetActorGuids = targetActorGuids == null
                    ? new List<string>()
                    : targetActorGuids.Where(guid => !string.IsNullOrWhiteSpace(guid)).ToList(),
            };
            SendToHostOrEcho(MultiplayerMessageType.HeroLoadoutRequest, payload);
        }

        public void SendRunStateSnapshot(RunStateSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            _latestRunStateSnapshot = snapshot;
            RefreshChoiceOverruleMapIfChanged();
            Broadcast(MultiplayerMessageType.RunStateSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestRunStateSnapshot(out RunStateSnapshotPayload snapshot)
        {
            snapshot = _latestRunStateSnapshot;
            return snapshot != null;
        }

        public void SendExpeditionOverviewSnapshot(ExpeditionOverviewSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            _latestExpeditionOverviewSnapshot = snapshot;
            RefreshChoiceOverruleMapIfChanged();
            ApplyChoiceOverruleState(snapshot);
            Broadcast(MultiplayerMessageType.ExpeditionOverviewSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestExpeditionOverviewSnapshot(out ExpeditionOverviewSnapshotPayload snapshot)
        {
            snapshot = _latestExpeditionOverviewSnapshot;
            return snapshot != null;
        }

        public void SendMainMenuSnapshot(MainMenuSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncMainMenuVoteContext(snapshot);
            _latestMainMenuSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.MainMenuSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestMainMenuSnapshot(out MainMenuSnapshotPayload snapshot)
        {
            snapshot = _latestMainMenuSnapshot;
            return snapshot != null;
        }

        public void RequestMainMenuAction(string action)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.MainMenuActionRequest,
                new MainMenuActionRequestPayload(Guid.NewGuid().ToString("N"), action));
        }

        public void SendStoryChoiceSnapshot(StoryChoiceSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            ApplyStoryChoiceOwners(snapshot);
            SyncStoryChoiceVoteContext(snapshot);
            ApplyChoiceOverruleState(snapshot);
            _latestStoryChoiceSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.StoryChoiceSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestStoryChoiceSnapshot(out StoryChoiceSnapshotPayload snapshot)
        {
            snapshot = _latestStoryChoiceSnapshot;
            return snapshot != null;
        }

        public void RequestStoryChoice(StoryChoiceOptionPayload option, bool isForced = false)
        {
            if (option == null)
            {
                return;
            }

            SendToHostOrEcho(
                MultiplayerMessageType.StoryChoiceRequest,
                new StoryChoiceRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    option.OptionIndex,
                    option.HeroSlot,
                    option.ActorGuid,
                    isForced));
        }

        public void SendInnSnapshot(InnSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncInnVoteContext(snapshot);
            _latestInnSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.InnSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestInnSnapshot(out InnSnapshotPayload snapshot)
        {
            snapshot = _latestInnSnapshot;
            return snapshot != null;
        }

        public void RequestInnSelectBiome(int optionIndex)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.InnActionRequest,
                new InnActionRequestPayload(Guid.NewGuid().ToString("N"), "select_biome", optionIndex));
        }

        public void RequestInnEmbark()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.InnActionRequest,
                new InnActionRequestPayload(Guid.NewGuid().ToString("N"), "embark"));
        }

        public void SendEmbarkSnapshot(EmbarkSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncEmbarkVoteContext(snapshot);
            _latestEmbarkSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.EmbarkSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestEmbarkSnapshot(out EmbarkSnapshotPayload snapshot)
        {
            snapshot = _latestEmbarkSnapshot;
            return snapshot != null;
        }

        public void RequestEmbarkApplyRelationships()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.EmbarkActionRequest,
                new EmbarkActionRequestPayload(Guid.NewGuid().ToString("N"), "apply_relationships"));
        }

        public void RequestEmbarkContinue()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.EmbarkActionRequest,
                new EmbarkActionRequestPayload(Guid.NewGuid().ToString("N"), "continue"));
        }

        public void SendAltarSnapshot(AltarSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncAltarVoteContext(snapshot);
            _latestAltarSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.AltarSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestAltarSnapshot(out AltarSnapshotPayload snapshot)
        {
            snapshot = _latestAltarSnapshot;
            return snapshot != null;
        }

        public void RequestAltarEmbark()
        {
            SendToHostOrEcho(
                MultiplayerMessageType.AltarActionRequest,
                new AltarActionRequestPayload(Guid.NewGuid().ToString("N"), "embark"));
        }

        public void RequestAltarTrackSpend(AltarTrackPayload track, float spendValue)
        {
            if (track == null)
            {
                return;
            }

            SendToHostOrEcho(
                MultiplayerMessageType.AltarActionRequest,
                new AltarActionRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "spend_track",
                    track.TrackIndex,
                    track.TrackId,
                    spendValue));
        }

        public void RequestAltarRewardPurchase(AltarRewardButtonPayload reward)
        {
            if (reward == null)
            {
                return;
            }

            SendToHostOrEcho(
                MultiplayerMessageType.AltarActionRequest,
                new AltarActionRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "purchase_reward",
                    reward.ButtonIndex,
                    reward.ScreenKind,
                    reward.CurrentUnlockTableId,
                    reward.UnlockTrackId,
                    reward.ItemType));
        }

        public void SendConfessionChoiceSnapshot(ConfessionChoiceSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncConfessionChoiceVoteContext(snapshot);
            _latestConfessionChoiceSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.ConfessionChoiceSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestConfessionChoiceSnapshot(out ConfessionChoiceSnapshotPayload snapshot)
        {
            snapshot = _latestConfessionChoiceSnapshot;
            return snapshot != null;
        }

        public void RequestConfessionChoice(ConfessionChoiceOptionPayload option)
        {
            if (option == null)
            {
                return;
            }

            SendToHostOrEcho(
                MultiplayerMessageType.ConfessionChoiceRequest,
                new ConfessionChoiceRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    option.OptionIndex,
                    option.BossId));
        }

        public void SendLairDecisionSnapshot(LairDecisionSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncLairDecisionVoteContext(snapshot);
            _latestLairDecisionSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.LairDecisionSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestLairDecisionSnapshot(out LairDecisionSnapshotPayload snapshot)
        {
            snapshot = _latestLairDecisionSnapshot;
            return snapshot != null;
        }

        public bool TryGetLatestVoteStatus(string voteKey, out VoteStatusPayload status)
        {
            status = null;
            return !string.IsNullOrWhiteSpace(voteKey) && _voteStatuses.TryGetValue(voteKey, out status);
        }

        public void RequestLairDecision(string action)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.LairDecisionRequest,
                new LairDecisionRequestPayload(Guid.NewGuid().ToString("N"), action));
        }

        public void SendConfirmationDialogSnapshot(ConfirmationDialogSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            SyncConfirmationDialogVoteContext(snapshot);
            _latestConfirmationDialogSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.ConfirmationDialogSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestConfirmationDialogSnapshot(out ConfirmationDialogSnapshotPayload snapshot)
        {
            snapshot = _latestConfirmationDialogSnapshot;
            return snapshot != null;
        }

        public void RequestConfirmationDialog(string action)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.ConfirmationDialogRequest,
                new ConfirmationDialogRequestPayload(Guid.NewGuid().ToString("N"), action));
        }

        public void SendStoreSnapshot(StoreSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            _latestStoreSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.StoreSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestStoreSnapshot(out StoreSnapshotPayload snapshot)
        {
            snapshot = _latestStoreSnapshot;
            return snapshot != null;
        }

        public void RequestStorePurchase(int inventoryIndex, string itemId)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.StoreActionRequest,
                new StoreActionRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "buy",
                    inventoryIndex,
                    itemId,
                    1));
        }

        public void SendStagecoachSnapshot(StagecoachSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            _latestStagecoachSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.StagecoachSnapshot, snapshot, false);
            PublishCurrentInteractionSnapshot();
        }

        public bool TryGetLatestStagecoachSnapshot(out StagecoachSnapshotPayload snapshot)
        {
            snapshot = _latestStagecoachSnapshot;
            return snapshot != null;
        }

        public void SendDamageMeterSnapshot(DamageMeterSnapshotPayload snapshot)
        {
            if (!_lobby.IsHost || snapshot == null)
            {
                return;
            }

            _latestDamageMeterSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.DamageMeterSnapshot, snapshot, false);
        }

        public bool TryGetLatestDamageMeterSnapshot(out DamageMeterSnapshotPayload snapshot)
        {
            snapshot = _latestDamageMeterSnapshot;
            return snapshot != null;
        }

        public bool TryGetLatestCurrentInteractionSnapshot(out CurrentInteractionSnapshotPayload snapshot)
        {
            snapshot = _latestCurrentInteractionSnapshot;
            return snapshot != null;
        }

        public void RequestStagecoachRepair(string repairKind)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.StagecoachActionRequest,
                new StagecoachActionRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "repair",
                    repairKind,
                    -1,
                    null,
                    -1,
                    null));
        }

        public void RequestStagecoachEquip(int sourceInventoryIndex, string itemId, string targetSlotType, int targetSlotIndex)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.StagecoachActionRequest,
                new StagecoachActionRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "equip_item",
                    null,
                    sourceInventoryIndex,
                    targetSlotType,
                    targetSlotIndex,
                    itemId));
        }

        public void RequestStagecoachUnequip(string targetSlotType, int targetSlotIndex, string itemId)
        {
            SendToHostOrEcho(
                MultiplayerMessageType.StagecoachActionRequest,
                new StagecoachActionRequestPayload(
                    Guid.NewGuid().ToString("N"),
                    "unequip_item",
                    null,
                    -1,
                    targetSlotType,
                    targetSlotIndex,
                    itemId));
        }

        public void RequestFullState(string reason)
        {
            string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
            SendToHostOrEcho(
                MultiplayerMessageType.FullStateRequest,
                new FullStateRequestPayload(Guid.NewGuid().ToString("N"), normalizedReason));
        }

        public void PollAutoResync()
        {
            if (!_lobby.IsInLobby || _lobby.IsHost)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (_lastHostStateReceivedUtc == DateTime.MinValue)
            {
                if (_nextAutoResyncUtc == DateTime.MinValue)
                {
                    _nextAutoResyncUtc = now.Add(AutoResyncStaleThreshold);
                    return;
                }

                if (now < _nextAutoResyncUtc)
                {
                    return;
                }

                _nextAutoResyncUtc = now.Add(AutoResyncCooldown);
                HostLog.Write("[resync] No host state received yet; requesting full state.");
                RequestFullState("auto-no-host-state");
                return;
            }

            if (now < _nextAutoResyncUtc)
            {
                return;
            }

            TimeSpan staleFor = now - _lastHostStateReceivedUtc;
            if (staleFor < AutoResyncStaleThreshold)
            {
                return;
            }

            _nextAutoResyncUtc = now.Add(AutoResyncCooldown);
            int staleSeconds = Math.Max(0, (int)Math.Round(staleFor.TotalSeconds));
            HostLog.Write("[resync] Host state stale for " + staleSeconds + "s; requesting full state.");
            RequestFullState("auto-stale-" + staleSeconds + "s");
        }

        public string GetAutoResyncStatus()
        {
            if (!_lobby.IsInLobby)
            {
                return "Auto resync: no active lobby.";
            }

            if (_lobby.IsHost)
            {
                return "Auto resync: host sends authoritative state.";
            }

            if (_lastHostStateReceivedUtc == DateTime.MinValue)
            {
                return "Auto resync: waiting for first host state.";
            }

            int seconds = Math.Max(0, (int)Math.Round((DateTime.UtcNow - _lastHostStateReceivedUtc).TotalSeconds));
            return "Auto resync: last host state " + seconds + "s ago.";
        }

        public void LogProtocolState()
        {
            HostLog.Write("[protocol] state protocol=" + ProtocolName +
                ", protocolVersion=" + MultiplayerProtocol.CurrentVersion +
                ", localLobbyVersion=" + SteamLobbyClient.LocalLobbyVersion +
                ", currentLobbyVersion=" + (string.IsNullOrEmpty(_lobby.CurrentLobbyVersion) ? "[none]" : _lobby.CurrentLobbyVersion) +
                ", lobbyVersionCompatible=" + _lobby.IsLobbyVersionCompatible +
                ", host=" + _lobby.IsHost +
                ", lobby=" + _lobby.CurrentLobby.m_SteamID + ".");

            if (_heroSlots.Count == 0)
            {
                HostLog.Write("[protocol] hero slots: none.");
            }
            else
            {
                foreach (HeroSlotAssignmentPayload slot in _heroSlots.Values.OrderBy(slot => slot.Slot))
                {
                    HostLog.Write("[protocol] slot " + slot.Slot + ": " + slot.Name + "/" + slot.SteamId + ".");
                }
            }

            if (_pvpModeState == null || !_pvpModeState.Enabled)
            {
                HostLog.Write("[pvp] disabled.");
            }
            else
            {
                HostLog.Write("[pvp] enabled mode=" + (_pvpModeState.Mode ?? "[none]") +
                    ", enemyController=" + (_pvpModeState.EnemyControllerName ?? "[none]") +
                    "/" + _pvpModeState.EnemyControllerSteamId +
                    ", runtimeEnemyInput=" + _pvpModeState.RuntimeEnemyInput +
                    ", suppressHeroSync=" + _pvpModeState.SuppressHeroSyncForEnemyController + ".");
            }

            _turnCoordinator.LogState();
        }

        private void OnLobbyReady()
        {
            if (_lobby.IsHost)
            {
                BroadcastLobbyState();
                if (_pvpModeState != null)
                {
                    Broadcast(MultiplayerMessageType.PvpModeState, _pvpModeState, true);
                }
            }
            else
            {
                _pvpModeState = null;
                SendToHost(
                    MultiplayerMessageType.Hello,
                    new HelloPayload(
                        ProtocolName,
                        4,
                        MultiplayerProtocol.CurrentVersion,
                        SteamLobbyClient.LocalLobbyVersion));
                _lastHostStateReceivedUtc = DateTime.MinValue;
                _nextAutoResyncUtc = DateTime.UtcNow.Add(AutoResyncStaleThreshold);
            }
        }

        private void OnMemberStateChanged(CSteamID member, EChatMemberStateChange state)
        {
            if (!_lobby.IsHost)
            {
                return;
            }

            if ((state & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                (state & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0 ||
                (state & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0 ||
                (state & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0)
            {
                foreach (int slot in _heroSlots.Where(pair => pair.Value.SteamId == member.m_SteamID).Select(pair => pair.Key).ToArray())
                {
                    _heroSlots.Remove(slot);
                }

                _lootActionVotes.Remove(member.m_SteamID);
                _routeChoiceVotes.Remove(member.m_SteamID);
                _storyChoiceVotes.Remove(member.m_SteamID);
                _heroSelectReadyVotes.Remove(member.m_SteamID);
                _innBiomeVotes.Remove(member.m_SteamID);
                _innEmbarkVotes.Remove(member.m_SteamID);
                _embarkContinueVotes.Remove(member.m_SteamID);
                _altarEmbarkVotes.Remove(member.m_SteamID);
                _gameResultsVotes.Remove(member.m_SteamID);
                _confessionChoiceVotes.Remove(member.m_SteamID);
                _lairDecisionVotes.Remove(member.m_SteamID);
                _confirmationDialogVotes.Remove(member.m_SteamID);
                _mainMenuActionVotes.Remove(member.m_SteamID);

                if (_pvpModeState != null &&
                    _pvpModeState.Enabled &&
                    _pvpModeState.EnemyControllerSteamId == member.m_SteamID)
                {
                    ApplyLocalPvpModeState(CreatePvpModeState(false, CSteamID.Nil, "enemy-left"));
                    Broadcast(MultiplayerMessageType.PvpModeState, _pvpModeState, true);
                    PublishCurrentInteractionSnapshot();
                }
            }

            BroadcastLobbyState();
        }

        private void OnMessageReceived(SteamTextMessage message)
        {
            MultiplayerEnvelope envelope;
            string error;
            if (!MultiplayerProtocol.TryDeserialize(message.Text, out envelope, out error))
            {
                HostLog.Write("[protocol/reject] Failed to decode message from " +
                    message.SenderName + "/" + message.SenderId.m_SteamID +
                    ": " + error +
                    ". raw=" + TrimProtocolText(message.Text, 240));
                return;
            }

            MarkHostStateReceived(envelope);

            switch (envelope.Type)
            {
                case MultiplayerMessageType.Hello:
                    PrintPayload<HelloPayload>(
                        envelope,
                        payload => "hello protocol=" + payload.ProtocolName +
                            ", protocolVersion=" + payload.ProtocolVersion +
                            ", lobbyVersion=" + (payload.LobbyVersion ?? "[none]") +
                            ", maxSlots=" + payload.MaxHeroSlots);
                    if (_lobby.IsHost)
                    {
                        ValidateHelloCompatibility(envelope);
                        SendFullStateToMember(
                            new CSteamID(envelope.SenderSteamId),
                            envelope.SenderName,
                            null,
                            "hello");
                    }

                    break;
                case MultiplayerMessageType.LobbyState:
                    ApplyLobbyState(envelope);
                    break;
                case MultiplayerMessageType.AssignHeroSlot:
                    AssignHeroSlotPayload slot;
                    if (TryRead(envelope, out slot))
                    {
                        _heroSlots[slot.Slot] = new HeroSlotAssignmentPayload(slot.Slot, slot.SteamId, slot.Name);
                        HostLog.Write("[protocol] " + envelope.SenderName + " assigned slot " + slot.Slot + " to " + slot.Name + "/" + slot.SteamId + ".");
                    }

                    break;
                case MultiplayerMessageType.TurnPrompt:
                    TurnPromptPayload turnPrompt;
                    if (TryRead(envelope, out turnPrompt))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": turn prompt r" + turnPrompt.Round +
                            "/t" + turnPrompt.Turn +
                            ": slot " + turnPrompt.HeroSlot +
                            ", role " + (turnPrompt.ControlRole ?? "[none]") +
                            ", team " + turnPrompt.TeamIndex + ":" + turnPrompt.TeamPosition +
                            ", actor " + turnPrompt.ActorName + " (" + turnPrompt.ActorGuid + ").");

                        HeroSlotAssignmentPayload owner;
                        if (turnPrompt.OwnerSteamId != 0UL)
                        {
                            owner = new HeroSlotAssignmentPayload(
                                turnPrompt.HeroSlot,
                                turnPrompt.OwnerSteamId,
                                turnPrompt.OwnerName);
                        }
                        else
                        {
                            _heroSlots.TryGetValue(turnPrompt.HeroSlot, out owner);
                        }

                        _turnCoordinator.StartTurn(turnPrompt, owner);
                    }

                    break;
                case MultiplayerMessageType.ChooseSkill:
                    ChooseSkillPayload chooseSkill;
                    if (TryRead(envelope, out chooseSkill))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": choose skill: slot " + chooseSkill.HeroSlot +
                            ", actor " + chooseSkill.ActorGuid +
                            ", skill " + chooseSkill.SkillId + ".");

                        if (_lobby.IsHost)
                        {
                            _turnCoordinator.HandleChooseSkill(envelope.SenderSteamId, envelope.SenderName, chooseSkill);
                        }
                    }

                    break;
                case MultiplayerMessageType.ChooseTarget:
                    ChooseTargetPayload chooseTarget;
                    if (TryRead(envelope, out chooseTarget))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": choose target: slot " + chooseTarget.HeroSlot +
                            ", actor " + chooseTarget.ActorGuid +
                            ", target " + chooseTarget.TargetGuid + ".");

                        if (_lobby.IsHost)
                        {
                            _turnCoordinator.HandleChooseTarget(envelope.SenderSteamId, envelope.SenderName, chooseTarget);
                        }
                    }

                    break;
                case MultiplayerMessageType.PassTurn:
                    PassTurnPayload passTurn;
                    if (TryRead(envelope, out passTurn))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": pass turn: slot " + passTurn.HeroSlot +
                            ", actor " + passTurn.ActorGuid + ".");

                        if (_lobby.IsHost)
                        {
                            _turnCoordinator.HandlePassTurn(envelope.SenderSteamId, envelope.SenderName, passTurn);
                        }
                    }

                    break;
                case MultiplayerMessageType.ClearTurn:
                    ClearTurnPayload clearTurn;
                    if (TryRead(envelope, out clearTurn))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": clear turn: slot " + clearTurn.HeroSlot +
                            ", actor " + clearTurn.ActorGuid +
                            ", reason " + (clearTurn.Reason ?? "[none]") + ".");

                        if (!_lobby.IsHost)
                        {
                            _turnCoordinator.ClearTurnIfMatches(clearTurn);
                        }
                    }

                    break;
                case MultiplayerMessageType.CombatSnapshot:
                    CombatSnapshotPayload combatSnapshot;
                    if (TryRead(envelope, out combatSnapshot))
                    {
                        _latestCombatSnapshot = combatSnapshot;
                        LogCombatSnapshotReceived(envelope, combatSnapshot);
                    }

                    break;
                case MultiplayerMessageType.BattleResult:
                    BattleResultPayload battleResult;
                    if (TryRead(envelope, out battleResult))
                    {
                        _latestBattleResult = battleResult;
                        LogBattleResultReceived(envelope, battleResult);
                    }

                    break;
                case MultiplayerMessageType.LootWindowSnapshot:
                    LootWindowSnapshotPayload lootWindowSnapshot;
                    if (TryRead(envelope, out lootWindowSnapshot))
                    {
                        _latestLootWindowSnapshot = lootWindowSnapshot;
                        LogLootWindowSnapshotReceived(envelope, lootWindowSnapshot);
                    }

                    break;
                case MultiplayerMessageType.LootActionRequest:
                    LootActionRequestPayload lootActionRequest;
                    if (TryRead(envelope, out lootActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": loot action request " +
                            (lootActionRequest.Action ?? "[none]") +
                            ", item=" + (lootActionRequest.ItemId ?? "[none]") +
                            ", index=" + lootActionRequest.InventoryIndex +
                            ", qty=" + lootActionRequest.Quantity +
                            ", selected=" + (lootActionRequest.Items == null ? 0 : lootActionRequest.Items.Count) + ".");

                        if (_lobby.IsHost)
                        {
                            HandleLootActionRequest(envelope.SenderSteamId, envelope.SenderName, lootActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.LootActionResult:
                    LootActionResultPayload lootActionResult;
                    if (TryRead(envelope, out lootActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": loot action result " +
                            (lootActionResult.Action ?? "[none]") +
                            ", accepted=" + lootActionResult.Accepted +
                            ", message=" + (lootActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.GameResultsSnapshot:
                    GameResultsSnapshotPayload gameResultsSnapshot;
                    if (TryRead(envelope, out gameResultsSnapshot))
                    {
                        _latestGameResultsSnapshot = gameResultsSnapshot;
                        LogGameResultsSnapshotReceived(envelope, gameResultsSnapshot);
                    }

                    break;
                case MultiplayerMessageType.GameResultsActionRequest:
                    GameResultsActionRequestPayload gameResultsActionRequest;
                    if (TryRead(envelope, out gameResultsActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": game results action request " +
                            (gameResultsActionRequest.Action ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleGameResultsActionRequest(envelope.SenderSteamId, envelope.SenderName, gameResultsActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.GameResultsActionResult:
                    GameResultsActionResultPayload gameResultsActionResult;
                    if (TryRead(envelope, out gameResultsActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": game results action result " +
                            (gameResultsActionResult.Action ?? "[none]") +
                            ", accepted=" + gameResultsActionResult.Accepted +
                            ", message=" + (gameResultsActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.RouteChoiceSnapshot:
                    RouteChoiceSnapshotPayload routeChoiceSnapshot;
                    if (TryRead(envelope, out routeChoiceSnapshot))
                    {
                        _latestRouteChoiceSnapshot = routeChoiceSnapshot;
                        LogRouteChoiceSnapshotReceived(envelope, routeChoiceSnapshot);
                    }

                    break;
                case MultiplayerMessageType.RouteChoiceRequest:
                    RouteChoiceRequestPayload routeChoiceRequest;
                    if (TryRead(envelope, out routeChoiceRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": route choice request option " +
                            routeChoiceRequest.OptionIndex + ".");

                        if (_lobby.IsHost)
                        {
                            HandleRouteChoiceRequest(envelope.SenderSteamId, envelope.SenderName, routeChoiceRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.RouteChoiceResult:
                    RouteChoiceResultPayload routeChoiceResult;
                    if (TryRead(envelope, out routeChoiceResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": route choice result option " +
                            routeChoiceResult.OptionIndex +
                            ", accepted=" + routeChoiceResult.Accepted +
                            ", message=" + (routeChoiceResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.HeroSelectSnapshot:
                    HeroSelectSnapshotPayload heroSelectSnapshot;
                    if (TryRead(envelope, out heroSelectSnapshot))
                    {
                        ApplyHeroSlotOwners(heroSelectSnapshot);
                        _latestHeroSelectSnapshot = heroSelectSnapshot;
                        LogHeroSelectSnapshotReceived(envelope, heroSelectSnapshot);
                    }

                    break;
                case MultiplayerMessageType.HeroSelectRequest:
                    HeroSelectRequestPayload heroSelectRequest;
                    if (TryRead(envelope, out heroSelectRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": hero select request " +
                            (heroSelectRequest.Action ?? "[none]") +
                            ", slotIndex=" + heroSelectRequest.SlotIndex +
                            ", actor=" + (heroSelectRequest.ActorGuid ?? "[none]") +
                            ", path=" + (heroSelectRequest.PathId ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleHeroSelectRequest(envelope.SenderSteamId, envelope.SenderName, heroSelectRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.HeroSelectResult:
                    HeroSelectResultPayload heroSelectResult;
                    if (TryRead(envelope, out heroSelectResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": hero select result " +
                            (heroSelectResult.Action ?? "[none]") +
                            ", slotIndex=" + heroSelectResult.SlotIndex +
                            ", actor=" + (heroSelectResult.ActorGuid ?? "[none]") +
                            ", accepted=" + heroSelectResult.Accepted +
                            ", message=" + (heroSelectResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.HeroLoadoutSnapshot:
                    HeroLoadoutSnapshotPayload heroLoadoutSnapshot;
                    if (TryRead(envelope, out heroLoadoutSnapshot))
                    {
                        ApplyHeroLoadoutOwners(heroLoadoutSnapshot);
                        _latestHeroLoadoutSnapshot = heroLoadoutSnapshot;
                        LogHeroLoadoutSnapshotReceived(envelope, heroLoadoutSnapshot);
                    }

                    break;
                case MultiplayerMessageType.HeroLoadoutRequest:
                    HeroLoadoutRequestPayload heroLoadoutRequest;
                    if (TryRead(envelope, out heroLoadoutRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": hero loadout request " +
                            (heroLoadoutRequest.Action ?? "[none]") +
                            ", slot=" + heroLoadoutRequest.HeroSlot +
                            ", actor=" + (heroLoadoutRequest.ActorGuid ?? "[none]") +
                            ", skill=" + (heroLoadoutRequest.SkillId ?? "[none]") +
                            ", equip=" + heroLoadoutRequest.Equip + ".");

                        if (_lobby.IsHost)
                        {
                            HandleHeroLoadoutRequest(envelope.SenderSteamId, envelope.SenderName, heroLoadoutRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.HeroLoadoutResult:
                    HeroLoadoutResultPayload heroLoadoutResult;
                    if (TryRead(envelope, out heroLoadoutResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": hero loadout result " +
                            (heroLoadoutResult.Action ?? "[none]") +
                            ", slot=" + heroLoadoutResult.HeroSlot +
                            ", actor=" + (heroLoadoutResult.ActorGuid ?? "[none]") +
                            ", skill=" + (heroLoadoutResult.SkillId ?? "[none]") +
                            ", equip=" + heroLoadoutResult.Equip +
                            ", accepted=" + heroLoadoutResult.Accepted +
                            ", message=" + (heroLoadoutResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.RunStateSnapshot:
                    RunStateSnapshotPayload runStateSnapshot;
                    if (TryRead(envelope, out runStateSnapshot))
                    {
                        _latestRunStateSnapshot = runStateSnapshot;
                        LogRunStateSnapshotReceived(envelope, runStateSnapshot);
                    }

                    break;
                case MultiplayerMessageType.ExpeditionOverviewSnapshot:
                    ExpeditionOverviewSnapshotPayload expeditionOverviewSnapshot;
                    if (TryRead(envelope, out expeditionOverviewSnapshot))
                    {
                        _latestExpeditionOverviewSnapshot = expeditionOverviewSnapshot;
                        LogExpeditionOverviewSnapshotReceived(envelope, expeditionOverviewSnapshot);
                    }

                    break;
                case MultiplayerMessageType.MainMenuSnapshot:
                    MainMenuSnapshotPayload mainMenuSnapshot;
                    if (TryRead(envelope, out mainMenuSnapshot))
                    {
                        _latestMainMenuSnapshot = mainMenuSnapshot;
                        LogMainMenuSnapshotReceived(envelope, mainMenuSnapshot);
                    }

                    break;
                case MultiplayerMessageType.MainMenuActionRequest:
                    MainMenuActionRequestPayload mainMenuActionRequest;
                    if (TryRead(envelope, out mainMenuActionRequest))
                    {
                        if (_lobby.IsHost)
                        {
                            HandleMainMenuActionRequest(envelope.SenderSteamId, envelope.SenderName, mainMenuActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.MainMenuActionResult:
                    MainMenuActionResultPayload mainMenuActionResult;
                    if (TryRead(envelope, out mainMenuActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": main menu result " +
                            (mainMenuActionResult.Action ?? "[none]") +
                            ", accepted=" + mainMenuActionResult.Accepted +
                            ", message=" + (mainMenuActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.StoryChoiceSnapshot:
                    StoryChoiceSnapshotPayload storyChoiceSnapshot;
                    if (TryRead(envelope, out storyChoiceSnapshot))
                    {
                        ApplyStoryChoiceOwners(storyChoiceSnapshot);
                        _latestStoryChoiceSnapshot = storyChoiceSnapshot;
                        LogStoryChoiceSnapshotReceived(envelope, storyChoiceSnapshot);
                    }

                    break;
                case MultiplayerMessageType.StoryChoiceRequest:
                    StoryChoiceRequestPayload storyChoiceRequest;
                    if (TryRead(envelope, out storyChoiceRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": story choice request option=" +
                            storyChoiceRequest.OptionIndex +
                            ", slot=" + storyChoiceRequest.HeroSlot +
                            ", actor=" + (storyChoiceRequest.ActorGuid ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleStoryChoiceRequest(envelope.SenderSteamId, envelope.SenderName, storyChoiceRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.StoryChoiceResult:
                    StoryChoiceResultPayload storyChoiceResult;
                    if (TryRead(envelope, out storyChoiceResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": story choice result option=" +
                            storyChoiceResult.OptionIndex +
                            ", slot=" + storyChoiceResult.HeroSlot +
                            ", actor=" + (storyChoiceResult.ActorGuid ?? "[none]") +
                            ", accepted=" + storyChoiceResult.Accepted +
                            ", message=" + (storyChoiceResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.InnSnapshot:
                    InnSnapshotPayload innSnapshot;
                    if (TryRead(envelope, out innSnapshot))
                    {
                        _latestInnSnapshot = innSnapshot;
                        LogInnSnapshotReceived(envelope, innSnapshot);
                    }

                    break;
                case MultiplayerMessageType.InnActionRequest:
                    InnActionRequestPayload innActionRequest;
                    if (TryRead(envelope, out innActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": inn action request " +
                            (innActionRequest.Action ?? "[none]") +
                            ", option=" + innActionRequest.OptionIndex + ".");

                        if (_lobby.IsHost)
                        {
                            HandleInnActionRequest(envelope.SenderSteamId, envelope.SenderName, innActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.InnActionResult:
                    InnActionResultPayload innActionResult;
                    if (TryRead(envelope, out innActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": inn action result " +
                            (innActionResult.Action ?? "[none]") +
                            ", option=" + innActionResult.OptionIndex +
                            ", accepted=" + innActionResult.Accepted +
                            ", message=" + (innActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.EmbarkSnapshot:
                    EmbarkSnapshotPayload embarkSnapshot;
                    if (TryRead(envelope, out embarkSnapshot))
                    {
                        _latestEmbarkSnapshot = embarkSnapshot;
                        LogEmbarkSnapshotReceived(envelope, embarkSnapshot);
                    }

                    break;
                case MultiplayerMessageType.EmbarkActionRequest:
                    EmbarkActionRequestPayload embarkActionRequest;
                    if (TryRead(envelope, out embarkActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": embark action request " +
                            (embarkActionRequest.Action ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleEmbarkActionRequest(envelope.SenderSteamId, envelope.SenderName, embarkActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.EmbarkActionResult:
                    EmbarkActionResultPayload embarkActionResult;
                    if (TryRead(envelope, out embarkActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": embark action result " +
                            (embarkActionResult.Action ?? "[none]") +
                            ", accepted=" + embarkActionResult.Accepted +
                            ", message=" + (embarkActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.AltarSnapshot:
                    AltarSnapshotPayload altarSnapshot;
                    if (TryRead(envelope, out altarSnapshot))
                    {
                        _latestAltarSnapshot = altarSnapshot;
                        LogAltarSnapshotReceived(envelope, altarSnapshot);
                    }

                    break;
                case MultiplayerMessageType.AltarActionRequest:
                    AltarActionRequestPayload altarActionRequest;
                    if (TryRead(envelope, out altarActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": altar action request " +
                            (altarActionRequest.Action ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleAltarActionRequest(envelope.SenderSteamId, envelope.SenderName, altarActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.AltarActionResult:
                    AltarActionResultPayload altarActionResult;
                    if (TryRead(envelope, out altarActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": altar action result " +
                            (altarActionResult.Action ?? "[none]") +
                            ", accepted=" + altarActionResult.Accepted +
                            ", message=" + (altarActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.ConfessionChoiceSnapshot:
                    ConfessionChoiceSnapshotPayload confessionChoiceSnapshot;
                    if (TryRead(envelope, out confessionChoiceSnapshot))
                    {
                        _latestConfessionChoiceSnapshot = confessionChoiceSnapshot;
                        LogConfessionChoiceSnapshotReceived(envelope, confessionChoiceSnapshot);
                    }

                    break;
                case MultiplayerMessageType.ConfessionChoiceRequest:
                    ConfessionChoiceRequestPayload confessionChoiceRequest;
                    if (TryRead(envelope, out confessionChoiceRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": confession choice request option=" +
                            confessionChoiceRequest.OptionIndex +
                            ", boss=" + (confessionChoiceRequest.BossId ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleConfessionChoiceRequest(envelope.SenderSteamId, envelope.SenderName, confessionChoiceRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.ConfessionChoiceResult:
                    ConfessionChoiceResultPayload confessionChoiceResult;
                    if (TryRead(envelope, out confessionChoiceResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": confession choice result option=" +
                            confessionChoiceResult.OptionIndex +
                            ", boss=" + (confessionChoiceResult.BossId ?? "[none]") +
                            ", accepted=" + confessionChoiceResult.Accepted +
                            ", message=" + (confessionChoiceResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.LairDecisionSnapshot:
                    LairDecisionSnapshotPayload lairDecisionSnapshot;
                    if (TryRead(envelope, out lairDecisionSnapshot))
                    {
                        _latestLairDecisionSnapshot = lairDecisionSnapshot;
                        LogLairDecisionSnapshotReceived(envelope, lairDecisionSnapshot);
                    }

                    break;
                case MultiplayerMessageType.LairDecisionRequest:
                    LairDecisionRequestPayload lairDecisionRequest;
                    if (TryRead(envelope, out lairDecisionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": lair decision request " +
                            (lairDecisionRequest.Action ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleLairDecisionRequest(envelope.SenderSteamId, envelope.SenderName, lairDecisionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.LairDecisionResult:
                    LairDecisionResultPayload lairDecisionResult;
                    if (TryRead(envelope, out lairDecisionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": lair decision result " +
                            (lairDecisionResult.Action ?? "[none]") +
                            ", accepted=" + lairDecisionResult.Accepted +
                            ", message=" + (lairDecisionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.ConfirmationDialogSnapshot:
                    ConfirmationDialogSnapshotPayload confirmationDialogSnapshot;
                    if (TryRead(envelope, out confirmationDialogSnapshot))
                    {
                        _latestConfirmationDialogSnapshot = confirmationDialogSnapshot;
                        LogConfirmationDialogSnapshotReceived(envelope, confirmationDialogSnapshot);
                    }

                    break;
                case MultiplayerMessageType.ConfirmationDialogRequest:
                    ConfirmationDialogRequestPayload confirmationDialogRequest;
                    if (TryRead(envelope, out confirmationDialogRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": confirmation dialog request " +
                            (confirmationDialogRequest.Action ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            HandleConfirmationDialogRequest(envelope.SenderSteamId, envelope.SenderName, confirmationDialogRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.ConfirmationDialogResult:
                    ConfirmationDialogResultPayload confirmationDialogResult;
                    if (TryRead(envelope, out confirmationDialogResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": confirmation dialog result " +
                            (confirmationDialogResult.Action ?? "[none]") +
                            ", accepted=" + confirmationDialogResult.Accepted +
                            ", message=" + (confirmationDialogResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.StoreSnapshot:
                    StoreSnapshotPayload storeSnapshot;
                    if (TryRead(envelope, out storeSnapshot))
                    {
                        _latestStoreSnapshot = storeSnapshot;
                        LogStoreSnapshotReceived(envelope, storeSnapshot);
                    }

                    break;
                case MultiplayerMessageType.StoreActionRequest:
                    StoreActionRequestPayload storeActionRequest;
                    if (TryRead(envelope, out storeActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": store action request " +
                            (storeActionRequest.Action ?? "[none]") +
                            ", item=" + (storeActionRequest.ItemId ?? "[none]") +
                            ", index=" + storeActionRequest.InventoryIndex +
                            ", qty=" + storeActionRequest.Quantity + ".");

                        if (_lobby.IsHost)
                        {
                            HandleStoreActionRequest(envelope.SenderSteamId, envelope.SenderName, storeActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.StoreActionResult:
                    StoreActionResultPayload storeActionResult;
                    if (TryRead(envelope, out storeActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": store action result " +
                            (storeActionResult.Action ?? "[none]") +
                            ", item=" + (storeActionResult.ItemId ?? "[none]") +
                            ", index=" + storeActionResult.InventoryIndex +
                            ", accepted=" + storeActionResult.Accepted +
                            ", message=" + (storeActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.StagecoachSnapshot:
                    StagecoachSnapshotPayload stagecoachSnapshot;
                    if (TryRead(envelope, out stagecoachSnapshot))
                    {
                        _latestStagecoachSnapshot = stagecoachSnapshot;
                        LogStagecoachSnapshotReceived(envelope, stagecoachSnapshot);
                    }

                    break;
                case MultiplayerMessageType.StagecoachActionRequest:
                    StagecoachActionRequestPayload stagecoachActionRequest;
                    if (TryRead(envelope, out stagecoachActionRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": stagecoach action request " +
                            (stagecoachActionRequest.Action ?? "[none]") +
                            ", repair=" + (stagecoachActionRequest.RepairKind ?? "[none]") +
                            ", item=" + (stagecoachActionRequest.ItemId ?? "[none]") +
                            ", source=" + stagecoachActionRequest.SourceInventoryIndex +
                            ", target=" + (stagecoachActionRequest.TargetSlotType ?? "[none]") +
                            "[" + stagecoachActionRequest.TargetSlotIndex + "].");

                        if (_lobby.IsHost)
                        {
                            HandleStagecoachActionRequest(envelope.SenderSteamId, envelope.SenderName, stagecoachActionRequest);
                        }
                    }

                    break;
                case MultiplayerMessageType.StagecoachActionResult:
                    StagecoachActionResultPayload stagecoachActionResult;
                    if (TryRead(envelope, out stagecoachActionResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": stagecoach action result " +
                            (stagecoachActionResult.Action ?? "[none]") +
                            ", repair=" + (stagecoachActionResult.RepairKind ?? "[none]") +
                            ", item=" + (stagecoachActionResult.ItemId ?? "[none]") +
                            ", target=" + (stagecoachActionResult.TargetSlotType ?? "[none]") +
                            "[" + stagecoachActionResult.TargetSlotIndex + "]" +
                            ", accepted=" + stagecoachActionResult.Accepted +
                            ", message=" + (stagecoachActionResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.DamageMeterSnapshot:
                    DamageMeterSnapshotPayload damageMeterSnapshot;
                    if (TryRead(envelope, out damageMeterSnapshot))
                    {
                        _latestDamageMeterSnapshot = damageMeterSnapshot;
                        LogDamageMeterSnapshotReceived(envelope, damageMeterSnapshot);
                    }

                    break;
                case MultiplayerMessageType.PvpModeState:
                    PvpModeStatePayload pvpModeState;
                    if (TryRead(envelope, out pvpModeState))
                    {
                        ApplyLocalPvpModeState(pvpModeState);
                    }

                    break;
                case MultiplayerMessageType.CurrentInteractionSnapshot:
                    CurrentInteractionSnapshotPayload currentInteractionSnapshot;
                    if (TryRead(envelope, out currentInteractionSnapshot))
                    {
                        _latestCurrentInteractionSnapshot = currentInteractionSnapshot;
                        LogCurrentInteractionSnapshotReceived(envelope, currentInteractionSnapshot);
                    }

                    break;
                case MultiplayerMessageType.VoteStatus:
                    VoteStatusPayload voteStatus;
                    if (TryRead(envelope, out voteStatus))
                    {
                        ApplyVoteStatus(voteStatus);
                        HostLog.Write("[protocol] " + envelope.SenderName + ": vote status " +
                            (voteStatus.VoteKey ?? "[none]") +
                            " " + voteStatus.VotedCount + "/" + voteStatus.RequiredCount +
                            ", active=" + voteStatus.IsActive +
                            ", resolved=" + voteStatus.IsResolved + ".");
                    }

                    break;
                case MultiplayerMessageType.FullStateRequest:
                    FullStateRequestPayload fullStateRequest;
                    if (TryRead(envelope, out fullStateRequest))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": full state request " +
                            (fullStateRequest.RequestId ?? "[none]") +
                            ", reason=" + (fullStateRequest.Reason ?? "[none]") + ".");

                        if (_lobby.IsHost)
                        {
                            SendFullStateToMember(
                                new CSteamID(envelope.SenderSteamId),
                                envelope.SenderName,
                                fullStateRequest.RequestId,
                                fullStateRequest.Reason);
                        }
                    }

                    break;
                case MultiplayerMessageType.FullStateResult:
                    FullStateResultPayload fullStateResult;
                    if (TryRead(envelope, out fullStateResult))
                    {
                        HostLog.Write("[protocol] " + envelope.SenderName + ": full state result " +
                            (fullStateResult.RequestId ?? "[none]") +
                            ", reason=" + (fullStateResult.Reason ?? "[none]") +
                            ", messages=" + fullStateResult.SentMessages +
                            ", pendingTurn=" + fullStateResult.HasPendingTurn +
                            ", message=" + (fullStateResult.Message ?? "[none]") + ".");
                    }

                    break;
                case MultiplayerMessageType.StateDigest:
                    PrintPayload<StateDigestPayload>(envelope, payload => "state digest " + payload.Label + ": " + payload.Digest);
                    break;
                case MultiplayerMessageType.Chat:
                    PrintPayload<ChatPayload>(envelope, payload => "chat: " + payload.Text);
                    break;
            }
        }

        private void ValidateHelloCompatibility(MultiplayerEnvelope envelope)
        {
            HelloPayload hello;
            if (!TryRead(envelope, out hello))
            {
                return;
            }

            if (!string.Equals(hello.ProtocolName, ProtocolName, StringComparison.Ordinal))
            {
                HostLog.Write("[protocol/warn] " + envelope.SenderName +
                    " reported protocol name " + (hello.ProtocolName ?? "[none]") +
                    "; local protocol name is " + ProtocolName + ".");
            }

            if (hello.ProtocolVersion != 0 && hello.ProtocolVersion != MultiplayerProtocol.CurrentVersion)
            {
                HostLog.Write("[protocol/warn] " + envelope.SenderName +
                    " reported protocol version " + hello.ProtocolVersion +
                    "; local protocol version is " + MultiplayerProtocol.CurrentVersion + ".");
            }

            if (!string.IsNullOrEmpty(hello.LobbyVersion) &&
                !string.Equals(hello.LobbyVersion, SteamLobbyClient.LocalLobbyVersion, StringComparison.Ordinal))
            {
                HostLog.Write("[protocol/warn] " + envelope.SenderName +
                    " reported lobby/component version " + hello.LobbyVersion +
                    "; local version is " + SteamLobbyClient.LocalLobbyVersion + ".");
            }
        }

        private void ApplyLobbyState(MultiplayerEnvelope envelope)
        {
            LobbyStatePayload state;
            if (!TryRead(envelope, out state))
            {
                return;
            }

            _heroSlots.Clear();
            foreach (HeroSlotAssignmentPayload slot in state.HeroSlots)
            {
                _heroSlots[slot.Slot] = slot;
            }

            ApplyHeroSlotOwners(_latestHeroSelectSnapshot);
            ApplyStoryChoiceOwners(_latestStoryChoiceSnapshot);

            HostLog.Write("[protocol] lobby state from " + envelope.SenderName +
                ": members=" + state.Members.Count +
                ", assignedSlots=" + state.HeroSlots.Count + ".");
        }

        private PvpModeStatePayload CreatePvpModeState(bool enabled, CSteamID enemyController, string reason)
        {
            ulong steamId = enabled && enemyController.IsValid() ? enemyController.m_SteamID : 0UL;
            string name = steamId == 0UL ? null : _lobby.GetPersonaName(enemyController);
            string digest = BuildPvpModeDigest(enabled, steamId, reason);
            return new PvpModeStatePayload(
                enabled,
                PvpModeExpeditionEnemyPilot,
                steamId,
                name,
                true,
                true,
                digest);
        }

        private static string BuildPvpModeDigest(bool enabled, ulong enemyControllerSteamId, string reason)
        {
            return (enabled ? "on" : "off") + ":" +
                enemyControllerSteamId + ":" +
                (reason ?? string.Empty);
        }

        private void ApplyLocalPvpModeState(PvpModeStatePayload state)
        {
            if (state == null)
            {
                return;
            }

            _pvpModeState = state;
            _lobby.RefreshMultiplayerRichPresence(state.Enabled);
            if (state.Enabled &&
                state.SuppressHeroSyncForEnemyController &&
                SteamUser.GetSteamID().m_SteamID == state.EnemyControllerSteamId)
            {
                _latestHeroSelectSnapshot = null;
                _latestHeroLoadoutSnapshot = null;
            }

            if (!string.Equals(_lastLoggedPvpModeDigest, state.Digest, StringComparison.Ordinal))
            {
                _lastLoggedPvpModeDigest = state.Digest;
                HostLog.Write("[pvp] mode=" + (state.Enabled ? "enabled" : "disabled") +
                    ", type=" + (state.Mode ?? "[none]") +
                    ", enemyController=" + (state.EnemyControllerName ?? "[none]") +
                    "/" + state.EnemyControllerSteamId +
                    ", runtimeEnemyInput=" + state.RuntimeEnemyInput +
                    ", suppressHeroSync=" + state.SuppressHeroSyncForEnemyController +
                    ", digest=" + (state.Digest ?? "[none]") + ".");
            }
        }

        private void MarkHostStateReceived()
        {
            _lastHostStateReceivedUtc = DateTime.UtcNow;
            _nextAutoResyncUtc = _lastHostStateReceivedUtc.Add(AutoResyncStaleThreshold);
        }

        private void MarkHostStateReceived(MultiplayerEnvelope envelope)
        {
            if (envelope == null || !_lobby.IsInLobby || _lobby.IsHost)
            {
                return;
            }

            if (envelope.SenderSteamId != _lobby.Owner.m_SteamID)
            {
                return;
            }

            MarkHostStateReceived();
        }

        private void ApplyVoteStatus(VoteStatusPayload status)
        {
            if (status == null || string.IsNullOrWhiteSpace(status.VoteKey))
            {
                return;
            }

            _voteStatuses[status.VoteKey] = status;
        }

        private void AssignHeroSlotToMember(int slot, CSteamID member, bool echoSelf)
        {
            AssignHeroSlotPayload payload = new AssignHeroSlotPayload(slot, member.m_SteamID, _lobby.GetPersonaName(member));
            _heroSlots[slot] = new HeroSlotAssignmentPayload(payload.Slot, payload.SteamId, payload.Name);
            Broadcast(MultiplayerMessageType.AssignHeroSlot, payload, echoSelf);
        }

        private int RemovePvpEnemyHeroSlots()
        {
            if (_pvpModeState == null || !_pvpModeState.Enabled || _pvpModeState.EnemyControllerSteamId == 0UL)
            {
                return 0;
            }

            return RemoveHeroSlotsForSteamId(_pvpModeState.EnemyControllerSteamId);
        }

        private int RemoveHeroSlotsForSteamId(ulong steamId)
        {
            if (steamId == 0UL)
            {
                return 0;
            }

            int removed = 0;
            foreach (int slot in _heroSlots
                .Where(pair => pair.Value != null && pair.Value.SteamId == steamId)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _heroSlots.Remove(slot);
                removed++;
            }

            return removed;
        }

        private List<CSteamID> GetSlotAssignmentMembers()
        {
            if (!_lobby.IsInLobby)
            {
                return new List<CSteamID>();
            }

            CSteamID owner = _lobby.Owner;
            return _lobby.GetMembers()
                .Where(member => !IsPvpEnemyController(member.m_SteamID))
                .OrderBy(member => member == owner ? 0 : 1)
                .ThenBy(member => member == owner ? 0UL : member.m_SteamID)
                .ToList();
        }

        private void BroadcastLobbyState()
        {
            if (!_lobby.IsInLobby)
            {
                return;
            }

            Broadcast(MultiplayerMessageType.LobbyState, CreateLobbyStatePayload(), false);
        }

        private LobbyStatePayload CreateLobbyStatePayload()
        {
            List<LobbyMemberPayload> members = _lobby.GetMembers()
                .Select(member => new LobbyMemberPayload(member.m_SteamID, _lobby.GetPersonaName(member), member == _lobby.Owner))
                .ToList();

            List<HeroSlotAssignmentPayload> slots = _heroSlots.Values.OrderBy(slot => slot.Slot).ToList();
            return new LobbyStatePayload(_lobby.CurrentLobby.m_SteamID, _lobby.Owner.m_SteamID, members, slots);
        }

        private void BroadcastFullState(string requestId, string reason)
        {
            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[resync] Full state ignored; no active lobby.");
                return;
            }

            if (!_lobby.IsHost)
            {
                HostLog.Write("[resync] Full state ignored; only lobby owner can broadcast authoritative state.");
                return;
            }

            string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
            int sentMessages = 0;

            BroadcastLobbyState();
            sentMessages++;

            if (_pvpModeState != null)
            {
                Broadcast(MultiplayerMessageType.PvpModeState, _pvpModeState, false);
                sentMessages++;
            }

            TurnPromptPayload prompt;
            HeroSlotAssignmentPayload owner;
            string skillId;
            string targetGuid;
            bool isPass;
            bool hasPendingTurn = _turnCoordinator.TryGetPendingTurn(
                out prompt,
                out owner,
                out skillId,
                out targetGuid,
                out isPass);
            if (hasPendingTurn)
            {
                Broadcast(MultiplayerMessageType.TurnPrompt, prompt, false);
                sentMessages++;
            }

            if (_latestRunStateSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.RunStateSnapshot, _latestRunStateSnapshot, false);
                sentMessages++;
            }

            if (_latestMainMenuSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.MainMenuSnapshot, _latestMainMenuSnapshot, false);
                sentMessages++;
            }

            if (_latestHeroSelectSnapshot != null)
            {
                ApplyHeroSlotOwners(_latestHeroSelectSnapshot);
                Broadcast(MultiplayerMessageType.HeroSelectSnapshot, _latestHeroSelectSnapshot, false);
                sentMessages++;
            }

            if (_latestHeroLoadoutSnapshot != null)
            {
                ApplyHeroLoadoutOwners(_latestHeroLoadoutSnapshot);
                Broadcast(MultiplayerMessageType.HeroLoadoutSnapshot, _latestHeroLoadoutSnapshot, false);
                sentMessages++;
            }

            if (_latestCombatSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.CombatSnapshot, _latestCombatSnapshot, false);
                sentMessages++;
            }

            if (_latestBattleResult != null)
            {
                Broadcast(MultiplayerMessageType.BattleResult, _latestBattleResult, false);
                sentMessages++;
            }

            if (_latestLootWindowSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.LootWindowSnapshot, _latestLootWindowSnapshot, false);
                sentMessages++;
            }

            if (_latestGameResultsSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.GameResultsSnapshot, _latestGameResultsSnapshot, false);
                sentMessages++;
            }

            if (_latestRouteChoiceSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.RouteChoiceSnapshot, _latestRouteChoiceSnapshot, false);
                sentMessages++;
            }

            if (_latestStoryChoiceSnapshot != null)
            {
                ApplyStoryChoiceOwners(_latestStoryChoiceSnapshot);
                Broadcast(MultiplayerMessageType.StoryChoiceSnapshot, _latestStoryChoiceSnapshot, false);
                sentMessages++;
            }

            if (_latestInnSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.InnSnapshot, _latestInnSnapshot, false);
                sentMessages++;
            }

            if (_latestEmbarkSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.EmbarkSnapshot, _latestEmbarkSnapshot, false);
                sentMessages++;
            }

            if (_latestAltarSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.AltarSnapshot, _latestAltarSnapshot, false);
                sentMessages++;
            }

            if (_latestConfessionChoiceSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.ConfessionChoiceSnapshot, _latestConfessionChoiceSnapshot, false);
                sentMessages++;
            }

            if (_latestLairDecisionSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.LairDecisionSnapshot, _latestLairDecisionSnapshot, false);
                sentMessages++;
            }

            if (_latestConfirmationDialogSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.ConfirmationDialogSnapshot, _latestConfirmationDialogSnapshot, false);
                sentMessages++;
            }

            if (_latestStoreSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.StoreSnapshot, _latestStoreSnapshot, false);
                sentMessages++;
            }

            if (_latestStagecoachSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.StagecoachSnapshot, _latestStagecoachSnapshot, false);
                sentMessages++;
            }

            if (_latestDamageMeterSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.DamageMeterSnapshot, _latestDamageMeterSnapshot, false);
                sentMessages++;
            }

            if (_latestCurrentInteractionSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.CurrentInteractionSnapshot, _latestCurrentInteractionSnapshot, false);
                sentMessages++;
            }

            if (_latestExpeditionOverviewSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.ExpeditionOverviewSnapshot, _latestExpeditionOverviewSnapshot, false);
                sentMessages++;
            }

            foreach (VoteStatusPayload status in _voteStatuses.Values.ToList())
            {
                Broadcast(MultiplayerMessageType.VoteStatus, status, false);
                sentMessages++;
            }

            string message = "resent cached host state";
            Broadcast(
                MultiplayerMessageType.FullStateResult,
                new FullStateResultPayload(requestId, normalizedReason, sentMessages, hasPendingTurn, message),
                true);

            HostLog.Write("[resync] Full state broadcast complete: reason=" + normalizedReason +
                ", messages=" + sentMessages +
                ", pendingTurn=" + hasPendingTurn + ".");
        }

        private void SendFullStateToMember(CSteamID target, string targetName, string requestId, string reason)
        {
            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[resync] Full state ignored; no active lobby.");
                return;
            }

            if (!_lobby.IsHost)
            {
                HostLog.Write("[resync] Full state ignored; only lobby owner can send authoritative state.");
                return;
            }

            if (!target.IsValid())
            {
                HostLog.Write("[resync] Full state ignored; invalid target member.");
                return;
            }

            if (!_lobby.GetMembers().Any(member => member == target))
            {
                HostLog.Write("[resync] Full state ignored; target is not in lobby: " +
                    (targetName ?? "[unknown]") + "/" + target.m_SteamID + ".");
                return;
            }

            string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
            string normalizedName = string.IsNullOrWhiteSpace(targetName) ? _lobby.GetPersonaName(target) : targetName;
            int sentMessages = 0;

            if (SendToMember(target, MultiplayerMessageType.LobbyState, CreateLobbyStatePayload()))
            {
                sentMessages++;
            }

            if (_pvpModeState != null && SendToMember(target, MultiplayerMessageType.PvpModeState, _pvpModeState))
            {
                sentMessages++;
            }

            TurnPromptPayload prompt;
            HeroSlotAssignmentPayload owner;
            string skillId;
            string targetGuid;
            bool isPass;
            bool hasPendingTurn = _turnCoordinator.TryGetPendingTurn(
                out prompt,
                out owner,
                out skillId,
                out targetGuid,
                out isPass);
            if (hasPendingTurn && SendToMember(target, MultiplayerMessageType.TurnPrompt, prompt))
            {
                sentMessages++;
            }

            sentMessages += SendCachedToMember(target, MultiplayerMessageType.RunStateSnapshot, _latestRunStateSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.ExpeditionOverviewSnapshot, _latestExpeditionOverviewSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.MainMenuSnapshot, _latestMainMenuSnapshot);

            if (_latestHeroSelectSnapshot != null)
            {
                ApplyHeroSlotOwners(_latestHeroSelectSnapshot);
                sentMessages += SendCachedToMember(target, MultiplayerMessageType.HeroSelectSnapshot, _latestHeroSelectSnapshot);
            }

            if (_latestHeroLoadoutSnapshot != null)
            {
                ApplyHeroLoadoutOwners(_latestHeroLoadoutSnapshot);
                sentMessages += SendCachedToMember(target, MultiplayerMessageType.HeroLoadoutSnapshot, _latestHeroLoadoutSnapshot);
            }

            sentMessages += SendCachedToMember(target, MultiplayerMessageType.CombatSnapshot, _latestCombatSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.BattleResult, _latestBattleResult);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.LootWindowSnapshot, _latestLootWindowSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.GameResultsSnapshot, _latestGameResultsSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.RouteChoiceSnapshot, _latestRouteChoiceSnapshot);

            if (_latestStoryChoiceSnapshot != null)
            {
                ApplyStoryChoiceOwners(_latestStoryChoiceSnapshot);
                sentMessages += SendCachedToMember(target, MultiplayerMessageType.StoryChoiceSnapshot, _latestStoryChoiceSnapshot);
            }

            sentMessages += SendCachedToMember(target, MultiplayerMessageType.InnSnapshot, _latestInnSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.EmbarkSnapshot, _latestEmbarkSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.AltarSnapshot, _latestAltarSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.ConfessionChoiceSnapshot, _latestConfessionChoiceSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.LairDecisionSnapshot, _latestLairDecisionSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.ConfirmationDialogSnapshot, _latestConfirmationDialogSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.StoreSnapshot, _latestStoreSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.StagecoachSnapshot, _latestStagecoachSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.DamageMeterSnapshot, _latestDamageMeterSnapshot);
            sentMessages += SendCachedToMember(target, MultiplayerMessageType.CurrentInteractionSnapshot, _latestCurrentInteractionSnapshot);

            foreach (VoteStatusPayload status in _voteStatuses.Values.ToList())
            {
                sentMessages += SendCachedToMember(target, MultiplayerMessageType.VoteStatus, status);
            }

            string message = "resent cached host state to " + normalizedName;
            SendToMember(
                target,
                MultiplayerMessageType.FullStateResult,
                new FullStateResultPayload(requestId, normalizedReason, sentMessages, hasPendingTurn, message));

            HostLog.Write("[resync] Full state sent to " + normalizedName + "/" + target.m_SteamID +
                ": reason=" + normalizedReason +
                ", messages=" + sentMessages +
                ", pendingTurn=" + hasPendingTurn + ".");
        }

        private int SendCachedToMember<TPayload>(CSteamID target, MultiplayerMessageType type, TPayload payload)
            where TPayload : class
        {
            if (payload == null)
            {
                return 0;
            }

            if (ShouldSkipForPvpEnemyController(type, target, payload))
            {
                return 0;
            }

            return SendToMember(target, type, payload) ? 1 : 0;
        }

        private void PublishVoteContext(string voteKey, string contextDigest, bool isActive)
        {
            IList<CSteamID> voters = isActive ? GetCurrentVoters() : Array.Empty<CSteamID>();
            PublishVoteStatusEntries(
                voteKey,
                contextDigest,
                isActive,
                false,
                null,
                voters,
                new List<VoteEntryPayload>());
        }

        private void PublishVoteStatus<TPayload>(
            string voteKey,
            string contextDigest,
            Dictionary<ulong, VoteRecord<TPayload>> votes,
            IList<CSteamID> voters,
            bool isResolved,
            string resolution,
            Func<VoteRecord<TPayload>, string> formatChoice)
            where TPayload : class
        {
            PublishVoteStatusEntries(
                voteKey,
                contextDigest,
                true,
                isResolved,
                resolution,
                voters,
                BuildVoteEntries(votes, voters, formatChoice));
        }

        private void PublishVoteStatusEntries(
            string voteKey,
            string contextDigest,
            bool isActive,
            bool isResolved,
            string resolution,
            IList<CSteamID> voters,
            IList<VoteEntryPayload> voteEntries)
        {
            if (!_lobby.IsHost || !_lobby.IsInLobby || string.IsNullOrWhiteSpace(voteKey))
            {
                return;
            }

            IList<CSteamID> voterList = voters ?? Array.Empty<CSteamID>();
            IList<VoteEntryPayload> votes = voteEntries ?? Array.Empty<VoteEntryPayload>();
            HashSet<ulong> votedIds = new HashSet<ulong>(votes.Select(vote => vote.SteamId));
            List<VoteEntryPayload> missing = isActive && !isResolved
                ? voterList
                    .Where(voter => !votedIds.Contains(voter.m_SteamID))
                    .Select(voter => new VoteEntryPayload(voter.m_SteamID, _lobby.GetPersonaName(voter), "[waiting]"))
                    .ToList()
                : new List<VoteEntryPayload>();

            VoteStatusPayload status = new VoteStatusPayload(
                voteKey,
                contextDigest,
                isActive,
                isResolved,
                resolution,
                votes.Count,
                isActive ? voterList.Count : 0,
                votes.ToList(),
                missing);

            _voteStatuses[voteKey] = status;
            Broadcast(MultiplayerMessageType.VoteStatus, status, false);
            PublishCurrentInteractionSnapshot();
        }

        private void PublishForcedVoteStatus(
            string voteKey,
            string contextDigest,
            ulong senderSteamId,
            string senderName,
            string choice,
            string resolution)
        {
            List<CSteamID> voters = GetCurrentVoters();
            List<VoteEntryPayload> entries = new List<VoteEntryPayload>
            {
                new VoteEntryPayload(senderSteamId, senderName, choice),
            };
            PublishVoteStatusEntries(voteKey, contextDigest, true, true, resolution, voters, entries);
        }

        private void PublishCurrentInteractionSnapshot()
        {
            if (!_lobby.IsHost || !_lobby.IsInLobby)
            {
                return;
            }

            CurrentInteractionSnapshotPayload snapshot = BuildCurrentInteractionSnapshot();
            if (snapshot == null)
            {
                return;
            }

            if (_latestCurrentInteractionSnapshot != null &&
                string.Equals(_latestCurrentInteractionSnapshot.Digest, snapshot.Digest, StringComparison.Ordinal))
            {
                return;
            }

            _latestCurrentInteractionSnapshot = snapshot;
            Broadcast(MultiplayerMessageType.CurrentInteractionSnapshot, snapshot, false);
        }

        private CurrentInteractionSnapshotPayload BuildCurrentInteractionSnapshot()
        {
            List<CurrentInteractionItemPayload> items = new List<CurrentInteractionItemPayload>();

            TurnPromptPayload prompt;
            HeroSlotAssignmentPayload owner;
            string skillId;
            string targetGuid;
            bool isPass;
            if (_turnCoordinator.TryGetPendingTurn(out prompt, out owner, out skillId, out targetGuid, out isPass) && prompt != null)
            {
                AddCurrentInteractionItem(
                    items,
                    10,
                    "combat_turn",
                    "Combat Turn",
                    "r" + prompt.Round + "/t" + prompt.Turn +
                    ", role=" + (prompt.ControlRole ?? "hero") +
                    ", team=" + prompt.TeamIndex + ":" + prompt.TeamPosition +
                    ", slot=" + prompt.HeroSlot +
                    ", actor=" + (prompt.ActorName ?? prompt.ActorGuid ?? "[actor]") +
                    ", owner=" + (owner == null ? "unassigned" : owner.Name ?? "[owner]") +
                    ", skill=" + (skillId ?? "[none]") +
                    ", target=" + (targetGuid ?? "[none]") +
                    ", pass=" + isPass,
                    "Combat",
                    null,
                    true,
                    prompt.ActorGuid);
            }

            ConfirmationDialogSnapshotPayload dialog = _latestConfirmationDialogSnapshot;
            if (dialog != null && dialog.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    dialog.IsAllowed ? 20 : 95,
                    "dialog",
                    "Dialog",
                    (dialog.Kind ?? "[dialog]") +
                    ", type=" + (dialog.DialogType ?? "[none]") +
                    ", confirm=" + dialog.CanConfirm +
                    ", decline=" + dialog.CanDecline +
                    (dialog.IsAllowed ? string.Empty : ", blocked=" + (dialog.BlockReason ?? "[blocked]")),
                    "Decisions",
                    dialog.IsAllowed ? VoteKeyConfirmationDialog : null,
                    dialog.IsAllowed && (dialog.CanConfirm || dialog.CanDecline),
                    dialog.Digest);
            }

            MainMenuSnapshotPayload mainMenu = _latestMainMenuSnapshot;
            if (mainMenu != null && mainMenu.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    30,
                    "main_menu",
                    "Main Menu",
                    "save=" + mainMenu.HasExpeditionSave +
                    ", continue=" + mainMenu.CanContinueExpedition +
                    ", new=" + mainMenu.CanStartNewExpedition,
                    "Run",
                    VoteKeyMainMenu,
                    mainMenu.CanContinueExpedition || mainMenu.CanStartNewExpedition,
                    mainMenu.Digest);
            }

            HeroSelectSnapshotPayload heroSelect = _latestHeroSelectSnapshot;
            if (heroSelect != null && heroSelect.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    35,
                    "hero_select",
                    "Hero Select",
                    "confirmed=" + heroSelect.RosterConfirmed +
                    ", canConfirm=" + heroSelect.CanConfirm +
                    ", slots=" + CountItems(heroSelect.Slots) +
                    ", heroes=" + CountItems(heroSelect.Heroes),
                    "Run",
                    VoteKeyHeroReady,
                    heroSelect.CanConfirm || !heroSelect.RosterConfirmed,
                    heroSelect.Digest);
            }

            LootWindowSnapshotPayload loot = _latestLootWindowSnapshot;
            if (loot != null && loot.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    40,
                    "loot",
                    "Loot",
                    "reason=" + (loot.Reason ?? "[none]") +
                    ", items=" + CountItems(loot.Items) +
                    ", takeAll=" + loot.CanTakeAll,
                    "Rewards",
                    VoteKeyLoot,
                    true,
                    loot.Digest);
            }

            GameResultsSnapshotPayload results = _latestGameResultsSnapshot;
            if (results != null && results.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    42,
                    "game_results",
                    "Results",
                    "reason=" + (results.GameOverReason ?? "[none]") +
                    ", hasScore=" + results.HasScore +
                    ", continue=" + results.CanContinue,
                    "Rewards",
                    VoteKeyGameResults,
                    results.CanContinue,
                    results.Digest);
            }

            RouteChoiceSnapshotPayload route = _latestRouteChoiceSnapshot;
            if (route != null && route.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    45,
                    "route",
                    "Route",
                    "choices=" + CountItems(route.Choices) +
                    ", selected=" + route.SelectedOptionIndex,
                    "Decisions",
                    VoteKeyRoute,
                    CountItems(route.Choices) > 0,
                    route.Digest);
            }

            StoryChoiceSnapshotPayload story = _latestStoryChoiceSnapshot;
            if (story != null && story.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    46,
                    "story",
                    "Story",
                    "type=" + (story.StoryType ?? "[none]") +
                    ", choices=" + CountItems(story.Choices) +
                    ", selected=" + (story.SelectedActorGuid ?? "[none]"),
                    "Decisions",
                    VoteKeyStory,
                    CountItems(story.Choices) > 0,
                    story.Digest);
            }

            InnSnapshotPayload inn = _latestInnSnapshot;
            if (inn != null && inn.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    48,
                    "inn",
                    "Inn",
                    "state=" + (inn.InnState ?? "[none]") +
                    ", choices=" + CountItems(inn.BiomeChoices) +
                    ", selected=" + inn.SelectedBiomeChoiceIndex +
                    ", embark=" + inn.CanEmbark,
                    "Decisions",
                    inn.CanEmbark ? VoteKeyInnEmbark : VoteKeyInnBiome,
                    CountItems(inn.BiomeChoices) > 0 || inn.CanEmbark,
                    inn.Digest);
            }

            EmbarkSnapshotPayload embark = _latestEmbarkSnapshot;
            if (embark != null && embark.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    50,
                    "embark",
                    "Embark",
                    "next=" + (embark.NextBiomeName ?? embark.NextBiomeType ?? "[none]") +
                    ", relationships=" + embark.RelationshipCount +
                    ", applied=" + embark.HasRelationshipsApplied +
                    ", continue=" + embark.CanContinue,
                    "Decisions",
                    VoteKeyEmbarkContinue,
                    embark.CanContinue,
                    embark.Digest);
            }

            AltarSnapshotPayload altar = _latestAltarSnapshot;
            if (altar != null && altar.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    52,
                    "altar",
                    "Altar",
                    "screen=" + (altar.ActiveSubscreen ?? "[none]") +
                    ", candles=" + altar.CandleCount +
                    ", tracks=" + CountItems(altar.Tracks) +
                    ", rewards=" + CountItems(altar.RewardButtons) +
                    ", leave=" + altar.CanEmbark,
                    "Decisions",
                    VoteKeyAltarEmbark,
                    altar.CanEmbark || CountItems(altar.Tracks) > 0 || CountItems(altar.RewardButtons) > 0,
                    altar.Digest);
            }

            ConfessionChoiceSnapshotPayload confession = _latestConfessionChoiceSnapshot;
            if (confession != null && confession.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    54,
                    "confession",
                    "Confession",
                    "choices=" + CountItems(confession.Choices) +
                    ", selected=" + (confession.SelectedBossId ?? "[none]") +
                    ", canChoose=" + confession.CanChoose,
                    "Decisions",
                    VoteKeyConfessionChoice,
                    confession.CanChoose,
                    confession.Digest);
            }

            LairDecisionSnapshotPayload lair = _latestLairDecisionSnapshot;
            if (lair != null && lair.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    56,
                    "lair",
                    "Lair",
                    "battle=" + lair.CurrentBattleIndex + "->" + lair.NextBattleIndex +
                    "/" + lair.TotalBattles +
                    ", continue=" + lair.CanContinue +
                    ", retreat=" + lair.CanRetreat,
                    "Decisions",
                    VoteKeyLairDecision,
                    lair.CanContinue || lair.CanRetreat,
                    lair.Digest);
            }

            CombatSnapshotPayload combat = _latestCombatSnapshot;
            if (combat != null && (combat.PartyInBattle || CountItems(combat.Actors) > 0))
            {
                string selectedSkill = combat.SelectedSkill == null
                    ? string.Empty
                    : ", selected=" + (combat.SelectedSkill.DisplayName ?? combat.SelectedSkill.SkillId ?? "[skill]") +
                        " targets=" + CountItems(combat.SelectedSkill.ValidTargets);
                AddCurrentInteractionItem(
                    items,
                    60,
                    "combat_state",
                    "Combat State",
                    "state=" + (combat.BattleState ?? "[none]") +
                    ", r" + combat.Round + "/t" + combat.Turn +
                    ", current=" + (combat.CurrentActorName ?? combat.CurrentActorGuid ?? "[none]") +
                    ", order=" + CountItems(combat.TurnOrder) +
                    selectedSkill,
                    "Combat",
                    null,
                    false,
                    combat.Digest);
            }

            StoreSnapshotPayload store = _latestStoreSnapshot;
            if (store != null && store.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    70,
                    "store",
                    "Store",
                    "kind=" + (store.StoreKind ?? "[none]") +
                    ", items=" + CountItems(store.Items),
                    "Rewards",
                    null,
                    CountItems(store.Items) > 0,
                    store.Digest);
            }

            StagecoachSnapshotPayload stagecoach = _latestStagecoachSnapshot;
            if (stagecoach != null && stagecoach.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    72,
                    "stagecoach",
                    "Stagecoach",
                    "editable=" + stagecoach.IsEditable +
                    ", armor=" + stagecoach.Armor + "/" + stagecoach.MaxArmor +
                    ", wheels=" + stagecoach.Wheels + "/" + stagecoach.MaxWheels +
                    ", slots=" + CountItems(stagecoach.Slots),
                    "Coach",
                    null,
                    stagecoach.IsEditable,
                    stagecoach.Digest);
            }

            HeroLoadoutSnapshotPayload loadout = _latestHeroLoadoutSnapshot;
            if (loadout != null && loadout.IsActive)
            {
                AddCurrentInteractionItem(
                    items,
                    74,
                    "loadout",
                    "Loadout",
                    "scope=" + (loadout.Scope ?? "[none]") +
                    ", mode=" + (loadout.CurrentGameMode ?? "[none]") +
                    ", mastery=" + loadout.HeroUpgradePoints +
                    ", trainer=" + loadout.CanMasterSkills +
                    ", actors=" + CountItems(loadout.Actors),
                    "Loadout",
                    null,
                    CountItems(loadout.Actors) > 0,
                    loadout.Digest);
            }

            List<CurrentInteractionItemPayload> ordered = items
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Label ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            CurrentInteractionItemPayload primary = ordered.FirstOrDefault(item => item.IsActionable) ?? ordered.FirstOrDefault();
            List<string> activeVoteKeys = _voteStatuses.Values
                .Where(status => status != null && status.IsActive)
                .Select(status => status.VoteKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            CurrentInteractionSnapshotPayload snapshot = new CurrentInteractionSnapshotPayload
            {
                IsActive = ordered.Count > 0,
                PrimaryKind = primary == null ? null : primary.Kind,
                PrimaryLabel = primary == null ? null : primary.Label,
                PrimarySummary = primary == null ? null : primary.Summary,
                PrimaryTargetTab = primary == null ? null : primary.TargetTab,
                PrimaryVoteKey = primary == null ? null : primary.VoteKey,
                Items = ordered,
                ActiveVoteKeys = activeVoteKeys,
            };
            snapshot.Digest = ComputeCurrentInteractionDigest(snapshot);
            return snapshot;
        }

        private void AddCurrentInteractionItem(
            IList<CurrentInteractionItemPayload> items,
            int priority,
            string kind,
            string label,
            string summary,
            string targetTab,
            string voteKey,
            bool isActionable,
            string sourceDigest)
        {
            if (items == null)
            {
                return;
            }

            items.Add(new CurrentInteractionItemPayload
            {
                Priority = priority,
                Kind = kind,
                Label = label,
                Summary = summary,
                TargetTab = targetTab,
                VoteKey = voteKey,
                IsActionable = isActionable,
                HasVote = HasActiveVote(voteKey),
                SourceDigest = CompactInteractionSourceDigest(sourceDigest),
            });
        }

        private bool HasActiveVote(string voteKey)
        {
            VoteStatusPayload status;
            return !string.IsNullOrWhiteSpace(voteKey) &&
                _voteStatuses.TryGetValue(voteKey, out status) &&
                status != null &&
                status.IsActive;
        }

        private static int CountItems<T>(ICollection<T> items)
        {
            return items == null ? 0 : items.Count;
        }

        private static string ComputeCurrentInteractionDigest(CurrentInteractionSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "current-interaction:none";
            }

            string itemDigest = string.Join("|", (snapshot.Items ?? new List<CurrentInteractionItemPayload>())
                .Select(item => string.Join(":",
                    item.Priority,
                    item.Kind ?? string.Empty,
                    item.Label ?? string.Empty,
                    item.Summary ?? string.Empty,
                    item.TargetTab ?? string.Empty,
                    item.VoteKey ?? string.Empty,
                    item.IsActionable ? "1" : "0",
                    item.HasVote ? "1" : "0",
                    item.SourceDigest ?? string.Empty))
                .ToArray());
            string voteDigest = string.Join("|", (snapshot.ActiveVoteKeys ?? new List<string>()).ToArray());
            return "current-interaction:" +
                (snapshot.IsActive ? "1" : "0") + ";" +
                (snapshot.PrimaryKind ?? string.Empty) + ";" +
                itemDigest + ";" +
                voteDigest;
        }

        private static string CompactInteractionSourceDigest(string sourceDigest)
        {
            if (string.IsNullOrWhiteSpace(sourceDigest))
            {
                return sourceDigest;
            }

            return sourceDigest.Length <= 32
                ? sourceDigest
                : ComputeStableDigest(sourceDigest);
        }

        private static string FormatLoggedDigest(string digest)
        {
            if (string.IsNullOrEmpty(digest))
            {
                return digest ?? string.Empty;
            }

            return digest.Length <= 96
                ? digest
                : ComputeStableDigest(digest) + " len=" + digest.Length;
        }

        private static string ComputeStableDigest(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string source = text ?? string.Empty;
                for (int i = 0; i < source.Length; i++)
                {
                    hash ^= source[i];
                    hash *= 1099511628211UL;
                }

                return hash.ToString("x16");
            }
        }

        private List<VoteEntryPayload> BuildVoteEntries<TPayload>(
            Dictionary<ulong, VoteRecord<TPayload>> votes,
            IList<CSteamID> voters,
            Func<VoteRecord<TPayload>, string> formatChoice)
            where TPayload : class
        {
            List<VoteEntryPayload> entries = new List<VoteEntryPayload>();
            if (votes == null || voters == null)
            {
                return entries;
            }

            foreach (CSteamID voter in voters)
            {
                VoteRecord<TPayload> vote;
                if (!votes.TryGetValue(voter.m_SteamID, out vote))
                {
                    continue;
                }

                entries.Add(new VoteEntryPayload(
                    voter.m_SteamID,
                    string.IsNullOrWhiteSpace(vote.SenderName) ? _lobby.GetPersonaName(voter) : vote.SenderName,
                    formatChoice == null ? "[voted]" : formatChoice(vote)));
            }

            return entries;
        }

        private void ApplyHeroSlotOwners(HeroSelectSnapshotPayload snapshot)
        {
            if (snapshot == null || snapshot.Slots == null)
            {
                return;
            }

            for (int i = 0; i < snapshot.Slots.Count; i++)
            {
                HeroSelectSlotPayload slot = snapshot.Slots[i];
                if (slot == null)
                {
                    continue;
                }

                HeroSlotAssignmentPayload owner;
                if (_heroSlots.TryGetValue(slot.HeroSlot, out owner))
                {
                    slot.OwnerSteamId = owner.SteamId;
                    slot.OwnerName = owner.Name;
                }
                else
                {
                    slot.OwnerSteamId = 0UL;
                    slot.OwnerName = null;
                }
            }
        }

        private void ApplyHeroLoadoutOwners(HeroLoadoutSnapshotPayload snapshot)
        {
            if (snapshot == null || snapshot.Actors == null)
            {
                return;
            }

            for (int i = 0; i < snapshot.Actors.Count; i++)
            {
                HeroLoadoutActorPayload actor = snapshot.Actors[i];
                if (actor == null)
                {
                    continue;
                }

                HeroSlotAssignmentPayload owner;
                if (actor.HeroSlot > 0 && _heroSlots.TryGetValue(actor.HeroSlot, out owner))
                {
                    actor.OwnerSteamId = owner.SteamId;
                    actor.OwnerName = owner.Name;
                }
                else
                {
                    actor.OwnerSteamId = 0UL;
                    actor.OwnerName = null;
                }
            }
        }

        private void ApplyStoryChoiceOwners(StoryChoiceSnapshotPayload snapshot)
        {
            if (snapshot == null || snapshot.Choices == null)
            {
                return;
            }

            for (int i = 0; i < snapshot.Choices.Count; i++)
            {
                StoryChoiceOptionPayload choice = snapshot.Choices[i];
                if (choice == null)
                {
                    continue;
                }

                HeroSlotAssignmentPayload owner;
                if (choice.HeroSlot > 0 && _heroSlots.TryGetValue(choice.HeroSlot, out owner))
                {
                    choice.OwnerSteamId = owner.SteamId;
                    choice.OwnerName = owner.Name;
                }
                else
                {
                    choice.OwnerSteamId = 0UL;
                    choice.OwnerName = null;
                }
            }
        }

        private void OnTurnCleared(ClearTurnPayload payload)
        {
            if (!_lobby.IsHost || payload == null)
            {
                return;
            }

            Broadcast(MultiplayerMessageType.ClearTurn, payload, true);
            PublishCurrentInteractionSnapshot();
        }

        private void LogCombatSnapshotReceived(MultiplayerEnvelope envelope, CombatSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedCombatSnapshotDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedCombatSnapshotDigest = digest;
            int actorCount = snapshot.Actors == null ? 0 : snapshot.Actors.Count;
            HostLog.Write("[snapshot] " + envelope.SenderName +
                ": state=" + (snapshot.BattleState ?? "[none]") +
                ", round=" + snapshot.Round +
                ", turn=" + snapshot.Turn +
                ", actors=" + actorCount +
                ", digest=" + digest + ".");
        }

        private void LogBattleResultReceived(MultiplayerEnvelope envelope, BattleResultPayload payload)
        {
            string digest = payload == null ? null : payload.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedBattleResultDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedBattleResultDigest = digest;
            int rewardCount = payload.LootRewards == null ? 0 : payload.LootRewards.Count;
            HostLog.Write("[result] " + envelope.SenderName +
                ": complete=" + payload.IsFightComplete +
                ", sequenceComplete=" + payload.IsBattleSequenceComplete +
                ", reason=" + (payload.LootReason ?? "[none]") +
                ", rewards=" + rewardCount +
                ", digest=" + digest + ".");
        }

        private void LogLootWindowSnapshotReceived(MultiplayerEnvelope envelope, LootWindowSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedLootWindowDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedLootWindowDigest = digest;
            int itemCount = snapshot.Items == null ? 0 : snapshot.Items.Count;
            HostLog.Write("[loot-window] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", reason=" + (snapshot.Reason ?? "[none]") +
                ", items=" + itemCount +
                ", digest=" + digest + ".");
        }

        private void LogGameResultsSnapshotReceived(MultiplayerEnvelope envelope, GameResultsSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedGameResultsDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedGameResultsDigest = digest;
            HostLog.Write("[game-results] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", reason=" + (snapshot.GameOverReason ?? "[none]") +
                ", hasScore=" + snapshot.HasScore +
                ", canContinue=" + snapshot.CanContinue +
                ", digest=" + digest + ".");
        }

        private void LogRouteChoiceSnapshotReceived(MultiplayerEnvelope envelope, RouteChoiceSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedRouteChoiceDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedRouteChoiceDigest = digest;
            int choiceCount = snapshot.Choices == null ? 0 : snapshot.Choices.Count;
            HostLog.Write("[route] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", choices=" + choiceCount +
                ", selected=" + snapshot.SelectedOptionIndex +
                ", digest=" + digest + ".");
        }

        private void LogHeroSelectSnapshotReceived(MultiplayerEnvelope envelope, HeroSelectSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedHeroSelectDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedHeroSelectDigest = digest;
            int slotCount = snapshot.Slots == null ? 0 : snapshot.Slots.Count;
            int heroCount = snapshot.Heroes == null ? 0 : snapshot.Heroes.Count;
            HostLog.Write("[hero-select] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", confirmed=" + snapshot.RosterConfirmed +
                ", slots=" + slotCount +
                ", heroes=" + heroCount +
                ", digest=" + digest + ".");
        }

        private void LogHeroLoadoutSnapshotReceived(MultiplayerEnvelope envelope, HeroLoadoutSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedHeroLoadoutDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedHeroLoadoutDigest = digest;
            int actorCount = snapshot.Actors == null ? 0 : snapshot.Actors.Count;
            int skillCount = snapshot.Actors == null
                ? 0
                : snapshot.Actors.Sum(actor => actor == null || actor.Skills == null ? 0 : actor.Skills.Count);
            HostLog.Write("[hero-loadout] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", scope=" + (snapshot.Scope ?? "[none]") +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", mastery=" + snapshot.HeroUpgradePoints +
                ", trainer=" + snapshot.CanMasterSkills +
                ", actors=" + actorCount +
                ", skills=" + skillCount +
                ", digest=" + FormatLoggedDigest(digest) + ".");
        }

        private void LogRunStateSnapshotReceived(MultiplayerEnvelope envelope, RunStateSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedRunStateDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedRunStateDigest = digest;
            int partyCount = snapshot.Party == null ? 0 : snapshot.Party.Count;
            HostLog.Write("[run-state] " + envelope.SenderName +
                ": mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", gameType=" + (snapshot.CurrentGameType ?? "[none]") +
                ", runStarted=" + snapshot.IsRunStarted +
                ", map=" + (snapshot.MapState ?? "[none]") +
                ", biome=" + (snapshot.BiomeType ?? "[none]") +
                ", party=" + partyCount +
                ", digest=" + digest + ".");
        }

        private void LogExpeditionOverviewSnapshotReceived(MultiplayerEnvelope envelope, ExpeditionOverviewSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedExpeditionOverviewDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedExpeditionOverviewDigest = digest;
            int heroCount = snapshot.Heroes == null ? 0 : snapshot.Heroes.Count;
            int inventoryCount = snapshot.InventoryItems == null ? 0 : snapshot.InventoryItems.Count;
            int stagecoachCount = snapshot.StagecoachItems == null ? 0 : snapshot.StagecoachItems.Count;
            HostLog.Write("[overview] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", gameType=" + (snapshot.CurrentGameType ?? "[none]") +
                ", relics=" + snapshot.Relics +
                ", baubles=" + snapshot.Baubles +
                ", mastery=" + snapshot.MasteryPoints +
                ", torch=" + snapshot.Torch + "/" + snapshot.TorchMax +
                ", loathing=" + snapshot.Loathing + "/" + snapshot.LoathingMax +
                ", heroes=" + heroCount +
                ", inventory=" + inventoryCount +
                ", coachItems=" + stagecoachCount +
                ", digest=" + digest + ".");
        }

        private void LogMainMenuSnapshotReceived(MultiplayerEnvelope envelope, MainMenuSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedMainMenuDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedMainMenuDigest = digest;
            HostLog.Write("[main-menu] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", profile=" + (snapshot.ProfileName ?? "[none]") +
                ", save=" + snapshot.HasExpeditionSave +
                ", canContinue=" + snapshot.CanContinueExpedition +
                ", canNew=" + snapshot.CanStartNewExpedition +
                ", digest=" + digest + ".");
        }

        private void LogStoryChoiceSnapshotReceived(MultiplayerEnvelope envelope, StoryChoiceSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedStoryChoiceDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedStoryChoiceDigest = digest;
            int choiceCount = snapshot.Choices == null ? 0 : snapshot.Choices.Count;
            HostLog.Write("[story-choice] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", storyType=" + (snapshot.StoryType ?? "[none]") +
                ", state=" + (snapshot.StoryState ?? "[none]") +
                ", choices=" + choiceCount +
                ", selected=" + (snapshot.SelectedActorGuid ?? "[none]") +
                ", digest=" + digest + ".");
        }

        private void LogInnSnapshotReceived(MultiplayerEnvelope envelope, InnSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedInnDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedInnDigest = digest;
            int choiceCount = snapshot.BiomeChoices == null ? 0 : snapshot.BiomeChoices.Count;
            HostLog.Write("[inn] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", state=" + (snapshot.InnState ?? "[none]") +
                ", choices=" + choiceCount +
                ", selected=" + snapshot.SelectedBiomeChoiceIndex +
                ", canEmbark=" + snapshot.CanEmbark +
                ", digest=" + digest + ".");
        }

        private void LogEmbarkSnapshotReceived(MultiplayerEnvelope envelope, EmbarkSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedEmbarkDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedEmbarkDigest = digest;
            HostLog.Write("[embark] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", biome=" + (snapshot.NextBiomeName ?? snapshot.NextBiomeType ?? "[none]") +
                ", relationships=" + snapshot.RelationshipCount +
                ", applied=" + snapshot.HasRelationshipsApplied +
                ", applying=" + snapshot.IsApplyingRelationships +
                ", canContinue=" + snapshot.CanContinue +
                ", digest=" + digest + ".");
        }

        private void LogAltarSnapshotReceived(MultiplayerEnvelope envelope, AltarSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedAltarDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedAltarDigest = digest;
            int trackCount = snapshot.Tracks == null ? 0 : snapshot.Tracks.Count;
            int rewardCount = snapshot.RewardButtons == null ? 0 : snapshot.RewardButtons.Count;
            HostLog.Write("[altar] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", screen=" + (snapshot.ActiveSubscreen ?? "[none]") +
                ", intro=" + snapshot.IsIntro +
                ", candles=" + snapshot.CandleCount +
                ", tracks=" + trackCount +
                ", rewards=" + rewardCount +
                ", exiting=" + snapshot.IsExiting +
                ", canEmbark=" + snapshot.CanEmbark +
                ", digest=" + digest + ".");
        }

        private void LogConfessionChoiceSnapshotReceived(MultiplayerEnvelope envelope, ConfessionChoiceSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedConfessionChoiceDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedConfessionChoiceDigest = digest;
            int choiceCount = snapshot.Choices == null ? 0 : snapshot.Choices.Count;
            HostLog.Write("[confession] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", screen=" + (snapshot.ScreenState ?? "[none]") +
                ", choices=" + choiceCount +
                ", selected=" + snapshot.SelectedOptionIndex + "/" + (snapshot.SelectedBossId ?? "[none]") +
                ", canChoose=" + snapshot.CanChoose +
                ", digest=" + digest + ".");
        }

        private void LogLairDecisionSnapshotReceived(MultiplayerEnvelope envelope, LairDecisionSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedLairDecisionDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedLairDecisionDigest = digest;
            HostLog.Write("[lair] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", source=" + (snapshot.CombatSource ?? "[none]") +
                ", current=" + (snapshot.CurrentBattleConfigurationId ?? "[none]") +
                ", next=" + (snapshot.NextBattleConfigurationId ?? "[none]") +
                ", index=" + snapshot.CurrentBattleIndex + "->" + snapshot.NextBattleIndex + "/" + snapshot.TotalBattles +
                ", canContinue=" + snapshot.CanContinue +
                ", canRetreat=" + snapshot.CanRetreat +
                ", digest=" + digest + ".");
        }

        private void LogConfirmationDialogSnapshotReceived(MultiplayerEnvelope envelope, ConfirmationDialogSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedConfirmationDialogDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedConfirmationDialogDigest = digest;
            HostLog.Write("[dialog] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", allowed=" + snapshot.IsAllowed +
                ", kind=" + (snapshot.Kind ?? "[none]") +
                ", type=" + (snapshot.DialogType ?? "[none]") +
                ", canConfirm=" + snapshot.CanConfirm +
                ", canDecline=" + snapshot.CanDecline +
                ", digest=" + digest + ".");
        }

        private void LogStoreSnapshotReceived(MultiplayerEnvelope envelope, StoreSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedStoreDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedStoreDigest = digest;
            int itemCount = snapshot.Items == null ? 0 : snapshot.Items.Count;
            HostLog.Write("[store] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", kind=" + (snapshot.StoreKind ?? "[none]") +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", items=" + itemCount +
                ", digest=" + digest + ".");
        }

        private void LogStagecoachSnapshotReceived(MultiplayerEnvelope envelope, StagecoachSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedStagecoachDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedStagecoachDigest = digest;
            int playerItemCount = snapshot.PlayerItems == null ? 0 : snapshot.PlayerItems.Count;
            int slotCount = snapshot.Slots == null ? 0 : snapshot.Slots.Count;
            HostLog.Write("[stagecoach] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", editable=" + snapshot.IsEditable +
                ", mode=" + (snapshot.CurrentGameMode ?? "[none]") +
                ", state=" + (snapshot.ScreenState ?? "[none]") +
                ", armor=" + snapshot.Armor + "/" + snapshot.MaxArmor +
                ", wheels=" + snapshot.Wheels + "/" + snapshot.MaxWheels +
                ", playerItems=" + playerItemCount +
                ", slots=" + slotCount +
                ", digest=" + digest + ".");
        }

        private void LogDamageMeterSnapshotReceived(MultiplayerEnvelope envelope, DamageMeterSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedDamageMeterDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedDamageMeterDigest = digest;
            int heroCount = snapshot.Heroes == null ? 0 : snapshot.Heroes.Count;
            int enemyCount = snapshot.Enemies == null ? 0 : snapshot.Enemies.Count;
            int contributionCount = snapshot.Contributions == null ? 0 : snapshot.Contributions.Count;
            HostLog.Write("[damage-meter] " + envelope.SenderName +
                ": available=" + snapshot.IsAvailable +
                ", active=" + snapshot.IsActive +
                ", round=" + snapshot.Round +
                ", turn=" + snapshot.Turn +
                ", state=" + (snapshot.BattleState ?? "[none]") +
                ", heroes=" + heroCount +
                ", enemies=" + enemyCount +
                ", contributions=" + contributionCount +
                ", reason=" + (snapshot.UnavailableReason ?? "[none]") +
                ", digest=" + digest + ".");
        }

        private void LogCurrentInteractionSnapshotReceived(MultiplayerEnvelope envelope, CurrentInteractionSnapshotPayload snapshot)
        {
            string digest = snapshot == null ? null : snapshot.Digest;
            if (string.IsNullOrEmpty(digest) || string.Equals(_lastLoggedCurrentInteractionDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedCurrentInteractionDigest = digest;
            int itemCount = snapshot.Items == null ? 0 : snapshot.Items.Count;
            int voteCount = snapshot.ActiveVoteKeys == null ? 0 : snapshot.ActiveVoteKeys.Count;
            HostLog.Write("[interaction] " + envelope.SenderName +
                ": active=" + snapshot.IsActive +
                ", primary=" + (snapshot.PrimaryKind ?? "[none]") +
                ", label=" + (snapshot.PrimaryLabel ?? "[none]") +
                ", items=" + itemCount +
                ", activeVotes=" + voteCount +
                ", digest=" + FormatLoggedDigest(digest) + ".");
        }

        private void HandleLootActionRequest(ulong senderSteamId, string senderName, LootActionRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<LootActionRequestPayload> resolvedVote;
            bool accepted = TryRecordLootActionVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            LootActionResultPayload result = new LootActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.LootActionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "loot action adapter is not available";
            accepted = _lootActionAdapter != null &&
                _lootActionAdapter.TryExecuteLootAction(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new LootActionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.LootActionResult, result, true);
        }

        private void HandleRouteChoiceRequest(ulong senderSteamId, string senderName, RouteChoiceRequestPayload request)
        {
            if (request != null && request.IsForced)
            {
                HandleForcedRouteChoiceRequest(senderSteamId, senderName, request);
                return;
            }

            string message;
            string resolutionMessage;
            VoteRecord<RouteChoiceRequestPayload> resolvedVote;
            bool accepted = TryRecordRouteChoiceVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            RouteChoiceResultPayload result = new RouteChoiceResultPayload(
                request == null ? null : request.RequestId,
                request == null ? -1 : request.OptionIndex,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.RouteChoiceResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "route choice adapter is not available";
            accepted = _routeChoiceAdapter != null &&
                _routeChoiceAdapter.TryExecuteRouteChoice(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new RouteChoiceResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? -1 : resolvedVote.Payload.OptionIndex,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.RouteChoiceResult, result, true);
        }

        private void HandleForcedRouteChoiceRequest(ulong senderSteamId, string senderName, RouteChoiceRequestPayload request)
        {
            string message = ValidateRouteChoiceVote(request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = ValidateChoiceOverruleRequest(senderSteamId, senderName, VoteKeyRoute);
                accepted = string.IsNullOrEmpty(message);
            }

            if (accepted)
            {
                message = "route choice adapter is not available";
                accepted = _routeChoiceAdapter != null &&
                    _routeChoiceAdapter.TryExecuteRouteChoice(request, senderSteamId, senderName, out message);
            }

            if (accepted)
            {
                ConsumeChoiceOverruleUse();
                _routeChoiceResolvedDigest = _routeChoiceVoteDigest;
                _routeChoiceVotes.Clear();
                string resolution = "route overruled by " + FormatVoteSender(senderName, senderSteamId) +
                    ": option " + request.OptionIndex;
                PublishForcedVoteStatus(
                    VoteKeyRoute,
                    _routeChoiceVoteDigest,
                    senderSteamId,
                    senderName,
                    "option " + request.OptionIndex,
                    resolution);
                message = resolution + "; " + message;
            }

            RouteChoiceResultPayload result = new RouteChoiceResultPayload(
                request == null ? null : request.RequestId,
                request == null ? -1 : request.OptionIndex,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.RouteChoiceResult, result, true);
        }

        private void HandleHeroSelectRequest(ulong senderSteamId, string senderName, HeroSelectRequestPayload request)
        {
            string action = request == null || request.Action == null
                ? string.Empty
                : request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "ready", StringComparison.Ordinal))
            {
                HandleHeroSelectReadyRequest(senderSteamId, senderName, request);
                return;
            }

            string message = ValidateHeroSelectRequestOwner(senderSteamId, request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = "hero select adapter is not available";
                accepted = _heroSelectAdapter != null &&
                    _heroSelectAdapter.TryExecuteHeroSelectRequest(request, senderSteamId, senderName, out message);
            }

            HeroSelectResultPayload result = new HeroSelectResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                request == null ? -1 : request.SlotIndex,
                request == null ? null : request.ActorGuid,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.HeroSelectResult, result, true);
        }

        private void HandleHeroSelectReadyRequest(ulong senderSteamId, string senderName, HeroSelectRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<HeroSelectRequestPayload> resolvedVote;
            bool accepted = TryRecordHeroSelectReadyVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            HeroSelectResultPayload result = new HeroSelectResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                -1,
                null,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.HeroSelectResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            HeroSelectRequestPayload confirmRequest = new HeroSelectRequestPayload(
                resolvedVote.Payload == null ? Guid.NewGuid().ToString("N") : resolvedVote.Payload.RequestId,
                "confirm",
                -1,
                null);
            message = "hero select adapter is not available";
            accepted = _heroSelectAdapter != null &&
                _heroSelectAdapter.TryExecuteHeroSelectRequest(
                    confirmRequest,
                    _lobby.Owner.m_SteamID,
                    _lobby.GetPersonaName(_lobby.Owner),
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new HeroSelectResultPayload(
                confirmRequest.RequestId,
                confirmRequest.Action,
                -1,
                null,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.HeroSelectResult, result, true);
        }

        private void HandleHeroLoadoutRequest(ulong senderSteamId, string senderName, HeroLoadoutRequestPayload request)
        {
            string message = ValidateHeroLoadoutRequestOwner(senderSteamId, request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = "hero loadout adapter is not available";
                accepted = _heroLoadoutAdapter != null &&
                    _heroLoadoutAdapter.TryExecuteHeroLoadoutRequest(request, senderSteamId, senderName, out message);
            }

            HeroLoadoutResultPayload result = new HeroLoadoutResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                request == null ? 0 : request.HeroSlot,
                request == null ? null : request.ActorGuid,
                request == null ? null : request.SkillId,
                request != null && request.Equip,
                senderSteamId,
                senderName,
                accepted,
                message);
            if (request != null)
            {
                HostLog.Write("[hero-loadout-result] " + senderName + "/" + senderSteamId +
                    " action=" + (request.Action ?? "[none]") +
                    ", slot=" + request.HeroSlot +
                    ", actor=" + (request.ActorGuid ?? "[none]") +
                    ", accepted=" + accepted +
                    ", message=" + (message ?? string.Empty) + ".");
            }

            Broadcast(MultiplayerMessageType.HeroLoadoutResult, result, true);
        }

        private void HandleStoryChoiceRequest(ulong senderSteamId, string senderName, StoryChoiceRequestPayload request)
        {
            if (request != null && request.IsForced)
            {
                HandleForcedStoryChoiceRequest(senderSteamId, senderName, request);
                return;
            }

            string message;
            string resolutionMessage;
            VoteRecord<StoryChoiceRequestPayload> resolvedVote;
            bool accepted = TryRecordStoryChoiceVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            StoryChoiceResultPayload result = new StoryChoiceResultPayload(
                request == null ? null : request.RequestId,
                request == null ? -1 : request.OptionIndex,
                request == null ? 0 : request.HeroSlot,
                request == null ? null : request.ActorGuid,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.StoryChoiceResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "story choice adapter is not available";
            accepted = _storyChoiceAdapter != null &&
                _storyChoiceAdapter.TryExecuteStoryChoice(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new StoryChoiceResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? -1 : resolvedVote.Payload.OptionIndex,
                resolvedVote.Payload == null ? 0 : resolvedVote.Payload.HeroSlot,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.ActorGuid,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.StoryChoiceResult, result, true);
        }

        private void HandleForcedStoryChoiceRequest(ulong senderSteamId, string senderName, StoryChoiceRequestPayload request)
        {
            string message = ValidateStoryChoiceVote(request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = ValidateChoiceOverruleRequest(senderSteamId, senderName, VoteKeyStory);
                accepted = string.IsNullOrEmpty(message);
            }

            if (accepted)
            {
                message = "story choice adapter is not available";
                accepted = _storyChoiceAdapter != null &&
                    _storyChoiceAdapter.TryExecuteStoryChoice(request, senderSteamId, senderName, out message);
            }

            if (accepted)
            {
                ConsumeChoiceOverruleUse();
                _storyChoiceResolvedDigest = _storyChoiceVoteDigest;
                _storyChoiceVotes.Clear();
                string resolution = "story overruled by " + FormatVoteSender(senderName, senderSteamId) +
                    ": option " + request.OptionIndex +
                    ", slot " + request.HeroSlot;
                PublishForcedVoteStatus(
                    VoteKeyStory,
                    _storyChoiceVoteDigest,
                    senderSteamId,
                    senderName,
                    StoryChoiceVoteKey(request),
                    resolution);
                message = resolution + "; " + message;
            }

            StoryChoiceResultPayload result = new StoryChoiceResultPayload(
                request == null ? null : request.RequestId,
                request == null ? -1 : request.OptionIndex,
                request == null ? 0 : request.HeroSlot,
                request == null ? null : request.ActorGuid,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.StoryChoiceResult, result, true);
        }

        private void HandleInnActionRequest(ulong senderSteamId, string senderName, InnActionRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<InnActionRequestPayload> resolvedVote;
            bool accepted = TryRecordInnActionVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            InnActionResultPayload result = new InnActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                request == null ? -1 : request.OptionIndex,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.InnActionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "inn action adapter is not available";
            accepted = _innActionAdapter != null &&
                _innActionAdapter.TryExecuteInnAction(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new InnActionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.Payload == null ? -1 : resolvedVote.Payload.OptionIndex,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.InnActionResult, result, true);
        }

        private void HandleEmbarkActionRequest(ulong senderSteamId, string senderName, EmbarkActionRequestPayload request)
        {
            string action = request == null || request.Action == null
                ? string.Empty
                : request.Action.Trim().ToLowerInvariant();

            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                HandleEmbarkContinueVote(senderSteamId, senderName, request);
                return;
            }

            string message = ValidateEmbarkActionRequest(senderSteamId, request, false);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = "embark action adapter is not available";
                accepted = _embarkActionAdapter != null &&
                    _embarkActionAdapter.TryExecuteEmbarkAction(request, senderSteamId, senderName, out message);
            }

            EmbarkActionResultPayload result = new EmbarkActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.EmbarkActionResult, result, true);
        }

        private void HandleEmbarkContinueVote(ulong senderSteamId, string senderName, EmbarkActionRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<EmbarkActionRequestPayload> resolvedVote;
            bool accepted = TryRecordEmbarkContinueVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            EmbarkActionResultPayload result = new EmbarkActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.EmbarkActionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "embark action adapter is not available";
            accepted = _embarkActionAdapter != null &&
                _embarkActionAdapter.TryExecuteEmbarkAction(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new EmbarkActionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.EmbarkActionResult, result, true);
        }

        private void HandleAltarActionRequest(ulong senderSteamId, string senderName, AltarActionRequestPayload request)
        {
            string action = NormalizeAltarAction(request);
            if (!string.Equals(action, "embark", StringComparison.Ordinal))
            {
                HandleAltarDirectActionRequest(senderSteamId, senderName, request);
                return;
            }

            string message;
            string resolutionMessage;
            VoteRecord<AltarActionRequestPayload> resolvedVote;
            bool accepted = TryRecordAltarEmbarkVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            AltarActionResultPayload result = new AltarActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.AltarActionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "altar action adapter is not available";
            accepted = _altarActionAdapter != null &&
                _altarActionAdapter.TryExecuteAltarAction(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new AltarActionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.AltarActionResult, result, true);
        }

        private void HandleAltarDirectActionRequest(ulong senderSteamId, string senderName, AltarActionRequestPayload request)
        {
            string message = ValidateAltarDirectActionRequest(request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = "altar action adapter is not available";
                accepted = _altarActionAdapter != null &&
                    _altarActionAdapter.TryExecuteAltarAction(request, senderSteamId, senderName, out message);
            }

            AltarActionResultPayload result = new AltarActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            HostLog.Write("[altar-result] " + senderName + "/" + senderSteamId +
                " action=" + (request == null ? "[none]" : request.Action ?? "[none]") +
                ", accepted=" + accepted +
                ", message=" + (message ?? string.Empty) + ".");
            Broadcast(MultiplayerMessageType.AltarActionResult, result, true);
        }

        private void HandleGameResultsActionRequest(ulong senderSteamId, string senderName, GameResultsActionRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<GameResultsActionRequestPayload> resolvedVote;
            bool accepted = TryRecordGameResultsVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            GameResultsActionResultPayload result = new GameResultsActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.GameResultsActionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "game results action adapter is not available";
            accepted = _gameResultsActionAdapter != null &&
                _gameResultsActionAdapter.TryExecuteGameResultsAction(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new GameResultsActionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.GameResultsActionResult, result, true);
        }

        private void HandleConfessionChoiceRequest(ulong senderSteamId, string senderName, ConfessionChoiceRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<ConfessionChoiceRequestPayload> resolvedVote;
            bool accepted = TryRecordConfessionChoiceVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            ConfessionChoiceResultPayload result = new ConfessionChoiceResultPayload(
                request == null ? null : request.RequestId,
                request == null ? -1 : request.OptionIndex,
                request == null ? null : request.BossId,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.ConfessionChoiceResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "confession choice adapter is not available";
            accepted = _confessionChoiceAdapter != null &&
                _confessionChoiceAdapter.TryExecuteConfessionChoice(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new ConfessionChoiceResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? -1 : resolvedVote.Payload.OptionIndex,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.BossId,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.ConfessionChoiceResult, result, true);
        }

        private void HandleLairDecisionRequest(ulong senderSteamId, string senderName, LairDecisionRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<LairDecisionRequestPayload> resolvedVote;
            bool accepted = TryRecordLairDecisionVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            LairDecisionResultPayload result = new LairDecisionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.LairDecisionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "lair decision adapter is not available";
            accepted = _lairDecisionAdapter != null &&
                _lairDecisionAdapter.TryExecuteLairDecision(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new LairDecisionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.LairDecisionResult, result, true);
        }

        private void HandleConfirmationDialogRequest(ulong senderSteamId, string senderName, ConfirmationDialogRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<ConfirmationDialogRequestPayload> resolvedVote;
            bool accepted = TryRecordConfirmationDialogVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);

            ConfirmationDialogResultPayload result = new ConfirmationDialogResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.ConfirmationDialogResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "confirmation dialog adapter is not available";
            accepted = _confirmationDialogAdapter != null &&
                _confirmationDialogAdapter.TryExecuteConfirmationDialog(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new ConfirmationDialogResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.ConfirmationDialogResult, result, true);
        }

        private void HandleMainMenuActionRequest(ulong senderSteamId, string senderName, MainMenuActionRequestPayload request)
        {
            string message;
            string resolutionMessage;
            VoteRecord<MainMenuActionRequestPayload> resolvedVote;
            bool accepted = TryRecordMainMenuActionVote(
                senderSteamId,
                senderName,
                request,
                out message,
                out resolvedVote,
                out resolutionMessage);
            MainMenuActionResultPayload result = new MainMenuActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                senderSteamId,
                senderName,
                accepted,
                message);
            HostLog.Write("[main-menu-result] " + senderName + "/" + senderSteamId +
                " action=" + (request == null ? "[none]" : request.Action ?? "[none]") +
                ", accepted=" + accepted +
                ", message=" + (message ?? string.Empty) + ".");
            Broadcast(MultiplayerMessageType.MainMenuActionResult, result, true);

            if (!accepted || resolvedVote == null)
            {
                return;
            }

            message = "main menu action adapter is not available";
            accepted = _mainMenuActionAdapter != null &&
                _mainMenuActionAdapter.TryExecuteMainMenuAction(
                    resolvedVote.Payload,
                    resolvedVote.SenderSteamId,
                    resolvedVote.SenderName,
                    out message);

            if (!string.IsNullOrWhiteSpace(resolutionMessage))
            {
                message = resolutionMessage + "; " + message;
            }

            result = new MainMenuActionResultPayload(
                resolvedVote.Payload == null ? null : resolvedVote.Payload.RequestId,
                resolvedVote.Payload == null ? null : resolvedVote.Payload.Action,
                resolvedVote.SenderSteamId,
                resolvedVote.SenderName,
                accepted,
                message);
            Broadcast(MultiplayerMessageType.MainMenuActionResult, result, true);
        }

        private string ValidateMainMenuActionRequest(MainMenuActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty main menu action request";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "continue", StringComparison.Ordinal) &&
                !string.Equals(action, "start_new", StringComparison.Ordinal) &&
                !string.Equals(action, "new", StringComparison.Ordinal))
            {
                return "unsupported main menu action: " + request.Action;
            }

            if (_latestMainMenuSnapshot == null || !_latestMainMenuSnapshot.IsActive)
            {
                return "main menu snapshot is not active";
            }

            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                return _latestMainMenuSnapshot.CanContinueExpedition
                    ? string.Empty
                    : "continue expedition is blocked: " +
                        (_latestMainMenuSnapshot.BlockReason ?? "[blocked]");
            }

            if (_latestMainMenuSnapshot.HasExpeditionSave)
            {
                return "start new expedition is host-only while an expedition save exists";
            }

            return _latestMainMenuSnapshot.CanStartNewExpedition
                ? string.Empty
                : "start new expedition is blocked: " +
                    (_latestMainMenuSnapshot.BlockReason ?? "[blocked]");
        }

        private bool TryRecordMainMenuActionVote(
            ulong senderSteamId,
            string senderName,
            MainMenuActionRequestPayload request,
            out string message,
            out VoteRecord<MainMenuActionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncMainMenuVoteContext(_latestMainMenuSnapshot);
            string validation = ValidateMainMenuActionRequest(request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            if (string.IsNullOrEmpty(_mainMenuVoteDigest))
            {
                message = "main menu vote context is not ready";
                return false;
            }

            if (string.Equals(_mainMenuResolvedDigest, _mainMenuVoteDigest, StringComparison.Ordinal))
            {
                message = "main menu vote already resolved";
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for main menu vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "main menu vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_mainMenuActionVotes, voters);
            bool updated = _mainMenuActionVotes.ContainsKey(senderSteamId);
            _mainMenuActionVotes[senderSteamId] =
                new VoteRecord<MainMenuActionRequestPayload>(senderSteamId, senderName, request);

            int votedCount = voters.Count(voter => _mainMenuActionVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "main menu vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": " + FormatMainMenuAction(request) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyMainMenu, _mainMenuVoteDigest, _mainMenuActionVotes, voters, false, null, FormatMainMenuChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_mainMenuActionVotes, voters, FormatMainMenuChoice);
            resolvedVote = ResolveMainMenuActionVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyMainMenu, _mainMenuVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private void HandleStoreActionRequest(ulong senderSteamId, string senderName, StoreActionRequestPayload request)
        {
            string message = ValidateStoreActionRequest(request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = "store action adapter is not available";
                accepted = _storeActionAdapter != null &&
                    _storeActionAdapter.TryExecuteStoreAction(request, senderSteamId, senderName, out message);
            }

            StoreActionResultPayload result = new StoreActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                request == null ? -1 : request.InventoryIndex,
                request == null ? null : request.ItemId,
                request == null ? 0 : request.Quantity,
                senderSteamId,
                senderName,
                accepted,
                message);
            HostLog.Write("[store-result] " + senderName + "/" + senderSteamId +
                " action=" + (request == null ? "[none]" : request.Action ?? "[none]") +
                ", item=" + (request == null ? "[none]" : request.ItemId ?? "[none]") +
                ", index=" + (request == null ? -1 : request.InventoryIndex) +
                ", accepted=" + accepted +
                ", message=" + (message ?? string.Empty) + ".");
            Broadcast(MultiplayerMessageType.StoreActionResult, result, true);
        }

        private string ValidateStoreActionRequest(StoreActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty store action request";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "buy", StringComparison.Ordinal) &&
                !string.Equals(action, "purchase", StringComparison.Ordinal))
            {
                return "unsupported store action: " + request.Action;
            }

            if (request.Quantity != 1)
            {
                return "store purchase currently supports quantity 1 only";
            }

            if (_latestStoreSnapshot == null || !_latestStoreSnapshot.IsActive)
            {
                return "store snapshot is not active";
            }

            StoreItemPayload item = (_latestStoreSnapshot.Items ?? Array.Empty<StoreItemPayload>())
                .FirstOrDefault(candidate => candidate != null && candidate.InventoryIndex == request.InventoryIndex);
            if (item == null)
            {
                return "store item index " + request.InventoryIndex + " is not in the current snapshot";
            }

            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(item.ItemId, request.ItemId, StringComparison.Ordinal))
            {
                return "store item changed at index " + request.InventoryIndex +
                    ": expected " + request.ItemId +
                    ", snapshot has " + (item.ItemId ?? "[none]");
            }

            if (item.Quantity <= 0)
            {
                return "store item " + (item.ItemId ?? "[item]") + " is out of stock";
            }

            if (!item.CanAfford)
            {
                return "cannot afford store item " + (item.ItemId ?? "[item]") +
                    " price=" + (item.PriceText ?? "[unknown]");
            }

            return string.Empty;
        }

        private void HandleStagecoachActionRequest(
            ulong senderSteamId,
            string senderName,
            StagecoachActionRequestPayload request)
        {
            string message = ValidateStagecoachActionRequest(request);
            bool accepted = string.IsNullOrEmpty(message);
            if (accepted)
            {
                message = "stagecoach action adapter is not available";
                accepted = _stagecoachActionAdapter != null &&
                    _stagecoachActionAdapter.TryExecuteStagecoachAction(request, senderSteamId, senderName, out message);
            }

            StagecoachActionResultPayload result = new StagecoachActionResultPayload(
                request == null ? null : request.RequestId,
                request == null ? null : request.Action,
                request == null ? null : request.RepairKind,
                request == null ? null : request.TargetSlotType,
                request == null ? -1 : request.TargetSlotIndex,
                request == null ? null : request.ItemId,
                senderSteamId,
                senderName,
                accepted,
                message);
            HostLog.Write("[stagecoach-result] " + senderName + "/" + senderSteamId +
                " action=" + (request == null ? "[none]" : request.Action ?? "[none]") +
                ", repair=" + (request == null ? "[none]" : request.RepairKind ?? "[none]") +
                ", item=" + (request == null ? "[none]" : request.ItemId ?? "[none]") +
                ", target=" + (request == null ? "[none]" : request.TargetSlotType ?? "[none]") +
                "[" + (request == null ? -1 : request.TargetSlotIndex) + "]" +
                ", accepted=" + accepted +
                ", message=" + (message ?? string.Empty) + ".");
            Broadcast(MultiplayerMessageType.StagecoachActionResult, result, true);
        }

        private string ValidateStagecoachActionRequest(StagecoachActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty stagecoach action request";
            }

            if (_latestStagecoachSnapshot == null || !_latestStagecoachSnapshot.IsActive)
            {
                return "stagecoach snapshot is not active";
            }

            if (!_latestStagecoachSnapshot.IsEditable)
            {
                return "stagecoach snapshot is not editable";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "repair", StringComparison.Ordinal))
            {
                StagecoachRepairPayload repair = FindStagecoachRepair(request.RepairKind);
                if (repair == null)
                {
                    return "stagecoach repair kind is not in the current snapshot: " +
                        (request.RepairKind ?? "[none]");
                }

                if (!repair.CanRepair)
                {
                    return "stagecoach " + (repair.RepairKind ?? "[repair]") + " cannot be repaired now";
                }

                if (!repair.CanAfford)
                {
                    return "cannot afford stagecoach " + (repair.RepairKind ?? "[repair]") +
                        " repair cost=" + (repair.CostText ?? "[unknown]");
                }

                return string.Empty;
            }

            if (string.Equals(action, "equip_item", StringComparison.Ordinal) ||
                string.Equals(action, "equip", StringComparison.Ordinal))
            {
                StagecoachItemPayload sourceItem = (_latestStagecoachSnapshot.PlayerItems ?? Array.Empty<StagecoachItemPayload>())
                    .FirstOrDefault(candidate => candidate != null &&
                        candidate.InventoryIndex == request.SourceInventoryIndex);
                if (sourceItem == null)
                {
                    return "player inventory item index " + request.SourceInventoryIndex +
                        " is not in the current stagecoach snapshot";
                }

                if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                    !string.Equals(sourceItem.ItemId, request.ItemId, StringComparison.Ordinal))
                {
                    return "player inventory item changed at index " + request.SourceInventoryIndex +
                        ": expected " + request.ItemId +
                        ", snapshot has " + (sourceItem.ItemId ?? "[none]");
                }

                if (!sourceItem.CanEquip)
                {
                    return "stagecoach item " + (sourceItem.ItemId ?? "[item]") +
                        " is not currently equippable";
                }

                StagecoachSlotPayload targetSlot = FindStagecoachSlot(request.TargetSlotType, request.TargetSlotIndex);
                if (targetSlot == null)
                {
                    return "target stagecoach slot is not in the current snapshot: " +
                        (request.TargetSlotType ?? "[none]") +
                        "[" + request.TargetSlotIndex + "]";
                }

                if (!string.Equals(sourceItem.SlotType, targetSlot.SlotType, StringComparison.Ordinal))
                {
                    return "stagecoach item " + (sourceItem.ItemId ?? "[item]") +
                        " belongs to " + (sourceItem.SlotType ?? "[none]") +
                        ", not " + (targetSlot.SlotType ?? "[none]");
                }

                if (targetSlot.Item != null && !targetSlot.CanUnequip)
                {
                    return "target stagecoach slot cannot return its current item";
                }

                return string.Empty;
            }

            if (string.Equals(action, "unequip_item", StringComparison.Ordinal) ||
                string.Equals(action, "unequip", StringComparison.Ordinal))
            {
                StagecoachSlotPayload targetSlot = FindStagecoachSlot(request.TargetSlotType, request.TargetSlotIndex);
                if (targetSlot == null)
                {
                    return "target stagecoach slot is not in the current snapshot: " +
                        (request.TargetSlotType ?? "[none]") +
                        "[" + request.TargetSlotIndex + "]";
                }

                if (targetSlot.Item == null)
                {
                    return "target stagecoach slot is empty";
                }

                if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                    !string.Equals(targetSlot.Item.ItemId, request.ItemId, StringComparison.Ordinal))
                {
                    return "target stagecoach slot changed: expected " + request.ItemId +
                        ", snapshot has " + (targetSlot.Item.ItemId ?? "[none]");
                }

                if (!targetSlot.CanUnequip)
                {
                    return "stagecoach item " + (targetSlot.Item.ItemId ?? "[item]") +
                        " cannot be unequipped now";
                }

                return string.Empty;
            }

            return "unsupported stagecoach action: " + request.Action;
        }

        private StagecoachRepairPayload FindStagecoachRepair(string repairKind)
        {
            string normalized = string.IsNullOrWhiteSpace(repairKind)
                ? string.Empty
                : repairKind.Trim().ToLowerInvariant();
            if (normalized == "armor" || normalized == "armour")
            {
                return _latestStagecoachSnapshot == null ? null : _latestStagecoachSnapshot.ArmorRepair;
            }

            if (normalized == "wheel" || normalized == "wheels")
            {
                return _latestStagecoachSnapshot == null ? null : _latestStagecoachSnapshot.WheelRepair;
            }

            return null;
        }

        private StagecoachSlotPayload FindStagecoachSlot(string slotType, int slotIndex)
        {
            return (_latestStagecoachSnapshot == null
                    ? Array.Empty<StagecoachSlotPayload>()
                    : _latestStagecoachSnapshot.Slots ?? Array.Empty<StagecoachSlotPayload>())
                .FirstOrDefault(candidate => candidate != null &&
                    candidate.SlotIndex == slotIndex &&
                    string.Equals(candidate.SlotType, slotType, StringComparison.Ordinal));
        }

        private string ValidateHeroSelectRequestOwner(ulong senderSteamId, HeroSelectRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty hero select request";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "confirm", StringComparison.Ordinal))
            {
                return senderSteamId == _lobby.Owner.m_SteamID
                    ? string.Empty
                    : "only the lobby host can confirm the party";
            }

            if (!string.Equals(action, "assign", StringComparison.Ordinal) &&
                !string.Equals(action, "clear_slot", StringComparison.Ordinal))
            {
                if (!string.Equals(action, "set_path", StringComparison.Ordinal))
                {
                    return string.Empty;
                }
            }

            int heroSlot = request.SlotIndex + 1;
            HeroSlotAssignmentPayload owner;
            if (!_heroSlots.TryGetValue(heroSlot, out owner))
            {
                return "hero slot " + heroSlot + " is not assigned";
            }

            if (owner.SteamId != senderSteamId)
            {
                return "hero slot " + heroSlot + " belongs to " + owner.Name + "/" + owner.SteamId;
            }

            return string.Empty;
        }

        private string ValidateHeroLoadoutRequestOwner(ulong senderSteamId, HeroLoadoutRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty hero loadout request";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "set_skill", StringComparison.Ordinal) &&
                !string.Equals(action, "master_skill", StringComparison.Ordinal) &&
                !string.Equals(action, "equip_item", StringComparison.Ordinal) &&
                !string.Equals(action, "unequip_item", StringComparison.Ordinal) &&
                !string.Equals(action, "use_rest_item", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (request.HeroSlot < 1 || request.HeroSlot > 4)
            {
                return "hero loadout slot must be 1-4";
            }

            HeroSlotAssignmentPayload owner;
            if (!_heroSlots.TryGetValue(request.HeroSlot, out owner))
            {
                return "hero slot " + request.HeroSlot + " is not assigned";
            }

            if (owner.SteamId != senderSteamId)
            {
                return "hero slot " + request.HeroSlot + " belongs to " + owner.Name + "/" + owner.SteamId;
            }

            if (_latestHeroLoadoutSnapshot != null && _latestHeroLoadoutSnapshot.Actors != null)
            {
                HeroLoadoutActorPayload actor = _latestHeroLoadoutSnapshot.Actors.FirstOrDefault(candidate =>
                    candidate != null && candidate.HeroSlot == request.HeroSlot);
                if (actor == null)
                {
                    return "hero slot " + request.HeroSlot + " is not present in current loadout snapshot";
                }

                if (!string.Equals(actor.ActorGuid ?? string.Empty, request.ActorGuid ?? string.Empty, StringComparison.Ordinal))
                {
                    return "hero slot " + request.HeroSlot +
                        " currently contains actor " + (actor.ActorGuid ?? "[none]") +
                        ", not " + (request.ActorGuid ?? "[none]");
                }

                bool isSkillAction =
                    string.Equals(action, "set_skill", StringComparison.Ordinal) ||
                    string.Equals(action, "master_skill", StringComparison.Ordinal);
                bool isRestItemAction = string.Equals(action, "use_rest_item", StringComparison.Ordinal);
                if (isSkillAction)
                {
                    bool hasSkill = actor.Skills != null &&
                        actor.Skills.Any(skill => skill != null && string.Equals(skill.SkillId, request.SkillId, StringComparison.Ordinal));
                    if (!hasSkill)
                    {
                        return "skill " + (request.SkillId ?? "[none]") +
                            " is not present on actor " + (request.ActorGuid ?? "[none]");
                    }
                }
                else if (isRestItemAction)
                {
                    bool hasSourceItem = _latestHeroLoadoutSnapshot.InventoryItems != null &&
                        _latestHeroLoadoutSnapshot.InventoryItems.Any(item => item != null &&
                            string.Equals(item.ItemKind, "rest", StringComparison.Ordinal) &&
                            item.InventoryIndex == request.SourceInventoryIndex &&
                            !item.IsEmpty &&
                            (string.IsNullOrWhiteSpace(request.ItemId) ||
                                string.Equals(item.ItemId, request.ItemId, StringComparison.Ordinal)));
                    if (!hasSourceItem)
                    {
                        return "rest item source slot " + request.SourceInventoryIndex +
                            " does not contain " + (request.ItemId ?? "[rest item]");
                    }

                    if (request.TargetActorGuids != null)
                    {
                        HashSet<string> actorGuids = new HashSet<string>(
                            _latestHeroLoadoutSnapshot.Actors
                                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.ActorGuid))
                                .Select(candidate => candidate.ActorGuid),
                            StringComparer.Ordinal);
                        foreach (string targetActorGuid in request.TargetActorGuids)
                        {
                            if (string.IsNullOrWhiteSpace(targetActorGuid))
                            {
                                continue;
                            }

                            if (!actorGuids.Contains(targetActorGuid))
                            {
                                return "rest item target actor " + targetActorGuid +
                                    " is not present in current loadout snapshot";
                            }
                        }
                    }
                }
                else
                {
                    string itemKind = (request.ItemKind ?? string.Empty).Trim().ToLowerInvariant();
                    IList<HeroLoadoutItemPayload> targetItems =
                        string.Equals(itemKind, "trinket", StringComparison.Ordinal) ||
                        string.Equals(itemKind, "trinkets", StringComparison.Ordinal)
                            ? actor.Trinkets
                            : actor.CombatItems;
                    bool hasTargetSlot = targetItems != null &&
                        targetItems.Any(item => item != null && item.InventoryIndex == request.TargetInventoryIndex);
                    if (!hasTargetSlot)
                    {
                        return "item target slot " + request.TargetInventoryIndex +
                            " is not present on actor " + (request.ActorGuid ?? "[none]");
                    }
                }
            }

            return string.Empty;
        }

        private bool TryRecordHeroSelectReadyVote(
            ulong senderSteamId,
            string senderName,
            HeroSelectRequestPayload request,
            out string message,
            out VoteRecord<HeroSelectRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncHeroSelectReadyVoteContext(_latestHeroSelectSnapshot);
            string validation = ValidateHeroSelectReadyVote(senderSteamId, request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for hero ready vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "hero ready vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_heroSelectReadyVotes, voters);
            bool updated = _heroSelectReadyVotes.ContainsKey(senderSteamId);
            _heroSelectReadyVotes[senderSteamId] =
                new VoteRecord<HeroSelectRequestPayload>(senderSteamId, senderName, request);

            int votedCount = voters.Count(voter => _heroSelectReadyVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "hero ready vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(
                    VoteKeyHeroReady,
                    _heroSelectReadyVoteDigest,
                    _heroSelectReadyVotes,
                    voters,
                    false,
                    null,
                    FormatHeroReadyChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_heroSelectReadyVotes, voters, FormatHeroReadyChoice);
            resolvedVote = ResolveHeroSelectReadyVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(
                VoteKeyHeroReady,
                _heroSelectReadyVoteDigest,
                true,
                true,
                resolutionMessage,
                voters,
                voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateHeroSelectReadyVote(ulong senderSteamId, HeroSelectRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty hero ready vote";
            }

            if (_latestHeroSelectSnapshot == null || !_latestHeroSelectSnapshot.IsActive)
            {
                return "hero select is not active";
            }

            if (_latestHeroSelectSnapshot.RosterConfirmed)
            {
                return "roster is already confirmed";
            }

            if (!_latestHeroSelectSnapshot.CanConfirm)
            {
                return "party cannot confirm yet";
            }

            if (string.IsNullOrEmpty(_heroSelectReadyVoteDigest))
            {
                return "hero ready vote context is not ready";
            }

            if (string.Equals(_heroSelectReadyResolvedDigest, _heroSelectReadyVoteDigest, StringComparison.Ordinal))
            {
                return "hero ready vote already resolved";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "hero ready rejected; sender is not in this lobby";
            }

            return string.Equals(request.Action.Trim(), "ready", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : "unsupported hero ready action: " + request.Action;
        }

        private VoteRecord<HeroSelectRequestPayload> ResolveHeroSelectReadyVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<HeroSelectRequestPayload>> votes = voters
                .Select(voter => _heroSelectReadyVotes[voter.m_SteamID])
                .ToList();
            string voteText = string.Join(", ", votes.Select(FormatHeroReadyVote).ToArray());
            VoteRecord<HeroSelectRequestPayload> picked = votes[0];
            message = "hero ready vote complete: " + voteText;

            _heroSelectReadyResolvedDigest = _heroSelectReadyVoteDigest;
            _heroSelectReadyVotes.Clear();
            return picked;
        }

        private bool TryRecordInnActionVote(
            ulong senderSteamId,
            string senderName,
            InnActionRequestPayload request,
            out string message,
            out VoteRecord<InnActionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncInnVoteContext(_latestInnSnapshot);
            string validation = ValidateInnActionVote(request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for inn vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "inn vote rejected; sender is not in this lobby";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            Dictionary<ulong, VoteRecord<InnActionRequestPayload>> votes =
                string.Equals(action, "embark", StringComparison.Ordinal)
                    ? _innEmbarkVotes
                    : _innBiomeVotes;
            string voteKey = string.Equals(action, "embark", StringComparison.Ordinal)
                ? VoteKeyInnEmbark
                : VoteKeyInnBiome;

            RemoveVotesFromNonMembers(votes, voters);
            bool updated = votes.ContainsKey(senderSteamId);
            votes[senderSteamId] = new VoteRecord<InnActionRequestPayload>(
                senderSteamId,
                senderName,
                new InnActionRequestPayload(request.RequestId, action, request.OptionIndex));

            int votedCount = voters.Count(voter => votes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "inn vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": " + FormatInnVote(votes[senderSteamId]) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(voteKey, _innVoteDigest, votes, voters, false, null, FormatInnChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(votes, voters, FormatInnChoice);
            resolvedVote = string.Equals(action, "embark", StringComparison.Ordinal)
                ? ResolveInnEmbarkVote(voters, out resolutionMessage)
                : ResolveInnBiomeVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(voteKey, _innVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateInnActionVote(InnActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty inn vote";
            }

            if (_latestInnSnapshot == null || !_latestInnSnapshot.IsActive)
            {
                return "inn is not active";
            }

            if (string.IsNullOrEmpty(_innVoteDigest))
            {
                return "inn vote context is not ready";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "select_biome", StringComparison.Ordinal))
            {
                if (string.Equals(_innBiomeResolvedDigest, _innVoteDigest, StringComparison.Ordinal))
                {
                    return "inn biome vote already resolved";
                }

                if (!HasInnBiomeOption(request.OptionIndex))
                {
                    return "inn biome option " + request.OptionIndex + " is not in the current snapshot";
                }

                return string.Empty;
            }

            if (string.Equals(action, "embark", StringComparison.Ordinal))
            {
                if (string.Equals(_innEmbarkResolvedDigest, _innVoteDigest, StringComparison.Ordinal))
                {
                    return "inn embark vote already resolved";
                }

                return _latestInnSnapshot.CanEmbark
                    ? string.Empty
                    : "inn cannot embark yet";
            }

            return "unsupported inn vote action: " + request.Action;
        }

        private VoteRecord<InnActionRequestPayload> ResolveInnBiomeVote(IList<CSteamID> voters, out string message)
        {
            List<VoteRecord<InnActionRequestPayload>> votes = voters
                .Select(voter => _innBiomeVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes.Select(vote => vote.Payload.OptionIndex).Distinct().Count() == 1;
            VoteRecord<InnActionRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatInnVote).ToArray());

            message = unanimous
                ? "inn biome vote unanimous: option " + picked.Payload.OptionIndex + " (" + voteText + ")"
                : "inn biome vote split: " + voteText + "; random picked option " + picked.Payload.OptionIndex;

            _innBiomeResolvedDigest = _innVoteDigest;
            _innBiomeVotes.Clear();
            return picked;
        }

        private VoteRecord<InnActionRequestPayload> ResolveInnEmbarkVote(IList<CSteamID> voters, out string message)
        {
            List<VoteRecord<InnActionRequestPayload>> votes = voters
                .Select(voter => _innEmbarkVotes[voter.m_SteamID])
                .ToList();
            string voteText = string.Join(", ", votes.Select(FormatInnVote).ToArray());
            VoteRecord<InnActionRequestPayload> picked = votes[0];
            message = "inn embark vote complete: " + voteText;

            _innEmbarkResolvedDigest = _innVoteDigest;
            _innEmbarkVotes.Clear();
            return picked;
        }

        private bool HasInnBiomeOption(int optionIndex)
        {
            IList<InnBiomeChoicePayload> choices = _latestInnSnapshot == null
                ? null
                : _latestInnSnapshot.BiomeChoices;
            return choices != null && choices.Any(choice => choice != null && choice.OptionIndex == optionIndex);
        }

        private bool TryRecordEmbarkContinueVote(
            ulong senderSteamId,
            string senderName,
            EmbarkActionRequestPayload request,
            out string message,
            out VoteRecord<EmbarkActionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncEmbarkVoteContext(_latestEmbarkSnapshot);
            string validation = ValidateEmbarkActionRequest(senderSteamId, request, true);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for embark vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "embark vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_embarkContinueVotes, voters);
            bool updated = _embarkContinueVotes.ContainsKey(senderSteamId);
            _embarkContinueVotes[senderSteamId] = new VoteRecord<EmbarkActionRequestPayload>(
                senderSteamId,
                senderName,
                new EmbarkActionRequestPayload(request.RequestId, "continue"));

            int votedCount = voters.Count(voter => _embarkContinueVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "embark continue vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyEmbarkContinue, _embarkVoteDigest, _embarkContinueVotes, voters, false, null, FormatEmbarkChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_embarkContinueVotes, voters, FormatEmbarkChoice);
            resolvedVote = ResolveEmbarkContinueVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyEmbarkContinue, _embarkVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateEmbarkActionRequest(
            ulong senderSteamId,
            EmbarkActionRequestPayload request,
            bool requireVoteContext)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty embark action";
            }

            if (_latestEmbarkSnapshot == null || !_latestEmbarkSnapshot.IsActive)
            {
                return "embark scene is not active";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "embark action rejected; sender is not in this lobby";
            }

            if (requireVoteContext && string.IsNullOrEmpty(_embarkVoteDigest))
            {
                return "embark vote context is not ready";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "apply_relationships", StringComparison.Ordinal))
            {
                if (_latestEmbarkSnapshot.HasRelationshipsApplied)
                {
                    return "embark relationships are already applied";
                }

                return _latestEmbarkSnapshot.CanApplyRelationships
                    ? string.Empty
                    : "embark relationships cannot be applied yet";
            }

            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                if (string.Equals(_embarkContinueResolvedDigest, _embarkVoteDigest, StringComparison.Ordinal))
                {
                    return "embark continue vote already resolved";
                }

                return _latestEmbarkSnapshot.CanContinue
                    ? string.Empty
                    : "embark scene cannot continue yet";
            }

            return "unsupported embark action: " + request.Action;
        }

        private VoteRecord<EmbarkActionRequestPayload> ResolveEmbarkContinueVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<EmbarkActionRequestPayload>> votes = voters
                .Select(voter => _embarkContinueVotes[voter.m_SteamID])
                .ToList();
            string voteText = string.Join(", ", votes.Select(FormatEmbarkVote).ToArray());
            VoteRecord<EmbarkActionRequestPayload> picked = votes[0];
            message = "embark continue vote complete: " + voteText;

            _embarkContinueResolvedDigest = _embarkVoteDigest;
            _embarkContinueVotes.Clear();
            return picked;
        }

        private bool TryRecordAltarEmbarkVote(
            ulong senderSteamId,
            string senderName,
            AltarActionRequestPayload request,
            out string message,
            out VoteRecord<AltarActionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncAltarVoteContext(_latestAltarSnapshot);
            string validation = ValidateAltarActionVote(senderSteamId, request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for altar vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "altar vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_altarEmbarkVotes, voters);
            bool updated = _altarEmbarkVotes.ContainsKey(senderSteamId);
            _altarEmbarkVotes[senderSteamId] = new VoteRecord<AltarActionRequestPayload>(
                senderSteamId,
                senderName,
                new AltarActionRequestPayload(request.RequestId, "embark"));

            int votedCount = voters.Count(voter => _altarEmbarkVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "altar embark vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyAltarEmbark, _altarVoteDigest, _altarEmbarkVotes, voters, false, null, FormatAltarChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_altarEmbarkVotes, voters, FormatAltarChoice);
            resolvedVote = ResolveAltarEmbarkVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyAltarEmbark, _altarVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateAltarActionVote(ulong senderSteamId, AltarActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty altar action";
            }

            string action = NormalizeAltarAction(request);
            if (!string.Equals(action, "embark", StringComparison.Ordinal))
            {
                return "unsupported altar action: " + request.Action;
            }

            if (_latestAltarSnapshot == null || !_latestAltarSnapshot.IsActive)
            {
                return "altar of hope is not active";
            }

            if (string.IsNullOrEmpty(_altarVoteDigest))
            {
                return "altar vote context is not ready";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "altar vote rejected; sender is not in this lobby";
            }

            if (string.Equals(_altarEmbarkResolvedDigest, _altarVoteDigest, StringComparison.Ordinal))
            {
                return "altar embark vote already resolved";
            }

            return _latestAltarSnapshot.CanEmbark
                ? string.Empty
                : "altar cannot embark: " + (_latestAltarSnapshot.BlockReason ?? "[blocked]");
        }

        private VoteRecord<AltarActionRequestPayload> ResolveAltarEmbarkVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<AltarActionRequestPayload>> votes = voters
                .Select(voter => _altarEmbarkVotes[voter.m_SteamID])
                .ToList();
            string voteText = string.Join(", ", votes.Select(FormatAltarVote).ToArray());
            VoteRecord<AltarActionRequestPayload> picked = votes[0];
            message = "altar embark vote complete: " + voteText;

            _altarEmbarkResolvedDigest = _altarVoteDigest;
            _altarEmbarkVotes.Clear();
            return picked;
        }

        private string ValidateAltarDirectActionRequest(AltarActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty altar action";
            }

            string action = NormalizeAltarAction(request);
            if (_latestAltarSnapshot == null || !_latestAltarSnapshot.IsActive)
            {
                return "altar of hope is not active";
            }

            if (string.Equals(action, "spend_track", StringComparison.Ordinal))
            {
                AltarTrackPayload track = FindAltarTrackPayload(request);
                if (track == null)
                {
                    return "altar track is not in the current snapshot: " +
                        request.TrackIndex + "/" + (request.TrackId ?? "[none]");
                }

                if (!track.CanPurchase)
                {
                    return "altar track cannot spend candles: " +
                        (track.TrackId ?? "[none]") +
                        " spent=" + track.SpentCandles +
                        "/" + track.TotalCandles;
                }

                if (request.SpendValue < 0f)
                {
                    return "altar track spend value is invalid: " + request.SpendValue;
                }

                float spendValue = request.SpendValue <= 0f ? 1f : request.SpendValue;
                if (spendValue < 1f)
                {
                    return "altar track spend value must be at least 1 candle";
                }

                if (spendValue > _latestAltarSnapshot.CandleCount)
                {
                    return "not enough candles for altar track spend: need " +
                        spendValue + ", have " + _latestAltarSnapshot.CandleCount;
                }

                return string.Empty;
            }

            if (string.Equals(action, "purchase_reward", StringComparison.Ordinal))
            {
                AltarRewardButtonPayload reward = FindAltarRewardPayload(request);
                if (reward == null)
                {
                    return "altar reward is not in the current snapshot: #" +
                        request.RewardButtonIndex +
                        "/" + (request.UnlockTableId ?? "[none]") +
                        "/" + (request.ItemType ?? "[none]");
                }

                if (!reward.CanPurchase)
                {
                    return "altar reward cannot be purchased: #" +
                        reward.ButtonIndex +
                        " " + (reward.DisplayName ?? reward.CurrentUnlockTableId ?? reward.ItemType ?? "[reward]") +
                        " mode=" + (reward.PurchaseMode ?? "[none]") +
                        " cost=" + (reward.CostText ?? "[none]");
                }

                return string.Empty;
            }

            return "unsupported altar action: " + request.Action;
        }

        private AltarTrackPayload FindAltarTrackPayload(AltarActionRequestPayload request)
        {
            if (_latestAltarSnapshot == null)
            {
                return null;
            }

            return (_latestAltarSnapshot.Tracks ?? Array.Empty<AltarTrackPayload>())
                .FirstOrDefault(track => track != null &&
                    (request.TrackIndex < 0 || track.TrackIndex == request.TrackIndex) &&
                    (string.IsNullOrWhiteSpace(request.TrackId) ||
                        string.Equals(track.TrackId ?? string.Empty, request.TrackId, StringComparison.Ordinal)));
        }

        private AltarRewardButtonPayload FindAltarRewardPayload(AltarActionRequestPayload request)
        {
            if (_latestAltarSnapshot == null)
            {
                return null;
            }

            return (_latestAltarSnapshot.RewardButtons ?? Array.Empty<AltarRewardButtonPayload>())
                .FirstOrDefault(reward => reward != null &&
                    (request.RewardButtonIndex < 0 || reward.ButtonIndex == request.RewardButtonIndex) &&
                    (string.IsNullOrWhiteSpace(request.ScreenKind) ||
                        string.Equals(reward.ScreenKind ?? string.Empty, request.ScreenKind, StringComparison.Ordinal)) &&
                    (string.IsNullOrWhiteSpace(request.UnlockTableId) ||
                        string.Equals(reward.CurrentUnlockTableId ?? string.Empty, request.UnlockTableId, StringComparison.Ordinal)) &&
                    (string.IsNullOrWhiteSpace(request.UnlockTrackId) ||
                        string.Equals(reward.UnlockTrackId ?? string.Empty, request.UnlockTrackId, StringComparison.Ordinal)) &&
                    (string.IsNullOrWhiteSpace(request.ItemType) ||
                        string.Equals(reward.ItemType ?? string.Empty, request.ItemType, StringComparison.Ordinal)));
        }

        private bool TryRecordGameResultsVote(
            ulong senderSteamId,
            string senderName,
            GameResultsActionRequestPayload request,
            out string message,
            out VoteRecord<GameResultsActionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncGameResultsVoteContext(_latestGameResultsSnapshot);
            string validation = ValidateGameResultsActionVote(senderSteamId, request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for game results vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "game results vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_gameResultsVotes, voters);
            bool updated = _gameResultsVotes.ContainsKey(senderSteamId);
            _gameResultsVotes[senderSteamId] = new VoteRecord<GameResultsActionRequestPayload>(
                senderSteamId,
                senderName,
                new GameResultsActionRequestPayload(request.RequestId, "continue"));

            int votedCount = voters.Count(voter => _gameResultsVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "game results vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyGameResults, _gameResultsVoteDigest, _gameResultsVotes, voters, false, null, FormatGameResultsChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_gameResultsVotes, voters, FormatGameResultsChoice);
            resolvedVote = ResolveGameResultsVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyGameResults, _gameResultsVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateGameResultsActionVote(ulong senderSteamId, GameResultsActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty game results action";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "continue", StringComparison.Ordinal))
            {
                return "unsupported game results action: " + request.Action;
            }

            if (_latestGameResultsSnapshot == null || !_latestGameResultsSnapshot.IsActive)
            {
                return "game results screen is not active";
            }

            if (string.IsNullOrEmpty(_gameResultsVoteDigest))
            {
                return "game results vote context is not ready";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "game results vote rejected; sender is not in this lobby";
            }

            if (string.Equals(_gameResultsResolvedDigest, _gameResultsVoteDigest, StringComparison.Ordinal))
            {
                return "game results vote already resolved";
            }

            return _latestGameResultsSnapshot.CanContinue
                ? string.Empty
                : "game results screen cannot continue yet";
        }

        private VoteRecord<GameResultsActionRequestPayload> ResolveGameResultsVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<GameResultsActionRequestPayload>> votes = voters
                .Select(voter => _gameResultsVotes[voter.m_SteamID])
                .ToList();
            string voteText = string.Join(", ", votes.Select(FormatGameResultsVote).ToArray());
            VoteRecord<GameResultsActionRequestPayload> picked = votes[0];
            message = "game results vote complete: " + voteText;

            _gameResultsResolvedDigest = _gameResultsVoteDigest;
            _gameResultsVotes.Clear();
            return picked;
        }

        private bool TryRecordConfessionChoiceVote(
            ulong senderSteamId,
            string senderName,
            ConfessionChoiceRequestPayload request,
            out string message,
            out VoteRecord<ConfessionChoiceRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncConfessionChoiceVoteContext(_latestConfessionChoiceSnapshot);
            string validation = ValidateConfessionChoiceVote(senderSteamId, request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for confession choice vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "confession choice rejected; sender is not in this lobby";
                return false;
            }

            ConfessionChoiceOptionPayload option;
            TryFindConfessionChoiceOption(request, out option);
            RemoveVotesFromNonMembers(_confessionChoiceVotes, voters);
            bool updated = _confessionChoiceVotes.ContainsKey(senderSteamId);
            _confessionChoiceVotes[senderSteamId] = new VoteRecord<ConfessionChoiceRequestPayload>(
                senderSteamId,
                senderName,
                new ConfessionChoiceRequestPayload(request.RequestId, option.OptionIndex, option.BossId));

            int votedCount = voters.Count(voter => _confessionChoiceVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "confession choice vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": " + FormatConfessionChoice(_confessionChoiceVotes[senderSteamId]) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyConfessionChoice, _confessionChoiceVoteDigest, _confessionChoiceVotes, voters, false, null, FormatConfessionChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_confessionChoiceVotes, voters, FormatConfessionChoice);
            resolvedVote = ResolveConfessionChoiceVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyConfessionChoice, _confessionChoiceVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateConfessionChoiceVote(ulong senderSteamId, ConfessionChoiceRequestPayload request)
        {
            if (request == null)
            {
                return "empty confession choice";
            }

            if (_latestConfessionChoiceSnapshot == null || !_latestConfessionChoiceSnapshot.IsActive)
            {
                return "confession choice screen is not active";
            }

            if (string.IsNullOrEmpty(_confessionChoiceVoteDigest))
            {
                return "confession choice vote context is not ready";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "confession choice rejected; sender is not in this lobby";
            }

            if (string.Equals(_confessionChoiceResolvedDigest, _confessionChoiceVoteDigest, StringComparison.Ordinal))
            {
                return "confession choice vote already resolved";
            }

            if (!_latestConfessionChoiceSnapshot.CanChoose)
            {
                return "confession choice screen cannot choose yet";
            }

            ConfessionChoiceOptionPayload option;
            if (!TryFindConfessionChoiceOption(request, out option))
            {
                return "confession option " + request.OptionIndex + "/" + (request.BossId ?? "[none]") +
                    " is not in the current snapshot";
            }

            return option.IsSelectable
                ? string.Empty
                : "confession option " + option.OptionIndex + "/" +
                    (option.BossId ?? "[none]") + " is not selectable";
        }

        private VoteRecord<ConfessionChoiceRequestPayload> ResolveConfessionChoiceVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<ConfessionChoiceRequestPayload>> votes = voters
                .Select(voter => _confessionChoiceVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes.Select(vote => ConfessionChoiceVoteKey(vote.Payload)).Distinct().Count() == 1;
            VoteRecord<ConfessionChoiceRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatConfessionVote).ToArray());

            message = unanimous
                ? "confession choice vote unanimous: " + FormatConfessionChoice(picked) + " (" + voteText + ")"
                : "confession choice vote split: " + voteText + "; random picked " + FormatConfessionChoice(picked);

            _confessionChoiceResolvedDigest = _confessionChoiceVoteDigest;
            _confessionChoiceVotes.Clear();
            return picked;
        }

        private bool TryRecordLairDecisionVote(
            ulong senderSteamId,
            string senderName,
            LairDecisionRequestPayload request,
            out string message,
            out VoteRecord<LairDecisionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncLairDecisionVoteContext(_latestLairDecisionSnapshot);
            string validation = ValidateLairDecisionVote(senderSteamId, request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for lair decision vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "lair decision rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_lairDecisionVotes, voters);
            bool updated = _lairDecisionVotes.ContainsKey(senderSteamId);
            string action = request.Action.Trim().ToLowerInvariant();
            _lairDecisionVotes[senderSteamId] = new VoteRecord<LairDecisionRequestPayload>(
                senderSteamId,
                senderName,
                new LairDecisionRequestPayload(request.RequestId, action));

            int votedCount = voters.Count(voter => _lairDecisionVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "lair decision vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": " + FormatLairDecisionVote(_lairDecisionVotes[senderSteamId]) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyLairDecision, _lairDecisionVoteDigest, _lairDecisionVotes, voters, false, null, FormatLairDecisionChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_lairDecisionVotes, voters, FormatLairDecisionChoice);
            resolvedVote = ResolveLairDecisionVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyLairDecision, _lairDecisionVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateLairDecisionVote(ulong senderSteamId, LairDecisionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty lair decision vote";
            }

            if (_latestLairDecisionSnapshot == null || !_latestLairDecisionSnapshot.IsActive)
            {
                return "lair decision dialog is not active";
            }

            if (string.IsNullOrEmpty(_lairDecisionVoteDigest))
            {
                return "lair decision vote context is not ready";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "lair decision rejected; sender is not in this lobby";
            }

            if (string.Equals(_lairDecisionResolvedDigest, _lairDecisionVoteDigest, StringComparison.Ordinal))
            {
                return "lair decision vote already resolved";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                return _latestLairDecisionSnapshot.CanContinue
                    ? string.Empty
                    : "lair continue is not available";
            }

            if (string.Equals(action, "retreat", StringComparison.Ordinal))
            {
                return _latestLairDecisionSnapshot.CanRetreat
                    ? string.Empty
                    : "lair retreat is not available";
            }

            return "unsupported lair decision: " + request.Action;
        }

        private VoteRecord<LairDecisionRequestPayload> ResolveLairDecisionVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<LairDecisionRequestPayload>> votes = voters
                .Select(voter => _lairDecisionVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes
                .Select(vote => vote.Payload.Action ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;
            VoteRecord<LairDecisionRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatLairDecisionVote).ToArray());

            message = unanimous
                ? "lair decision vote unanimous: " + (picked.Payload.Action ?? "[none]") + " (" + voteText + ")"
                : "lair decision vote split: " + voteText + "; random picked " + (picked.Payload.Action ?? "[none]");

            _lairDecisionResolvedDigest = _lairDecisionVoteDigest;
            _lairDecisionVotes.Clear();
            return picked;
        }

        private bool TryRecordConfirmationDialogVote(
            ulong senderSteamId,
            string senderName,
            ConfirmationDialogRequestPayload request,
            out string message,
            out VoteRecord<ConfirmationDialogRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncConfirmationDialogVoteContext(_latestConfirmationDialogSnapshot);
            string validation = ValidateConfirmationDialogVote(senderSteamId, request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for confirmation dialog vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "confirmation dialog vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_confirmationDialogVotes, voters);
            bool updated = _confirmationDialogVotes.ContainsKey(senderSteamId);
            string action = request.Action.Trim().ToLowerInvariant();
            _confirmationDialogVotes[senderSteamId] = new VoteRecord<ConfirmationDialogRequestPayload>(
                senderSteamId,
                senderName,
                new ConfirmationDialogRequestPayload(request.RequestId, action));

            int votedCount = voters.Count(voter => _confirmationDialogVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "confirmation dialog vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": " + FormatConfirmationDialogVote(_confirmationDialogVotes[senderSteamId]) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(
                    VoteKeyConfirmationDialog,
                    _confirmationDialogVoteDigest,
                    _confirmationDialogVotes,
                    voters,
                    false,
                    null,
                    FormatConfirmationDialogChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_confirmationDialogVotes, voters, FormatConfirmationDialogChoice);
            resolvedVote = ResolveConfirmationDialogVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(
                VoteKeyConfirmationDialog,
                _confirmationDialogVoteDigest,
                true,
                true,
                resolutionMessage,
                voters,
                voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateConfirmationDialogVote(ulong senderSteamId, ConfirmationDialogRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty confirmation dialog vote";
            }

            if (_latestConfirmationDialogSnapshot == null || !_latestConfirmationDialogSnapshot.IsActive)
            {
                return "confirmation dialog is not active";
            }

            if (!_latestConfirmationDialogSnapshot.IsAllowed)
            {
                return "confirmation dialog is not multiplayer-safe: " +
                    (_latestConfirmationDialogSnapshot.BlockReason ?? "[none]");
            }

            if (string.IsNullOrEmpty(_confirmationDialogVoteDigest))
            {
                return "confirmation dialog vote context is not ready";
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (!HasVoter(voters, senderSteamId))
            {
                return "confirmation dialog vote rejected; sender is not in this lobby";
            }

            if (string.Equals(_confirmationDialogResolvedDigest, _confirmationDialogVoteDigest, StringComparison.Ordinal))
            {
                return "confirmation dialog vote already resolved";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "confirm", StringComparison.Ordinal))
            {
                return _latestConfirmationDialogSnapshot.CanConfirm
                    ? string.Empty
                    : "confirmation action is not available";
            }

            if (string.Equals(action, "decline", StringComparison.Ordinal))
            {
                return _latestConfirmationDialogSnapshot.CanDecline
                    ? string.Empty
                    : "decline action is not available";
            }

            return "unsupported confirmation dialog action: " + request.Action;
        }

        private VoteRecord<ConfirmationDialogRequestPayload> ResolveConfirmationDialogVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<ConfirmationDialogRequestPayload>> votes = voters
                .Select(voter => _confirmationDialogVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes
                .Select(vote => vote.Payload.Action ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;
            VoteRecord<ConfirmationDialogRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatConfirmationDialogVote).ToArray());

            message = unanimous
                ? "confirmation dialog vote unanimous: " + (picked.Payload.Action ?? "[none]") + " (" + voteText + ")"
                : "confirmation dialog vote split: " + voteText + "; random picked " + (picked.Payload.Action ?? "[none]");

            _confirmationDialogResolvedDigest = _confirmationDialogVoteDigest;
            _confirmationDialogVotes.Clear();
            return picked;
        }

        private bool TryRecordLootActionVote(
            ulong senderSteamId,
            string senderName,
            LootActionRequestPayload request,
            out string message,
            out VoteRecord<LootActionRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncLootVoteContext(_latestLootWindowSnapshot);
            string validation = ValidateLootActionVote(request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetLootVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for loot vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "loot vote rejected; sender is not eligible for loot vote";
                return false;
            }

            RemoveVotesFromNonMembers(_lootActionVotes, voters);
            bool updated = _lootActionVotes.ContainsKey(senderSteamId);
            _lootActionVotes[senderSteamId] =
                new VoteRecord<LootActionRequestPayload>(senderSteamId, senderName, NormalizeLootActionRequest(request));

            int votedCount = voters.Count(voter => _lootActionVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "loot vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": " + FormatLootVote(_lootActionVotes[senderSteamId]) +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyLoot, _lootVoteDigest, _lootActionVotes, voters, false, null, FormatLootChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_lootActionVotes, voters, FormatLootChoice);
            resolvedVote = ResolveLootActionVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyLoot, _lootVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateLootActionVote(LootActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return "empty loot vote";
            }

            if (_latestLootWindowSnapshot == null || !_latestLootWindowSnapshot.IsActive)
            {
                return "loot window is not active";
            }

            if (string.IsNullOrEmpty(_lootVoteDigest))
            {
                return "loot vote context is not ready";
            }

            if (string.Equals(_lootResolvedDigest, _lootVoteDigest, StringComparison.Ordinal))
            {
                return "loot vote already resolved";
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "take_all", StringComparison.Ordinal))
            {
                return _latestLootWindowSnapshot.CanTakeAll
                    ? string.Empty
                    : "loot window cannot take all right now";
            }

            if (string.Equals(action, "discard_all", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (string.Equals(action, "take_item", StringComparison.Ordinal))
            {
                LootActionItemPayload item = new LootActionItemPayload(request.ItemId, request.Quantity, request.InventoryIndex);
                return ValidateLootVoteItem(item);
            }

            if (string.Equals(action, "take_selected", StringComparison.Ordinal))
            {
                IList<LootActionItemPayload> items = request.Items ?? Array.Empty<LootActionItemPayload>();
                if (items.Count == 0)
                {
                    return "loot vote has no selected items";
                }

                foreach (LootActionItemPayload item in items)
                {
                    string validation = ValidateLootVoteItem(item);
                    if (!string.IsNullOrEmpty(validation))
                    {
                        return validation;
                    }
                }

                return string.Empty;
            }

            return "unsupported loot vote action: " + request.Action;
        }

        private string ValidateLootVoteItem(LootActionItemPayload requestedItem)
        {
            if (requestedItem == null)
            {
                return "empty selected loot item";
            }

            LootItemSnapshotPayload currentItem;
            if (!TryFindLootItem(requestedItem.InventoryIndex, out currentItem))
            {
                return "loot item index " + requestedItem.InventoryIndex + " is not in the current snapshot";
            }

            if (!string.IsNullOrWhiteSpace(requestedItem.ItemId) &&
                !string.Equals(requestedItem.ItemId, currentItem.ItemId, StringComparison.Ordinal))
            {
                return "loot item changed at index " + requestedItem.InventoryIndex +
                    ": expected " + requestedItem.ItemId +
                    ", found " + (currentItem.ItemId ?? "[none]");
            }

            return string.Empty;
        }

        private VoteRecord<LootActionRequestPayload> ResolveLootActionVote(IList<CSteamID> voters, out string message)
        {
            List<VoteRecord<LootActionRequestPayload>> votes = voters
                .Select(voter => _lootActionVotes[voter.m_SteamID])
                .ToList();
            string voteText = string.Join(", ", votes.Select(FormatLootVote).ToArray());

            VoteRecord<LootActionRequestPayload> takeAllVote = votes.FirstOrDefault(IsTakeAllVote);
            if (takeAllVote != null)
            {
                message = "loot vote resolved: take_all because at least one player requested all (" + voteText + ")";
                _lootResolvedDigest = _lootVoteDigest;
                _lootActionVotes.Clear();
                return takeAllVote;
            }

            List<LootActionItemPayload> selectedItems = MergeLootVoteItems(votes);
            if (selectedItems.Count == 0)
            {
                VoteRecord<LootActionRequestPayload> discardVote = votes.FirstOrDefault(IsDiscardAllVote) ?? votes[0];
                LootActionRequestPayload discardPayload =
                    new LootActionRequestPayload(Guid.NewGuid().ToString("N"), "discard_all", null, 0);
                message = "loot vote resolved: discard_all because nobody selected loot (" + voteText + ")";
                _lootResolvedDigest = _lootVoteDigest;
                _lootActionVotes.Clear();
                return new VoteRecord<LootActionRequestPayload>(
                    discardVote.SenderSteamId,
                    discardVote.SenderName,
                    discardPayload);
            }

            LootActionRequestPayload selectedPayload =
                new LootActionRequestPayload(Guid.NewGuid().ToString("N"), "take_selected", selectedItems);
            message = "loot vote resolved: take_selected " + selectedItems.Count +
                " item(s), then close remaining loot (" + voteText + ")";
            _lootResolvedDigest = _lootVoteDigest;
            _lootActionVotes.Clear();
            return new VoteRecord<LootActionRequestPayload>(
                _lobby.Owner.m_SteamID,
                "loot-vote",
                selectedPayload);
        }

        private LootActionRequestPayload NormalizeLootActionRequest(LootActionRequestPayload request)
        {
            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "take_item", StringComparison.Ordinal))
            {
                return new LootActionRequestPayload(
                    request.RequestId,
                    "take_selected",
                    new[]
                    {
                        new LootActionItemPayload(request.ItemId, request.Quantity, request.InventoryIndex),
                    });
            }

            if (string.Equals(action, "take_selected", StringComparison.Ordinal))
            {
                return new LootActionRequestPayload(
                    request.RequestId,
                    "take_selected",
                    NormalizeLootVoteItems(request.Items));
            }

            return new LootActionRequestPayload(
                request.RequestId,
                action,
                request.ItemId,
                request.Quantity,
                request.InventoryIndex);
        }

        private List<LootActionItemPayload> NormalizeLootVoteItems(IEnumerable<LootActionItemPayload> items)
        {
            Dictionary<int, LootActionItemPayload> result = new Dictionary<int, LootActionItemPayload>();
            foreach (LootActionItemPayload item in items ?? Array.Empty<LootActionItemPayload>())
            {
                if (item == null || item.InventoryIndex < 0)
                {
                    continue;
                }

                LootItemSnapshotPayload currentItem;
                if (TryFindLootItem(item.InventoryIndex, out currentItem))
                {
                    result[item.InventoryIndex] = new LootActionItemPayload(
                        currentItem.ItemId,
                        currentItem.Quantity,
                        currentItem.InventoryIndex);
                }
            }

            return result.Values
                .OrderBy(item => item.InventoryIndex)
                .ToList();
        }

        private List<LootActionItemPayload> MergeLootVoteItems(IEnumerable<VoteRecord<LootActionRequestPayload>> votes)
        {
            Dictionary<int, LootActionItemPayload> selectedItems = new Dictionary<int, LootActionItemPayload>();
            foreach (VoteRecord<LootActionRequestPayload> vote in votes)
            {
                LootActionRequestPayload payload = vote.Payload;
                if (payload == null || string.IsNullOrWhiteSpace(payload.Action))
                {
                    continue;
                }

                string action = payload.Action.Trim().ToLowerInvariant();
                IEnumerable<LootActionItemPayload> items = string.Equals(action, "take_selected", StringComparison.Ordinal)
                    ? payload.Items ?? Array.Empty<LootActionItemPayload>()
                    : Array.Empty<LootActionItemPayload>();

                foreach (LootActionItemPayload item in NormalizeLootVoteItems(items))
                {
                    selectedItems[item.InventoryIndex] = item;
                }
            }

            return selectedItems.Values
                .OrderByDescending(item => item.InventoryIndex)
                .ToList();
        }

        private bool TryFindLootItem(int inventoryIndex, out LootItemSnapshotPayload item)
        {
            item = null;
            IList<LootItemSnapshotPayload> items = _latestLootWindowSnapshot == null
                ? null
                : _latestLootWindowSnapshot.Items;
            if (items == null)
            {
                return false;
            }

            item = items.FirstOrDefault(candidate => candidate != null && candidate.InventoryIndex == inventoryIndex);
            return item != null;
        }

        private static bool IsTakeAllVote(VoteRecord<LootActionRequestPayload> vote)
        {
            return vote != null &&
                vote.Payload != null &&
                string.Equals(vote.Payload.Action, "take_all", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDiscardAllVote(VoteRecord<LootActionRequestPayload> vote)
        {
            return vote != null &&
                vote.Payload != null &&
                string.Equals(vote.Payload.Action, "discard_all", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryRecordRouteChoiceVote(
            ulong senderSteamId,
            string senderName,
            RouteChoiceRequestPayload request,
            out string message,
            out VoteRecord<RouteChoiceRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncRouteChoiceVoteContext(_latestRouteChoiceSnapshot);
            string validation = ValidateRouteChoiceVote(request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for route vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "route vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_routeChoiceVotes, voters);
            bool updated = _routeChoiceVotes.ContainsKey(senderSteamId);
            _routeChoiceVotes[senderSteamId] =
                new VoteRecord<RouteChoiceRequestPayload>(senderSteamId, senderName, request);

            int votedCount = voters.Count(voter => _routeChoiceVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "route vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": option " + request.OptionIndex +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyRoute, _routeChoiceVoteDigest, _routeChoiceVotes, voters, false, null, FormatRouteChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_routeChoiceVotes, voters, FormatRouteChoice);
            resolvedVote = ResolveRouteChoiceVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyRoute, _routeChoiceVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private bool TryRecordStoryChoiceVote(
            ulong senderSteamId,
            string senderName,
            StoryChoiceRequestPayload request,
            out string message,
            out VoteRecord<StoryChoiceRequestPayload> resolvedVote,
            out string resolutionMessage)
        {
            resolvedVote = null;
            resolutionMessage = null;

            SyncStoryChoiceVoteContext(_latestStoryChoiceSnapshot);
            string validation = ValidateStoryChoiceVote(request);
            if (!string.IsNullOrEmpty(validation))
            {
                message = validation;
                return false;
            }

            List<CSteamID> voters = GetCurrentVoters();
            if (voters.Count == 0)
            {
                message = "no lobby members are available for story vote";
                return false;
            }

            if (!HasVoter(voters, senderSteamId))
            {
                message = "story vote rejected; sender is not in this lobby";
                return false;
            }

            RemoveVotesFromNonMembers(_storyChoiceVotes, voters);
            bool updated = _storyChoiceVotes.ContainsKey(senderSteamId);
            _storyChoiceVotes[senderSteamId] =
                new VoteRecord<StoryChoiceRequestPayload>(senderSteamId, senderName, request);

            int votedCount = voters.Count(voter => _storyChoiceVotes.ContainsKey(voter.m_SteamID));
            if (votedCount < voters.Count)
            {
                message = "story vote " + (updated ? "updated" : "recorded") +
                    " from " + FormatVoteSender(senderName, senderSteamId) +
                    ": option " + request.OptionIndex +
                    ", slot " + request.HeroSlot +
                    "; waiting " + votedCount + "/" + voters.Count + ".";
                PublishVoteStatus(VoteKeyStory, _storyChoiceVoteDigest, _storyChoiceVotes, voters, false, null, FormatStoryChoice);
                return true;
            }

            List<VoteEntryPayload> voteEntries = BuildVoteEntries(_storyChoiceVotes, voters, FormatStoryChoice);
            resolvedVote = ResolveStoryChoiceVote(voters, out resolutionMessage);
            PublishVoteStatusEntries(VoteKeyStory, _storyChoiceVoteDigest, true, true, resolutionMessage, voters, voteEntries);
            message = resolutionMessage;
            return true;
        }

        private string ValidateRouteChoiceVote(RouteChoiceRequestPayload request)
        {
            if (request == null)
            {
                return "empty route choice request";
            }

            if (_latestRouteChoiceSnapshot == null || !_latestRouteChoiceSnapshot.IsActive)
            {
                return "route choice is not active";
            }

            if (string.IsNullOrEmpty(_routeChoiceVoteDigest))
            {
                return "route choice vote context is not ready";
            }

            if (string.Equals(_routeChoiceResolvedDigest, _routeChoiceVoteDigest, StringComparison.Ordinal))
            {
                return "route choice vote already resolved";
            }

            if (!HasRouteChoiceOption(request.OptionIndex))
            {
                return "route choice option " + request.OptionIndex + " is not in the current snapshot";
            }

            return string.Empty;
        }

        private string ValidateStoryChoiceVote(StoryChoiceRequestPayload request)
        {
            if (request == null)
            {
                return "empty story choice request";
            }

            if (_latestStoryChoiceSnapshot == null || !_latestStoryChoiceSnapshot.IsActive)
            {
                return "story choice is not active";
            }

            if (string.IsNullOrEmpty(_storyChoiceVoteDigest))
            {
                return "story choice vote context is not ready";
            }

            if (string.Equals(_storyChoiceResolvedDigest, _storyChoiceVoteDigest, StringComparison.Ordinal))
            {
                return "story choice vote already resolved";
            }

            StoryChoiceOptionPayload option;
            if (!TryFindStoryChoiceOption(request, out option))
            {
                return "story choice option " + request.OptionIndex +
                    ", slot " + request.HeroSlot +
                    ", actor " + (request.ActorGuid ?? "[none]") +
                    " is not in the current snapshot";
            }

            if (!option.CanChoose)
            {
                return "story choice option " + request.OptionIndex + " is not selectable yet";
            }

            return string.Empty;
        }

        private string ValidateChoiceOverruleRequest(ulong senderSteamId, string senderName, string voteKey)
        {
            EnsureChoiceOverruleMapState();
            if (!_choiceOverruleEnabled)
            {
                return "choice overrule is disabled by host";
            }

            if (_choiceOverruleLimitPerMap <= 0)
            {
                return "choice overrule limit is 0";
            }

            if (_choiceOverruleRemaining <= 0)
            {
                return "choice overrule has no uses left on this map";
            }

            if (!_lobby.IsInLobby)
            {
                return "choice overrule rejected; no active lobby";
            }

            CSteamID sender = new CSteamID(senderSteamId);
            if (!sender.IsValid() || !_lobby.GetMembers().Any(member => member.m_SteamID == senderSteamId))
            {
                return "choice overrule rejected; sender is not in this lobby";
            }

            if (senderSteamId == _lobby.Owner.m_SteamID)
            {
                return "choice overrule is for clients; host can choose normally";
            }

            if (!string.Equals(voteKey, VoteKeyRoute, StringComparison.Ordinal) &&
                !string.Equals(voteKey, VoteKeyStory, StringComparison.Ordinal))
            {
                return "choice overrule does not support vote " + (voteKey ?? "[none]");
            }

            return string.Empty;
        }

        private void ConsumeChoiceOverruleUse()
        {
            EnsureChoiceOverruleMapState();
            if (_choiceOverruleRemaining > 0)
            {
                _choiceOverruleRemaining--;
            }

            ApplyChoiceOverruleState(_latestRouteChoiceSnapshot);
            ApplyChoiceOverruleState(_latestStoryChoiceSnapshot);
            ApplyChoiceOverruleState(_latestExpeditionOverviewSnapshot);
            BroadcastChoiceOverruleSnapshots();
        }

        private VoteRecord<RouteChoiceRequestPayload> ResolveRouteChoiceVote(IList<CSteamID> voters, out string message)
        {
            List<VoteRecord<RouteChoiceRequestPayload>> votes = voters
                .Select(voter => _routeChoiceVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes.Select(vote => vote.Payload.OptionIndex).Distinct().Count() == 1;
            VoteRecord<RouteChoiceRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatRouteVote).ToArray());

            message = unanimous
                ? "route vote unanimous: option " + picked.Payload.OptionIndex + " (" + voteText + ")"
                : "route vote split: " + voteText + "; random picked option " + picked.Payload.OptionIndex;

            _routeChoiceResolvedDigest = _routeChoiceVoteDigest;
            _routeChoiceVotes.Clear();
            return picked;
        }

        private VoteRecord<StoryChoiceRequestPayload> ResolveStoryChoiceVote(IList<CSteamID> voters, out string message)
        {
            List<VoteRecord<StoryChoiceRequestPayload>> votes = voters
                .Select(voter => _storyChoiceVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes.Select(vote => StoryChoiceVoteKey(vote.Payload)).Distinct().Count() == 1;
            VoteRecord<StoryChoiceRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatStoryVote).ToArray());

            message = unanimous
                ? "story vote unanimous: option " + picked.Payload.OptionIndex +
                    ", slot " + picked.Payload.HeroSlot + " (" + voteText + ")"
                : "story vote split: " + voteText +
                    "; random picked option " + picked.Payload.OptionIndex +
                    ", slot " + picked.Payload.HeroSlot;

            _storyChoiceResolvedDigest = _storyChoiceVoteDigest;
            _storyChoiceVotes.Clear();
            return picked;
        }

        private VoteRecord<MainMenuActionRequestPayload> ResolveMainMenuActionVote(
            IList<CSteamID> voters,
            out string message)
        {
            List<VoteRecord<MainMenuActionRequestPayload>> votes = voters
                .Select(voter => _mainMenuActionVotes[voter.m_SteamID])
                .ToList();
            bool unanimous = votes.Select(vote => NormalizeMainMenuAction(vote.Payload)).Distinct().Count() == 1;
            VoteRecord<MainMenuActionRequestPayload> picked = unanimous
                ? votes[0]
                : votes[_voteRandom.Next(votes.Count)];
            string voteText = string.Join(", ", votes.Select(FormatMainMenuVote).ToArray());

            message = unanimous
                ? "main menu vote unanimous: " + FormatMainMenuAction(picked.Payload) + " (" + voteText + ")"
                : "main menu vote split: " + voteText + "; random picked " + FormatMainMenuAction(picked.Payload);

            _mainMenuResolvedDigest = _mainMenuVoteDigest;
            _mainMenuActionVotes.Clear();
            return picked;
        }

        private void SyncRouteChoiceVoteContext(RouteChoiceSnapshotPayload snapshot)
        {
            string digest = snapshot != null && snapshot.IsActive ? snapshot.Digest : null;
            if (string.Equals(_routeChoiceVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _routeChoiceVoteDigest = digest;
            _routeChoiceResolvedDigest = null;
            _routeChoiceVotes.Clear();
            PublishVoteContext(VoteKeyRoute, digest, snapshot != null && snapshot.IsActive);
        }

        private void EnsureChoiceOverruleMapState()
        {
            string currentMapKey = BuildChoiceOverruleMapKey();
            if (string.Equals(_choiceOverruleMapKey, currentMapKey, StringComparison.Ordinal))
            {
                return;
            }

            _choiceOverruleMapKey = currentMapKey;
            _choiceOverruleRemaining = _choiceOverruleEnabled ? _choiceOverruleLimitPerMap : 0;
        }

        private void RefreshChoiceOverruleMapIfChanged()
        {
            string before = _choiceOverruleMapKey;
            EnsureChoiceOverruleMapState();
            if (string.Equals(before, _choiceOverruleMapKey, StringComparison.Ordinal))
            {
                return;
            }

            ApplyChoiceOverruleState(_latestRouteChoiceSnapshot);
            ApplyChoiceOverruleState(_latestStoryChoiceSnapshot);
            ApplyChoiceOverruleState(_latestExpeditionOverviewSnapshot);
            BroadcastChoiceOverruleSnapshots();
            HostLog.Write("[overrule] map changed; remaining=" + _choiceOverruleRemaining +
                "/" + _choiceOverruleLimitPerMap +
                ", map=" + (_choiceOverruleMapKey ?? "[none]") + ".");
        }

        private string BuildChoiceOverruleMapKey()
        {
            ExpeditionOverviewSnapshotPayload overview = _latestExpeditionOverviewSnapshot;
            if (overview != null && overview.IsActive)
            {
                ExpeditionMapRoutePayload route = overview.MapRoute;
                int biomeIndex = route == null ? -1 : route.BiomeIndex;
                return string.Join(":",
                    "overview",
                    overview.CurrentGameType ?? string.Empty,
                    overview.BiomeType ?? string.Empty,
                    overview.BiomeSubType ?? string.Empty,
                    biomeIndex.ToString());
            }

            RunStateSnapshotPayload run = _latestRunStateSnapshot;
            if (run != null && run.IsRunStarted)
            {
                return string.Join(":",
                    "run",
                    run.CurrentGameType ?? string.Empty,
                    run.BiomeType ?? string.Empty,
                    run.BiomeSubType ?? string.Empty,
                    run.BiomeIndex.ToString());
            }

            return "no-active-map";
        }

        private void ApplyChoiceOverruleState(RouteChoiceSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            EnsureChoiceOverruleMapState();
            snapshot.ChoiceOverruleEnabled = _choiceOverruleEnabled;
            snapshot.ChoiceOverruleLimitPerMap = _choiceOverruleLimitPerMap;
            snapshot.ChoiceOverruleRemaining = _choiceOverruleRemaining;
            snapshot.ChoiceOverruleMapKey = _choiceOverruleMapKey;
        }

        private void ApplyChoiceOverruleState(StoryChoiceSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            EnsureChoiceOverruleMapState();
            snapshot.ChoiceOverruleEnabled = _choiceOverruleEnabled;
            snapshot.ChoiceOverruleLimitPerMap = _choiceOverruleLimitPerMap;
            snapshot.ChoiceOverruleRemaining = _choiceOverruleRemaining;
            snapshot.ChoiceOverruleMapKey = _choiceOverruleMapKey;
        }

        private void ApplyChoiceOverruleState(ExpeditionOverviewSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            EnsureChoiceOverruleMapState();
            snapshot.ChoiceOverruleEnabled = _choiceOverruleEnabled;
            snapshot.ChoiceOverruleLimitPerMap = _choiceOverruleLimitPerMap;
            snapshot.ChoiceOverruleRemaining = _choiceOverruleRemaining;
            snapshot.ChoiceOverruleMapKey = _choiceOverruleMapKey;
        }

        private void BroadcastChoiceOverruleSnapshots()
        {
            if (!_lobby.IsHost || !_lobby.IsInLobby)
            {
                return;
            }

            if (_latestRouteChoiceSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.RouteChoiceSnapshot, _latestRouteChoiceSnapshot, false);
            }

            if (_latestStoryChoiceSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.StoryChoiceSnapshot, _latestStoryChoiceSnapshot, false);
            }

            if (_latestExpeditionOverviewSnapshot != null)
            {
                Broadcast(MultiplayerMessageType.ExpeditionOverviewSnapshot, _latestExpeditionOverviewSnapshot, false);
            }
        }

        private void SyncLootVoteContext(LootWindowSnapshotPayload snapshot)
        {
            string digest = snapshot != null && snapshot.IsActive ? snapshot.Digest : null;
            if (string.Equals(_lootVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lootVoteDigest = digest;
            _lootResolvedDigest = null;
            _lootActionVotes.Clear();
            bool isActive = snapshot != null && snapshot.IsActive;
            IList<CSteamID> voters = isActive ? GetLootVoters() : Array.Empty<CSteamID>();
            PublishVoteStatusEntries(
                VoteKeyLoot,
                digest,
                isActive,
                false,
                null,
                voters,
                new List<VoteEntryPayload>());
        }

        private void RefreshLootVoteEligibility()
        {
            string activeDigest = _latestLootWindowSnapshot != null && _latestLootWindowSnapshot.IsActive
                ? _latestLootWindowSnapshot.Digest
                : null;
            if (string.IsNullOrEmpty(activeDigest))
            {
                return;
            }

            _lootVoteDigest = null;
            SyncLootVoteContext(_latestLootWindowSnapshot);
        }

        private void SyncGameResultsVoteContext(GameResultsSnapshotPayload snapshot)
        {
            bool isActive = snapshot != null && snapshot.IsActive && snapshot.CanContinue;
            string digest = isActive ? snapshot.Digest : null;
            if (string.Equals(_gameResultsVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _gameResultsVoteDigest = digest;
            _gameResultsResolvedDigest = null;
            _gameResultsVotes.Clear();
            PublishVoteContext(VoteKeyGameResults, digest, isActive);
        }

        private void SyncStoryChoiceVoteContext(StoryChoiceSnapshotPayload snapshot)
        {
            string digest = snapshot != null && snapshot.IsActive ? snapshot.Digest : null;
            if (string.Equals(_storyChoiceVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _storyChoiceVoteDigest = digest;
            _storyChoiceResolvedDigest = null;
            _storyChoiceVotes.Clear();
            PublishVoteContext(VoteKeyStory, digest, snapshot != null && snapshot.IsActive);
        }

        private void SyncMainMenuVoteContext(MainMenuSnapshotPayload snapshot)
        {
            bool isActive = snapshot != null &&
                snapshot.IsActive &&
                (snapshot.CanContinueExpedition || snapshot.CanStartNewExpedition);
            string digest = isActive ? snapshot.Digest : null;
            if (string.Equals(_mainMenuVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _mainMenuVoteDigest = digest;
            _mainMenuResolvedDigest = null;
            _mainMenuActionVotes.Clear();
            PublishVoteContext(VoteKeyMainMenu, digest, isActive);
        }

        private void SyncHeroSelectReadyVoteContext(HeroSelectSnapshotPayload snapshot)
        {
            bool isActive = snapshot != null &&
                snapshot.IsActive &&
                !snapshot.RosterConfirmed &&
                snapshot.CanConfirm;
            string digest = isActive ? snapshot.Digest : null;
            if (string.Equals(_heroSelectReadyVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _heroSelectReadyVoteDigest = digest;
            _heroSelectReadyResolvedDigest = null;
            _heroSelectReadyVotes.Clear();
            PublishVoteContext(VoteKeyHeroReady, digest, isActive);
        }

        private void SyncInnVoteContext(InnSnapshotPayload snapshot)
        {
            string digest = snapshot != null && snapshot.IsActive ? snapshot.Digest : null;
            if (string.Equals(_innVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _innVoteDigest = digest;
            _innBiomeResolvedDigest = null;
            _innEmbarkResolvedDigest = null;
            _innBiomeVotes.Clear();
            _innEmbarkVotes.Clear();
            bool isActive = snapshot != null && snapshot.IsActive;
            PublishVoteContext(VoteKeyInnBiome, digest, isActive);
            PublishVoteContext(VoteKeyInnEmbark, digest, isActive && snapshot.CanEmbark);
        }

        private void SyncEmbarkVoteContext(EmbarkSnapshotPayload snapshot)
        {
            string digest = snapshot != null && snapshot.IsActive ? snapshot.Digest : null;
            if (string.Equals(_embarkVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _embarkVoteDigest = digest;
            _embarkContinueResolvedDigest = null;
            _embarkContinueVotes.Clear();
            PublishVoteContext(VoteKeyEmbarkContinue, digest, snapshot != null && snapshot.IsActive && snapshot.CanContinue);
        }

        private void SyncAltarVoteContext(AltarSnapshotPayload snapshot)
        {
            bool isActive = snapshot != null && snapshot.IsActive && snapshot.CanEmbark;
            string digest = isActive ? snapshot.Digest : null;
            if (string.Equals(_altarVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _altarVoteDigest = digest;
            _altarEmbarkResolvedDigest = null;
            _altarEmbarkVotes.Clear();
            PublishVoteContext(VoteKeyAltarEmbark, digest, isActive);
        }

        private void SyncConfessionChoiceVoteContext(ConfessionChoiceSnapshotPayload snapshot)
        {
            bool isActive = snapshot != null &&
                snapshot.IsActive &&
                snapshot.CanChoose &&
                snapshot.Choices != null &&
                snapshot.Choices.Any(choice => choice != null && choice.IsSelectable);
            string digest = isActive ? snapshot.Digest : null;
            if (string.Equals(_confessionChoiceVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _confessionChoiceVoteDigest = digest;
            _confessionChoiceResolvedDigest = null;
            _confessionChoiceVotes.Clear();
            PublishVoteContext(VoteKeyConfessionChoice, digest, isActive);
        }

        private void SyncLairDecisionVoteContext(LairDecisionSnapshotPayload snapshot)
        {
            string digest = snapshot != null && snapshot.IsActive ? snapshot.Digest : null;
            if (string.Equals(_lairDecisionVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lairDecisionVoteDigest = digest;
            _lairDecisionResolvedDigest = null;
            _lairDecisionVotes.Clear();
            PublishVoteContext(VoteKeyLairDecision, digest, snapshot != null && snapshot.IsActive);
        }

        private void SyncConfirmationDialogVoteContext(ConfirmationDialogSnapshotPayload snapshot)
        {
            bool isActive = snapshot != null &&
                snapshot.IsActive &&
                snapshot.IsAllowed &&
                (snapshot.CanConfirm || snapshot.CanDecline);
            string digest = isActive ? snapshot.Digest : null;
            if (string.Equals(_confirmationDialogVoteDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _confirmationDialogVoteDigest = digest;
            _confirmationDialogResolvedDigest = null;
            _confirmationDialogVotes.Clear();
            PublishVoteContext(VoteKeyConfirmationDialog, digest, isActive);
        }

        private bool HasRouteChoiceOption(int optionIndex)
        {
            IList<RouteChoiceOptionPayload> choices = _latestRouteChoiceSnapshot == null
                ? null
                : _latestRouteChoiceSnapshot.Choices;
            return choices != null && choices.Any(choice => choice != null && choice.OptionIndex == optionIndex);
        }

        private bool TryFindStoryChoiceOption(StoryChoiceRequestPayload request, out StoryChoiceOptionPayload option)
        {
            option = null;
            IList<StoryChoiceOptionPayload> choices = _latestStoryChoiceSnapshot == null
                ? null
                : _latestStoryChoiceSnapshot.Choices;
            if (request == null || choices == null)
            {
                return false;
            }

            option = choices.FirstOrDefault(choice =>
                choice != null &&
                choice.OptionIndex == request.OptionIndex &&
                choice.HeroSlot == request.HeroSlot &&
                string.Equals(choice.ActorGuid ?? string.Empty, request.ActorGuid ?? string.Empty, StringComparison.Ordinal));
            return option != null;
        }

        private bool TryFindConfessionChoiceOption(ConfessionChoiceRequestPayload request, out ConfessionChoiceOptionPayload option)
        {
            option = null;
            IList<ConfessionChoiceOptionPayload> choices = _latestConfessionChoiceSnapshot == null
                ? null
                : _latestConfessionChoiceSnapshot.Choices;
            if (request == null || choices == null)
            {
                return false;
            }

            option = choices.FirstOrDefault(choice =>
                choice != null &&
                choice.OptionIndex == request.OptionIndex &&
                (string.IsNullOrWhiteSpace(request.BossId) ||
                    string.Equals(choice.BossId ?? string.Empty, request.BossId, StringComparison.Ordinal)));
            return option != null;
        }

        private List<CSteamID> GetCurrentVoters()
        {
            if (!_lobby.IsInLobby)
            {
                return new List<CSteamID>();
            }

            return _lobby.GetMembers()
                .OrderBy(member => member.m_SteamID)
                .ToList();
        }

        private List<CSteamID> GetLootVoters()
        {
            List<CSteamID> voters = GetCurrentVoters();
            if (_pvpModeState == null || !_pvpModeState.Enabled || _pvpModeState.EnemyControllerSteamId == 0UL)
            {
                return voters;
            }

            voters.RemoveAll(member => member.m_SteamID == _pvpModeState.EnemyControllerSteamId);
            return voters;
        }

        private bool IsPvpEnemyController(ulong steamId)
        {
            return steamId != 0UL &&
                _pvpModeState != null &&
                _pvpModeState.Enabled &&
                _pvpModeState.EnemyControllerSteamId == steamId;
        }

        private static bool HasVoter(IEnumerable<CSteamID> voters, ulong steamId)
        {
            return voters.Any(voter => voter.m_SteamID == steamId);
        }

        private static void RemoveVotesFromNonMembers<TPayload>(
            Dictionary<ulong, VoteRecord<TPayload>> votes,
            IEnumerable<CSteamID> voters)
            where TPayload : class
        {
            HashSet<ulong> memberIds = new HashSet<ulong>(voters.Select(voter => voter.m_SteamID));
            foreach (ulong steamId in votes.Keys.Where(steamId => !memberIds.Contains(steamId)).ToArray())
            {
                votes.Remove(steamId);
            }
        }

        private static string FormatInnVote(VoteRecord<InnActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            string action = vote.Payload.Action ?? "[none]";
            string optionText = vote.Payload.OptionIndex >= 0
                ? ":" + vote.Payload.OptionIndex
                : string.Empty;
            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) + "->" + action + optionText;
        }

        private static string FormatInnChoice(VoteRecord<InnActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            string action = vote.Payload.Action ?? "[none]";
            return vote.Payload.OptionIndex >= 0
                ? action + ":" + vote.Payload.OptionIndex
                : action;
        }

        private static string FormatEmbarkVote(VoteRecord<EmbarkActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + (vote.Payload.Action ?? "[none]");
        }

        private static string FormatEmbarkChoice(VoteRecord<EmbarkActionRequestPayload> vote)
        {
            return vote == null || vote.Payload == null
                ? "[none]"
                : vote.Payload.Action ?? "[none]";
        }

        private static string FormatAltarVote(VoteRecord<AltarActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + NormalizeAltarAction(vote.Payload);
        }

        private static string FormatAltarChoice(VoteRecord<AltarActionRequestPayload> vote)
        {
            return vote == null || vote.Payload == null
                ? "[none]"
                : NormalizeAltarAction(vote.Payload);
        }

        private static string FormatGameResultsVote(VoteRecord<GameResultsActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + (vote.Payload.Action ?? "[none]");
        }

        private static string FormatGameResultsChoice(VoteRecord<GameResultsActionRequestPayload> vote)
        {
            return vote == null || vote.Payload == null
                ? "[none]"
                : vote.Payload.Action ?? "[none]";
        }

        private static string FormatConfessionVote(VoteRecord<ConfessionChoiceRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + ConfessionChoiceVoteKey(vote.Payload);
        }

        private static string FormatConfessionChoice(VoteRecord<ConfessionChoiceRequestPayload> vote)
        {
            return ConfessionChoiceVoteKey(vote == null ? null : vote.Payload);
        }

        private static string FormatLairDecisionVote(VoteRecord<LairDecisionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + (vote.Payload.Action ?? "[none]");
        }

        private static string FormatLairDecisionChoice(VoteRecord<LairDecisionRequestPayload> vote)
        {
            return vote == null || vote.Payload == null
                ? "[none]"
                : vote.Payload.Action ?? "[none]";
        }

        private static string FormatConfirmationDialogVote(VoteRecord<ConfirmationDialogRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + (vote.Payload.Action ?? "[none]");
        }

        private static string FormatConfirmationDialogChoice(VoteRecord<ConfirmationDialogRequestPayload> vote)
        {
            return vote == null || vote.Payload == null
                ? "[none]"
                : vote.Payload.Action ?? "[none]";
        }

        private static string FormatLootVote(VoteRecord<LootActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            string action = vote.Payload.Action ?? "[none]";
            if (string.Equals(action, "take_selected", StringComparison.OrdinalIgnoreCase))
            {
                IList<LootActionItemPayload> items = vote.Payload.Items ?? Array.Empty<LootActionItemPayload>();
                string itemText = items.Count == 0
                    ? "[none]"
                    : string.Join("+", items.Select(item => "#" + item.InventoryIndex + "/" + (item.ItemId ?? "[item]")).ToArray());
                return FormatVoteSender(vote.SenderName, vote.SenderSteamId) + "->take_selected(" + itemText + ")";
            }

            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) + "->" + action;
        }

        private static string FormatLootChoice(VoteRecord<LootActionRequestPayload> vote)
        {
            if (vote == null || vote.Payload == null)
            {
                return "[none]";
            }

            string action = vote.Payload.Action ?? "[none]";
            if (!string.Equals(action, "take_selected", StringComparison.OrdinalIgnoreCase))
            {
                return action;
            }

            IList<LootActionItemPayload> items = vote.Payload.Items ?? Array.Empty<LootActionItemPayload>();
            string itemText = items.Count == 0
                ? "[none]"
                : string.Join("+", items.Select(item => "#" + item.InventoryIndex + "/" + (item.ItemId ?? "[item]")).ToArray());
            return "take_selected(" + itemText + ")";
        }

        private static string FormatRouteVote(VoteRecord<RouteChoiceRequestPayload> vote)
        {
            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + (vote.Payload == null ? -1 : vote.Payload.OptionIndex);
        }

        private static string FormatRouteChoice(VoteRecord<RouteChoiceRequestPayload> vote)
        {
            return "option " + (vote == null || vote.Payload == null ? -1 : vote.Payload.OptionIndex);
        }

        private static string FormatStoryVote(VoteRecord<StoryChoiceRequestPayload> vote)
        {
            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + StoryChoiceVoteKey(vote.Payload);
        }

        private static string FormatHeroReadyVote(VoteRecord<HeroSelectRequestPayload> vote)
        {
            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) + "->ready";
        }

        private static string FormatHeroReadyChoice(VoteRecord<HeroSelectRequestPayload> vote)
        {
            return "ready";
        }

        private static string FormatStoryChoice(VoteRecord<StoryChoiceRequestPayload> vote)
        {
            return StoryChoiceVoteKey(vote == null ? null : vote.Payload);
        }

        private static string FormatMainMenuVote(VoteRecord<MainMenuActionRequestPayload> vote)
        {
            return FormatVoteSender(vote.SenderName, vote.SenderSteamId) +
                "->" + FormatMainMenuAction(vote.Payload);
        }

        private static string FormatMainMenuChoice(VoteRecord<MainMenuActionRequestPayload> vote)
        {
            return FormatMainMenuAction(vote == null ? null : vote.Payload);
        }

        private static string FormatMainMenuAction(MainMenuActionRequestPayload request)
        {
            string action = NormalizeMainMenuAction(request);
            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                return "continue";
            }

            if (string.Equals(action, "start_new", StringComparison.Ordinal))
            {
                return "start_new";
            }

            return "[none]";
        }

        private static string NormalizeMainMenuAction(MainMenuActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return string.Empty;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            return string.Equals(action, "new", StringComparison.Ordinal)
                ? "start_new"
                : action;
        }

        private static string NormalizeAltarAction(AltarActionRequestPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return string.Empty;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "continue", StringComparison.Ordinal) ||
                string.Equals(action, "exit", StringComparison.Ordinal))
            {
                return "embark";
            }

            if (string.Equals(action, "track", StringComparison.Ordinal) ||
                string.Equals(action, "spend", StringComparison.Ordinal))
            {
                return "spend_track";
            }

            if (string.Equals(action, "reward", StringComparison.Ordinal) ||
                string.Equals(action, "purchase", StringComparison.Ordinal))
            {
                return "purchase_reward";
            }

            return action;
        }

        private static string StoryChoiceVoteKey(StoryChoiceRequestPayload request)
        {
            if (request == null)
            {
                return "-1/0/[none]";
            }

            return request.OptionIndex + "/" + request.HeroSlot + "/" + (request.ActorGuid ?? "[none]");
        }

        private static string ConfessionChoiceVoteKey(ConfessionChoiceRequestPayload request)
        {
            if (request == null)
            {
                return "-1/[none]";
            }

            return request.OptionIndex + "/" + (request.BossId ?? "[none]");
        }

        private static string FormatVoteSender(string senderName, ulong senderSteamId)
        {
            return string.IsNullOrWhiteSpace(senderName)
                ? senderSteamId.ToString()
                : senderName + "/" + senderSteamId;
        }

        private static string TrimProtocolText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || maxLength <= 0 || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        private void SendToHostOrEcho<TPayload>(MultiplayerMessageType type, TPayload payload)
        {
            if (_lobby.IsHost)
            {
                HostLog.Write("[me/protocol] " + type + ".");
                HandleLocalHostCommand(type, payload);
                return;
            }

            SendToHost(type, payload);
        }

        private void SendToHost<TPayload>(MultiplayerMessageType type, TPayload payload)
        {
            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[protocol] No lobby is active.");
                return;
            }

            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            string text = null;
            bool sent = false;
            try
            {
                text = MultiplayerProtocol.Serialize(type, NextSequence(), payload);
                sent = _lobby.SendRaw(_lobby.Owner, _transport, text);
            }
            finally
            {
                RecordProtocolPerf(
                    "send_host:" + type,
                    System.Diagnostics.Stopwatch.GetTimestamp() - startTicks,
                    text == null ? 0 : text.Length,
                    sent ? 1 : 0);
            }
        }

        private bool SendToMember<TPayload>(CSteamID target, MultiplayerMessageType type, TPayload payload)
        {
            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[protocol] No lobby is active.");
                return false;
            }

            if (!target.IsValid())
            {
                HostLog.Write("[protocol] Cannot send " + type + "; target member is invalid.");
                return false;
            }

            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            string text = null;
            bool sent = false;
            try
            {
                text = MultiplayerProtocol.Serialize(type, NextSequence(), payload);
                sent = _lobby.SendRaw(target, _transport, text);
                return sent;
            }
            finally
            {
                RecordProtocolPerf(
                    "send_member:" + type,
                    System.Diagnostics.Stopwatch.GetTimestamp() - startTicks,
                    text == null ? 0 : text.Length,
                    sent ? 1 : 0);
            }
        }

        private void Broadcast<TPayload>(MultiplayerMessageType type, TPayload payload, bool echoSelf)
        {
            if (!_lobby.IsInLobby)
            {
                HostLog.Write("[protocol] No lobby is active.");
                return;
            }

            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            string text = null;
            int sent = 0;
            try
            {
                text = MultiplayerProtocol.Serialize(type, NextSequence(), payload);
                if (ShouldFilterBroadcastForPvp(type, payload))
                {
                    IReadOnlyList<CSteamID> members = _lobby.GetMembers();
                    for (int i = 0; i < members.Count; i++)
                    {
                        CSteamID member = members[i];
                        if (member == SteamUser.GetSteamID() || ShouldSkipForPvpEnemyController(type, member, payload))
                        {
                            continue;
                        }

                        if (_lobby.SendRaw(member, _transport, text))
                        {
                            sent++;
                        }
                    }
                }
                else
                {
                    sent = _lobby.BroadcastRaw(_transport, text, false);
                }

                if (echoSelf)
                {
                    HostLog.Write("[me/protocol] " + type + " (sentTo=" + sent + ").");
                }
            }
            finally
            {
                RecordProtocolPerf(
                    "broadcast:" + type,
                    System.Diagnostics.Stopwatch.GetTimestamp() - startTicks,
                    text == null ? 0 : text.Length,
                sent);
            }
        }

        private bool ShouldFilterBroadcastForPvp<TPayload>(MultiplayerMessageType type, TPayload payload)
        {
            if (type == MultiplayerMessageType.HeroSelectSnapshot ||
                type == MultiplayerMessageType.HeroLoadoutSnapshot ||
                type == MultiplayerMessageType.LootWindowSnapshot)
            {
                return true;
            }

            if (type == MultiplayerMessageType.VoteStatus)
            {
                object boxedPayload = payload;
                VoteStatusPayload voteStatus = boxedPayload as VoteStatusPayload;
                return voteStatus != null &&
                    string.Equals(voteStatus.VoteKey, VoteKeyLoot, StringComparison.Ordinal);
            }

            return false;
        }

        private bool ShouldSkipForPvpEnemyController<TPayload>(MultiplayerMessageType type, CSteamID target, TPayload payload)
        {
            if (_pvpModeState == null ||
                !_pvpModeState.Enabled ||
                !_pvpModeState.SuppressHeroSyncForEnemyController ||
                _pvpModeState.EnemyControllerSteamId == 0UL ||
                target.m_SteamID != _pvpModeState.EnemyControllerSteamId)
            {
                return false;
            }

            return ShouldFilterBroadcastForPvp(type, payload);
        }

        private void RecordProtocolPerf(string label, long elapsedTicks, int chars, int sent)
        {
            if (!ProtocolPerfLoggingEnabled)
            {
                return;
            }

            double elapsedMs = StopwatchTicksToMilliseconds(elapsedTicks);
            ProtocolPerfStats stats;
            if (!_protocolPerfStats.TryGetValue(label, out stats))
            {
                stats = new ProtocolPerfStats();
                _protocolPerfStats[label] = stats;
            }

            stats.Calls++;
            stats.TotalMs += elapsedMs;
            stats.TotalSent += sent;
            if (chars > stats.MaxChars)
            {
                stats.MaxChars = chars;
            }

            if (elapsedMs > stats.MaxMs)
            {
                stats.MaxMs = elapsedMs;
            }

            if (elapsedMs >= ProtocolPerfSlowThresholdMs)
            {
                stats.SlowCalls++;
                HostLog.Write("[perf/send/slow] " + label +
                    " took " + FormatPerfMs(elapsedMs) +
                    " ms, chars=" + chars +
                    ", sent=" + sent + ".");
            }

            LogProtocolPerfSummaryIfDue();
        }

        private void LogProtocolPerfSummaryIfDue()
        {
            DateTime now = DateTime.UtcNow;
            if (_nextProtocolPerfSummaryUtc != DateTime.MinValue && now < _nextProtocolPerfSummaryUtc)
            {
                return;
            }

            _nextProtocolPerfSummaryUtc = now.Add(ProtocolPerfSummaryInterval);
            List<string> rows = _protocolPerfStats
                .Where(pair => pair.Value.Calls > 0)
                .OrderByDescending(pair => pair.Value.TotalMs)
                .ThenByDescending(pair => pair.Value.MaxMs)
                .Take(8)
                .Select(pair =>
                {
                    ProtocolPerfStats stats = pair.Value;
                    double averageMs = stats.TotalMs / Math.Max(1, stats.Calls);
                    return pair.Key +
                        " calls=" + stats.Calls +
                        " avg=" + FormatPerfMs(averageMs) +
                        " max=" + FormatPerfMs(stats.MaxMs) +
                        " slow=" + stats.SlowCalls +
                        " maxChars=" + stats.MaxChars +
                        " sent=" + stats.TotalSent;
                })
                .ToList();

            if (rows.Count > 0)
            {
                HostLog.Write("[perf/send] last " +
                    ProtocolPerfSummaryInterval.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                    "s top: " + string.Join("; ", rows.ToArray()));
            }

            foreach (ProtocolPerfStats stats in _protocolPerfStats.Values)
            {
                stats.Calls = 0;
                stats.SlowCalls = 0;
                stats.MaxChars = 0;
                stats.TotalSent = 0;
                stats.TotalMs = 0;
                stats.MaxMs = 0;
            }
        }

        private static double StopwatchTicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static string FormatPerfMs(double value)
        {
            return value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private long NextSequence()
        {
            return System.Threading.Interlocked.Increment(ref _sequence);
        }

        private void HandleLocalHostCommand<TPayload>(MultiplayerMessageType type, TPayload payload)
        {
            ulong senderSteamId = SteamUser.GetSteamID().m_SteamID;
            string senderName = SteamFriends.GetPersonaName() ?? string.Empty;

            switch (type)
            {
                case MultiplayerMessageType.ChooseSkill:
                    ChooseSkillPayload chooseSkill = payload as ChooseSkillPayload;
                    if (chooseSkill != null)
                    {
                        _turnCoordinator.HandleChooseSkill(senderSteamId, senderName, chooseSkill);
                    }

                    break;
                case MultiplayerMessageType.ChooseTarget:
                    ChooseTargetPayload chooseTarget = payload as ChooseTargetPayload;
                    if (chooseTarget != null)
                    {
                        _turnCoordinator.HandleChooseTarget(senderSteamId, senderName, chooseTarget);
                    }

                    break;
                case MultiplayerMessageType.PassTurn:
                    PassTurnPayload passTurn = payload as PassTurnPayload;
                    if (passTurn != null)
                    {
                        _turnCoordinator.HandlePassTurn(senderSteamId, senderName, passTurn);
                    }

                    break;
                case MultiplayerMessageType.LootActionRequest:
                    LootActionRequestPayload lootActionRequest = payload as LootActionRequestPayload;
                    if (lootActionRequest != null)
                    {
                        HandleLootActionRequest(senderSteamId, senderName, lootActionRequest);
                    }

                    break;
                case MultiplayerMessageType.RouteChoiceRequest:
                    RouteChoiceRequestPayload routeChoiceRequest = payload as RouteChoiceRequestPayload;
                    if (routeChoiceRequest != null)
                    {
                        HandleRouteChoiceRequest(senderSteamId, senderName, routeChoiceRequest);
                    }

                    break;
                case MultiplayerMessageType.HeroSelectRequest:
                    HeroSelectRequestPayload heroSelectRequest = payload as HeroSelectRequestPayload;
                    if (heroSelectRequest != null)
                    {
                        HandleHeroSelectRequest(senderSteamId, senderName, heroSelectRequest);
                    }

                    break;
                case MultiplayerMessageType.HeroLoadoutRequest:
                    HeroLoadoutRequestPayload heroLoadoutRequest = payload as HeroLoadoutRequestPayload;
                    if (heroLoadoutRequest != null)
                    {
                        HandleHeroLoadoutRequest(senderSteamId, senderName, heroLoadoutRequest);
                    }

                    break;
                case MultiplayerMessageType.StoryChoiceRequest:
                    StoryChoiceRequestPayload storyChoiceRequest = payload as StoryChoiceRequestPayload;
                    if (storyChoiceRequest != null)
                    {
                        HandleStoryChoiceRequest(senderSteamId, senderName, storyChoiceRequest);
                    }

                    break;
                case MultiplayerMessageType.InnActionRequest:
                    InnActionRequestPayload innActionRequest = payload as InnActionRequestPayload;
                    if (innActionRequest != null)
                    {
                        HandleInnActionRequest(senderSteamId, senderName, innActionRequest);
                    }

                    break;
                case MultiplayerMessageType.EmbarkActionRequest:
                    EmbarkActionRequestPayload embarkActionRequest = payload as EmbarkActionRequestPayload;
                    if (embarkActionRequest != null)
                    {
                        HandleEmbarkActionRequest(senderSteamId, senderName, embarkActionRequest);
                    }

                    break;
                case MultiplayerMessageType.AltarActionRequest:
                    AltarActionRequestPayload altarActionRequest = payload as AltarActionRequestPayload;
                    if (altarActionRequest != null)
                    {
                        HandleAltarActionRequest(senderSteamId, senderName, altarActionRequest);
                    }

                    break;
                case MultiplayerMessageType.ConfessionChoiceRequest:
                    ConfessionChoiceRequestPayload confessionChoiceRequest = payload as ConfessionChoiceRequestPayload;
                    if (confessionChoiceRequest != null)
                    {
                        HandleConfessionChoiceRequest(senderSteamId, senderName, confessionChoiceRequest);
                    }

                    break;
                case MultiplayerMessageType.GameResultsActionRequest:
                    GameResultsActionRequestPayload gameResultsActionRequest = payload as GameResultsActionRequestPayload;
                    if (gameResultsActionRequest != null)
                    {
                        HandleGameResultsActionRequest(senderSteamId, senderName, gameResultsActionRequest);
                    }

                    break;
                case MultiplayerMessageType.LairDecisionRequest:
                    LairDecisionRequestPayload lairDecisionRequest = payload as LairDecisionRequestPayload;
                    if (lairDecisionRequest != null)
                    {
                        HandleLairDecisionRequest(senderSteamId, senderName, lairDecisionRequest);
                    }

                    break;
                case MultiplayerMessageType.ConfirmationDialogRequest:
                    ConfirmationDialogRequestPayload confirmationDialogRequest = payload as ConfirmationDialogRequestPayload;
                    if (confirmationDialogRequest != null)
                    {
                        HandleConfirmationDialogRequest(senderSteamId, senderName, confirmationDialogRequest);
                    }

                    break;
                case MultiplayerMessageType.MainMenuActionRequest:
                    MainMenuActionRequestPayload mainMenuActionRequest = payload as MainMenuActionRequestPayload;
                    if (mainMenuActionRequest != null)
                    {
                        HandleMainMenuActionRequest(senderSteamId, senderName, mainMenuActionRequest);
                    }

                    break;
                case MultiplayerMessageType.StoreActionRequest:
                    StoreActionRequestPayload storeActionRequest = payload as StoreActionRequestPayload;
                    if (storeActionRequest != null)
                    {
                        HandleStoreActionRequest(senderSteamId, senderName, storeActionRequest);
                    }

                    break;
                case MultiplayerMessageType.StagecoachActionRequest:
                    StagecoachActionRequestPayload stagecoachActionRequest = payload as StagecoachActionRequestPayload;
                    if (stagecoachActionRequest != null)
                    {
                        HandleStagecoachActionRequest(senderSteamId, senderName, stagecoachActionRequest);
                    }

                    break;
                case MultiplayerMessageType.FullStateRequest:
                    FullStateRequestPayload fullStateRequest = payload as FullStateRequestPayload;
                    if (fullStateRequest != null)
                    {
                        BroadcastFullState(fullStateRequest.RequestId, fullStateRequest.Reason);
                    }

                    break;
            }
        }

        private sealed class VoteRecord<TPayload>
            where TPayload : class
        {
            public VoteRecord(ulong senderSteamId, string senderName, TPayload payload)
            {
                SenderSteamId = senderSteamId;
                SenderName = senderName;
                Payload = payload;
            }

            public ulong SenderSteamId { get; }

            public string SenderName { get; }

            public TPayload Payload { get; }
        }

        private static void PrintPayload<TPayload>(MultiplayerEnvelope envelope, Func<TPayload, string> format)
            where TPayload : class
        {
            TPayload payload;
            if (TryRead(envelope, out payload))
            {
                HostLog.Write("[protocol] " + envelope.SenderName + ": " + format(payload) + ".");
            }
        }

        private static bool TryRead<TPayload>(MultiplayerEnvelope envelope, out TPayload payload)
            where TPayload : class
        {
            string error;
            if (MultiplayerProtocol.TryReadPayload(envelope, out payload, out error))
            {
                return true;
            }

            HostLog.Write("[protocol] failed to read " + envelope.Type + " payload from " + envelope.SenderName + ": " + error + ".");
            return false;
        }
    }
}
