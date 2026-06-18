using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IStagecoachActionAdapter
    {
        bool TryGetStagecoachSnapshot(out StagecoachSnapshotPayload snapshot);

        bool TryExecuteStagecoachAction(
            StagecoachActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
