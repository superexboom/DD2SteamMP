using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface ILairDecisionAdapter
    {
        bool TryGetLairDecisionSnapshot(out LairDecisionSnapshotPayload snapshot);

        bool TryExecuteLairDecision(
            LairDecisionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
