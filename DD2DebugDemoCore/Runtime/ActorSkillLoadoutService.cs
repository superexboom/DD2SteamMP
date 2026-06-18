using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Source;
using Assets.Code.Unlock;
using Assets.Code.Utils;
using DD2DebugDemoCore.Diagnostics;

namespace DD2DebugDemoCore.Runtime
{
    public sealed class ActorSkillLoadoutService
    {
        private const string GenericMonsterMoveSkillId = "maa_move";
        private const string GenericMonsterPassSkillId = "pass_stress";
        private const string DeprecatedGenericMoveSkillId = "move";
        private const string HealingPassSkillId = "pass_heal";

        private static readonly string[] GenericEnemySystemSkillIds =
        {
            GenericMonsterMoveSkillId,
            GenericMonsterPassSkillId
        };

        private readonly IDebugDemoLogger _log;

        public ActorSkillLoadoutService(IDebugDemoLogger log = null)
        {
            _log = log ?? NullDebugDemoLogger.Instance;
        }

        public bool TryApplyDraftSkills(
            ActorInstance actor,
            IReadOnlyList<string> targetSkillIds,
            out bool changed,
            out string error)
        {
            changed = false;
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            List<string> normalizedTargetSkillIds = NormalizeRequestedSkillIds(targetSkillIds, out error);
            if (normalizedTargetSkillIds == null)
            {
                return false;
            }

            if (normalizedTargetSkillIds.Count < 1 || normalizedTargetSkillIds.Count > 5)
            {
                error = "draft needs 1-5 skills; found " + normalizedTargetSkillIds.Count;
                return false;
            }

            List<string> beforeSkillIds = GetEquippedSkillIds(actor);
            foreach (string skillId in normalizedTargetSkillIds)
            {
                if (!TryPrepareDraftSkill(actor, skillId, out string prepareError))
                {
                    error = prepareError;
                    return false;
                }
            }

            if (!TryNormalizeCombatSkillInstances(actor, normalizedTargetSkillIds, out int removedDuplicateSkills, out string normalizeError))
            {
                error = normalizeError;
                return false;
            }

            if (!TryClearCombatSkillEquipment(actor, out int clearedSkills, out string clearError))
            {
                error = clearError;
                return false;
            }

            foreach (string skillId in normalizedTargetSkillIds)
            {
                if (!TryEquipDraftSkill(actor, skillId, out string equipError))
                {
                    error = equipError;
                    return false;
                }
            }

            if (!TryRestoreSystemCombatSkillEquipment(actor, out int restoredSystemSkills, out string restoreSystemError))
            {
                error = restoreSystemError;
                return false;
            }

            actor.RefreshStats();

            List<string> afterSkillIds = GetDraftRelevantEquippedSkillIds(actor);
            if (!SkillIdSetsEqual(afterSkillIds, normalizedTargetSkillIds))
            {
                error = "game did not apply draft skills for " + DescribeActor(actor) +
                    "; expected=" + string.Join(",", normalizedTargetSkillIds.ToArray()) +
                    ", actual=" + string.Join(",", GetEquippedSkillIds(actor).ToArray());
                return false;
            }

            changed = removedDuplicateSkills > 0 ||
                clearedSkills > 0 ||
                restoredSystemSkills > 0 ||
                !SkillIdSetsEqual(GetDraftRelevantSkillIds(beforeSkillIds), afterSkillIds);
            return true;
        }

        public bool TryEnsureSystemCombatSkills(ActorInstance actor, out bool changed, out string error)
        {
            changed = false;
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> beforeSkills, out error))
            {
                return false;
            }

            string beforeDigest = BuildSystemSkillDigest(beforeSkills);
            if (!TryAddMissingInitialCombatSkills(actor, out string addMissingError))
            {
                error = addMissingError;
                return false;
            }

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> skills, out error))
            {
                return false;
            }

            if (!TryAddGenericEnemySystemCombatSkills(actor, skills, out string addGenericError))
            {
                error = addGenericError;
                return false;
            }

            for (int i = 0; i < skills.Count; i++)
            {
                SkillInstance skill = skills[i];
                if (skill == null || !IsSystemCombatSkill(skill))
                {
                    continue;
                }

                try
                {
                    if (!skill.GetIsUnlocked())
                    {
                        skill.SetIsUnlocked();
                    }

                    if (!skill.GetIsEquipped())
                    {
                        skill.SetIsEquipped(true, skills, false);
                    }
                }
                catch (Exception ex)
                {
                    error = "failed to restore system skill " + (skill == null ? "[skill]" : skill.SkillId) +
                        " on " + DescribeActor(actor) + ": " + ex.Message;
                    return false;
                }
            }

            actor.RefreshStats();
            changed = !string.Equals(beforeDigest, BuildSystemSkillDigest(skills), StringComparison.Ordinal);
            return true;
        }

        private static bool TryAddGenericEnemySystemCombatSkills(
            ActorInstance actor,
            List<SkillInstance> skills,
            out string error)
        {
            error = string.Empty;
            if (actor == null || skills == null)
            {
                error = "actor or combat skill list is missing";
                return false;
            }

            if (actor.TeamIndex == 0)
            {
                return true;
            }

            if (!IsRosterActor(actor))
            {
                RemoveExactSkill(skills, DeprecatedGenericMoveSkillId);
                RemoveExactSkill(skills, HealingPassSkillId);
            }

            bool hasMoveSkill = HasSystemSkillOfKind(skills, definition => definition.IsMoveSkill);
            bool hasPassSkill = HasSystemSkillOfKind(skills, definition => definition.IsPassSkill);
            for (int i = 0; i < GenericEnemySystemSkillIds.Length; i++)
            {
                string skillId = GenericEnemySystemSkillIds[i];
                if (skills.Any(skill => skill != null && string.Equals(skill.SkillId, skillId, StringComparison.Ordinal)))
                {
                    continue;
                }

                ActorDataSkill definition = GetSkillDefinition(skillId);
                if (definition == null)
                {
                    error = "generic enemy system skill " + skillId + " is missing from the skill library";
                    return false;
                }

                if ((definition.IsMoveSkill && hasMoveSkill) ||
                    (definition.IsPassSkill && hasPassSkill))
                {
                    continue;
                }

                if (!definition.GetIsValidForMode(actor.ActorDataMode))
                {
                    continue;
                }

                if (!TryGetSkillResource(skillId, out ResourceSkillBase resource, out error))
                {
                    return false;
                }

                if (resource == null)
                {
                    continue;
                }

                skills.Add(new SkillInstance(skillId, SourceType.CLASS, true, true));
            }

            return true;
        }

        private static bool IsRosterActor(ActorInstance actor)
        {
            return actor != null &&
                actor.ActorDataClass != null &&
                actor.ActorDataClass.IsPopulateInRoster;
        }

        private static void RemoveExactSkill(List<SkillInstance> skills, string skillId)
        {
            if (skills == null || string.IsNullOrWhiteSpace(skillId))
            {
                return;
            }

            for (int i = skills.Count - 1; i >= 0; i--)
            {
                SkillInstance skill = skills[i];
                if (skill != null && string.Equals(skill.SkillId, skillId, StringComparison.Ordinal))
                {
                    skills.RemoveAt(i);
                }
            }
        }

        private static bool HasSystemSkillOfKind(IEnumerable<SkillInstance> skills, Func<ActorDataSkill, bool> predicate)
        {
            if (skills == null || predicate == null)
            {
                return false;
            }

            foreach (SkillInstance skill in skills)
            {
                if (skill == null || string.IsNullOrWhiteSpace(skill.SkillId))
                {
                    continue;
                }

                ActorDataSkill definition = GetSkillDefinition(skill.SkillId);
                if (definition != null && predicate(definition))
                {
                    return true;
                }
            }

            return false;
        }

        public static string DescribeActor(ActorInstance actor)
        {
            if (actor == null)
            {
                return "[actor]";
            }

            return (actor.ActorDataId ?? "[actor]") + "/" + actor.ActorGuid;
        }

        private static List<string> NormalizeRequestedSkillIds(IReadOnlyList<string> skillIds, out string error)
        {
            error = string.Empty;
            List<string> result = new List<string>();
            HashSet<string> seenExactIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenRootIds = new HashSet<string>(StringComparer.Ordinal);
            if (skillIds == null)
            {
                error = "draft skill list is missing";
                return null;
            }

            for (int i = 0; i < skillIds.Count; i++)
            {
                string id = (skillIds[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    error = "skill slot " + (i + 1) + " is empty";
                    return null;
                }

                if (!seenExactIds.Add(id))
                {
                    error = "duplicate skill id in draft: " + id;
                    return null;
                }

                string rootId = GetSkillRootId(id);
                if (string.IsNullOrWhiteSpace(rootId))
                {
                    error = "skill slot " + (i + 1) + " has an invalid root id: " + id;
                    return null;
                }

                if (!seenRootIds.Add(rootId))
                {
                    error = "draft contains both base/upgraded variants for skill root: " + rootId;
                    return null;
                }

                result.Add(id);
            }

            return result;
        }

        private static List<string> GetDraftRelevantEquippedSkillIds(ActorInstance actor)
        {
            return GetDraftRelevantSkillIds(GetEquippedSkillIds(actor));
        }

        private static List<string> GetDraftRelevantSkillIds(IEnumerable<string> skillIds)
        {
            List<string> result = new List<string>();
            HashSet<string> seenRootIds = new HashSet<string>(StringComparer.Ordinal);
            if (skillIds == null)
            {
                return result;
            }

            foreach (string skillId in skillIds)
            {
                string id = (skillId ?? string.Empty).Trim();
                string rootId = GetSkillRootId(id);
                if (string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(rootId) ||
                    !seenRootIds.Add(rootId))
                {
                    continue;
                }

                if (IsSystemCombatSkill(id))
                {
                    continue;
                }

                result.Add(id);
            }

            return result;
        }

        private static bool TryNormalizeCombatSkillInstances(
            ActorInstance actor,
            IReadOnlyList<string> targetSkillIds,
            out int removedSkills,
            out string error)
        {
            removedSkills = 0;
            error = string.Empty;

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> skills, out error))
            {
                return false;
            }

            HashSet<string> targetIds = new HashSet<string>(targetSkillIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            Dictionary<string, int> keepIndexBySkillId = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<int> removeIndexes = new HashSet<int>();
            for (int i = 0; i < skills.Count; i++)
            {
                SkillInstance skill = skills[i];
                if (skill == null || string.IsNullOrWhiteSpace(skill.SkillId))
                {
                    removeIndexes.Add(i);
                    continue;
                }

                string id = skill.SkillId.Trim();
                if (!keepIndexBySkillId.TryGetValue(id, out int keepIndex))
                {
                    keepIndexBySkillId[id] = i;
                    continue;
                }

                SkillInstance kept = keepIndex >= 0 && keepIndex < skills.Count ? skills[keepIndex] : null;
                if (ShouldPreferSkillInstance(skill, kept, targetIds))
                {
                    removeIndexes.Add(keepIndex);
                    keepIndexBySkillId[id] = i;
                }
                else
                {
                    removeIndexes.Add(i);
                }
            }

            foreach (int index in removeIndexes.OrderByDescending(index => index))
            {
                if (index >= 0 && index < skills.Count)
                {
                    skills.RemoveAt(index);
                    removedSkills++;
                }
            }

            return true;
        }

        private static bool ShouldPreferSkillInstance(SkillInstance candidate, SkillInstance kept, HashSet<string> targetIds)
        {
            if (kept == null)
            {
                return true;
            }

            if (candidate == null)
            {
                return false;
            }

            bool candidateIsTarget = targetIds != null && targetIds.Contains(candidate.SkillId);
            bool keptIsTarget = targetIds != null && targetIds.Contains(kept.SkillId);
            if (candidateIsTarget != keptIsTarget)
            {
                return candidateIsTarget;
            }

            bool candidateUnlocked = candidate.GetIsUnlocked();
            bool keptUnlocked = kept.GetIsUnlocked();
            if (candidateUnlocked != keptUnlocked)
            {
                return candidateUnlocked;
            }

            bool candidateEquipped = candidate.GetIsEquipped();
            bool keptEquipped = kept.GetIsEquipped();
            return candidateEquipped && !keptEquipped;
        }

        private static bool TryClearCombatSkillEquipment(ActorInstance actor, out int clearedSkills, out string error)
        {
            clearedSkills = 0;
            error = string.Empty;

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> skills, out error))
            {
                return false;
            }

            foreach (SkillInstance skill in skills.ToArray())
            {
                if (skill == null || !skill.GetIsEquipped() || IsSystemCombatSkill(skill))
                {
                    continue;
                }

                try
                {
                    if (skill.TryClearIsEquipped())
                    {
                        clearedSkills++;
                    }
                    else if (skill.GetIsEquipped())
                    {
                        skill.SetIsEquipped(false, skills, false);
                        clearedSkills++;
                    }
                }
                catch (Exception ex)
                {
                    error = "failed to clear equipped skill " + (skill == null ? "[skill]" : skill.SkillId) +
                        " on " + DescribeActor(actor) + ": " + ex.Message;
                    return false;
                }
            }

            return true;
        }

        private static bool TryRestoreSystemCombatSkillEquipment(ActorInstance actor, out int restoredSkills, out string error)
        {
            restoredSkills = 0;
            error = string.Empty;

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> skills, out error))
            {
                return false;
            }

            foreach (SkillInstance skill in skills)
            {
                if (skill == null || !IsSystemCombatSkill(skill) || !skill.GetIsUnlocked() || skill.GetIsEquipped())
                {
                    continue;
                }

                try
                {
                    skill.SetIsEquipped(true, skills, false);
                    restoredSkills++;
                }
                catch (Exception ex)
                {
                    error = "failed to restore system skill " + skill.SkillId + " on " +
                        DescribeActor(actor) + ": " + ex.Message;
                    return false;
                }
            }

            return true;
        }

        private static bool IsSystemCombatSkill(string skillId)
        {
            try
            {
                ActorDataSkill definition = GetSkillDefinition(skillId);
                return definition != null && (definition.IsMoveSkill || definition.IsPassSkill || definition.m_IsAlwaysEquipped);
            }
            catch
            {
                return false;
            }
        }

        private static ActorDataSkill GetSkillDefinition(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId) ||
                !SingletonMonoBehaviour<Library<string, ActorDataSkill>>.HasInstance(false))
            {
                return null;
            }

            return SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance.GetLibraryElement(skillId);
        }

        private static bool TryGetSkillResource(string skillId, out ResourceSkillBase resource, out string error)
        {
            resource = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(skillId) || !Singleton<ResourceDatabaseSkills>.HasInstance())
            {
                return true;
            }

            try
            {
                resource = Singleton<ResourceDatabaseSkills>.Instance.GetResource(skillId, false, null);
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to load system skill resource " + skillId + ": " + ex.Message;
                return false;
            }
        }

        private static bool IsSystemCombatSkill(SkillInstance skill)
        {
            if (skill == null)
            {
                return false;
            }

            if (skill.m_IsAlwaysEquipped)
            {
                return true;
            }

            return IsSystemCombatSkill(skill.SkillId);
        }

        private static string BuildSystemSkillDigest(IEnumerable<SkillInstance> skills)
        {
            if (skills == null)
            {
                return string.Empty;
            }

            List<string> parts = new List<string>();
            foreach (SkillInstance skill in skills)
            {
                if (skill == null || !IsSystemCombatSkill(skill))
                {
                    continue;
                }

                parts.Add((skill.SkillId ?? string.Empty) +
                    ":" + (skill.GetIsUnlocked() ? "u" : "-") +
                    ":" + (skill.GetIsEquipped() ? "e" : "-"));
            }

            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts.ToArray());
        }

        private bool TryEquipDraftSkill(ActorInstance actor, string skillId, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(skillId))
            {
                error = "missing skill id";
                return false;
            }

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> skills, out error))
            {
                return false;
            }

            SkillInstance skill = skills
                .Where(instance =>
                    instance != null &&
                    string.Equals(instance.SkillId, skillId, StringComparison.Ordinal) &&
                    instance.GetIsUnlocked())
                .OrderByDescending(instance => instance.GetCanChangeEquip(actor.ActorGuid, true))
                .ThenByDescending(instance => instance.GetIsEquipped())
                .FirstOrDefault();

            if (skill == null)
            {
                error = "skill " + skillId + " is not available for " + DescribeActor(actor);
                return false;
            }

            try
            {
                if (!skill.GetCanChangeEquip(actor.ActorGuid, true))
                {
                    _log.Warning("Forcing draft skill equip despite UI equip guard: " +
                        skillId + " on " + DescribeActor(actor) +
                        ", alwaysEquipped=" + skill.m_IsAlwaysEquipped + ".");
                }

                skill.SetIsEquipped(true, skills, true);
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to equip skill " + skillId + " on " + DescribeActor(actor) + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryGetCombatSkillInstances(ActorInstance actor, out List<SkillInstance> skills, out string error)
        {
            skills = null;
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            try
            {
                FieldInfo field = typeof(ActorInstance).GetField("m_CombatSkills", BindingFlags.Instance | BindingFlags.NonPublic);
                skills = field == null ? null : field.GetValue(actor) as List<SkillInstance>;
                if (skills == null)
                {
                    error = "could not access combat skill instances for " + DescribeActor(actor);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "failed to access combat skill instances for " + DescribeActor(actor) + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryRestoreBaseSkillFromUpgrade(ActorInstance actor, string skillId, out string error)
        {
            error = string.Empty;
            if (actor == null || string.IsNullOrWhiteSpace(skillId) || IsSkillUpgrade(skillId))
            {
                return true;
            }

            if (!TryGetCombatSkillInstances(actor, out List<SkillInstance> skills, out error))
            {
                return false;
            }

            string rootId = GetSkillRootId(skillId);
            SkillInstance upgradedInstance = skills
                .Where(instance =>
                    instance != null &&
                    !instance.m_IsAlwaysEquipped &&
                    !string.Equals(instance.SkillId, skillId, StringComparison.Ordinal) &&
                    string.Equals(GetSkillRootId(instance.SkillId), rootId, StringComparison.Ordinal))
                .OrderByDescending(instance => instance.GetIsUnlocked())
                .FirstOrDefault();

            if (upgradedInstance == null)
            {
                return true;
            }

            string previousSkillId = upgradedInstance.SkillId;
            try
            {
                if (!upgradedInstance.TryRemoveUpgrades(null) ||
                    !string.Equals(upgradedInstance.SkillId, skillId, StringComparison.Ordinal))
                {
                    error = "failed to restore base skill " + skillId + " from " + previousSkillId +
                        " on " + DescribeActor(actor);
                    return false;
                }

                if (!upgradedInstance.GetIsUnlocked())
                {
                    upgradedInstance.SetIsUnlocked();
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "failed to restore base skill " + skillId + " from " + previousSkillId +
                    " on " + DescribeActor(actor) + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryAddMissingInitialCombatSkills(ActorInstance actor, out string error)
        {
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            try
            {
                ResourceActor actorResource = Singleton<ResourceDatabaseActors>.Instance.GetResource(actor.ActorDataId, true, null);
                if (actorResource == null)
                {
                    error = "could not find actor resource for " + DescribeActor(actor);
                    return false;
                }

                MethodInfo method = typeof(ActorInstance).GetMethod(
                    "AddMissingInitialCombatSkills",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null)
                {
                    error = "could not find AddMissingInitialCombatSkills for " + DescribeActor(actor);
                    return false;
                }

                method.Invoke(actor, new object[] { actorResource });
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to add missing initial combat skills for " + DescribeActor(actor) + ": " +
                    (ex.InnerException == null ? ex.Message : ex.InnerException.Message);
                return false;
            }
        }

        private static bool TryPrepareDraftSkill(ActorInstance actor, string skillId, out string error)
        {
            error = string.Empty;
            if (actor == null || string.IsNullOrWhiteSpace(skillId))
            {
                error = "missing actor or skill id";
                return false;
            }

            SkillInstance skill = actor.GetCombatSkillInstance(skillId);
            if (skill != null && skill.GetIsUnlocked())
            {
                return true;
            }

            if (skill == null && !IsSkillUpgrade(skillId))
            {
                if (!TryRestoreBaseSkillFromUpgrade(actor, skillId, out string restoreError))
                {
                    error = restoreError;
                    return false;
                }

                skill = actor.GetCombatSkillInstance(skillId);
                if (skill != null)
                {
                    if (!skill.GetIsUnlocked())
                    {
                        skill.SetIsUnlocked();
                    }

                    return true;
                }

                if (!TryAddMissingInitialCombatSkills(actor, out string addMissingError))
                {
                    error = addMissingError;
                    return false;
                }

                skill = actor.GetCombatSkillInstance(skillId);
                if (skill != null)
                {
                    if (!skill.GetIsUnlocked())
                    {
                        skill.SetIsUnlocked();
                    }

                    return true;
                }
            }

            try
            {
                if (skillId.EndsWith("_u", StringComparison.Ordinal))
                {
                    string baseSkillId = skillId.Remove(skillId.Length - 2);
                    foreach (UnlockDefinition unlock in LibraryUnlock.GetUnlocksFromRequirementId(baseSkillId))
                    {
                        actor.UnlockSkill(unlock, SourceType.DEBUG);
                    }
                }
                else
                {
                    UnlockDefinition unlock = SkillUtils.GetUnlockFromSkillId(skillId);
                    if (unlock != null)
                    {
                        actor.UnlockSkill(unlock, SourceType.DEBUG);
                    }
                }
            }
            catch (Exception ex)
            {
                error = "failed to unlock draft skill " + skillId + ": " + ex.Message;
                return false;
            }

            skill = actor.GetCombatSkillInstance(skillId);
            if (skill == null)
            {
                error = "skill " + skillId + " is not on actor " + DescribeActor(actor);
                return false;
            }

            if (!skill.GetIsUnlocked())
            {
                error = "skill " + skillId + " is locked on actor " + DescribeActor(actor);
                return false;
            }

            return true;
        }

        private static List<string> GetEquippedSkillIds(ActorInstance actor)
        {
            return actor == null
                ? new List<string>()
                : actor.GetEquippedCombatSkillIds(null, false, false, false, false, false, true).ToList();
        }

        private static bool SkillIdSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            HashSet<string> leftSet = new HashSet<string>(left, StringComparer.Ordinal);
            return leftSet.SetEquals(right);
        }

        private static bool IsSkillUpgrade(string skillId)
        {
            return !string.IsNullOrWhiteSpace(skillId) && skillId.EndsWith("_u", StringComparison.Ordinal);
        }

        private static string GetSkillRootId(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return string.Empty;
            }

            string id = skillId.Trim();
            return IsSkillUpgrade(id) ? id.Substring(0, id.Length - 2) : id;
        }
    }
}
