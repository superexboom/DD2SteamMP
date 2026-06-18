using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Combat;
using Assets.Code.Combat.BattleConfiguration;
using Assets.Code.Game;
using Assets.Code.UI;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2LairDecisionSyncAdapter : ILairDecisionAdapter
    {
        public bool TryGetLairDecisionSnapshot(out LairDecisionSnapshotPayload snapshot)
        {
            try
            {
                if (!IsDungeonConfirmationDialogActive())
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                DungeonConfirmationDialogBhv dialog = FindActiveDialog();
                if (dialog == null)
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                UiScreenBhv screen = GetPrivateField<UiScreenBhv>(dialog, "m_screenBhv");
                Action confirm = GetPrivateField<Action>(dialog, "m_onConfirmCmd");
                Action decline = GetPrivateField<Action>(dialog, "m_onDeclineCmd");
                GameObject declineButton = GetPrivateField<GameObject>(dialog, "m_declineBtn");
                GameObject continueButton = GetPrivateField<GameObject>(dialog, "m_continueButton");
                List<GameObject> itemsAdded = GetPrivateField<List<GameObject>>(dialog, "m_itemsAdded") ?? new List<GameObject>();

                CombatScenarioData scenario = Singleton<GameTypeMgr>.HasInstance()
                    ? Singleton<GameTypeMgr>.Instance.CombatScenarioData
                    : null;
                IReadOnlyList<BattleConfigurationDefinition> battleConfigurations = scenario == null
                    ? null
                    : scenario.BattleConfigurations;
                int currentIndex = scenario == null ? -1 : scenario.CurrentBattleConfigurationIndex;
                int nextIndex = currentIndex >= 0 ? currentIndex + 1 : -1;
                BattleConfigurationDefinition currentConfig = scenario == null ? null : scenario.CurrentBattleConfiguration;
                BattleConfigurationDefinition nextConfig = battleConfigurations != null && nextIndex >= 0 && nextIndex < battleConfigurations.Count
                    ? battleConfigurations[nextIndex]
                    : null;

                snapshot = new LairDecisionSnapshotPayload
                {
                    IsActive = true,
                    CurrentGameMode = Convert.ToString(GameModeMgr.CurrentMode),
                    ScreenState = screen == null ? "[none]" : Convert.ToString(screen.ScreenState),
                    CombatSource = scenario == null || scenario.m_LoadedFrom == null ? "[none]" : scenario.m_LoadedFrom.GetName(),
                    CurrentBattleConfigurationId = currentConfig == null ? null : currentConfig.m_Id,
                    CurrentBattleIndex = currentIndex,
                    NextBattleIndex = nextIndex,
                    TotalBattles = battleConfigurations == null ? 0 : battleConfigurations.Count,
                    NextBattleConfigurationId = nextConfig == null ? null : nextConfig.m_Id,
                    CanContinue = confirm != null && IsActiveButton(continueButton),
                    CanRetreat = decline != null && IsActiveButton(declineButton),
                    LootedRewardCount = CountRewardItems(dialog, "m_lootedItemsContainer"),
                    UpcomingRewardCount = CountRewardItems(dialog, "m_upcomingItemsContainer"),
                };

                if (snapshot.LootedRewardCount == 0 && snapshot.UpcomingRewardCount == 0)
                {
                    snapshot.UpcomingRewardCount = itemsAdded.Count;
                }

                snapshot.Digest = ComputeLairDecisionDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[lair] Failed to collect lair decision snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        private static bool IsDungeonConfirmationDialogActive()
        {
            try
            {
                return SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsDungeonConfirmationDialogActive();
            }
            catch
            {
                return false;
            }
        }

        public bool TryExecuteLairDecision(
            LairDecisionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty lair decision";
                return false;
            }

            DungeonConfirmationDialogBhv dialog = FindActiveDialog();
            if (dialog == null)
            {
                message = "lair decision dialog is not active";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                Action confirm = GetPrivateField<Action>(dialog, "m_onConfirmCmd");
                if (confirm == null)
                {
                    message = "lair continue action is not available";
                    return false;
                }

                dialog.OnConfirm();
                HostLog.Write("[lair-action] " + senderName + "/" + senderSteamId + " chose continue.");
                message = "lair continue invoked on host";
                return true;
            }

            if (string.Equals(action, "retreat", StringComparison.Ordinal))
            {
                Action decline = GetPrivateField<Action>(dialog, "m_onDeclineCmd");
                if (decline == null)
                {
                    message = "lair retreat action is not available";
                    return false;
                }

                dialog.OnDecline();
                HostLog.Write("[lair-action] " + senderName + "/" + senderSteamId + " chose retreat.");
                message = "lair retreat invoked on host";
                return true;
            }

            message = "unsupported lair decision: " + request.Action;
            return false;
        }

        private static DungeonConfirmationDialogBhv FindActiveDialog()
        {
            DungeonConfirmationDialogBhv[] dialogs = UnityObject.FindObjectsOfType<DungeonConfirmationDialogBhv>(true);
            return dialogs
                .Where(dialog => dialog != null && dialog.gameObject != null && dialog.gameObject.activeInHierarchy)
                .OrderByDescending(dialog => dialog.enabled)
                .FirstOrDefault();
        }

        private static bool IsActiveButton(GameObject button)
        {
            return button != null && button.activeInHierarchy;
        }

        private static int CountRewardItems(DungeonConfirmationDialogBhv dialog, string containerFieldName)
        {
            Transform container = GetPrivateField<Transform>(dialog, containerFieldName);
            if (container == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                if (child != null && child.gameObject != null && child.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }

            return count;
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            if (target == null || string.IsNullOrEmpty(fieldName))
            {
                return default(T);
            }

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return default(T);
            }

            object value = field.GetValue(target);
            return value is T ? (T)value : default(T);
        }

        private static LairDecisionSnapshotPayload CreateInactiveSnapshot()
        {
            LairDecisionSnapshotPayload snapshot = new LairDecisionSnapshotPayload
            {
                IsActive = false,
                CurrentGameMode = Convert.ToString(GameModeMgr.CurrentMode),
                ScreenState = "[none]",
                CombatSource = "[none]",
                CurrentBattleConfigurationId = null,
                CurrentBattleIndex = -1,
                NextBattleIndex = -1,
                TotalBattles = 0,
                NextBattleConfigurationId = null,
                CanContinue = false,
                CanRetreat = false,
                LootedRewardCount = 0,
                UpcomingRewardCount = 0,
            };
            snapshot.Digest = ComputeLairDecisionDigest(snapshot);
            return snapshot;
        }

        private static string ComputeLairDecisionDigest(LairDecisionSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.CurrentGameMode + ":" +
                snapshot.ScreenState + ":" +
                snapshot.CombatSource + ":" +
                snapshot.CurrentBattleConfigurationId + ":" +
                snapshot.CurrentBattleIndex + ":" +
                snapshot.NextBattleIndex + ":" +
                snapshot.TotalBattles + ":" +
                snapshot.NextBattleConfigurationId + ":" +
                snapshot.CanContinue + ":" +
                snapshot.CanRetreat + ":" +
                snapshot.LootedRewardCount + ":" +
                snapshot.UpcomingRewardCount;
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
    }
}
