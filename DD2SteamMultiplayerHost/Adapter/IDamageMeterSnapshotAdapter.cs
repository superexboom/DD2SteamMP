using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IDamageMeterSnapshotAdapter
    {
        bool TryGetDamageMeterSnapshot(
            CombatSnapshotPayload combatSnapshot,
            out DamageMeterSnapshotPayload snapshot);
    }
}
