using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.Events;
using Assets.Code.Analytics;
using Assets.Code.Campaign;
using Assets.Code.Condition;
using Assets.Code.Cost;
using Assets.Code.Effect;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Inn.Events;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Library;
using Assets.Code.Math;
using Assets.Code.Roster.Events;
using Assets.Code.Run;
using Assets.Code.Skill;
using Assets.Code.Skill.Events;
using Assets.Code.Source;
using Assets.Code.UI.Events;
using Assets.Code.UI.HeroSelect;
using Assets.Code.Unlock;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2HeroLoadoutSyncAdapter : IHeroLoadoutAdapter, IDisposable
    {
        private const float CachedSnapshotForcedRefreshInterval = 10f;

        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private bool _snapshotDirty = true;
        private float _nextForcedSnapshotRefreshTime;
        private string _cachedModeName;
        private HeroLoadoutSnapshotPayload _cachedSnapshot;

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
                    HostLog.Write("[hero-loadout] EventManager is not ready; hero loadout sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventActorChangeSkillEquipped>(HandleEventActorChangeSkillEquipped, false, 0);
            EventManager.AddListener<EventSkillUpgradeCommitted>(HandleEventSkillUpgradeCommitted, false, 0);
            EventManager.AddListener<EventInventoryItemQtyChange>(HandleEventInventoryItemQtyChange, false, 0);
            EventManager.AddListener<EventInventorySlotsChanged>(HandleEventInventorySlotsChanged, false, 0);
            EventManager.AddListener<EventInventoryItemPurchased>(HandleEventInventoryItemPurchased, false, 0);
            EventManager.AddListener<EventInventoryItemConsumed>(HandleEventInventoryItemConsumed, false, 0);
            EventManager.AddListener<EventLootItemReceived>(HandleEventLootItemReceived, false, 0);
            EventManager.AddListener<EventItemDiscarded>(HandleEventItemDiscarded, false, 0);
            EventManager.AddListener<EventHeroSelectRosterChanged>(HandleHeroSelectChanged, false, 0);
            EventManager.AddListener<EventHeroSelectRosterActorsSwapped>(HandleHeroSelectChanged, false, 0);
            EventManager.AddListener<EventRosterConfirmParty>(HandleHeroSelectChanged, false, 0);
            EventManager.AddListener<EventHeroSelectPathChange>(HandleHeroSelectChanged, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[hero-loadout] Hero loadout listeners registered; dirty-cache enabled.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventActorChangeSkillEquipped>(HandleEventActorChangeSkillEquipped);
            EventManager.RemoveListener<EventSkillUpgradeCommitted>(HandleEventSkillUpgradeCommitted);
            EventManager.RemoveListener<EventInventoryItemQtyChange>(HandleEventInventoryItemQtyChange);
            EventManager.RemoveListener<EventInventorySlotsChanged>(HandleEventInventorySlotsChanged);
            EventManager.RemoveListener<EventInventoryItemPurchased>(HandleEventInventoryItemPurchased);
            EventManager.RemoveListener<EventInventoryItemConsumed>(HandleEventInventoryItemConsumed);
            EventManager.RemoveListener<EventLootItemReceived>(HandleEventLootItemReceived);
            EventManager.RemoveListener<EventItemDiscarded>(HandleEventItemDiscarded);
            EventManager.RemoveListener<EventHeroSelectRosterChanged>(HandleHeroSelectChanged);
            EventManager.RemoveListener<EventHeroSelectRosterActorsSwapped>(HandleHeroSelectChanged);
            EventManager.RemoveListener<EventRosterConfirmParty>(HandleHeroSelectChanged);
            EventManager.RemoveListener<EventHeroSelectPathChange>(HandleHeroSelectChanged);
            _listenersRegistered = false;
        }

        public bool TryGetHeroLoadoutSnapshot(out HeroLoadoutSnapshotPayload snapshot)
        {
            snapshot = null;

            try
            {
                string currentMode = SafeGetCurrentGameModeName();
                if (GameModeMgr.CurrentMode == GameModeType.COMBAT)
                {
                    snapshot = CreateInactiveSnapshot("combat");
                    CacheSnapshot(snapshot, currentMode);
                    return true;
                }

                if (CanUseCachedSnapshot(currentMode))
                {
                    snapshot = _cachedSnapshot;
                    return true;
                }

                bool includeRestItems = GameModeMgr.CurrentMode == GameModeType.INN;
                string scope;
                List<HeroLoadoutActorSource> sources = GetActorSources(out scope);
                snapshot = new HeroLoadoutSnapshotPayload
                {
                    IsActive = sources.Count > 0,
                    Scope = scope,
                    CurrentGameMode = SafeGetCurrentGameModeName(),
                    HeroUpgradePoints = GetHeroUpgradePoints(),
                    CanMasterSkills = CanMasterSkills(),
                    InventoryItems = BuildPlayerLoadoutItems(includeRestItems),
                    Actors = sources
                        .Select(BuildActorPayload)
                        .Where(actor => actor != null)
                        .OrderBy(actor => actor.HeroSlot)
                        .ThenBy(actor => actor.ActorGuid)
                        .ToList(),
                };
                snapshot.Digest = ComputeHeroLoadoutDigest(snapshot);
                CacheSnapshot(snapshot, currentMode);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[hero-loadout] Failed to collect hero loadout snapshot: " + ex.Message + ".");
                return false;
            }
        }

        private bool CanUseCachedSnapshot(string currentMode)
        {
            return _cachedSnapshot != null &&
                !_snapshotDirty &&
                Time.unscaledTime < _nextForcedSnapshotRefreshTime &&
                string.Equals(_cachedModeName ?? string.Empty, currentMode ?? string.Empty, StringComparison.Ordinal);
        }

        private void CacheSnapshot(HeroLoadoutSnapshotPayload snapshot, string currentMode)
        {
            _cachedSnapshot = snapshot;
            _cachedModeName = currentMode ?? string.Empty;
            _snapshotDirty = false;
            _nextForcedSnapshotRefreshTime = Time.unscaledTime + CachedSnapshotForcedRefreshInterval;
        }

        private void MarkSnapshotDirty()
        {
            _snapshotDirty = true;
        }

        private static HeroLoadoutSnapshotPayload CreateInactiveSnapshot(string scope)
        {
            HeroLoadoutSnapshotPayload snapshot = new HeroLoadoutSnapshotPayload
            {
                IsActive = false,
                Scope = string.IsNullOrWhiteSpace(scope) ? "none" : scope,
                CurrentGameMode = SafeGetCurrentGameModeName(),
                HeroUpgradePoints = 0,
                CanMasterSkills = false,
                InventoryItems = new List<HeroLoadoutItemPayload>(),
                Actors = new List<HeroLoadoutActorPayload>(),
            };
            snapshot.Digest = ComputeHeroLoadoutDigest(snapshot);
            return snapshot;
        }

        public bool TryExecuteHeroLoadoutRequest(
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty hero loadout request";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "master_skill", StringComparison.Ordinal))
            {
                bool accepted = TryExecuteMasterSkillRequest(request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (string.Equals(action, "equip_item", StringComparison.Ordinal) ||
                string.Equals(action, "unequip_item", StringComparison.Ordinal))
            {
                bool accepted = TryExecuteItemLoadoutRequest(action, request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (string.Equals(action, "use_rest_item", StringComparison.Ordinal))
            {
                bool accepted = TryExecuteRestItemRequest(request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (!string.Equals(action, "set_skill", StringComparison.Ordinal))
            {
                message = "unsupported hero loadout action: " + request.Action;
                return false;
            }

            if (GameModeMgr.CurrentMode == GameModeType.COMBAT)
            {
                message = "skill loadout cannot be edited during combat";
                return false;
            }

            uint actorGuid;
            if (!TryParseActorGuid(request.ActorGuid, out actorGuid))
            {
                message = "invalid actor guid: " + (request.ActorGuid ?? "[none]");
                return false;
            }

            ActorInstance actor;
            if (!TryResolveActor(actorGuid, out actor))
            {
                message = "actor " + actorGuid + " was not found";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.SkillId))
            {
                message = "missing skill id";
                return false;
            }

            SkillInstance skill = actor.GetCombatSkillInstance(request.SkillId);
            if (skill == null)
            {
                message = "skill " + request.SkillId + " is not on actor " + DescribeActor(actor);
                return false;
            }

            if (!skill.GetIsUnlocked())
            {
                message = "skill " + request.SkillId + " is locked on actor " + DescribeActor(actor);
                return false;
            }

            if (!skill.GetCanChangeEquip(actorGuid, request.Equip))
            {
                message = "skill " + request.SkillId + " cannot be " + (request.Equip ? "equipped" : "unequipped");
                return false;
            }

            bool before = actor.GetCombatSkillEquipped(request.SkillId);
            if (before == request.Equip)
            {
                message = "skill " + request.SkillId + " already " + (request.Equip ? "equipped" : "unequipped");
                return true;
            }

            int equipLimit = GetEquippedSkillLimit(actor);
            int equippedCount = SafeGetEquippedCharacterSheetSkillIds(actor).Count;
            if (request.Equip && equipLimit > 0 && equippedCount >= equipLimit)
            {
                message = "skill limit reached: " + equippedCount + "/" + equipLimit;
                return false;
            }

            try
            {
                HostLog.Write("[hero-loadout-action] " + senderName + "/" + senderSteamId +
                    " " + (request.Equip ? "equips" : "unequips") +
                    " skill=" + request.SkillId +
                    " actor=" + DescribeActor(actor) +
                    " slot=" + request.HeroSlot + ".");
                actor.SetCombatSkillEquipped(request.SkillId, request.Equip, true);

                bool after = actor.GetCombatSkillEquipped(request.SkillId);
                if (after != request.Equip)
                {
                    message = "game rejected skill loadout change; requested=" + request.Equip + ", actual=" + after;
                    HostLog.Write("[hero-loadout-action] " + message + ".");
                    return false;
                }

                message = "skill " + request.SkillId + " " + (request.Equip ? "equipped" : "unequipped") +
                    " on actor " + DescribeActor(actor);
                MarkSnapshotDirty();
                return true;
            }
            catch (Exception ex)
            {
                message = "skill loadout change failed: " + ex.Message;
                HostLog.Write("[hero-loadout-action] " + message + ".");
                return false;
            }
        }

        private static bool TryExecuteItemLoadoutRequest(
            string action,
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (GameModeMgr.CurrentMode == GameModeType.COMBAT)
            {
                message = "hero equipment cannot be changed during combat";
                return false;
            }

            uint actorGuid;
            if (!TryParseActorGuid(request.ActorGuid, out actorGuid))
            {
                message = "invalid actor guid: " + (request.ActorGuid ?? "[none]");
                return false;
            }

            ActorInstance actor;
            if (!TryResolveActor(actorGuid, out actor))
            {
                message = "actor " + actorGuid + " was not found";
                return false;
            }

            string itemKind = NormalizeItemKind(request.ItemKind);
            if (itemKind == null)
            {
                message = "unsupported item kind: " + (request.ItemKind ?? "[none]");
                return false;
            }

            ItemInventory targetInventory = GetActorEquipmentInventory(actor, itemKind);
            if (targetInventory == null)
            {
                message = "target inventory " + itemKind + " is not available on actor " + DescribeActor(actor);
                return false;
            }

            if (!Singleton<GameTypeMgr>.HasInstance() ||
                Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
            {
                message = "player inventory is not available";
                return false;
            }

            ItemInventory playerInventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            if (string.Equals(action, "equip_item", StringComparison.Ordinal))
            {
                return TryEquipItemFromPlayerInventory(
                    actor,
                    playerInventory,
                    targetInventory,
                    itemKind,
                    request,
                    senderSteamId,
                    senderName,
                    out message);
            }

            return TryUnequipItemToPlayerInventory(
                actor,
                playerInventory,
                targetInventory,
                itemKind,
                request,
                senderSteamId,
                senderName,
                out message);
        }

        private static bool TryExecuteRestItemRequest(
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (GameModeMgr.CurrentMode != GameModeType.INN)
            {
                message = "inn item cannot be used outside the inn";
                return false;
            }

            if (!Singleton<GameTypeMgr>.HasInstance() ||
                Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
            {
                message = "player inventory is not available";
                return false;
            }

            uint ownerActorGuid;
            if (!TryParseActorGuid(request.ActorGuid, out ownerActorGuid))
            {
                message = "invalid actor guid: " + (request.ActorGuid ?? "[none]");
                return false;
            }

            ActorInstance ownerActor;
            if (!TryResolveActor(ownerActorGuid, out ownerActor))
            {
                message = "actor " + ownerActorGuid + " was not found";
                return false;
            }

            ItemInventory playerInventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            if (!IsInventoryIndexValid(playerInventory, request.SourceInventoryIndex))
            {
                message = "source inventory index is invalid: " + request.SourceInventoryIndex;
                return false;
            }

            IReadOnlyItemInstance sourceItem = playerInventory.GetItemOrDefault(request.SourceInventoryIndex);
            if (!ItemUtils.IsValid(sourceItem))
            {
                message = "source inventory slot " + request.SourceInventoryIndex + " is empty";
                return false;
            }

            ItemDefinition itemDefinition = sourceItem.GetItemDefinition();
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(itemDefinition.m_id, request.ItemId, StringComparison.Ordinal))
            {
                message = "source inventory slot " + request.SourceInventoryIndex +
                    " contains " + itemDefinition.m_id +
                    ", not " + request.ItemId;
                return false;
            }

            if (!IsRestItemDefinition(itemDefinition))
            {
                message = "item " + itemDefinition.m_id + " is not an inn rest item";
                return false;
            }

            List<ActorInstance> targets;
            if (!TryResolveRestItemTargets(itemDefinition, request, out targets, out message))
            {
                return false;
            }

            try
            {
                HostLog.Write("[hero-rest-item-action] " + senderName + "/" + senderSteamId +
                    " uses rest item=" + itemDefinition.m_id +
                    " from playerInventory[" + request.SourceInventoryIndex + "]" +
                    " ownerActor=" + DescribeActor(ownerActor) +
                    " targets=" + string.Join(",", targets.Select(DescribeActor).ToArray()) + ".");

                ApplyRestItem(itemDefinition, targets);
                EventInventoryItemConsumed.Trigger(playerInventory, itemDefinition);

                if (sourceItem.IsConsumable())
                {
                    playerInventory.RemoveSingleItemAt(request.SourceInventoryIndex);
                }

                message = "used inn item " + itemDefinition.m_id +
                    " on " + string.Join(",", targets.Select(DescribeActor).ToArray());
                return true;
            }
            catch (Exception ex)
            {
                message = "inn item use failed: " + ex.Message;
                HostLog.Write("[hero-rest-item-action] " + message + ".");
                return false;
            }
        }

        private static bool TryEquipItemFromPlayerInventory(
            ActorInstance actor,
            ItemInventory playerInventory,
            ItemInventory targetInventory,
            string itemKind,
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (!IsInventoryIndexValid(playerInventory, request.SourceInventoryIndex))
            {
                message = "source inventory index is invalid: " + request.SourceInventoryIndex;
                return false;
            }

            if (!IsInventoryIndexValid(targetInventory, request.TargetInventoryIndex))
            {
                message = "target " + itemKind + " slot is invalid: " + request.TargetInventoryIndex;
                return false;
            }

            IReadOnlyItemInstance sourceItem = playerInventory.GetItemOrDefault(request.SourceInventoryIndex);
            if (!ItemUtils.IsValid(sourceItem))
            {
                message = "source inventory slot " + request.SourceInventoryIndex + " is empty";
                return false;
            }

            ItemDefinition itemDefinition = sourceItem.GetItemDefinition();
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(itemDefinition.m_id, request.ItemId, StringComparison.Ordinal))
            {
                message = "source inventory slot " + request.SourceInventoryIndex +
                    " contains " + itemDefinition.m_id +
                    ", not " + request.ItemId;
                return false;
            }

            if (!CanEquipItem(actor, targetInventory, itemKind, itemDefinition, out message))
            {
                return false;
            }

            try
            {
                HostLog.Write("[hero-equipment-action] " + senderName + "/" + senderSteamId +
                    " equips " + itemKind +
                    " item=" + itemDefinition.m_id +
                    " from playerInventory[" + request.SourceInventoryIndex + "]" +
                    " to actor=" + DescribeActor(actor) +
                    " slot=" + request.TargetInventoryIndex + ".");
                targetInventory.SwapItems(playerInventory, request.SourceInventoryIndex, request.TargetInventoryIndex, false);
                message = "equipped " + itemDefinition.m_id +
                    " to " + itemKind + " slot " + request.TargetInventoryIndex +
                    " on actor " + DescribeActor(actor);
                return true;
            }
            catch (Exception ex)
            {
                message = "equipment change failed: " + ex.Message;
                HostLog.Write("[hero-equipment-action] " + message + ".");
                return false;
            }
        }

        private static bool TryUnequipItemToPlayerInventory(
            ActorInstance actor,
            ItemInventory playerInventory,
            ItemInventory targetInventory,
            string itemKind,
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (!IsInventoryIndexValid(targetInventory, request.TargetInventoryIndex))
            {
                message = "target " + itemKind + " slot is invalid: " + request.TargetInventoryIndex;
                return false;
            }

            IReadOnlyItemInstance targetItem = targetInventory.GetItemOrDefault(request.TargetInventoryIndex);
            if (!ItemUtils.IsValid(targetItem))
            {
                message = itemKind + " slot " + request.TargetInventoryIndex + " is empty";
                return false;
            }

            ItemDefinition itemDefinition = targetItem.GetItemDefinition();
            if (!string.IsNullOrWhiteSpace(request.ItemId) &&
                !string.Equals(itemDefinition.m_id, request.ItemId, StringComparison.Ordinal))
            {
                message = itemKind + " slot " + request.TargetInventoryIndex +
                    " contains " + itemDefinition.m_id +
                    ", not " + request.ItemId;
                return false;
            }

            int destinationIndex = playerInventory.FindFirstEmptySlot();
            if (destinationIndex < 0)
            {
                message = "player inventory has no empty slot for unequipping " + itemDefinition.m_id;
                return false;
            }

            try
            {
                HostLog.Write("[hero-equipment-action] " + senderName + "/" + senderSteamId +
                    " unequips " + itemKind +
                    " item=" + itemDefinition.m_id +
                    " from actor=" + DescribeActor(actor) +
                    " slot=" + request.TargetInventoryIndex +
                    " to playerInventory[" + destinationIndex + "].");
                playerInventory.SwapItems(targetInventory, request.TargetInventoryIndex, destinationIndex, false);
                message = "unequipped " + itemDefinition.m_id +
                    " from " + itemKind + " slot " + request.TargetInventoryIndex +
                    " on actor " + DescribeActor(actor);
                return true;
            }
            catch (Exception ex)
            {
                message = "equipment change failed: " + ex.Message;
                HostLog.Write("[hero-equipment-action] " + message + ".");
                return false;
            }
        }

        private static bool TryExecuteMasterSkillRequest(
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (GameModeMgr.CurrentMode == GameModeType.COMBAT)
            {
                message = "skill mastery cannot be changed during combat";
                return false;
            }

            if (!CanMasterSkills())
            {
                message = "trainer skill mastery is not available in the current host state";
                return false;
            }

            uint actorGuid;
            if (!TryParseActorGuid(request.ActorGuid, out actorGuid))
            {
                message = "invalid actor guid: " + (request.ActorGuid ?? "[none]");
                return false;
            }

            ActorInstance actor;
            if (!TryResolveActor(actorGuid, out actor))
            {
                message = "actor " + actorGuid + " was not found";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.SkillId))
            {
                message = "missing skill id";
                return false;
            }

            SkillInstance skill = actor.GetCombatSkillInstance(request.SkillId);
            if (skill == null)
            {
                message = "skill " + request.SkillId + " is not on actor " + DescribeActor(actor);
                return false;
            }

            if (!skill.GetIsUnlocked())
            {
                message = "skill " + request.SkillId + " is locked on actor " + DescribeActor(actor);
                return false;
            }

            if (skill.GetIsUpgraded())
            {
                message = "skill " + request.SkillId + " is already mastered";
                return false;
            }

            string masterySkillId;
            string resolveMessage;
            if (!TryResolveMasterySkillId(actor, request.SkillId, out masterySkillId, out resolveMessage))
            {
                message = resolveMessage;
                return false;
            }

            UnlockDefinition unlock = SkillUtils.GetUnlockFromSkillId(masterySkillId);
            if (unlock == null)
            {
                message = "skill " + masterySkillId + " has no mastery unlock";
                return false;
            }

            if (unlock.CostDefinition != null && !CostCalculation.CanAffordCost(unlock.CostDefinition))
            {
                message = "cannot afford mastery cost for " + request.SkillId +
                    "; masteryPoints=" + GetHeroUpgradePoints();
                return false;
            }

            try
            {
                int pointsBefore = GetHeroUpgradePoints();
                int skillCountBefore = SafeGetUnlockedCharacterSheetSkillIds(actor).Count;
                HostLog.Write("[hero-mastery-action] " + senderName + "/" + senderSteamId +
                    " masters skill=" + request.SkillId +
                    " -> " + masterySkillId +
                    " actor=" + DescribeActor(actor) +
                    " slot=" + request.HeroSlot +
                    ", pointsBefore=" + pointsBefore +
                    ", skillsBefore=" + skillCountBefore + ".");

                bool applied = ApplyVerifiedSkillUpgrade(actor, request.SkillId, masterySkillId, true, out message);
                if (!applied)
                {
                    HostLog.Write("[hero-mastery-action] " + message + ".");
                    return false;
                }

                ApplyModeLinkedSkillUpgrade(actor, masterySkillId);
                EventUpdatePlayerCurrency.Trigger();

                int skillCountAfter = SafeGetUnlockedCharacterSheetSkillIds(actor).Count;
                if (skillCountAfter <= 0)
                {
                    message = "skill mastery produced an invalid empty skill list for actor " + DescribeActor(actor);
                    HostLog.Write("[hero-mastery-action] " + message + ".");
                    return false;
                }

                message = "skill " + request.SkillId + " mastered as " + masterySkillId +
                    " on actor " + DescribeActor(actor) +
                    "; pointsAfter=" + GetHeroUpgradePoints() +
                    ", skillsAfter=" + skillCountAfter;
                return true;
            }
            catch (Exception ex)
            {
                message = "skill mastery failed: " + ex.Message;
                HostLog.Write("[hero-mastery-action] " + message + ".");
                return false;
            }
        }

        private static bool ApplyVerifiedSkillUpgrade(
            ActorInstance actor,
            string currentSkillId,
            string masterySkillId,
            bool spendCost,
            out string message)
        {
            message = string.Empty;
            if (actor == null)
            {
                message = "actor is missing";
                return false;
            }

            SkillInstance currentSkill = actor.GetCombatSkillInstance(currentSkillId);
            if (currentSkill == null)
            {
                message = "skill " + (currentSkillId ?? "[none]") + " is not on actor " + DescribeActor(actor);
                return false;
            }

            if (currentSkill.GetIsUpgraded())
            {
                message = "skill " + currentSkillId + " is already mastered";
                return false;
            }

            UnlockDefinition unlock = SkillUtils.GetUnlockFromSkillId(masterySkillId);
            if (unlock == null)
            {
                message = "skill " + (masterySkillId ?? "[none]") + " has no mastery unlock";
                return false;
            }

            if (unlock.RequirementIds == null || !unlock.RequirementIds.Contains(currentSkillId))
            {
                message = "refusing unsafe mastery " + currentSkillId + " -> " + masterySkillId +
                    "; unlock requirements do not contain the current skill";
                return false;
            }

            if (spendCost && unlock.CostDefinition != null &&
                !CostCalculation.AttemptSpendCost(unlock.CostDefinition, SourceType.STORE))
            {
                message = "failed to spend mastery cost for " + currentSkillId;
                return false;
            }

            actor.UnlockSkill(unlock, SourceType.INN);

            SkillInstance upgradedSkill = actor.GetCombatSkillInstance(unlock.m_Id);
            if (upgradedSkill == null || !upgradedSkill.GetIsUnlocked() || !upgradedSkill.GetIsUpgraded())
            {
                message = "game did not produce upgraded skill " + unlock.m_Id +
                    " for " + currentSkillId + " on actor " + DescribeActor(actor);
                return false;
            }

            EventSkillUpgradeCommitted.Trigger(unlock.m_Id, actor.ActorGuid);
            TryCacheSkillUpgradeAnalytics(actor, unlock.m_Id);
            message = "skill " + currentSkillId + " upgraded to " + unlock.m_Id;
            return true;
        }

        private static void ApplyModeLinkedSkillUpgrade(ActorInstance actor, string masterySkillId)
        {
            try
            {
                ActorDataSkill skillData = TryGetSkillData(masterySkillId);
                if (actor == null || skillData == null || skillData.ModeLinkedActorDataSkill == null)
                {
                    return;
                }

                string linkedMasterySkillId = skillData.ModeLinkedActorDataSkill.Id;
                UnlockDefinition linkedUnlock = SkillUtils.GetUnlockFromSkillId(linkedMasterySkillId);
                if (linkedUnlock == null || linkedUnlock.RequirementIds == null)
                {
                    return;
                }

                string linkedCurrentSkillId = linkedUnlock.RequirementIds
                    .FirstOrDefault(requirementId => actor.GetCombatSkillInstance(requirementId) != null);
                if (string.IsNullOrWhiteSpace(linkedCurrentSkillId))
                {
                    return;
                }

                string linkedMessage;
                if (!ApplyVerifiedSkillUpgrade(actor, linkedCurrentSkillId, linkedMasterySkillId, false, out linkedMessage))
                {
                    HostLog.Write("[hero-mastery-action] linked mastery skipped: " + linkedMessage + ".");
                    return;
                }

                HostLog.Write("[hero-mastery-action] linked mastery applied: " + linkedMessage + ".");
            }
            catch (Exception ex)
            {
                HostLog.Write("[hero-mastery-action] linked mastery failed: " + ex.Message + ".");
            }
        }

        private static List<HeroLoadoutActorSource> GetActorSources(out string scope)
        {
            List<HeroLoadoutActorSource> heroSelectActors = TryGetHeroSelectActorSources();
            if (heroSelectActors.Count > 0)
            {
                scope = "hero_select";
                return heroSelectActors;
            }

            List<HeroLoadoutActorSource> partyActors = TryGetPartyActorSources();
            if (partyActors.Count > 0)
            {
                scope = "party";
                return partyActors;
            }

            scope = "none";
            return new List<HeroLoadoutActorSource>();
        }

        private static List<HeroLoadoutActorSource> TryGetHeroSelectActorSources()
        {
            List<HeroLoadoutActorSource> sources = new List<HeroLoadoutActorSource>();
            HeroSelectBhv heroSelect = FindHeroSelect();
            if (!IsActive(heroSelect))
            {
                return sources;
            }

            List<uint> selectedActorGuids = GetPrivateField<List<uint>>(heroSelect, "m_SelectedActorGuids") ?? new List<uint>();
            for (int i = 0; i < selectedActorGuids.Count; i++)
            {
                uint actorGuid = selectedActorGuids[i];
                if (actorGuid == 0U)
                {
                    continue;
                }

                ActorInstance actor;
                if (!TryResolveActor(actorGuid, out actor))
                {
                    continue;
                }

                sources.Add(new HeroLoadoutActorSource(actor, i + 1, i, "hero_select"));
            }

            return DeduplicateSources(sources);
        }

        private static List<HeroLoadoutActorSource> TryGetPartyActorSources()
        {
            List<HeroLoadoutActorSource> sources = new List<HeroLoadoutActorSource>();
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.RosterManager == null)
                {
                    return sources;
                }

                IReadOnlyList<ActorInstance> actors = Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors();
                if (actors == null)
                {
                    return sources;
                }

                for (int i = 0; i < actors.Count; i++)
                {
                    ActorInstance actor = actors[i];
                    if (actor == null || actor.ActorGuid == 0U)
                    {
                        continue;
                    }

                    int teamPosition = actor.TeamPosition >= 0 ? actor.TeamPosition : i;
                    sources.Add(new HeroLoadoutActorSource(actor, teamPosition + 1, teamPosition, "party"));
                }
            }
            catch
            {
            }

            return DeduplicateSources(sources);
        }

        private static List<HeroLoadoutActorSource> DeduplicateSources(IEnumerable<HeroLoadoutActorSource> sources)
        {
            return (sources ?? Array.Empty<HeroLoadoutActorSource>())
                .Where(source => source != null && source.Actor != null && source.Actor.ActorGuid != 0U)
                .GroupBy(source => source.Actor.ActorGuid)
                .Select(group => group.OrderBy(source => source.HeroSlot).First())
                .OrderBy(source => source.HeroSlot)
                .ToList();
        }

        private static HeroLoadoutActorPayload BuildActorPayload(HeroLoadoutActorSource source)
        {
            ActorInstance actor = source == null ? null : source.Actor;
            if (actor == null)
            {
                return null;
            }

            IList<HeroLoadoutSkillPayload> skills = BuildSkillPayloads(actor);
            return new HeroLoadoutActorPayload
            {
                HeroSlot = source.HeroSlot,
                TeamPosition = source.TeamPosition,
                ActorGuid = actor.ActorGuid.ToString(),
                ActorDataId = SafeGetActorDataId(actor),
                ActorName = SafeGetActorName(actor),
                PathId = SafeGetActorPathId(actor),
                EquippedSkillCount = skills.Count(skill => skill != null && skill.IsEquipped),
                EquippedSkillLimit = GetEquippedSkillLimit(actor),
                CanEditSkills = CanEditSkills(),
                Trinkets = BuildEquipmentSlotItems(actor.GetTrinketInventory(), "trinket", actor),
                CombatItems = BuildEquipmentSlotItems(actor.GetCombatSkillInventory(), "combat", actor),
                Skills = skills,
            };
        }

        private static IList<HeroLoadoutItemPayload> BuildPlayerLoadoutItems(bool includeRestItems)
        {
            List<HeroLoadoutItemPayload> items = new List<HeroLoadoutItemPayload>();
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.PlayerInventory == null)
                {
                    return items;
                }

                ItemInventory inventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
                int slotCount = inventory.GetNumberOfTotalSlots();
                for (int i = 0; i < slotCount; i++)
                {
                    IReadOnlyItemInstance item = inventory.GetItemOrDefault(i);
                    if (!ItemUtils.IsValid(item))
                    {
                        continue;
                    }

                    ItemDefinition definition = item.GetItemDefinition();
                    string itemKind = GetLoadoutItemKind(definition, includeRestItems);
                    if (itemKind == null)
                    {
                        continue;
                    }

                    items.Add(BuildItemPayload(itemKind, i, item, false, IsEquipmentKind(itemKind)));
                }
            }
            catch
            {
            }

            return items;
        }

        private static IList<HeroLoadoutItemPayload> BuildEquipmentSlotItems(
            ItemInventory inventory,
            string itemKind,
            ActorInstance actor)
        {
            List<HeroLoadoutItemPayload> items = new List<HeroLoadoutItemPayload>();
            if (inventory == null)
            {
                return items;
            }

            try
            {
                int slotCount = inventory.GetNumberOfTotalSlots();
                for (int i = 0; i < slotCount; i++)
                {
                    IReadOnlyItemInstance item = inventory.GetItemOrDefault(i);
                    bool canEquip = false;
                    if (ItemUtils.IsValid(item))
                    {
                        string message;
                        canEquip = CanEquipItem(actor, inventory, itemKind, item.GetItemDefinition(), out message);
                    }

                    items.Add(BuildItemPayload(itemKind, i, item, !ItemUtils.IsValid(item), canEquip));
                }
            }
            catch
            {
            }

            return items;
        }

        private static HeroLoadoutItemPayload BuildItemPayload(
            string itemKind,
            int inventoryIndex,
            IReadOnlyItemInstance item,
            bool isEmpty,
            bool canEquip)
        {
            if (!ItemUtils.IsValid(item))
            {
                return new HeroLoadoutItemPayload(
                    itemKind,
                    inventoryIndex,
                    null,
                    "[empty]",
                    null,
                    0,
                    true,
                    canEquip);
            }

            ItemDefinition definition = item.GetItemDefinition();
            bool isRestItem = IsRestItemDefinition(definition);
            return new HeroLoadoutItemPayload(
                itemKind,
                inventoryIndex,
                definition.m_id,
                GetItemDisplayName(definition),
                definition.m_type == null ? null : definition.m_type.GetName(),
                item.GetQty(),
                isEmpty,
                canEquip,
                isRestItem,
                isRestItem ? definition.GetNumberOfTargets() : 0,
                isRestItem ? definition.GetNumberOfSelectableRestItemTargets() : 0,
                isRestItem && definition.m_isRandomTarget,
                GetItemDescription(definition));
        }

        private static IList<HeroLoadoutSkillPayload> BuildSkillPayloads(ActorInstance actor)
        {
            List<HeroLoadoutSkillPayload> skills = new List<HeroLoadoutSkillPayload>();
            if (actor == null)
            {
                return skills;
            }

            HashSet<string> unlocked = new HashSet<string>(SafeGetUnlockedCharacterSheetSkillIds(actor));
            HashSet<string> equipped = new HashSet<string>(SafeGetEquippedCharacterSheetSkillIds(actor));
            HashSet<string> upgraded = new HashSet<string>(SafeGetUpgradedSkillIds(actor));
            HashSet<string> allSkillIds = new HashSet<string>(unlocked);
            foreach (string skillId in SafeGetLockedSkillIds(actor))
            {
                allSkillIds.Add(skillId);
            }

            int equipLimit = GetEquippedSkillLimit(actor);
            int equippedCount = equipped.Count;
            bool canEdit = CanEditSkills();

            foreach (string skillId in allSkillIds.OrderBy(GetSkillDisplayName).ThenBy(skillId => skillId))
            {
                if (string.IsNullOrWhiteSpace(skillId))
                {
                    continue;
                }

                SkillInstance skill = actor.GetCombatSkillInstance(skillId);
                bool isUnlocked = unlocked.Contains(skillId);
                bool isEquipped = equipped.Contains(skillId);
                bool canEquip = canEdit &&
                    isUnlocked &&
                    !isEquipped &&
                    skill != null &&
                    skill.GetCanChangeEquip(actor.ActorGuid, true) &&
                    (equipLimit <= 0 || equippedCount < equipLimit);
                bool canUnequip = canEdit &&
                    isUnlocked &&
                    isEquipped &&
                    skill != null &&
                    skill.GetCanChangeEquip(actor.ActorGuid, false);
                string masteredSkillId;
                bool canMaster = CanMasterSkill(actor, skillId, isUnlocked, upgraded.Contains(skillId), out masteredSkillId);

                skills.Add(new HeroLoadoutSkillPayload(
                    skillId,
                    GetSkillDisplayName(skillId),
                    isEquipped,
                    isUnlocked,
                    upgraded.Contains(skillId),
                    skill != null && skill.m_IsAlwaysEquipped,
                    canEquip,
                    canUnequip,
                    canMaster,
                    masteredSkillId,
                    GetSkillDescription(skillId, actor)));
            }

            return skills;
        }

        private void HandleEventActorChangeSkillEquipped(EventActorChangeSkillEquipped evt)
        {
            if (evt == null)
            {
                return;
            }

            MarkSnapshotDirty();
            HostLog.Write("[hero-loadout] skill changed actor=" + evt.m_actorGuid +
                ", skill=" + (evt.m_skillId ?? "[none]") +
                ", equipped=" + evt.m_isEquipped +
                ", refreshStats=" + evt.m_refreshStats + ".");
        }

        private void HandleEventSkillUpgradeCommitted(EventSkillUpgradeCommitted evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleEventInventoryItemQtyChange(EventInventoryItemQtyChange evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleEventInventorySlotsChanged(EventInventorySlotsChanged evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleEventInventoryItemPurchased(EventInventoryItemPurchased evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleEventInventoryItemConsumed(EventInventoryItemConsumed evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleEventLootItemReceived(EventLootItemReceived evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleEventItemDiscarded(EventItemDiscarded evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleHeroSelectChanged(EventHeroSelectRosterChanged evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleHeroSelectChanged(EventHeroSelectRosterActorsSwapped evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleHeroSelectChanged(EventRosterConfirmParty evt)
        {
            MarkSnapshotDirty();
        }

        private void HandleHeroSelectChanged(EventHeroSelectPathChange evt)
        {
            MarkSnapshotDirty();
        }

        private static bool CanMasterSkill(
            ActorInstance actor,
            string skillId,
            bool isUnlocked,
            bool isUpgraded,
            out string masteredSkillId)
        {
            masteredSkillId = null;
            if (actor == null ||
                string.IsNullOrWhiteSpace(skillId) ||
                !isUnlocked ||
                isUpgraded ||
                !CanMasterSkills())
            {
                return false;
            }

            SkillInstance skill = actor.GetCombatSkillInstance(skillId);
            if (skill == null || !skill.GetIsUnlocked() || skill.GetIsUpgraded())
            {
                return false;
            }

            string resolveMessage;
            if (!TryResolveMasterySkillId(actor, skillId, out masteredSkillId, out resolveMessage))
            {
                return false;
            }

            UnlockDefinition unlock = SkillUtils.GetUnlockFromSkillId(masteredSkillId);
            if (unlock == null)
            {
                masteredSkillId = null;
                return false;
            }

            if (unlock.CostDefinition != null && !CostCalculation.CanAffordCost(unlock.CostDefinition))
            {
                masteredSkillId = null;
                return false;
            }

            return true;
        }

        private static bool TryResolveMasterySkillId(
            ActorInstance actor,
            string currentSkillId,
            out string masterySkillId,
            out string message)
        {
            masterySkillId = null;
            message = string.Empty;
            if (actor == null)
            {
                message = "actor is missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(currentSkillId))
            {
                message = "missing skill id";
                return false;
            }

            SkillInstance currentSkill = actor.GetCombatSkillInstance(currentSkillId);
            if (currentSkill == null)
            {
                message = "skill " + currentSkillId + " is not on actor " + DescribeActor(actor);
                return false;
            }

            if (currentSkill.GetIsUpgraded())
            {
                message = "skill " + currentSkillId + " is already mastered";
                return false;
            }

            List<string> candidates = new List<string>();
            try
            {
                IReadOnlyList<UnlockDefinition> unlocks = LibraryUnlock.GetUnlocksFromRequirementId(currentSkillId);
                if (unlocks != null)
                {
                    foreach (UnlockDefinition unlock in unlocks)
                    {
                        if (unlock == null || string.IsNullOrWhiteSpace(unlock.m_Id))
                        {
                            continue;
                        }

                        ActorDataSkill skillData = TryGetSkillData(unlock.m_Id);
                        if (skillData == null)
                        {
                            continue;
                        }

                        UnlockDefinition skillUnlock = SkillUtils.GetUnlockFromSkillId(skillData.Id);
                        if (skillUnlock == null ||
                            skillUnlock.RequirementIds == null ||
                            !skillUnlock.RequirementIds.Contains(currentSkillId))
                        {
                            continue;
                        }

                        candidates.Add(skillData.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                message = "failed to resolve mastery unlock for " + currentSkillId + ": " + ex.Message;
                return false;
            }

            masterySkillId = candidates
                .Distinct()
                .OrderBy(candidate => string.Equals(candidate, currentSkillId + "_u", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(candidate => candidate.EndsWith("_u", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(candidate => candidate)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(masterySkillId))
            {
                message = "skill " + currentSkillId + " has no safe mastery upgrade unlock";
                return false;
            }

            return true;
        }

        private static bool CanEditSkills()
        {
            try
            {
                return GameModeMgr.CurrentMode != GameModeType.COMBAT;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeItemKind(string itemKind)
        {
            if (string.IsNullOrWhiteSpace(itemKind))
            {
                return null;
            }

            string value = itemKind.Trim().ToLowerInvariant();
            if (string.Equals(value, "trinket", StringComparison.Ordinal) ||
                string.Equals(value, "trinkets", StringComparison.Ordinal))
            {
                return "trinket";
            }

            if (string.Equals(value, "combat", StringComparison.Ordinal) ||
                string.Equals(value, "combat_item", StringComparison.Ordinal) ||
                string.Equals(value, "combat_items", StringComparison.Ordinal))
            {
                return "combat";
            }

            if (string.Equals(value, "rest", StringComparison.Ordinal) ||
                string.Equals(value, "inn", StringComparison.Ordinal) ||
                string.Equals(value, "rest_item", StringComparison.Ordinal) ||
                string.Equals(value, "rest_items", StringComparison.Ordinal))
            {
                return "rest";
            }

            return null;
        }

        private static bool IsEquipmentKind(string itemKind)
        {
            return string.Equals(itemKind, "trinket", StringComparison.Ordinal) ||
                string.Equals(itemKind, "combat", StringComparison.Ordinal);
        }

        private static string GetLoadoutItemKind(ItemDefinition itemDefinition, bool includeRestItems)
        {
            string equipmentKind = GetEquipmentKind(itemDefinition);
            if (equipmentKind != null)
            {
                return equipmentKind;
            }

            return includeRestItems && IsRestItemDefinition(itemDefinition) ? "rest" : null;
        }

        private static string GetEquipmentKind(ItemDefinition itemDefinition)
        {
            if (itemDefinition == null)
            {
                return null;
            }

            if (itemDefinition.m_type == ItemType.TRINKET)
            {
                return "trinket";
            }

            if (itemDefinition.m_type == ItemType.COMBAT)
            {
                return "combat";
            }

            return null;
        }

        private static bool IsRestItemDefinition(ItemDefinition itemDefinition)
        {
            return itemDefinition != null &&
                itemDefinition.m_type == ItemType.REST &&
                itemDefinition.IsUsedInInn();
        }

        private static ItemInventory GetActorEquipmentInventory(ActorInstance actor, string itemKind)
        {
            if (actor == null)
            {
                return null;
            }

            if (string.Equals(itemKind, "trinket", StringComparison.Ordinal))
            {
                return actor.GetTrinketInventory();
            }

            if (string.Equals(itemKind, "combat", StringComparison.Ordinal))
            {
                return actor.GetCombatSkillInventory();
            }

            return null;
        }

        private static bool CanEquipItem(
            ActorInstance actor,
            ItemInventory targetInventory,
            string itemKind,
            ItemDefinition itemDefinition,
            out string message)
        {
            message = string.Empty;
            if (actor == null)
            {
                message = "actor is missing";
                return false;
            }

            if (targetInventory == null)
            {
                message = "target inventory is missing";
                return false;
            }

            if (itemDefinition == null)
            {
                message = "item definition is missing";
                return false;
            }

            string actualKind = GetEquipmentKind(itemDefinition);
            if (!string.Equals(actualKind, itemKind, StringComparison.Ordinal))
            {
                message = "item " + itemDefinition.m_id + " is " + (actualKind ?? "[none]") +
                    ", not " + itemKind;
                return false;
            }

            if (!targetInventory.GetIsItemEquipType(itemDefinition))
            {
                message = "item " + itemDefinition.m_id + " does not fit " + itemKind + " inventory";
                return false;
            }

            if (!actor.GetIsItemConditionsMet(itemDefinition))
            {
                message = "actor " + DescribeActor(actor) +
                    " does not meet item conditions for " + itemDefinition.m_id;
                return false;
            }

            if (string.Equals(itemKind, "trinket", StringComparison.Ordinal) &&
                targetInventory.GetItemQty(itemDefinition) > 0)
            {
                message = "actor " + DescribeActor(actor) +
                    " already has trinket " + itemDefinition.m_id;
                return false;
            }

            if (string.Equals(itemKind, "combat", StringComparison.Ordinal) &&
                itemDefinition.GetActorDataSkill() == null)
            {
                message = "combat item " + itemDefinition.m_id + " has no actor skill data";
                return false;
            }

            return true;
        }

        private static bool TryResolveRestItemTargets(
            ItemDefinition itemDefinition,
            HeroLoadoutRequestPayload request,
            out List<ActorInstance> targets,
            out string message)
        {
            targets = new List<ActorInstance>();
            message = string.Empty;

            List<ActorInstance> partyActors = GetPartyActors();
            if (partyActors.Count == 0)
            {
                message = "party actors are not available";
                return false;
            }

            int selectableTargetCount = itemDefinition.GetNumberOfSelectableRestItemTargets();
            List<string> requestedGuids = new List<string>();
            if (request.TargetActorGuids != null)
            {
                requestedGuids.AddRange(request.TargetActorGuids.Where(guid => !string.IsNullOrWhiteSpace(guid)));
            }

            if (selectableTargetCount >= 4)
            {
                return TryResolvePartyRestItemTargets(
                    itemDefinition,
                    requestedGuids,
                    partyActors,
                    out targets,
                    out message);
            }

            if (requestedGuids.Count == 0 && !string.IsNullOrWhiteSpace(request.ActorGuid))
            {
                requestedGuids.Add(request.ActorGuid);
            }

            HashSet<uint> partyActorGuids = new HashSet<uint>(partyActors.Select(actor => actor.ActorGuid));
            HashSet<uint> seen = new HashSet<uint>();
            foreach (string guidText in requestedGuids)
            {
                uint actorGuid;
                if (!TryParseActorGuid(guidText, out actorGuid))
                {
                    message = "invalid rest item target actor guid: " + (guidText ?? "[none]");
                    return false;
                }

                if (!seen.Add(actorGuid))
                {
                    continue;
                }

                if (!partyActorGuids.Contains(actorGuid))
                {
                    message = "rest item target actor " + actorGuid + " is not in the current party";
                    return false;
                }

                ActorInstance targetActor;
                if (!TryResolveActor(actorGuid, out targetActor))
                {
                    message = "rest item target actor " + actorGuid + " was not found";
                    return false;
                }

                string targetMessage;
                if (!CanUseRestItemOnActor(targetActor, itemDefinition, true, out targetMessage))
                {
                    message = targetMessage;
                    return false;
                }

                targets.Add(targetActor);
            }

            int requiredSelectableCount = selectableTargetCount >= 4
                ? partyActors.Count
                : selectableTargetCount;
            if (requiredSelectableCount > 0 && targets.Count != requiredSelectableCount)
            {
                message = "inn item " + itemDefinition.m_id +
                    " requires " + requiredSelectableCount +
                    " selected target(s), got " + targets.Count;
                return false;
            }

            if (itemDefinition.m_isRandomTarget)
            {
                int finalTargetCount = itemDefinition.GetNumberOfTargets();
                if (finalTargetCount <= 0 || targets.Count < finalTargetCount)
                {
                    message = "inn item " + itemDefinition.m_id +
                        " has invalid random target count: " + finalTargetCount +
                        " from " + targets.Count + " selectable target(s)";
                    return false;
                }

                targets.Randomize(RandomIdentifier.INN);
                if (targets.Count > finalTargetCount)
                {
                    targets.RemoveRange(finalTargetCount, targets.Count - finalTargetCount);
                }
            }

            return true;
        }

        private static bool TryResolvePartyRestItemTargets(
            ItemDefinition itemDefinition,
            IList<string> requestedGuids,
            IList<ActorInstance> partyActors,
            out List<ActorInstance> targets,
            out string message)
        {
            targets = new List<ActorInstance>();
            message = string.Empty;

            HashSet<uint> requestedActorGuids = null;
            if (requestedGuids != null && requestedGuids.Count > 0)
            {
                requestedActorGuids = new HashSet<uint>();
                HashSet<uint> partyActorGuids = new HashSet<uint>(partyActors.Select(actor => actor.ActorGuid));
                foreach (string guidText in requestedGuids)
                {
                    uint actorGuid;
                    if (!TryParseActorGuid(guidText, out actorGuid))
                    {
                        message = "invalid rest item target actor guid: " + (guidText ?? "[none]");
                        return false;
                    }

                    if (!partyActorGuids.Contains(actorGuid))
                    {
                        message = "rest item target actor " + actorGuid + " is not in the current party";
                        return false;
                    }

                    requestedActorGuids.Add(actorGuid);
                }
            }

            List<string> skippedTargets = new List<string>();
            foreach (ActorInstance partyActor in partyActors)
            {
                if (partyActor == null || partyActor.ActorGuid == 0U)
                {
                    continue;
                }

                if (requestedActorGuids != null && !requestedActorGuids.Contains(partyActor.ActorGuid))
                {
                    continue;
                }

                string targetMessage;
                if (!CanUseRestItemOnActor(partyActor, itemDefinition, false, out targetMessage))
                {
                    skippedTargets.Add(targetMessage);
                    continue;
                }

                targets.Add(partyActor);
            }

            if (skippedTargets.Count > 0)
            {
                HostLog.Write("[hero-rest-item-action] party item " + itemDefinition.m_id +
                    " skipped " + skippedTargets.Count +
                    " ineligible target(s): " + string.Join("; ", skippedTargets.ToArray()) + ".");
            }

            if (itemDefinition.m_isRandomTarget)
            {
                int finalTargetCount = itemDefinition.GetNumberOfTargets();
                if (finalTargetCount <= 0 || targets.Count < finalTargetCount)
                {
                    message = "inn item " + itemDefinition.m_id +
                        " needs " + finalTargetCount +
                        " eligible random target(s), got " + targets.Count;
                    return false;
                }

                targets.Randomize(RandomIdentifier.INN);
                if (targets.Count > finalTargetCount)
                {
                    targets.RemoveRange(finalTargetCount, targets.Count - finalTargetCount);
                }
            }
            else if (targets.Count == 0)
            {
                message = "inn party item " + itemDefinition.m_id +
                    " has no eligible party targets";
                return false;
            }

            return true;
        }

        private static bool CanUseRestItemOnActor(
            ActorInstance actor,
            ItemDefinition itemDefinition,
            bool includeActOutBlock,
            out string message)
        {
            message = string.Empty;
            if (actor == null)
            {
                message = "rest item target actor is missing";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(itemDefinition.m_forcedTargetActorDataId) &&
                !string.Equals(itemDefinition.m_forcedTargetActorDataId, actor.ActorDataId, StringComparison.Ordinal))
            {
                message = "inn item " + itemDefinition.m_id +
                    " must target actor data " + itemDefinition.m_forcedTargetActorDataId +
                    ", not " + DescribeActor(actor);
                return false;
            }

            ItemBlockDefinition itemBlockDefinition;
            if (actor.GetIsItemBlocked(itemDefinition, out itemBlockDefinition))
            {
                message = "actor " + DescribeActor(actor) +
                    " blocks inn item " + itemDefinition.m_id +
                    (itemBlockDefinition == null ? string.Empty : " via " + itemBlockDefinition.m_Id);
                return false;
            }

            if (includeActOutBlock &&
                actor.GetRestItemBlockedSourceActOut(itemDefinition.m_tags) != null)
            {
                message = "actor " + DescribeActor(actor) +
                    " currently blocks inn item " + itemDefinition.m_id + " via act out";
                return false;
            }

            if (!actor.GetIsItemConditionsMet(itemDefinition))
            {
                message = "actor " + DescribeActor(actor) +
                    " does not meet inn item conditions for " + itemDefinition.m_id;
                return false;
            }

            if (!actor.GetIsItemUnderUseLimit(itemDefinition))
            {
                message = "actor " + DescribeActor(actor) +
                    " is over the inn item use limit for " + itemDefinition.m_id;
                return false;
            }

            return true;
        }

        private static void ApplyRestItem(ItemDefinition itemDefinition, List<ActorInstance> targets)
        {
            foreach (ActorInstance target in targets)
            {
                ApplyRestItemActorEffects(target, itemDefinition);
            }

            if (targets.Count > 1)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    ActorInstance performerActor = targets[i];
                    for (int j = i + 1; j < targets.Count; j++)
                    {
                        ActorInstance targetActor = targets[j];
                        ApplyRestItemEffectList(
                            performerActor,
                            targetActor,
                            itemDefinition,
                            itemDefinition.GetCombinationEffects(),
                            itemDefinition.GetCombinationApplyLimitEffects(),
                            itemDefinition.m_combinationEffectApplyLimit,
                            null);
                    }
                }
            }

            foreach (ActorInstance partyActor in GetPartyActors())
            {
                ApplyRestItemEffectList(
                    partyActor,
                    partyActor,
                    itemDefinition,
                    itemDefinition.GetPartyEffects(),
                    itemDefinition.GetPartyApplyLimitEffects(),
                    itemDefinition.m_partyEffectApplyLimit,
                    null);
            }

            foreach (ActorInstance target in targets)
            {
                target.RecordItemUseHistory(itemDefinition);
                EventRestItemApplied.Trigger(target.ActorGuid, itemDefinition.ToString());
            }
        }

        private static void ApplyRestItemActorEffects(ActorInstance actor, ItemDefinition itemDefinition)
        {
            ActorDataEffects dataContainerSum = actor.ActorData.GetDataContainerSum<ActorDataEffects>(null, null);
            ApplyRestItemEffectList(
                actor,
                actor,
                itemDefinition,
                itemDefinition.GetEffects(),
                itemDefinition.GetApplyLimitEffects(),
                itemDefinition.m_effectApplyLimit,
                dataContainerSum);
        }

        private static void ApplyRestItemEffectList(
            ActorInstance performerActor,
            ActorInstance targetActor,
            ItemDefinition itemDefinition,
            IReadOnlyList<SourceDefinition<EffectDefinition>> sourceEffects,
            IReadOnlyList<SourceDefinition<EffectDefinition>> applyLimitSourceEffects,
            int applyLimit,
            ActorDataEffects actorDataEffects)
        {
            int sourceEffectCount = sourceEffects == null ? 0 : sourceEffects.Count;
            int applyLimitEffectCount = applyLimitSourceEffects == null ? 0 : applyLimitSourceEffects.Count;
            if (sourceEffectCount <= 0 && applyLimitEffectCount <= 0 && actorDataEffects == null)
            {
                return;
            }

            float chance = 1f;
            chance += targetActor.GetClampedStatValue(ActorStatType.REST_ITEM_EFFECT_CHANCE_MODIFIER, itemDefinition.m_tags);
            if (!RandomContainer.CheckValue(RandomIdentifier.INN, chance))
            {
                return;
            }

            ConditionCalculation.Input conditionInput = new ConditionCalculation.Input(
                performerActor,
                targetActor,
                SourceType.REST_ITEM,
                itemDefinition.m_id);
            if (sourceEffectCount > 0)
            {
                EffectApply.Apply(new AppliedEffects.Input<SourceDefinition<EffectDefinition>>(
                    sourceEffects,
                    performerActor,
                    targetActor,
                    conditionInput));
            }

            if (applyLimitEffectCount > 0)
            {
                EffectApply.Apply(new AppliedEffects.Input<SourceDefinition<EffectDefinition>>(
                    applyLimitSourceEffects,
                    applyLimit,
                    performerActor,
                    targetActor,
                    conditionInput));
            }

            if (actorDataEffects != null)
            {
                EffectApply.Apply(
                    new AppliedEffects.Input<SourceDefinition<EffectDefinition>>(
                        performerActor,
                        targetActor,
                        conditionInput),
                    actorDataEffects,
                    ActorDataEffectType.REST_ITEM);
            }
        }

        private static List<ActorInstance> GetPartyActors()
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance() ||
                    Singleton<GameTypeMgr>.Instance.RosterManager == null)
                {
                    return new List<ActorInstance>();
                }

                return Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors()
                    .Where(actor => actor != null && actor.ActorGuid != 0U)
                    .OrderBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .ToList();
            }
            catch
            {
                return new List<ActorInstance>();
            }
        }

        private static bool IsInventoryIndexValid(ItemInventory inventory, int inventoryIndex)
        {
            return inventory != null &&
                inventoryIndex >= 0 &&
                inventoryIndex < inventory.GetNumberOfTotalSlots();
        }

        private static bool CanMasterSkills()
        {
            try
            {
                if (GameModeMgr.CurrentMode != GameModeType.INN)
                {
                    return false;
                }

                if (GetHeroUpgradePoints() <= 0)
                {
                    return false;
                }

                InnBhv innBhv = Singleton<InnBhv>.Instance;
                if (innBhv == null || innBhv.GetInnInstance() == null)
                {
                    return false;
                }

                return innBhv.GetInnInstance().GetIsInnFeatureEnabled(InnFeatureType.TRAINER);
            }
            catch
            {
                return false;
            }
        }

        private static int GetHeroUpgradePoints()
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance())
                {
                    return 0;
                }

                return (int)Singleton<GameTypeMgr>.Instance.RunValues.GetValue(RunValueType.HERO_UPGRADE_POINTS);
            }
            catch
            {
                return 0;
            }
        }

        private static int GetEquippedSkillLimit(ActorInstance actor)
        {
            try
            {
                return actor == null || actor.ActorDataClass == null
                    ? 0
                    : actor.ActorDataClass.m_EquippedCombatSkillLimit;
            }
            catch
            {
                return 0;
            }
        }

        private static IReadOnlyList<string> SafeGetUnlockedCharacterSheetSkillIds(ActorInstance actor)
        {
            try
            {
                return actor == null ? Array.Empty<string>() : actor.GetUnlockedCharacterSheetCombatSkillIds();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> SafeGetEquippedCharacterSheetSkillIds(ActorInstance actor)
        {
            try
            {
                return actor == null ? Array.Empty<string>() : actor.GetEquippedCharacterSheetCombatSkillIds();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> SafeGetLockedSkillIds(ActorInstance actor)
        {
            try
            {
                return actor == null ? Array.Empty<string>() : actor.GetLockedCombatSkillIds();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> SafeGetUpgradedSkillIds(ActorInstance actor)
        {
            try
            {
                return actor == null ? Array.Empty<string>() : actor.GetUpgradedCombatSkillIds();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string GetSkillDisplayName(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return "[unknown]";
            }

            try
            {
                if (SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false))
                {
                    ActorDataSkill skill = SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetLibraryElement(skillId);
                    if (skill != null)
                    {
                        string displayName = SkillDescription.GetNameText(skill);
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            return displayName;
                        }
                    }
                }
            }
            catch
            {
            }

            return skillId;
        }

        private static string GetItemDisplayName(ItemDefinition itemDefinition)
        {
            if (itemDefinition == null)
            {
                return "[empty]";
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
                    false,
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

        private static string GetSkillDescription(string skillId, ActorInstance actor)
        {
            try
            {
                ActorDataSkill skill = TryGetSkillData(skillId);
                return skill == null ? string.Empty : BuildSkillDescription(skill, actor);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildSkillDescription(ActorDataSkill skill, ActorInstance actor)
        {
            if (skill == null)
            {
                return string.Empty;
            }

            List<string> blocks = new List<string>();
            AddSkillDescriptionBlock(blocks, SkillDescription.GetTopBarString(skill, actor));

            try
            {
                uint actorGuid = actor == null ? 0U : actor.ActorGuid;
                foreach (string result in SkillDescription.GetResultStringsByTargetType(skill, false, actorGuid) ?? new List<string>())
                {
                    AddSkillDescriptionBlock(blocks, result);
                }
            }
            catch
            {
            }

            return string.Join("\n", blocks.ToArray());
        }

        private static void AddSkillDescriptionBlock(List<string> blocks, string text)
        {
            if (blocks == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string trimmed = text.Trim();
            if (blocks.Any(existing => string.Equals(existing, trimmed, StringComparison.Ordinal)))
            {
                return;
            }

            blocks.Add(trimmed);
        }

        private static ActorDataSkill TryGetSkillData(string skillId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(skillId) ||
                    !SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false))
                {
                    return null;
                }

                return SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetLibraryElement(skillId);
            }
            catch
            {
                return null;
            }
        }

        private static void TryCacheSkillUpgradeAnalytics(ActorInstance actor, string skillId)
        {
            try
            {
                if (actor != null && SingletonMonoBehaviour<AnalyticsBhv>.HasInstance(false))
                {
                    SingletonMonoBehaviour<AnalyticsBhv>.Instance.CacheSkillUpgrade(actor.ActorDataId, skillId);
                }
            }
            catch
            {
            }
        }

        private static string SafeGetCurrentGameModeName()
        {
            try
            {
                return GameModeMgr.CurrentMode == null ? null : GameModeMgr.CurrentMode.GetName();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryResolveActor(uint actorGuid, out ActorInstance actor)
        {
            actor = null;
            if (actorGuid == 0U || !SingletonMonoBehaviour<Library<uint, ActorInstance>>.HasInstance(false))
            {
                return false;
            }

            actor = SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(actorGuid);
            return actor != null;
        }

        private static HeroSelectBhv FindHeroSelect()
        {
            HeroSelectBhv[] heroSelectScreens = UnityObject.FindObjectsOfType<HeroSelectBhv>(true);
            return heroSelectScreens.FirstOrDefault(IsActive) ?? heroSelectScreens.FirstOrDefault();
        }

        private static bool IsActive(HeroSelectBhv heroSelect)
        {
            return heroSelect != null &&
                heroSelect.gameObject != null &&
                heroSelect.gameObject.activeInHierarchy;
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
            where T : class
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance) as T;
        }

        private static string SafeGetActorDataId(ActorInstance actor)
        {
            try
            {
                return actor == null ? null : actor.ActorDataId;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetActorName(ActorInstance actor)
        {
            try
            {
                return actor == null ? null : actor.ActorName;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetActorPathId(ActorInstance actor)
        {
            try
            {
                return actor == null || actor.ActorDataPath == null ? null : actor.ActorDataPath.Id;
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeActor(ActorInstance actor)
        {
            if (actor == null)
            {
                return "[null]";
            }

            return actor.ActorGuid + "/" + (SafeGetActorDataId(actor) ?? "[unknown]");
        }

        private static bool TryParseActorGuid(string text, out uint actorGuid)
        {
            actorGuid = 0U;
            return !string.IsNullOrWhiteSpace(text) && uint.TryParse(text.Trim(), out actorGuid) && actorGuid != 0U;
        }

        private static string ComputeHeroLoadoutDigest(HeroLoadoutSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "none";
            }

            string actorPart = string.Join(
                "|",
                (snapshot.Actors ?? Array.Empty<HeroLoadoutActorPayload>())
                    .Select(actor =>
                    {
                        string skillPart = string.Join(
                            ",",
                            (actor.Skills ?? Array.Empty<HeroLoadoutSkillPayload>())
                                .Select(skill => (skill.SkillId ?? string.Empty) +
                                    ":" + (skill.IsEquipped ? "1" : "0") +
                                    ":" + (skill.IsUnlocked ? "1" : "0") +
                                    ":" + (skill.IsUpgraded ? "1" : "0"))
                                .ToArray());
                        string trinketPart = FormatItemDigest(actor.Trinkets);
                        string combatItemPart = FormatItemDigest(actor.CombatItems);
                        return actor.HeroSlot +
                            ":" + (actor.ActorGuid ?? string.Empty) +
                            ":" + (actor.PathId ?? string.Empty) +
                            ":" + actor.EquippedSkillCount +
                            "/" + actor.EquippedSkillLimit +
                            ":trinkets=" + trinketPart +
                            ":combatItems=" + combatItemPart +
                            ":" + skillPart;
                    })
                    .ToArray());
            string inventoryPart = FormatItemDigest(snapshot.InventoryItems);
            return (snapshot.IsActive ? "active" : "inactive") +
                ";" + (snapshot.Scope ?? string.Empty) +
                ";" + (snapshot.CurrentGameMode ?? string.Empty) +
                ";inventory=" + inventoryPart +
                ";" + actorPart;
        }

        private static string FormatItemDigest(IList<HeroLoadoutItemPayload> items)
        {
            if (items == null || items.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ",",
                items
                    .Where(item => item != null)
                    .OrderBy(item => item.ItemKind)
                    .ThenBy(item => item.InventoryIndex)
                    .Select(item => (item.ItemKind ?? string.Empty) +
                        ":" + item.InventoryIndex +
                        ":" + (item.ItemId ?? string.Empty) +
                        ":" + item.Quantity)
                    .ToArray());
        }

        private sealed class HeroLoadoutActorSource
        {
            public HeroLoadoutActorSource(ActorInstance actor, int heroSlot, int teamPosition, string scope)
            {
                Actor = actor;
                HeroSlot = heroSlot;
                TeamPosition = teamPosition;
                Scope = scope;
            }

            public ActorInstance Actor { get; }

            public int HeroSlot { get; }

            public int TeamPosition { get; }

            public string Scope { get; }
        }
    }
}
