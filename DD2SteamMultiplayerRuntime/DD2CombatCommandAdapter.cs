using Assets.Code.Actor;
using Assets.Code.Actor.Events;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Library;
using Assets.Code.Utils;
using DD2SteamMultiplayerPrototype.Adapter;
using UnityEngine;

namespace DD2SteamMultiplayerRuntime
{
    public sealed class DD2CombatCommandAdapter : ICombatCommandAdapter
    {
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
            if (!TryValidateActiveTurn(command.ActorGuid, out uint actorGuid))
            {
                return;
            }

            if (!TryParseGuid(command.TargetGuid, "target", out uint targetGuid))
            {
                return;
            }

            Debug.Log($"[DD2SteamMP] ExecuteSkill actor={actorGuid}, skill={command.SkillId}, target={targetGuid}, sender={command.SenderName}/{command.SenderSteamId}");
            EventSkillSelectionChanged.Trigger(true, actorGuid, command.SkillId, false, false);
            EventSelectActor.Trigger(targetGuid, false);
        }

        private static void PassTurn(PassTurnCommand command)
        {
            if (!TryValidateActiveTurn(command.ActorGuid, out uint actorGuid))
            {
                return;
            }

            Debug.Log($"[DD2SteamMP] PassTurn actor={actorGuid}, sender={command.SenderName}/{command.SenderSteamId}");
            EventBattlePass.Trigger(actorGuid);
        }

        private static bool TryValidateActiveTurn(string commandActorGuid, out uint actorGuid)
        {
            actorGuid = 0U;
            if (!TryParseGuid(commandActorGuid, "actor", out actorGuid))
            {
                return false;
            }

            if (!SingletonMonoBehaviour<CombatBhv>.HasInstance(false))
            {
                Debug.LogWarning("[DD2SteamMP] Ignoring command: CombatBhv is not available.");
                return false;
            }

            CombatBhv combat = SingletonMonoBehaviour<CombatBhv>.Instance;
            if (!combat.IsPartyInBattle || combat.CurrentBattleState != BattleState.IN_TURN_SELECT)
            {
                Debug.LogWarning($"[DD2SteamMP] Ignoring command: battle state is {combat.CurrentBattleState}, partyInBattle={combat.IsPartyInBattle}.");
                return false;
            }

            uint currentActorGuid = combat.GetCurrentActorGuid();
            if (currentActorGuid != actorGuid)
            {
                Debug.LogWarning($"[DD2SteamMP] Ignoring command: active actor is {currentActorGuid}, command actor is {actorGuid}.");
                return false;
            }

            if (!SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetHasLibraryKey(actorGuid))
            {
                Debug.LogWarning($"[DD2SteamMP] Ignoring command: actor {actorGuid} is not in the actor library.");
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

            Debug.LogWarning($"[DD2SteamMP] Ignoring command: invalid {label} guid '{value}'. Runtime commands must use numeric DD2 actor guids.");
            return false;
        }
    }
}
