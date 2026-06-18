namespace DD2SteamMultiplayerPrototype.Adapter;

public enum CombatCommandType
{
    ExecuteSkill,
    PassTurn,
}

public abstract class CombatCommand
{
    protected CombatCommand(
        CombatCommandType type,
        int round,
        int turn,
        int heroSlot,
        string actorGuid,
        ulong senderSteamId,
        string senderName)
    {
        Type = type;
        Round = round;
        Turn = turn;
        HeroSlot = heroSlot;
        ActorGuid = actorGuid;
        SenderSteamId = senderSteamId;
        SenderName = senderName;
    }

    public CombatCommandType Type { get; }

    public int Round { get; }

    public int Turn { get; }

    public int HeroSlot { get; }

    public string ActorGuid { get; }

    public ulong SenderSteamId { get; }

    public string SenderName { get; }
}

public sealed class ExecuteSkillCommand : CombatCommand
{
    public ExecuteSkillCommand(
        int round,
        int turn,
        int heroSlot,
        string actorGuid,
        string skillId,
        string targetGuid,
        ulong senderSteamId,
        string senderName)
        : base(CombatCommandType.ExecuteSkill, round, turn, heroSlot, actorGuid, senderSteamId, senderName)
    {
        SkillId = skillId;
        TargetGuid = targetGuid;
    }

    public string SkillId { get; }

    public string TargetGuid { get; }
}

public sealed class PassTurnCommand : CombatCommand
{
    public PassTurnCommand(
        int round,
        int turn,
        int heroSlot,
        string actorGuid,
        ulong senderSteamId,
        string senderName)
        : base(CombatCommandType.PassTurn, round, turn, heroSlot, actorGuid, senderSteamId, senderName)
    {
    }
}
