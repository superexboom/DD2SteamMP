using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Boss;
using Assets.Code.Game;
using Assets.Code.Locale;
using Assets.Code.UI;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2ConfessionChoiceSyncAdapter : IConfessionChoiceAdapter
    {
        public bool TryGetConfessionChoiceSnapshot(out ConfessionChoiceSnapshotPayload snapshot)
        {
            try
            {
                if (!IsBossSelectActive())
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                BossSelectWidgetBhv widget = FindActiveWidget();
                if (widget == null)
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                snapshot = BuildSnapshot(widget);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[confession] Failed to collect confession choice snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        public bool TryExecuteConfessionChoice(
            ConfessionChoiceRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null)
            {
                message = "empty confession choice";
                return false;
            }

            BossSelectWidgetBhv widget = FindActiveWidget();
            if (widget == null)
            {
                message = "confession choice screen is not active";
                return false;
            }

            ConfessionRuntimeOption option = GetRuntimeOptions(widget)
                .FirstOrDefault(candidate =>
                    candidate.Payload.OptionIndex == request.OptionIndex &&
                    (string.IsNullOrWhiteSpace(request.BossId) ||
                        string.Equals(candidate.Payload.BossId ?? string.Empty, request.BossId, StringComparison.Ordinal)));
            if (option == null)
            {
                message = "confession option " + request.OptionIndex + "/" + (request.BossId ?? "[none]") +
                    " is not in the current screen";
                return false;
            }

            ConfessionChoiceSnapshotPayload snapshot = BuildSnapshot(widget);
            if (!snapshot.CanChoose)
            {
                message = "confession choice screen cannot choose yet";
                return false;
            }

            if (!option.Payload.IsSelectable)
            {
                message = "confession option " + option.Payload.OptionIndex + "/" +
                    (option.Payload.BossId ?? "[none]") + " is not selectable";
                return false;
            }

            try
            {
                option.Option.OnSelected();
                widget.OnConfirm();
                HostLog.Write("[confession-action] " + senderName + "/" + senderSteamId +
                    " chose confession " + (option.Payload.BossId ?? "[none]") +
                    " option=" + option.Payload.OptionIndex + ".");
                message = "confession choice invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "confession choice failed: " + ex.Message;
                HostLog.Write("[confession-action] " + message + ".");
                return false;
            }
        }

        private static ConfessionChoiceSnapshotPayload BuildSnapshot(BossSelectWidgetBhv widget)
        {
            UiScreenBhv screen = GetPrivateField<UiScreenBhv>(widget, "m_screenBhv");
            bool confirmClicked = GetPrivateField<bool>(widget, "m_confirmClicked");
            SelectBossOptionBhv selectedOption = GetPrivateField<SelectBossOptionBhv>(widget, "m_selectedOption");
            List<ConfessionRuntimeOption> options = GetRuntimeOptions(widget);
            int selectedOptionIndex = -1;
            string selectedBossId = null;
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].Option == selectedOption)
                {
                    selectedOptionIndex = options[i].Payload.OptionIndex;
                    selectedBossId = options[i].Payload.BossId;
                    break;
                }
            }

            bool screenOpen = screen != null && screen.ScreenState == UiScreenState.Open;
            ConfessionChoiceSnapshotPayload snapshot = new ConfessionChoiceSnapshotPayload
            {
                IsActive = widget != null && widget.gameObject != null && widget.gameObject.activeInHierarchy,
                CurrentGameMode = SafeGetCurrentModeName(),
                ScreenState = screen == null ? "[none]" : Convert.ToString(screen.ScreenState),
                CanChoose = screenOpen && !confirmClicked && options.Any(option => option.Payload.IsSelectable),
                SelectedOptionIndex = selectedOptionIndex,
                SelectedBossId = selectedBossId,
                Choices = options.Select(option => option.Payload).ToList(),
            };
            snapshot.Digest = ComputeConfessionChoiceDigest(snapshot);
            return snapshot;
        }

        private static bool IsBossSelectActive()
        {
            try
            {
                return SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsBossSelectActive;
            }
            catch
            {
                return false;
            }
        }

        private static List<ConfessionRuntimeOption> GetRuntimeOptions(BossSelectWidgetBhv widget)
        {
            List<ConfessionRuntimeOption> result = new List<ConfessionRuntimeOption>();
            if (widget == null)
            {
                return result;
            }

            Transform layout = GetPrivateField<Transform>(widget, "m_bossOptionLayout");
            SelectBossOptionBhv[] options = layout == null
                ? widget.GetComponentsInChildren<SelectBossOptionBhv>(true)
                : layout.GetComponentsInChildren<SelectBossOptionBhv>(true);
            for (int i = 0; i < options.Length; i++)
            {
                SelectBossOptionBhv option = options[i];
                if (option == null || option.gameObject == null || !option.gameObject.activeInHierarchy)
                {
                    continue;
                }

                BossDefinition bossDefinition = option.GetBossDefinition();
                string bossId = bossDefinition == null ? null : bossDefinition.m_Id;
                bool selectable = bossDefinition != null && IsOptionInteractable(option);
                bool selected = GetPrivateField<bool>(option, "m_selected");
                result.Add(new ConfessionRuntimeOption(
                    option,
                    new ConfessionChoiceOptionPayload(
                        result.Count,
                        bossId,
                        GetBossLabel(bossId),
                        selectable,
                        selected)));
            }

            return result;
        }

        private static BossSelectWidgetBhv FindActiveWidget()
        {
            return UnityObject.FindObjectsOfType<BossSelectWidgetBhv>(true)
                .Where(widget => widget != null && widget.gameObject != null && widget.gameObject.activeInHierarchy)
                .OrderByDescending(widget => widget.enabled)
                .FirstOrDefault();
        }

        private static bool IsOptionInteractable(SelectBossOptionBhv option)
        {
            Button button = GetPrivateField<Button>(option, "m_buttonComponent");
            return button != null && button.interactable;
        }

        private static string GetBossLabel(string bossId)
        {
            if (string.IsNullOrWhiteSpace(bossId))
            {
                return "boss_choice_unknown_label";
            }

            string key = "boss_choice_" + bossId + "_label";
            try
            {
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

            return key;
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

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName))
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

        private static ConfessionChoiceSnapshotPayload CreateInactiveSnapshot()
        {
            ConfessionChoiceSnapshotPayload snapshot = new ConfessionChoiceSnapshotPayload
            {
                IsActive = false,
                CurrentGameMode = SafeGetCurrentModeName(),
                ScreenState = "[none]",
                CanChoose = false,
                SelectedOptionIndex = -1,
                SelectedBossId = null,
            };
            snapshot.Digest = ComputeConfessionChoiceDigest(snapshot);
            return snapshot;
        }

        private static string ComputeConfessionChoiceDigest(ConfessionChoiceSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.CurrentGameMode + ":" +
                snapshot.ScreenState + ":" +
                snapshot.CanChoose + ":" +
                snapshot.SelectedOptionIndex + ":" +
                (snapshot.SelectedBossId ?? string.Empty) + ":" +
                string.Join("|", (snapshot.Choices ?? Array.Empty<ConfessionChoiceOptionPayload>())
                    .Where(choice => choice != null)
                    .Select(choice =>
                        choice.OptionIndex + "," +
                        (choice.BossId ?? string.Empty) + "," +
                        choice.IsSelectable + "," +
                        choice.IsSelected)
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

        private sealed class ConfessionRuntimeOption
        {
            public ConfessionRuntimeOption(SelectBossOptionBhv option, ConfessionChoiceOptionPayload payload)
            {
                Option = option;
                Payload = payload;
            }

            public SelectBossOptionBhv Option { get; }

            public ConfessionChoiceOptionPayload Payload { get; }
        }
    }
}
