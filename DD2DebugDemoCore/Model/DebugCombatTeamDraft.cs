using System.Collections.Generic;
using System.Linq;

namespace DD2DebugDemoCore.Model
{
    public enum DebugCombatTeamSourceMode
    {
        OfficialPreset,
        CustomTeam
    }

    public sealed class DebugCombatTeamDraft
    {
        public DebugCombatTeamDraft(int teamIndex)
        {
            TeamIndex = teamIndex;
            ControllerType = "INPUT";
            SourceMode = teamIndex == 0 ? DebugCombatTeamSourceMode.CustomTeam : DebugCombatTeamSourceMode.OfficialPreset;
            Actors = new List<DebugCombatActorDraft>();
        }

        public int TeamIndex { get; }
        public string ControllerType { get; set; }
        public DebugCombatTeamSourceMode SourceMode { get; set; }
        public List<DebugCombatActorDraft> Actors { get; }

        public bool HasCustomActors()
        {
            return Actors.Any(actor => actor != null && actor.HasAnyConfiguredValue());
        }

        public DebugCombatTeamDraft Clone()
        {
            DebugCombatTeamDraft clone = new DebugCombatTeamDraft(TeamIndex)
            {
                ControllerType = ControllerType,
                SourceMode = SourceMode
            };
            clone.Actors.AddRange(Actors.Select(actor => actor == null ? null : actor.Clone()));
            return clone;
        }
    }
}
