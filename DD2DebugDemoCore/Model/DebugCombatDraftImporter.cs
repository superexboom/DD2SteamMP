using System;
using System.Collections.Generic;
using DD2DebugDemoCore.Prefs;

namespace DD2DebugDemoCore.Model
{
    public static class DebugCombatDraftImporter
    {
        private const int TrinketsPerActor = 2;
        private const int PositiveQuirksPerActor = 3;
        private const int NegativeQuirksPerActor = 3;

        public static DebugCombatDraft Import(EditorPrefsDocument document, int slotCount = 4, int skillCount = 5)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            DebugCombatDraft draft = new DebugCombatDraft
            {
                BattleConfigurationId = document.GetLastValueOrDefault("battle_test_battle_configuration"),
                CombatArenaId = document.GetLastValueOrDefault("battle_test_combat_arena"),
                CombatSourceId = document.GetLastValueOrDefault("battle_test_combat_source")
            };

            string controls = document.GetLastValueOrDefault("battle_test_controls");
            if (!string.IsNullOrWhiteSpace(controls) && bool.TryParse(controls, out bool parsedControls))
            {
                draft.BattleTestControlsOnDuringLaunch = parsedControls;
            }

            string bossModifier = document.GetLastValueOrDefault("run_test_boss_modifier");
            if (!string.IsNullOrWhiteSpace(bossModifier))
            {
                draft.EnemyBossModifierId = CleanOptionalValue(bossModifier);
            }

            AddCsvValues(draft.BattleSequenceIds, document.GetLastValueOrDefault("dd2demo_battle_sequence"));
            AddCsvValues(draft.HeroStartEffects, document.GetLastValueOrDefault("hero_test_start_effect"));

            ImportTeam(document, draft.GetOrCreateTeam(0), 0, slotCount, skillCount);
            ImportTeam(document, draft.GetOrCreateTeam(1), 1, slotCount, skillCount);
            ImportPartySideNativeDetails(document, draft.GetOrCreateTeam(0), slotCount, skillCount);
            ImportTeamExtensionDetails(document, draft.GetOrCreateTeam(0), 0, slotCount, skillCount);
            ImportTeamExtensionDetails(document, draft.GetOrCreateTeam(1), 1, slotCount, skillCount);

            return draft;
        }

        private static void ImportTeam(
            EditorPrefsDocument document,
            DebugCombatTeamDraft team,
            int teamIndex,
            int slotCount,
            int skillCount)
        {
            string teamKey = "battle_test_team_" + teamIndex;
            string controllerKey = "battle_test_team_" + teamIndex + "_controller";
            string[] actorIds = document.GetLastCsvValues(teamKey);
            team.Actors.Clear();

            int actorCount = Math.Max(slotCount, actorIds.Length);
            for (int i = 0; i < actorCount; i++)
            {
                DebugCombatActorDraft actor = new DebugCombatActorDraft();
                if (i < actorIds.Length)
                {
                    actor.ActorId = CleanOptionalValue(actorIds[i]);
                }

                team.Actors.Add(actor);
            }

            string controller = document.GetLastValueOrDefault(controllerKey);
            if (!string.IsNullOrWhiteSpace(controller))
            {
                team.ControllerType = controller.Trim();
            }

            if (teamIndex == 1 && actorIds.Length > 0)
            {
                team.SourceMode = DebugCombatTeamSourceMode.CustomTeam;
            }
        }

        private static void ImportPartySideNativeDetails(
            EditorPrefsDocument document,
            DebugCombatTeamDraft team,
            int slotCount,
            int skillCount)
        {
            ImportActorFlatValues(team, document.GetLastCsvValues("hero_test_paths"), slotCount, 1, (actor, values, offset) =>
            {
                actor.PathId = GetValue(values, offset);
            });

            ImportActorFlatValues(team, document.GetLastCsvValues("hero_test_start_skills"), slotCount, skillCount, (actor, values, offset) =>
            {
                actor.SkillIds.Clear();
                for (int i = 0; i < skillCount; i++)
                {
                    actor.SkillIds.Add(GetValue(values, offset + i));
                }
            });

            ImportActorFlatValues(team, document.GetLastCsvValues("hero_test_start_combat_item"), slotCount, 1, (actor, values, offset) =>
            {
                actor.CombatItemId = GetValue(values, offset);
            });

            ImportActorFlatValues(team, document.GetLastCsvValues("hero_test_start_trinkets"), slotCount, TrinketsPerActor, (actor, values, offset) =>
            {
                actor.TrinketIds.Clear();
                actor.TrinketIds.Add(GetValue(values, offset));
                actor.TrinketIds.Add(GetValue(values, offset + 1));
            });

            ImportQuirks(team, document.GetLastCsvValues("hero_quirks_per_hero"), slotCount);
        }

        private static void ImportTeamExtensionDetails(
            EditorPrefsDocument document,
            DebugCombatTeamDraft team,
            int teamIndex,
            int slotCount,
            int skillCount)
        {
            string prefix = "dd2demo_team_" + teamIndex;

            ImportActorFlatValues(team, document.GetLastCsvValues(prefix + "_paths"), slotCount, 1, (actor, values, offset) =>
            {
                actor.PathId = GetValue(values, offset);
            });

            ImportActorFlatValues(team, document.GetLastCsvValues(prefix + "_start_skills"), slotCount, skillCount, (actor, values, offset) =>
            {
                actor.SkillIds.Clear();
                for (int i = 0; i < skillCount; i++)
                {
                    actor.SkillIds.Add(GetValue(values, offset + i));
                }
            });

            ImportActorFlatValues(team, document.GetLastCsvValues(prefix + "_start_combat_item"), slotCount, 1, (actor, values, offset) =>
            {
                actor.CombatItemId = GetValue(values, offset);
            });

            ImportActorFlatValues(team, document.GetLastCsvValues(prefix + "_start_trinkets"), slotCount, TrinketsPerActor, (actor, values, offset) =>
            {
                actor.TrinketIds.Clear();
                actor.TrinketIds.Add(GetValue(values, offset));
                actor.TrinketIds.Add(GetValue(values, offset + 1));
            });

            ImportQuirks(team, document.GetLastCsvValues(prefix + "_quirks_per_hero"), slotCount);
        }

        private static void ImportActorFlatValues(
            DebugCombatTeamDraft team,
            string[] values,
            int slotCount,
            int valuesPerActor,
            Action<DebugCombatActorDraft, string[], int> apply)
        {
            if (values == null || values.Length == 0 || apply == null)
            {
                return;
            }

            int actorCount = Math.Max(slotCount, (values.Length + valuesPerActor - 1) / valuesPerActor);
            EnsureActorSlots(team, actorCount);
            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                apply(team.Actors[actorIndex], values, actorIndex * valuesPerActor);
            }
        }

        private static void ImportQuirks(DebugCombatTeamDraft team, string[] values, int slotCount)
        {
            if (values == null || values.Length == 0)
            {
                return;
            }

            int valuesPerActor = PositiveQuirksPerActor + NegativeQuirksPerActor + 1;
            int actorCount = Math.Max(slotCount, (values.Length + valuesPerActor - 1) / valuesPerActor);
            EnsureActorSlots(team, actorCount);
            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                DebugCombatActorDraft actor = team.Actors[actorIndex];
                int offset = actorIndex * valuesPerActor;
                actor.PositiveQuirkIds.Clear();
                actor.NegativeQuirkIds.Clear();
                for (int i = 0; i < PositiveQuirksPerActor; i++)
                {
                    actor.PositiveQuirkIds.Add(GetValue(values, offset + i));
                }

                for (int i = 0; i < NegativeQuirksPerActor; i++)
                {
                    actor.NegativeQuirkIds.Add(GetValue(values, offset + PositiveQuirksPerActor + i));
                }

                actor.DiseaseId = GetValue(values, offset + PositiveQuirksPerActor + NegativeQuirksPerActor);
            }
        }

        private static void EnsureActorSlots(DebugCombatTeamDraft team, int count)
        {
            while (team.Actors.Count < count)
            {
                team.Actors.Add(new DebugCombatActorDraft());
            }
        }

        private static void AddCsvValues(ICollection<string> destination, string value)
        {
            foreach (string item in EditorPrefsDocument.SplitCsv(value))
            {
                if (!string.IsNullOrWhiteSpace(item) &&
                    !string.Equals(item, EditorPrefsDocument.NoneValue, StringComparison.OrdinalIgnoreCase))
                {
                    destination.Add(item);
                }
            }
        }

        private static string GetValue(IReadOnlyList<string> values, int index)
        {
            return index >= 0 && index < values.Count ? CleanOptionalValue(values[index]) : string.Empty;
        }

        private static string CleanOptionalValue(string value)
        {
            string cleaned = EditorPrefsDocument.CleanSlotValue(value);
            return string.Equals(cleaned, EditorPrefsDocument.NoneValue, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : cleaned;
        }
    }
}
