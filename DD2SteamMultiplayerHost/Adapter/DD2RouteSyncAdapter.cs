using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Map.Events;
using Assets.Code.UI;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2RouteSyncAdapter : IRouteChoiceAdapter, IDisposable
    {
        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;
        private int _lastPathCount;
        private int _lastSelectedOptionIndex = -1;

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
                    HostLog.Write("[route] EventManager is not ready; route sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventIntersectionCalculatedChoices>(HandleEventIntersectionCalculatedChoices, false, 0);
            EventManager.AddListener<EventRoadIntersectionOptionSelected>(HandleEventRoadIntersectionOptionSelected, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[route] Intersection route listeners registered.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventIntersectionCalculatedChoices>(HandleEventIntersectionCalculatedChoices);
            EventManager.RemoveListener<EventRoadIntersectionOptionSelected>(HandleEventRoadIntersectionOptionSelected);
            _listenersRegistered = false;
        }

        public bool TryGetRouteChoiceSnapshot(out RouteChoiceSnapshotPayload snapshot)
        {
            try
            {
                if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
                {
                    snapshot = CreateInactiveRouteSnapshot();
                    return true;
                }

                List<RouteChoiceRuntimeOption> options = GetActiveRuntimeOptions();
                if (options.Count == 0)
                {
                    snapshot = CreateInactiveRouteSnapshot();
                    return true;
                }

                List<RouteChoiceOptionPayload> choices = options
                    .Select(option => option.Payload)
                    .OrderBy(option => option.OptionIndex)
                    .ToList();

                int choiceCount = Math.Max(_lastPathCount, choices.Count == 0 ? 0 : choices.Max(choice => choice.OptionIndex) + 1);
                snapshot = new RouteChoiceSnapshotPayload
                {
                    IsActive = true,
                    ChoiceCount = choiceCount,
                    SelectedOptionIndex = _lastSelectedOptionIndex,
                    Choices = choices,
                };
                snapshot.Digest = ComputeRouteChoiceDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[route] Failed to collect route choice snapshot: " + ex.Message + ".");
                snapshot = null;
                return false;
            }
        }

        public bool TryExecuteRouteChoice(
            RouteChoiceRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null)
            {
                message = "empty route choice request";
                return false;
            }

            if (request.OptionIndex < 0)
            {
                message = "route option must be >= 0";
                return false;
            }

            List<RouteChoiceRuntimeOption> options = GetActiveRuntimeOptions();
            RouteChoiceRuntimeOption option = options.FirstOrDefault(candidate => candidate.Payload.OptionIndex == request.OptionIndex);
            if (option == null)
            {
                message = "route option " + request.OptionIndex + " is not active";
                return false;
            }

            try
            {
                HostLog.Write("[route-action] " + senderName + "/" + senderSteamId +
                    " requested option " + request.OptionIndex +
                    " (" + (option.Payload.NodeType ?? "[unknown]") + ").");
                option.Indicator.OnClick();
                message = "route option " + request.OptionIndex + " invoked on host";
                return true;
            }
            catch (Exception ex)
            {
                message = "route option failed: " + ex.Message;
                HostLog.Write("[route-action] " + message + ".");
                return false;
            }
        }

        private void HandleEventIntersectionCalculatedChoices(EventIntersectionCalculatedChoices evt)
        {
            if (evt == null)
            {
                return;
            }

            _lastPathCount = evt.m_pathCount;
            _lastSelectedOptionIndex = -1;
            HostLog.Write("[route] intersection choices calculated: count=" + evt.m_pathCount + ".");
        }

        private void HandleEventRoadIntersectionOptionSelected(EventRoadIntersectionOptionSelected evt)
        {
            if (evt == null)
            {
                return;
            }

            _lastSelectedOptionIndex = evt.m_IntersectionOptionIndex;
            HostLog.Write("[route] selected option=" + evt.m_IntersectionOptionIndex +
                ", nodeIndexInRow=" + evt.m_NodeIndexInRow + ".");
        }

        private static List<RouteChoiceRuntimeOption> GetActiveRuntimeOptions()
        {
            RoadIndicatorUIBhv[] indicators = UnityObject.FindObjectsOfType<RoadIndicatorUIBhv>(true);
            List<RouteChoiceRuntimeOption> options = new List<RouteChoiceRuntimeOption>();
            for (int i = 0; i < indicators.Length; i++)
            {
                RoadIndicatorUIBhv indicator = indicators[i];
                if (indicator == null || indicator.gameObject == null || !indicator.gameObject.activeInHierarchy)
                {
                    continue;
                }

                bool hoverable = GetPrivateFieldValue(indicator, "m_hoverable", false);
                if (!hoverable)
                {
                    continue;
                }

                int optionIndex = GetPrivateFieldValue(indicator, "m_intersectionOptionIndex", -1);
                if (optionIndex < 0)
                {
                    continue;
                }

                int nodeIndexInRow = GetPrivateFieldValue(indicator, "m_nodeIndexInRow", -1);
                bool isRevealed = SafeGetIsRevealed(indicator);
                string nodeType = isRevealed ? SafeGetNodeType(indicator) : "Unknown";
                string nodeSubType = isRevealed ? SafeGetNodeSubType(indicator) : null;
                string direction = Convert.ToString(GetPrivateFieldObject(indicator, "m_indicatorDirection"));

                options.Add(new RouteChoiceRuntimeOption(
                    indicator,
                    new RouteChoiceOptionPayload(optionIndex, nodeIndexInRow, nodeType, nodeSubType, isRevealed, direction)));
            }

            return options
                .GroupBy(option => option.Payload.OptionIndex)
                .Select(group => group.OrderBy(option => option.Payload.NodeIndexInRow).First())
                .OrderBy(option => option.Payload.OptionIndex)
                .ToList();
        }

        private static bool SafeGetIsRevealed(RoadIndicatorUIBhv indicator)
        {
            try
            {
                return indicator != null && indicator.IsRevealed();
            }
            catch
            {
                return false;
            }
        }

        private static string SafeGetNodeType(RoadIndicatorUIBhv indicator)
        {
            try
            {
                return indicator.GetNodeType() == null ? "[unknown]" : indicator.GetNodeType().GetName();
            }
            catch
            {
                return "[unknown]";
            }
        }

        private static string SafeGetNodeSubType(RoadIndicatorUIBhv indicator)
        {
            try
            {
                return indicator.GetNodeSubType();
            }
            catch
            {
                return null;
            }
        }

        private static RouteChoiceSnapshotPayload CreateInactiveRouteSnapshot()
        {
            return new RouteChoiceSnapshotPayload
            {
                IsActive = false,
                ChoiceCount = 0,
                SelectedOptionIndex = -1,
                Digest = "route-inactive",
            };
        }

        private static T GetPrivateFieldValue<T>(object instance, string fieldName, T defaultValue)
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

        private static string ComputeRouteChoiceDigest(RouteChoiceSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.ChoiceCount + ":" +
                snapshot.SelectedOptionIndex + ":" +
                string.Join("|", (snapshot.Choices ?? Array.Empty<RouteChoiceOptionPayload>())
                    .OrderBy(choice => choice.OptionIndex)
                    .Select(choice =>
                        choice.OptionIndex + "," +
                        choice.NodeIndexInRow + "," +
                        choice.NodeType + "," +
                        choice.NodeSubType + "," +
                        choice.IsRevealed + "," +
                        choice.Direction)
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

        private sealed class RouteChoiceRuntimeOption
        {
            public RouteChoiceRuntimeOption(RoadIndicatorUIBhv indicator, RouteChoiceOptionPayload payload)
            {
                Indicator = indicator;
                Payload = payload;
            }

            public RoadIndicatorUIBhv Indicator { get; }

            public RouteChoiceOptionPayload Payload { get; }
        }
    }
}
