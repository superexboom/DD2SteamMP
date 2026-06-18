using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IConfessionChoiceAdapter
    {
        bool TryGetConfessionChoiceSnapshot(out ConfessionChoiceSnapshotPayload snapshot);

        bool TryExecuteConfessionChoice(
            ConfessionChoiceRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
