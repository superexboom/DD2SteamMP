using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IRouteChoiceAdapter
    {
        bool TryExecuteRouteChoice(
            RouteChoiceRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
