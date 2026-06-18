using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IConfirmationDialogAdapter
    {
        bool TryGetConfirmationDialogSnapshot(out ConfirmationDialogSnapshotPayload snapshot);

        bool TryExecuteConfirmationDialog(
            ConfirmationDialogRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
