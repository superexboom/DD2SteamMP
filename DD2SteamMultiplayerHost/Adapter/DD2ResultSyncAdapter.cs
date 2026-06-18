using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Campaign;
using Assets.Code.Combat;
using Assets.Code.Combat.BattleConfiguration;
using Assets.Code.Combat.Events;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Loot;
using Assets.Code.UI.Controllers;
using Assets.Code.UI.Items;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2ResultSyncAdapter : ILootActionAdapter, IGameResultsActionAdapter, IDisposable
    {
        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private int _battleResultEventId;
        private BattleResultPayload _latestBattleResult;
        private LootWindowSnapshotPayload _latestLootWindowSnapshot;

        public event Action<BattleResultPayload> BattleResultReady;

        public void TryEnsureListeners()
        {
            if (_listenersRegistered)
            {
                return;
            }

            if (Singleton<EventManager>.Instance == null)
            {
                if (!_eventManagerMissingLogged)
                {
                    _eventManagerMissingLogged = true;
                    HostLog.Write("[result] EventManager is not ready; result/loot sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventBattleResult>(HandleEventBattleResult, false, 0);
            EventManager.AddListener<EventLootItemReceived>(HandleEventLootItemReceived, false, 0);
            EventManager.AddListener<EventItemDiscarded>(HandleEventItemDiscarded, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[result] Battle result and loot listeners registered.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventBattleResult>(HandleEventBattleResult);
            EventManager.RemoveListener<EventLootItemReceived>(HandleEventLootItemReceived);
            EventManager.RemoveListener<EventItemDiscarded>(HandleEventItemDiscarded);
            _listenersRegistered = false;
        }

        public bool TryGetLatestBattleResult(out BattleResultPayload payload)
        {
            payload = _latestBattleResult;
            return payload != null;
        }

        public bool TryGetLootWindowSnapshot(out LootWindowSnapshotPayload snapshot)
        {
            snapshot = null;

            try
            {
                bool isLootActive = SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsLootActive;
                if (!isLootActive)
                {
                    snapshot = CreateInactiveLootSnapshot();
                    _latestLootWindowSnapshot = snapshot;
                    return true;
                }

                LootUiControllerBhv controller;
                UiScreenBhv screen;
                if (!TryGetActiveLootUi(out controller, out screen))
                {
                    snapshot = CreateInactiveLootSnapshot();
                    snapshot.ScreenState = "active-but-controller-missing";
                    snapshot.Digest = ComputeLootWindowDigest(snapshot);
                    _latestLootWindowSnapshot = snapshot;
                    return true;
                }

                LootUiPushParams pushParams = screen.PushParams as LootUiPushParams;
                IReadOnlyList<LootItemRuntimeEntry> currentItems = GetCurrentLootItemEntries(screen, pushParams);
                snapshot = new LootWindowSnapshotPayload
                {
                    IsActive = true,
                    ScreenState = Convert.ToString(screen.ScreenState),
                    Reason = pushParams == null || pushParams.m_reason == null ? null : pushParams.m_reason.GetName(),
                    ReasonId = pushParams == null ? null : pushParams.m_reasonId,
                    HeroPoints = pushParams == null ? 0 : pushParams.m_heroPoints,
                    TorchGain = pushParams == null ? 0 : pushParams.m_torchGain,
                    ArmorGain = pushParams == null ? 0 : pushParams.m_armorGain,
                    WheelGain = pushParams == null ? 0 : pushParams.m_wheelGain,
                    HeroDied = pushParams != null && pushParams.m_heroDied,
                    CanTakeAll = currentItems.Count > 0,
                    Items = BuildLootItemSnapshots(currentItems),
                    SkillsGranted = pushParams == null || pushParams.m_skillsGranted == null
                        ? new List<string>()
                        : pushParams.m_skillsGranted.OrderBy(skill => skill).ToList(),
                };
                snapshot.Digest = ComputeLootWindowDigest(snapshot);
                _latestLootWindowSnapshot = snapshot;
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[result] Failed to collect loot window snapshot: " + ex.Message + ".");
                return false;
            }
        }

        public bool TryGetGameResultsSnapshot(out GameResultsSnapshotPayload snapshot)
        {
            try
            {
                bool isGameResultsActive = SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsGameResultsActive;
                if (!isGameResultsActive)
                {
                    snapshot = CreateInactiveGameResultsSnapshot();
                    return true;
                }

                GameOverScreenWidgetBhv widget;
                UiScreenBhv screen;
                if (!TryGetActiveGameResultsUi(out widget, out screen))
                {
                    snapshot = CreateInactiveGameResultsSnapshot();
                    return true;
                }

                string screenState = screen == null ? "[none]" : Convert.ToString(screen.ScreenState);
                ScoreInstance score = TryGetLastRunScore();
                snapshot = new GameResultsSnapshotPayload
                {
                    IsActive = true,
                    ScreenState = screenState,
                    GameOverReason = score == null || score.GetGameOverReason() == null
                        ? "[none]"
                        : score.GetGameOverReason().GetName(),
                    HasScore = score != null && score.HasAnyScore(),
                    CanContinue = screen != null && screen.ScreenState == UiScreenState.Open,
                };
                snapshot.Digest = ComputeGameResultsDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[game-results] Failed to collect game results snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveGameResultsSnapshot();
                return false;
            }
        }

        public bool TryExecuteLootAction(
            LootActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty loot action";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            LootUiControllerBhv controller;
            UiScreenBhv screen;
            if (!TryGetActiveLootUi(out controller, out screen))
            {
                message = "no active loot window";
                return false;
            }

            if (string.Equals(action, "take_all", StringComparison.Ordinal))
            {
                try
                {
                    HostLog.Write("[loot-action] " + senderName + "/" + senderSteamId + " requested take_all.");
                    controller.ButtonTakeAll();
                    message = "take_all invoked on host";
                    return true;
                }
                catch (Exception ex)
                {
                    message = "take_all failed: " + ex.Message;
                    HostLog.Write("[loot-action] " + message + ".");
                    return false;
                }
            }

            if (string.Equals(action, "take_item", StringComparison.Ordinal))
            {
                return TryExecuteTakeItem(screen, request, senderSteamId, senderName, out message);
            }

            if (string.Equals(action, "take_selected", StringComparison.Ordinal))
            {
                return TryExecuteTakeSelected(controller, screen, request, senderSteamId, senderName, out message);
            }

            if (string.Equals(action, "discard_all", StringComparison.Ordinal))
            {
                return TryExecuteDiscardAll(controller, senderSteamId, senderName, out message);
            }

            message = "unsupported loot action: " + request.Action;
            return false;
        }

        public bool TryBypassActiveLootWindowForArena(out string message)
        {
            message = string.Empty;
            LootUiControllerBhv controller;
            UiScreenBhv screen;
            if (!TryGetActiveLootUi(out controller, out screen) || controller == null || screen == null)
            {
                message = "no active loot window";
                return false;
            }

            if (screen.ScreenState != UiScreenState.Open)
            {
                message = "loot window is not open yet: " + screen.ScreenState;
                return false;
            }

            try
            {
                MethodInfo closeAction = controller.GetType().GetMethod("CloseAction", BindingFlags.Instance | BindingFlags.NonPublic);
                if (closeAction == null)
                {
                    message = "loot close action was not found";
                    return false;
                }

                closeAction.Invoke(controller, Array.Empty<object>());
                message = "arena loot window closed without taking rewards";
                return true;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                message = "arena loot bypass failed: " + inner.Message;
                HostLog.Write("[arena] " + message + ".");
                return false;
            }
        }

        public bool TryExecuteGameResultsAction(
            GameResultsActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty game results action";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "continue", StringComparison.Ordinal))
            {
                message = "unsupported game results action: " + request.Action;
                return false;
            }

            GameOverScreenWidgetBhv widget;
            UiScreenBhv screen;
            if (!TryGetActiveGameResultsUi(out widget, out screen) || screen == null)
            {
                message = "game results screen is not active";
                return false;
            }

            if (screen.ScreenState != UiScreenState.Open)
            {
                message = "game results screen is not ready: " + screen.ScreenState;
                return false;
            }

            try
            {
                screen.TryCloseScreen();
                HostLog.Write("[game-results-action] " + senderName + "/" + senderSteamId +
                    " continued from game results.");
                message = "game results continue invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "game results continue failed: " + ex.Message;
                HostLog.Write("[game-results-action] " + message + ".");
                return false;
            }
        }

        private void HandleEventBattleResult(EventBattleResult evt)
        {
            if (evt == null || evt.m_BattleResult == null)
            {
                return;
            }

            BattleResultPayload payload = BuildBattleResultPayload(evt.m_BattleResult, ++_battleResultEventId);
            _latestBattleResult = payload;
            HostLog.Write("[result] battle result event=" + payload.EventId +
                ", complete=" + payload.IsFightComplete +
                ", sequenceComplete=" + payload.IsBattleSequenceComplete +
                ", reason=" + (payload.LootReason ?? "[none]") +
                ", rewards=" + payload.LootRewards.Count +
                ", digest=" + payload.Digest + ".");

            Action<BattleResultPayload> handler = BattleResultReady;
            if (handler != null)
            {
                handler(payload);
            }
        }

        private static BattleResultPayload BuildBattleResultPayload(BattleResult result, int eventId)
        {
            BattleConfigurationDefinition currentConfig = result.CurrentBattleConfiguration;
            BattleResultPayload payload = new BattleResultPayload
            {
                EventId = eventId,
                IsFightComplete = result.IsFightComplete,
                IsBattleSequenceComplete = result.IsBattleSequenceComplete,
                HasNextBattle = result.HasNextBattle,
                IsForceEnd = result.m_IsForceEnd,
                IsRetreat = result.m_IsRetreat,
                IsBiomeBossBattle = result.m_IsBiomeBossBattle,
                IsExpeditionBossBattle = result.m_IsExpeditionBossBattle,
                IsGangBossBattle = result.m_IsGangBossBattle,
                LootReason = result.m_LootReason == null ? null : result.m_LootReason.GetName(),
                LootReasonId = result.m_LootReasonId,
                CombatSource = result.m_CombatSource == null ? null : result.m_CombatSource.GetName(),
                NodeSubType = result.m_NodeSubType,
                CurrentBattleConfigurationId = currentConfig == null ? null : currentConfig.m_Id,
                CurrentBattleConfigurationIndex = result.m_CurrentBattleConfigurationIndex,
                NextBattleConfigurationIndex = result.m_NextBattleConfigurationIndex,
                LootRewards = BuildLootRewardPayloads(result.m_LootRewards),
            };
            payload.Digest = ComputeBattleResultDigest(payload);
            return payload;
        }

        private static IList<LootRewardPayload> BuildLootRewardPayloads(IReadOnlyList<Reward<LootType>> rewards)
        {
            if (rewards == null || rewards.Count == 0)
            {
                return new List<LootRewardPayload>();
            }

            return rewards
                .Where(reward => reward != null)
                .Select(reward => new LootRewardPayload(
                    reward.m_type == null ? "[unknown]" : reward.m_type.GetName(),
                    reward.m_id,
                    reward.m_qty))
                .OrderBy(reward => reward.Type)
                .ThenBy(reward => reward.Id)
                .ThenBy(reward => reward.Quantity)
                .ToList();
        }

        private static void HandleEventLootItemReceived(EventLootItemReceived evt)
        {
            if (evt == null || evt.m_itemDef == null)
            {
                return;
            }

            HostLog.Write("[loot] received " + evt.m_itemDef.m_id +
                " x" + evt.m_qty +
                ", wasLastItem=" + evt.m_wasLastItem + ".");
        }

        private static void HandleEventItemDiscarded(EventItemDiscarded evt)
        {
            if (evt == null)
            {
                return;
            }

            HostLog.Write("[loot] discarded " + (evt.m_ItemId ?? "[unknown]") +
                " x" + evt.m_ItemQty +
                ", sell=" + evt.m_IsSell + ".");
        }

        private static bool TryGetActiveLootUi(out LootUiControllerBhv controller, out UiScreenBhv screen)
        {
            controller = null;
            screen = null;

            LootUiControllerBhv[] controllers = UnityObject.FindObjectsOfType<LootUiControllerBhv>(true);
            for (int i = 0; i < controllers.Length; i++)
            {
                UiScreenBhv candidateScreen = GetPrivateField<UiScreenBhv>(controllers[i], "m_screenBhv");
                if (candidateScreen == null)
                {
                    continue;
                }

                if (candidateScreen.ScreenState == UiScreenState.Closing ||
                    candidateScreen.ScreenState == UiScreenState.Closed ||
                    candidateScreen.PushParams as LootUiPushParams == null)
                {
                    continue;
                }

                controller = controllers[i];
                screen = candidateScreen;
                return true;
            }

            return false;
        }

        private static bool TryGetActiveGameResultsUi(out GameOverScreenWidgetBhv widget, out UiScreenBhv screen)
        {
            widget = null;
            screen = null;

            GameOverScreenWidgetBhv[] widgets = UnityObject.FindObjectsOfType<GameOverScreenWidgetBhv>(true);
            for (int i = 0; i < widgets.Length; i++)
            {
                if (widgets[i] == null || widgets[i].gameObject == null || !widgets[i].gameObject.activeInHierarchy)
                {
                    continue;
                }

                UiScreenBhv candidateScreen = GetPrivateField<UiScreenBhv>(widgets[i], "m_uiScreenBhv");
                if (candidateScreen == null ||
                    candidateScreen.ScreenState == UiScreenState.Closing ||
                    candidateScreen.ScreenState == UiScreenState.Closed)
                {
                    continue;
                }

                widget = widgets[i];
                screen = candidateScreen;
                return true;
            }

            return false;
        }

        private static ScoreInstance TryGetLastRunScore()
        {
            try
            {
                return SingletonMonoBehaviour<CampaignBhv>.HasInstance(false)
                    ? SingletonMonoBehaviour<CampaignBhv>.Instance.LastRunScore
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<LootItemRuntimeEntry> GetCurrentLootItemEntries(UiScreenBhv screen, LootUiPushParams pushParams)
        {
            LootInventoryItemContainerBhv container;
            if (TryGetLootContainer(screen, out container) && container.Inventory != null)
            {
                List<LootItemRuntimeEntry> entries = new List<LootItemRuntimeEntry>();
                ItemInventory inventory = container.Inventory;
                for (int i = 0; i < inventory.GetNumberOfTotalSlots(); i++)
                {
                    IReadOnlyItemInstance item = inventory.GetItem(i);
                    if (ItemUtils.IsValid(item))
                    {
                        entries.Add(new LootItemRuntimeEntry(i, item));
                    }
                }

                return entries;
            }

            if (pushParams != null && pushParams.m_items != null)
            {
                List<LootItemRuntimeEntry> entries = new List<LootItemRuntimeEntry>();
                foreach (IReadOnlyItemInstance item in pushParams.m_items)
                {
                    if (ItemUtils.IsValid(item))
                    {
                        entries.Add(new LootItemRuntimeEntry(-1, item));
                    }
                }

                return entries;
            }

            return Array.Empty<LootItemRuntimeEntry>();
        }

        private static bool TryGetLootContainer(UiScreenBhv screen, out LootInventoryItemContainerBhv container)
        {
            container = null;
            LootInventoryWidgetBhv widget = screen == null ? null : screen.GetWidget<LootInventoryWidgetBhv>();
            container = widget == null
                ? null
                : GetPrivateField<LootInventoryItemContainerBhv>(widget, "m_itemContainerBhv");
            return container != null;
        }

        private static IList<LootItemSnapshotPayload> BuildLootItemSnapshots(IReadOnlyList<LootItemRuntimeEntry> items)
        {
            if (items == null || items.Count == 0)
            {
                return new List<LootItemSnapshotPayload>();
            }

            List<LootItemSnapshotPayload> result = new List<LootItemSnapshotPayload>();
            for (int i = 0; i < items.Count; i++)
            {
                LootItemRuntimeEntry entry = items[i];
                IReadOnlyItemInstance item = entry.Item;
                if (!ItemUtils.IsValid(item))
                {
                    continue;
                }

                ItemDefinition definition = item.GetItemDefinition();
                result.Add(new LootItemSnapshotPayload(
                    definition == null ? "[unknown]" : definition.m_id,
                    definition == null || definition.m_type == null ? null : definition.m_type.GetName(),
                    definition == null || definition.m_slot == null ? null : definition.m_slot.GetName(),
                    item.GetQty(),
                    item.GetDurationAmount(),
                    GetItemDisplayName(definition),
                    entry.InventoryIndex));
            }

            return result
                .OrderBy(item => item.InventoryIndex < 0 ? int.MaxValue : item.InventoryIndex)
                .ThenBy(item => item.SlotType)
                .ThenBy(item => item.ItemId)
                .ThenBy(item => item.Quantity)
                .ToList();
        }

        private static string GetItemDisplayName(ItemDefinition definition)
        {
            if (definition == null)
            {
                return "[unknown]";
            }

            try
            {
                string displayName = ItemDescription.GetTitle(definition, 0);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return displayName;
                }
            }
            catch
            {
            }

            return definition.m_id ?? "[unknown]";
        }

        private static bool TryExecuteTakeItem(
            UiScreenBhv screen,
            LootActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            LootInventoryItemContainerBhv container;
            if (!TryGetLootContainer(screen, out container) || container.Inventory == null)
            {
                message = "loot item container is not available";
                return false;
            }

            if (request.InventoryIndex < 0 || request.InventoryIndex >= container.Inventory.GetNumberOfTotalSlots())
            {
                message = "loot inventory index is invalid: " + request.InventoryIndex;
                return false;
            }

            IReadOnlyItemInstance item = container.Inventory.GetItem(request.InventoryIndex);
            if (!ItemUtils.IsValid(item))
            {
                message = "loot item is no longer available at index " + request.InventoryIndex;
                return false;
            }

            ItemDefinition definition = item.GetItemDefinition();
            string itemId = definition == null ? null : definition.m_id;
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(request.ItemId, itemId, StringComparison.Ordinal))
            {
                message = "loot item changed at index " + request.InventoryIndex +
                    ": expected " + request.ItemId +
                    ", found " + (itemId ?? "[unknown]");
                return false;
            }

            int qty = item.GetQty();
            if (definition == null || qty <= 0)
            {
                message = "loot item is invalid at index " + request.InventoryIndex;
                return false;
            }

            ItemInventory playerInventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            if (playerInventory == null || !playerInventory.CanAdd(definition, qty))
            {
                message = "player inventory cannot accept " + itemId + " x" + qty;
                return false;
            }

            try
            {
                LootInventoryItemBhv lootItemBhv = container.FindItemBhvWithItemIndex(request.InventoryIndex) as LootInventoryItemBhv;
                HostLog.Write("[loot-action] " + senderName + "/" + senderSteamId +
                    " requested take_item index=" + request.InventoryIndex +
                    ", item=" + itemId +
                    ", qty=" + qty + ".");
                if (lootItemBhv != null)
                {
                    lootItemBhv.OnSubmit(null);
                    message = "take_item invoked on host for " + itemId + " x" + qty;
                    return true;
                }

                playerInventory.AddItems(definition, qty, false);
                container.Inventory.TakeItemQty(request.InventoryIndex, qty);
                bool wasLastItem = container.Inventory.GetNumberOfFilledSlots() == 0;
                EventLootItemReceived.Trigger(definition, qty, wasLastItem);
                container.MarkForRepopulate();
                message = "take_item transferred on host for " + itemId + " x" + qty;
                return true;
            }
            catch (Exception ex)
            {
                message = "take_item failed: " + ex.Message;
                HostLog.Write("[loot-action] " + message + ".");
                return false;
            }
        }

        private static bool TryExecuteTakeSelected(
            LootUiControllerBhv controller,
            UiScreenBhv screen,
            LootActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            IList<LootActionItemPayload> selectedItems = request == null
                ? null
                : request.Items;
            if (selectedItems == null || selectedItems.Count == 0)
            {
                message = "take_selected has no selected items";
                return false;
            }

            List<LootActionItemPayload> orderedItems = selectedItems
                .Where(item => item != null && item.InventoryIndex >= 0)
                .GroupBy(item => item.InventoryIndex)
                .Select(group => group.First())
                .OrderByDescending(item => item.InventoryIndex)
                .ToList();
            if (orderedItems.Count == 0)
            {
                message = "take_selected has no valid selected item indexes";
                return false;
            }

            List<string> messages = new List<string>();
            HostLog.Write("[loot-action] " + senderName + "/" + senderSteamId +
                " requested take_selected count=" + orderedItems.Count + ".");

            foreach (LootActionItemPayload item in orderedItems)
            {
                LootActionRequestPayload itemRequest = new LootActionRequestPayload(
                    request.RequestId,
                    "take_item",
                    item.ItemId,
                    item.Quantity,
                    item.InventoryIndex);
                string itemMessage;
                if (!TryExecuteTakeItem(screen, itemRequest, senderSteamId, senderName, out itemMessage))
                {
                    messages.Add(itemMessage);
                    message = "take_selected stopped after " + messages.Count +
                        " step(s): " + string.Join(" | ", messages.ToArray());
                    return false;
                }

                messages.Add(itemMessage);
            }

            LootUiControllerBhv activeController;
            UiScreenBhv activeScreen;
            if (!TryGetActiveLootUi(out activeController, out activeScreen))
            {
                message = "take_selected completed and loot window closed: " + string.Join(" | ", messages.ToArray());
                return true;
            }

            string closeMessage;
            if (TryExecuteDiscardAll(activeController ?? controller, senderSteamId, senderName, out closeMessage))
            {
                messages.Add(closeMessage);
                message = "take_selected completed and remaining loot closed: " + string.Join(" | ", messages.ToArray());
                return true;
            }

            messages.Add(closeMessage);
            message = "take_selected completed but remaining loot close failed: " + string.Join(" | ", messages.ToArray());
            return false;
        }

        private static bool TryExecuteDiscardAll(
            LootUiControllerBhv controller,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            try
            {
                HostLog.Write("[loot-action] " + senderName + "/" + senderSteamId + " requested discard_all.");
                MethodInfo closeAction = controller.GetType().GetMethod("CloseAction", BindingFlags.Instance | BindingFlags.NonPublic);
                if (closeAction == null)
                {
                    message = "loot close action was not found";
                    return false;
                }

                closeAction.Invoke(controller, Array.Empty<object>());
                message = "discard_all invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                message = "discard_all failed: " + inner.Message;
                HostLog.Write("[loot-action] " + message + ".");
                return false;
            }
        }

        private static LootWindowSnapshotPayload CreateInactiveLootSnapshot()
        {
            return new LootWindowSnapshotPayload
            {
                IsActive = false,
                ScreenState = "[none]",
                Digest = "loot-inactive",
            };
        }

        private static GameResultsSnapshotPayload CreateInactiveGameResultsSnapshot()
        {
            GameResultsSnapshotPayload snapshot = new GameResultsSnapshotPayload
            {
                IsActive = false,
                ScreenState = "[none]",
                GameOverReason = "[none]",
                HasScore = false,
                CanContinue = false,
            };
            snapshot.Digest = ComputeGameResultsDigest(snapshot);
            return snapshot;
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
            where T : class
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return null;
            }

            return field.GetValue(instance) as T;
        }

        private static string ComputeBattleResultDigest(BattleResultPayload payload)
        {
            if (payload == null)
            {
                return "0000000000000000";
            }

            string raw =
                payload.EventId + ":" +
                payload.IsFightComplete + ":" +
                payload.IsBattleSequenceComplete + ":" +
                payload.HasNextBattle + ":" +
                payload.IsForceEnd + ":" +
                payload.IsRetreat + ":" +
                payload.LootReason + ":" +
                payload.LootReasonId + ":" +
                payload.CurrentBattleConfigurationId + ":" +
                string.Join("|", (payload.LootRewards ?? Array.Empty<LootRewardPayload>())
                    .Select(reward => reward.Type + "," + reward.Id + "," + reward.Quantity)
                    .ToArray());
            return ComputeStableDigest(raw);
        }

        private static string ComputeLootWindowDigest(LootWindowSnapshotPayload payload)
        {
            if (payload == null)
            {
                return "0000000000000000";
            }

            string raw =
                payload.IsActive + ":" +
                payload.ScreenState + ":" +
                payload.Reason + ":" +
                payload.ReasonId + ":" +
                payload.HeroPoints + ":" +
                payload.TorchGain + ":" +
                payload.ArmorGain + ":" +
                payload.WheelGain + ":" +
                payload.HeroDied + ":" +
                string.Join("|", (payload.Items ?? Array.Empty<LootItemSnapshotPayload>())
                    .Select(item => item.InventoryIndex + "," + item.ItemId + "," + item.Quantity + "," + item.Duration)
                    .ToArray()) + ":" +
                string.Join("|", (payload.SkillsGranted ?? Array.Empty<string>()).OrderBy(skill => skill).ToArray());
            return ComputeStableDigest(raw);
        }

        private static string ComputeGameResultsDigest(GameResultsSnapshotPayload payload)
        {
            if (payload == null)
            {
                return "0000000000000000";
            }

            string raw =
                payload.IsActive + ":" +
                payload.ScreenState + ":" +
                payload.GameOverReason + ":" +
                payload.HasScore + ":" +
                payload.CanContinue;
            return ComputeStableDigest(raw);
        }

        private static string ComputeStableDigest(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string value = text ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }

                return hash.ToString("x16");
            }
        }

        private sealed class LootItemRuntimeEntry
        {
            public LootItemRuntimeEntry(int inventoryIndex, IReadOnlyItemInstance item)
            {
                InventoryIndex = inventoryIndex;
                Item = item;
            }

            public int InventoryIndex { get; }

            public IReadOnlyItemInstance Item { get; }
        }
    }
}
