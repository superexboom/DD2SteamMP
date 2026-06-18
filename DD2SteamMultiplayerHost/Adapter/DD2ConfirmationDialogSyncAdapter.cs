using System;
using System.Linq;
using System.Reflection;
using Assets.Code.Data;
using Assets.Code.Locale;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2ConfirmationDialogSyncAdapter : IConfirmationDialogAdapter
    {
        public bool TryGetConfirmationDialogSnapshot(out ConfirmationDialogSnapshotPayload snapshot)
        {
            try
            {
                if (!IsConfirmationDialogActive())
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                ConfirmationDialogBhv dialog = FindActiveDialog();
                if (dialog == null)
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                snapshot = BuildSnapshot(dialog);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[dialog] Failed to collect confirmation dialog snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        private static bool IsConfirmationDialogActive()
        {
            try
            {
                return SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsConfirmationDialogActive;
            }
            catch
            {
                return false;
            }
        }

        public bool TryExecuteConfirmationDialog(
            ConfirmationDialogRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty confirmation dialog request";
                return false;
            }

            ConfirmationDialogBhv dialog = FindActiveDialog();
            if (dialog == null)
            {
                message = "confirmation dialog is not active";
                return false;
            }

            ConfirmationDialogSnapshotPayload snapshot = BuildSnapshot(dialog);
            if (!snapshot.IsAllowed)
            {
                message = "confirmation dialog is not multiplayer-safe: " + (snapshot.BlockReason ?? "[none]");
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "confirm", StringComparison.Ordinal))
            {
                if (!snapshot.CanConfirm)
                {
                    message = "confirmation action is not available";
                    return false;
                }

                dialog.OnConfirmPressed();
                HostLog.Write("[dialog-action] " + senderName + "/" + senderSteamId +
                    " confirmed " + (snapshot.Kind ?? "[unknown]") + ".");
                message = "confirmation dialog confirm invoked on host";
                return true;
            }

            if (string.Equals(action, "decline", StringComparison.Ordinal))
            {
                if (!snapshot.CanDecline)
                {
                    message = "decline action is not available";
                    return false;
                }

                dialog.OnDeclinePressed();
                HostLog.Write("[dialog-action] " + senderName + "/" + senderSteamId +
                    " declined " + (snapshot.Kind ?? "[unknown]") + ".");
                message = "confirmation dialog decline invoked on host";
                return true;
            }

            message = "unsupported confirmation dialog action: " + request.Action;
            return false;
        }

        private static ConfirmationDialogSnapshotPayload BuildSnapshot(ConfirmationDialogBhv dialog)
        {
            DataContextBhv dataContext = GetPrivateField<DataContextBhv>(dialog, "m_DataContextBhv") ??
                dialog.GetComponent<DataContextBhv>();
            UiScreenBhv screen = GetPrivateField<UiScreenBhv>(dialog, "m_screenBhv");
            GameObject acceptButton = GetPrivateField<GameObject>(dialog, "m_AcceptBtn");
            GameObject declineButton = GetPrivateField<GameObject>(dialog, "m_DeclineBtn");
            Action confirmAction = GetPrivateField<Action>(dialog, "m_ConfirmAction");
            Action declineAction = GetPrivateField<Action>(dialog, "m_DeclineAction");

            string title = dataContext == null ? null : dataContext.GetStringValue("confirmation_title");
            string description = dataContext == null ? null : dataContext.GetStringValue("confirmation_desc");
            string confirmLabel = dataContext == null ? null : dataContext.GetStringValue("confirmation_label");
            string declineLabel = dataContext == null ? null : dataContext.GetStringValue("decline_label");

            string kind;
            string blockReason;
            bool allowed = TryClassifyAllowedDialog(
                dialog.DialogType,
                title,
                description,
                confirmLabel,
                declineLabel,
                confirmAction,
                declineAction,
                out kind,
                out blockReason);

            ConfirmationDialogSnapshotPayload snapshot = new ConfirmationDialogSnapshotPayload
            {
                IsActive = true,
                IsAllowed = allowed,
                Kind = kind,
                DialogType = Convert.ToString(dialog.DialogType),
                ScreenState = screen == null ? "[none]" : Convert.ToString(screen.ScreenState),
                Title = title,
                Description = description,
                ConfirmLabel = confirmLabel,
                DeclineLabel = declineLabel,
                CanConfirm = allowed && IsActiveButton(acceptButton),
                CanDecline = allowed && declineAction != null && IsActiveButton(declineButton),
                BlockReason = allowed ? null : blockReason,
            };
            snapshot.Digest = ComputeConfirmationDialogDigest(snapshot);
            return snapshot;
        }

        private static bool TryClassifyAllowedDialog(
            CommonUiBhv.ConfirmationDialogType dialogType,
            string title,
            string description,
            string confirmLabel,
            string declineLabel,
            Action confirmAction,
            Action declineAction,
            out string kind,
            out string blockReason)
        {
            kind = null;
            blockReason = null;

            if (dialogType == CommonUiBhv.ConfirmationDialogType.HotkeyCloseable &&
                MatchesLocalized(title, "character_sheet_can_equip_skills_prompt_title") &&
                (MatchesLocalized(description, "hero_select_can_equip_skills_prompt_desc") ||
                    MatchesLocalized(description, "character_sheet_can_equip_skills_prompt_desc")))
            {
                kind = "skill_loadout_warning";
                return true;
            }

            if (dialogType == CommonUiBhv.ConfirmationDialogType.HotkeyCloseable &&
                MatchesLocalized(description, "loot_close_confirmation_dialog_desc_label"))
            {
                kind = "loot_close_warning";
                return true;
            }

            if ((dialogType == CommonUiBhv.ConfirmationDialogType.Default ||
                    dialogType == CommonUiBhv.ConfirmationDialogType.HotkeyCloseable) &&
                declineAction == null &&
                (MatchesLocalized(confirmLabel, "continue_label") ||
                    MatchesLocalized(confirmLabel, "embark_continue_label")))
            {
                kind = "continue_notice";
                return true;
            }

            if (dialogType == CommonUiBhv.ConfirmationDialogType.InnEmbark)
            {
                kind = "inn_embark_warning";
                return true;
            }

            if (dialogType == CommonUiBhv.ConfirmationDialogType.AbandonRun ||
                dialogType == CommonUiBhv.ConfirmationDialogType.AbandonRunMainMenu ||
                dialogType == CommonUiBhv.ConfirmationDialogType.AbandonRunExitGame ||
                dialogType == CommonUiBhv.ConfirmationDialogType.MainMenuExitGame ||
                dialogType == CommonUiBhv.ConfirmationDialogType.DiscardItem ||
                dialogType == CommonUiBhv.ConfirmationDialogType.StagecoachUnequippable)
            {
                blockReason = "host-only dialog type " + dialogType;
                return false;
            }

            blockReason = "dialog is not in the multiplayer allowlist";
            return false;
        }

        private static bool MatchesLocalized(string value, string locKey)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(locKey))
            {
                return false;
            }

            string normalizedValue = NormalizeText(value);
            if (string.Equals(normalizedValue, NormalizeText(locKey), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                if (Singleton<Localization>.HasInstance())
                {
                    string localized = Singleton<Localization>.Instance.GetString(locKey, true);
                    if (string.Equals(normalizedValue, NormalizeText(localized), StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static string NormalizeText(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static ConfirmationDialogBhv FindActiveDialog()
        {
            ConfirmationDialogBhv[] dialogs = UnityObject.FindObjectsOfType<ConfirmationDialogBhv>(true);
            return dialogs
                .Where(dialog => dialog != null && dialog.gameObject != null && dialog.gameObject.activeInHierarchy)
                .OrderByDescending(dialog => dialog.enabled)
                .FirstOrDefault();
        }

        private static bool IsActiveButton(GameObject button)
        {
            return button != null && button.activeInHierarchy;
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

        private static ConfirmationDialogSnapshotPayload CreateInactiveSnapshot()
        {
            ConfirmationDialogSnapshotPayload snapshot = new ConfirmationDialogSnapshotPayload
            {
                IsActive = false,
                IsAllowed = false,
                Kind = "[none]",
                DialogType = "[none]",
                ScreenState = "[none]",
                CanConfirm = false,
                CanDecline = false,
            };
            snapshot.Digest = ComputeConfirmationDialogDigest(snapshot);
            return snapshot;
        }

        private static string ComputeConfirmationDialogDigest(ConfirmationDialogSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.IsAllowed + ":" +
                snapshot.Kind + ":" +
                snapshot.DialogType + ":" +
                snapshot.ScreenState + ":" +
                snapshot.Title + ":" +
                snapshot.Description + ":" +
                snapshot.ConfirmLabel + ":" +
                snapshot.DeclineLabel + ":" +
                snapshot.CanConfirm + ":" +
                snapshot.CanDecline + ":" +
                snapshot.BlockReason;
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
