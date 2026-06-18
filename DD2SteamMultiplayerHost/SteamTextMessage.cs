using Steamworks;

namespace DD2SteamMultiplayerHost
{
    internal sealed class SteamTextMessage
    {
        public SteamTextMessage(CSteamID senderId, string senderName, string text)
        {
            SenderId = senderId;
            SenderName = senderName;
            Text = text;
        }

        public CSteamID SenderId { get; }

        public string SenderName { get; }

        public string Text { get; }
    }
}
