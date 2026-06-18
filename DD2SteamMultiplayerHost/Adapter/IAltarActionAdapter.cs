using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IAltarActionAdapter
    {
        bool TryGetAltarSnapshot(out AltarSnapshotPayload snapshot);

        bool TryExecuteAltarAction(
            AltarActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
