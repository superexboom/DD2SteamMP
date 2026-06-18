using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IGameResultsActionAdapter
    {
        bool TryGetGameResultsSnapshot(out GameResultsSnapshotPayload snapshot);

        bool TryExecuteGameResultsAction(
            GameResultsActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
