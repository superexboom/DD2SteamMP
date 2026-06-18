using System;
using Newtonsoft.Json.Linq;

namespace DD2SteamMultiplayerHost.Protocol
{
    internal sealed class MultiplayerEnvelope
    {
        public int Version { get; set; }

        public MultiplayerMessageType Type { get; set; }

        public ulong SenderSteamId { get; set; }

        public string SenderName { get; set; }

        public long Sequence { get; set; }

        public DateTime SentAtUtc { get; set; }

        public JToken Payload { get; set; }
    }
}
