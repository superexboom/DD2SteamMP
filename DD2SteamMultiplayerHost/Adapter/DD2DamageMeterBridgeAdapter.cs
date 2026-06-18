using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class DD2DamageMeterBridgeAdapter : IDamageMeterSnapshotAdapter
    {
        private const int BridgeApiVersion = 1;
        private const string ApiTypeName = "DD2DamageMeter.DamageMeterMultiplayerApi";
        private const string ApiMethodName = "TryGetLiveSnapshot";
        private MethodInfo _tryGetLiveSnapshotMethod;
        private string _lastDiscoveryFailure;

        public bool TryGetDamageMeterSnapshot(
            CombatSnapshotPayload combatSnapshot,
            out DamageMeterSnapshotPayload snapshot)
        {
            snapshot = null;

            string failure;
            if (!TryResolveSnapshotApi(out failure))
            {
                snapshot = CreateUnavailableSnapshot(combatSnapshot, failure);
                return true;
            }

            object providerSnapshot;
            if (!TryInvokeSnapshotApi(out providerSnapshot, out failure))
            {
                snapshot = CreateUnavailableSnapshot(combatSnapshot, failure);
                return true;
            }

            if (providerSnapshot == null)
            {
                snapshot = CreateUnavailableSnapshot(combatSnapshot, "DamageMeter provider returned no live snapshot.");
                return true;
            }

            snapshot = ConvertSnapshot(providerSnapshot, combatSnapshot);
            return true;
        }

        private bool TryResolveSnapshotApi(out string failure)
        {
            failure = string.Empty;

            if (_tryGetLiveSnapshotMethod != null)
            {
                return true;
            }

            Type apiType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    apiType = assembly.GetType(ApiTypeName, false);
                    if (apiType != null)
                    {
                        break;
                    }
                }
                catch
                {
                }
            }

            if (apiType == null)
            {
                return SetDiscoveryFailure("DamageMeter multiplayer API is not loaded.");
            }

            MethodInfo method = apiType.GetMethod(
                ApiMethodName,
                BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                return SetDiscoveryFailure("DamageMeter multiplayer API does not expose " + ApiMethodName + ".");
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (method.ReturnType != typeof(bool) ||
                parameters.Length != 1 ||
                !parameters[0].IsOut)
            {
                return SetDiscoveryFailure("DamageMeter multiplayer API signature is incompatible.");
            }

            _tryGetLiveSnapshotMethod = method;
            _lastDiscoveryFailure = null;
            return true;
        }

        private bool SetDiscoveryFailure(string failure)
        {
            _lastDiscoveryFailure = failure;
            return false;
        }

        private bool TryInvokeSnapshotApi(out object providerSnapshot, out string failure)
        {
            providerSnapshot = null;
            failure = string.Empty;

            try
            {
                object[] arguments = { null };
                object result = _tryGetLiveSnapshotMethod.Invoke(null, arguments);
                if (!(result is bool) || !(bool)result)
                {
                    failure = "DamageMeter provider is not ready.";
                    return false;
                }

                providerSnapshot = arguments[0];
                return true;
            }
            catch (TargetInvocationException ex)
            {
                failure = "DamageMeter provider threw " + ex.GetType().Name + ": " +
                    (ex.InnerException == null ? ex.Message : ex.InnerException.Message);
                return false;
            }
            catch (Exception ex)
            {
                _tryGetLiveSnapshotMethod = null;
                failure = "DamageMeter provider call failed: " + ex.Message;
                return false;
            }
        }

        private DamageMeterSnapshotPayload ConvertSnapshot(object source, CombatSnapshotPayload combatSnapshot)
        {
            DamageMeterSnapshotPayload snapshot = new DamageMeterSnapshotPayload
            {
                ApiVersion = GetInt(source, "ApiVersion", BridgeApiVersion),
                ProviderVersion = GetString(source, "ProviderVersion", null),
                Capabilities = GetString(source, "Capabilities", null),
                IsAvailable = GetBool(source, "IsAvailable", true),
                IsActive = GetBool(source, "IsActive", combatSnapshot != null && combatSnapshot.PartyInBattle),
                UnavailableReason = GetString(source, "UnavailableReason", null),
                Round = GetInt(source, "Round", combatSnapshot == null ? 0 : combatSnapshot.Round),
                Turn = GetInt(source, "Turn", combatSnapshot == null ? 0 : combatSnapshot.Turn),
                BattleState = GetString(source, "BattleState", combatSnapshot == null ? null : combatSnapshot.BattleState),
                CurrentActorGuid = GetString(source, "CurrentActorGuid", combatSnapshot == null ? null : combatSnapshot.CurrentActorGuid),
                CurrentActorName = GetString(source, "CurrentActorName", combatSnapshot == null ? null : combatSnapshot.CurrentActorName),
                PlayerTotalDamage = GetFloat(source, "PlayerTotalDamage", 0f),
                EnemyTotalDamage = GetFloat(source, "EnemyTotalDamage", 0f),
                Heroes = ReadActorRows(GetFirstPropertyValue(source, "Heroes", "PlayerStats")),
                Enemies = ReadActorRows(GetFirstPropertyValue(source, "Enemies", "EnemyStats")),
                Contributions = ReadContributionRows(GetFirstPropertyValue(source, "Contributions", "ContributionStats")),
                StatusTotals = ReadStatusTotals(GetFirstPropertyValue(source, "StatusTotals")),
                CombatLogEntries = ReadCombatLogRows(GetFirstPropertyValue(source, "CombatLogEntries", "LogEntries", "CombatLog")),
            };

            string digest = GetString(source, "Digest", null);
            snapshot.Digest = string.IsNullOrWhiteSpace(digest)
                ? ComputeDamageMeterDigest(snapshot)
                : digest;
            return snapshot;
        }

        private DamageMeterSnapshotPayload CreateUnavailableSnapshot(
            CombatSnapshotPayload combatSnapshot,
            string reason)
        {
            string normalizedReason = string.IsNullOrWhiteSpace(reason)
                ? _lastDiscoveryFailure ?? "DamageMeter provider is unavailable."
                : reason.Trim();

            DamageMeterSnapshotPayload snapshot = new DamageMeterSnapshotPayload
            {
                ApiVersion = BridgeApiVersion,
                IsAvailable = false,
                IsActive = false,
                UnavailableReason = normalizedReason,
                Round = combatSnapshot == null ? 0 : combatSnapshot.Round,
                Turn = combatSnapshot == null ? 0 : combatSnapshot.Turn,
                BattleState = combatSnapshot == null ? null : combatSnapshot.BattleState,
                CurrentActorGuid = combatSnapshot == null ? null : combatSnapshot.CurrentActorGuid,
                CurrentActorName = combatSnapshot == null ? null : combatSnapshot.CurrentActorName,
            };
            snapshot.Digest = ComputeStableDigest("unavailable:" + normalizedReason);
            return snapshot;
        }

        private static IList<DamageMeterActorStatsPayload> ReadActorRows(object value)
        {
            List<DamageMeterActorStatsPayload> rows = new List<DamageMeterActorStatsPayload>();
            IEnumerable enumerable = AsEnumerable(value);
            if (enumerable == null)
            {
                return rows;
            }

            foreach (object item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                rows.Add(new DamageMeterActorStatsPayload
                {
                    ActorGuid = GetString(item, "ActorGuid", null),
                    ActorName = GetString(item, "ActorName", null),
                    TeamIndex = GetInt(item, "TeamIndex", -1),
                    TotalDamageDealt = GetFloat(item, "TotalDamageDealt", 0f),
                    DotDamageDealt = GetFloat(item, "DotDamageDealt", 0f),
                    TotalDamageReceived = GetFloat(item, "TotalDamageReceived", 0f),
                    RawDamageReceived = GetFloat(item, "RawDamageReceived", 0f),
                    OverkillDamageDealt = GetFloat(item, "OverkillDamageDealt", 0f),
                    TotalHealingDone = GetFloat(item, "TotalHealingDone", 0f),
                    TotalHealingReceived = GetFloat(item, "TotalHealingReceived", 0f),
                    TotalStressReceived = GetFloat(item, "TotalStressReceived", 0f),
                    Kills = GetInt(item, "Kills", 0),
                    Crits = GetInt(item, "Crits", 0),
                    IncomingAttacks = GetInt(item, "IncomingAttacks", 0),
                    AvoidedAttacks = GetInt(item, "AvoidedAttacks", 0),
                    DodgeAvoids = GetInt(item, "DodgeAvoids", 0),
                    MissAvoids = GetInt(item, "MissAvoids", 0),
                });
            }

            return rows;
        }

        private static IList<DamageMeterContributionPayload> ReadContributionRows(object value)
        {
            List<DamageMeterContributionPayload> rows = new List<DamageMeterContributionPayload>();
            IEnumerable enumerable = AsEnumerable(value);
            if (enumerable == null)
            {
                return rows;
            }

            foreach (object item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                float bonusDamage = GetFloat(item, "BonusDamage", 0f);
                float vulnerableDamage = GetFloat(item, "VulnerableDamage", 0f);
                float shieldPrevented = GetFloat(item, "ShieldPrevented", 0f);
                float guardProtected = GetFloat(item, "GuardProtected", 0f);
                float dotDamagePrevented = GetFloat(item, "DotDamagePrevented", 0f);
                float totalContribution = bonusDamage + vulnerableDamage + shieldPrevented + guardProtected + dotDamagePrevented;
                rows.Add(new DamageMeterContributionPayload
                {
                    ActorGuid = GetString(item, "ActorGuid", null),
                    ActorName = GetString(item, "ActorName", null),
                    TeamIndex = GetInt(item, "TeamIndex", -1),
                    BonusDamage = bonusDamage,
                    VulnerableDamage = vulnerableDamage,
                    ShieldPrevented = shieldPrevented,
                    GuardProtected = guardProtected,
                    DotDamagePrevented = dotDamagePrevented,
                    ShieldWasted = GetInt(item, "ShieldWasted", 0),
                    ComboApplied = GetInt(item, "ComboApplied", 0),
                    ComboConsumed = GetInt(item, "ComboConsumed", 0),
                    TotalContribution = GetFloat(item, "TotalContribution", totalContribution),
                });
            }

            return rows;
        }

        private static DamageMeterStatusTotalsPayload ReadStatusTotals(object value)
        {
            if (value == null)
            {
                return null;
            }

            return new DamageMeterStatusTotalsPayload
            {
                PlayerBuffApplied = GetInt(value, "PlayerBuffApplied", 0),
                PlayerDebuffApplied = GetInt(value, "PlayerDebuffApplied", 0),
                EnemyBuffApplied = GetInt(value, "EnemyBuffApplied", 0),
                EnemyDebuffApplied = GetInt(value, "EnemyDebuffApplied", 0),
                PlayerStatusRemoved = GetInt(value, "PlayerStatusRemoved", 0),
                EnemyStatusRemoved = GetInt(value, "EnemyStatusRemoved", 0),
                PlayerStatusConsumed = GetInt(value, "PlayerStatusConsumed", 0),
                EnemyStatusConsumed = GetInt(value, "EnemyStatusConsumed", 0),
            };
        }

        private static IList<DamageMeterCombatLogEntryPayload> ReadCombatLogRows(object value)
        {
            List<DamageMeterCombatLogEntryPayload> rows = new List<DamageMeterCombatLogEntryPayload>();
            IEnumerable enumerable = AsEnumerable(value);
            if (enumerable == null)
            {
                return rows;
            }

            foreach (object item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                rows.Add(new DamageMeterCombatLogEntryPayload
                {
                    Index = GetInt(item, "Index", rows.Count),
                    Round = GetInt(item, "Round", 0),
                    EntryType = GetString(item, "EntryType", "entry"),
                    SourceName = GetString(item, "SourceName", null),
                    TargetName = GetString(item, "TargetName", null),
                    SourceIsPlayer = GetBool(item, "SourceIsPlayer", false),
                    TargetIsPlayer = GetBool(item, "TargetIsPlayer", false),
                    ActionType = GetString(item, "ActionType", null),
                    Value = GetFloat(item, "Value", 0f),
                    SkillId = GetString(item, "SkillId", null),
                    Extra = GetString(item, "Extra", null),
                    DotType = GetString(item, "DotType", null),
                    OverkillDamage = GetFloat(item, "OverkillDamage", 0f),
                });
            }

            return rows;
        }

        private static IEnumerable AsEnumerable(object value)
        {
            if (value == null || value is string)
            {
                return null;
            }

            return value as IEnumerable;
        }

        private static object GetFirstPropertyValue(object source, params string[] propertyNames)
        {
            if (source == null || propertyNames == null)
            {
                return null;
            }

            for (int i = 0; i < propertyNames.Length; i++)
            {
                PropertyInfo property = source.GetType().GetProperty(
                    propertyNames[i],
                    BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                {
                    return property.GetValue(source, null);
                }
            }

            return null;
        }

        private static string GetString(object source, string propertyName, string fallback)
        {
            object value = GetFirstPropertyValue(source, propertyName);
            return value == null ? fallback : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(object source, string propertyName, bool fallback)
        {
            object value = GetFirstPropertyValue(source, propertyName);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static int GetInt(object source, string propertyName, int fallback)
        {
            object value = GetFirstPropertyValue(source, propertyName);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static float GetFloat(object source, string propertyName, float fallback)
        {
            object value = GetFirstPropertyValue(source, propertyName);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static string ComputeDamageMeterDigest(DamageMeterSnapshotPayload snapshot)
        {
            if (snapshot == null)
            {
                return "0000000000000000";
            }

            string raw =
                snapshot.ApiVersion + ":" +
                snapshot.ProviderVersion + ":" +
                snapshot.Capabilities + ":" +
                snapshot.IsAvailable + ":" +
                snapshot.IsActive + ":" +
                snapshot.UnavailableReason + ":" +
                snapshot.Round + ":" +
                snapshot.Turn + ":" +
                snapshot.BattleState + ":" +
                snapshot.CurrentActorGuid + ":" +
                snapshot.PlayerTotalDamage.ToString("0.###", CultureInfo.InvariantCulture) + ":" +
                snapshot.EnemyTotalDamage.ToString("0.###", CultureInfo.InvariantCulture) + ":" +
                string.Join("|", (snapshot.Heroes ?? new List<DamageMeterActorStatsPayload>())
                    .Select(DescribeActorRow).ToArray()) + ":" +
                string.Join("|", (snapshot.Enemies ?? new List<DamageMeterActorStatsPayload>())
                    .Select(DescribeActorRow).ToArray()) + ":" +
                string.Join("|", (snapshot.Contributions ?? new List<DamageMeterContributionPayload>())
                    .Select(DescribeContributionRow).ToArray()) + ":" +
                DescribeStatusTotals(snapshot.StatusTotals) + ":" +
                string.Join("|", (snapshot.CombatLogEntries ?? new List<DamageMeterCombatLogEntryPayload>())
                    .Select(DescribeCombatLogRow).ToArray());

            return ComputeStableDigest(raw);
        }

        private static string DescribeActorRow(DamageMeterActorStatsPayload row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            return string.Join(",", new[]
            {
                row.ActorGuid ?? string.Empty,
                row.ActorName ?? string.Empty,
                row.TeamIndex.ToString(CultureInfo.InvariantCulture),
                FormatFloat(row.TotalDamageDealt),
                FormatFloat(row.DotDamageDealt),
                FormatFloat(row.TotalDamageReceived),
                FormatFloat(row.RawDamageReceived),
                FormatFloat(row.OverkillDamageDealt),
                FormatFloat(row.TotalHealingDone),
                FormatFloat(row.TotalHealingReceived),
                FormatFloat(row.TotalStressReceived),
                row.Kills.ToString(CultureInfo.InvariantCulture),
                row.Crits.ToString(CultureInfo.InvariantCulture),
                row.IncomingAttacks.ToString(CultureInfo.InvariantCulture),
                row.AvoidedAttacks.ToString(CultureInfo.InvariantCulture),
                row.DodgeAvoids.ToString(CultureInfo.InvariantCulture),
                row.MissAvoids.ToString(CultureInfo.InvariantCulture),
            });
        }

        private static string DescribeContributionRow(DamageMeterContributionPayload row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            return string.Join(",", new[]
            {
                row.ActorGuid ?? string.Empty,
                row.ActorName ?? string.Empty,
                row.TeamIndex.ToString(CultureInfo.InvariantCulture),
                FormatFloat(row.BonusDamage),
                FormatFloat(row.VulnerableDamage),
                FormatFloat(row.ShieldPrevented),
                FormatFloat(row.GuardProtected),
                FormatFloat(row.DotDamagePrevented),
                row.ShieldWasted.ToString(CultureInfo.InvariantCulture),
                row.ComboApplied.ToString(CultureInfo.InvariantCulture),
                row.ComboConsumed.ToString(CultureInfo.InvariantCulture),
                FormatFloat(row.TotalContribution),
            });
        }

        private static string DescribeStatusTotals(DamageMeterStatusTotalsPayload totals)
        {
            if (totals == null)
            {
                return string.Empty;
            }

            return totals.PlayerBuffApplied + "," +
                totals.PlayerDebuffApplied + "," +
                totals.EnemyBuffApplied + "," +
                totals.EnemyDebuffApplied + "," +
                totals.PlayerStatusRemoved + "," +
                totals.EnemyStatusRemoved + "," +
                totals.PlayerStatusConsumed + "," +
                totals.EnemyStatusConsumed;
        }

        private static string DescribeCombatLogRow(DamageMeterCombatLogEntryPayload row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            return string.Join(",", new[]
            {
                row.Index.ToString(CultureInfo.InvariantCulture),
                row.Round.ToString(CultureInfo.InvariantCulture),
                row.EntryType ?? string.Empty,
                row.SourceName ?? string.Empty,
                row.ActionType ?? string.Empty,
                FormatFloat(row.Value),
                row.TargetName ?? string.Empty,
                row.SkillId ?? string.Empty,
                row.Extra ?? string.Empty,
                row.DotType ?? string.Empty,
                FormatFloat(row.OverkillDamage),
            });
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string ComputeStableDigest(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
