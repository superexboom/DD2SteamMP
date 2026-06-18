using System;
using DD2SteamMultiplayerHost.Adapter;
using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost
{
    internal sealed class TurnCommandCoordinator
    {
        private PendingTurn _pendingTurn;

        public event Action<CombatCommand> CommandReady;

        public event Action<ClearTurnPayload> TurnCleared;

        public void StartTurn(TurnPromptPayload prompt, HeroSlotAssignmentPayload owner)
        {
            _pendingTurn = new PendingTurn(prompt, owner);
            string ownerText = owner == null ? "unassigned" : owner.Name + "/" + owner.SteamId;
            HostLog.Write("[turn] waiting for slot " + prompt.HeroSlot +
                " role=" + (prompt.ControlRole ?? "hero") +
                " team=" + prompt.TeamIndex + ":" + prompt.TeamPosition +
                " owner=" + ownerText +
                ", actor=" + prompt.ActorName + " (" + prompt.ActorGuid + ").");
        }

        public void ClearTurn(string reason)
        {
            PendingTurn turn = _pendingTurn;
            if (turn == null)
            {
                return;
            }

            _pendingTurn = null;
            ClearTurnPayload payload = new ClearTurnPayload(
                turn.Prompt.Round,
                turn.Prompt.Turn,
                turn.Prompt.HeroSlot,
                turn.Prompt.ActorGuid,
                string.IsNullOrWhiteSpace(reason) ? "cleared" : reason);

            HostLog.Write("[turn] cleared slot=" + payload.HeroSlot +
                ", role=" + (turn.Prompt.ControlRole ?? "hero") +
                ", actor=" + payload.ActorGuid +
                ", reason=" + payload.Reason + ".");

            Action<ClearTurnPayload> handler = TurnCleared;
            if (handler != null)
            {
                handler(payload);
            }
        }

        public bool ClearTurnIfMatches(ClearTurnPayload payload)
        {
            PendingTurn turn = _pendingTurn;
            if (turn == null || payload == null)
            {
                return false;
            }

            if (turn.Prompt.Round != payload.Round ||
                turn.Prompt.Turn != payload.Turn ||
                turn.Prompt.HeroSlot != payload.HeroSlot ||
                !string.Equals(turn.Prompt.ActorGuid, payload.ActorGuid, StringComparison.Ordinal))
            {
                HostLog.Write("[turn] clear ignored; pending actor=" + turn.Prompt.ActorGuid +
                    ", clear actor=" + payload.ActorGuid + ".");
                return false;
            }

            ClearTurn(payload.Reason);
            return true;
        }

        public bool TryGetPendingTurn(
            out TurnPromptPayload prompt,
            out HeroSlotAssignmentPayload owner,
            out string skillId,
            out string targetGuid,
            out bool isPass)
        {
            PendingTurn turn = _pendingTurn;
            if (turn == null)
            {
                prompt = null;
                owner = null;
                skillId = null;
                targetGuid = null;
                isPass = false;
                return false;
            }

            prompt = turn.Prompt;
            owner = turn.Owner;
            skillId = turn.SkillId;
            targetGuid = turn.TargetGuid;
            isPass = turn.IsPass;
            return true;
        }

        public void LogState()
        {
            if (_pendingTurn == null)
            {
                HostLog.Write("[turn] pending=none.");
                return;
            }

            PendingTurn turn = _pendingTurn;
            string ownerText = turn.Owner == null ? "unassigned" : turn.Owner.Name + "/" + turn.Owner.SteamId;
            HostLog.Write("[turn] pending round=" + turn.Prompt.Round +
                ", turn=" + turn.Prompt.Turn +
                ", slot=" + turn.Prompt.HeroSlot +
                ", role=" + (turn.Prompt.ControlRole ?? "hero") +
                ", team=" + turn.Prompt.TeamIndex + ":" + turn.Prompt.TeamPosition +
                ", owner=" + ownerText +
                ", actor=" + turn.Prompt.ActorName + "/" + turn.Prompt.ActorGuid +
                ", skill=" + (turn.SkillId ?? "[none]") +
                ", target=" + (turn.TargetGuid ?? "[none]") + ".");
        }

        public void HandleChooseSkill(ulong senderSteamId, string senderName, ChooseSkillPayload payload)
        {
            PendingTurn turn;
            if (!TryValidateSender(senderSteamId, senderName, payload.HeroSlot, payload.ActorGuid, out turn))
            {
                return;
            }

            turn.SkillId = payload.SkillId;
            turn.IsPass = false;
            HostLog.Write("[turn] accepted skill from " + senderName + ": " + payload.SkillId + ".");
            TryEmitReadyCommand(turn, senderName);
        }

        public void HandleChooseTarget(ulong senderSteamId, string senderName, ChooseTargetPayload payload)
        {
            PendingTurn turn;
            if (!TryValidateSender(senderSteamId, senderName, payload.HeroSlot, payload.ActorGuid, out turn))
            {
                return;
            }

            turn.TargetGuid = payload.TargetGuid;
            turn.IsPass = false;
            HostLog.Write("[turn] accepted target from " + senderName + ": " + payload.TargetGuid + ".");
            TryEmitReadyCommand(turn, senderName);
        }

        public void HandlePassTurn(ulong senderSteamId, string senderName, PassTurnPayload payload)
        {
            PendingTurn turn;
            if (!TryValidateSender(senderSteamId, senderName, payload.HeroSlot, payload.ActorGuid, out turn))
            {
                return;
            }

            turn.IsPass = true;
            turn.SkillId = null;
            turn.TargetGuid = null;
            PassTurnCommand command = new PassTurnCommand(
                turn.Prompt.Round,
                turn.Prompt.Turn,
                turn.Prompt.HeroSlot,
                turn.Prompt.ActorGuid,
                senderSteamId,
                senderName);

            HostLog.Write("[adapter-ready] PassTurn slot=" + command.HeroSlot +
                ", role=" + (turn.Prompt.ControlRole ?? "hero") +
                ", actor=" + command.ActorGuid +
                ", sender=" + command.SenderName + "/" + command.SenderSteamId + ".");

            Action<CombatCommand> handler = CommandReady;
            if (handler != null)
            {
                handler(command);
            }

            ClearTurn("pass");
        }

        private bool TryValidateSender(ulong senderSteamId, string senderName, int heroSlot, string actorGuid, out PendingTurn turn)
        {
            turn = _pendingTurn;
            if (turn == null)
            {
                HostLog.Write("[turn/reject] " + senderName + ": no pending turn.");
                return false;
            }

            if (heroSlot != turn.Prompt.HeroSlot)
            {
                HostLog.Write("[turn/reject] " + senderName + ": slot " + heroSlot + " is not active slot " + turn.Prompt.HeroSlot + ".");
                return false;
            }

            if (!string.Equals(actorGuid, turn.Prompt.ActorGuid, StringComparison.Ordinal))
            {
                HostLog.Write("[turn/reject] " + senderName + ": actor " + actorGuid + " does not match active actor " + turn.Prompt.ActorGuid + ".");
                return false;
            }

            if (turn.Owner == null)
            {
                HostLog.Write("[turn/reject] " + senderName + ": active slot " + turn.Prompt.HeroSlot + " has no assigned owner.");
                return false;
            }

            if (senderSteamId != turn.Owner.SteamId)
            {
                HostLog.Write("[turn/reject] " + senderName + ": slot " + turn.Prompt.HeroSlot + " belongs to " + turn.Owner.Name + "/" + turn.Owner.SteamId + ".");
                return false;
            }

            return true;
        }

        private void TryEmitReadyCommand(PendingTurn turn, string senderName)
        {
            if (turn.IsPass || string.IsNullOrWhiteSpace(turn.SkillId) || string.IsNullOrWhiteSpace(turn.TargetGuid) || turn.Owner == null)
            {
                return;
            }

            ExecuteSkillCommand command = new ExecuteSkillCommand(
                turn.Prompt.Round,
                turn.Prompt.Turn,
                turn.Prompt.HeroSlot,
                turn.Prompt.ActorGuid,
                turn.SkillId,
                turn.TargetGuid,
                turn.Owner.SteamId,
                senderName);

            HostLog.Write("[adapter-ready] ExecuteSkill slot=" + command.HeroSlot +
                ", role=" + (turn.Prompt.ControlRole ?? "hero") +
                ", actor=" + command.ActorGuid +
                ", skill=" + command.SkillId +
                ", target=" + command.TargetGuid +
                ", sender=" + command.SenderName + "/" + command.SenderSteamId + ".");

            Action<CombatCommand> handler = CommandReady;
            if (handler != null)
            {
                handler(command);
            }

            ClearTurn("execute-skill");
        }

        private sealed class PendingTurn
        {
            public PendingTurn(TurnPromptPayload prompt, HeroSlotAssignmentPayload owner)
            {
                Prompt = prompt;
                Owner = owner;
            }

            public TurnPromptPayload Prompt { get; }

            public HeroSlotAssignmentPayload Owner { get; }

            public string SkillId { get; set; }

            public string TargetGuid { get; set; }

            public bool IsPass { get; set; }
        }
    }
}
