using System.Collections.Generic;
using DD2DebugDemoCore.Prefs;

namespace DD2DebugDemoCore.Model
{
    public sealed class DebugCombatDraft
    {
        public DebugCombatDraft()
        {
            BattleSequenceIds = new List<string>();
            HeroStartEffects = new List<string>();
            Teams = new List<DebugCombatTeamDraft>
            {
                new DebugCombatTeamDraft(0),
                new DebugCombatTeamDraft(1)
            };
        }

        public string BattleConfigurationId { get; set; } = string.Empty;
        public List<string> BattleSequenceIds { get; }
        public string CombatArenaId { get; set; } = string.Empty;
        public string CombatSourceId { get; set; } = string.Empty;
        public bool BattleTestControlsOnDuringLaunch { get; set; } = true;
        public List<DebugCombatTeamDraft> Teams { get; }
        public List<string> HeroStartEffects { get; }
        public string EnemyBossModifierId { get; set; } = string.Empty;

        public DebugCombatTeamDraft GetOrCreateTeam(int teamIndex)
        {
            while (Teams.Count <= teamIndex)
            {
                Teams.Add(new DebugCombatTeamDraft(Teams.Count));
            }

            return Teams[teamIndex];
        }

        public static DebugCombatDraft FromEditorPrefs(EditorPrefsDocument document, int slotCount = 4, int skillCount = 5)
        {
            return DebugCombatDraftImporter.Import(document, slotCount, skillCount);
        }
    }
}
