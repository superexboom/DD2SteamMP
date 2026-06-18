using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IStoreActionAdapter
    {
        bool TryGetStoreSnapshot(out StoreSnapshotPayload snapshot);

        bool TryExecuteStoreAction(
            StoreActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
