using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Locale;
using Assets.Code.Map.Generation.Biome;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2InnSyncAdapter : IInnActionAdapter
    {
        public bool TryGetInnSnapshot(out InnSnapshotPayload snapshot)
        {
            try
            {
                if (!IsInnActive())
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                InnBhv inn = Singleton<InnBhv>.Instance;
                IReadOnlyList<BiomeChoice> biomeChoices = inn.GetBiomeChoices() ?? Array.Empty<BiomeChoice>();
                int selectedIndex = inn.GetSelectedBiomeChoiceIndex();
                snapshot = new InnSnapshotPayload
                {
                    IsActive = true,
                    GameType = Singleton<GameTypeMgr>.Instance.CurrentGameType.GetName(),
                    InnState = Convert.ToString(inn.GetInnState()),
                    IsCamp = inn.IsCamp(),
                    CanEmbark = inn.GetCanEmbark(),
                    SelectedBiomeChoiceIndex = selectedIndex,
                    BiomeChoices = biomeChoices
                        .Select((choice, index) => CreateBiomeChoicePayload(choice, index, selectedIndex))
                        .Where(choice => choice != null)
                        .ToList(),
                };
                snapshot.Digest = ComputeInnDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[inn] Failed to collect inn snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        public bool TryExecuteInnAction(
            InnActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty inn action";
                return false;
            }

            if (!IsInnActive())
            {
                message = "inn is not active";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "select_biome", StringComparison.Ordinal))
            {
                return TrySelectBiome(request.OptionIndex, senderSteamId, senderName, out message);
            }

            if (string.Equals(action, "embark", StringComparison.Ordinal))
            {
                return TryEmbark(senderSteamId, senderName, out message);
            }

            message = "unsupported inn action: " + request.Action;
            return false;
        }

        private static bool TrySelectBiome(int optionIndex, ulong senderSteamId, string senderName, out string message)
        {
            InnBhv inn = Singleton<InnBhv>.Instance;
            IReadOnlyList<BiomeChoice> biomeChoices = inn.GetBiomeChoices() ?? Array.Empty<BiomeChoice>();
            if (optionIndex < 0 || optionIndex >= biomeChoices.Count)
            {
                message = "inn biome option index is invalid: " + optionIndex;
                return false;
            }

            inn.SetSelectedBiomeChoiceIndex(optionIndex);
            BiomeChoice choice = biomeChoices[optionIndex];
            string biomeName = GetBiomeName(choice);
            HostLog.Write("[inn-action] " + senderName + "/" + senderSteamId +
                " selected biome option " + optionIndex + " (" + biomeName + ").");
            message = "inn biome selected: option " + optionIndex + " (" + biomeName + ")";
            return true;
        }

        private static bool TryEmbark(ulong senderSteamId, string senderName, out string message)
        {
            InnBhv inn = Singleton<InnBhv>.Instance;
            if (!inn.GetCanEmbark())
            {
                message = "inn cannot embark yet; no valid destination is selected";
                return false;
            }

            inn.NextState();
            inn.CompleteInn();
            HostLog.Write("[inn-action] " + senderName + "/" + senderSteamId + " confirmed inn embark.");
            message = "inn embark invoked on host";
            return true;
        }

        private static bool IsInnActive()
        {
            return GameModeMgr.CurrentMode == GameModeType.INN &&
                Singleton<InnBhv>.HasInstance() &&
                Singleton<InnBhv>.Instance.GetInnState() != InnState.INACTIVE;
        }

        private static InnBiomeChoicePayload CreateBiomeChoicePayload(BiomeChoice choice, int index, int selectedIndex)
        {
            if (choice == null || choice.m_BiomeGenerationData == null)
            {
                return null;
            }

            BiomeType biomeType = choice.m_BiomeGenerationData.GetBiomeType();
            string biomeTypeName = biomeType == null ? "[none]" : biomeType.GetName();
            string biomeName = biomeType == null ? "[none]" : biomeType.ToString();
            string goalId = choice.m_BiomeGoal == null ? null : choice.m_BiomeGoal.m_Id;
            string modifierId = choice.m_BiomeModifier == null ? null : choice.m_BiomeModifier.m_Id;
            bool isEndBiome = biomeType != null && biomeType.m_SubType == BiomeSubType.END;
            return new InnBiomeChoicePayload(
                index,
                biomeTypeName,
                biomeName,
                goalId,
                modifierId,
                index == selectedIndex,
                isEndBiome,
                GetBiomeGoalName(choice.m_BiomeGoal),
                GetBiomeGoalDescription(choice.m_BiomeGoal),
                GetBiomeModifierName(choice.m_BiomeModifier),
                GetBiomeModifierDescription(choice.m_BiomeModifier));
        }

        private static string GetBiomeName(BiomeChoice choice)
        {
            if (choice == null || choice.m_BiomeGenerationData == null)
            {
                return "[none]";
            }

            BiomeType biomeType = choice.m_BiomeGenerationData.GetBiomeType();
            return biomeType == null ? "[none]" : biomeType.GetName();
        }

        private static string GetBiomeGoalName(BiomeGoalDefinition goal)
        {
            if (goal == null || string.IsNullOrWhiteSpace(goal.m_Id))
            {
                return null;
            }

            try
            {
                return Singleton<Localization>.Instance.GetString("biome_goal_" + goal.m_Id, true);
            }
            catch
            {
                return goal.m_Id;
            }
        }

        private static string GetBiomeGoalDescription(BiomeGoalDefinition goal)
        {
            if (goal == null || string.IsNullOrWhiteSpace(goal.m_Id))
            {
                return null;
            }

            try
            {
                string sourceText =
                    Singleton<Localization>.Instance.GetString("biome_goal_tooltip_label", true) +
                    "\n" +
                    Singleton<Localization>.Instance.GetString("biome_goal_description_" + goal.m_Id, true);
                return Singleton<Localization>.Instance.GetSubstitutedText(sourceText, Array.Empty<string>());
            }
            catch
            {
                return goal.m_Id;
            }
        }

        private static string GetBiomeModifierName(BiomeModifierDefinition modifier)
        {
            if (modifier == null || string.IsNullOrWhiteSpace(modifier.m_Id))
            {
                return null;
            }

            try
            {
                return Singleton<Localization>.Instance.GetString("biome_mutator_" + modifier.m_Id, true);
            }
            catch
            {
                return modifier.m_Id;
            }
        }

        private static string GetBiomeModifierDescription(BiomeModifierDefinition modifier)
        {
            if (modifier == null)
            {
                return null;
            }

            try
            {
                return BiomeDescription.GetBiomeModifierDescription(modifier);
            }
            catch
            {
                return modifier.m_Id;
            }
        }

        private static InnSnapshotPayload CreateInactiveSnapshot()
        {
            InnSnapshotPayload snapshot = new InnSnapshotPayload
            {
                IsActive = false,
                GameType = "[none]",
                InnState = "[none]",
                IsCamp = false,
                CanEmbark = false,
                SelectedBiomeChoiceIndex = -1,
            };
            snapshot.Digest = ComputeInnDigest(snapshot);
            return snapshot;
        }

        private static string ComputeInnDigest(InnSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.GameType + ":" +
                snapshot.InnState + ":" +
                snapshot.IsCamp + ":" +
                snapshot.CanEmbark + ":" +
                snapshot.SelectedBiomeChoiceIndex + ":" +
                string.Join("|", (snapshot.BiomeChoices ?? Array.Empty<InnBiomeChoicePayload>())
                    .OrderBy(choice => choice.OptionIndex)
                    .Select(choice =>
                        choice.OptionIndex + "," +
                        choice.BiomeType + "," +
                        choice.BiomeName + "," +
                        choice.BiomeGoalId + "," +
                        choice.BiomeModifierId + "," +
                        choice.BiomeGoalName + "," +
                        choice.BiomeModifierName + "," +
                        choice.IsSelected + "," +
                        choice.IsEndBiome)
                    .ToArray());
            return ComputeStableDigest(raw);
        }

        private static string ComputeStableDigest(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string source = text ?? string.Empty;
                for (int i = 0; i < source.Length; i++)
                {
                    hash ^= source[i];
                    hash *= 1099511628211UL;
                }

                return hash.ToString("x16");
            }
        }
    }
}
