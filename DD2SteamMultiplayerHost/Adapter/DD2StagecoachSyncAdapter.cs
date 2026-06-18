using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Code.Actor;
using Assets.Code.Campaign;
using Assets.Code.Cost;
using Assets.Code.Effect;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Inn;
using Assets.Code.Inn.Presentation;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Locale;
using Assets.Code.Math;
using Assets.Code.Run;
using Assets.Code.Run.Events;
using Assets.Code.Source;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2StagecoachSyncAdapter : IStagecoachActionAdapter, IDisposable
    {
        private const float CachedSnapshotForcedRefreshInterval = 10f;

        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private bool _snapshotDirty = true;
        private float _nextForcedSnapshotRefreshTime;
        private StagecoachSnapshotPayload _cachedSnapshot;

        public void TryEnsureListeners()
        {
            if (_listenersRegistered)
            {
                return;
            }

            if (!Singleton<EventManager>.HasInstance())
            {
                if (!_eventManagerMissingLogged)
                {
                    _eventManagerMissingLogged = true;
                    HostLog.Write("[stagecoach] EventManager is not ready; stagecoach sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventInventoryItemSwapped>(HandleStagecoachInventoryChanged, false, 0);
            EventManager.AddListener<EventInventoryItemQtyChange>(HandleStagecoachInventoryChanged, false, 0);
            EventManager.AddListener<EventInventorySlotsChanged>(HandleStagecoachInventoryChanged, false, 0);
            EventManager.AddListener<EventInventoryItemPurchased>(HandleStagecoachInventoryChanged, false, 0);
            EventManager.AddListener<EventItemDiscarded>(HandleStagecoachInventoryChanged, false, 0);
            EventManager.AddListener<EventUpdatePlayerCurrency>(HandleStagecoachCurrencyChanged, false, 0);
            EventManager.AddListener<EventRunValueChanged>(HandleStagecoachRunValueChanged, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[stagecoach] Stagecoach listeners registered; dirty-cache enabled.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventInventoryItemSwapped>(HandleStagecoachInventoryChanged);
            EventManager.RemoveListener<EventInventoryItemQtyChange>(HandleStagecoachInventoryChanged);
            EventManager.RemoveListener<EventInventorySlotsChanged>(HandleStagecoachInventoryChanged);
            EventManager.RemoveListener<EventInventoryItemPurchased>(HandleStagecoachInventoryChanged);
            EventManager.RemoveListener<EventItemDiscarded>(HandleStagecoachInventoryChanged);
            EventManager.RemoveListener<EventUpdatePlayerCurrency>(HandleStagecoachCurrencyChanged);
            EventManager.RemoveListener<EventRunValueChanged>(HandleStagecoachRunValueChanged);
            _listenersRegistered = false;
        }

        public bool TryGetStagecoachSnapshot(out StagecoachSnapshotPayload snapshot)
        {
            try
            {
                if (!IsStagecoachUiActive())
                {
                    snapshot = CreateInactiveSnapshot();
                    CacheSnapshot(snapshot);
                    MarkSnapshotDirty();
                    return true;
                }

                if (CanUseCachedSnapshot())
                {
                    snapshot = _cachedSnapshot;
                    return true;
                }

                StagecoachContext context;
                if (!TryFindActiveStagecoach(out context))
                {
                    snapshot = CreateInactiveSnapshot();
                    CacheSnapshot(snapshot);
                    MarkSnapshotDirty();
                    return true;
                }

                snapshot = BuildSnapshot(context);
                CacheSnapshot(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[stagecoach] Failed to collect stagecoach snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        public bool TryExecuteStagecoachAction(
            StagecoachActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty stagecoach action request";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "repair", StringComparison.Ordinal))
            {
                bool accepted = TryRepair(request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (string.Equals(action, "equip_item", StringComparison.Ordinal) ||
                string.Equals(action, "equip", StringComparison.Ordinal))
            {
                bool accepted = TryEquip(request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (string.Equals(action, "unequip_item", StringComparison.Ordinal) ||
                string.Equals(action, "unequip", StringComparison.Ordinal))
            {
                bool accepted = TryUnequip(request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            message = "unsupported stagecoach action: " + request.Action;
            return false;
        }

        private bool CanUseCachedSnapshot()
        {
            return _cachedSnapshot != null &&
                _cachedSnapshot.IsActive &&
                !_snapshotDirty &&
                Time.unscaledTime < _nextForcedSnapshotRefreshTime;
        }

        private void CacheSnapshot(StagecoachSnapshotPayload snapshot)
        {
            _cachedSnapshot = snapshot;
            _snapshotDirty = false;
            _nextForcedSnapshotRefreshTime = Time.unscaledTime + CachedSnapshotForcedRefreshInterval;
        }

        private void MarkSnapshotDirty()
        {
            _snapshotDirty = true;
        }

        private void HandleStagecoachInventoryChanged(EventInventoryItemSwapped evt)
        {
            if (evt == null ||
                IsStagecoachItem(evt.m_itemDefinition) ||
                IsStagecoachRelevantInventory(evt.m_sourceInventory) ||
                IsStagecoachRelevantInventory(evt.m_destinationInventory))
            {
                MarkSnapshotDirty();
            }
        }

        private void HandleStagecoachInventoryChanged(EventInventoryItemQtyChange evt)
        {
            if (evt == null ||
                IsStagecoachItem(evt.m_itemDef) ||
                IsStagecoachRelevantInventory(evt.m_inventory))
            {
                MarkSnapshotDirty();
            }
        }

        private void HandleStagecoachInventoryChanged(EventInventorySlotsChanged evt)
        {
            if (evt == null || IsStagecoachRelevantInventory(evt.m_inventory))
            {
                MarkSnapshotDirty();
            }
        }

        private void HandleStagecoachInventoryChanged(EventInventoryItemPurchased evt)
        {
            if (evt == null || IsStagecoachItem(evt.m_itemDefinition))
            {
                MarkSnapshotDirty();
            }
        }

        private void HandleStagecoachInventoryChanged(EventItemDiscarded evt)
        {
            if (IsStagecoachUiActive())
            {
                MarkSnapshotDirty();
            }
        }

        private void HandleStagecoachCurrencyChanged(EventUpdatePlayerCurrency evt)
        {
            if (IsStagecoachUiActive())
            {
                MarkSnapshotDirty();
            }
        }

        private void HandleStagecoachRunValueChanged(EventRunValueChanged evt)
        {
            if (evt == null ||
                evt.m_RunValueType == RunValueType.STAGE_COACH_ARMOR ||
                evt.m_RunValueType == RunValueType.STAGE_COACH_WHEELS)
            {
                MarkSnapshotDirty();
            }
        }

        private static bool IsStagecoachItem(ItemDefinition itemDefinition)
        {
            return itemDefinition != null && itemDefinition.m_type == ItemType.STAGE_COACH_UPGRADE;
        }

        private static bool IsStagecoachRelevantInventory(ItemInventory inventory)
        {
            if (inventory == null)
            {
                return false;
            }

            if (Singleton<GameTypeMgr>.HasInstance() &&
                ReferenceEquals(Singleton<GameTypeMgr>.Instance.PlayerInventory, inventory))
            {
                return true;
            }

            if (!TryGetStagecoach(out StageCoach stageCoach))
            {
                return false;
            }

            return ReferenceEquals(stageCoach.GetSlotInventory(ItemSlotType.GENERAL), inventory) ||
                ReferenceEquals(stageCoach.GetSlotInventory(ItemSlotType.TROPHY), inventory) ||
                ReferenceEquals(stageCoach.GetSlotInventory(ItemSlotType.PET), inventory) ||
                ReferenceEquals(stageCoach.GetSlotInventory(ItemSlotType.FLAME), inventory);
        }

        private static StagecoachSnapshotPayload BuildSnapshot(StagecoachContext context)
        {
            int armor = GetRunValue(RunValueType.STAGE_COACH_ARMOR);
            int wheels = GetRunValue(RunValueType.STAGE_COACH_WHEELS);
            int maxArmor = GetRunValueMax(RunStatType.STAGE_COACH_ARMOR_MAX_VALUE);
            int maxWheels = GetRunValueMax(RunStatType.STAGE_COACH_WHEELS_MAX_VALUE);

            StagecoachSnapshotPayload snapshot = new StagecoachSnapshotPayload
            {
                IsActive = true,
                IsEditable = context.Ui.IsEditable,
                CurrentGameMode = SafeGetCurrentGameModeName(),
                ScreenState = Convert.ToString(context.Ui.ScreenState),
                Armor = armor,
                MaxArmor = maxArmor,
                Wheels = wheels,
                MaxWheels = maxWheels,
                ArmorRepair = BuildRepairPayload("armor", RunValueType.STAGE_COACH_ARMOR, armor, maxArmor),
                WheelRepair = BuildRepairPayload("wheels", RunValueType.STAGE_COACH_WHEELS, wheels, maxWheels),
                PlayerItems = BuildPlayerItems(),
                Slots = BuildStagecoachSlots(),
            };
            snapshot.Digest = ComputeStagecoachDigest(snapshot);
            return snapshot;
        }

        private static bool IsStagecoachUiActive()
        {
            try
            {
                return SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsStageCoachSheetActive;
            }
            catch
            {
                return false;
            }
        }

        private static StagecoachRepairPayload BuildRepairPayload(
            string repairKind,
            RunValueType runValueType,
            int currentValue,
            int maxValue)
        {
            SourceDefinition<RunValueTransactionDefinition> transaction = FindRepairTransaction(runValueType);
            bool canAfford = transaction != null &&
                Singleton<GameTypeMgr>.Instance.RunValues.CanAfford(transaction);
            bool canRepair = transaction != null &&
                Singleton<GameTypeMgr>.Instance.RunValues.CanDoTransaction(transaction) &&
                currentValue < maxValue;
            int amount = transaction == null
                ? 0
                : MathUtils.RoundToInt(transaction.Definition.m_TransactionAmount);
            string costText = transaction == null
                ? string.Empty
                : GetTransactionCostText(transaction);

            return new StagecoachRepairPayload(
                repairKind,
                runValueType == null ? null : runValueType.GetName(),
                transaction == null ? null : transaction.Definition.m_Id,
                currentValue,
                maxValue,
                amount,
                costText,
                canRepair,
                canAfford);
        }

        private static List<StagecoachItemPayload> BuildPlayerItems()
        {
            List<StagecoachItemPayload> items = new List<StagecoachItemPayload>();
            if (!Singleton<GameTypeMgr>.HasInstance() ||
                Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
            {
                return items;
            }

            ItemInventory inventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            for (int i = 0; i < inventory.GetNumberOfTotalSlots(); i++)
            {
                IReadOnlyItemInstance item = inventory.GetItemOrDefault(i);
                if (!ItemUtils.IsValid(item))
                {
                    continue;
                }

                ItemDefinition itemDefinition = item.GetItemDefinition();
                if (itemDefinition.m_type != ItemType.STAGE_COACH_UPGRADE)
                {
                    continue;
                }

                items.Add(BuildItemPayload("player", i, item, CanEquipAnyStagecoachSlot(itemDefinition)));
            }

            return items;
        }

        private static List<StagecoachSlotPayload> BuildStagecoachSlots()
        {
            List<StagecoachSlotPayload> slots = new List<StagecoachSlotPayload>();
            if (!TryGetStagecoach(out StageCoach stageCoach))
            {
                return slots;
            }

            AddStagecoachSlots(slots, stageCoach, ItemSlotType.GENERAL);
            AddStagecoachSlots(slots, stageCoach, ItemSlotType.TROPHY);
            AddStagecoachSlots(slots, stageCoach, ItemSlotType.PET);
            AddStagecoachSlots(slots, stageCoach, ItemSlotType.FLAME);
            return slots;
        }

        private static void AddStagecoachSlots(
            List<StagecoachSlotPayload> slots,
            StageCoach stageCoach,
            ItemSlotType slotType)
        {
            ItemInventory inventory = stageCoach.GetSlotInventory(slotType);
            if (inventory == null)
            {
                return;
            }

            for (int i = 0; i < inventory.GetNumberOfTotalSlots(); i++)
            {
                IReadOnlyItemInstance item = inventory.GetItemOrDefault(i);
                bool hasItem = ItemUtils.IsValid(item);
                StagecoachItemPayload payload = hasItem
                    ? BuildItemPayload(slotType.GetName(), i, item, false)
                    : null;
                bool canUnequip = hasItem &&
                    !item.GetItemDefinition().m_IsUnequipInvalid &&
                    Singleton<GameTypeMgr>.Instance.PlayerInventory != null &&
                    Singleton<GameTypeMgr>.Instance.PlayerInventory.CanAdd(item.GetItemDefinition(), item.GetQty());
                slots.Add(new StagecoachSlotPayload(
                    slotType.GetName(),
                    i,
                    true,
                    canUnequip,
                    payload));
            }
        }

        private static StagecoachItemPayload BuildItemPayload(
            string inventoryKind,
            int inventoryIndex,
            IReadOnlyItemInstance item,
            bool canEquip)
        {
            ItemDefinition itemDefinition = item.GetItemDefinition();
            return new StagecoachItemPayload(
                inventoryKind,
                inventoryIndex,
                itemDefinition.m_id,
                GetItemDisplayName(itemDefinition),
                itemDefinition.m_type == null ? null : itemDefinition.m_type.GetName(),
                itemDefinition.m_slot == null ? null : itemDefinition.m_slot.GetName(),
                item.GetQty(),
                itemDefinition.m_IsUnequipInvalid,
                canEquip);
        }

        private static bool TryRepair(
            StagecoachActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            StagecoachContext context;
            if (!TryFindActiveStagecoach(out context))
            {
                message = "stagecoach sheet is not active on host";
                return false;
            }

            if (!context.Ui.IsEditable)
            {
                message = "stagecoach sheet is not editable";
                return false;
            }

            RunValueType runValueType;
            if (!TryGetRepairRunValueType(request.RepairKind, out runValueType))
            {
                message = "unsupported repair kind: " + (request.RepairKind ?? "[none]");
                return false;
            }

            SourceDefinition<RunValueTransactionDefinition> transaction = FindRepairTransaction(runValueType);
            if (transaction == null)
            {
                message = "repair transaction is not available for " + runValueType.GetName();
                return false;
            }

            if (!Singleton<GameTypeMgr>.Instance.RunValues.CanDoTransactionIgnoringCost(transaction))
            {
                message = "cannot repair " + runValueType.GetName() + " in current host state";
                return false;
            }

            if (!Singleton<GameTypeMgr>.Instance.RunValues.CanAfford(transaction))
            {
                message = "cannot afford repair " + runValueType.GetName() +
                    " cost=" + GetTransactionCostText(transaction);
                return false;
            }

            try
            {
                Singleton<GameTypeMgr>.Instance.RunValues.DoTransaction(transaction);
                RefreshStagecoachUi(context.Ui, runValueType);
                HostLog.Write("[stagecoach-action] " + senderName + "/" + senderSteamId +
                    " repaired " + runValueType.GetName() +
                    " amount=" + transaction.Definition.m_TransactionAmount +
                    " cost=" + GetTransactionCostText(transaction) + ".");
                message = "repaired " + runValueType.GetName();
                return true;
            }
            catch (Exception ex)
            {
                message = "stagecoach repair failed: " + ex.Message;
                HostLog.Write("[stagecoach-action] " + message + ".");
                return false;
            }
        }

        private static bool TryEquip(
            StagecoachActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            StagecoachContext context;
            if (!TryFindActiveStagecoach(out context))
            {
                message = "stagecoach sheet is not active on host";
                return false;
            }

            if (!context.Ui.IsEditable)
            {
                message = "stagecoach sheet is not editable";
                return false;
            }

            if (!TryGetStagecoach(out StageCoach stageCoach))
            {
                message = "stagecoach runtime is not available";
                return false;
            }

            if (Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
            {
                message = "player inventory is not available";
                return false;
            }

            ItemSlotType slotType;
            if (!TryResolveSlotType(request.TargetSlotType, out slotType))
            {
                message = "unsupported stagecoach slot type: " + (request.TargetSlotType ?? "[none]");
                return false;
            }

            ItemInventory playerInventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            ItemInventory targetInventory = stageCoach.GetSlotInventory(slotType);
            if (!IsInventoryIndexValid(playerInventory, request.SourceInventoryIndex))
            {
                message = "source player inventory index is invalid: " + request.SourceInventoryIndex;
                return false;
            }

            if (!IsInventoryIndexValid(targetInventory, request.TargetSlotIndex))
            {
                message = "target " + slotType.GetName() + " slot is invalid: " + request.TargetSlotIndex;
                return false;
            }

            IReadOnlyItemInstance sourceItem = playerInventory.GetItemOrDefault(request.SourceInventoryIndex);
            if (!ItemUtils.IsValid(sourceItem))
            {
                message = "source player inventory slot " + request.SourceInventoryIndex + " is empty";
                return false;
            }

            ItemDefinition itemDefinition = sourceItem.GetItemDefinition();
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(itemDefinition.m_id, request.ItemId, StringComparison.Ordinal))
            {
                message = "source player inventory slot " + request.SourceInventoryIndex +
                    " contains " + itemDefinition.m_id +
                    ", not " + request.ItemId;
                return false;
            }

            if (!CanEquipToStagecoachSlot(targetInventory, request.TargetSlotIndex, slotType, itemDefinition, out message))
            {
                return false;
            }

            try
            {
                targetInventory.SwapItems(playerInventory, request.SourceInventoryIndex, request.TargetSlotIndex, false);
                EventInventoryItemSwapped.Trigger(itemDefinition, playerInventory, targetInventory);
                playerInventory.RefreshStackQtysAndAddToOverflow();
                playerInventory.Condense();
                playerInventory.RemoveEmptyOverlimitItems();
                CheckActorsForItemOverflow(playerInventory);
                RefreshStagecoachUi(context.Ui, null);
                HostLog.Write("[stagecoach-action] " + senderName + "/" + senderSteamId +
                    " equipped item=" + itemDefinition.m_id +
                    " from playerInventory[" + request.SourceInventoryIndex + "]" +
                    " to " + slotType.GetName() +
                    "[" + request.TargetSlotIndex + "].");
                message = "equipped " + itemDefinition.m_id +
                    " to " + slotType.GetName() +
                    " slot " + request.TargetSlotIndex;
                return true;
            }
            catch (Exception ex)
            {
                message = "stagecoach equip failed: " + ex.Message;
                HostLog.Write("[stagecoach-action] " + message + ".");
                return false;
            }
        }

        private static bool TryUnequip(
            StagecoachActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            StagecoachContext context;
            if (!TryFindActiveStagecoach(out context))
            {
                message = "stagecoach sheet is not active on host";
                return false;
            }

            if (!context.Ui.IsEditable)
            {
                message = "stagecoach sheet is not editable";
                return false;
            }

            if (!TryGetStagecoach(out StageCoach stageCoach))
            {
                message = "stagecoach runtime is not available";
                return false;
            }

            if (Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
            {
                message = "player inventory is not available";
                return false;
            }

            ItemSlotType slotType;
            if (!TryResolveSlotType(request.TargetSlotType, out slotType))
            {
                message = "unsupported stagecoach slot type: " + (request.TargetSlotType ?? "[none]");
                return false;
            }

            ItemInventory targetInventory = stageCoach.GetSlotInventory(slotType);
            if (!IsInventoryIndexValid(targetInventory, request.TargetSlotIndex))
            {
                message = "target " + slotType.GetName() + " slot is invalid: " + request.TargetSlotIndex;
                return false;
            }

            IReadOnlyItemInstance targetItem = targetInventory.GetItemOrDefault(request.TargetSlotIndex);
            if (!ItemUtils.IsValid(targetItem))
            {
                message = "target " + slotType.GetName() + " slot " + request.TargetSlotIndex + " is empty";
                return false;
            }

            ItemDefinition itemDefinition = targetItem.GetItemDefinition();
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(itemDefinition.m_id, request.ItemId, StringComparison.Ordinal))
            {
                message = "target " + slotType.GetName() + " slot " + request.TargetSlotIndex +
                    " contains " + itemDefinition.m_id +
                    ", not " + request.ItemId;
                return false;
            }

            if (itemDefinition.m_IsUnequipInvalid)
            {
                message = "stagecoach item " + itemDefinition.m_id + " cannot be unequipped";
                return false;
            }

            int quantity = targetItem.GetQty();
            ItemInventory playerInventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            if (!playerInventory.CanAdd(itemDefinition, quantity))
            {
                message = "player inventory cannot accept unequipped item " + itemDefinition.m_id;
                return false;
            }

            try
            {
                targetInventory.TakeItemQty(request.TargetSlotIndex, quantity);
                playerInventory.AddItemsWithOverflow(itemDefinition, quantity, false);
                playerInventory.RefreshStackQtysAndAddToOverflow();
                CheckActorsForItemOverflow(playerInventory);
                EventUpdatePlayerCurrency.Trigger();
                RefreshStagecoachUi(context.Ui, null);
                HostLog.Write("[stagecoach-action] " + senderName + "/" + senderSteamId +
                    " unequipped item=" + itemDefinition.m_id +
                    " from " + slotType.GetName() +
                    "[" + request.TargetSlotIndex + "] to player inventory.");
                message = "unequipped " + itemDefinition.m_id +
                    " from " + slotType.GetName() +
                    " slot " + request.TargetSlotIndex;
                return true;
            }
            catch (Exception ex)
            {
                message = "stagecoach unequip failed: " + ex.Message;
                HostLog.Write("[stagecoach-action] " + message + ".");
                return false;
            }
        }

        private static bool CanEquipToStagecoachSlot(
            ItemInventory targetInventory,
            int targetSlotIndex,
            ItemSlotType slotType,
            ItemDefinition itemDefinition,
            out string message)
        {
            message = string.Empty;
            if (itemDefinition.m_type != ItemType.STAGE_COACH_UPGRADE)
            {
                message = "item " + itemDefinition.m_id + " is not a stagecoach upgrade";
                return false;
            }

            if (itemDefinition.m_slot != slotType)
            {
                message = "item " + itemDefinition.m_id +
                    " belongs to " + (itemDefinition.m_slot == null ? "[none]" : itemDefinition.m_slot.GetName()) +
                    ", not " + slotType.GetName();
                return false;
            }

            IReadOnlyItemInstance targetItem = targetInventory.GetItemOrDefault(targetSlotIndex);
            if (ItemUtils.IsValid(targetItem) &&
                targetItem.GetItemDefinition().m_IsUnequipInvalid)
            {
                message = "target slot contains an unequippable item: " + targetItem.GetItemDefinition().m_id;
                return false;
            }

            if (itemDefinition.m_IsUnequipInvalid)
            {
                message = "stagecoach item " + itemDefinition.m_id +
                    " requires DD2's unequippable-item confirmation; remote equip is blocked for now";
                return false;
            }

            if (!ItemUtils.IsValid(targetItem) &&
                Singleton<InnBhv>.HasInstance() &&
                Singleton<InnBhv>.Instance.GetInnInstance() != null &&
                !Singleton<InnBhv>.Instance.GetInnInstance().GetCanEquipStageCoachItem(itemDefinition))
            {
                message = "host inn rules do not allow equipping " + itemDefinition.m_id +
                    " into an empty " + slotType.GetName() + " slot";
                return false;
            }

            return true;
        }

        private static bool CanEquipAnyStagecoachSlot(ItemDefinition itemDefinition)
        {
            if (itemDefinition == null ||
                itemDefinition.m_type != ItemType.STAGE_COACH_UPGRADE ||
                itemDefinition.m_slot == null ||
                itemDefinition.m_IsUnequipInvalid)
            {
                return false;
            }

            if (!TryGetStagecoach(out StageCoach stageCoach))
            {
                return false;
            }

            ItemInventory inventory = stageCoach.GetSlotInventory(itemDefinition.m_slot);
            if (inventory == null)
            {
                return false;
            }

            for (int i = 0; i < inventory.GetNumberOfTotalSlots(); i++)
            {
                string message;
                if (CanEquipToStagecoachSlot(inventory, i, itemDefinition.m_slot, itemDefinition, out message))
                {
                    return true;
                }
            }

            return false;
        }

        private static SourceDefinition<RunValueTransactionDefinition> FindRepairTransaction(RunValueType runValueType)
        {
            try
            {
                if (runValueType == null ||
                    !SingletonMonoBehaviour<InnPresentationBhv>.HasInstance(false) ||
                    SingletonMonoBehaviour<InnPresentationBhv>.Instance.ActiveInnInstance == null ||
                    SingletonMonoBehaviour<InnPresentationBhv>.Instance.ActiveInnInstance.GetInnDefinition() == null)
                {
                    return null;
                }

                IReadOnlyList<SourceDefinition<RunValueTransactionDefinition>> transactions =
                    SingletonMonoBehaviour<InnPresentationBhv>.Instance.ActiveInnInstance.GetInnDefinition().SourceRunValueTransactions;
                return transactions == null
                    ? null
                    : transactions.FirstOrDefault(candidate => candidate != null &&
                        candidate.Definition != null &&
                        candidate.Definition.m_RunValueType == runValueType);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryFindActiveStagecoach(out StagecoachContext context)
        {
            context = null;
            if (!TryGetStagecoach(out _))
            {
                return false;
            }

            StageCoachConfigUiBhv ui = UnityObject.FindObjectsOfType<StageCoachConfigUiBhv>(true)
                .Where(candidate => candidate != null &&
                    candidate.gameObject != null &&
                    candidate.gameObject.activeInHierarchy)
                .OrderByDescending(candidate => candidate.enabled)
                .FirstOrDefault();
            if (ui == null)
            {
                return false;
            }

            context = new StagecoachContext(ui);
            return true;
        }

        private static bool TryGetStagecoach(out StageCoach stageCoach)
        {
            stageCoach = null;
            if (!Singleton<GameTypeMgr>.HasInstance() ||
                Singleton<GameTypeMgr>.Instance.StageCoach == null ||
                Singleton<GameTypeMgr>.Instance.RunValues == null ||
                Singleton<GameTypeMgr>.Instance.RunDataManager == null)
            {
                return false;
            }

            stageCoach = Singleton<GameTypeMgr>.Instance.StageCoach;
            return true;
        }

        private static bool TryGetRepairRunValueType(string repairKind, out RunValueType runValueType)
        {
            runValueType = null;
            string normalized = string.IsNullOrWhiteSpace(repairKind)
                ? string.Empty
                : repairKind.Trim().ToLowerInvariant();
            if (normalized == "armor" || normalized == "armour")
            {
                runValueType = RunValueType.STAGE_COACH_ARMOR;
                return true;
            }

            if (normalized == "wheel" || normalized == "wheels")
            {
                runValueType = RunValueType.STAGE_COACH_WHEELS;
                return true;
            }

            return false;
        }

        private static bool TryResolveSlotType(string slotTypeName, out ItemSlotType slotType)
        {
            slotType = null;
            string normalized = string.IsNullOrWhiteSpace(slotTypeName)
                ? string.Empty
                : slotTypeName.Trim().ToLowerInvariant();
            if (normalized == "general")
            {
                slotType = ItemSlotType.GENERAL;
                return true;
            }

            if (normalized == "trophy")
            {
                slotType = ItemSlotType.TROPHY;
                return true;
            }

            if (normalized == "pet")
            {
                slotType = ItemSlotType.PET;
                return true;
            }

            if (normalized == "flame")
            {
                slotType = ItemSlotType.FLAME;
                return true;
            }

            return false;
        }

        private static void RefreshStagecoachUi(StageCoachConfigUiBhv ui, RunValueType repairedType)
        {
            try
            {
                if (ui == null)
                {
                    return;
                }

                ui.RefreshStats();
                if (repairedType == RunValueType.STAGE_COACH_ARMOR)
                {
                    ui.RefreshRepairPrice(RunValueType.STAGE_COACH_WHEELS);
                }
                else if (repairedType == RunValueType.STAGE_COACH_WHEELS)
                {
                    ui.RefreshRepairPrice(RunValueType.STAGE_COACH_ARMOR);
                }
                else
                {
                    ui.RefreshRepairPrice(RunValueType.STAGE_COACH_ARMOR);
                    ui.RefreshRepairPrice(RunValueType.STAGE_COACH_WHEELS);
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[stagecoach] Failed to refresh native stagecoach UI: " + ex.Message + ".");
            }
        }

        private static void CheckActorsForItemOverflow(ItemInventory playerInventory)
        {
            try
            {
                if (!SingletonMonoBehaviour<CampaignBhv>.HasInstance(false) ||
                    !Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.RosterManager == null)
                {
                    return;
                }

                foreach (ActorInstance actor in Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors())
                {
                    List<ItemInstance> overflow = actor.GetCombatSkillInventory().RefreshStackQtys();
                    if (overflow == null)
                    {
                        continue;
                    }

                    foreach (ItemInstance item in overflow)
                    {
                        if (item != null && item.GetItemDefinition() != null)
                        {
                            playerInventory.AddItemsWithOverflow(item.GetItemDefinition(), item.GetQty(), false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[stagecoach] Failed to check actor item overflow: " + ex.Message + ".");
            }
        }

        private static bool IsInventoryIndexValid(ItemInventory inventory, int index)
        {
            return inventory != null && index >= 0 && index < inventory.GetNumberOfTotalSlots();
        }

        private static int GetRunValue(RunValueType runValueType)
        {
            return Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.RunValues != null
                ? Mathf.RoundToInt(Singleton<GameTypeMgr>.Instance.RunValues.GetValue(runValueType))
                : 0;
        }

        private static int GetRunValueMax(RunStatType runStatType)
        {
            return Singleton<GameTypeMgr>.HasInstance() && Singleton<GameTypeMgr>.Instance.RunDataManager != null
                ? MathUtils.RoundToInt(Singleton<GameTypeMgr>.Instance.RunDataManager.GetStatValue(runStatType, (string)null))
                : 0;
        }

        private static string GetTransactionCostText(SourceDefinition<RunValueTransactionDefinition> transaction)
        {
            try
            {
                float multiplier = Singleton<GameTypeMgr>.Instance.RunValues.GetTransactionCostMultiplier(transaction.Definition);
                return CostDescription.GetStoreBuyDescription(transaction.Definition.TransactionCost, multiplier, false);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetItemDisplayName(ItemDefinition itemDefinition)
        {
            if (itemDefinition == null)
            {
                return "[item]";
            }

            try
            {
                string displayName = ItemDescription.GetTitle(itemDefinition, 0);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return displayName;
                }
            }
            catch
            {
            }

            return itemDefinition.m_id ?? "[item]";
        }

        private static string SafeGetCurrentGameModeName()
        {
            try
            {
                return Convert.ToString(GameModeMgr.CurrentMode);
            }
            catch
            {
                return "[none]";
            }
        }

        private static StagecoachSnapshotPayload CreateInactiveSnapshot()
        {
            StagecoachSnapshotPayload snapshot = new StagecoachSnapshotPayload
            {
                IsActive = false,
                IsEditable = false,
                CurrentGameMode = SafeGetCurrentGameModeName(),
                ScreenState = "[none]",
            };
            snapshot.Digest = ComputeStagecoachDigest(snapshot);
            return snapshot;
        }

        private static string ComputeStagecoachDigest(StagecoachSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string repairPart = DescribeRepair(snapshot.ArmorRepair) + "|" + DescribeRepair(snapshot.WheelRepair);
            string playerPart = string.Join(
                "|",
                (snapshot.PlayerItems ?? Array.Empty<StagecoachItemPayload>())
                    .OrderBy(item => item.InventoryIndex)
                    .Select(DescribeItem)
                    .ToArray());
            string slotPart = string.Join(
                "|",
                (snapshot.Slots ?? Array.Empty<StagecoachSlotPayload>())
                    .OrderBy(slot => slot.SlotType)
                    .ThenBy(slot => slot.SlotIndex)
                    .Select(slot => (slot.SlotType ?? string.Empty) +
                        ":" + slot.SlotIndex +
                        ":" + slot.CanAcceptItems +
                        ":" + slot.CanUnequip +
                        ":" + DescribeItem(slot.Item))
                    .ToArray());
            return ComputeStableDigest(
                snapshot.IsActive + ";" +
                snapshot.IsEditable + ";" +
                (snapshot.CurrentGameMode ?? string.Empty) + ";" +
                (snapshot.ScreenState ?? string.Empty) + ";" +
                snapshot.Armor + "/" + snapshot.MaxArmor + ";" +
                snapshot.Wheels + "/" + snapshot.MaxWheels + ";" +
                repairPart + ";" +
                playerPart + ";" +
                slotPart);
        }

        private static string DescribeRepair(StagecoachRepairPayload repair)
        {
            return repair == null
                ? string.Empty
                : (repair.RepairKind ?? string.Empty) +
                    ":" + (repair.TransactionId ?? string.Empty) +
                    ":" + repair.CurrentValue +
                    ":" + repair.MaxValue +
                    ":" + repair.Amount +
                    ":" + (repair.CostText ?? string.Empty) +
                    ":" + repair.CanRepair +
                    ":" + repair.CanAfford;
        }

        private static string DescribeItem(StagecoachItemPayload item)
        {
            return item == null
                ? string.Empty
                : (item.InventoryKind ?? string.Empty) +
                    ":" + item.InventoryIndex +
                    ":" + (item.ItemId ?? string.Empty) +
                    ":" + (item.SlotType ?? string.Empty) +
                    ":" + item.Quantity +
                    ":" + item.IsUnequipInvalid +
                    ":" + item.CanEquip;
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

        private sealed class StagecoachContext
        {
            public StagecoachContext(StageCoachConfigUiBhv ui)
            {
                Ui = ui;
            }

            public StageCoachConfigUiBhv Ui { get; }
        }
    }
}
