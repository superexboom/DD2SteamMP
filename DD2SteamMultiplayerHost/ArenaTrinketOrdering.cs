using System;
using System.Collections.Generic;
using System.Linq;

namespace DD2SteamMultiplayerHost
{
    internal static class ArenaTrinketOrdering
    {
        public static bool HasTag(IEnumerable<string> tags, string tag)
        {
            return tags != null &&
                tags.Any(candidate => string.Equals(candidate, tag, StringComparison.Ordinal));
        }
    }
}
