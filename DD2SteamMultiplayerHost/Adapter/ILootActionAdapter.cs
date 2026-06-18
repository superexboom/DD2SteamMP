using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface ILootActionAdapter
    {
        bool TryExecuteLootAction(
            LootActionRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
