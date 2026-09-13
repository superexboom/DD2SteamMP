using System;
using System.Collections.Generic;
using System.Linq;

namespace DD2SteamMultiplayerHost
{
    internal static class PassSkillSelection
    {
        public static string SelectPreferredId(IEnumerable<string> validPassSkillIds)
        {
            return validPassSkillIds == null
                ? null
                : validPassSkillIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .OrderBy(id => string.Equals(id, "pass_heal", StringComparison.Ordinal) ? 0 : 1)
                .FirstOrDefault();
        }
    }
}
