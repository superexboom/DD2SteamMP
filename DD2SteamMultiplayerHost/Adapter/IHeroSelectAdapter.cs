using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IHeroSelectAdapter
    {
        bool TryGetHeroSelectSnapshot(out HeroSelectSnapshotPayload snapshot);

        bool TryExecuteHeroSelectRequest(
            HeroSelectRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
