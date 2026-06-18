using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IEmbarkActionAdapter
    {
        bool TryGetEmbarkSnapshot(out EmbarkSnapshotPayload snapshot);

        bool TryExecuteEmbarkAction(
            EmbarkActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
