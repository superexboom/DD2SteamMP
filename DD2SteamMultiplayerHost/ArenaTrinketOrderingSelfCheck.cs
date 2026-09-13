using System;

namespace DD2SteamMultiplayerHost
{
    internal static class ArenaTrinketOrderingSelfCheck
    {
        public static void Main()
        {
            if (!ArenaTrinketOrdering.HasTag(new[] { "other", "stained" }, "stained") ||
                ArenaTrinketOrdering.HasTag(new[] { "other" }, "stained") ||
                ArenaTrinketOrdering.HasTag(null, "stained") ||
                PassSkillSelection.SelectPreferredId(new[] { "pass_stress", "pass_heal" }) != "pass_heal" ||
                PassSkillSelection.SelectPreferredId(new[] { "pass_stress" }) != "pass_stress" ||
                PassSkillSelection.SelectPreferredId(null) != null)
            {
                throw new InvalidOperationException("MP regression self-check failed.");
            }
        }
    }
}
