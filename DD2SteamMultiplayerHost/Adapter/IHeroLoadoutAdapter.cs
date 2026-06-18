using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IHeroLoadoutAdapter
    {
        bool TryGetHeroLoadoutSnapshot(out HeroLoadoutSnapshotPayload snapshot);

        bool TryExecuteHeroLoadoutRequest(
            HeroLoadoutRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
