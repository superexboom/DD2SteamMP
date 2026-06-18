using System;
using System.Collections.Generic;
using System.Linq;
using DD2DebugDemoCore.Prefs;

namespace DD2DebugDemoCore.Model
{
    public sealed class DebugCombatDraftExportOptions
    {
        public int SlotCount { get; set; } = 4;
        public int SkillCount { get; set; } = 5;
        public bool IncludeExtensionKeys { get; set; } = true;
    }

    public static class DebugCombatDraftExporter
    {
        private const int TrinketsPerActor = 2;
        private const int PositiveQuirksPerActor = 3;
        private const int NegativeQuirksPerActor = 3;

        public static string[] ExportToEditorPrefsLines(
            DebugCombatDraft draft,
            DebugCombatDraftExportOptions options = null)
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            options = options ?? new DebugCombatDraftExportOptions();
            EditorPrefsWriter writer = new EditorPrefsWriter();
            DebugCombatTeamDraft team0 = GetTeam(draft, 0);
            DebugCombatTeamDraft team1 = GetTeam(draft, 1);

            writer.Add("demo_build", "True");
            writer.Add("disable_load_profile", "True");
            writer.Add("disable_save_profile", "True");
            writer.Add("run_test_game_mode", "combat");
            writer.Add("battle_test_battle_configuration", TrimOrEmpty(draft.BattleConfigurationId));

            if (options.IncludeExtensionKeys && HasAnyPresentValue(draft.BattleSequenceIds))
            {
                writer.AddCsv("dd2demo_battle_sequence", draft.BattleSequenceIds);
            }

            if (!string.IsNullOrWhiteSpace(draft.CombatArenaId))
            {
                writer.Add("battle_test_combat_arena", draft.CombatArenaId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(draft.CombatSourceId))
            {
                writer.Add("battle_test_combat_source", draft.CombatSourceId.Trim());
            }

            writer.Add("battle_test_controls", draft.BattleTestControlsOnDuringLaunch ? "True" : "False");
            WritePartyTeam(writer, team0, options);
            WriteEnemyTeam(writer, team1, options);
            WriteSharedHeroStartEffects(writer, draft);

            if (!string.IsNullOrWhiteSpace(draft.EnemyBossModifierId))
            {
                writer.Add("run_test_boss_modifier", draft.EnemyBossModifierId.Trim());
            }

            return writer.ToLines();
        }

        private static void WritePartyTeam(
            EditorPrefsWriter writer,
            DebugCombatTeamDraft team,
            DebugCombatDraftExportOptions options)
        {
            int configuredCount = GetConfiguredActorCount(team, options.SlotCount);
            writer.Add("battle_test_team_0_controller", GetControllerType(team, "INPUT"));
            writer.AddCsv("battle_test_team_0", FlattenActorValues(team, configuredCount, actor => actor == null ? string.Empty : actor.ActorId));
            writer.AddCsv("hero_test_paths", FlattenActorValues(team, configuredCount, actor => actor == null ? string.Empty : actor.PathId));

            bool canWriteNativeSkills = configuredCount > 0 &&
                GetConfiguredActors(team, configuredCount)
                    .All(actor => actor != null && CountPresentValues(actor.SkillIds) == options.SkillCount);
            if (canWriteNativeSkills)
            {
                writer.AddCsv("hero_test_start_skills", FlattenSkillValues(team, configuredCount, options.SkillCount));
            }

            List<string> combatItems = FlattenActorValues(team, configuredCount, actor => actor == null ? string.Empty : actor.CombatItemId).ToList();
            if (combatItems.Count == configuredCount && configuredCount > 0 && combatItems.All(IsPresentValue))
            {
                writer.AddCsv("hero_test_start_combat_item", combatItems);
            }

            List<string> trinkets = FlattenTrinketValues(team, configuredCount).ToList();
            if (trinkets.Count == configuredCount * TrinketsPerActor && configuredCount > 0 && trinkets.All(IsPresentValue))
            {
                writer.AddCsv("hero_test_start_trinkets", trinkets);
            }

            if (options.IncludeExtensionKeys)
            {
                List<string> extensionSkills = FlattenSkillValues(team, options.SlotCount, options.SkillCount).ToList();
                if (HasAnyPresentValue(extensionSkills))
                {
                    writer.AddCsv("dd2demo_team_0_start_skills", extensionSkills);
                }

                List<string> extensionCombatItems = FlattenActorValues(team, options.SlotCount, actor => actor == null ? string.Empty : actor.CombatItemId).ToList();
                if (HasAnyPresentValue(extensionCombatItems))
                {
                    writer.AddCsv("dd2demo_team_0_start_combat_item", extensionCombatItems);
                }

                List<string> extensionTrinkets = FlattenTrinketValues(team, options.SlotCount).ToList();
                if (HasAnyPresentValue(extensionTrinkets))
                {
                    writer.AddCsv("dd2demo_team_0_start_trinkets", extensionTrinkets);
                }
            }

            List<string> quirks = FlattenQuirkValues(team, options.SlotCount).ToList();
            if (HasAnyPresentValue(quirks))
            {
                writer.AddCsv("hero_quirks_per_hero", quirks);
            }
        }

        private static void WriteEnemyTeam(
            EditorPrefsWriter writer,
            DebugCombatTeamDraft team,
            DebugCombatDraftExportOptions options)
        {
            if (team == null || !team.HasCustomActors())
            {
                return;
            }

            int configuredCount = GetConfiguredActorCount(team, options.SlotCount);
            if (configuredCount <= 0)
            {
                return;
            }

            writer.Add("battle_test_team_1_controller", GetControllerType(team, "RANDOM"));
            writer.AddCsv("battle_test_team_1", FlattenActorValues(team, configuredCount, actor => actor == null ? string.Empty : actor.ActorId));
            if (!options.IncludeExtensionKeys)
            {
                return;
            }

            writer.AddCsv("dd2demo_team_1_paths", FlattenActorValues(team, options.SlotCount, actor => actor == null ? string.Empty : actor.PathId));
            writer.AddCsv("dd2demo_team_1_start_skills", FlattenSkillValues(team, options.SlotCount, options.SkillCount));

            List<string> combatItems = FlattenActorValues(team, options.SlotCount, actor => actor == null ? string.Empty : actor.CombatItemId).ToList();
            if (HasAnyPresentValue(combatItems))
            {
                writer.AddCsv("dd2demo_team_1_start_combat_item", combatItems);
            }

            List<string> trinkets = FlattenTrinketValues(team, options.SlotCount).ToList();
            if (HasAnyPresentValue(trinkets))
            {
                writer.AddCsv("dd2demo_team_1_start_trinkets", trinkets);
            }

            List<string> quirks = FlattenQuirkValues(team, options.SlotCount).ToList();
            if (HasAnyPresentValue(quirks))
            {
                writer.AddCsv("dd2demo_team_1_quirks_per_hero", quirks);
            }
        }

        private static void WriteSharedHeroStartEffects(EditorPrefsWriter writer, DebugCombatDraft draft)
        {
            if (!HasAnyPresentValue(draft.HeroStartEffects))
            {
                return;
            }

            writer.AddCsv("hero_test_start_effect", draft.HeroStartEffects);
            writer.Add("hero_test_start_effect_source_type", "rest_item");
            writer.Add("hero_test_start_effect_source_id", "dd2steammp_arena");
        }

        private static DebugCombatTeamDraft GetTeam(DebugCombatDraft draft, int teamIndex)
        {
            return draft.Teams == null
                ? null
                : draft.Teams.FirstOrDefault(team => team != null && team.TeamIndex == teamIndex);
        }

        private static string GetControllerType(DebugCombatTeamDraft team, string fallback)
        {
            return team == null || string.IsNullOrWhiteSpace(team.ControllerType)
                ? fallback
                : team.ControllerType.Trim();
        }

        private static IEnumerable<string> FlattenActorValues(
            DebugCombatTeamDraft team,
            int slotCount,
            Func<DebugCombatActorDraft, string> selector)
        {
            for (int i = 0; i < slotCount; i++)
            {
                DebugCombatActorDraft actor = GetActor(team, i);
                yield return selector == null ? string.Empty : selector(actor);
            }
        }

        private static IEnumerable<string> FlattenSkillValues(DebugCombatTeamDraft team, int slotCount, int skillCount)
        {
            for (int actorIndex = 0; actorIndex < slotCount; actorIndex++)
            {
                DebugCombatActorDraft actor = GetActor(team, actorIndex);
                for (int skillIndex = 0; skillIndex < skillCount; skillIndex++)
                {
                    yield return actor != null && skillIndex < actor.SkillIds.Count
                        ? actor.SkillIds[skillIndex]
                        : string.Empty;
                }
            }
        }

        private static IEnumerable<string> FlattenTrinketValues(DebugCombatTeamDraft team, int slotCount)
        {
            for (int actorIndex = 0; actorIndex < slotCount; actorIndex++)
            {
                DebugCombatActorDraft actor = GetActor(team, actorIndex);
                for (int trinketIndex = 0; trinketIndex < TrinketsPerActor; trinketIndex++)
                {
                    yield return actor != null && trinketIndex < actor.TrinketIds.Count
                        ? actor.TrinketIds[trinketIndex]
                        : string.Empty;
                }
            }
        }

        private static IEnumerable<string> FlattenQuirkValues(DebugCombatTeamDraft team, int slotCount)
        {
            for (int actorIndex = 0; actorIndex < slotCount; actorIndex++)
            {
                DebugCombatActorDraft actor = GetActor(team, actorIndex);
                for (int i = 0; i < PositiveQuirksPerActor; i++)
                {
                    yield return actor != null && i < actor.PositiveQuirkIds.Count ? actor.PositiveQuirkIds[i] : string.Empty;
                }

                for (int i = 0; i < NegativeQuirksPerActor; i++)
                {
                    yield return actor != null && i < actor.NegativeQuirkIds.Count ? actor.NegativeQuirkIds[i] : string.Empty;
                }

                yield return actor == null ? string.Empty : actor.DiseaseId;
            }
        }

        private static DebugCombatActorDraft GetActor(DebugCombatTeamDraft team, int index)
        {
            return team != null && team.Actors != null && index >= 0 && index < team.Actors.Count
                ? team.Actors[index]
                : null;
        }

        private static int GetConfiguredActorCount(DebugCombatTeamDraft team, int maxSlots)
        {
            int count = 0;
            for (int i = 0; i < maxSlots; i++)
            {
                DebugCombatActorDraft actor = GetActor(team, i);
                if (actor == null || string.IsNullOrWhiteSpace(actor.ActorId))
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private static IEnumerable<DebugCombatActorDraft> GetConfiguredActors(DebugCombatTeamDraft team, int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return GetActor(team, i);
            }
        }

        private static int CountPresentValues(IEnumerable<string> values)
        {
            return values == null ? 0 : values.Count(IsPresentValue);
        }

        private static bool HasAnyPresentValue(IEnumerable<string> values)
        {
            return values != null && values.Any(IsPresentValue);
        }

        private static bool IsPresentValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value.Trim(), EditorPrefsDocument.NoneValue, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimOrEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
