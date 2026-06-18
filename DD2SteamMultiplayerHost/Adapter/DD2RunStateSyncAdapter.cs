using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Code.Actor;
using Assets.Code.Events;
using Assets.Code.Game;
using Assets.Code.Game.Events;
using Assets.Code.Map;
using Assets.Code.Map.Events;
using Assets.Code.Map.Generation;
using Assets.Code.Map.Generation.Biome;
using Assets.Code.Run;
using Assets.Code.Run.Events;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2RunStateSyncAdapter : IDisposable
    {
        private bool _listenersRegistered;
        private bool _eventManagerMissingLogged;

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
                    HostLog.Write("[run-state] EventManager is not ready; run state sync will retry.");
                }

                return;
            }

            EventManager.AddListener<EventGameTypeStarted>(HandleEventGameTypeStarted, false, 0);
            EventManager.AddListener<EventGameTypeEnded>(HandleEventGameTypeEnded, false, 0);
            EventManager.AddListener<EventRunStarted>(HandleEventRunStarted, false, 0);
            EventManager.AddListener<EventRunEnded>(HandleEventRunEnded, false, 0);
            EventManager.AddListener<EventMapStateChanged>(HandleEventMapStateChanged, false, 0);
            _listenersRegistered = true;
            HostLog.Write("[run-state] Game/run/map state listeners registered.");
        }

        public void Dispose()
        {
            if (!_listenersRegistered)
            {
                return;
            }

            EventManager.RemoveListener<EventGameTypeStarted>(HandleEventGameTypeStarted);
            EventManager.RemoveListener<EventGameTypeEnded>(HandleEventGameTypeEnded);
            EventManager.RemoveListener<EventRunStarted>(HandleEventRunStarted);
            EventManager.RemoveListener<EventRunEnded>(HandleEventRunEnded);
            EventManager.RemoveListener<EventMapStateChanged>(HandleEventMapStateChanged);
            _listenersRegistered = false;
        }

        public bool TryGetRunStateSnapshot(out RunStateSnapshotPayload snapshot)
        {
            snapshot = CreateEmptySnapshot();

            try
            {
                CollectGameMode(snapshot);
                CollectGameTypeAndParty(snapshot);
                CollectRun(snapshot);
                CollectMap(snapshot);
                snapshot.Digest = ComputeRunStateDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[run-state] Failed to collect run state snapshot: " + ex.Message + ".");
                snapshot = null;
                return false;
            }
        }

        private static void CollectGameMode(RunStateSnapshotPayload snapshot)
        {
            try
            {
                snapshot.CurrentGameMode = SafeGetName(GameModeMgr.CurrentMode);
                if (!Singleton<GameModeMgr>.HasInstance())
                {
                    return;
                }

                GameModeMgr gameModeMgr = Singleton<GameModeMgr>.Instance;
                snapshot.IsGameModeChanging = gameModeMgr.IsChangingState();
                snapshot.IsEnteringState = gameModeMgr.IsEnteringState();
                snapshot.IsExitingState = gameModeMgr.IsExitingState();
            }
            catch (Exception ex)
            {
                HostLog.Write("[run-state] GameModeMgr snapshot failed: " + ex.Message + ".");
            }
        }

        private static void CollectGameTypeAndParty(RunStateSnapshotPayload snapshot)
        {
            try
            {
                if (!Singleton<GameTypeMgr>.HasInstance())
                {
                    return;
                }

                GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
                snapshot.CurrentGameType = SafeGetName(gameTypeMgr.CurrentGameType);
                snapshot.IsGameTypeStarted = gameTypeMgr.IsGameTypeStarted;
                snapshot.Party = BuildParty(gameTypeMgr);
            }
            catch (Exception ex)
            {
                HostLog.Write("[run-state] GameTypeMgr snapshot failed: " + ex.Message + ".");
            }
        }

        private static void CollectRun(RunStateSnapshotPayload snapshot)
        {
            try
            {
                if (!SingletonMonoBehaviour<RunBhv>.HasInstance(false))
                {
                    return;
                }

                RunBhv runBhv = SingletonMonoBehaviour<RunBhv>.Instance;
                snapshot.IsRunStarted = runBhv.IsRunStarted;
                snapshot.RunStartType = SafeGetName(runBhv.RunStartType);
            }
            catch (Exception ex)
            {
                HostLog.Write("[run-state] RunBhv snapshot failed: " + ex.Message + ".");
            }
        }

        private static void CollectMap(RunStateSnapshotPayload snapshot)
        {
            try
            {
                if (!SingletonMonoBehaviour<MapMgrBhv>.HasInstance(false))
                {
                    return;
                }

                MapMgrBhv mapMgr = SingletonMonoBehaviour<MapMgrBhv>.Instance;
                snapshot.MapState = Convert.ToString(mapMgr.CurrentState);
                snapshot.IsInDrivingState = mapMgr.IsInDrivingState();

                Map map = mapMgr.GetMap();
                if (map != null)
                {
                    snapshot.BiomeIndex = SafeGetInt(map.GetCurrentBiomeIndex);
                    snapshot.BiomeRowIndex = SafeGetInt(map.GetCurrentBiomeRowIndex);
                    snapshot.LastVisitedBiomeRowIndex = SafeGetInt(map.GetLastVisitedBiomeRowIndex);
                    snapshot.LastVisitedNodeIndex = SafeGetInt(map.GetLastVisitedNodeIndex);
                    snapshot.BiomeType = SafeGetName(map.GetCurrentBiomeType());
                    BiomeType biomeType = map.GetCurrentBiomeType();
                    snapshot.BiomeSubType = biomeType == null ? null : SafeGetName(biomeType.m_SubType);
                    snapshot.LastVisitedNodeType = SafeGetLastVisitedNodeType(map);
                }

                CollectProgress(snapshot, mapMgr);
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "run-state-mapmgr-snapshot-failed",
                    "[run-state] MapMgr snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
            }
        }

        private static void CollectProgress(RunStateSnapshotPayload snapshot, MapMgrBhv mapMgr)
        {
            try
            {
                ProgressInfo progress = mapMgr.GetProgress();
                snapshot.ProgressIsValid = progress.IsValid;
                if (!progress.IsValid)
                {
                    return;
                }

                snapshot.ProgressAtNode = progress.IsAtNode();
                snapshot.ProgressBiomeIndex = progress.GetBiomeIndex();
                snapshot.ProgressRowIndex = progress.GetRowIndex();
                snapshot.ProgressIndex = progress.GetIndex();
                snapshot.ProgressRowCount = progress.GetRowCount();
                snapshot.ProgressBiomeTravelRatio = progress.GetBiomeTravelRatio();
                snapshot.ProgressBetweenRowsRatio = progress.GetMinimapRatioBetweenRows();
                snapshot.ProgressBetweenBiomesRatio = progress.GetMinimapRatioBetweenBiomes();
            }
            catch (Exception ex)
            {
                HostLog.WriteThrottled(
                    "run-state-map-progress-snapshot-failed",
                    "[run-state] Map progress snapshot failed: " + ex.Message + ".",
                    TimeSpan.FromSeconds(15));
            }
        }

        private static IList<RunStatePartyActorPayload> BuildParty(GameTypeMgr gameTypeMgr)
        {
            List<RunStatePartyActorPayload> party = new List<RunStatePartyActorPayload>();
            if (gameTypeMgr == null || gameTypeMgr.RosterManager == null)
            {
                return party;
            }

            IReadOnlyList<ActorInstance> actors = gameTypeMgr.RosterManager.GetPartyActors();
            if (actors == null)
            {
                return party;
            }

            for (int i = 0; i < actors.Count; i++)
            {
                ActorInstance actor = actors[i];
                if (actor == null)
                {
                    continue;
                }

                party.Add(new RunStatePartyActorPayload
                {
                    HeroSlot = actor.TeamPosition >= 0 ? actor.TeamPosition + 1 : 0,
                    ActorGuid = actor.ActorGuid.ToString(),
                    ActorDataId = SafeGetActorDataId(actor),
                    ActorName = SafeGetActorName(actor),
                    PathId = SafeGetActorPathId(actor),
                    TeamPosition = actor.TeamPosition,
                });
            }

            return party
                .OrderBy(actor => actor.TeamPosition)
                .ThenBy(actor => actor.ActorGuid)
                .ToList();
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

        private static string SafeGetLastVisitedNodeType(Map map)
        {
            try
            {
                MapObjectNode node = map.GetLastVisitedNode();
                return node == null || node.GetNodeType() == null ? null : node.GetNodeType().GetName();
            }
            catch
            {
                return null;
            }
        }

        private static int SafeGetInt(Func<int> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return -1;
            }
        }

        private static string SafeGetName<T>(CustomEnum<T> value) where T : CustomEnum<T>
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

        private static RunStateSnapshotPayload CreateEmptySnapshot()
        {
            return new RunStateSnapshotPayload
            {
                CurrentGameMode = "[unknown]",
                BiomeIndex = -1,
                BiomeRowIndex = -1,
                LastVisitedBiomeRowIndex = -1,
                LastVisitedNodeIndex = -1,
                ProgressBiomeIndex = -1,
                ProgressRowIndex = -1,
                ProgressIndex = -1,
                ProgressRowCount = -1,
            };
        }

        private void HandleEventGameTypeStarted(EventGameTypeStarted evt)
        {
            if (evt == null)
            {
                return;
            }

            HostLog.Write("[run-state] game type started: type=" + SafeGetName(evt.m_GameType) +
                ", mode=" + SafeGetName(evt.m_StartingGameModeType) +
                ", load=" + evt.m_IsLoad + ".");
        }

        private void HandleEventGameTypeEnded(EventGameTypeEnded evt)
        {
            if (evt == null)
            {
                return;
            }

            HostLog.Write("[run-state] game type ended: type=" + SafeGetName(evt.m_GameType) + ".");
        }

        private void HandleEventRunStarted(EventRunStarted evt)
        {
            if (evt == null)
            {
                return;
            }

            HostLog.Write("[run-state] run started: type=" + SafeGetName(evt.m_RunStartType) +
                ", mode=" + SafeGetName(evt.m_GameModeType) +
                ", campaignLoad=" + evt.m_IsCampaignLoad +
                ", runLoad=" + evt.m_IsRunLoad +
                ", debugReset=" + evt.m_IsDebugLoaderReset + ".");
        }

        private void HandleEventRunEnded(EventRunEnded evt)
        {
            if (evt == null)
            {
                return;
            }

            HostLog.Write("[run-state] run ended: type=" + SafeGetName(evt.m_RunEndType) +
                ", typicalBiomes=" + evt.m_BiomeTypicalCount + ".");
        }

        private void HandleEventMapStateChanged(EventMapStateChanged evt)
        {
            if (evt == null)
            {
                return;
            }

            HostLog.Write("[run-state] map state changed: " + evt.MapState + ".");
        }

        private static string ComputeRunStateDigest(RunStateSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.CurrentGameMode + ":" +
                snapshot.IsGameModeChanging + ":" +
                snapshot.IsEnteringState + ":" +
                snapshot.IsExitingState + ":" +
                snapshot.CurrentGameType + ":" +
                snapshot.IsGameTypeStarted + ":" +
                snapshot.IsRunStarted + ":" +
                snapshot.RunStartType + ":" +
                snapshot.MapState + ":" +
                snapshot.IsInDrivingState + ":" +
                snapshot.BiomeType + ":" +
                snapshot.BiomeSubType + ":" +
                snapshot.BiomeIndex + ":" +
                snapshot.BiomeRowIndex + ":" +
                snapshot.LastVisitedBiomeRowIndex + ":" +
                snapshot.LastVisitedNodeIndex + ":" +
                snapshot.LastVisitedNodeType + ":" +
                snapshot.ProgressIsValid + ":" +
                snapshot.ProgressAtNode + ":" +
                snapshot.ProgressBiomeIndex + ":" +
                snapshot.ProgressRowIndex + ":" +
                snapshot.ProgressIndex + ":" +
                snapshot.ProgressRowCount + ":" +
                FormatFloat(snapshot.ProgressBiomeTravelRatio) + ":" +
                FormatFloat(snapshot.ProgressBetweenRowsRatio) + ":" +
                FormatFloat(snapshot.ProgressBetweenBiomesRatio) + ":" +
                string.Join("|", (snapshot.Party ?? Array.Empty<RunStatePartyActorPayload>())
                    .OrderBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .Select(actor =>
                        actor.HeroSlot + "," +
                        actor.TeamPosition + "," +
                        actor.ActorGuid + "," +
                        actor.ActorDataId + "," +
                        actor.ActorName + "," +
                        actor.PathId)
                    .ToArray());

            return ComputeStableDigest(raw);
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
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
