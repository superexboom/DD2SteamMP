using System;
using System.Collections.Generic;

namespace DD2SteamMultiplayerHost.Protocol
{
    internal sealed class HelloPayload
    {
        public HelloPayload()
        {
        }

        public HelloPayload(string protocolName, int maxHeroSlots)
            : this(protocolName, maxHeroSlots, 0, null)
        {
        }

        public HelloPayload(string protocolName, int maxHeroSlots, int protocolVersion, string lobbyVersion)
        {
            ProtocolName = protocolName;
            MaxHeroSlots = maxHeroSlots;
            ProtocolVersion = protocolVersion;
            LobbyVersion = lobbyVersion;
        }

        public string ProtocolName { get; set; }

        public int ProtocolVersion { get; set; }

        public string LobbyVersion { get; set; }

        public int MaxHeroSlots { get; set; }
    }

    internal sealed class LobbyStatePayload
    {
        public LobbyStatePayload()
        {
            Members = new List<LobbyMemberPayload>();
            HeroSlots = new List<HeroSlotAssignmentPayload>();
        }

        public LobbyStatePayload(
            ulong lobbyId,
            ulong hostSteamId,
            IList<LobbyMemberPayload> members,
            IList<HeroSlotAssignmentPayload> heroSlots)
        {
            LobbyId = lobbyId;
            HostSteamId = hostSteamId;
            Members = members;
            HeroSlots = heroSlots;
        }

        public ulong LobbyId { get; set; }

        public ulong HostSteamId { get; set; }

        public IList<LobbyMemberPayload> Members { get; set; }

        public IList<HeroSlotAssignmentPayload> HeroSlots { get; set; }
    }

    internal sealed class LobbyMemberPayload
    {
        public LobbyMemberPayload()
        {
        }

        public LobbyMemberPayload(ulong steamId, string name, bool isHost)
        {
            SteamId = steamId;
            Name = name;
            IsHost = isHost;
        }

        public ulong SteamId { get; set; }

        public string Name { get; set; }

        public bool IsHost { get; set; }
    }

    internal sealed class HeroSlotAssignmentPayload
    {
        public HeroSlotAssignmentPayload()
        {
        }

        public HeroSlotAssignmentPayload(int slot, ulong steamId, string name)
        {
            Slot = slot;
            SteamId = steamId;
            Name = name;
        }

        public int Slot { get; set; }

        public ulong SteamId { get; set; }

        public string Name { get; set; }
    }

    internal sealed class AssignHeroSlotPayload
    {
        public AssignHeroSlotPayload()
        {
        }

        public AssignHeroSlotPayload(int slot, ulong steamId, string name)
        {
            Slot = slot;
            SteamId = steamId;
            Name = name;
        }

        public int Slot { get; set; }

        public ulong SteamId { get; set; }

        public string Name { get; set; }
    }

    internal sealed class TurnPromptPayload
    {
        public TurnPromptPayload()
        {
            SkillOptions = new List<TurnSkillOptionPayload>();
            ControlRole = "hero";
        }

        public TurnPromptPayload(
            int round,
            int turn,
            int heroSlot,
            string actorGuid,
            string actorName,
            IList<TurnSkillOptionPayload> skillOptions = null)
            : this(
                round,
                turn,
                heroSlot,
                actorGuid,
                actorName,
                0,
                heroSlot - 1,
                "hero",
                0UL,
                null,
                skillOptions)
        {
        }

        public TurnPromptPayload(
            int round,
            int turn,
            int heroSlot,
            string actorGuid,
            string actorName,
            int teamIndex,
            int teamPosition,
            string controlRole,
            ulong ownerSteamId,
            string ownerName,
            IList<TurnSkillOptionPayload> skillOptions = null)
        {
            Round = round;
            Turn = turn;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            ActorName = actorName;
            TeamIndex = teamIndex;
            TeamPosition = teamPosition;
            ControlRole = string.IsNullOrWhiteSpace(controlRole) ? "hero" : controlRole;
            OwnerSteamId = ownerSteamId;
            OwnerName = ownerName;
            SkillOptions = skillOptions ?? new List<TurnSkillOptionPayload>();
        }

        public int Round { get; set; }

        public int Turn { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string ActorName { get; set; }

        public int TeamIndex { get; set; }

        public int TeamPosition { get; set; }

        public string ControlRole { get; set; }

        public ulong OwnerSteamId { get; set; }

        public string OwnerName { get; set; }

        public IList<TurnSkillOptionPayload> SkillOptions { get; set; }
    }

    internal sealed class PvpModeStatePayload
    {
        public PvpModeStatePayload()
        {
        }

        public PvpModeStatePayload(
            bool enabled,
            string mode,
            ulong enemyControllerSteamId,
            string enemyControllerName,
            bool runtimeEnemyInput,
            bool suppressHeroSyncForEnemyController,
            string digest)
        {
            Enabled = enabled;
            Mode = mode;
            EnemyControllerSteamId = enemyControllerSteamId;
            EnemyControllerName = enemyControllerName;
            RuntimeEnemyInput = runtimeEnemyInput;
            SuppressHeroSyncForEnemyController = suppressHeroSyncForEnemyController;
            Digest = digest;
        }

        public bool Enabled { get; set; }

        public string Mode { get; set; }

        public ulong EnemyControllerSteamId { get; set; }

        public string EnemyControllerName { get; set; }

        public bool RuntimeEnemyInput { get; set; }

        public bool SuppressHeroSyncForEnemyController { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class TurnSkillOptionPayload
    {
        public TurnSkillOptionPayload()
        {
            Targets = new List<TurnTargetOptionPayload>();
        }

        public TurnSkillOptionPayload(string skillId, string displayName, IList<TurnTargetOptionPayload> targets, string description = null)
        {
            SkillId = skillId;
            DisplayName = displayName;
            Targets = targets ?? new List<TurnTargetOptionPayload>();
            Description = description;
        }

        public string SkillId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public IList<TurnTargetOptionPayload> Targets { get; set; }
    }

    internal sealed class TurnTargetOptionPayload
    {
        public TurnTargetOptionPayload()
        {
        }

        public TurnTargetOptionPayload(string actorGuid, string displayName, int teamIndex, int teamPosition)
        {
            ActorGuid = actorGuid;
            DisplayName = displayName;
            TeamIndex = teamIndex;
            TeamPosition = teamPosition;
        }

        public string ActorGuid { get; set; }

        public string DisplayName { get; set; }

        public int TeamIndex { get; set; }

        public int TeamPosition { get; set; }
    }

    internal sealed class ChooseSkillPayload
    {
        public ChooseSkillPayload()
        {
        }

        public ChooseSkillPayload(int heroSlot, string actorGuid, string skillId)
        {
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            SkillId = skillId;
        }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string SkillId { get; set; }
    }

    internal sealed class ChooseTargetPayload
    {
        public ChooseTargetPayload()
        {
        }

        public ChooseTargetPayload(int heroSlot, string actorGuid, string targetGuid)
        {
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            TargetGuid = targetGuid;
        }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string TargetGuid { get; set; }
    }

    internal sealed class PassTurnPayload
    {
        public PassTurnPayload()
        {
        }

        public PassTurnPayload(int heroSlot, string actorGuid)
        {
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
        }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }
    }

    internal sealed class ClearTurnPayload
    {
        public ClearTurnPayload()
        {
        }

        public ClearTurnPayload(int round, int turn, int heroSlot, string actorGuid, string reason)
        {
            Round = round;
            Turn = turn;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            Reason = reason;
        }

        public int Round { get; set; }

        public int Turn { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string Reason { get; set; }
    }

    internal sealed class CombatSnapshotPayload
    {
        public CombatSnapshotPayload()
        {
            Actors = new List<ActorSnapshotPayload>();
            TurnOrder = new List<CombatTurnOrderEntryPayload>();
        }

        public int Round { get; set; }

        public int Turn { get; set; }

        public string BattleState { get; set; }

        public string NextState { get; set; }

        public bool PartyInBattle { get; set; }

        public bool HasCurrentActor { get; set; }

        public string CurrentActorGuid { get; set; }

        public string CurrentActorName { get; set; }

        public string CurrentFirstTurnActorGuid { get; set; }

        public string CurrentLastTurnActorGuid { get; set; }

        public CombatSelectedSkillPayload SelectedSkill { get; set; }

        public IList<CombatTurnOrderEntryPayload> TurnOrder { get; set; }

        public IList<ActorSnapshotPayload> Actors { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class CombatTurnOrderEntryPayload
    {
        public int Index { get; set; }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string DisplayName { get; set; }

        public int TeamIndex { get; set; }

        public int TeamPosition { get; set; }

        public bool IsCurrentActor { get; set; }

        public bool IsFirstNormalTurn { get; set; }

        public bool IsLastNormalTurn { get; set; }

        public bool IsMissingActor { get; set; }
    }

    internal sealed class CombatSelectedSkillPayload
    {
        public CombatSelectedSkillPayload()
        {
            ValidTargets = new List<TurnTargetOptionPayload>();
            StealthedTargets = new List<TurnTargetOptionPayload>();
        }

        public string SkillId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public IList<TurnTargetOptionPayload> ValidTargets { get; set; }

        public IList<TurnTargetOptionPayload> StealthedTargets { get; set; }
    }

    internal sealed class ActorSnapshotPayload
    {
        public ActorSnapshotPayload()
        {
            Tokens = new List<StatusSnapshotPayload>();
            Buffs = new List<StatusSnapshotPayload>();
            Dots = new List<StatusSnapshotPayload>();
        }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string DisplayName { get; set; }

        public int TeamIndex { get; set; }

        public int TeamPosition { get; set; }

        public bool IsLiving { get; set; }

        public bool IsDeathsDoor { get; set; }

        public int Health { get; set; }

        public int MaxHealth { get; set; }

        public int Stress { get; set; }

        public int StressMax { get; set; }

        public IList<StatusSnapshotPayload> Tokens { get; set; }

        public IList<StatusSnapshotPayload> Buffs { get; set; }

        public IList<StatusSnapshotPayload> Dots { get; set; }
    }

    internal sealed class StatusSnapshotPayload
    {
        public StatusSnapshotPayload()
        {
        }

        public StatusSnapshotPayload(string id, int count, int duration, string displayName, string description = null, string kind = null)
        {
            Id = id;
            Count = count;
            Duration = duration;
            DisplayName = displayName;
            Description = description;
            Kind = kind;
        }

        public string Id { get; set; }

        public int Count { get; set; }

        public int Duration { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string Kind { get; set; }
    }

    internal sealed class BattleResultPayload
    {
        public BattleResultPayload()
        {
            LootRewards = new List<LootRewardPayload>();
        }

        public int EventId { get; set; }

        public bool IsFightComplete { get; set; }

        public bool IsBattleSequenceComplete { get; set; }

        public bool HasNextBattle { get; set; }

        public bool IsForceEnd { get; set; }

        public bool IsRetreat { get; set; }

        public bool IsBiomeBossBattle { get; set; }

        public bool IsExpeditionBossBattle { get; set; }

        public bool IsGangBossBattle { get; set; }

        public string LootReason { get; set; }

        public string LootReasonId { get; set; }

        public string CombatSource { get; set; }

        public string NodeSubType { get; set; }

        public string CurrentBattleConfigurationId { get; set; }

        public int CurrentBattleConfigurationIndex { get; set; }

        public int NextBattleConfigurationIndex { get; set; }

        public IList<LootRewardPayload> LootRewards { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class LootRewardPayload
    {
        public LootRewardPayload()
        {
        }

        public LootRewardPayload(string type, string id, int quantity)
        {
            Type = type;
            Id = id;
            Quantity = quantity;
        }

        public string Type { get; set; }

        public string Id { get; set; }

        public int Quantity { get; set; }
    }

    internal sealed class LootWindowSnapshotPayload
    {
        public LootWindowSnapshotPayload()
        {
            Items = new List<LootItemSnapshotPayload>();
            SkillsGranted = new List<string>();
        }

        public bool IsActive { get; set; }

        public string ScreenState { get; set; }

        public string Reason { get; set; }

        public string ReasonId { get; set; }

        public int HeroPoints { get; set; }

        public int TorchGain { get; set; }

        public int ArmorGain { get; set; }

        public int WheelGain { get; set; }

        public bool HeroDied { get; set; }

        public bool CanTakeAll { get; set; }

        public IList<LootItemSnapshotPayload> Items { get; set; }

        public IList<string> SkillsGranted { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class LootItemSnapshotPayload
    {
        public LootItemSnapshotPayload()
        {
        }

        public LootItemSnapshotPayload(string itemId, string itemType, string slotType, int quantity, int duration, string displayName, int inventoryIndex = -1)
        {
            ItemId = itemId;
            ItemType = itemType;
            SlotType = slotType;
            Quantity = quantity;
            Duration = duration;
            DisplayName = displayName;
            InventoryIndex = inventoryIndex;
        }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public string ItemType { get; set; }

        public string SlotType { get; set; }

        public int Quantity { get; set; }

        public int Duration { get; set; }

        public string DisplayName { get; set; }
    }

    internal sealed class LootActionRequestPayload
    {
        public LootActionRequestPayload()
        {
            Items = new List<LootActionItemPayload>();
        }

        public LootActionRequestPayload(string requestId, string action, string itemId, int quantity, int inventoryIndex = -1)
        {
            RequestId = requestId;
            Action = action;
            ItemId = itemId;
            Quantity = quantity;
            InventoryIndex = inventoryIndex;
            Items = new List<LootActionItemPayload>();
        }

        public LootActionRequestPayload(
            string requestId,
            string action,
            IList<LootActionItemPayload> items)
            : this(requestId, action, null, 0, -1)
        {
            Items = items ?? new List<LootActionItemPayload>();
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public string ItemId { get; set; }

        public int InventoryIndex { get; set; }

        public int Quantity { get; set; }

        public IList<LootActionItemPayload> Items { get; set; }
    }

    internal sealed class LootActionItemPayload
    {
        public LootActionItemPayload()
        {
        }

        public LootActionItemPayload(string itemId, int quantity, int inventoryIndex)
        {
            ItemId = itemId;
            Quantity = quantity;
            InventoryIndex = inventoryIndex;
        }

        public string ItemId { get; set; }

        public int Quantity { get; set; }

        public int InventoryIndex { get; set; }
    }

    internal sealed class LootActionResultPayload
    {
        public LootActionResultPayload()
        {
        }

        public LootActionResultPayload(string requestId, string action, ulong senderSteamId, string senderName, bool accepted, string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class GameResultsSnapshotPayload
    {
        public bool IsActive { get; set; }

        public string ScreenState { get; set; }

        public string GameOverReason { get; set; }

        public bool HasScore { get; set; }

        public bool CanContinue { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class GameResultsActionRequestPayload
    {
        public GameResultsActionRequestPayload()
        {
        }

        public GameResultsActionRequestPayload(string requestId, string action)
        {
            RequestId = requestId;
            Action = action;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }
    }

    internal sealed class GameResultsActionResultPayload
    {
        public GameResultsActionResultPayload()
        {
        }

        public GameResultsActionResultPayload(
            string requestId,
            string action,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class RouteChoiceSnapshotPayload
    {
        public RouteChoiceSnapshotPayload()
        {
            Choices = new List<RouteChoiceOptionPayload>();
        }

        public bool IsActive { get; set; }

        public int ChoiceCount { get; set; }

        public int SelectedOptionIndex { get; set; }

        public IList<RouteChoiceOptionPayload> Choices { get; set; }

        public bool ChoiceOverruleEnabled { get; set; }

        public int ChoiceOverruleLimitPerMap { get; set; }

        public int ChoiceOverruleRemaining { get; set; }

        public string ChoiceOverruleMapKey { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class RouteChoiceOptionPayload
    {
        public RouteChoiceOptionPayload()
        {
        }

        public RouteChoiceOptionPayload(
            int optionIndex,
            int nodeIndexInRow,
            string nodeType,
            string nodeSubType,
            bool isRevealed,
            string direction)
        {
            OptionIndex = optionIndex;
            NodeIndexInRow = nodeIndexInRow;
            NodeType = nodeType;
            NodeSubType = nodeSubType;
            IsRevealed = isRevealed;
            Direction = direction;
        }

        public int OptionIndex { get; set; }

        public int NodeIndexInRow { get; set; }

        public string NodeType { get; set; }

        public string NodeSubType { get; set; }

        public bool IsRevealed { get; set; }

        public string Direction { get; set; }
    }

    internal sealed class RouteChoiceRequestPayload
    {
        public RouteChoiceRequestPayload()
        {
        }

        public RouteChoiceRequestPayload(string requestId, int optionIndex, bool isForced = false)
        {
            RequestId = requestId;
            OptionIndex = optionIndex;
            IsForced = isForced;
        }

        public string RequestId { get; set; }

        public int OptionIndex { get; set; }

        public bool IsForced { get; set; }
    }

    internal sealed class RouteChoiceResultPayload
    {
        public RouteChoiceResultPayload()
        {
        }

        public RouteChoiceResultPayload(
            string requestId,
            int optionIndex,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            OptionIndex = optionIndex;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public int OptionIndex { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class HeroSelectSnapshotPayload
    {
        public HeroSelectSnapshotPayload()
        {
            Slots = new List<HeroSelectSlotPayload>();
            Heroes = new List<HeroSelectHeroPayload>();
        }

        public bool IsActive { get; set; }

        public bool RosterConfirmed { get; set; }

        public bool CanConfirm { get; set; }

        public string SelectedActorGuid { get; set; }

        public string SelectedPathId { get; set; }

        public IList<HeroSelectSlotPayload> Slots { get; set; }

        public IList<HeroSelectHeroPayload> Heroes { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class HeroSelectSlotPayload
    {
        public HeroSelectSlotPayload()
        {
        }

        public HeroSelectSlotPayload(
            int slotIndex,
            int heroSlot,
            string actorGuid,
            string actorDataId,
            string actorName,
            string pathId)
        {
            SlotIndex = slotIndex;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            ActorDataId = actorDataId;
            ActorName = actorName;
            PathId = pathId;
        }

        public int SlotIndex { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string ActorName { get; set; }

        public string PathId { get; set; }

        public ulong OwnerSteamId { get; set; }

        public string OwnerName { get; set; }
    }

    internal sealed class HeroSelectHeroPayload
    {
        public HeroSelectHeroPayload()
        {
            Paths = new List<HeroSelectPathPayload>();
        }

        public HeroSelectHeroPayload(
            string actorGuid,
            string actorDataId,
            string actorName,
            string pathId,
            bool isSelected,
            bool isKingdomPreferred,
            IList<HeroSelectPathPayload> paths = null)
        {
            ActorGuid = actorGuid;
            ActorDataId = actorDataId;
            ActorName = actorName;
            PathId = pathId;
            IsSelected = isSelected;
            IsKingdomPreferred = isKingdomPreferred;
            Paths = paths ?? new List<HeroSelectPathPayload>();
        }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string ActorName { get; set; }

        public string PathId { get; set; }

        public bool IsSelected { get; set; }

        public bool IsKingdomPreferred { get; set; }

        public IList<HeroSelectPathPayload> Paths { get; set; }
    }

    internal sealed class HeroSelectPathPayload
    {
        public HeroSelectPathPayload()
        {
        }

        public HeroSelectPathPayload(string pathId, string displayName, bool isCurrent)
        {
            PathId = pathId;
            DisplayName = displayName;
            IsCurrent = isCurrent;
        }

        public string PathId { get; set; }

        public string DisplayName { get; set; }

        public bool IsCurrent { get; set; }
    }

    internal sealed class HeroSelectRequestPayload
    {
        public HeroSelectRequestPayload()
        {
        }

        public HeroSelectRequestPayload(string requestId, string action, int slotIndex, string actorGuid, string pathId = null)
        {
            RequestId = requestId;
            Action = action;
            SlotIndex = slotIndex;
            ActorGuid = actorGuid;
            PathId = pathId;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int SlotIndex { get; set; }

        public string ActorGuid { get; set; }

        public string PathId { get; set; }
    }

    internal sealed class HeroSelectResultPayload
    {
        public HeroSelectResultPayload()
        {
        }

        public HeroSelectResultPayload(
            string requestId,
            string action,
            int slotIndex,
            string actorGuid,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SlotIndex = slotIndex;
            ActorGuid = actorGuid;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int SlotIndex { get; set; }

        public string ActorGuid { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class HeroLoadoutSnapshotPayload
    {
        public HeroLoadoutSnapshotPayload()
        {
            Actors = new List<HeroLoadoutActorPayload>();
            InventoryItems = new List<HeroLoadoutItemPayload>();
        }

        public bool IsActive { get; set; }

        public string Scope { get; set; }

        public string CurrentGameMode { get; set; }

        public int HeroUpgradePoints { get; set; }

        public bool CanMasterSkills { get; set; }

        public IList<HeroLoadoutItemPayload> InventoryItems { get; set; }

        public IList<HeroLoadoutActorPayload> Actors { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class HeroLoadoutActorPayload
    {
        public HeroLoadoutActorPayload()
        {
            Skills = new List<HeroLoadoutSkillPayload>();
            Trinkets = new List<HeroLoadoutItemPayload>();
            CombatItems = new List<HeroLoadoutItemPayload>();
        }

        public int HeroSlot { get; set; }

        public int TeamPosition { get; set; }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string ActorName { get; set; }

        public string PathId { get; set; }

        public ulong OwnerSteamId { get; set; }

        public string OwnerName { get; set; }

        public int EquippedSkillCount { get; set; }

        public int EquippedSkillLimit { get; set; }

        public bool CanEditSkills { get; set; }

        public IList<HeroLoadoutItemPayload> Trinkets { get; set; }

        public IList<HeroLoadoutItemPayload> CombatItems { get; set; }

        public IList<HeroLoadoutSkillPayload> Skills { get; set; }
    }

    internal sealed class HeroLoadoutItemPayload
    {
        public HeroLoadoutItemPayload()
        {
        }

        public HeroLoadoutItemPayload(
            string itemKind,
            int inventoryIndex,
            string itemId,
            string displayName,
            string itemType,
            int quantity,
            bool isEmpty,
            bool canEquip,
            bool canUseRestItem = false,
            int restTargetCount = 0,
            int restSelectableTargetCount = 0,
            bool isRandomRestTarget = false,
            string description = null)
        {
            ItemKind = itemKind;
            InventoryIndex = inventoryIndex;
            ItemId = itemId;
            DisplayName = displayName;
            ItemType = itemType;
            Quantity = quantity;
            IsEmpty = isEmpty;
            CanEquip = canEquip;
            CanUseRestItem = canUseRestItem;
            RestTargetCount = restTargetCount;
            RestSelectableTargetCount = restSelectableTargetCount;
            IsRandomRestTarget = isRandomRestTarget;
            Description = description;
        }

        public string ItemKind { get; set; }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string ItemType { get; set; }

        public int Quantity { get; set; }

        public bool IsEmpty { get; set; }

        public bool CanEquip { get; set; }

        public bool CanUseRestItem { get; set; }

        public int RestTargetCount { get; set; }

        public int RestSelectableTargetCount { get; set; }

        public bool IsRandomRestTarget { get; set; }
    }

    internal sealed class HeroLoadoutSkillPayload
    {
        public HeroLoadoutSkillPayload()
        {
        }

        public HeroLoadoutSkillPayload(
            string skillId,
            string displayName,
            bool isEquipped,
            bool isUnlocked,
            bool isUpgraded,
            bool isAlwaysEquipped,
            bool canEquip,
            bool canUnequip,
            bool canMaster,
            string masteredSkillId,
            string description = null)
        {
            SkillId = skillId;
            DisplayName = displayName;
            IsEquipped = isEquipped;
            IsUnlocked = isUnlocked;
            IsUpgraded = isUpgraded;
            IsAlwaysEquipped = isAlwaysEquipped;
            CanEquip = canEquip;
            CanUnequip = canUnequip;
            CanMaster = canMaster;
            MasteredSkillId = masteredSkillId;
            Description = description;
        }

        public string SkillId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public bool IsEquipped { get; set; }

        public bool IsUnlocked { get; set; }

        public bool IsUpgraded { get; set; }

        public bool IsAlwaysEquipped { get; set; }

        public bool CanEquip { get; set; }

        public bool CanUnequip { get; set; }

        public bool CanMaster { get; set; }

        public string MasteredSkillId { get; set; }
    }

    internal sealed class HeroLoadoutRequestPayload
    {
        public HeroLoadoutRequestPayload()
        {
            TargetActorGuids = new List<string>();
        }

        public HeroLoadoutRequestPayload(
            string requestId,
            string action,
            int heroSlot,
            string actorGuid,
            string skillId,
            bool equip)
        {
            RequestId = requestId;
            Action = action;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            SkillId = skillId;
            Equip = equip;
            TargetActorGuids = new List<string>();
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string SkillId { get; set; }

        public bool Equip { get; set; }

        public string ItemKind { get; set; }

        public int SourceInventoryIndex { get; set; }

        public int TargetInventoryIndex { get; set; }

        public string ItemId { get; set; }

        public IList<string> TargetActorGuids { get; set; }
    }

    internal sealed class HeroLoadoutResultPayload
    {
        public HeroLoadoutResultPayload()
        {
        }

        public HeroLoadoutResultPayload(
            string requestId,
            string action,
            int heroSlot,
            string actorGuid,
            string skillId,
            bool equip,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            SkillId = skillId;
            Equip = equip;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string SkillId { get; set; }

        public bool Equip { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class RunStateSnapshotPayload
    {
        public RunStateSnapshotPayload()
        {
            Party = new List<RunStatePartyActorPayload>();
        }

        public string CurrentGameMode { get; set; }

        public bool IsGameModeChanging { get; set; }

        public bool IsEnteringState { get; set; }

        public bool IsExitingState { get; set; }

        public string CurrentGameType { get; set; }

        public bool IsGameTypeStarted { get; set; }

        public bool IsRunStarted { get; set; }

        public string RunStartType { get; set; }

        public string MapState { get; set; }

        public bool IsInDrivingState { get; set; }

        public string BiomeType { get; set; }

        public string BiomeSubType { get; set; }

        public int BiomeIndex { get; set; }

        public int BiomeRowIndex { get; set; }

        public int LastVisitedBiomeRowIndex { get; set; }

        public int LastVisitedNodeIndex { get; set; }

        public string LastVisitedNodeType { get; set; }

        public bool ProgressIsValid { get; set; }

        public bool ProgressAtNode { get; set; }

        public int ProgressBiomeIndex { get; set; }

        public int ProgressRowIndex { get; set; }

        public int ProgressIndex { get; set; }

        public int ProgressRowCount { get; set; }

        public float ProgressBiomeTravelRatio { get; set; }

        public float ProgressBetweenRowsRatio { get; set; }

        public float ProgressBetweenBiomesRatio { get; set; }

        public IList<RunStatePartyActorPayload> Party { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class RunStatePartyActorPayload
    {
        public RunStatePartyActorPayload()
        {
        }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string ActorName { get; set; }

        public string PathId { get; set; }

        public int TeamPosition { get; set; }
    }

    internal sealed class ExpeditionOverviewSnapshotPayload
    {
        public ExpeditionOverviewSnapshotPayload()
        {
            Currencies = new List<ExpeditionCurrencyPayload>();
            InventoryItems = new List<ExpeditionItemPayload>();
            StagecoachItems = new List<ExpeditionItemPayload>();
            Heroes = new List<ExpeditionHeroPayload>();
            Relationships = new List<ExpeditionRelationshipPayload>();
        }

        public bool IsActive { get; set; }

        public string CurrentGameMode { get; set; }

        public string CurrentGameType { get; set; }

        public bool IsGameTypeStarted { get; set; }

        public bool IsRunStarted { get; set; }

        public string RunStartType { get; set; }

        public string MapState { get; set; }

        public string BiomeType { get; set; }

        public string BiomeSubType { get; set; }

        public int Relics { get; set; }

        public int Baubles { get; set; }

        public int Candles { get; set; }

        public int MasteryPoints { get; set; }

        public int Torch { get; set; }

        public int TorchMax { get; set; }

        public int Loathing { get; set; }

        public int LoathingMax { get; set; }

        public int Armor { get; set; }

        public int ArmorMax { get; set; }

        public int Wheels { get; set; }

        public int WheelsMax { get; set; }

        public int InventoryFilledSlots { get; set; }

        public int InventoryTotalSlots { get; set; }

        public IList<ExpeditionCurrencyPayload> Currencies { get; set; }

        public IList<ExpeditionItemPayload> InventoryItems { get; set; }

        public IList<ExpeditionItemPayload> StagecoachItems { get; set; }

        public IList<ExpeditionHeroPayload> Heroes { get; set; }

        public IList<ExpeditionRelationshipPayload> Relationships { get; set; }

        public ExpeditionBiomeGoalPayload BiomeGoal { get; set; }

        public ExpeditionBiomeModifierPayload BiomeModifier { get; set; }

        public ExpeditionCombatScenarioPayload CombatScenario { get; set; }

        public ExpeditionMapProgressPayload MapProgress { get; set; }

        public ExpeditionMapRoutePayload MapRoute { get; set; }

        public ExpeditionMapNodePayload LastVisitedNode { get; set; }

        public ExpeditionMapNodePayload LastCompletedNode { get; set; }

        public bool ChoiceOverruleEnabled { get; set; }

        public int ChoiceOverruleLimitPerMap { get; set; }

        public int ChoiceOverruleRemaining { get; set; }

        public string ChoiceOverruleMapKey { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class ExpeditionCombatScenarioPayload
    {
        public ExpeditionCombatScenarioPayload()
        {
            BattleConfigurationIds = new List<string>();
            EnemyActorIds = new List<string>();
            Tags = new List<string>();
        }

        public bool IsActive { get; set; }

        public bool IsLoadStarted { get; set; }

        public bool IsLoading { get; set; }

        public bool IsLoaded { get; set; }

        public bool IsUnloading { get; set; }

        public bool IsUnloaded { get; set; }

        public bool IsLoadingCombatIntro { get; set; }

        public string CombatSource { get; set; }

        public string NodeType { get; set; }

        public string NodeSubType { get; set; }

        public string BackgroundSceneName { get; set; }

        public string CurrentBattleConfigurationId { get; set; }

        public string AdditionalBattleConfigurationId { get; set; }

        public int CurrentBattleConfigurationIndex { get; set; }

        public int CurrentBattleNumber { get; set; }

        public int TotalNumberOfBattles { get; set; }

        public int RemainingNumberOfBattles { get; set; }

        public bool HasNextBattle { get; set; }

        public bool HasAdditionalBattle { get; set; }

        public bool IsNextBattleOptional { get; set; }

        public bool IsExpeditionBoss { get; set; }

        public uint BiomeKillContractGuid { get; set; }

        public string StoryActorGuid { get; set; }

        public string StoryActorDataId { get; set; }

        public string StoryChoiceId { get; set; }

        public int StoryRetryCount { get; set; }

        public IList<string> BattleConfigurationIds { get; set; }

        public IList<string> EnemyActorIds { get; set; }

        public IList<string> Tags { get; set; }
    }

    internal sealed class ExpeditionMapNodePayload
    {
        public string Role { get; set; }

        public int RowIndex { get; set; }

        public int NodeIndex { get; set; }

        public string NodeType { get; set; }

        public string NodeSubType { get; set; }

        public int OutgoingPathCount { get; set; }

        public int IncomingPathCount { get; set; }
    }

    internal sealed class ExpeditionMapProgressPayload
    {
        public bool IsValid { get; set; }

        public bool IsAtNode { get; set; }

        public int BiomeIndex { get; set; }

        public int RowIndex { get; set; }

        public int NodeIndex { get; set; }

        public int RowCount { get; set; }

        public float BiomeTravelRatio { get; set; }

        public float BetweenRowsRatio { get; set; }

        public float BetweenBiomesRatio { get; set; }
    }

    internal sealed class ExpeditionMapRoutePayload
    {
        public ExpeditionMapRoutePayload()
        {
            Rows = new List<ExpeditionMapRouteRowPayload>();
        }

        public int BiomeIndex { get; set; }

        public int CurrentRowIndex { get; set; }

        public int CurrentNodeIndex { get; set; }

        public int LastVisitedRowIndex { get; set; }

        public int LastVisitedNodeIndex { get; set; }

        public int LastCompletedRowIndex { get; set; }

        public int LastCompletedNodeIndex { get; set; }

        public int RowCount { get; set; }

        public int NodeCount { get; set; }

        public int RevealedNodeCount { get; set; }

        public int LinkCount { get; set; }

        public int RevealedLinkCount { get; set; }

        public IList<ExpeditionMapRouteRowPayload> Rows { get; set; }
    }

    internal sealed class ExpeditionMapRouteRowPayload
    {
        public ExpeditionMapRouteRowPayload()
        {
            Nodes = new List<ExpeditionMapRouteNodePayload>();
            Links = new List<ExpeditionMapRouteLinkPayload>();
        }

        public int RowIndex { get; set; }

        public bool IsCurrentRow { get; set; }

        public bool IsLastVisitedRow { get; set; }

        public bool IsLastCompletedRow { get; set; }

        public IList<ExpeditionMapRouteNodePayload> Nodes { get; set; }

        public IList<ExpeditionMapRouteLinkPayload> Links { get; set; }
    }

    internal sealed class ExpeditionMapRouteNodePayload
    {
        public int NodeIndex { get; set; }

        public string NodeType { get; set; }

        public string NodeSubType { get; set; }

        public bool IsGenerated { get; set; }

        public bool IsRevealed { get; set; }

        public bool IsCurrentNode { get; set; }

        public bool IsLastVisitedNode { get; set; }

        public bool IsLastCompletedNode { get; set; }

        public bool HasBiomeKillContract { get; set; }

        public uint BiomeKillContractGuid { get; set; }
    }

    internal sealed class ExpeditionMapRouteLinkPayload
    {
        public int FromNodeIndex { get; set; }

        public int ToNodeIndex { get; set; }

        public string RouteId { get; set; }

        public string RouteType { get; set; }

        public bool IsRevealed { get; set; }

        public bool IsChosen { get; set; }
    }

    internal sealed class ExpeditionBiomeGoalPayload
    {
        public ExpeditionBiomeGoalPayload()
        {
            TypeStrings = new List<string>();
        }

        public string GoalId { get; set; }

        public string Description { get; set; }

        public string GoalType { get; set; }

        public string State { get; set; }

        public int CurrentCount { get; set; }

        public bool ShowCountProgress { get; set; }

        public bool HasCompleteThreshold { get; set; }

        public string CompleteThresholdType { get; set; }

        public int CompleteThresholdAmount { get; set; }

        public bool HasFailThreshold { get; set; }

        public string FailThresholdType { get; set; }

        public int FailThresholdAmount { get; set; }

        public bool IsComplete { get; set; }

        public bool IsFailed { get; set; }

        public string RewardId { get; set; }

        public IList<string> TypeStrings { get; set; }
    }

    internal sealed class ExpeditionBiomeModifierPayload
    {
        public ExpeditionBiomeModifierPayload()
        {
            Tags = new List<string>();
        }

        public string ModifierId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public IList<string> Tags { get; set; }
    }

    internal sealed class ExpeditionRelationshipPayload
    {
        public string ActorGuidA { get; set; }

        public string ActorGuidB { get; set; }

        public string ActorNameA { get; set; }

        public string ActorNameB { get; set; }

        public string ActorDataIdA { get; set; }

        public string ActorDataIdB { get; set; }

        public int HeroSlotA { get; set; }

        public int HeroSlotB { get; set; }

        public int TeamPositionA { get; set; }

        public int TeamPositionB { get; set; }

        public int Leaning { get; set; }

        public int LeaningMin { get; set; }

        public int LeaningMax { get; set; }

        public float LeaningPercent { get; set; }

        public string LeaningLevelId { get; set; }

        public bool HasCurrentRelationship { get; set; }

        public bool HasPendingRelationship { get; set; }

        public bool HasRelationshipDuration { get; set; }

        public int RelationshipDurationRemaining { get; set; }

        public string RelationshipId { get; set; }

        public string RelationshipName { get; set; }

        public string RelationshipKind { get; set; }
    }

    internal sealed class ExpeditionHeroPayload
    {
        public ExpeditionHeroPayload()
        {
            Quirks = new List<ExpeditionQuirkPayload>();
            Diseases = new List<ExpeditionQuirkPayload>();
            Memories = new List<ExpeditionItemPayload>();
            Trinkets = new List<ExpeditionItemPayload>();
            CombatItems = new List<ExpeditionItemPayload>();
        }

        public int HeroSlot { get; set; }

        public int TeamPosition { get; set; }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string ActorName { get; set; }

        public string PathId { get; set; }

        public int Hp { get; set; }

        public int HpMax { get; set; }

        public int Stress { get; set; }

        public int StressMax { get; set; }

        public float WoundPercent { get; set; }

        public IList<ExpeditionQuirkPayload> Quirks { get; set; }

        public IList<ExpeditionQuirkPayload> Diseases { get; set; }

        public IList<ExpeditionItemPayload> Memories { get; set; }

        public IList<ExpeditionItemPayload> Trinkets { get; set; }

        public IList<ExpeditionItemPayload> CombatItems { get; set; }

        public string RunGoalId { get; set; }

        public string RunGoalDescription { get; set; }

        public string RunGoalProgress { get; set; }

        public string RunGoalCategoryId { get; set; }

        public bool RunGoalComplete { get; set; }

        public int RunGoalScore { get; set; }

        public string RunGoalLootTableId { get; set; }
    }

    internal sealed class ExpeditionQuirkPayload
    {
        public string QuirkId { get; set; }

        public string DisplayName { get; set; }

        public string Kind { get; set; }

        public bool IsLocked { get; set; }

        public bool IsNew { get; set; }

        public int Duration { get; set; }

        public string SourceType { get; set; }

        public string SourceId { get; set; }
    }

    internal sealed class ExpeditionItemPayload
    {
        public string Scope { get; set; }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string ItemType { get; set; }

        public string SlotType { get; set; }

        public int Quantity { get; set; }
    }

    internal sealed class ExpeditionCurrencyPayload
    {
        public string ItemId { get; set; }

        public string DisplayName { get; set; }

        public int Quantity { get; set; }
    }

    internal sealed class MainMenuSnapshotPayload
    {
        public bool IsActive { get; set; }

        public string CurrentGameMode { get; set; }

        public string ProfileName { get; set; }

        public bool HasDisclaimerShown { get; set; }

        public bool IsLoadingGameOrCinematic { get; set; }

        public bool IsInputtingText { get; set; }

        public bool HasExpeditionSave { get; set; }

        public bool CanAbandonExpedition { get; set; }

        public bool CanContinueExpedition { get; set; }

        public bool CanStartNewExpedition { get; set; }

        public string SaveValidationAction { get; set; }

        public string SaveFailureReason { get; set; }

        public string BlockReason { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class MainMenuActionRequestPayload
    {
        public MainMenuActionRequestPayload()
        {
        }

        public MainMenuActionRequestPayload(string requestId, string action)
        {
            RequestId = requestId;
            Action = action;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }
    }

    internal sealed class MainMenuActionResultPayload
    {
        public MainMenuActionResultPayload()
        {
        }

        public MainMenuActionResultPayload(
            string requestId,
            string action,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class StoryChoiceSnapshotPayload
    {
        public StoryChoiceSnapshotPayload()
        {
            Choices = new List<StoryChoiceOptionPayload>();
        }

        public bool IsActive { get; set; }

        public string StoryType { get; set; }

        public string StoryState { get; set; }

        public string EngageType { get; set; }

        public string SelectedActorGuid { get; set; }

        public int ChoiceCount { get; set; }

        public IList<StoryChoiceOptionPayload> Choices { get; set; }

        public bool ChoiceOverruleEnabled { get; set; }

        public int ChoiceOverruleLimitPerMap { get; set; }

        public int ChoiceOverruleRemaining { get; set; }

        public string ChoiceOverruleMapKey { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class StoryChoiceOptionPayload
    {
        public StoryChoiceOptionPayload()
        {
            PlayerPreviews = new List<StoryChoicePreviewPayload>();
            EnemyPreviews = new List<StoryChoicePreviewPayload>();
        }

        public int OptionIndex { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public string ActorDataId { get; set; }

        public string ActorName { get; set; }

        public string ChoiceId { get; set; }

        public string ResultType { get; set; }

        public string BarkText { get; set; }

        public string QuirkChoiceId { get; set; }

        public bool CanChoose { get; set; }

        public ulong OwnerSteamId { get; set; }

        public string OwnerName { get; set; }

        public IList<StoryChoicePreviewPayload> PlayerPreviews { get; set; }

        public IList<StoryChoicePreviewPayload> EnemyPreviews { get; set; }
    }

    internal sealed class StoryChoicePreviewPayload
    {
        public StoryChoicePreviewPayload()
        {
        }

        public StoryChoicePreviewPayload(string previewId, int value, bool showNumber, string displayName = null, string description = null)
        {
            PreviewId = previewId;
            Value = value;
            ShowNumber = showNumber;
            DisplayName = displayName;
            Description = description;
        }

        public string PreviewId { get; set; }

        public int Value { get; set; }

        public bool ShowNumber { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }
    }

    internal sealed class StoryChoiceRequestPayload
    {
        public StoryChoiceRequestPayload()
        {
        }

        public StoryChoiceRequestPayload(string requestId, int optionIndex, int heroSlot, string actorGuid, bool isForced = false)
        {
            RequestId = requestId;
            OptionIndex = optionIndex;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            IsForced = isForced;
        }

        public string RequestId { get; set; }

        public int OptionIndex { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public bool IsForced { get; set; }
    }

    internal sealed class StoryChoiceResultPayload
    {
        public StoryChoiceResultPayload()
        {
        }

        public StoryChoiceResultPayload(
            string requestId,
            int optionIndex,
            int heroSlot,
            string actorGuid,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            OptionIndex = optionIndex;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public int OptionIndex { get; set; }

        public int HeroSlot { get; set; }

        public string ActorGuid { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class InnSnapshotPayload
    {
        public InnSnapshotPayload()
        {
            BiomeChoices = new List<InnBiomeChoicePayload>();
        }

        public bool IsActive { get; set; }

        public string GameType { get; set; }

        public string InnState { get; set; }

        public bool IsCamp { get; set; }

        public bool CanEmbark { get; set; }

        public int SelectedBiomeChoiceIndex { get; set; }

        public IList<InnBiomeChoicePayload> BiomeChoices { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class InnBiomeChoicePayload
    {
        public InnBiomeChoicePayload()
        {
        }

        public InnBiomeChoicePayload(
            int optionIndex,
            string biomeType,
            string biomeName,
            string biomeGoalId,
            string biomeModifierId,
            bool isSelected,
            bool isEndBiome,
            string biomeGoalName = null,
            string biomeGoalDescription = null,
            string biomeModifierName = null,
            string biomeModifierDescription = null)
        {
            OptionIndex = optionIndex;
            BiomeType = biomeType;
            BiomeName = biomeName;
            BiomeGoalId = biomeGoalId;
            BiomeModifierId = biomeModifierId;
            IsSelected = isSelected;
            IsEndBiome = isEndBiome;
            BiomeGoalName = biomeGoalName;
            BiomeGoalDescription = biomeGoalDescription;
            BiomeModifierName = biomeModifierName;
            BiomeModifierDescription = biomeModifierDescription;
        }

        public int OptionIndex { get; set; }

        public string BiomeType { get; set; }

        public string BiomeName { get; set; }

        public string BiomeGoalId { get; set; }

        public string BiomeModifierId { get; set; }

        public string BiomeGoalName { get; set; }

        public string BiomeGoalDescription { get; set; }

        public string BiomeModifierName { get; set; }

        public string BiomeModifierDescription { get; set; }

        public bool IsSelected { get; set; }

        public bool IsEndBiome { get; set; }
    }

    internal sealed class InnActionRequestPayload
    {
        public InnActionRequestPayload()
        {
        }

        public InnActionRequestPayload(string requestId, string action, int optionIndex = -1)
        {
            RequestId = requestId;
            Action = action;
            OptionIndex = optionIndex;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int OptionIndex { get; set; }
    }

    internal sealed class InnActionResultPayload
    {
        public InnActionResultPayload()
        {
        }

        public InnActionResultPayload(
            string requestId,
            string action,
            int optionIndex,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            OptionIndex = optionIndex;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int OptionIndex { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class EmbarkSnapshotPayload
    {
        public bool IsActive { get; set; }

        public string CurrentGameMode { get; set; }

        public string GameType { get; set; }

        public bool EmbarkIsStarted { get; set; }

        public bool IsCamp { get; set; }

        public bool HasUi { get; set; }

        public bool IsExiting { get; set; }

        public bool IsApplyingRelationships { get; set; }

        public bool HasRelationshipsApplied { get; set; }

        public int RelationshipCount { get; set; }

        public bool CanApplyRelationships { get; set; }

        public bool CanContinue { get; set; }

        public string NextBiomeType { get; set; }

        public string NextBiomeName { get; set; }

        public string BiomeGoalId { get; set; }

        public string BiomeModifierId { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class EmbarkActionRequestPayload
    {
        public EmbarkActionRequestPayload()
        {
        }

        public EmbarkActionRequestPayload(string requestId, string action)
        {
            RequestId = requestId;
            Action = action;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }
    }

    internal sealed class EmbarkActionResultPayload
    {
        public EmbarkActionResultPayload()
        {
        }

        public EmbarkActionResultPayload(
            string requestId,
            string action,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class AltarSnapshotPayload
    {
        public AltarSnapshotPayload()
        {
            Tracks = new List<AltarTrackPayload>();
            RewardButtons = new List<AltarRewardButtonPayload>();
        }

        public bool IsActive { get; set; }

        public string CurrentGameMode { get; set; }

        public string ActiveSubscreen { get; set; }

        public bool HasUi { get; set; }

        public bool HasActiveSystem { get; set; }

        public bool IsIntro { get; set; }

        public int CandleCount { get; set; }

        public bool IsExiting { get; set; }

        public bool IsGameModeChanging { get; set; }

        public bool CanEmbark { get; set; }

        public string BlockReason { get; set; }

        public IList<AltarTrackPayload> Tracks { get; set; }

        public IList<AltarRewardButtonPayload> RewardButtons { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class AltarTrackPayload
    {
        public AltarTrackPayload()
        {
        }

        public AltarTrackPayload(
            int trackIndex,
            string trackId,
            string displayName,
            string trackKind,
            float spentCandles,
            float totalCandles,
            float nextMilestoneCandles,
            float spendToNext,
            bool canProgress,
            bool canPurchase,
            bool buttonInteractable,
            bool isHolding,
            bool isComplete)
        {
            TrackIndex = trackIndex;
            TrackId = trackId;
            DisplayName = displayName;
            TrackKind = trackKind;
            SpentCandles = spentCandles;
            TotalCandles = totalCandles;
            NextMilestoneCandles = nextMilestoneCandles;
            SpendToNext = spendToNext;
            CanProgress = canProgress;
            CanPurchase = canPurchase;
            ButtonInteractable = buttonInteractable;
            IsHolding = isHolding;
            IsComplete = isComplete;
        }

        public int TrackIndex { get; set; }

        public string TrackId { get; set; }

        public string DisplayName { get; set; }

        public string TrackKind { get; set; }

        public float SpentCandles { get; set; }

        public float TotalCandles { get; set; }

        public float NextMilestoneCandles { get; set; }

        public float SpendToNext { get; set; }

        public bool CanProgress { get; set; }

        public bool CanPurchase { get; set; }

        public bool ButtonInteractable { get; set; }

        public bool IsHolding { get; set; }

        public bool IsComplete { get; set; }
    }

    internal sealed class AltarRewardButtonPayload
    {
        public AltarRewardButtonPayload()
        {
        }

        public AltarRewardButtonPayload(
            int buttonIndex,
            string screenKind,
            string unlockTrackId,
            string currentUnlockTableId,
            string itemType,
            string displayName,
            int numUnlocked,
            int totalItemCount,
            int cost,
            string costText,
            bool isLocked,
            bool isComplete,
            bool isRepeatable,
            bool canPurchase,
            bool canAfford,
            string purchaseMode)
        {
            ButtonIndex = buttonIndex;
            ScreenKind = screenKind;
            UnlockTrackId = unlockTrackId;
            CurrentUnlockTableId = currentUnlockTableId;
            ItemType = itemType;
            DisplayName = displayName;
            NumUnlocked = numUnlocked;
            TotalItemCount = totalItemCount;
            Cost = cost;
            CostText = costText;
            IsLocked = isLocked;
            IsComplete = isComplete;
            IsRepeatable = isRepeatable;
            CanPurchase = canPurchase;
            CanAfford = canAfford;
            PurchaseMode = purchaseMode;
        }

        public int ButtonIndex { get; set; }

        public string ScreenKind { get; set; }

        public string UnlockTrackId { get; set; }

        public string CurrentUnlockTableId { get; set; }

        public string ItemType { get; set; }

        public string DisplayName { get; set; }

        public int NumUnlocked { get; set; }

        public int TotalItemCount { get; set; }

        public int Cost { get; set; }

        public string CostText { get; set; }

        public bool IsLocked { get; set; }

        public bool IsComplete { get; set; }

        public bool IsRepeatable { get; set; }

        public bool CanPurchase { get; set; }

        public bool CanAfford { get; set; }

        public string PurchaseMode { get; set; }
    }

    internal sealed class AltarActionRequestPayload
    {
        public AltarActionRequestPayload()
        {
            TrackIndex = -1;
            RewardButtonIndex = -1;
            SpendValue = 1f;
        }

        public AltarActionRequestPayload(string requestId, string action)
        {
            RequestId = requestId;
            Action = action;
            TrackIndex = -1;
            RewardButtonIndex = -1;
            SpendValue = 1f;
        }

        public AltarActionRequestPayload(string requestId, string action, int trackIndex, string trackId, float spendValue)
            : this(requestId, action)
        {
            TrackIndex = trackIndex;
            TrackId = trackId;
            SpendValue = spendValue;
        }

        public AltarActionRequestPayload(
            string requestId,
            string action,
            int rewardButtonIndex,
            string screenKind,
            string unlockTableId,
            string unlockTrackId,
            string itemType)
            : this(requestId, action)
        {
            RewardButtonIndex = rewardButtonIndex;
            ScreenKind = screenKind;
            UnlockTableId = unlockTableId;
            UnlockTrackId = unlockTrackId;
            ItemType = itemType;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int TrackIndex { get; set; }

        public string TrackId { get; set; }

        public float SpendValue { get; set; }

        public int RewardButtonIndex { get; set; }

        public string ScreenKind { get; set; }

        public string UnlockTableId { get; set; }

        public string UnlockTrackId { get; set; }

        public string ItemType { get; set; }
    }

    internal sealed class AltarActionResultPayload
    {
        public AltarActionResultPayload()
        {
        }

        public AltarActionResultPayload(
            string requestId,
            string action,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class ConfessionChoiceSnapshotPayload
    {
        public ConfessionChoiceSnapshotPayload()
        {
            Choices = new List<ConfessionChoiceOptionPayload>();
        }

        public bool IsActive { get; set; }

        public string CurrentGameMode { get; set; }

        public string ScreenState { get; set; }

        public bool CanChoose { get; set; }

        public int SelectedOptionIndex { get; set; }

        public string SelectedBossId { get; set; }

        public IList<ConfessionChoiceOptionPayload> Choices { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class ConfessionChoiceOptionPayload
    {
        public ConfessionChoiceOptionPayload()
        {
        }

        public ConfessionChoiceOptionPayload(
            int optionIndex,
            string bossId,
            string label,
            bool isSelectable,
            bool isSelected)
        {
            OptionIndex = optionIndex;
            BossId = bossId;
            Label = label;
            IsSelectable = isSelectable;
            IsSelected = isSelected;
        }

        public int OptionIndex { get; set; }

        public string BossId { get; set; }

        public string Label { get; set; }

        public bool IsSelectable { get; set; }

        public bool IsSelected { get; set; }
    }

    internal sealed class ConfessionChoiceRequestPayload
    {
        public ConfessionChoiceRequestPayload()
        {
        }

        public ConfessionChoiceRequestPayload(string requestId, int optionIndex, string bossId)
        {
            RequestId = requestId;
            OptionIndex = optionIndex;
            BossId = bossId;
        }

        public string RequestId { get; set; }

        public int OptionIndex { get; set; }

        public string BossId { get; set; }
    }

    internal sealed class ConfessionChoiceResultPayload
    {
        public ConfessionChoiceResultPayload()
        {
        }

        public ConfessionChoiceResultPayload(
            string requestId,
            int optionIndex,
            string bossId,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            OptionIndex = optionIndex;
            BossId = bossId;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public int OptionIndex { get; set; }

        public string BossId { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class LairDecisionSnapshotPayload
    {
        public bool IsActive { get; set; }

        public string CurrentGameMode { get; set; }

        public string ScreenState { get; set; }

        public string CombatSource { get; set; }

        public string CurrentBattleConfigurationId { get; set; }

        public int CurrentBattleIndex { get; set; }

        public int NextBattleIndex { get; set; }

        public int TotalBattles { get; set; }

        public string NextBattleConfigurationId { get; set; }

        public bool CanContinue { get; set; }

        public bool CanRetreat { get; set; }

        public int LootedRewardCount { get; set; }

        public int UpcomingRewardCount { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class LairDecisionRequestPayload
    {
        public LairDecisionRequestPayload()
        {
        }

        public LairDecisionRequestPayload(string requestId, string action)
        {
            RequestId = requestId;
            Action = action;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }
    }

    internal sealed class LairDecisionResultPayload
    {
        public LairDecisionResultPayload()
        {
        }

        public LairDecisionResultPayload(
            string requestId,
            string action,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class ConfirmationDialogSnapshotPayload
    {
        public bool IsActive { get; set; }

        public bool IsAllowed { get; set; }

        public string Kind { get; set; }

        public string DialogType { get; set; }

        public string ScreenState { get; set; }

        public string Title { get; set; }

        public string Description { get; set; }

        public string ConfirmLabel { get; set; }

        public string DeclineLabel { get; set; }

        public bool CanConfirm { get; set; }

        public bool CanDecline { get; set; }

        public string BlockReason { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class ConfirmationDialogRequestPayload
    {
        public ConfirmationDialogRequestPayload()
        {
        }

        public ConfirmationDialogRequestPayload(string requestId, string action)
        {
            RequestId = requestId;
            Action = action;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }
    }

    internal sealed class ConfirmationDialogResultPayload
    {
        public ConfirmationDialogResultPayload()
        {
        }

        public ConfirmationDialogResultPayload(
            string requestId,
            string action,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class StoreSnapshotPayload
    {
        public StoreSnapshotPayload()
        {
            Items = new List<StoreItemPayload>();
        }

        public bool IsActive { get; set; }

        public string StoreKind { get; set; }

        public string ScreenState { get; set; }

        public IList<StoreItemPayload> Items { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class StoreItemPayload
    {
        public StoreItemPayload()
        {
        }

        public StoreItemPayload(
            int inventoryIndex,
            string itemId,
            string displayName,
            string itemType,
            int quantity,
            string priceText,
            bool canAfford,
            float costMultiplier,
            bool isBuyHidden,
            string description = null)
        {
            InventoryIndex = inventoryIndex;
            ItemId = itemId;
            DisplayName = displayName;
            ItemType = itemType;
            Quantity = quantity;
            PriceText = priceText;
            CanAfford = canAfford;
            CostMultiplier = costMultiplier;
            IsBuyHidden = isBuyHidden;
            Description = description;
        }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string ItemType { get; set; }

        public int Quantity { get; set; }

        public string PriceText { get; set; }

        public bool CanAfford { get; set; }

        public float CostMultiplier { get; set; }

        public bool IsBuyHidden { get; set; }
    }

    internal sealed class StoreActionRequestPayload
    {
        public StoreActionRequestPayload()
        {
        }

        public StoreActionRequestPayload(
            string requestId,
            string action,
            int inventoryIndex,
            string itemId,
            int quantity)
        {
            RequestId = requestId;
            Action = action;
            InventoryIndex = inventoryIndex;
            ItemId = itemId;
            Quantity = quantity;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public int Quantity { get; set; }
    }

    internal sealed class StoreActionResultPayload
    {
        public StoreActionResultPayload()
        {
        }

        public StoreActionResultPayload(
            string requestId,
            string action,
            int inventoryIndex,
            string itemId,
            int quantity,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            InventoryIndex = inventoryIndex;
            ItemId = itemId;
            Quantity = quantity;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public int Quantity { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class StagecoachSnapshotPayload
    {
        public StagecoachSnapshotPayload()
        {
            PlayerItems = new List<StagecoachItemPayload>();
            Slots = new List<StagecoachSlotPayload>();
        }

        public bool IsActive { get; set; }

        public bool IsEditable { get; set; }

        public string CurrentGameMode { get; set; }

        public string ScreenState { get; set; }

        public int Armor { get; set; }

        public int MaxArmor { get; set; }

        public int Wheels { get; set; }

        public int MaxWheels { get; set; }

        public StagecoachRepairPayload ArmorRepair { get; set; }

        public StagecoachRepairPayload WheelRepair { get; set; }

        public IList<StagecoachItemPayload> PlayerItems { get; set; }

        public IList<StagecoachSlotPayload> Slots { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class StagecoachRepairPayload
    {
        public StagecoachRepairPayload()
        {
        }

        public StagecoachRepairPayload(
            string repairKind,
            string runValueType,
            string transactionId,
            int currentValue,
            int maxValue,
            int amount,
            string costText,
            bool canRepair,
            bool canAfford)
        {
            RepairKind = repairKind;
            RunValueType = runValueType;
            TransactionId = transactionId;
            CurrentValue = currentValue;
            MaxValue = maxValue;
            Amount = amount;
            CostText = costText;
            CanRepair = canRepair;
            CanAfford = canAfford;
        }

        public string RepairKind { get; set; }

        public string RunValueType { get; set; }

        public string TransactionId { get; set; }

        public int CurrentValue { get; set; }

        public int MaxValue { get; set; }

        public int Amount { get; set; }

        public string CostText { get; set; }

        public bool CanRepair { get; set; }

        public bool CanAfford { get; set; }
    }

    internal sealed class StagecoachItemPayload
    {
        public StagecoachItemPayload()
        {
        }

        public StagecoachItemPayload(
            string inventoryKind,
            int inventoryIndex,
            string itemId,
            string displayName,
            string itemType,
            string slotType,
            int quantity,
            bool isUnequipInvalid,
            bool canEquip)
        {
            InventoryKind = inventoryKind;
            InventoryIndex = inventoryIndex;
            ItemId = itemId;
            DisplayName = displayName;
            ItemType = itemType;
            SlotType = slotType;
            Quantity = quantity;
            IsUnequipInvalid = isUnequipInvalid;
            CanEquip = canEquip;
        }

        public string InventoryKind { get; set; }

        public int InventoryIndex { get; set; }

        public string ItemId { get; set; }

        public string DisplayName { get; set; }

        public string ItemType { get; set; }

        public string SlotType { get; set; }

        public int Quantity { get; set; }

        public bool IsUnequipInvalid { get; set; }

        public bool CanEquip { get; set; }
    }

    internal sealed class StagecoachSlotPayload
    {
        public StagecoachSlotPayload()
        {
        }

        public StagecoachSlotPayload(
            string slotType,
            int slotIndex,
            bool canAcceptItems,
            bool canUnequip,
            StagecoachItemPayload item)
        {
            SlotType = slotType;
            SlotIndex = slotIndex;
            CanAcceptItems = canAcceptItems;
            CanUnequip = canUnequip;
            Item = item;
        }

        public string SlotType { get; set; }

        public int SlotIndex { get; set; }

        public bool CanAcceptItems { get; set; }

        public bool CanUnequip { get; set; }

        public StagecoachItemPayload Item { get; set; }
    }

    internal sealed class StagecoachActionRequestPayload
    {
        public StagecoachActionRequestPayload()
        {
        }

        public StagecoachActionRequestPayload(
            string requestId,
            string action,
            string repairKind,
            int sourceInventoryIndex,
            string targetSlotType,
            int targetSlotIndex,
            string itemId)
        {
            RequestId = requestId;
            Action = action;
            RepairKind = repairKind;
            SourceInventoryIndex = sourceInventoryIndex;
            TargetSlotType = targetSlotType;
            TargetSlotIndex = targetSlotIndex;
            ItemId = itemId;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public string RepairKind { get; set; }

        public int SourceInventoryIndex { get; set; }

        public string TargetSlotType { get; set; }

        public int TargetSlotIndex { get; set; }

        public string ItemId { get; set; }
    }

    internal sealed class StagecoachActionResultPayload
    {
        public StagecoachActionResultPayload()
        {
        }

        public StagecoachActionResultPayload(
            string requestId,
            string action,
            string repairKind,
            string targetSlotType,
            int targetSlotIndex,
            string itemId,
            ulong senderSteamId,
            string senderName,
            bool accepted,
            string message)
        {
            RequestId = requestId;
            Action = action;
            RepairKind = repairKind;
            TargetSlotType = targetSlotType;
            TargetSlotIndex = targetSlotIndex;
            ItemId = itemId;
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            Accepted = accepted;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Action { get; set; }

        public string RepairKind { get; set; }

        public string TargetSlotType { get; set; }

        public int TargetSlotIndex { get; set; }

        public string ItemId { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public bool Accepted { get; set; }

        public string Message { get; set; }
    }

    internal sealed class DamageMeterSnapshotPayload
    {
        public DamageMeterSnapshotPayload()
        {
            Heroes = new List<DamageMeterActorStatsPayload>();
            Enemies = new List<DamageMeterActorStatsPayload>();
            Contributions = new List<DamageMeterContributionPayload>();
            CombatLogEntries = new List<DamageMeterCombatLogEntryPayload>();
        }

        public int ApiVersion { get; set; }

        public string ProviderVersion { get; set; }

        public string Capabilities { get; set; }

        public bool IsAvailable { get; set; }

        public bool IsActive { get; set; }

        public string UnavailableReason { get; set; }

        public int Round { get; set; }

        public int Turn { get; set; }

        public string BattleState { get; set; }

        public string CurrentActorGuid { get; set; }

        public string CurrentActorName { get; set; }

        public float PlayerTotalDamage { get; set; }

        public float EnemyTotalDamage { get; set; }

        public IList<DamageMeterActorStatsPayload> Heroes { get; set; }

        public IList<DamageMeterActorStatsPayload> Enemies { get; set; }

        public IList<DamageMeterContributionPayload> Contributions { get; set; }

        public DamageMeterStatusTotalsPayload StatusTotals { get; set; }

        public IList<DamageMeterCombatLogEntryPayload> CombatLogEntries { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class DamageMeterActorStatsPayload
    {
        public string ActorGuid { get; set; }

        public string ActorName { get; set; }

        public int TeamIndex { get; set; }

        public float TotalDamageDealt { get; set; }

        public float DotDamageDealt { get; set; }

        public float TotalDamageReceived { get; set; }

        public float RawDamageReceived { get; set; }

        public float OverkillDamageDealt { get; set; }

        public float TotalHealingDone { get; set; }

        public float TotalHealingReceived { get; set; }

        public float TotalStressReceived { get; set; }

        public int Kills { get; set; }

        public int Crits { get; set; }

        public int IncomingAttacks { get; set; }

        public int AvoidedAttacks { get; set; }

        public int DodgeAvoids { get; set; }

        public int MissAvoids { get; set; }
    }

    internal sealed class DamageMeterContributionPayload
    {
        public string ActorGuid { get; set; }

        public string ActorName { get; set; }

        public int TeamIndex { get; set; }

        public float BonusDamage { get; set; }

        public float VulnerableDamage { get; set; }

        public float ShieldPrevented { get; set; }

        public float GuardProtected { get; set; }

        public float DotDamagePrevented { get; set; }

        public int ShieldWasted { get; set; }

        public int ComboApplied { get; set; }

        public int ComboConsumed { get; set; }

        public float TotalContribution { get; set; }
    }

    internal sealed class DamageMeterStatusTotalsPayload
    {
        public int PlayerBuffApplied { get; set; }

        public int PlayerDebuffApplied { get; set; }

        public int EnemyBuffApplied { get; set; }

        public int EnemyDebuffApplied { get; set; }

        public int PlayerStatusRemoved { get; set; }

        public int EnemyStatusRemoved { get; set; }

        public int PlayerStatusConsumed { get; set; }

        public int EnemyStatusConsumed { get; set; }
    }

    internal sealed class DamageMeterCombatLogEntryPayload
    {
        public int Index { get; set; }

        public int Round { get; set; }

        public string EntryType { get; set; }

        public string SourceName { get; set; }

        public string TargetName { get; set; }

        public bool SourceIsPlayer { get; set; }

        public bool TargetIsPlayer { get; set; }

        public string ActionType { get; set; }

        public float Value { get; set; }

        public string SkillId { get; set; }

        public string Extra { get; set; }

        public string DotType { get; set; }

        public float OverkillDamage { get; set; }
    }

    internal sealed class CurrentInteractionSnapshotPayload
    {
        public CurrentInteractionSnapshotPayload()
        {
            Items = new List<CurrentInteractionItemPayload>();
            ActiveVoteKeys = new List<string>();
        }

        public bool IsActive { get; set; }

        public string PrimaryKind { get; set; }

        public string PrimaryLabel { get; set; }

        public string PrimarySummary { get; set; }

        public string PrimaryTargetTab { get; set; }

        public string PrimaryVoteKey { get; set; }

        public IList<CurrentInteractionItemPayload> Items { get; set; }

        public IList<string> ActiveVoteKeys { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class CurrentInteractionItemPayload
    {
        public int Priority { get; set; }

        public string Kind { get; set; }

        public string Label { get; set; }

        public string Summary { get; set; }

        public string TargetTab { get; set; }

        public string VoteKey { get; set; }

        public bool IsActionable { get; set; }

        public bool HasVote { get; set; }

        public string SourceDigest { get; set; }
    }

    internal sealed class VoteStatusPayload
    {
        public VoteStatusPayload()
        {
            Votes = new List<VoteEntryPayload>();
            Missing = new List<VoteEntryPayload>();
        }

        public VoteStatusPayload(
            string voteKey,
            string contextDigest,
            bool isActive,
            bool isResolved,
            string resolution,
            int votedCount,
            int requiredCount,
            IList<VoteEntryPayload> votes,
            IList<VoteEntryPayload> missing)
        {
            VoteKey = voteKey;
            ContextDigest = contextDigest;
            IsActive = isActive;
            IsResolved = isResolved;
            Resolution = resolution;
            VotedCount = votedCount;
            RequiredCount = requiredCount;
            Votes = votes ?? new List<VoteEntryPayload>();
            Missing = missing ?? new List<VoteEntryPayload>();
        }

        public string VoteKey { get; set; }

        public string ContextDigest { get; set; }

        public bool IsActive { get; set; }

        public bool IsResolved { get; set; }

        public string Resolution { get; set; }

        public int VotedCount { get; set; }

        public int RequiredCount { get; set; }

        public IList<VoteEntryPayload> Votes { get; set; }

        public IList<VoteEntryPayload> Missing { get; set; }
    }

    internal sealed class VoteEntryPayload
    {
        public VoteEntryPayload()
        {
        }

        public VoteEntryPayload(ulong steamId, string name, string choice)
        {
            SteamId = steamId;
            Name = name;
            Choice = choice;
        }

        public ulong SteamId { get; set; }

        public string Name { get; set; }

        public string Choice { get; set; }
    }

    internal sealed class FullStateRequestPayload
    {
        public FullStateRequestPayload()
        {
        }

        public FullStateRequestPayload(string requestId, string reason)
        {
            RequestId = requestId;
            Reason = reason;
        }

        public string RequestId { get; set; }

        public string Reason { get; set; }
    }

    internal sealed class FullStateResultPayload
    {
        public FullStateResultPayload()
        {
        }

        public FullStateResultPayload(
            string requestId,
            string reason,
            int sentMessages,
            bool hasPendingTurn,
            string message)
        {
            RequestId = requestId;
            Reason = reason;
            SentMessages = sentMessages;
            HasPendingTurn = hasPendingTurn;
            Message = message;
        }

        public string RequestId { get; set; }

        public string Reason { get; set; }

        public int SentMessages { get; set; }

        public bool HasPendingTurn { get; set; }

        public string Message { get; set; }
    }

    internal sealed class StateDigestPayload
    {
        public StateDigestPayload()
        {
        }

        public StateDigestPayload(string label, string digest)
        {
            Label = label;
            Digest = digest;
        }

        public string Label { get; set; }

        public string Digest { get; set; }
    }

    internal sealed class ChatPayload
    {
        public ChatPayload()
        {
        }

        public ChatPayload(string text)
        {
            Text = text;
        }

        public string Text { get; set; }
    }
}
