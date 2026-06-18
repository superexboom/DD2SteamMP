using DD2SteamMultiplayerHost.Protocol;

namespace DD2SteamMultiplayerHost.Adapter
{
    internal interface IStoryChoiceAdapter
    {
        bool TryExecuteStoryChoice(
            StoryChoiceRequestPayload request,
            ulong senderSteamId,
            string senderName,
            out string message);
    }
}
