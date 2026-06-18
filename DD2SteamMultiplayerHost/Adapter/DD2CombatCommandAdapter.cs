using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorController;
using Assets.Code.Actor.Events;
using Assets.Code.Buff;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Combat.Queries;
using Assets.Code.Dot;
using Assets.Code.Game;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Skill;
using Assets.Code.Token;
using Assets.Code.UI.Tooltips;
using Assets.Code.UI.Queries;
using Assets.Code.Utils;
using DD2DebugDemoCore.Runtime;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2CombatCommandAdapter : ICombatCommandAdapter
    {
        private static readonly HashSet<string> PvpExcludedEnemyActorDataIds = new HashSet<string>(
            new[] { "coven_sister_a", "coven_sister_b" },
            StringComparer.OrdinalIgnoreCase);

        public void Execute(CombatCommand command)
        {
            switch (command)
            {
                case ExecuteSkillCommand execute:
                    ExecuteSkill(execute);
                    break;
                case PassTurnCommand pass:
                    PassTurn(pass);
                    break;
            }
        }

        private static void ExecuteSkill(ExecuteSkillCommand command)
        {
            if (!TryValidateActiveTurn(command.ActorGuid, out uint actorGuid, out CombatBhv combat, out ActorInstance actor))
            {
                return;
            }

            if (!TryParseGuid(command.TargetGuid, "target", out uint targetGuid))
            {
                return;
            }

            if (!TryResolveCurrentCombatActor(targetGuid, out ActorInstance targetActor))
            {
                LogWarning("Ignoring command: target actor guid " + targetGuid +
                    " was not found in current combat actors. Current combat actors: " + DescribeCurrentCombatActors() + ".");
                return;
            }

            if (!TryValidateSkill(actor, command.SkillId))
            {
                return;
            }

            Log("ExecuteSkill actor=" + actorGuid +
                ", skill=" + command.SkillId +
                ", target=" + DescribeActor(targetActor) +
                ", sender=" + command.SenderName + "/" + command.SenderSteamId + ".");

            EventSkillSelectionChanged.Trigger(true, actorGuid, command.SkillId, true, false);

            string selectedSkillId = actor.GetSelectedSkillId();
            if (!string.Equals(selectedSkillId, command.SkillId, StringComparison.Ordinal))
            {
                LogWarning("Ignoring command: skill selection did not stick for actor " + DescribeActor(actor) +
                    ". requested=" + command.SkillId +
                    ", selected=" + (selectedSkillId ?? "[none]") + ".");
                return;
            }

            if (!actor.Controller.GetIsValidSkillTarget(command.SkillId, targetGuid))
            {
                LogWarning("Ignoring command: target " + DescribeActor(targetActor) +
                    " is not valid for skill " + command.SkillId +
                    " from actor " + DescribeActor(actor) +
                    ". Valid targets: " + DescribeValidTargets(actorGuid, command.SkillId) + ".");
                return;
            }

            EventSelectActor.Trigger(targetGuid, true);
        }

        private static void PassTurn(PassTurnCommand command)
        {
            if (!TryValidateActiveTurn(command.ActorGuid, out uint actorGuid, out _, out _))
            {
                return;
            }

            Log("PassTurn actor=" + actorGuid + ", sender=" + command.SenderName + "/" + command.SenderSteamId + ".");
            EventBattlePass.Trigger(actorGuid);
        }

        public void LogCombatState()
        {
            if (!TryGetCombat(out CombatBhv combat))
            {
                Log("[combat] CombatBhv is not available.");
                return;
            }

            ActorInstance currentActor = combat.GetCurrentActor();
            string currentActorText = currentActor == null ? "[none]" : DescribeActor(currentActor);
            Log("[combat] state=" + combat.CurrentBattleState +
                ", next=" + combat.GetNextState() +
                ", partyInBattle=" + combat.IsPartyInBattle +
                ", hasCurrentActor=" + combat.GetHasCurrentActor() +
                ", current=" + currentActorText + ".");

            Log("[combat] current actors: " + DescribeCurrentCombatActors() + ".");

            if (currentActor != null)
            {
                string selectedSkillId = currentActor.GetSelectedSkillId();
                Log("[combat] selected skill=" + (selectedSkillId ?? "[none]") + ".");
                if (!string.IsNullOrEmpty(selectedSkillId))
                {
                    Log("[combat] valid targets: " + DescribeValidTargets(currentActor.ActorGuid, selectedSkillId) + ".");
                }

                CombatTurnInfo turnInfo;
                if (TryGetCurrentTurnInfo(true, out turnInfo))
                {
                    Log("[combat] default control slot=" + turnInfo.HeroSlot +
                        " role=" + turnInfo.ControlRole +
                        " from team " + turnInfo.TeamIndex +
                        " position " + turnInfo.TeamPosition + ".");
                }
            }
        }

        public bool TryGetCombatSnapshot(out CombatSnapshotPayload snapshot)
        {
            snapshot = null;

            if (GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return false;
            }

            CombatBhv combat;
            if (!TryGetCombat(out combat))
            {
                return false;
            }

            try
            {
                ActorInstance currentActor = combat.GetCurrentActor();
                List<ActorSnapshotPayload> actors = new List<ActorSnapshotPayload>();
                IReadOnlyList<uint> currentActorGuids = GetCurrentCombatActorGuids();
                for (int i = 0; i < currentActorGuids.Count; i++)
                {
                    ActorInstance actor;
                    if (TryResolveActor(currentActorGuids[i], out actor))
                    {
                        actors.Add(BuildActorSnapshot(actor));
                    }
                }

                actors = actors
                    .OrderBy(actor => actor.TeamIndex)
                    .ThenBy(actor => actor.TeamPosition)
                    .ThenBy(actor => actor.ActorGuid)
                    .ToList();

                QueryTurnOrder turnOrderQuery = SafeQueryTurnOrder();
                snapshot = new CombatSnapshotPayload
                {
                    Round = combat.CurrentRound,
                    Turn = combat.CurrentTurn,
                    BattleState = Convert.ToString(combat.CurrentBattleState),
                    NextState = Convert.ToString(combat.GetNextState()),
                    PartyInBattle = combat.IsPartyInBattle,
                    HasCurrentActor = combat.GetHasCurrentActor(),
                    CurrentActorGuid = currentActor == null ? null : currentActor.ActorGuid.ToString(),
                    CurrentActorName = currentActor == null ? null : GetActorDisplayName(currentActor),
                    CurrentFirstTurnActorGuid = turnOrderQuery == null || turnOrderQuery.m_CurrentFirstTurnActorGuid == 0U
                        ? null
                        : turnOrderQuery.m_CurrentFirstTurnActorGuid.ToString(),
                    CurrentLastTurnActorGuid = turnOrderQuery == null || turnOrderQuery.m_CurrentLastTurnActorGuid == 0U
                        ? null
                        : turnOrderQuery.m_CurrentLastTurnActorGuid.ToString(),
                    SelectedSkill = BuildSelectedSkillSnapshot(currentActor),
                    TurnOrder = BuildTurnOrderSnapshots(turnOrderQuery, currentActor),
                    Actors = actors,
                };
                snapshot.Digest = ComputeSnapshotDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                LogSnapshotWarning("Failed to collect combat snapshot: " + ex.Message);
                return false;
            }
        }

        public bool TryGetCurrentTurnInfo(out CombatTurnInfo info)
        {
            return TryGetCurrentTurnInfo(false, out info);
        }

        public bool TryGetCurrentTurnInfo(bool includeEnemyTurns, out CombatTurnInfo info)
        {
            info = null;

            CombatBhv combat;
            if (!TryGetCombat(out combat))
            {
                return false;
            }

            if (!combat.IsPartyInBattle || combat.CurrentBattleState != BattleState.IN_TURN_SELECT || !combat.GetHasCurrentActor())
            {
                return false;
            }

            ActorInstance actor = combat.GetCurrentActor();
            if (actor == null)
            {
                return false;
            }

            if (actor.TeamIndex != 0 && !includeEnemyTurns)
            {
                return false;
            }

            if (actor.TeamIndex != 0 && IsPvpExcludedEnemyActor(actor))
            {
                return false;
            }

            int teamPosition = actor.TeamPosition;
            if (actor.TeamIndex == 0 && (teamPosition < 0 || teamPosition > 3))
            {
                return false;
            }

            if (actor.TeamIndex != 0 && teamPosition < 0)
            {
                return false;
            }

            int controlSlot = actor.TeamIndex == 0
                ? teamPosition + 1
                : GetEnemyControlSlot(actor.TeamIndex, teamPosition);
            string actorName = string.IsNullOrEmpty(actor.ActorDataId) ? actor.ActorGuid.ToString() : actor.ActorDataId;
            info = new CombatTurnInfo(
                combat.CurrentRound,
                combat.CurrentTurn,
                controlSlot,
                actor.ActorGuid,
                actorName,
                actor.TeamIndex,
                teamPosition);
            return true;
        }

        public IList<TurnSkillOptionPayload> GetCurrentTurnSkillOptions()
        {
            return GetCurrentTurnSkillOptions(false);
        }

        public IList<TurnSkillOptionPayload> GetCurrentTurnSkillOptions(bool includeEnemyTurns)
        {
            List<TurnSkillOptionPayload> options = new List<TurnSkillOptionPayload>();

            CombatTurnInfo info;
            if (!TryGetCurrentTurnInfo(includeEnemyTurns, out info))
            {
                return options;
            }

            ActorInstance actor;
            if (!TryResolveActor(info.ActorGuid, out actor) || actor.Controller == null)
            {
                return options;
            }

            if (includeEnemyTurns && actor.TeamIndex != 0)
            {
                EnsureEnemySystemCombatSkills(actor, true);
            }

            try
            {
                foreach (SkillTargetEntry entry in actor.Controller.GetValidSkillTargetEntries())
                {
                    if (string.IsNullOrWhiteSpace(entry.m_SkillId))
                    {
                        continue;
                    }

                    List<TurnTargetOptionPayload> targets = new List<TurnTargetOptionPayload>();
                    foreach (uint targetGuid in entry.m_ValidTargetActorGuids)
                    {
                        ActorInstance targetActor;
                        if (!TryResolveCurrentCombatActor(targetGuid, out targetActor))
                        {
                            continue;
                        }

                        targets.Add(new TurnTargetOptionPayload(
                            targetGuid.ToString(),
                            GetActorDisplayName(targetActor),
                            targetActor.TeamIndex,
                            targetActor.TeamPosition));
                    }

                    if (targets.Count == 0)
                    {
                        continue;
                    }

                    targets = targets
                        .OrderBy(target => target.TeamIndex)
                        .ThenBy(target => target.TeamPosition)
                        .ThenBy(target => target.ActorGuid)
                        .ToList();

                    TurnSkillOptionPayload existing = options.FirstOrDefault(option => option.SkillId == entry.m_SkillId);
                    if (existing == null)
                    {
                        options.Add(new TurnSkillOptionPayload(
                            entry.m_SkillId,
                            GetSkillDisplayName(entry.m_SkillId),
                            targets,
                            GetSkillDescription(entry.m_SkillId, actor)));
                    }
                    else
                    {
                        foreach (TurnTargetOptionPayload target in targets)
                        {
                            if (!existing.Targets.Any(existingTarget => existingTarget.ActorGuid == target.ActorGuid))
                            {
                                existing.Targets.Add(target);
                            }
                        }
                    }
                }

                foreach (TurnSkillOptionPayload option in options)
                {
                    option.Targets = option.Targets
                        .OrderBy(target => target.TeamIndex)
                        .ThenBy(target => target.TeamPosition)
                        .ThenBy(target => target.ActorGuid)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                LogWarning("Failed to collect current turn skill options: " + ex.Message);
            }

            return options;
        }

        public bool TryGetEnemyControllerDigest(out string digest)
        {
            digest = "inactive";

            if (GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return false;
            }

            CombatBhv combat;
            if (!TryGetCombat(out combat))
            {
                return false;
            }

            try
            {
                List<string> rows = new List<string>();
                IReadOnlyList<uint> currentActorGuids = GetCurrentCombatActorGuids();
                for (int i = 0; i < currentActorGuids.Count; i++)
                {
                    ActorInstance actor;
                    if (!TryResolveActor(currentActorGuids[i], out actor) ||
                        actor == null ||
                        actor.TeamIndex == 0 ||
                        IsPvpExcludedEnemyActor(actor))
                    {
                        continue;
                    }

                    string controller = actor.Controller == null
                        ? "none"
                        : Convert.ToString(actor.Controller.m_ActorControllerType);
                    rows.Add(actor.TeamIndex + ":" +
                        actor.TeamPosition + ":" +
                        actor.ActorGuid + ":" +
                        (actor.ActorDataId ?? string.Empty) + ":" +
                        actor.IsLiving + ":" +
                        controller);
                }

                rows.Sort(StringComparer.Ordinal);
                digest = rows.Count == 0 ? "inactive" : string.Join("|", rows.ToArray());
                return rows.Count > 0;
            }
            catch (Exception ex)
            {
                LogSnapshotWarning("Failed to collect PVP enemy controller digest: " + ex.Message);
                return false;
            }
        }

        public void SetEnemyTeamInputControllers(bool inputActive)
        {
            try
            {
                if (TrySetEnemyTeamControllersDirect(inputActive))
                {
                    return;
                }

                ActorControllerType controllerType = inputActive ? ActorControllerType.INPUT : ActorControllerType.COUNT;
                EventDebugUpdateEnemyTeamControllers.Trigger(controllerType);
                Log("PVP enemy controllers set to " + controllerType + ".");
            }
            catch (Exception ex)
            {
                LogWarning("Failed to update enemy team controllers for PVP: " + ex.Message);
            }
        }

        private static bool TrySetEnemyTeamControllersDirect(bool inputActive)
        {
            CombatBhv combat;
            BattleTeams battleTeams;
            if (!TryGetCombat(out combat) || !TryGetBattleTeams(combat, out battleTeams))
            {
                return false;
            }

            Type inputControllerType = typeof(ActorControllerBase).Assembly.GetType(
                "Assets.Code.Actor.ActorController.ActorControllerInput",
                false);
            if (inputActive && inputControllerType == null)
            {
                return false;
            }

            int changed = 0;
            int skippedExcluded = 0;
            int restoredSystemSkills = 0;
            IReadOnlyList<uint> currentActorGuids = GetCurrentCombatActorGuids();
            for (int i = 0; i < currentActorGuids.Count; i++)
            {
                ActorInstance actor;
                if (!TryResolveActor(currentActorGuids[i], out actor) || actor == null || actor.TeamIndex == 0)
                {
                    continue;
                }

                if (inputActive && IsPvpExcludedEnemyActor(actor))
                {
                    skippedExcluded++;
                    continue;
                }

                bool systemSkillsChanged = inputActive && EnsureEnemySystemCombatSkills(actor, false);
                if (systemSkillsChanged)
                {
                    restoredSystemSkills++;
                }

                ActorControllerType currentType = actor.Controller == null
                    ? ActorControllerType.COUNT
                    : actor.Controller.m_ActorControllerType;
                if (inputActive && currentType == ActorControllerType.INPUT)
                {
                    if (systemSkillsChanged)
                    {
                        RefreshCurrentActorSkillSelection(actor, combat);
                    }

                    continue;
                }

                if (!inputActive && currentType != ActorControllerType.INPUT)
                {
                    continue;
                }

                ActorControllerBase newController;
                if (inputActive)
                {
                    newController = Activator.CreateInstance(
                        inputControllerType,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new object[] { battleTeams },
                        null) as ActorControllerBase;
                }
                else
                {
                    Team team = battleTeams.GetTeam(actor.TeamIndex);
                    newController = battleTeams.CreateActorController(actor, team);
                }

                if (newController == null)
                {
                    continue;
                }

                if (actor.Controller != null)
                {
                    actor.Controller.Destroy();
                }

                actor.SetActorController(newController);
                if (inputActive &&
                    actor.Controller != null)
                {
                    RefreshCurrentActorSkillSelection(actor, combat);
                }

                changed++;
            }

            Log("PVP enemy controllers " + (inputActive ? "input" : "restored") +
                " direct changed=" + changed +
                (skippedExcluded > 0 ? ", skippedExcluded=" + skippedExcluded : string.Empty) +
                (restoredSystemSkills > 0 ? ", restoredSystemSkills=" + restoredSystemSkills : string.Empty) +
                ".");
            return true;
        }

        private static bool EnsureEnemySystemCombatSkills(ActorInstance actor, bool refreshCurrentController)
        {
            if (actor == null || actor.TeamIndex == 0)
            {
                return false;
            }

            try
            {
                ActorSkillLoadoutService skillLoadoutService = new ActorSkillLoadoutService();
                if (!skillLoadoutService.TryEnsureSystemCombatSkills(actor, out bool changed, out string error))
                {
                    LogWarning("Failed to restore PVP enemy system skills for " + DescribeActor(actor) + ": " + error + ".");
                    return false;
                }

                if (changed)
                {
                    Log("Restored PVP enemy system skills for " + DescribeActor(actor) + ".");
                    if (refreshCurrentController &&
                        actor.Controller != null)
                    {
                        RefreshCurrentActorSkillSelection(actor, null);
                    }
                }

                return changed;
            }
            catch (Exception ex)
            {
                LogWarning("Failed to restore PVP enemy system skills for " + DescribeActor(actor) + ": " + ex.Message + ".");
                return false;
            }
        }

        private static void RefreshCurrentActorSkillSelection(ActorInstance actor, CombatBhv combat)
        {
            if (actor == null || actor.Controller == null)
            {
                return;
            }

            try
            {
                CombatBhv activeCombat = combat;
                if (activeCombat == null && SingletonMonoBehaviour<CombatBhv>.HasInstance(false))
                {
                    activeCombat = SingletonMonoBehaviour<CombatBhv>.Instance;
                }

                if (activeCombat == null ||
                    activeCombat.CurrentBattleState != BattleState.IN_TURN_SELECT ||
                    activeCombat.GetCurrentActor() == null ||
                    activeCombat.GetCurrentActor().ActorGuid != actor.ActorGuid)
                {
                    return;
                }

                EventActorTryResetSelection.Trigger();
                actor.ResetSelection();
                actor.Controller.OnTurnSelect();
            }
            catch (Exception ex)
            {
                LogWarning("Failed to refresh current actor skill selection for " + DescribeActor(actor) + ": " + ex.Message + ".");
            }
        }

        private static bool IsPvpExcludedEnemyActor(ActorInstance actor)
        {
            return actor != null &&
                !string.IsNullOrWhiteSpace(actor.ActorDataId) &&
                PvpExcludedEnemyActorDataIds.Contains(actor.ActorDataId);
        }

        private static bool TryGetBattleTeams(CombatBhv combat, out BattleTeams battleTeams)
        {
            battleTeams = null;
            if (combat == null)
            {
                return false;
            }

            try
            {
                FieldInfo battleField = typeof(CombatBhv).GetField(
                    "m_Battle",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object battle = battleField == null ? null : battleField.GetValue(combat);
                if (battle == null)
                {
                    return false;
                }

                FieldInfo teamsField = battle.GetType().GetField(
                    "m_BattleTeams",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                battleTeams = teamsField == null ? null : teamsField.GetValue(battle) as BattleTeams;
                return battleTeams != null;
            }
            catch
            {
                battleTeams = null;
                return false;
            }
        }

        private static int GetEnemyControlSlot(int teamIndex, int teamPosition)
        {
            return -((Math.Max(1, teamIndex) * 100) + teamPosition + 1);
        }

        private static ActorSnapshotPayload BuildActorSnapshot(ActorInstance actor)
        {
            return new ActorSnapshotPayload
            {
                ActorGuid = actor.ActorGuid.ToString(),
                ActorDataId = string.IsNullOrEmpty(actor.ActorDataId) ? "[unknown]" : actor.ActorDataId,
                TeamIndex = actor.TeamIndex,
                TeamPosition = actor.TeamPosition,
                IsLiving = actor.IsLiving,
                IsDeathsDoor = SafeGetDeathsDoor(actor),
                Health = Mathf.RoundToInt(actor.HpRounded),
                MaxHealth = Mathf.RoundToInt(actor.CurrentHpMax),
                Stress = Mathf.RoundToInt(actor.Stress),
                StressMax = Mathf.RoundToInt(actor.StressMax),
                Tokens = BuildTokenStatusSnapshots(actor.ReadOnlyTokenContainer == null
                    ? null
                    : actor.ReadOnlyTokenContainer.GetIdAmountDictionary(), false),
                Buffs = BuildBuffStatusSnapshots(actor.ReadOnlyBuffContainer == null
                    ? null
                    : actor.ReadOnlyBuffContainer.GetVisibleInstances(), actor.ActorGuid),
                Dots = BuildDotStatusSnapshots(actor.ReadOnlyDotContainer == null
                    ? null
                    : actor.ReadOnlyDotContainer.GetVisibleInstances()),
            };
        }

        private static CombatSelectedSkillPayload BuildSelectedSkillSnapshot(ActorInstance currentActor)
        {
            if (currentActor == null)
            {
                return null;
            }

            string selectedSkillId = currentActor.GetSelectedSkillId();
            if (string.IsNullOrWhiteSpace(selectedSkillId))
            {
                return null;
            }

            try
            {
                QuerySelectedSkillTargets query = QuerySelectedSkillTargets.Trigger(currentActor.ActorGuid, selectedSkillId);
                return new CombatSelectedSkillPayload
                {
                    SkillId = selectedSkillId,
                    DisplayName = GetSkillDisplayName(selectedSkillId),
                    Description = GetSkillDescription(selectedSkillId, currentActor),
                    ValidTargets = BuildTargetSnapshots(query == null ? null : query.m_ValidTargetActorGuids),
                    StealthedTargets = BuildTargetSnapshots(query == null ? null : query.m_StealthedTargetActorGuids),
                };
            }
            catch (Exception ex)
            {
                LogWarning("Failed to collect selected skill snapshot: " + ex.Message);
                return new CombatSelectedSkillPayload
                {
                    SkillId = selectedSkillId,
                    DisplayName = GetSkillDisplayName(selectedSkillId),
                    Description = GetSkillDescription(selectedSkillId, currentActor),
                };
            }
        }

        private static IList<TurnTargetOptionPayload> BuildTargetSnapshots(IList<uint> actorGuids)
        {
            List<TurnTargetOptionPayload> targets = new List<TurnTargetOptionPayload>();
            if (actorGuids == null || actorGuids.Count == 0)
            {
                return targets;
            }

            foreach (uint actorGuid in actorGuids)
            {
                ActorInstance actor;
                if (TryResolveActor(actorGuid, out actor))
                {
                    targets.Add(new TurnTargetOptionPayload(
                        actorGuid.ToString(),
                        GetActorDisplayName(actor),
                        actor.TeamIndex,
                        actor.TeamPosition));
                }
                else
                {
                    targets.Add(new TurnTargetOptionPayload(
                        actorGuid.ToString(),
                        actorGuid + "/[missing]",
                        -1,
                        -1));
                }
            }

            return targets
                .OrderBy(target => target.TeamIndex)
                .ThenBy(target => target.TeamPosition)
                .ThenBy(target => target.ActorGuid)
                .ToList();
        }

        private static IList<CombatTurnOrderEntryPayload> BuildTurnOrderSnapshots(
            QueryTurnOrder query,
            ActorInstance currentActor)
        {
            List<CombatTurnOrderEntryPayload> result = new List<CombatTurnOrderEntryPayload>();
            if (query == null || query.m_RemainingTurnOrder == null || query.m_RemainingTurnOrder.Count == 0)
            {
                return result;
            }

            string currentActorGuid = currentActor == null ? null : currentActor.ActorGuid.ToString();
            for (int i = 0; i < query.m_RemainingTurnOrder.Count; i++)
            {
                uint actorGuid = query.m_RemainingTurnOrder[i];
                ActorInstance actor;
                bool resolved = TryResolveActor(actorGuid, out actor);
                string actorGuidText = actorGuid == 0U ? null : actorGuid.ToString();
                result.Add(new CombatTurnOrderEntryPayload
                {
                    Index = i,
                    ActorGuid = actorGuidText,
                    ActorDataId = resolved ? actor.ActorDataId : null,
                    DisplayName = resolved ? GetActorDisplayName(actor) : (actorGuidText ?? "[actorless]"),
                    TeamIndex = resolved ? actor.TeamIndex : -1,
                    TeamPosition = resolved ? actor.TeamPosition : -1,
                    IsCurrentActor = !string.IsNullOrEmpty(currentActorGuid) &&
                        string.Equals(actorGuidText, currentActorGuid, StringComparison.Ordinal),
                    IsFirstNormalTurn = query.m_CurrentFirstTurnActorGuid != 0U &&
                        actorGuid == query.m_CurrentFirstTurnActorGuid,
                    IsLastNormalTurn = query.m_CurrentLastTurnActorGuid != 0U &&
                        actorGuid == query.m_CurrentLastTurnActorGuid,
                    IsMissingActor = !resolved,
                });
            }

            return result;
        }

        private static IList<StatusSnapshotPayload> BuildTokenStatusSnapshots(IReadOnlyDictionary<string, int> amounts, bool isDot)
        {
            if (amounts == null || amounts.Count == 0)
            {
                return new List<StatusSnapshotPayload>();
            }

            return amounts
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                .OrderBy(pair => pair.Key)
                .Select(pair => new StatusSnapshotPayload(
                    pair.Key,
                    pair.Value,
                    -1,
                    GetStatusDisplayName(pair.Key, pair.Value, isDot),
                    GetStatusDescription(pair.Key, pair.Value, isDot),
                    isDot ? "dot" : "token"))
                .ToList();
        }

        private static IList<StatusSnapshotPayload> BuildDotStatusSnapshots(IReadOnlyList<DotInstance> instances)
        {
            if (instances == null || instances.Count == 0)
            {
                return new List<StatusSnapshotPayload>();
            }

            return instances
                .Where(instance => instance != null && instance.Definition != null && !string.IsNullOrWhiteSpace(instance.Definition.m_Type))
                .GroupBy(instance => instance.Definition.m_Type)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    List<DotInstance> dotInstances = group.ToList();
                    int duration = -1;
                    try
                    {
                        duration = dotInstances.Max(instance => instance.GetDurationAmount());
                    }
                    catch
                    {
                        duration = -1;
                    }

                    string description = string.Empty;
                    try
                    {
                        description = DotTooltipBhv.MakeTooltipText(dotInstances, true);
                    }
                    catch
                    {
                        description = GetStatusDescription(group.Key, dotInstances.Count, true);
                    }

                    return new StatusSnapshotPayload(
                        group.Key,
                        dotInstances.Count,
                        duration,
                        GetStatusDisplayName(group.Key, dotInstances.Count, true),
                        description,
                        "dot");
                })
                .ToList();
        }

        private static IList<StatusSnapshotPayload> BuildBuffStatusSnapshots(IReadOnlyList<BuffInstance> instances, uint actorGuid)
        {
            if (instances == null || instances.Count == 0)
            {
                return new List<StatusSnapshotPayload>();
            }

            return instances
                .Where(instance => instance != null && instance.Definition != null && !string.IsNullOrWhiteSpace(instance.Definition.Id))
                .GroupBy(instance => instance.Definition.Id)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    List<BuffInstance> buffInstances = group.ToList();
                    int duration = -1;
                    try
                    {
                        duration = buffInstances.Max(instance => instance.GetDurationAmount());
                    }
                    catch
                    {
                        duration = -1;
                    }

                    string description = string.Empty;
                    try
                    {
                        description = BuffDescription.GetDescription(buffInstances, false, actorGuid);
                    }
                    catch
                    {
                        description = string.Empty;
                    }

                    return new StatusSnapshotPayload(
                        group.Key,
                        buffInstances.Count,
                        duration,
                        GetBuffDisplayName(group.Key, description),
                        description,
                        "buff");
                })
                .ToList();
        }

        private static string ComputeSnapshotDigest(CombatSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.Round + ":" +
                snapshot.Turn + ":" +
                snapshot.BattleState + ":" +
                snapshot.NextState + ":" +
                snapshot.PartyInBattle + ":" +
                snapshot.HasCurrentActor + ":" +
                snapshot.CurrentActorGuid + ":" +
                snapshot.CurrentFirstTurnActorGuid + ":" +
                snapshot.CurrentLastTurnActorGuid + ":" +
                DescribeSelectedSkill(snapshot.SelectedSkill) + ":" +
                string.Join("|", (snapshot.TurnOrder ?? Array.Empty<CombatTurnOrderEntryPayload>()).Select(entry =>
                    entry.Index + "," +
                    entry.ActorGuid + "," +
                    entry.ActorDataId + "," +
                    entry.TeamIndex + "," +
                    entry.TeamPosition + "," +
                    entry.IsCurrentActor + "," +
                    entry.IsFirstNormalTurn + "," +
                    entry.IsLastNormalTurn + "," +
                    entry.IsMissingActor).ToArray()) + ":" +
                string.Join("|", (snapshot.Actors ?? Array.Empty<ActorSnapshotPayload>()).Select(actor =>
                    actor.ActorGuid + "," +
                    actor.ActorDataId + "," +
                    actor.TeamIndex + "," +
                    actor.TeamPosition + "," +
                    actor.IsLiving + "," +
                    actor.IsDeathsDoor + "," +
                    actor.Health + "/" + actor.MaxHealth + "," +
                    actor.Stress + "/" + actor.StressMax + "," +
                    FormatStatusesForDigest(actor.Tokens) + "," +
                    FormatStatusesForDigest(actor.Buffs) + "," +
                    FormatStatusesForDigest(actor.Dots)).ToArray());

            return ComputeStableDigest(raw);
        }

        private static string DescribeSelectedSkill(CombatSelectedSkillPayload selectedSkill)
        {
            if (selectedSkill == null)
            {
                return string.Empty;
            }

            return (selectedSkill.SkillId ?? string.Empty) + "[" +
                DescribeTargets(selectedSkill.ValidTargets) + "](" +
                DescribeTargets(selectedSkill.StealthedTargets) + ")";
        }

        private static string DescribeTargets(IList<TurnTargetOptionPayload> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(",", targets
                .OrderBy(target => target.TeamIndex)
                .ThenBy(target => target.TeamPosition)
                .ThenBy(target => target.ActorGuid)
                .Select(target =>
                    (target.ActorGuid ?? string.Empty) + "/" +
                    target.TeamIndex + "/" +
                    target.TeamPosition)
                .ToArray());
        }

        private static string FormatStatusesForDigest(IList<StatusSnapshotPayload> statuses)
        {
            if (statuses == null || statuses.Count == 0)
            {
                return "-";
            }

            return string.Join(";", statuses
                .OrderBy(status => status.Id)
                .Select(status => status.Id + "x" + status.Count)
                .ToArray());
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

        private static bool TryValidateActiveTurn(string commandActorGuid, out uint actorGuid, out CombatBhv combat, out ActorInstance actor)
        {
            actorGuid = 0U;
            combat = null;
            actor = null;

            if (!TryParseGuid(commandActorGuid, "actor", out actorGuid))
            {
                return false;
            }

            if (!TryGetCombat(out combat))
            {
                LogWarning("Ignoring command: CombatBhv is not available.");
                return false;
            }

            if (!combat.IsPartyInBattle || combat.CurrentBattleState != BattleState.IN_TURN_SELECT)
            {
                LogWarning("Ignoring command: battle state is " + combat.CurrentBattleState +
                    ", partyInBattle=" + combat.IsPartyInBattle + ".");
                return false;
            }

            uint currentActorGuid = combat.GetCurrentActorGuid();
            if (currentActorGuid != actorGuid)
            {
                LogWarning("Ignoring command: active actor is " + currentActorGuid +
                    ", command actor is " + actorGuid + ".");
                return false;
            }

            actor = combat.GetCurrentActor();
            if (actor == null)
            {
                LogWarning("Ignoring command: current actor " + actorGuid + " could not be resolved from CombatBhv.");
                return false;
            }

            return true;
        }

        private static bool TryValidateSkill(ActorInstance actor, string skillId)
        {
            if (actor.Controller == null)
            {
                LogWarning("Ignoring command: actor " + DescribeActor(actor) + " has no controller.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(skillId))
            {
                LogWarning("Ignoring command: skill id is empty.");
                return false;
            }

            if (!SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false) ||
                !SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetHasLibraryKey(skillId))
            {
                LogWarning("Ignoring command: skill id '" + skillId + "' was not found in the skill library.");
                return false;
            }

            if (!actor.Controller.GetIsValidSkill(skillId))
            {
                LogWarning("Ignoring command: skill '" + skillId + "' is not currently valid for actor " +
                    DescribeActor(actor) + ". This usually means the actor cannot launch it now or it has no valid targets.");
                return false;
            }

            return true;
        }

        private static bool TryParseGuid(string value, string label, out uint guid)
        {
            if (uint.TryParse(value, out guid) && guid != 0U)
            {
                return true;
            }

            LogWarning("Ignoring command: invalid " + label + " guid '" + value + "'. Runtime commands must use numeric DD2 actor guids.");
            return false;
        }

        private static bool TryGetCombat(out CombatBhv combat)
        {
            combat = null;
            if (!SingletonMonoBehaviour<CombatBhv>.HasInstance(false))
            {
                return false;
            }

            combat = SingletonMonoBehaviour<CombatBhv>.Instance;
            return combat != null;
        }

        private static bool TryResolveCurrentCombatActor(uint actorGuid, out ActorInstance actor)
        {
            actor = null;
            if (!SingletonMonoBehaviour<Library<uint, ActorInstance>>.HasInstance(false))
            {
                return false;
            }

            actor = SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(actorGuid);
            if (actor == null)
            {
                return false;
            }

            IReadOnlyList<uint> currentActorGuids = GetCurrentCombatActorGuids();
            return currentActorGuids.Count == 0 || currentActorGuids.Contains(actorGuid);
        }

        private static IReadOnlyList<uint> GetCurrentCombatActorGuids()
        {
            try
            {
                return QueryCurrentActors.Trigger().m_CurrentActorGuids;
            }
            catch (Exception ex)
            {
                LogWarning("Failed to query current combat actors: " + ex.Message);
                return Array.Empty<uint>();
            }
        }

        private static QueryTurnOrder SafeQueryTurnOrder()
        {
            try
            {
                return QueryTurnOrder.Trigger(true);
            }
            catch (Exception ex)
            {
                LogWarning("Failed to query combat turn order: " + ex.Message);
                return null;
            }
        }

        private static string DescribeCurrentCombatActors()
        {
            IReadOnlyList<uint> currentActorGuids = GetCurrentCombatActorGuids();
            if (currentActorGuids.Count == 0)
            {
                return "[none]";
            }

            return string.Join(", ", currentActorGuids.Select(guid =>
            {
                ActorInstance actor;
                return TryResolveActor(guid, out actor) ? DescribeActor(actor) : guid + "/[missing]";
            }).ToArray());
        }

        private static string DescribeValidTargets(uint actorGuid, string skillId)
        {
            try
            {
                QuerySelectedSkillTargets query = QuerySelectedSkillTargets.Trigger(actorGuid, skillId);
                if (query.m_ValidTargetActorGuids.Count == 0)
                {
                    return "[none]";
                }

                return string.Join(", ", query.m_ValidTargetActorGuids.Select(guid =>
                {
                    ActorInstance actor;
                    return TryResolveActor(guid, out actor) ? DescribeActor(actor) : guid + "/[missing]";
                }).ToArray());
            }
            catch (Exception ex)
            {
                return "[query failed: " + ex.Message + "]";
            }
        }

        private static bool TryResolveActor(uint actorGuid, out ActorInstance actor)
        {
            actor = null;
            if (!SingletonMonoBehaviour<Library<uint, ActorInstance>>.HasInstance(false))
            {
                return false;
            }

            actor = SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(actorGuid);
            return actor != null;
        }

        private static string DescribeActor(ActorInstance actor)
        {
            if (actor == null)
            {
                return "[null]";
            }

            return string.IsNullOrEmpty(actor.ActorDataId) ? "[unknown]" : actor.ActorDataId;
        }

        private static string GetActorDisplayName(ActorInstance actor)
        {
            if (actor == null)
            {
                return "[missing]";
            }

            string actorDataId = string.IsNullOrEmpty(actor.ActorDataId) ? "[unknown]" : actor.ActorDataId;
            return actor.ActorGuid + "/" + actorDataId + "/team=" + actor.TeamIndex + "/pos=" + actor.TeamPosition;
        }

        private static string GetSkillDisplayName(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return "[unknown]";
            }

            try
            {
                if (SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false))
                {
                    ActorDataSkill skill = SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetLibraryElement(skillId);
                    if (skill != null)
                    {
                        string displayName = SkillDescription.GetNameText(skill);
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            return displayName;
                        }
                    }
                }
            }
            catch
            {
            }

            return skillId;
        }

        private static string GetSkillDescription(string skillId, ActorInstance actor = null)
        {
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return string.Empty;
            }

            try
            {
                if (SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false))
                {
                    ActorDataSkill skill = SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetLibraryElement(skillId);
                    if (skill != null)
                    {
                        return BuildSkillDescription(skill, actor);
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string BuildSkillDescription(ActorDataSkill skill, ActorInstance actor)
        {
            if (skill == null)
            {
                return string.Empty;
            }

            List<string> blocks = new List<string>();
            AddSkillDescriptionBlock(blocks, SkillDescription.GetTopBarString(skill, actor));

            try
            {
                uint actorGuid = actor == null ? 0U : actor.ActorGuid;
                foreach (string result in SkillDescription.GetResultStringsByTargetType(skill, false, actorGuid) ?? new List<string>())
                {
                    AddSkillDescriptionBlock(blocks, result);
                }
            }
            catch
            {
            }

            return string.Join("\n", blocks.ToArray());
        }

        private static void AddSkillDescriptionBlock(List<string> blocks, string text)
        {
            if (blocks == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string trimmed = text.Trim();
            if (blocks.Any(existing => string.Equals(existing, trimmed, StringComparison.Ordinal)))
            {
                return;
            }

            blocks.Add(trimmed);
        }

        private static bool SafeGetDeathsDoor(ActorInstance actor)
        {
            try
            {
                return actor != null && actor.GetIsStatusActive(ActorStatusType.DEATHS_DOOR);
            }
            catch
            {
                return false;
            }
        }

        private static string GetStatusDisplayName(string id, int count, bool isDot)
        {
            try
            {
                return isDot
                    ? DotDescription.GetUnglyphedName(id)
                    : TokenDescription.GetNameString(id);
            }
            catch
            {
                return id ?? "[status]";
            }
        }

        private static string GetBuffDisplayName(string id, string description)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return "[buff]";
            }

            try
            {
                string localized = Singleton<Localization>.Instance.TryGetString(id, false);
                if (!string.IsNullOrWhiteSpace(localized) &&
                    !string.Equals(localized, id, StringComparison.OrdinalIgnoreCase))
                {
                    return localized;
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                string firstLine = description
                    .Replace("\r", string.Empty)
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine) && firstLine.Length <= 40)
                {
                    return firstLine.Trim();
                }
            }

            return id;
        }

        private static string GetStatusDescription(string id, int count, bool isDot)
        {
            try
            {
                return isDot
                    ? DotDescription.GetDescription(id, count, true)
                    : TokenDescription.GetDescription(id, true);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void Log(string message)
        {
            Debug.Log("[DD2SteamMP] " + message);
            HostLog.Write("[adapter] " + message);
        }

        private static void LogWarning(string message)
        {
            Debug.LogWarning("[DD2SteamMP] " + message);
            HostLog.Write("[adapter] " + message);
        }

        private static void LogSnapshotWarning(string message)
        {
            if (HostLog.WriteThrottled(
                "adapter-combat-snapshot-failed",
                "[adapter] " + message,
                TimeSpan.FromSeconds(15)))
            {
                Debug.LogWarning("[DD2SteamMP] " + message);
            }
        }
    }
}
