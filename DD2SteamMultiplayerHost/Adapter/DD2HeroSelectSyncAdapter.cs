using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Campaign;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Library;
using Assets.Code.Roster.Events;
using Assets.Code.UI.Events;
using Assets.Code.UI.HeroSelect;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2HeroSelectSyncAdapter : IHeroSelectAdapter, IDisposable
    {
        private const float CachedSnapshotForcedRefreshInterval = 10f;

        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private bool _snapshotDirty = true;
        private float _nextForcedSnapshotRefreshTime;
        private string _cachedModeName;
        private HeroSelectSnapshotPayload _cachedSnapshot;

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
                    HostLog.Write("[hero-select] EventManager is not ready; hero select sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventHeroSelectRosterChanged>(HandleEventHeroSelectRosterChanged, false, 0);
            EventManager.AddListener<EventRosterConfirmParty>(HandleEventRosterConfirmParty, false, 0);
            EventManager.AddListener<EventHeroSelectPathChange>(HandleEventHeroSelectPathChange, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[hero-select] Hero select listeners registered; dirty-cache enabled.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventHeroSelectRosterChanged>(HandleEventHeroSelectRosterChanged);
            EventManager.RemoveListener<EventRosterConfirmParty>(HandleEventRosterConfirmParty);
            EventManager.RemoveListener<EventHeroSelectPathChange>(HandleEventHeroSelectPathChange);
            _listenersRegistered = false;
        }

        public bool TryGetHeroSelectSnapshot(out HeroSelectSnapshotPayload snapshot)
        {
            snapshot = null;

            try
            {
                string currentMode = SafeGetCurrentGameModeName();
                if (CanUseCachedSnapshot(currentMode))
                {
                    snapshot = _cachedSnapshot;
                    return true;
                }

                if (GameModeMgr.CurrentMode != GameModeType.HERO_SELECT)
                {
                    snapshot = CreateInactiveHeroSelectSnapshot();
                    CacheSnapshot(snapshot, currentMode);
                    MarkSnapshotDirty();
                    return true;
                }

                HeroSelectBhv heroSelect = FindHeroSelect();
                if (!IsActive(heroSelect))
                {
                    snapshot = CreateInactiveHeroSelectSnapshot();
                    CacheSnapshot(snapshot, currentMode);
                    MarkSnapshotDirty();
                    return true;
                }

                List<uint> selectedActorGuids = GetPrivateField<List<uint>>(heroSelect, "m_SelectedActorGuids") ?? new List<uint>();
                List<uint> preferredActorGuids = GetPrivateField<List<uint>>(heroSelect, "m_kingdomPreferredActorGuids") ?? new List<uint>();
                Dictionary<uint, HeroSelectActorUIBhv> heroSelectActorBhvs =
                    GetPrivateField<Dictionary<uint, HeroSelectActorUIBhv>>(heroSelect, "m_heroSelectActorBhvs") ??
                    new Dictionary<uint, HeroSelectActorUIBhv>();

                snapshot = new HeroSelectSnapshotPayload
                {
                    IsActive = true,
                    RosterConfirmed = GetPrivateField(heroSelect, "m_RosterConfirmed", false),
                    SelectedActorGuid = SafeGetSpawnedActorGuid(heroSelect),
                    SelectedPathId = GetPrivateField<string>(heroSelect, "m_SelectedPathId"),
                    Slots = BuildSlotPayloads(selectedActorGuids, heroSelectActorBhvs),
                    Heroes = BuildHeroPayloads(heroSelectActorBhvs, selectedActorGuids, preferredActorGuids),
                };
                snapshot.CanConfirm = !snapshot.RosterConfirmed &&
                    snapshot.Slots.Count > 0 &&
                    snapshot.Slots.All(slot => !string.IsNullOrWhiteSpace(slot.ActorGuid) && slot.ActorGuid != "0");
                snapshot.Digest = ComputeHeroSelectDigest(snapshot);
                CacheSnapshot(snapshot, currentMode);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[hero-select] Failed to collect hero select snapshot: " + ex.Message + ".");
                return false;
            }
        }

        public bool TryExecuteHeroSelectRequest(
            HeroSelectRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty hero select request";
                return false;
            }

            HeroSelectBhv heroSelect = FindHeroSelect();
            if (!IsActive(heroSelect))
            {
                message = "hero select is not active on host";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "confirm", StringComparison.Ordinal))
            {
                bool accepted = TryConfirmRoster(heroSelect, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            List<uint> selectedActorGuids = GetPrivateField<List<uint>>(heroSelect, "m_SelectedActorGuids") ?? new List<uint>();
            if (request.SlotIndex < 0 || request.SlotIndex >= selectedActorGuids.Count)
            {
                message = "hero select slot index " + request.SlotIndex + " is not valid";
                return false;
            }

            if (GetPrivateField(heroSelect, "m_RosterConfirmed", false))
            {
                message = "roster is already confirmed";
                return false;
            }

            if (string.Equals(action, "assign", StringComparison.Ordinal))
            {
                bool accepted = TryAssignHero(heroSelect, selectedActorGuids, request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (string.Equals(action, "clear_slot", StringComparison.Ordinal))
            {
                bool accepted = TryClearSlot(heroSelect, selectedActorGuids, request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            if (string.Equals(action, "set_path", StringComparison.Ordinal))
            {
                bool accepted = TrySetActorPath(heroSelect, selectedActorGuids, request, senderSteamId, senderName, out message);
                if (accepted)
                {
                    MarkSnapshotDirty();
                }

                return accepted;
            }

            message = "unsupported hero select action: " + request.Action;
            return false;
        }

        private bool CanUseCachedSnapshot(string currentMode)
        {
            return _cachedSnapshot != null &&
                _cachedSnapshot.IsActive &&
                !_snapshotDirty &&
                Time.unscaledTime < _nextForcedSnapshotRefreshTime &&
                string.Equals(_cachedModeName ?? string.Empty, currentMode ?? string.Empty, StringComparison.Ordinal);
        }

        private void CacheSnapshot(HeroSelectSnapshotPayload snapshot, string currentMode)
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

        private static bool TryAssignHero(
            HeroSelectBhv heroSelect,
            List<uint> selectedActorGuids,
            HeroSelectRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (!TryParseActorGuid(request.ActorGuid, out uint actorGuid))
            {
                message = "invalid actor guid: " + (request.ActorGuid ?? "[none]");
                return false;
            }

            Dictionary<uint, HeroSelectActorUIBhv> heroSelectActorBhvs =
                GetPrivateField<Dictionary<uint, HeroSelectActorUIBhv>>(heroSelect, "m_heroSelectActorBhvs") ??
                new Dictionary<uint, HeroSelectActorUIBhv>();
            if (!heroSelectActorBhvs.ContainsKey(actorGuid))
            {
                message = "actor guid " + actorGuid + " is not available in hero select";
                return false;
            }

            int existingSlot = selectedActorGuids.FindIndex(guid => guid == actorGuid);
            if (existingSlot == request.SlotIndex)
            {
                message = "actor " + actorGuid + " is already assigned to slot index " + request.SlotIndex;
                return true;
            }

            if (existingSlot >= 0)
            {
                message = "actor " + actorGuid + " is already assigned to slot index " + existingSlot;
                return false;
            }

            try
            {
                HostLog.Write("[hero-select-action] " + senderName + "/" + senderSteamId +
                    " assigns actor " + actorGuid +
                    " to slotIndex=" + request.SlotIndex + ".");
                heroSelect.SetHeroSelection(actorGuid, request.SlotIndex, true);
                SelectHeroForHostUi(heroSelect, actorGuid, heroSelectActorBhvs);
                message = "actor " + actorGuid + " assigned to slot index " + request.SlotIndex + " on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "hero assign failed: " + ex.Message;
                HostLog.Write("[hero-select-action] " + message + ".");
                return false;
            }
        }

        private static bool TryClearSlot(
            HeroSelectBhv heroSelect,
            List<uint> selectedActorGuids,
            HeroSelectRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            uint existingActorGuid = selectedActorGuids[request.SlotIndex];
            if (existingActorGuid == 0U)
            {
                message = "slot index " + request.SlotIndex + " is already empty";
                return true;
            }

            try
            {
                HostLog.Write("[hero-select-action] " + senderName + "/" + senderSteamId +
                    " clears slotIndex=" + request.SlotIndex +
                    " actor=" + existingActorGuid + ".");
                heroSelect.ClearRosterIndex(request.SlotIndex);
                SelectFallbackHeroForHostUi(heroSelect, selectedActorGuids, existingActorGuid);
                message = "slot index " + request.SlotIndex + " cleared on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "hero clear failed: " + ex.Message;
                HostLog.Write("[hero-select-action] " + message + ".");
                return false;
            }
        }

        private static bool TrySetActorPath(
            HeroSelectBhv heroSelect,
            List<uint> selectedActorGuids,
            HeroSelectRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(request.PathId))
            {
                message = "path id is empty";
                return false;
            }

            uint slotActorGuid = selectedActorGuids[request.SlotIndex];
            if (slotActorGuid == 0U)
            {
                message = "slot index " + request.SlotIndex + " has no actor";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.ActorGuid) &&
                (!uint.TryParse(request.ActorGuid.Trim(), out uint requestActorGuid) || requestActorGuid != slotActorGuid))
            {
                message = "actor " + (request.ActorGuid ?? "[none]") + " does not match slot actor " + slotActorGuid;
                return false;
            }

            ActorInstance actor = SafeGetActorInstance(slotActorGuid, (HeroSelectActorUIBhv)null);
            if (actor == null)
            {
                message = "slot actor " + slotActorGuid + " was not found";
                return false;
            }

            ActorDataPath path = GetAvailablePath(actor, request.PathId.Trim());
            if (path == null)
            {
                message = "path " + request.PathId + " is not available for actor " + DescribeActor(actor);
                return false;
            }

            if (actor.ActorDataPath == path)
            {
                message = "actor " + slotActorGuid + " already uses path " + path.Id;
                return true;
            }

            try
            {
                HostLog.Write("[hero-select-action] " + senderName + "/" + senderSteamId +
                    " sets path " + path.Id +
                    " for actor " + DescribeActor(actor) +
                    " at slotIndex=" + request.SlotIndex + ".");
                actor.SetActorPath(path, false);
                SelectHeroForHostUi(heroSelect, slotActorGuid);
                uint spawnedActorGuid = 0U;
                try
                {
                    spawnedActorGuid = heroSelect.GetSpawnedActorGuid();
                }
                catch
                {
                }

                if (spawnedActorGuid == slotActorGuid)
                {
                    SetPrivateField(heroSelect, "m_SelectedPathId", path.Id);
                }

                EventHeroSelectPathChange.Trigger(path.Id);
                message = "path " + path.Id + " set for actor " + slotActorGuid + " on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "set path failed: " + ex.Message;
                HostLog.Write("[hero-select-action] " + message + ".");
                return false;
            }
        }

        private static void SelectHeroForHostUi(
            HeroSelectBhv heroSelect,
            uint actorGuid,
            IDictionary<uint, HeroSelectActorUIBhv> heroSelectActorBhvs = null)
        {
            if (heroSelect == null || actorGuid == 0U)
            {
                return;
            }

            try
            {
                uint currentActorGuid = heroSelect.GetSpawnedActorGuid();
                if (currentActorGuid == actorGuid)
                {
                    return;
                }

                HeroSelectActorUIBhv heroSelectActorBhv = null;
                if (heroSelectActorBhvs != null)
                {
                    heroSelectActorBhvs.TryGetValue(actorGuid, out heroSelectActorBhv);
                }

                if (heroSelectActorBhv == null)
                {
                    heroSelectActorBhv = heroSelect.GetHeroSelectObject(actorGuid);
                }

                if (heroSelectActorBhv != null)
                {
                    heroSelect.OnActorSelected(heroSelectActorBhv, false);
                    SyncSelectedPathCache(heroSelect, actorGuid);
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[hero-select-action] Host UI selection refresh failed for actor " + actorGuid + ": " + ex.Message + ".");
            }
        }

        private static void SyncSelectedPathCache(HeroSelectBhv heroSelect, uint actorGuid)
        {
            try
            {
                ActorInstance actor = SafeGetActorInstance(actorGuid, (HeroSelectActorUIBhv)null);
                string pathId = SafeGetActorPathId(actor);
                if (!string.IsNullOrWhiteSpace(pathId))
                {
                    SetPrivateField(heroSelect, "m_SelectedPathId", pathId);
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("[hero-select-action] Selected path cache refresh failed for actor " + actorGuid + ": " + ex.Message + ".");
            }
        }

        private static void SelectFallbackHeroForHostUi(HeroSelectBhv heroSelect, IList<uint> selectedActorGuids, uint clearedActorGuid)
        {
            if (heroSelect == null || clearedActorGuid == 0U)
            {
                return;
            }

            try
            {
                if (heroSelect.GetSpawnedActorGuid() != clearedActorGuid)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            uint fallbackActorGuid = selectedActorGuids == null
                ? 0U
                : selectedActorGuids.FirstOrDefault(actorGuid => actorGuid != 0U && actorGuid != clearedActorGuid);
            if (fallbackActorGuid != 0U)
            {
                SelectHeroForHostUi(heroSelect, fallbackActorGuid);
            }
        }

        private static ActorDataPath GetAvailablePath(ActorInstance actor, string pathId)
        {
            if (actor == null || string.IsNullOrWhiteSpace(pathId))
            {
                return null;
            }

            try
            {
                return ActorPathCalculation.GetActorDataPaths(actor, true)
                    .FirstOrDefault(path => path != null && string.Equals(path.Id, pathId, StringComparison.Ordinal));
            }
            catch
            {
                return null;
            }
        }

        private static bool TryConfirmRoster(
            HeroSelectBhv heroSelect,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            List<uint> selectedActorGuids = GetPrivateField<List<uint>>(heroSelect, "m_SelectedActorGuids") ?? new List<uint>();
            if (selectedActorGuids.Count == 0 || selectedActorGuids.Contains(0U))
            {
                message = "party is incomplete";
                return false;
            }

            if (GetPrivateField(heroSelect, "m_RosterConfirmed", false))
            {
                message = "roster is already confirmed";
                return false;
            }

            try
            {
                HostLog.Write("[hero-select-action] " + senderName + "/" + senderSteamId + " confirms party.");
                heroSelect.ConfirmRosterSelection();
                message = "confirm roster invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "confirm roster failed: " + ex.Message;
                HostLog.Write("[hero-select-action] " + message + ".");
                return false;
            }
        }

        private static IList<HeroSelectSlotPayload> BuildSlotPayloads(
            IList<uint> selectedActorGuids,
            IDictionary<uint, HeroSelectActorUIBhv> heroSelectActorBhvs)
        {
            List<HeroSelectSlotPayload> slots = new List<HeroSelectSlotPayload>();
            if (selectedActorGuids == null)
            {
                return slots;
            }

            for (int i = 0; i < selectedActorGuids.Count; i++)
            {
                uint actorGuid = selectedActorGuids[i];
                ActorInstance actor = SafeGetActorInstance(actorGuid, heroSelectActorBhvs);
                slots.Add(new HeroSelectSlotPayload(
                    i,
                    i + 1,
                    actorGuid == 0U ? null : actorGuid.ToString(),
                    SafeGetActorDataId(actor),
                    SafeGetActorName(actor),
                    SafeGetActorPathId(actor)));
            }

            return slots;
        }

        private static IList<HeroSelectHeroPayload> BuildHeroPayloads(
            IDictionary<uint, HeroSelectActorUIBhv> heroSelectActorBhvs,
            IList<uint> selectedActorGuids,
            IList<uint> preferredActorGuids)
        {
            if (heroSelectActorBhvs == null || heroSelectActorBhvs.Count == 0)
            {
                return new List<HeroSelectHeroPayload>();
            }

            HashSet<uint> selected = new HashSet<uint>(selectedActorGuids ?? Array.Empty<uint>());
            HashSet<uint> preferred = new HashSet<uint>(preferredActorGuids ?? Array.Empty<uint>());
            List<HeroSelectHeroPayload> heroes = new List<HeroSelectHeroPayload>();
            foreach (KeyValuePair<uint, HeroSelectActorUIBhv> pair in heroSelectActorBhvs)
            {
                if (pair.Key == 0U || pair.Value == null)
                {
                    continue;
                }

                ActorInstance actor = SafeGetActorInstance(pair.Key, pair.Value);
                heroes.Add(new HeroSelectHeroPayload(
                    pair.Key.ToString(),
                    SafeGetActorDataId(actor),
                    SafeGetActorName(actor),
                    SafeGetActorPathId(actor),
                    selected.Contains(pair.Key),
                    preferred.Contains(pair.Key),
                    BuildPathPayloads(actor)));
            }

            return heroes
                .OrderBy(hero => hero.ActorDataId)
                .ThenBy(hero => hero.ActorGuid)
                .ToList();
        }

        private static IList<HeroSelectPathPayload> BuildPathPayloads(ActorInstance actor)
        {
            List<HeroSelectPathPayload> paths = new List<HeroSelectPathPayload>();
            if (actor == null)
            {
                return paths;
            }

            try
            {
                string currentPathId = SafeGetActorPathId(actor);
                IReadOnlyList<ActorDataPath> actorPaths = ActorPathCalculation.GetActorDataPaths(actor, true);
                for (int i = 0; i < actorPaths.Count; i++)
                {
                    ActorDataPath path = actorPaths[i];
                    if (path == null)
                    {
                        continue;
                    }

                    paths.Add(new HeroSelectPathPayload(
                        path.Id,
                        SafeGetActorPathName(path, actor),
                        string.Equals(currentPathId, path.Id, StringComparison.Ordinal)));
                }
            }
            catch
            {
            }

            return paths
                .GroupBy(path => path.PathId)
                .Select(group => group.First())
                .OrderBy(path => path.DisplayName)
                .ThenBy(path => path.PathId)
                .ToList();
        }

        private void HandleEventHeroSelectRosterChanged(EventHeroSelectRosterChanged evt)
        {
            if (evt == null)
            {
                return;
            }

            MarkSnapshotDirty();
            HostLog.Write("[hero-select] roster changed actor=" + evt.m_actorGuid +
                ", added=" + evt.m_addedToRoster + ".");
        }

        private void HandleEventRosterConfirmParty(EventRosterConfirmParty evt)
        {
            if (evt == null || evt.m_PartyActorGuids == null)
            {
                return;
            }

            MarkSnapshotDirty();
            HostLog.Write("[hero-select] roster confirmed: " + string.Join(",", evt.m_PartyActorGuids.Select(guid => guid.ToString()).ToArray()) + ".");
        }

        private void HandleEventHeroSelectPathChange(EventHeroSelectPathChange evt)
        {
            if (evt == null)
            {
                return;
            }

            MarkSnapshotDirty();
            HostLog.Write("[hero-select] selected path changed=" + (evt.m_pathId ?? "[none]") + ".");
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

        private static ActorInstance SafeGetActorInstance(uint actorGuid, IDictionary<uint, HeroSelectActorUIBhv> heroSelectActorBhvs)
        {
            if (actorGuid == 0U)
            {
                return null;
            }

            HeroSelectActorUIBhv heroSelectActorBhv;
            if (heroSelectActorBhvs != null && heroSelectActorBhvs.TryGetValue(actorGuid, out heroSelectActorBhv))
            {
                return SafeGetActorInstance(actorGuid, heroSelectActorBhv);
            }

            return SafeGetActorInstance(actorGuid, (HeroSelectActorUIBhv)null);
        }

        private static ActorInstance SafeGetActorInstance(uint actorGuid, HeroSelectActorUIBhv heroSelectActorBhv)
        {
            if (actorGuid == 0U)
            {
                return null;
            }

            try
            {
                if (heroSelectActorBhv != null && heroSelectActorBhv.ActorGuid == actorGuid)
                {
                    return heroSelectActorBhv.ActorInstance;
                }
            }
            catch
            {
            }

            try
            {
                return SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(actorGuid);
            }
            catch
            {
                return null;
            }
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

        private static string SafeGetActorPathName(ActorDataPath path, ActorInstance actor)
        {
            try
            {
                if (path == null)
                {
                    return null;
                }

                string gender = actor == null || actor.ActorDataClass == null ? string.Empty : actor.ActorDataClass.m_LocalizationGender;
                return ActorPathDescription.GetNameString(path, gender, false);
            }
            catch
            {
                return path == null ? null : path.Id;
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

        private static string SafeGetSpawnedActorGuid(HeroSelectBhv heroSelect)
        {
            try
            {
                uint actorGuid = heroSelect == null ? 0U : heroSelect.GetSpawnedActorGuid();
                return actorGuid == 0U ? null : actorGuid.ToString();
            }
            catch
            {
                return null;
            }
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

        private static bool TryParseActorGuid(string text, out uint actorGuid)
        {
            actorGuid = 0U;
            return !string.IsNullOrWhiteSpace(text) && uint.TryParse(text.Trim(), out actorGuid) && actorGuid != 0U;
        }

        private static HeroSelectSnapshotPayload CreateInactiveHeroSelectSnapshot()
        {
            return new HeroSelectSnapshotPayload
            {
                IsActive = false,
                RosterConfirmed = false,
                CanConfirm = false,
                Digest = "hero-select-inactive",
            };
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            object value = GetPrivateFieldObject(instance, fieldName);
            if (value is T typed)
            {
                return typed;
            }

            return default(T);
        }

        private static T GetPrivateField<T>(object instance, string fieldName, T defaultValue)
        {
            object value = GetPrivateFieldObject(instance, fieldName);
            if (value is T typed)
            {
                return typed;
            }

            return defaultValue;
        }

        private static object GetPrivateFieldObject(object instance, string fieldName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static string ComputeHeroSelectDigest(HeroSelectSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.RosterConfirmed + ":" +
                snapshot.CanConfirm + ":" +
                snapshot.SelectedActorGuid + ":" +
                snapshot.SelectedPathId + ":" +
                string.Join("|", (snapshot.Slots ?? Array.Empty<HeroSelectSlotPayload>())
                    .OrderBy(slot => slot.SlotIndex)
                    .Select(slot =>
                        slot.SlotIndex + "," +
                        slot.ActorGuid + "," +
                        slot.ActorDataId + "," +
                        slot.PathId)
                    .ToArray()) + ":" +
                string.Join("|", (snapshot.Heroes ?? Array.Empty<HeroSelectHeroPayload>())
                    .OrderBy(hero => hero.ActorGuid)
                    .Select(hero =>
                        hero.ActorGuid + "," +
                        hero.ActorDataId + "," +
                        hero.PathId + "," +
                        hero.IsSelected + "," +
                        hero.IsKingdomPreferred + "," +
                        string.Join(";", (hero.Paths ?? Array.Empty<HeroSelectPathPayload>())
                            .OrderBy(path => path.PathId)
                            .Select(path => path.PathId + "=" + path.IsCurrent)
                            .ToArray()))
                    .ToArray());
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
    }
}
