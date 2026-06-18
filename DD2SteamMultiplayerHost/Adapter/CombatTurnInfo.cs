namespace DD2SteamMultiplayerHost.Adapter
{
    internal sealed class CombatTurnInfo
    {
        public CombatTurnInfo(int round, int turn, int heroSlot, uint actorGuid, string actorName, int teamIndex, int teamPosition)
        {
            Round = round;
            Turn = turn;
            HeroSlot = heroSlot;
            ActorGuid = actorGuid;
            ActorName = actorName;
            TeamIndex = teamIndex;
            TeamPosition = teamPosition;
        }

        public int Round { get; }

        public int Turn { get; }

        public int HeroSlot { get; }

        public uint ActorGuid { get; }

        public string ActorName { get; }

        public int TeamIndex { get; }

        public int TeamPosition { get; }

        public bool IsHeroTeam
        {
            get { return TeamIndex == 0; }
        }

        public string ControlRole
        {
            get { return TeamIndex == 0 ? "hero" : "enemy"; }
        }

        public string ActorGuidText
        {
            get { return ActorGuid.ToString(); }
        }
    }
}
