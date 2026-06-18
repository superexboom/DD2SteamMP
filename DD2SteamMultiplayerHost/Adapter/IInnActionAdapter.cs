using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IInnActionAdapter
    {
        bool TryGetInnSnapshot(out InnSnapshotPayload snapshot);

        bool TryExecuteInnAction(
            InnActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
