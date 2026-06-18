using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IMainMenuActionAdapter
    {
        bool TryGetMainMenuSnapshot(out MainMenuSnapshotPayload snapshot);

        bool TryExecuteMainMenuAction(
            MainMenuActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
