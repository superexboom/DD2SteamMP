using System;
using System.Linq;
using System.Reflection;
using Assets.Code.Embark;
using Assets.Code.Game;
using Assets.Code.Map.Generation.Biome;
using Assets.Code.UI;
using Assets.Code.Utils;
using DD2SteamMultiplayerHost.Protocol;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2EmbarkSyncAdapter : IEmbarkActionAdapter
    {
        public bool TryGetEmbarkSnapshot(out EmbarkSnapshotPayload snapshot)
        {
            try
            {
                if (!IsEmbarkMode())
                {
                    snapshot = CreateInactiveSnapshot();
                    return true;
                }

                EmbarkUiBhv ui = FindActiveEmbarkUi();
                EmbarkBhv embark = SingletonMonoBehaviour<EmbarkBhv>.HasInstance(false)
                    ? SingletonMonoBehaviour<EmbarkBhv>.Instance
                    : null;
                BiomeChoice nextBiome = embark == null ? null : embark.NextBiomeChoice;
                BiomeType biomeType = nextBiome == null || nextBiome.m_BiomeGenerationData == null
                    ? null
                    : nextBiome.m_BiomeGenerationData.GetBiomeType();

                bool hasUi = ui != null;
                bool isApplying = hasUi && SafeGetIsApplying(ui);
                bool hasRelationshipsApplied = hasUi && SafeGetHasRelationshipsApplied(ui);
                bool isExiting = hasUi && GetPrivateField<bool>(ui, "m_exiting");

                snapshot = new EmbarkSnapshotPayload
                {
                    IsActive = true,
                    CurrentGameMode = Convert.ToString(GameModeMgr.CurrentMode),
                    GameType = Singleton<GameTypeMgr>.HasInstance()
                        ? Singleton<GameTypeMgr>.Instance.CurrentGameType.GetName()
                        : "[none]",
                    EmbarkIsStarted = embark != null && embark.EmbarkIsStarted,
                    IsCamp = embark != null && embark.IsCamp,
                    HasUi = hasUi,
                    IsExiting = isExiting,
                    IsApplyingRelationships = isApplying,
                    HasRelationshipsApplied = hasRelationshipsApplied,
                    RelationshipCount = hasUi ? GetPrivateField<int>(ui, "m_relationshipCount") : 0,
                    CanApplyRelationships = hasUi && !isExiting && !isApplying && !hasRelationshipsApplied,
                    CanContinue = hasUi && !isExiting && !isApplying && hasRelationshipsApplied,
                    NextBiomeType = biomeType == null ? "[none]" : biomeType.GetName(),
                    NextBiomeName = biomeType == null ? "[none]" : biomeType.ToString(),
                    BiomeGoalId = nextBiome == null || nextBiome.m_BiomeGoal == null ? null : nextBiome.m_BiomeGoal.m_Id,
                    BiomeModifierId = nextBiome == null || nextBiome.m_BiomeModifier == null ? null : nextBiome.m_BiomeModifier.m_Id,
                };
                snapshot.Digest = ComputeEmbarkDigest(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("[embark] Failed to collect embark snapshot: " + ex.Message + ".");
                snapshot = CreateInactiveSnapshot();
                return false;
            }
        }

        public bool TryExecuteEmbarkAction(
            EmbarkActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message)
        {
            message = string.Empty;
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                message = "empty embark action";
                return false;
            }

            if (!IsEmbarkMode())
            {
                message = "embark scene is not active";
                return false;
            }

            EmbarkUiBhv ui = FindActiveEmbarkUi();
            if (ui == null)
            {
                message = "active EmbarkUiBhv was not found";
                return false;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (string.Equals(action, "apply_relationships", StringComparison.Ordinal))
            {
                return TryApplyRelationships(ui, senderSteamId, senderName, out message);
            }

            if (string.Equals(action, "continue", StringComparison.Ordinal))
            {
                return TryContinueEmbark(ui, senderSteamId, senderName, out message);
            }

            message = "unsupported embark action: " + request.Action;
            return false;
        }

        private static bool TryApplyRelationships(EmbarkUiBhv ui, ulong senderSteamId, string senderName, out string message)
        {
            if (SafeGetIsApplying(ui))
            {
                message = "embark relationships are already applying";
                return false;
            }

            if (SafeGetHasRelationshipsApplied(ui))
            {
                message = "embark relationships are already applied";
                return true;
            }

            ui.OnApplyAllRelationshipsButton();
            HostLog.Write("[embark-action] " + senderName + "/" + senderSteamId +
                " applied all embark relationships.");
            message = "embark apply-all relationships invoked on host";
            return true;
        }

        private static bool TryContinueEmbark(EmbarkUiBhv ui, ulong senderSteamId, string senderName, out string message)
        {
            if (SafeGetIsApplying(ui))
            {
                message = "embark relationships are still applying";
                return false;
            }

            if (!SafeGetHasRelationshipsApplied(ui))
            {
                message = "embark relationships are not applied yet";
                return false;
            }

            ui.OnEmbark();
            HostLog.Write("[embark-action] " + senderName + "/" + senderSteamId +
                " continued from embark scene.");
            message = "embark continue invoked on host";
            return true;
        }

        private static bool IsEmbarkMode()
        {
            return GameModeMgr.CurrentMode == GameModeType.EMBARK;
        }

        private static EmbarkUiBhv FindActiveEmbarkUi()
        {
            EmbarkUiBhv[] screens = UnityObject.FindObjectsOfType<EmbarkUiBhv>(true);
            return screens
                .Where(screen => screen != null && screen.gameObject != null && screen.gameObject.activeInHierarchy)
                .OrderByDescending(screen => screen.enabled)
                .FirstOrDefault();
        }

        private static bool SafeGetHasRelationshipsApplied(EmbarkUiBhv ui)
        {
            try
            {
                return ui != null && ui.HasRelationshipsApplied;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeGetIsApplying(EmbarkUiBhv ui)
        {
            try
            {
                return ui != null && ui.IsApplying;
            }
            catch
            {
                return false;
            }
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            if (target == null || string.IsNullOrEmpty(fieldName))
            {
                return default(T);
            }

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return default(T);
            }

            object value = field.GetValue(target);
            return value is T ? (T)value : default(T);
        }

        private static EmbarkSnapshotPayload CreateInactiveSnapshot()
        {
            EmbarkSnapshotPayload snapshot = new EmbarkSnapshotPayload
            {
                IsActive = false,
                CurrentGameMode = Convert.ToString(GameModeMgr.CurrentMode),
                GameType = "[none]",
                EmbarkIsStarted = false,
                IsCamp = false,
                HasUi = false,
                IsExiting = false,
                IsApplyingRelationships = false,
                HasRelationshipsApplied = false,
                RelationshipCount = 0,
                CanApplyRelationships = false,
                CanContinue = false,
                NextBiomeType = "[none]",
                NextBiomeName = "[none]",
            };
            snapshot.Digest = ComputeEmbarkDigest(snapshot);
            return snapshot;
        }

        private static string ComputeEmbarkDigest(EmbarkSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.IsActive + ":" +
                snapshot.CurrentGameMode + ":" +
                snapshot.GameType + ":" +
                snapshot.EmbarkIsStarted + ":" +
                snapshot.IsCamp + ":" +
                snapshot.HasUi + ":" +
                snapshot.IsExiting + ":" +
                snapshot.IsApplyingRelationships + ":" +
                snapshot.HasRelationshipsApplied + ":" +
                snapshot.RelationshipCount + ":" +
                snapshot.CanApplyRelationships + ":" +
                snapshot.CanContinue + ":" +
                snapshot.NextBiomeType + ":" +
                snapshot.NextBiomeName + ":" +
                snapshot.BiomeGoalId + ":" +
                snapshot.BiomeModifierId;
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
