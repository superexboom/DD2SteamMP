using System;
using System.Linq;
using Assets.Code.Game;
using Assets.Code.Platform;
using Assets.Code.Profile;
using Assets.Code.Serialization;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2MainMenuSyncAdapter : IMainMenuActionAdapter
    {
        public bool TryGetMainMenuSnapshot(out MainMenuSnapshotPayload snapshot)
        {
            try
            {
                if (GameModeMgr.CurrentMode != GameModeType.MAIN_MENU)
                {
                    snapshot = BuildInactiveSnapshot(SafeGetCurrentModeName());
                    return true;
                }

                MainMenuUiScreenBhv menu = FindActiveMainMenu();
                snapshot = BuildSnapshot(menu);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[main-menu] Failed to collect main menu snapshot: " + ex.Message + ".");
                snapshot = BuildInactiveSnapshot("[error]");
                return false;
            }
        }

        public bool TryExecuteMainMenuAction(
            MainMenuActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty main menu action request";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (!string.Equals(action, "continue", StringComparison.Ordinal) &&
                !string.Equals(action, "start_new", StringComparison.Ordinal) &&
                !string.Equals(action, "new", StringComparison.Ordinal))
            {
                message = "unsupported main menu action: " + request.Action;
                return false;
            }

            MainMenuUiScreenBhv menu = FindActiveMainMenu();
            MainMenuSnapshotPayload snapshot = BuildSnapshot(menu);
            if (!snapshot.IsActive || menu == null)
            {
                message = "main menu is not active on host";
                return false;
            }

            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                if (!snapshot.CanContinueExpedition)
                {
                    message = "cannot continue expedition: " + (snapshot.BlockReason ?? "[blocked]");
                    return false;
                }

                try
                {
                    menu.OnContinueGameClick();
                    HostLog.Write("[main-menu-action] " + senderName + "/" + senderSteamId +
                        " requested continue expedition.");
                    message = "continue expedition requested";
                    return true;
                }
                catch (Exception ex)
                {
                    message = "continue expedition failed: " + ex.Message;
                    HostLog.Write("[main-menu-action] " + message + ".");
                    return false;
                }
            }

            if (!snapshot.CanStartNewExpedition)
            {
                message = "cannot start new expedition: " + BuildStartNewBlockReason(snapshot);
                return false;
            }

            try
            {
                menu.OnNewGameClick();
                HostLog.Write("[main-menu-action] " + senderName + "/" + senderSteamId +
                    " requested new expedition.");
                message = "new expedition requested";
                return true;
            }
            catch (Exception ex)
            {
                message = "new expedition failed: " + ex.Message;
                HostLog.Write("[main-menu-action] " + message + ".");
                return false;
            }
        }

        private static MainMenuSnapshotPayload BuildSnapshot(MainMenuUiScreenBhv menu)
        {
            MainMenuSnapshotPayload snapshot = new MainMenuSnapshotPayload
            {
                IsActive = menu != null && SafeGetCurrentModeName() == "MAIN_MENU",
                CurrentGameMode = SafeGetCurrentModeName(),
                HasDisclaimerShown = SafeGetHasDisclaimerShown(menu),
                IsLoadingGameOrCinematic = SafeGetIsLoading(menu),
                IsInputtingText = SafeGetIsInputtingText(menu),
                HasExpeditionSave = SafeGetHasExpeditionSave(),
                CanAbandonExpedition = SafeGetCanAbandonExpedition(),
                ProfileName = SafeGetProfileName(),
                SaveValidationAction = SafeGetSaveValidationAction(),
                SaveFailureReason = SafeGetSaveFailureReason(),
            };

            bool isBusy = SafeIsGameModeChanging() ||
                snapshot.IsLoadingGameOrCinematic ||
                snapshot.IsInputtingText ||
                !snapshot.HasDisclaimerShown;
            snapshot.CanContinueExpedition = snapshot.IsActive &&
                !isBusy &&
                snapshot.HasExpeditionSave;
            snapshot.CanStartNewExpedition = snapshot.IsActive &&
                !isBusy &&
                !snapshot.HasExpeditionSave;
            snapshot.BlockReason = BuildBlockReason(snapshot, isBusy);
            snapshot.Digest = ComputeMainMenuDigest(snapshot);
            return snapshot;
        }

        private static MainMenuSnapshotPayload BuildInactiveSnapshot(string mode)
        {
            MainMenuSnapshotPayload snapshot = new MainMenuSnapshotPayload
            {
                IsActive = false,
                CurrentGameMode = mode,
                ProfileName = SafeGetProfileName(),
                SaveValidationAction = SafeGetSaveValidationAction(),
                SaveFailureReason = SafeGetSaveFailureReason(),
                BlockReason = "main menu is not active",
            };
            snapshot.Digest = ComputeMainMenuDigest(snapshot);
            return snapshot;
        }

        private static MainMenuUiScreenBhv FindActiveMainMenu()
        {
            try
            {
                return UnityObject.FindObjectsOfType<MainMenuUiScreenBhv>(true)
                    .Where(menu => menu != null &&
                        menu.gameObject != null &&
                        menu.gameObject.activeInHierarchy)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildBlockReason(MainMenuSnapshotPayload snapshot, bool isBusy)
        {
            if (snapshot == null || !snapshot.IsActive)
            {
                return "main menu is not active";
            }

            if (!snapshot.HasDisclaimerShown)
            {
                return "main menu disclaimer/start animation is not ready";
            }

            if (snapshot.IsInputtingText)
            {
                return "profile text input is active";
            }

            if (snapshot.IsLoadingGameOrCinematic)
            {
                return "main menu is already loading or playing a cinematic";
            }

            if (SafeIsGameModeChanging())
            {
                return "game mode is changing";
            }

            if (snapshot.HasExpeditionSave && !snapshot.CanAbandonExpedition)
            {
                return "existing expedition save cannot be abandoned from this state";
            }

            return isBusy ? "main menu is busy" : string.Empty;
        }

        private static string BuildStartNewBlockReason(MainMenuSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "[blocked]";
            }

            if (snapshot.HasExpeditionSave)
            {
                return "existing expedition save is host-only; use DD2's native host UI to abandon it";
            }

            return snapshot.BlockReason ?? "[blocked]";
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

        private static bool SafeGetHasDisclaimerShown(MainMenuUiScreenBhv menu)
        {
            try
            {
                return menu != null && menu.HasDisclaimerShown;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeGetIsLoading(MainMenuUiScreenBhv menu)
        {
            try
            {
                return menu != null && menu.IsLoadingGameOrCinematic;
            }
            catch
            {
                return true;
            }
        }

        private static bool SafeGetIsInputtingText(MainMenuUiScreenBhv menu)
        {
            try
            {
                return menu != null && menu.IsInputtingText;
            }
            catch
            {
                return true;
            }
        }

        private static bool SafeGetHasExpeditionSave()
        {
            try
            {
                return PlatformMgr.Instance != null &&
                    PlatformMgr.Instance.DoesAnySaveExist(GameType.EXPEDITION);
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeGetCanAbandonExpedition()
        {
            try
            {
                return SingletonMonoBehaviour<ProfileBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile() != null &&
                    SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile().GetCanAbandon(GameType.EXPEDITION);
            }
            catch
            {
                return false;
            }
        }

        private static string SafeGetProfileName()
        {
            try
            {
                return SingletonMonoBehaviour<ProfileBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile() != null
                    ? SingletonMonoBehaviour<ProfileBhv>.Instance.GetCurrentProfile().GetName()
                    : "[none]";
            }
            catch
            {
                return "[none]";
            }
        }

        private static string SafeGetSaveValidationAction()
        {
            try
            {
                return SingletonMonoBehaviour<SaveLoadMgr>.HasInstance(false)
                    ? Convert.ToString(SingletonMonoBehaviour<SaveLoadMgr>.Instance.GetValidationActionForSave())
                    : "[none]";
            }
            catch
            {
                return "[none]";
            }
        }

        private static string SafeGetSaveFailureReason()
        {
            try
            {
                return SingletonMonoBehaviour<SaveLoadMgr>.HasInstance(false)
                    ? Convert.ToString(SingletonMonoBehaviour<SaveLoadMgr>.Instance.GetFailureReasonForSave())
                    : "[none]";
            }
            catch
            {
                return "[none]";
            }
        }

        private static string ComputeMainMenuDigest(MainMenuSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            return ComputeStableDigest(
                snapshot.IsActive + ";" +
                (snapshot.CurrentGameMode ?? string.Empty) + ";" +
                (snapshot.ProfileName ?? string.Empty) + ";" +
                snapshot.HasDisclaimerShown + ";" +
                snapshot.IsLoadingGameOrCinematic + ";" +
                snapshot.IsInputtingText + ";" +
                snapshot.HasExpeditionSave + ";" +
                snapshot.CanAbandonExpedition + ";" +
                snapshot.CanContinueExpedition + ";" +
                snapshot.CanStartNewExpedition + ";" +
                (snapshot.SaveValidationAction ?? string.Empty) + ";" +
                (snapshot.SaveFailureReason ?? string.Empty) + ";" +
                (snapshot.BlockReason ?? string.Empty));
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
