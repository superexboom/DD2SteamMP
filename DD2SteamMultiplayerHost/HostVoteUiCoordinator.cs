using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Assets.Code.Inn.Presentation;
using Assets.Code.UI;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Story;
using Assets.Code.UI.Widgets;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost
{
    internal sealed class HostVoteUiCoordinator
    {
        private const float AnchorRefreshInterval = 0.35f;
        private const float BadgeHeight = 22f;
        private const float BadgeGap = 3f;
        private const float PanelWidth = 360f;
        private static readonly Regex RouteOptionRegex = new Regex(@"option\s+(-?\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex ActionOptionRegex = new Regex(@"(?:select_biome|option)\s*:?\s*(-?\d+)", RegexOptions.IgnoreCase);
        private static readonly string[] KnownVoteKeys =
        {
            MultiplayerSession.VoteKeyRoute,
            MultiplayerSession.VoteKeyStory,
            MultiplayerSession.VoteKeyInnBiome,
            MultiplayerSession.VoteKeyInnEmbark,
            MultiplayerSession.VoteKeyLairDecision,
            MultiplayerSession.VoteKeyConfirmationDialog,
            MultiplayerSession.VoteKeyLoot,
            MultiplayerSession.VoteKeyGameResults,
            MultiplayerSession.VoteKeyEmbarkContinue,
            MultiplayerSession.VoteKeyAltarEmbark,
            MultiplayerSession.VoteKeyConfessionChoice,
            MultiplayerSession.VoteKeyHeroReady,
            MultiplayerSession.VoteKeyMainMenu,
        };

        private readonly Dictionary<string, List<Rect>> _anchorsByChoice =
            new Dictionary<string, List<Rect>>(StringComparer.OrdinalIgnoreCase);
        private float _nextAnchorRefreshTime;
        private string _anchorVoteKey;
        private string _anchorDigest;

        public bool HasDrawableVote(MultiplayerSession session, SteamLobbyClient lobby)
        {
            HostVoteDisplayModel model;
            return TryBuildDisplayModel(session, lobby, false, out model) && model != null && model.IsActive;
        }

        public void Draw(MultiplayerSession session, SteamLobbyClient lobby, bool isChineseUi)
        {
            HostVoteDisplayModel model;
            if (!TryBuildDisplayModel(session, lobby, isChineseUi, out model) || model == null || !model.IsActive)
            {
                return;
            }

            RefreshAnchorsIfNeeded(model);
            int anchoredVoteCount = DrawAnchoredVotes(model);
            if (anchoredVoteCount == 0 || model.Waiting.Count > 0 || model.IsResolved)
            {
                DrawSummaryPanel(model, anchoredVoteCount == 0);
            }
        }

        private static bool TryBuildDisplayModel(
            MultiplayerSession session,
            SteamLobbyClient lobby,
            bool isChineseUi,
            out HostVoteDisplayModel model)
        {
            model = null;
            if (session == null || lobby == null || !lobby.IsInLobby || !lobby.IsHost)
            {
                return false;
            }

            CurrentInteractionSnapshotPayload interaction;
            session.TryGetLatestCurrentInteractionSnapshot(out interaction);
            List<string> voteKeys = BuildVoteKeyPriority(interaction);
            VoteStatusPayload status = null;
            string selectedKey = null;
            foreach (string voteKey in voteKeys)
            {
                VoteStatusPayload candidate;
                if (session.TryGetLatestVoteStatus(voteKey, out candidate) && candidate != null && candidate.IsActive)
                {
                    status = candidate;
                    selectedKey = voteKey;
                    if (!candidate.IsResolved)
                    {
                        break;
                    }
                }
            }

            if (status == null)
            {
                return false;
            }

            string label = ResolveInteractionLabel(interaction, selectedKey, isChineseUi);
            model = new HostVoteDisplayModel
            {
                VoteKey = status.VoteKey,
                Label = label,
                ContextDigest = status.ContextDigest,
                IsActive = status.IsActive,
                IsResolved = status.IsResolved,
                Resolution = status.Resolution,
                VotedCount = status.VotedCount,
                RequiredCount = status.RequiredCount,
                IsChineseUi = isChineseUi,
            };

            foreach (VoteEntryPayload vote in status.Votes ?? Array.Empty<VoteEntryPayload>())
            {
                if (vote == null)
                {
                    continue;
                }

                model.Choices.Add(new HostVoteChoiceDisplay
                {
                    SteamId = vote.SteamId,
                    PlayerName = ShortPlayerName(vote.Name, vote.SteamId),
                    RawChoice = vote.Choice,
                    ChoiceKey = NormalizeChoiceKey(status.VoteKey, vote.Choice),
                    DisplayChoice = FormatDisplayChoice(status.VoteKey, vote.Choice, isChineseUi),
                    PlayerColor = GetPlayerColor(vote.SteamId),
                });
            }

            foreach (VoteEntryPayload waiting in status.Missing ?? Array.Empty<VoteEntryPayload>())
            {
                if (waiting == null)
                {
                    continue;
                }

                model.Waiting.Add(new HostVoteChoiceDisplay
                {
                    SteamId = waiting.SteamId,
                    PlayerName = ShortPlayerName(waiting.Name, waiting.SteamId),
                    RawChoice = "[waiting]",
                    ChoiceKey = "[waiting]",
                    DisplayChoice = Ui(isChineseUi, "Waiting", "等待中"),
                    PlayerColor = new Color(0.36f, 0.39f, 0.42f, 1f),
                });
            }

            return true;
        }

        private static List<string> BuildVoteKeyPriority(CurrentInteractionSnapshotPayload interaction)
        {
            List<string> keys = new List<string>();
            if (interaction != null && interaction.IsActive)
            {
                AddKey(keys, interaction.PrimaryVoteKey);
                foreach (string key in interaction.ActiveVoteKeys ?? Array.Empty<string>())
                {
                    AddKey(keys, key);
                }

                foreach (CurrentInteractionItemPayload item in interaction.Items ?? Array.Empty<CurrentInteractionItemPayload>())
                {
                    AddKey(keys, item == null ? null : item.VoteKey);
                }
            }

            foreach (string key in KnownVoteKeys)
            {
                AddKey(keys, key);
            }

            return keys;
        }

        private static void AddKey(List<string> keys, string key)
        {
            if (keys == null || string.IsNullOrWhiteSpace(key) || keys.Contains(key))
            {
                return;
            }

            keys.Add(key);
        }

        private static string ResolveInteractionLabel(CurrentInteractionSnapshotPayload interaction, string voteKey, bool isChineseUi)
        {
            if (interaction != null &&
                string.Equals(interaction.PrimaryVoteKey, voteKey, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(interaction.PrimaryLabel))
            {
                return interaction.PrimaryLabel;
            }

            if (interaction != null && interaction.Items != null)
            {
                CurrentInteractionItemPayload item = interaction.Items.FirstOrDefault(candidate =>
                    candidate != null && string.Equals(candidate.VoteKey, voteKey, StringComparison.Ordinal));
                if (item != null && !string.IsNullOrWhiteSpace(item.Label))
                {
                    return item.Label;
                }
            }

            return FormatVoteKey(voteKey, isChineseUi);
        }

        private void RefreshAnchorsIfNeeded(HostVoteDisplayModel model)
        {
            string digest = model.VoteKey + ":" + (model.ContextDigest ?? string.Empty);
            if (Time.unscaledTime < _nextAnchorRefreshTime &&
                string.Equals(_anchorVoteKey, model.VoteKey, StringComparison.Ordinal) &&
                string.Equals(_anchorDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _nextAnchorRefreshTime = Time.unscaledTime + AnchorRefreshInterval;
            _anchorVoteKey = model.VoteKey;
            _anchorDigest = digest;
            _anchorsByChoice.Clear();

            if (string.Equals(model.VoteKey, MultiplayerSession.VoteKeyRoute, StringComparison.Ordinal))
            {
                CollectRouteAnchors();
            }
            else if (string.Equals(model.VoteKey, MultiplayerSession.VoteKeyStory, StringComparison.Ordinal))
            {
                CollectStoryAnchors();
            }
            else if (string.Equals(model.VoteKey, MultiplayerSession.VoteKeyInnBiome, StringComparison.Ordinal))
            {
                CollectInnBiomeAnchors();
            }
            else if (string.Equals(model.VoteKey, MultiplayerSession.VoteKeyInnEmbark, StringComparison.Ordinal) ||
                string.Equals(model.VoteKey, MultiplayerSession.VoteKeyConfirmationDialog, StringComparison.Ordinal))
            {
                CollectConfirmationAnchors();
            }
            else if (string.Equals(model.VoteKey, MultiplayerSession.VoteKeyLairDecision, StringComparison.Ordinal))
            {
                CollectLairAnchors();
            }
        }

        private int DrawAnchoredVotes(HostVoteDisplayModel model)
        {
            int drawn = 0;
            foreach (IGrouping<string, HostVoteChoiceDisplay> group in model.Choices.GroupBy(choice => choice.ChoiceKey ?? string.Empty))
            {
                List<Rect> anchors = FindAnchors(group.Key);
                if (anchors.Count == 0)
                {
                    continue;
                }

                List<HostVoteChoiceDisplay> choices = group.ToList();
                for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
                {
                    Rect anchor = anchors[anchorIndex];
                    for (int i = 0; i < choices.Count; i++)
                    {
                        DrawVoteBadge(anchor, choices[i], i, model.IsResolved);
                        drawn++;
                    }
                }
            }

            return drawn;
        }

        private List<Rect> FindAnchors(string choiceKey)
        {
            List<Rect> anchors;
            if (!string.IsNullOrWhiteSpace(choiceKey) && _anchorsByChoice.TryGetValue(choiceKey, out anchors))
            {
                return anchors;
            }

            string optionKey = ExtractOptionChoiceKey(choiceKey);
            if (!string.IsNullOrWhiteSpace(optionKey) && _anchorsByChoice.TryGetValue(optionKey, out anchors))
            {
                return anchors;
            }

            string actionKey = ExtractActionChoiceKey(choiceKey);
            if (!string.IsNullOrWhiteSpace(actionKey) && _anchorsByChoice.TryGetValue(actionKey, out anchors))
            {
                return anchors;
            }

            return new List<Rect>();
        }

        private void DrawVoteBadge(Rect anchor, HostVoteChoiceDisplay choice, int stackIndex, bool resolved)
        {
            string text = choice.PlayerName;
            float width = Mathf.Clamp(54f + text.Length * 7f, 96f, 176f);
            float x = Mathf.Clamp(anchor.center.x - width * 0.5f, 8f, Screen.width - width - 8f);
            float y = anchor.y - (BadgeHeight + BadgeGap) * (stackIndex + 1) - 4f;
            if (y < 8f)
            {
                y = anchor.yMax + 6f + (BadgeHeight + BadgeGap) * stackIndex;
            }

            Rect rect = new Rect(x, y, width, BadgeHeight);
            Color color = resolved
                ? new Color(choice.PlayerColor.r * 0.75f, choice.PlayerColor.g * 0.75f, choice.PlayerColor.b * 0.75f, 0.92f)
                : choice.PlayerColor;
            DrawSolidRect(rect, color);
            DrawRectBorder(rect, new Color(0f, 0f, 0f, 0.72f), 1f);
            GUI.Label(rect, text, CreateLabelStyle(12, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter));
        }

        private void DrawSummaryPanel(HostVoteDisplayModel model, bool fallback)
        {
            int rowCount = 1 + model.Choices.Count + model.Waiting.Count + (string.IsNullOrWhiteSpace(model.Resolution) ? 0 : 1);
            float height = Mathf.Clamp(40f + rowCount * 22f, 74f, 260f);
            Rect panel = new Rect(Screen.width - PanelWidth - 22f, 84f, PanelWidth, height);
            DrawSolidRect(panel, new Color(0.055f, 0.065f, 0.075f, 0.92f));
            DrawRectBorder(panel, new Color(0.30f, 0.35f, 0.40f, 0.95f), 1f);

            string title = (fallback ? Ui(model.IsChineseUi, "Vote", "投票") : Ui(model.IsChineseUi, "Vote Status", "投票状态")) +
                ": " + (model.Label ?? FormatVoteKey(model.VoteKey, model.IsChineseUi));
            GUI.Label(new Rect(panel.x + 12f, panel.y + 8f, panel.width - 24f, 22f),
                title + "  " + model.VotedCount + "/" + model.RequiredCount,
                CreateLabelStyle(14, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft));

            float y = panel.y + 34f;
            foreach (HostVoteChoiceDisplay choice in model.Choices)
            {
                DrawSummaryRow(new Rect(panel.x + 12f, y, panel.width - 24f, 20f), choice, false);
                y += 22f;
            }

            foreach (HostVoteChoiceDisplay waiting in model.Waiting)
            {
                DrawSummaryRow(new Rect(panel.x + 12f, y, panel.width - 24f, 20f), waiting, true);
                y += 22f;
            }

            if (!string.IsNullOrWhiteSpace(model.Resolution))
            {
                GUI.Label(new Rect(panel.x + 12f, y, panel.width - 24f, 34f),
                    TrimText(model.Resolution, 92),
                    CreateLabelStyle(11, FontStyle.Normal, new Color(0.88f, 0.91f, 0.94f, 1f), TextAnchor.UpperLeft));
            }
        }

        private static void DrawSummaryRow(Rect row, HostVoteChoiceDisplay choice, bool waiting)
        {
            Rect dot = new Rect(row.x, row.y + 4f, 12f, 12f);
            DrawSolidRect(dot, choice.PlayerColor);
            DrawRectBorder(dot, new Color(0f, 0f, 0f, 0.65f), 1f);
            string text = choice.PlayerName + ": " + choice.DisplayChoice;
            Color color = waiting ? new Color(0.64f, 0.68f, 0.72f, 1f) : Color.white;
            GUI.Label(new Rect(row.x + 18f, row.y, row.width - 18f, row.height),
                TrimText(text, 56),
                CreateLabelStyle(12, FontStyle.Normal, color, TextAnchor.MiddleLeft));
        }

        private void CollectRouteAnchors()
        {
            foreach (RoadIndicatorUIBhv indicator in UnityObject.FindObjectsOfType<RoadIndicatorUIBhv>(true))
            {
                if (indicator == null || indicator.gameObject == null || !indicator.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!GetPrivateFieldValue(indicator, "m_hoverable", false))
                {
                    continue;
                }

                int optionIndex = GetPrivateFieldValue(indicator, "m_intersectionOptionIndex", -1);
                Rect rect;
                if (optionIndex >= 0 && TryGetGuiRect(indicator.gameObject, out rect))
                {
                    AddAnchor("option:" + optionIndex.ToString(CultureInfo.InvariantCulture), rect);
                }
            }
        }

        private void CollectStoryAnchors()
        {
            List<StoryChoiceButtonBhv> buttons = new List<StoryChoiceButtonBhv>();
            StoryScreenBhv screen = UnityObject.FindObjectsOfType<StoryScreenBhv>(true)
                .FirstOrDefault(candidate => candidate != null && candidate.gameObject != null && candidate.gameObject.activeInHierarchy);
            if (screen != null)
            {
                buttons = GetPrivateFieldValue<List<StoryChoiceButtonBhv>>(screen, "m_StoryChoiceButtons", null) ??
                    screen.GetComponentsInChildren<StoryChoiceButtonBhv>(true).ToList();
            }

            if (buttons.Count == 0)
            {
                buttons = UnityObject.FindObjectsOfType<StoryChoiceButtonBhv>(true)
                    .Where(button => button != null && button.gameObject != null && button.gameObject.activeInHierarchy)
                    .ToList();
            }

            for (int i = 0; i < buttons.Count; i++)
            {
                StoryChoiceButtonBhv button = buttons[i];
                if (button == null || button.gameObject == null || !button.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Rect rect;
                if (!TryGetStoryChoiceAnchorRect(button, out rect))
                {
                    continue;
                }

                AddAnchor("option:" + i.ToString(CultureInfo.InvariantCulture), rect);
                uint actorGuid = SafeGetStoryActorGuid(button);
                if (actorGuid != 0U)
                {
                    AddAnchor("actor:" + actorGuid.ToString(CultureInfo.InvariantCulture), rect);
                }
            }
        }

        private void CollectInnBiomeAnchors()
        {
            foreach (BiomeChoiceBhv choice in UnityObject.FindObjectsOfType<BiomeChoiceBhv>(true))
            {
                if (choice == null || choice.gameObject == null || !choice.gameObject.activeInHierarchy)
                {
                    continue;
                }

                int index = GetPrivateFieldValue(choice, "m_index", -1);
                Rect rect;
                if (index >= 0 && TryGetGuiRect(choice.gameObject, out rect))
                {
                    AddAnchor("option:" + index.ToString(CultureInfo.InvariantCulture), rect);
                }
            }
        }

        private static bool TryGetStoryChoiceAnchorRect(StoryChoiceButtonBhv button, out Rect rect)
        {
            rect = default(Rect);
            if (button == null || button.gameObject == null)
            {
                return false;
            }

            ActorStatusUiBhv statusBars = GetPrivateFieldValue<ActorStatusUiBhv>(button, "m_statusBars", null);
            if (TryGetStoryStatusAnchorRect(statusBars, out rect))
            {
                return true;
            }

            Selectable selectable = SafeGetSelectable(button);
            if (TryGetComponentGuiRect(selectable, out rect) && IsUsableStoryAnchorRect(rect))
            {
                rect = TrimLargeStoryAnchorRect(rect);
                return true;
            }

            Image holdSelectFill = GetPrivateFieldValue<Image>(button, "m_holdSelectFillImage", null);
            if (TryGetComponentGuiRect(holdSelectFill, out rect) && IsUsableStoryAnchorRect(rect))
            {
                return true;
            }

            if (TryGetGuiRect(button.gameObject, out rect))
            {
                rect = TrimLargeStoryAnchorRect(rect);
                return true;
            }

            return false;
        }

        private static bool TryGetStoryStatusAnchorRect(ActorStatusUiBhv statusBars, out Rect rect)
        {
            rect = default(Rect);
            if (statusBars == null || statusBars.gameObject == null || !statusBars.gameObject.activeInHierarchy)
            {
                return false;
            }

            StatusBarBhv healthBar = GetPrivateFieldValue<StatusBarBhv>(statusBars, "m_healthBar", null);
            if (TryGetComponentGuiRect(healthBar, out rect) && IsUsableStoryAnchorRect(rect))
            {
                return true;
            }

            if (TryGetGuiRect(statusBars.gameObject, out rect) && IsUsableStoryAnchorRect(rect))
            {
                rect = TrimLargeStoryAnchorRect(rect);
                return true;
            }

            return false;
        }

        private static bool TryGetComponentGuiRect(Component component, out Rect rect)
        {
            rect = default(Rect);
            if (component == null || component.gameObject == null || !component.gameObject.activeInHierarchy)
            {
                return false;
            }

            return TryGetGuiRect(component.gameObject, out rect);
        }

        private static bool IsUsableStoryAnchorRect(Rect rect)
        {
            return rect.width >= 12f &&
                rect.height >= 12f &&
                rect.xMax >= 0f &&
                rect.x <= Screen.width &&
                rect.yMax >= 0f &&
                rect.y <= Screen.height;
        }

        private static Rect TrimLargeStoryAnchorRect(Rect rect)
        {
            float maxWidth = Mathf.Min(150f, Mathf.Max(42f, Screen.width * 0.10f));
            float maxHeight = Mathf.Min(150f, Mathf.Max(42f, Screen.height * 0.16f));
            if (rect.width <= maxWidth && rect.height <= maxHeight)
            {
                return rect;
            }

            float width = Mathf.Min(rect.width, maxWidth);
            float height = Mathf.Min(rect.height, maxHeight);
            return new Rect(
                rect.center.x - width * 0.5f,
                rect.center.y - height * 0.5f,
                width,
                height);
        }

        private static Selectable SafeGetSelectable(StoryChoiceButtonBhv button)
        {
            try
            {
                return button == null ? null : button.Selectable;
            }
            catch
            {
                return null;
            }
        }

        private void CollectConfirmationAnchors()
        {
            ConfirmationDialogBhv dialog = UnityObject.FindObjectsOfType<ConfirmationDialogBhv>(true)
                .FirstOrDefault(candidate => candidate != null && candidate.gameObject != null && candidate.gameObject.activeInHierarchy);
            if (dialog == null)
            {
                return;
            }

            AddGameObjectAnchor("confirm", GetPrivateFieldValue<GameObject>(dialog, "m_AcceptBtn", null));
            AddGameObjectAnchor("decline", GetPrivateFieldValue<GameObject>(dialog, "m_DeclineBtn", null));
            AddGameObjectAnchor("cancel", GetPrivateFieldValue<GameObject>(dialog, "m_DeclineBtn", null));
            AddGameObjectAnchor("embark", GetPrivateFieldValue<GameObject>(dialog, "m_AcceptBtn", null));
        }

        private void CollectLairAnchors()
        {
            DungeonConfirmationDialogBhv dialog = UnityObject.FindObjectsOfType<DungeonConfirmationDialogBhv>(true)
                .FirstOrDefault(candidate => candidate != null && candidate.gameObject != null && candidate.gameObject.activeInHierarchy);
            if (dialog == null)
            {
                return;
            }

            AddGameObjectAnchor("continue", GetPrivateFieldValue<GameObject>(dialog, "m_continueButton", null));
            AddGameObjectAnchor("retreat", GetPrivateFieldValue<GameObject>(dialog, "m_declineBtn", null));
            AddGameObjectAnchor("decline", GetPrivateFieldValue<GameObject>(dialog, "m_declineBtn", null));
        }

        private void AddGameObjectAnchor(string key, GameObject target)
        {
            Rect rect;
            if (target != null && target.activeInHierarchy && TryGetGuiRect(target, out rect))
            {
                AddAnchor(key, rect);
            }
        }

        private void AddAnchor(string key, Rect rect)
        {
            if (string.IsNullOrWhiteSpace(key) || rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            List<Rect> anchors;
            if (!_anchorsByChoice.TryGetValue(key, out anchors))
            {
                anchors = new List<Rect>();
                _anchorsByChoice[key] = anchors;
            }

            anchors.Add(rect);
        }

        private static bool TryGetGuiRect(GameObject obj, out Rect rect)
        {
            rect = default(Rect);
            RectTransform transform = obj == null ? null : obj.GetComponent<RectTransform>();
            if (transform == null)
            {
                return false;
            }

            Vector3[] corners = new Vector3[4];
            transform.GetWorldCorners(corners);
            Camera camera = null;
            Canvas canvas = transform.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.worldCamera == null ? Camera.main : canvas.worldCamera;
            }

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                minX = Mathf.Min(minX, screen.x);
                minY = Mathf.Min(minY, screen.y);
                maxX = Mathf.Max(maxX, screen.x);
                maxY = Mathf.Max(maxY, screen.y);
            }

            if (maxX <= minX || maxY <= minY)
            {
                return false;
            }

            rect = new Rect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
            return rect.xMax >= 0f && rect.x <= Screen.width && rect.yMax >= 0f && rect.y <= Screen.height;
        }

        private static uint SafeGetStoryActorGuid(StoryChoiceButtonBhv button)
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

        private static T GetPrivateFieldValue<T>(object instance, string fieldName, T defaultValue)
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return defaultValue;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return defaultValue;
            }

            object value = field.GetValue(instance);
            return value is T ? (T)value : defaultValue;
        }

        private static string NormalizeChoiceKey(string voteKey, string choice)
        {
            string raw = (choice ?? string.Empty).Trim();
            if (string.Equals(voteKey, MultiplayerSession.VoteKeyRoute, StringComparison.Ordinal))
            {
                string option = ExtractOptionChoiceKey(raw);
                return string.IsNullOrWhiteSpace(option) ? raw.ToLowerInvariant() : option;
            }

            if (string.Equals(voteKey, MultiplayerSession.VoteKeyStory, StringComparison.Ordinal))
            {
                string[] parts = raw.Split('/');
                if (parts.Length >= 3)
                {
                    return "option:" + parts[0] + "|slot:" + parts[1] + "|actor:" + parts[2];
                }

                return raw.ToLowerInvariant();
            }

            if (string.Equals(voteKey, MultiplayerSession.VoteKeyInnBiome, StringComparison.Ordinal))
            {
                string option = ExtractOptionChoiceKey(raw);
                return string.IsNullOrWhiteSpace(option) ? raw.ToLowerInvariant() : option;
            }

            return ExtractActionChoiceKey(raw) ?? raw.ToLowerInvariant();
        }

        private static string ExtractOptionChoiceKey(string choiceKey)
        {
            if (string.IsNullOrWhiteSpace(choiceKey))
            {
                return null;
            }

            Match match = RouteOptionRegex.Match(choiceKey);
            if (!match.Success)
            {
                match = ActionOptionRegex.Match(choiceKey);
            }

            if (!match.Success)
            {
                string[] parts = choiceKey.Split('/');
                int parsed;
                if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return "option:" + parsed.ToString(CultureInfo.InvariantCulture);
                }

                return null;
            }

            return "option:" + match.Groups[1].Value;
        }

        private static string ExtractActionChoiceKey(string choiceKey)
        {
            if (string.IsNullOrWhiteSpace(choiceKey))
            {
                return null;
            }

            string text = choiceKey.Trim().ToLowerInvariant();
            if (text.Contains(":"))
            {
                text = text.Substring(0, text.IndexOf(":", StringComparison.Ordinal));
            }

            if (text.Contains("("))
            {
                text = text.Substring(0, text.IndexOf("(", StringComparison.Ordinal));
            }

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string FormatDisplayChoice(string voteKey, string choice, bool isChineseUi)
        {
            string raw = choice ?? "[none]";
            string option = ExtractOptionChoiceKey(raw);
            if (!string.IsNullOrWhiteSpace(option))
            {
                string index = option.Substring("option:".Length);
                if (string.Equals(voteKey, MultiplayerSession.VoteKeyInnBiome, StringComparison.Ordinal))
                {
                    return Ui(isChineseUi, "Biome ", "区域 ") + index;
                }

                return Ui(isChineseUi, "Option ", "选项 ") + index;
            }

            if (string.Equals(raw, "confirm", StringComparison.OrdinalIgnoreCase))
            {
                return Ui(isChineseUi, "Confirm", "确认");
            }

            if (string.Equals(raw, "decline", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                return Ui(isChineseUi, "Decline", "拒绝");
            }

            if (string.Equals(raw, "continue", StringComparison.OrdinalIgnoreCase))
            {
                return Ui(isChineseUi, "Continue", "继续");
            }

            if (string.Equals(raw, "retreat", StringComparison.OrdinalIgnoreCase))
            {
                return Ui(isChineseUi, "Retreat", "撤退");
            }

            if (string.Equals(raw, "embark", StringComparison.OrdinalIgnoreCase))
            {
                return Ui(isChineseUi, "Embark", "启程");
            }

            return TrimText(Humanize(raw), 36);
        }

        private static string FormatVoteKey(string voteKey, bool isChineseUi)
        {
            switch (voteKey ?? string.Empty)
            {
                case MultiplayerSession.VoteKeyRoute:
                    return Ui(isChineseUi, "Route", "路线");
                case MultiplayerSession.VoteKeyStory:
                    return Ui(isChineseUi, "Story", "故事");
                case MultiplayerSession.VoteKeyInnBiome:
                    return Ui(isChineseUi, "Inn Biome", "旅馆选图");
                case MultiplayerSession.VoteKeyInnEmbark:
                    return Ui(isChineseUi, "Inn Embark", "旅馆启程");
                case MultiplayerSession.VoteKeyLairDecision:
                    return Ui(isChineseUi, "Lair Decision", "巢穴抉择");
                case MultiplayerSession.VoteKeyConfirmationDialog:
                    return Ui(isChineseUi, "Dialog", "弹窗");
                case MultiplayerSession.VoteKeyLoot:
                    return Ui(isChineseUi, "Loot", "战利品");
                case MultiplayerSession.VoteKeyHeroReady:
                    return Ui(isChineseUi, "Hero Ready", "选人确认");
                case MultiplayerSession.VoteKeyMainMenu:
                    return Ui(isChineseUi, "Main Menu", "主菜单");
                default:
                    return Humanize(voteKey);
            }
        }

        private static string ShortPlayerName(string name, ulong steamId)
        {
            string value = string.IsNullOrWhiteSpace(name) ? steamId.ToString(CultureInfo.InvariantCulture) : name.Trim();
            int slash = value.IndexOf('/');
            if (slash > 0)
            {
                value = value.Substring(0, slash);
            }

            return TrimText(value, 14);
        }

        private static Color GetPlayerColor(ulong steamId)
        {
            float hue = (steamId % 360UL) / 360f;
            Color color = Color.HSVToRGB(hue, 0.58f, 0.84f);
            color.a = 0.94f;
            return color;
        }

        private static string Humanize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "[none]";
            }

            string value = text.Replace('_', ' ').Replace('-', ' ');
            value = Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
        }

        private static string TrimText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || maxLength <= 0 || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        private static string Ui(bool isChineseUi, string english, string chinese)
        {
            return isChineseUi ? chinese : english;
        }

        private static GUIStyle CreateLabelStyle(int fontSize, FontStyle fontStyle, Color color, TextAnchor alignment)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                fontStyle = fontStyle,
                alignment = alignment,
                wordWrap = true,
            };
            style.normal.textColor = color;
            return style;
        }

        private static void DrawSolidRect(Rect rect, Color color)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = oldColor;
        }

        private static void DrawRectBorder(Rect rect, Color color, float thickness)
        {
            DrawSolidRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawSolidRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawSolidRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawSolidRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private sealed class HostVoteDisplayModel
        {
            public HostVoteDisplayModel()
            {
                Choices = new List<HostVoteChoiceDisplay>();
                Waiting = new List<HostVoteChoiceDisplay>();
            }

            public string VoteKey { get; set; }

            public string Label { get; set; }

            public string ContextDigest { get; set; }

            public bool IsActive { get; set; }

            public bool IsResolved { get; set; }

            public string Resolution { get; set; }

            public int VotedCount { get; set; }

            public int RequiredCount { get; set; }

            public bool IsChineseUi { get; set; }

            public List<HostVoteChoiceDisplay> Choices { get; }

            public List<HostVoteChoiceDisplay> Waiting { get; }
        }

        private sealed class HostVoteChoiceDisplay
        {
            public ulong SteamId { get; set; }

            public string PlayerName { get; set; }

            public string RawChoice { get; set; }

            public string ChoiceKey { get; set; }

            public string DisplayChoice { get; set; }

            public Color PlayerColor { get; set; }
        }
    }
}
