using System;
using System.Collections.Generic;
using System.Linq;

namespace DD2DebugDemoCore.Model
{
    public sealed class DebugCombatActorDraft
    {
        public DebugCombatActorDraft()
        {
            SkillIds = new List<string>(5);
            TrinketIds = new List<string>(2);
            PositiveQuirkIds = new List<string>();
            NegativeQuirkIds = new List<string>();
            StartEffectIds = new List<string>();
        }

        public string ActorId { get; set; } = string.Empty;
        public string PaletteId { get; set; } = string.Empty;
        public string PathId { get; set; } = string.Empty;
        public List<string> SkillIds { get; }
        public string CombatItemId { get; set; } = string.Empty;
        public List<string> TrinketIds { get; }
        public List<string> PositiveQuirkIds { get; }
        public List<string> NegativeQuirkIds { get; }
        public string DiseaseId { get; set; } = string.Empty;
        public List<string> StartEffectIds { get; }

        public DebugCombatActorDraft Clone()
        {
            DebugCombatActorDraft clone = new DebugCombatActorDraft
            {
                ActorId = ActorId,
                PaletteId = PaletteId,
                PathId = PathId,
                CombatItemId = CombatItemId,
                DiseaseId = DiseaseId
            };
            clone.SkillIds.AddRange(SkillIds);
            clone.TrinketIds.AddRange(TrinketIds);
            clone.PositiveQuirkIds.AddRange(PositiveQuirkIds);
            clone.NegativeQuirkIds.AddRange(NegativeQuirkIds);
            clone.StartEffectIds.AddRange(StartEffectIds);
            return clone;
        }

        public bool HasAnyConfiguredValue()
        {
            return !string.IsNullOrWhiteSpace(ActorId) ||
                !string.IsNullOrWhiteSpace(PathId) ||
                !string.IsNullOrWhiteSpace(CombatItemId) ||
                !string.IsNullOrWhiteSpace(DiseaseId) ||
                SkillIds.Any(value => !string.IsNullOrWhiteSpace(value)) ||
                TrinketIds.Any(value => !string.IsNullOrWhiteSpace(value)) ||
                PositiveQuirkIds.Any(value => !string.IsNullOrWhiteSpace(value)) ||
                NegativeQuirkIds.Any(value => !string.IsNullOrWhiteSpace(value)) ||
                StartEffectIds.Any(value => !string.IsNullOrWhiteSpace(value));
        }
    }
}
