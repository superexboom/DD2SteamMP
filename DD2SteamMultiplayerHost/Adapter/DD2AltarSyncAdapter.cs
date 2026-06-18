using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.AltarOfHope;
using Assets.Code.Cost;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Locale;
using Assets.Code.Profile;
using Assets.Code.Source;
using Assets.Code.UI;
using Assets.Code.UI.Screens;
using Assets.Code.Unlock;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2AltarSyncAdapter : IAltarActionAdapter
    {
        public bool TryGetAltarSnapshot(out AltarSnapshotPayload snapshot)
        {
            try
            {
                if (!IsAltarMode())
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                snapshot = BuildSnapshot(FindActiveAltarUi());
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[altar] Failed to collect altar snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        public bool TryExecuteAltarAction(
            AltarActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty altar action";
                return false;
            }

            string action = NormalizeAction(request.Action);
            if (string.Equals(action, "embark", StringComparison.Ordinal))
            {
                return TryExecuteEmbark(senderSteamId, senderName, out message);
            }

            if (string.Equals(action, "spend_track", StringComparison.Ordinal))
            {
                return TryExecuteTrackSpend(request, senderSteamId, senderName, out message);
            }

            if (string.Equals(action, "purchase_reward", StringComparison.Ordinal))
            {
                return TryExecuteRewardPurchase(request, senderSteamId, senderName, out message);
            }

            message = "unsupported altar action: " + request.Action;
            return false;
        }

        private static bool TryExecuteEmbark(
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            AltarOfHopeUiBhv ui = FindActiveAltarUi();
            AltarSnapshotPayload snapshot = BuildSnapshot(ui);
            if (!snapshot.CanEmbark)
            {
                message = "altar cannot embark: " + (snapshot.BlockReason ?? "[blocked]");
                return false;
            }

            try
            {
                ui.OnEmbark();
                HostLog.Write("[altar-action] " + senderName + "/" + senderSteamId +
                    " continued from altar of hope.");
                message = "altar embark invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "altar embark failed: " + ex.Message;
                HostLog.Write("[altar-action] " + message + ".");
                return false;
            }
        }

        private static bool TryExecuteTrackSpend(
            AltarActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            AltarProgressTrackBaseBhv track;
            AltarTrackPayload payload;
            if (!TryFindRuntimeTrack(request, out track, out payload))
            {
                message = "altar track is not active: " +
                    request.TrackIndex + "/" + (request.TrackId ?? "[none]");
                return false;
            }

            if (!payload.CanPurchase)
            {
                message = "altar track cannot spend candles: " +
                    (payload.TrackId ?? "[none]") +
                    " spent=" + payload.SpentCandles +
                    "/" + payload.TotalCandles;
                return false;
            }

            float spendValue = request.SpendValue <= 0f ? 1f : request.SpendValue;
            if (payload.SpendToNext > 0f)
            {
                spendValue = Math.Min(spendValue, payload.SpendToNext);
            }

            if (spendValue < 1f)
            {
                message = "altar track spend value must be at least 1 candle";
                return false;
            }

            int candles = SafeGetCandleCount();
            if (spendValue > candles)
            {
                message = "not enough candles for altar track spend: need " + spendValue + ", have " + candles;
                return false;
            }

            try
            {
                track.OnTrackSpendAttempt(spendValue);
                HostLog.Write("[altar-action] " + senderName + "/" + senderSteamId +
                    " spent candles on track=" + (payload.TrackId ?? "[none]") +
                    " amount=" + spendValue + ".");
                message = "altar track spend invoked on host: " +
                    (payload.TrackId ?? "[none]") +
                    " amount=" + spendValue;
                return true;
            }
            catch (Exception ex)
            {
                message = "altar track spend failed: " + ex.Message;
                HostLog.Write("[altar-action] " + message + ".");
                return false;
            }
        }

        private static bool TryExecuteRewardPurchase(
            AltarActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            AltarItemRewardButtonBhv button;
            AltarRewardButtonPayload payload;
            if (!TryFindRuntimeRewardButton(request, out button, out payload))
            {
                message = "altar reward button is not active: #" +
                    request.RewardButtonIndex +
                    "/" + (request.UnlockTableId ?? "[none]") +
                    "/" + (request.ItemType ?? "[none]");
                return false;
            }

            if (!payload.CanPurchase)
            {
                message = "altar reward cannot be purchased: #" +
                    payload.ButtonIndex +
                    " " + (payload.DisplayName ?? payload.CurrentUnlockTableId ?? payload.ItemType ?? "[reward]") +
                    " mode=" + (payload.PurchaseMode ?? "[none]") +
                    " cost=" + payload.CostText;
                return false;
            }

            try
            {
                MethodInfo purchase = typeof(AltarItemRewardButtonBhv).GetMethod(
                    "Purchase",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (purchase == null)
                {
                    message = "altar reward Purchase() method was not found";
                    return false;
                }

                purchase.Invoke(button, null);
                HostLog.Write("[altar-action] " + senderName + "/" + senderSteamId +
                    " purchased altar reward #" + payload.ButtonIndex +
                    " table=" + (payload.CurrentUnlockTableId ?? "[none]") +
                    " itemType=" + (payload.ItemType ?? "[none]") +
                    " mode=" + (payload.PurchaseMode ?? "[none]") + ".");
                message = "altar reward purchase invoked on host: #" +
                    payload.ButtonIndex +
                    " " + (payload.DisplayName ?? payload.CurrentUnlockTableId ?? payload.ItemType ?? "[reward]");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                message = "altar reward purchase failed: " + inner.Message;
                HostLog.Write("[altar-action] " + message + ".");
                return false;
            }
            catch (Exception ex)
            {
                message = "altar reward purchase failed: " + ex.Message;
                HostLog.Write("[altar-action] " + message + ".");
                return false;
            }
        }

        private static AltarSnapshotPayload BuildSnapshot(AltarOfHopeUiBhv ui)
        {
            bool isActive = IsAltarMode();
            bool hasUi = ui != null;
            AltarOfHopeBhv altar = SingletonMonoBehaviour<AltarOfHopeBhv>.HasInstance(false)
                ? SingletonMonoBehaviour<AltarOfHopeBhv>.Instance
                : null;
            bool hasActiveSystem = altar != null && altar.HasActiveSystem;
            bool isIntro = altar != null && altar.IsIntro;
            int candleCount = SafeGetCandleCount();
            bool isExiting = hasUi && GetPrivateField<bool>(ui, "m_exiting");
            bool isChanging = SafeIsGameModeChanging();

            AltarSnapshotPayload snapshot = new AltarSnapshotPayload
            {
                IsActive = isActive,
                CurrentGameMode = SafeGetCurrentModeName(),
                ActiveSubscreen = isActive ? FindActiveAltarSubscreenName() : "[none]",
                HasUi = hasUi,
                HasActiveSystem = hasActiveSystem,
                IsIntro = isIntro,
                CandleCount = candleCount,
                IsExiting = isExiting,
                IsGameModeChanging = isChanging,
                Tracks = isActive ? BuildTrackPayloads() : new List<AltarTrackPayload>(),
                RewardButtons = isActive ? BuildRewardButtonPayloads() : new List<AltarRewardButtonPayload>(),
            };
            snapshot.BlockReason = BuildBlockReason(snapshot);
            snapshot.CanEmbark = snapshot.IsActive &&
                snapshot.HasUi &&
                snapshot.HasActiveSystem &&
                !snapshot.IsExiting &&
                !snapshot.IsGameModeChanging &&
                !(snapshot.IsIntro && snapshot.CandleCount > 0);
            snapshot.Digest = ComputeAltarDigest(snapshot);
            return snapshot;
        }

        private static List<AltarTrackPayload> BuildTrackPayloads()
        {
            List<AltarTrackPayload> result = new List<AltarTrackPayload>();
            AltarProgressTrackBaseBhv[] tracks = UnityObject.FindObjectsOfType<AltarProgressTrackBaseBhv>(true)
                .Where(track => track != null &&
                    track.gameObject != null &&
                    track.gameObject.activeInHierarchy)
                .OrderBy(track => GetHierarchyPath(track.transform))
                .ToArray();

            for (int i = 0; i < tracks.Length; i++)
            {
                AltarTrackPayload payload;
                if (TryBuildTrackPayload(tracks[i], i, out payload))
                {
                    result.Add(payload);
                }
            }

            return result;
        }

        private static bool TryBuildTrackPayload(
            AltarProgressTrackBaseBhv track,
            int index,
            out AltarTrackPayload payload)
        {
            payload = null;
            UnlockTrackDefinition definition = GetPrivateField<UnlockTrackDefinition>(track, "m_unlockTrackDefinition");
            if (definition == null || string.IsNullOrWhiteSpace(definition.m_Id))
            {
                return false;
            }

            float spent = SafeGetTrackSpent(definition.m_Id);
            float total = GetTotalCandleCost(definition);
            float nextMilestone = GetNextMilestoneCandleCost(definition, spent);
            float spendToNext = nextMilestone > spent ? nextMilestone - spent : 0f;
            bool canProgress = GetPrivateField<bool>(track, "m_canProgress");
            bool canPurchase = GetPrivateField<bool>(track, "m_canPurchase");
            bool holding = GetPrivateField<bool>(track, "m_holding") ||
                GetPrivateField<bool>(track, "m_holdPurchasing") ||
                GetPrivateField<bool>(track, "m_holdPurchased");
            Button button = track.GetButton();
            bool buttonInteractable = button != null && button.interactable;
            bool isComplete = total > 0f && spent >= total;
            payload = new AltarTrackPayload(
                index,
                definition.m_Id,
                GetTrackDisplayName(definition.m_Id, track),
                GetTrackKind(track),
                spent,
                total,
                nextMilestone,
                spendToNext,
                canProgress,
                canProgress && canPurchase && buttonInteractable && !holding && !isComplete,
                buttonInteractable,
                holding,
                isComplete);
            return true;
        }

        private static List<AltarRewardButtonPayload> BuildRewardButtonPayloads()
        {
            List<AltarRewardButtonPayload> result = new List<AltarRewardButtonPayload>();
            AltarItemRewardButtonBhv[] buttons = UnityObject.FindObjectsOfType<AltarItemRewardButtonBhv>(true)
                .Where(button => button != null &&
                    button.gameObject != null &&
                    button.gameObject.activeInHierarchy)
                .OrderBy(button => GetHierarchyPath(button.transform))
                .ToArray();

            for (int i = 0; i < buttons.Length; i++)
            {
                AltarRewardButtonPayload payload;
                if (TryBuildRewardButtonPayload(buttons[i], i, out payload))
                {
                    result.Add(payload);
                }
            }

            return result;
        }

        private static bool TryBuildRewardButtonPayload(
            AltarItemRewardButtonBhv button,
            int index,
            out AltarRewardButtonPayload payload)
        {
            payload = null;
            if (button == null)
            {
                return false;
            }

            IItemRewardScreen parent = GetPrivateField<IItemRewardScreen>(button, "m_parentRewardInterface");
            if (parent == null)
            {
                return false;
            }

            string currentUnlockTableId = GetCurrentUnlockTableId(button);
            string unlockTrackId = GetPrivateField<string>(button, "m_unlockTrackID");
            bool isLocked = GetPrivateField<bool>(button, "m_isLocked");
            ItemType itemType = GetRewardItemType(button);
            string itemTypeName = itemType == null ? null : itemType.GetName();
            bool isComplete = SafeAreAllRewardsUnlocked(button);
            RewardPurchaseState purchaseState = BuildRewardPurchaseState(
                parent,
                currentUnlockTableId,
                itemType,
                isLocked);

            payload = new AltarRewardButtonPayload(
                index,
                parent.GetType().Name,
                unlockTrackId,
                currentUnlockTableId,
                itemTypeName,
                GetRewardDisplayName(unlockTrackId, currentUnlockTableId, itemTypeName),
                button.NumUnlocked,
                button.TotalItemCount,
                purchaseState.Cost,
                purchaseState.CostText,
                isLocked,
                isComplete,
                purchaseState.IsRepeatable,
                purchaseState.CanPurchase,
                purchaseState.CanAfford,
                purchaseState.Mode);
            return true;
        }

        private static string BuildBlockReason(AltarSnapshotPayload snapshot)
        {
            if (snapshot == null || !snapshot.IsActive)
            {
                return "altar of hope is not active";
            }

            if (!snapshot.HasUi)
            {
                return "altar UI is not available";
            }

            if (!snapshot.HasActiveSystem)
            {
                return "altar system is not active";
            }

            if (snapshot.IsExiting)
            {
                return "altar is already exiting";
            }

            if (snapshot.IsGameModeChanging)
            {
                return "game mode is changing";
            }

            if (snapshot.IsIntro && snapshot.CandleCount > 0)
            {
                return "altar intro requires spending all candles before leaving";
            }

            return string.Empty;
        }

        private static AltarOfHopeUiBhv FindActiveAltarUi()
        {
            return UnityObject.FindObjectsOfType<AltarOfHopeUiBhv>(true)
                .Where(ui => ui != null && ui.gameObject != null && ui.gameObject.activeInHierarchy)
                .OrderByDescending(ui => ui.enabled)
                .FirstOrDefault();
        }

        private static bool IsAltarMode()
        {
            try
            {
                return GameModeMgr.CurrentMode == GameModeType.ALTAR_OF_HOPE;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindRuntimeTrack(
            AltarActionRequestPayload request,
            out AltarProgressTrackBaseBhv track,
            out AltarTrackPayload payload)
        {
            track = null;
            payload = null;
            AltarProgressTrackBaseBhv[] tracks = UnityObject.FindObjectsOfType<AltarProgressTrackBaseBhv>(true)
                .Where(candidate => candidate != null &&
                    candidate.gameObject != null &&
                    candidate.gameObject.activeInHierarchy)
                .OrderBy(candidate => GetHierarchyPath(candidate.transform))
                .ToArray();

            for (int i = 0; i < tracks.Length; i++)
            {
                AltarTrackPayload candidatePayload;
                if (!TryBuildTrackPayload(tracks[i], i, out candidatePayload))
                {
                    continue;
                }

                bool indexMatches = request.TrackIndex < 0 || candidatePayload.TrackIndex == request.TrackIndex;
                bool idMatches = string.IsNullOrWhiteSpace(request.TrackId) ||
                    string.Equals(candidatePayload.TrackId ?? string.Empty, request.TrackId, StringComparison.Ordinal);
                if (indexMatches && idMatches)
                {
                    track = tracks[i];
                    payload = candidatePayload;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindRuntimeRewardButton(
            AltarActionRequestPayload request,
            out AltarItemRewardButtonBhv button,
            out AltarRewardButtonPayload payload)
        {
            button = null;
            payload = null;
            AltarItemRewardButtonBhv[] buttons = UnityObject.FindObjectsOfType<AltarItemRewardButtonBhv>(true)
                .Where(candidate => candidate != null &&
                    candidate.gameObject != null &&
                    candidate.gameObject.activeInHierarchy)
                .OrderBy(candidate => GetHierarchyPath(candidate.transform))
                .ToArray();

            for (int i = 0; i < buttons.Length; i++)
            {
                AltarRewardButtonPayload candidatePayload;
                if (!TryBuildRewardButtonPayload(buttons[i], i, out candidatePayload))
                {
                    continue;
                }

                bool indexMatches = request.RewardButtonIndex < 0 || candidatePayload.ButtonIndex == request.RewardButtonIndex;
                bool screenMatches = string.IsNullOrWhiteSpace(request.ScreenKind) ||
                    string.Equals(candidatePayload.ScreenKind ?? string.Empty, request.ScreenKind, StringComparison.Ordinal);
                bool tableMatches = string.IsNullOrWhiteSpace(request.UnlockTableId) ||
                    string.Equals(candidatePayload.CurrentUnlockTableId ?? string.Empty, request.UnlockTableId, StringComparison.Ordinal);
                bool trackMatches = string.IsNullOrWhiteSpace(request.UnlockTrackId) ||
                    string.Equals(candidatePayload.UnlockTrackId ?? string.Empty, request.UnlockTrackId, StringComparison.Ordinal);
                bool itemTypeMatches = string.IsNullOrWhiteSpace(request.ItemType) ||
                    string.Equals(candidatePayload.ItemType ?? string.Empty, request.ItemType, StringComparison.Ordinal);
                if (indexMatches && screenMatches && tableMatches && trackMatches && itemTypeMatches)
                {
                    button = buttons[i];
                    payload = candidatePayload;
                    return true;
                }
            }

            return false;
        }

        private static bool SafeIsGameModeChanging()
        {
            try
            {
                return Singleton<GameModeMgr>.HasInstance() &&
                    Singleton<GameModeMgr>.Instance.IsChangingState();
            }
            catch
            {
                return true;
            }
        }

        private static string FindActiveAltarSubscreenName()
        {
            if (HasActiveSubscreen<AltarClassSubScreenBhv>())
            {
                return nameof(AltarClassSubScreenBhv);
            }

            if (HasActiveSubscreen<AltarGeneralSubScreenBhv>())
            {
                return nameof(AltarGeneralSubScreenBhv);
            }

            if (HasActiveSubscreen<AltarItemSubScreenBhv>())
            {
                return nameof(AltarItemSubScreenBhv);
            }

            if (HasActiveSubscreen<AltarCosmeticSubScreenBhv>())
            {
                return nameof(AltarCosmeticSubScreenBhv);
            }

            if (HasActiveSubscreen<AltarMemorySubScreenBhv>())
            {
                return nameof(AltarMemorySubScreenBhv);
            }

            return "[none]";
        }

        private static bool HasActiveSubscreen<T>()
            where T : UnityEngine.Component
        {
            return UnityObject.FindObjectsOfType<T>(true)
                .Any(component => component != null &&
                    component.gameObject != null &&
                    component.gameObject.activeInHierarchy);
        }

        private static RewardPurchaseState BuildRewardPurchaseState(
            IItemRewardScreen parent,
            string currentUnlockTableId,
            ItemType itemType,
            bool isLocked)
        {
            RewardPurchaseState state = new RewardPurchaseState
            {
                Cost = 0,
                CostText = string.Empty,
                Mode = "complete",
                CanAfford = false,
                CanPurchase = false,
                IsRepeatable = false,
            };

            try
            {
                ProfileInstance profile = SingletonMonoBehaviour<ProfileBhv>.HasInstance(false)
                    ? SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile()
                    : null;
                if (profile == null || parent == null || string.IsNullOrWhiteSpace(currentUnlockTableId) || isLocked)
                {
                    return state;
                }

                CostDefinition cost = profile.GetUnlockTableCost(currentUnlockTableId);
                bool parentComplete = parent.AreAllRewardsUnlocked();
                if (cost == null && parentComplete && itemType != null)
                {
                    state.Mode = "repeatable";
                    state.IsRepeatable = true;
                    if (!IsRepeatableRewardItemType(itemType) ||
                        !SingletonMonoBehaviour<AltarOfHopeBhv>.HasInstance(false) ||
                        !SingletonMonoBehaviour<AltarOfHopeBhv>.Instance.GetHasValidRepeatableItems(itemType))
                    {
                        return state;
                    }

                    cost = SingletonMonoBehaviour<AltarOfHopeBhv>.Instance.GetRepeatableItemCost(itemType);
                    state.Cost = GetCostValue(cost);
                    state.CostText = FormatCandleCost(state.Cost);
                    state.CanAfford = cost != null && profile.CanAffordCandleCost(state.Cost);
                    state.CanPurchase = state.CanAfford && !parent.IsPresenting;
                    return state;
                }

                state.Mode = "unlock_roll";
                state.Cost = GetCostValue(cost);
                state.CostText = FormatCandleCost(state.Cost);
                state.CanAfford = cost != null && profile.CanAffordUnlockTableRoll(currentUnlockTableId, SourceType.RUN);
                state.CanPurchase = state.CanAfford &&
                    !parent.IsPresenting &&
                    profile.HasPossibleUnlockTableRoll(currentUnlockTableId);
                return state;
            }
            catch
            {
                return state;
            }
        }

        private static bool IsRepeatableRewardItemType(ItemType itemType)
        {
            return itemType == ItemType.COMBAT ||
                itemType == ItemType.REST ||
                itemType == ItemType.STAGE_COACH_UPGRADE ||
                itemType == ItemType.TRINKET;
        }

        private static string GetCurrentUnlockTableId(AltarItemRewardButtonBhv button)
        {
            try
            {
                string current = GetPrivateField<string>(button, "m_currentUnlockTableId");
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return current;
                }

                button.AreAllRewardsUnlocked();
                current = GetPrivateField<string>(button, "m_currentUnlockTableId");
                return current ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool SafeAreAllRewardsUnlocked(AltarItemRewardButtonBhv button)
        {
            try
            {
                return button != null && button.AreAllRewardsUnlocked();
            }
            catch
            {
                return false;
            }
        }

        private static ItemType GetRewardItemType(AltarItemRewardButtonBhv button)
        {
            try
            {
                SelectionItemType selection = GetPrivateField<SelectionItemType>(button, "m_itemType");
                return selection == null ? null : selection.GetSelection();
            }
            catch
            {
                return null;
            }
        }

        private static float SafeGetTrackSpent(string trackId)
        {
            try
            {
                return SingletonMonoBehaviour<ProfileBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile() != null
                    ? SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile()
                        .GetProfileValuesForUnlockTrack(trackId)
                        .GetValue(ProfileValueType.CANDLES)
                    : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static float GetTotalCandleCost(UnlockTrackDefinition definition)
        {
            if (definition == null || definition.Unlocks == null || definition.Unlocks.Count == 0)
            {
                return 0f;
            }

            return GetCandleCost(definition.GetTotalCosts(definition.Unlocks[definition.Unlocks.Count - 1].m_Id));
        }

        private static float GetNextMilestoneCandleCost(UnlockTrackDefinition definition, float spent)
        {
            if (definition == null || definition.Unlocks == null)
            {
                return 0f;
            }

            for (int i = 0; i < definition.Unlocks.Count; i++)
            {
                float cost = GetCandleCost(definition.GetTotalCosts(definition.Unlocks[i].m_Id));
                if (cost > spent)
                {
                    return cost;
                }
            }

            return GetTotalCandleCost(definition);
        }

        private static float GetCandleCost(IEnumerable<CostSum> costs)
        {
            if (costs == null)
            {
                return 0f;
            }

            foreach (CostSum cost in costs)
            {
                if (cost != null && cost.ProfileValueType == ProfileValueType.CANDLES)
                {
                    return cost.GetProfileValueValue(1f);
                }
            }

            return 0f;
        }

        private static int GetCostValue(CostDefinition cost)
        {
            try
            {
                return cost == null ? 0 : (int)cost.GetProfileValueValue(1f);
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatCandleCost(int cost)
        {
            if (cost <= 0)
            {
                return string.Empty;
            }

            try
            {
                if (Singleton<Localization>.HasInstance())
                {
                    return cost + Singleton<Localization>.Instance.GetString("candle_icon", true);
                }
            }
            catch
            {
            }

            return cost + " candles";
        }

        private static string GetTrackDisplayName(string trackId, AltarProgressTrackBaseBhv track)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                return "[track]";
            }

            try
            {
                string key = track is AltarGeneralObjectBhv
                    ? "altar_upgrade_" + trackId
                    : trackId;
                if (Singleton<Localization>.HasInstance())
                {
                    string localized = Singleton<Localization>.Instance.GetString(key, true);
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        return localized;
                    }
                }
            }
            catch
            {
            }

            return trackId;
        }

        private static string GetRewardDisplayName(string unlockTrackId, string unlockTableId, string itemType)
        {
            if (!string.IsNullOrWhiteSpace(unlockTrackId))
            {
                try
                {
                    if (Singleton<Localization>.HasInstance())
                    {
                        string localized = Singleton<Localization>.Instance.GetString(unlockTrackId, true);
                        if (!string.IsNullOrWhiteSpace(localized))
                        {
                            return localized;
                        }
                    }
                }
                catch
                {
                }

                return unlockTrackId;
            }

            if (!string.IsNullOrWhiteSpace(itemType))
            {
                return itemType;
            }

            return string.IsNullOrWhiteSpace(unlockTableId) ? "[reward]" : unlockTableId;
        }

        private static string GetTrackKind(AltarProgressTrackBaseBhv track)
        {
            if (track == null)
            {
                return "[track]";
            }

            if (track is AltarClassHeroBhv)
            {
                return "class";
            }

            if (track is AltarGeneralObjectBhv)
            {
                return "general";
            }

            return track.GetType().Name;
        }

        private static string GetHierarchyPath(UnityEngine.Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            List<string> parts = new List<string>();
            UnityEngine.Transform current = transform;
            while (current != null)
            {
                parts.Add(current.GetSiblingIndex().ToString("D4") + ":" + current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string SafeGetCurrentModeName()
        {
            try
            {
                return GameModeMgr.CurrentMode == null
                    ? "[none]"
                    : GameModeMgr.CurrentMode.GetName();
            }
            catch
            {
                return "[none]";
            }
        }

        private static int SafeGetCandleCount()
        {
            try
            {
                return SingletonMonoBehaviour<ProfileBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile() != null
                    ? (int)SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile().ProfileValues.GetValue(ProfileValueType.CANDLES)
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string NormalizeAction(string action)
        {
            string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
            if (string.Equals(normalized, "continue", StringComparison.Ordinal) ||
                string.Equals(normalized, "exit", StringComparison.Ordinal))
            {
                return "embark";
            }

            if (string.Equals(normalized, "track", StringComparison.Ordinal) ||
                string.Equals(normalized, "spend", StringComparison.Ordinal))
            {
                return "spend_track";
            }

            if (string.Equals(normalized, "reward", StringComparison.Ordinal) ||
                string.Equals(normalized, "purchase", StringComparison.Ordinal))
            {
                return "purchase_reward";
            }

            return normalized;
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return default(T);
            }

            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    object value = field.GetValue(target);
                    return value is T ? (T)value : default(T);
                }

                type = type.BaseType;
            }

            return default(T);
        }

        private static AltarSnapshotPayload CreateInactiveSnapshot()
        {
            AltarSnapshotPayload snapshot = new AltarSnapshotPayload
            {
                IsActive = false,
                CurrentGameMode = SafeGetCurrentModeName(),
                ActiveSubscreen = "[none]",
                HasUi = false,
                HasActiveSystem = false,
                IsIntro = false,
                CandleCount = SafeGetCandleCount(),
                IsExiting = false,
                IsGameModeChanging = SafeIsGameModeChanging(),
                CanEmbark = false,
                BlockReason = "altar of hope is not active",
                Tracks = new List<AltarTrackPayload>(),
                RewardButtons = new List<AltarRewardButtonPayload>(),
            };
            snapshot.Digest = ComputeAltarDigest(snapshot);
            return snapshot;
        }

        private static string ComputeAltarDigest(AltarSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.CurrentGameMode + ":" +
                snapshot.ActiveSubscreen + ":" +
                snapshot.HasUi + ":" +
                snapshot.HasActiveSystem + ":" +
                snapshot.IsIntro + ":" +
                snapshot.CandleCount + ":" +
                snapshot.IsExiting + ":" +
                snapshot.IsGameModeChanging + ":" +
                snapshot.CanEmbark + ":" +
                snapshot.BlockReason + ":" +
                string.Join("|", (snapshot.Tracks ?? Array.Empty<AltarTrackPayload>())
                    .Where(track => track != null)
                    .Select(track =>
                        track.TrackIndex + "," +
                        (track.TrackId ?? string.Empty) + "," +
                        track.SpentCandles + "," +
                        track.TotalCandles + "," +
                        track.NextMilestoneCandles + "," +
                        track.SpendToNext + "," +
                        track.CanPurchase + "," +
                        track.IsComplete)
                    .ToArray()) + ":" +
                string.Join("|", (snapshot.RewardButtons ?? Array.Empty<AltarRewardButtonPayload>())
                    .Where(button => button != null)
                    .Select(button =>
                        button.ButtonIndex + "," +
                        (button.ScreenKind ?? string.Empty) + "," +
                        (button.CurrentUnlockTableId ?? string.Empty) + "," +
                        (button.UnlockTrackId ?? string.Empty) + "," +
                        (button.ItemType ?? string.Empty) + "," +
                        button.Cost + "," +
                        button.NumUnlocked + "/" + button.TotalItemCount + "," +
                        button.CanPurchase + "," +
                        button.IsComplete + "," +
                        (button.PurchaseMode ?? string.Empty))
                    .ToArray());
            return ComputeStableDigest(raw);
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

        private sealed class RewardPurchaseState
        {
            public int Cost { get; set; }

            public string CostText { get; set; }

            public bool IsRepeatable { get; set; }

            public bool CanPurchase { get; set; }

            public bool CanAfford { get; set; }

            public string Mode { get; set; }
        }
    }
}
