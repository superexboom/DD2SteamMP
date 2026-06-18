using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Events;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Story;
using Assets.Code.Story.Events;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Story;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2StoryChoiceSyncAdapter : IStoryChoiceAdapter, IDisposable
    {
        private static readonly MethodInfo OnChoiceSelectedMethod =
            typeof(StoryChoiceButtonBhv).GetMethod("OnChoiceSelected", BindingFlags.Instance | BindingFlags.NonPublic);

        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private string _lastStoryType;
        private string _lastStoryState = StoryState.INACTIVE.ToString();
        private string _lastEngageType = EventStoryEngageStateChanged.EngageType.None.ToString();
        private uint _lastSelectedActorGuid;

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
                    HostLog.Write("[story-choice] EventManager is not ready; story choice sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventStoryTriggered>(HandleEventStoryTriggered, false, 0);
            EventManager.AddListener<EventStoryStateChanged>(HandleEventStoryStateChanged, false, 0);
            EventManager.AddListener<EventStoryEngageStateChanged>(HandleEventStoryEngageStateChanged, false, 0);
            EventManager.AddListener<EventSelectStoryChoice>(HandleEventSelectStoryChoice, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[story-choice] Story choice listeners registered.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventStoryTriggered>(HandleEventStoryTriggered);
            EventManager.RemoveListener<EventStoryStateChanged>(HandleEventStoryStateChanged);
            EventManager.RemoveListener<EventStoryEngageStateChanged>(HandleEventStoryEngageStateChanged);
            EventManager.RemoveListener<EventSelectStoryChoice>(HandleEventSelectStoryChoice);
            _listenersRegistered = false;
        }

        public bool TryGetStoryChoiceSnapshot(out StoryChoiceSnapshotPayload snapshot)
        {
            snapshot = null;

            try
            {
                if (!IsStoryScreenActive())
                {
                    snapshot = CreateInactiveStoryChoiceSnapshot();
                    return true;
                }

                StoryScreenBhv storyScreen = FindActiveStoryScreen();
                if (!IsActive(storyScreen))
                {
                    snapshot = CreateInactiveStoryChoiceSnapshot();
                    return true;
                }

                List<StoryChoiceRuntimeOption> options = GetRuntimeOptions(storyScreen);
                snapshot = new StoryChoiceSnapshotPayload
                {
                    IsActive = options.Count > 0,
                    StoryType = options.Select(option => option.StoryTypeName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? _lastStoryType,
                    StoryState = _lastStoryState,
                    EngageType = _lastEngageType,
                    SelectedActorGuid = _lastSelectedActorGuid == 0U ? null : _lastSelectedActorGuid.ToString(),
                    ChoiceCount = options.Count,
                    Choices = options.Select(option => option.Payload).ToList(),
                };
                snapshot.Digest = ComputeStoryChoiceDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[story-choice] Failed to collect story choice snapshot: " + ex.Message + ".");
                return false;
            }
        }

        private static bool IsStoryScreenActive()
        {
            try
            {
                return SingletonMonoBehaviour<CommonUiBhv>.HasInstance(false) &&
                    SingletonMonoBehaviour<CommonUiBhv>.Instance.IsStoryScreenActive;
            }
            catch
            {
                return false;
            }
        }

        public bool TryExecuteStoryChoice(
            StoryChoiceRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null)
            {
                message = "empty story choice request";
                return false;
            }

            if (request.OptionIndex < 0)
            {
                message = "story choice option must be >= 0";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.ActorGuid) || !uint.TryParse(request.ActorGuid.Trim(), out uint actorGuid) || actorGuid == 0U)
            {
                message = "invalid story choice actor guid: " + (request.ActorGuid ?? "[none]");
                return false;
            }

            StoryScreenBhv storyScreen = FindActiveStoryScreen();
            if (!IsActive(storyScreen))
            {
                message = "story screen is not active on host";
                return false;
            }

            StoryChoiceRuntimeOption option = GetRuntimeOptions(storyScreen)
                .FirstOrDefault(candidate =>
                    candidate.Payload.OptionIndex == request.OptionIndex &&
                    candidate.Payload.HeroSlot == request.HeroSlot &&
                    string.Equals(candidate.Payload.ActorGuid, request.ActorGuid, StringComparison.Ordinal));

            if (option == null)
            {
                message = "story choice option " + request.OptionIndex +
                    " for actor " + request.ActorGuid +
                    " is not active";
                return false;
            }

            if (!option.Payload.CanChoose)
            {
                message = "story choice option " + request.OptionIndex + " is not selectable yet";
                return false;
            }

            if (OnChoiceSelectedMethod == null)
            {
                message = "StoryChoiceButtonBhv.OnChoiceSelected was not found";
                return false;
            }

            try
            {
                HostLog.Write("[story-choice-action] " + senderName + "/" + senderSteamId +
                    " selects option=" + request.OptionIndex +
                    ", slot=" + request.HeroSlot +
                    ", actor=" + option.Payload.ActorName + "/" + option.Payload.ActorGuid +
                    ", choice=" + (option.Payload.ChoiceId ?? "[unknown]") + ".");
                OnChoiceSelectedMethod.Invoke(option.Button, Array.Empty<object>());
                message = "story choice option " + request.OptionIndex + " invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "story choice failed: " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message);
                HostLog.Write("[story-choice-action] " + message + ".");
                return false;
            }
        }

        private void HandleEventStoryTriggered(EventStoryTriggered evt)
        {
            if (evt == null)
            {
                return;
            }

            _lastStoryType = SafeGetCustomEnumName(evt.m_StoryType);
            _lastStoryState = StoryState.START.ToString();
            _lastEngageType = EventStoryEngageStateChanged.EngageType.None.ToString();
            _lastSelectedActorGuid = 0U;
            HostLog.Write("[story-choice] story triggered: type=" + (_lastStoryType ?? "[unknown]") +
                ", nodeSubType=" + (evt.m_NodeSubType ?? "[none]") + ".");
        }

        private void HandleEventStoryStateChanged(EventStoryStateChanged evt)
        {
            if (evt == null)
            {
                return;
            }

            _lastStoryState = evt.m_ToStoryState.ToString();
            HostLog.Write("[story-choice] state " + evt.m_FromStoryState + " -> " + evt.m_ToStoryState + ".");
        }

        private void HandleEventStoryEngageStateChanged(EventStoryEngageStateChanged evt)
        {
            if (evt == null)
            {
                return;
            }

            _lastStoryType = SafeGetCustomEnumName(evt.m_storyType) ?? _lastStoryType;
            _lastEngageType = evt.m_engageType.ToString();
            HostLog.Write("[story-choice] engage type=" + _lastEngageType +
                ", storyType=" + (_lastStoryType ?? "[unknown]") + ".");
        }

        private void HandleEventSelectStoryChoice(EventSelectStoryChoice evt)
        {
            if (evt == null)
            {
                return;
            }

            _lastStoryType = SafeGetCustomEnumName(evt.m_StoryType) ?? _lastStoryType;
            _lastSelectedActorGuid = evt.m_ActorGuid;
            HostLog.Write("[story-choice] selected actor=" + evt.m_ActorGuid +
                ", storyType=" + (_lastStoryType ?? "[unknown]") + ".");
        }

        private static StoryScreenBhv FindActiveStoryScreen()
        {
            StoryScreenBhv[] screens = UnityObject.FindObjectsOfType<StoryScreenBhv>(true);
            return screens.FirstOrDefault(IsActive);
        }

        private static bool IsActive(StoryScreenBhv storyScreen)
        {
            return storyScreen != null &&
                storyScreen.gameObject != null &&
                storyScreen.gameObject.activeInHierarchy;
        }

        private static List<StoryChoiceRuntimeOption> GetRuntimeOptions(StoryScreenBhv storyScreen)
        {
            List<StoryChoiceRuntimeOption> options = new List<StoryChoiceRuntimeOption>();
            if (storyScreen == null)
            {
                return options;
            }

            List<StoryChoiceButtonBhv> buttons =
                GetPrivateField<List<StoryChoiceButtonBhv>>(storyScreen, "m_StoryChoiceButtons") ??
                storyScreen.GetComponentsInChildren<StoryChoiceButtonBhv>(true).ToList();

            for (int i = 0; i < buttons.Count; i++)
            {
                StoryChoiceButtonBhv button = buttons[i];
                if (button == null || button.gameObject == null || !button.gameObject.activeInHierarchy)
                {
                    continue;
                }

                uint actorGuid = SafeGetActorGuid(button);
                if (actorGuid == 0U)
                {
                    continue;
                }

                ActorInstance actor = SafeGetActorInstance(actorGuid);
                StoryType storyType = GetPrivateField<StoryType>(button, "m_StoryType");
                StoryChoiceDefinition storyChoice = SafeGetStoryChoice(actorGuid);
                List<StoryChoicePreviewPayload> playerPreviews = BuildPreviewPayloads(GetPrivateField<IEnumerable<StoryChoiceDefinition.StoryChoicePreview>>(button, "m_PlayerStoryChoicePreviews"));
                List<StoryChoicePreviewPayload> enemyPreviews = BuildPreviewPayloads(GetPrivateField<IEnumerable<StoryChoiceDefinition.StoryChoicePreview>>(button, "m_EnemyStoryChoicePreviews"));
                StoryChoiceOptionPayload payload = new StoryChoiceOptionPayload
                {
                    OptionIndex = i,
                    HeroSlot = SafeGetHeroSlot(actor),
                    ActorGuid = actorGuid.ToString(),
                    ActorDataId = SafeGetActorDataId(actor),
                    ActorName = SafeGetActorName(actor),
                    ChoiceId = storyChoice == null ? null : storyChoice.m_Id,
                    ResultType = storyChoice == null ? null : storyChoice.m_ResultType.ToString(),
                    BarkText = GetPrivateField<string>(button, "m_barkString"),
                    QuirkChoiceId = GetPrivateField<string>(button, "m_quirkChoiceId"),
                    CanChoose = SafeGetHoverable(button),
                    PlayerPreviews = playerPreviews,
                    EnemyPreviews = enemyPreviews,
                };

                options.Add(new StoryChoiceRuntimeOption(
                    button,
                    SafeGetCustomEnumName(storyType),
                    payload));
            }

            return options
                .OrderBy(option => option.Payload.OptionIndex)
                .ToList();
        }

        private static uint SafeGetActorGuid(StoryChoiceButtonBhv button)
        {
            try
            {
                return button == null ? 0U : button.ActorGuid;
            }
            catch
            {
                return 0U;
            }
        }

        private static bool SafeGetHoverable(StoryChoiceButtonBhv button)
        {
            try
            {
                return button != null && button.Hoverable;
            }
            catch
            {
                return false;
            }
        }

        private static ActorInstance SafeGetActorInstance(uint actorGuid)
        {
            if (actorGuid == 0U)
            {
                return null;
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

        private static int SafeGetHeroSlot(ActorInstance actor)
        {
            try
            {
                return actor == null || actor.TeamPosition < 0 ? 0 : actor.TeamPosition + 1;
            }
            catch
            {
                return 0;
            }
        }

        private static StoryChoiceDefinition SafeGetStoryChoice(uint actorGuid)
        {
            try
            {
                object storyBhv = FindStoryBhvInstance();
                if (storyBhv == null)
                {
                    return null;
                }

                MethodInfo method = storyBhv.GetType().GetMethod(
                    "GetStoryChoiceFromActorGuid",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return method == null ? null : method.Invoke(storyBhv, new object[] { actorGuid }) as StoryChoiceDefinition;
            }
            catch
            {
                return null;
            }
        }

        private static object FindStoryBhvInstance()
        {
            Type storyBhvType = typeof(StoryChoiceDefinition).Assembly.GetType("Assets.Code.Story.StoryBhv");
            if (storyBhvType == null)
            {
                return null;
            }

            try
            {
                Type type = storyBhvType;
                while (type != null)
                {
                    PropertyInfo property = type.GetProperty(
                        "Instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    if (property != null)
                    {
                        object instance = property.GetValue(null, null);
                        if (instance != null)
                        {
                            return instance;
                        }
                    }

                    type = type.BaseType;
                }
            }
            catch
            {
            }

            try
            {
                UnityObject[] instances = UnityObject.FindObjectsOfType(storyBhvType, true);
                return instances == null ? null : instances.FirstOrDefault(instance => instance != null);
            }
            catch
            {
                return null;
            }
        }

        private static List<StoryChoicePreviewPayload> BuildPreviewPayloads(IEnumerable<StoryChoiceDefinition.StoryChoicePreview> previews)
        {
            List<StoryChoicePreviewPayload> payloads = new List<StoryChoicePreviewPayload>();
            if (previews == null)
            {
                return payloads;
            }

            foreach (StoryChoiceDefinition.StoryChoicePreview preview in previews)
            {
                if (preview == null || string.IsNullOrWhiteSpace(preview.PreviewId))
                {
                    continue;
                }

                payloads.Add(new StoryChoicePreviewPayload(
                    preview.PreviewId,
                    preview.Value,
                    preview.ShowNumber,
                    GetPreviewLabel(preview),
                    GetPreviewDescription(preview)));
            }

            return payloads;
        }

        private static string GetPreviewLabel(StoryChoiceDefinition.StoryChoicePreview preview)
        {
            string description = GetPreviewDescription(preview);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            return preview == null ? null : preview.PreviewId;
        }

        private static string GetPreviewDescription(StoryChoiceDefinition.StoryChoicePreview preview)
        {
            if (preview == null || string.IsNullOrWhiteSpace(preview.PreviewId))
            {
                return null;
            }

            try
            {
                string value = null;
                if (preview.Value < 0)
                {
                    value = Singleton<Localization>.Instance.TryGetString(
                        "story_icon_description_" + preview.PreviewId + "_negative",
                        false);
                }

                if (string.IsNullOrEmpty(value))
                {
                    value = Singleton<Localization>.Instance.TryGetString(
                        "story_icon_description_" + preview.PreviewId,
                        false);
                }

                return string.IsNullOrWhiteSpace(value) ? preview.PreviewId : value;
            }
            catch
            {
                return preview.PreviewId;
            }
        }

        private static string SafeGetCustomEnumName<T>(CustomEnum<T> value)
            where T : CustomEnum<T>
        {
            try
            {
                return value == null ? null : value.GetName();
            }
            catch
            {
                return value == null ? null : Convert.ToString(value);
            }
        }

        private static StoryChoiceSnapshotPayload CreateInactiveStoryChoiceSnapshot()
        {
            return new StoryChoiceSnapshotPayload
            {
                IsActive = false,
                StoryState = StoryState.INACTIVE.ToString(),
                EngageType = EventStoryEngageStateChanged.EngageType.None.ToString(),
                SelectedActorGuid = null,
                ChoiceCount = 0,
                Digest = "story-choice-inactive",
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

        private static object GetPrivateFieldObject(object instance, string fieldName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static string ComputeStoryChoiceDigest(StoryChoiceSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.StoryType + ":" +
                snapshot.StoryState + ":" +
                snapshot.EngageType + ":" +
                snapshot.SelectedActorGuid + ":" +
                snapshot.ChoiceCount + ":" +
                string.Join("|", (snapshot.Choices ?? Array.Empty<StoryChoiceOptionPayload>())
                    .OrderBy(choice => choice.OptionIndex)
                    .Select(choice =>
                        choice.OptionIndex + "," +
                        choice.HeroSlot + "," +
                        choice.ActorGuid + "," +
                        choice.ActorDataId + "," +
                        choice.ActorName + "," +
                        choice.ChoiceId + "," +
                        choice.ResultType + "," +
                        choice.CanChoose + "," +
                        string.Join(";", (choice.PlayerPreviews ?? Array.Empty<StoryChoicePreviewPayload>())
                            .Select(preview => preview.PreviewId + "=" + preview.Value + ":" + preview.ShowNumber)
                            .ToArray()) + "," +
                        string.Join(";", (choice.EnemyPreviews ?? Array.Empty<StoryChoicePreviewPayload>())
                            .Select(preview => preview.PreviewId + "=" + preview.Value + ":" + preview.ShowNumber)
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

        private sealed class StoryChoiceRuntimeOption
        {
            public StoryChoiceRuntimeOption(
                StoryChoiceButtonBhv button,
                string storyTypeName,
                StoryChoiceOptionPayload payload)
            {
                Button = button;
                StoryTypeName = storyTypeName;
                Payload = payload;
            }

            public StoryChoiceButtonBhv Button { get; }

            public string StoryTypeName { get; }

            public StoryChoiceOptionPayload Payload { get; }
        }
    }
}
