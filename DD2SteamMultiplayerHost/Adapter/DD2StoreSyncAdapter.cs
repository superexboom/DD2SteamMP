using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Code.Cost;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Locale;
using Assets.Code.Run;
using Assets.Code.Source;
using Assets.Code.UI.Items;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2StoreSyncAdapter : IStoreActionAdapter, IDisposable
    {
        private const float CachedSnapshotForcedRefreshInterval = 10f;

        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private bool _snapshotDirty = true;
        private float _nextForcedSnapshotRefreshTime;
        private StoreSnapshotPayload _cachedSnapshot;

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
                    HostLog.Write("[store] EventManager is not ready; store sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventInventoryItemPurchased>(HandleStoreInventoryChanged, false, 0);
            EventManager.AddListener<EventInventoryItemQtyChange>(HandleStoreInventoryChanged, false, 0);
            EventManager.AddListener<EventInventorySlotsChanged>(HandleStoreInventoryChanged, false, 0);
            EventManager.AddListener<EventItemDiscarded>(HandleStoreInventoryChanged, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[store] Store listeners registered; dirty-cache enabled.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventInventoryItemPurchased>(HandleStoreInventoryChanged);
            EventManager.RemoveListener<EventInventoryItemQtyChange>(HandleStoreInventoryChanged);
            EventManager.RemoveListener<EventInventorySlotsChanged>(HandleStoreInventoryChanged);
            EventManager.RemoveListener<EventItemDiscarded>(HandleStoreInventoryChanged);
            _listenersRegistered = false;
        }

        public bool TryGetStoreSnapshot(out StoreSnapshotPayload snapshot)
        {
            try
            {
                if (!IsStoreUiActive())
                {
                    snapshot = CreateInactiveSnapshot();
                    CacheSnapshot(snapshot);
                    _snapshotDirty = true;
                    return true;
                }

                if (CanUseCachedSnapshot())
                {
                    snapshot = _cachedSnapshot;
                    return true;
                }

                StoreContext context;
                if (!TryFindActiveStore(out context))
                {
                    snapshot = CreateInactiveSnapshot();
                    CacheSnapshot(snapshot);
                    _snapshotDirty = true;
                    return true;
                }

                snapshot = BuildSnapshot(context);
                CacheSnapshot(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[store] Failed to collect store snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        public bool TryExecuteStoreAction(
            StoreActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty store action request";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "buy", StringComparison.Ordinal) &&
                !string.Equals(action, "purchase", StringComparison.Ordinal))
            {
                message = "unsupported store action: " + request.Action;
                return false;
            }

            if (request.Quantity != 1)
            {
                message = "store purchase currently supports quantity 1 only";
                return false;
            }

            StoreContext context;
            if (!TryFindActiveStore(out context))
            {
                message = "store is not active on host";
                return false;
            }

            PlayerInventoryItemContainerBhv activePlayerItemContainer = context.Container.ActivePlayerItemContainer;
            if (activePlayerItemContainer == null || activePlayerItemContainer.Inventory == null)
            {
                message = "active player inventory is not available";
                return false;
            }

            ItemInventory storeInventory = context.Container.Inventory;
            if (storeInventory == null ||
                request.InventoryIndex < 0 ||
                request.InventoryIndex >= storeInventory.GetNumberOfTotalSlots())
            {
                message = "store inventory index is invalid: " + request.InventoryIndex;
                return false;
            }

            IReadOnlyItemInstance item = storeInventory.GetItemOrDefault(request.InventoryIndex);
            if (!ItemUtils.IsValid(item))
            {
                message = "store item is no longer available at index " + request.InventoryIndex;
                return false;
            }

            ItemDefinition itemDefinition = item.GetItemDefinition();
            string itemId = itemDefinition == null ? string.Empty : itemDefinition.m_id;
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(itemId, request.ItemId, StringComparison.Ordinal))
            {
                message = "store item changed at index " + request.InventoryIndex +
                    ": expected " + request.ItemId +
                    ", got " + itemId;
                return false;
            }

            float costMultiplier = GetStoreCostMultiplier(item);
            string priceText = GetStorePriceText(item, costMultiplier);
            if (itemDefinition.m_isBuyHidden &&
                !CostCalculation.CanAffordCost(item.BuyCostDefinition, costMultiplier))
            {
                message = "store item " + itemId + " is hidden until affordable";
                return false;
            }

            if (!CostCalculation.CanAffordCost(item.BuyCostDefinition, costMultiplier))
            {
                message = "cannot afford " + itemId +
                    " price=" + priceText;
                return false;
            }

            StoreInventoryItemBhv storeItemBhv;
            if (TryFindStoreItemBehaviour(context, request.InventoryIndex, itemId, out storeItemBhv))
            {
                int beforeQty = item.GetQty();
                try
                {
                    storeItemBhv.OnTryBuyItem();
                    context.Container.SetAllItemsDirty();
                    context.Container.CheckIfOutOfStock();
                    MarkSnapshotDirty();
                    if (!DidStoreItemQuantityDecrease(storeInventory, request.InventoryIndex, itemId, beforeQty))
                    {
                        message = "native store buy did not change store item " + itemId +
                            " at index " + request.InventoryIndex;
                        HostLog.Write("[store-action] " + message + ".");
                        return false;
                    }

                    HostLog.Write("[store-action] " + senderName + "/" + senderSteamId +
                        " bought item=" + itemId +
                        " from " + context.StoreKind +
                        "[" + request.InventoryIndex + "] via native UI" +
                        " price=" + priceText + ".");
                    message = "bought " + itemId +
                        " from store index " + request.InventoryIndex;
                    return true;
                }
                catch (Exception ex)
                {
                    message = "native store purchase failed: " + ex.Message;
                    HostLog.Write("[store-action] " + message + ".");
                    return false;
                }
            }

            if (item.BuyCostDefinition != null &&
                !CostCalculation.AttemptSpendCost(item.BuyCostDefinition, costMultiplier, SourceType.STORE))
            {
                message = "failed to spend store cost for " + itemId;
                return false;
            }

            try
            {
                ItemInstance purchasedItem = storeInventory.TakeItemQty(request.InventoryIndex, 1);
                if (purchasedItem == null || purchasedItem.GetItemDefinition() == null)
                {
                    message = "store inventory did not return purchased item";
                    return false;
                }

                purchasedItem.SetQty(1, 1);
                activePlayerItemContainer.Inventory.AddItemsWithOverflow(
                    purchasedItem.GetItemDefinition(),
                    1,
                    true);
                context.Container.SetAllItemsDirty();
                context.Container.CheckIfOutOfStock();
                EventInventoryItemPurchased.Trigger(purchasedItem.GetItemDefinition(), false);
                MarkSnapshotDirty();

                HostLog.Write("[store-action] " + senderName + "/" + senderSteamId +
                    " bought item=" + purchasedItem.GetItemDefinition().m_id +
                    " from " + context.StoreKind +
                    "[" + request.InventoryIndex + "] via direct inventory" +
                    " price=" + priceText + ".");
                message = "bought " + purchasedItem.GetItemDefinition().m_id +
                    " from store index " + request.InventoryIndex;
                return true;
            }
            catch (Exception ex)
            {
                message = "store purchase failed: " + ex.Message;
                HostLog.Write("[store-action] " + message + ".");
                return false;
            }
        }

        private bool CanUseCachedSnapshot()
        {
            return _cachedSnapshot != null &&
                _cachedSnapshot.IsActive &&
                !_snapshotDirty &&
                Time.unscaledTime < _nextForcedSnapshotRefreshTime;
        }

        private void CacheSnapshot(StoreSnapshotPayload snapshot)
        {
            _cachedSnapshot = snapshot;
            _snapshotDirty = false;
            _nextForcedSnapshotRefreshTime = Time.unscaledTime + CachedSnapshotForcedRefreshInterval;
        }

        private void MarkSnapshotDirty()
        {
            _snapshotDirty = true;
        }

        private void HandleStoreInventoryChanged(EventInventoryItemPurchased evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleStoreInventoryChanged(EventInventoryItemQtyChange evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleStoreInventoryChanged(EventInventorySlotsChanged evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleStoreInventoryChanged(EventItemDiscarded evt)
        {
            MarkSnapshotDirty();
        }

        private static StoreSnapshotPayload BuildSnapshot(StoreContext context)
        {
            List<StoreItemPayload> items = new List<StoreItemPayload>();
            ItemInventory inventory = context.Container.Inventory;
            int slotCount = inventory == null ? 0 : inventory.GetNumberOfTotalSlots();
            for (int i = 0; i < slotCount; i++)
            {
                IReadOnlyItemInstance item = inventory.GetItemOrDefault(i);
                if (!ItemUtils.IsValid(item))
                {
                    continue;
                }

                float costMultiplier = GetStoreCostMultiplier(item);
                bool canAfford = CostCalculation.CanAffordCost(item.BuyCostDefinition, costMultiplier);
                ItemDefinition itemDefinition = item.GetItemDefinition();
                if (itemDefinition.m_isBuyHidden && !canAfford)
                {
                    continue;
                }

                items.Add(new StoreItemPayload(
                    i,
                    itemDefinition.m_id,
                    GetItemDisplayName(itemDefinition),
                    itemDefinition.m_type == null ? null : itemDefinition.m_type.GetName(),
                    item.GetQty(),
                    GetStorePriceText(item, costMultiplier),
                    canAfford,
                    costMultiplier,
                    itemDefinition.m_isBuyHidden,
                    GetItemDescription(itemDefinition)));
            }

            StoreSnapshotPayload snapshot = new StoreSnapshotPayload
            {
                IsActive = true,
                StoreKind = context.StoreKind,
                ScreenState = context.ScreenState,
                Items = items,
            };
            snapshot.Digest = ComputeStoreDigest(snapshot);
            return snapshot;
        }

        private static bool IsStoreUiActive()
        {
            try
            {
                return SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsAnyStoreActive;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindActiveStore(out StoreContext context)
        {
            context = null;

            InnStoreUiBhv innStore = UnityObject.FindObjectsOfType<InnStoreUiBhv>(true)
                .Where(store => store != null &&
                    store.gameObject != null &&
                    store.gameObject.activeInHierarchy &&
                    store.ItemContainerBhv != null &&
                    store.ItemContainerBhv.Inventory != null)
                .OrderByDescending(store => store.enabled)
                .FirstOrDefault();
            if (innStore != null)
            {
                context = new StoreContext("inn", "[inn]", innStore.ItemContainerBhv);
                return true;
            }

            StoreUiBhv storeUi = UnityObject.FindObjectsOfType<StoreUiBhv>(true)
                .Where(store => store != null &&
                    store.gameObject != null &&
                    store.gameObject.activeInHierarchy &&
                    store.ItemContainerBhv != null &&
                    store.ItemContainerBhv.Inventory != null)
                .OrderByDescending(store => store.enabled)
                .FirstOrDefault();
            if (storeUi != null)
            {
                context = new StoreContext("store", SafeGetScreenState(storeUi), storeUi.ItemContainerBhv);
                return true;
            }

            return false;
        }

        private static bool TryFindStoreItemBehaviour(
            StoreContext context,
            int inventoryIndex,
            string itemId,
            out StoreInventoryItemBhv storeItemBhv)
        {
            storeItemBhv = UnityObject.FindObjectsOfType<StoreInventoryItemBhv>(true)
                .Where(candidate => candidate != null &&
                    candidate.ItemContainer == context.Container &&
                    candidate.ItemIndex == inventoryIndex &&
                    ItemUtils.IsValid(candidate.Item) &&
                    candidate.Item.GetItemDefinition() != null &&
                    string.Equals(candidate.Item.GetItemDefinition().m_id, itemId, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.gameObject != null && candidate.gameObject.activeInHierarchy)
                .FirstOrDefault();
            return storeItemBhv != null;
        }

        private static bool DidStoreItemQuantityDecrease(
            ItemInventory storeInventory,
            int inventoryIndex,
            string itemId,
            int beforeQty)
        {
            if (storeInventory == null)
            {
                return false;
            }

            IReadOnlyItemInstance currentItem = storeInventory.GetItemOrDefault(inventoryIndex);
            if (!ItemUtils.IsValid(currentItem) || currentItem.GetItemDefinition() == null)
            {
                return beforeQty <= 1;
            }

            if (!string.Equals(currentItem.GetItemDefinition().m_id, itemId, StringComparison.Ordinal))
            {
                return true;
            }

            return currentItem.GetQty() < beforeQty;
        }

        private static string SafeGetScreenState(StoreUiBhv storeUi)
        {
            try
            {
                UiScreenBhv screen = GetPrivateField<UiScreenBhv>(storeUi, "m_screenBhv");
                return screen == null ? "[none]" : Convert.ToString(screen.ScreenState);
            }
            catch
            {
                return "[none]";
            }
        }

        private static float GetStoreCostMultiplier(IReadOnlyItemInstance item)
        {
            try
            {
                if (item == null ||
                    item.GetItemType() == null ||
                    !Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.RunDataManager == null)
                {
                    return 1f;
                }

                return Singleton<GameTypeMgr>.Instance.RunDataManager.GetStatValue(
                    RunStatType.STORE_COST_BUY_MULTIPLIER,
                    item.GetItemType().GetName());
            }
            catch
            {
                return 1f;
            }
        }

        private static string GetStorePriceText(IReadOnlyItemInstance item, float costMultiplier)
        {
            try
            {
                return item == null
                    ? string.Empty
                    : CostDescription.GetStoreBuyDescription(item.BuyCostDefinition, costMultiplier, true);
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

        private static string GetItemDescription(ItemDefinition itemDefinition)
        {
            if (itemDefinition == null)
            {
                return string.Empty;
            }

            try
            {
                return ItemDescription.GetDescription(
                    itemDefinition,
                    -1,
                    false,
                    0,
                    true,
                    false,
                    false,
                    true,
                    true,
                    null) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static StoreSnapshotPayload CreateInactiveSnapshot()
        {
            StoreSnapshotPayload snapshot = new StoreSnapshotPayload
            {
                IsActive = false,
                StoreKind = "[none]",
                ScreenState = "[none]",
                Items = new List<StoreItemPayload>(),
            };
            snapshot.Digest = ComputeStoreDigest(snapshot);
            return snapshot;
        }

        private static string ComputeStoreDigest(StoreSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string itemPart = string.Join(
                "|",
                (snapshot.Items ?? Array.Empty<StoreItemPayload>())
                    .OrderBy(item => item.InventoryIndex)
                    .Select(item => item.InventoryIndex +
                        ":" + (item.ItemId ?? string.Empty) +
                        ":" + item.Quantity +
                        ":" + (item.PriceText ?? string.Empty) +
                        ":" + item.CanAfford)
                    .ToArray());
            return ComputeStableDigest(
                snapshot.IsActive + ";" +
                (snapshot.StoreKind ?? string.Empty) + ";" +
                (snapshot.ScreenState ?? string.Empty) + ";" +
                itemPart);
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

        private static T GetPrivateField<T>(object target, string fieldName)
            where T : class
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            System.Reflection.FieldInfo field = target.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target) as T;
        }

        private sealed class StoreContext
        {
            public StoreContext(string storeKind, string screenState, StoreInventoryItemContainerBhv container)
            {
                StoreKind = storeKind;
                ScreenState = screenState;
                Container = container;
            }

            public string StoreKind { get; }

            public string ScreenState { get; }

            public StoreInventoryItemContainerBhv Container { get; }
        }
    }
}
