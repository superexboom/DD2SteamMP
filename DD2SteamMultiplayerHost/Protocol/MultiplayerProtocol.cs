using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Steamworks;

namespace DD2SteamMultiplayerHost.Protocol
{
    internal static class MultiplayerProtocol
    {
        public const int CurrentVersion = 70;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter() },
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(JsonSettings);

        public static string Serialize<TPayload>(MultiplayerMessageType type, long sequence, TPayload payload)
        {
            MultiplayerEnvelope envelope = new MultiplayerEnvelope
            {
                Version = CurrentVersion,
                Type = type,
                SenderSteamId = SteamUser.GetSteamID().m_SteamID,
                SenderName = SteamFriends.GetPersonaName() ?? string.Empty,
                Sequence = sequence,
                SentAtUtc = DateTime.UtcNow,
                Payload = payload == null ? JValue.CreateNull() : JToken.FromObject(payload, Serializer),
            };

            return JsonConvert.SerializeObject(envelope, JsonSettings);
        }

        public static bool TryDeserialize(string text, out MultiplayerEnvelope envelope, out string error)
        {
            envelope = null;
            error = string.Empty;

            try
            {
                envelope = JsonConvert.DeserializeObject<MultiplayerEnvelope>(text, JsonSettings);
                if (envelope == null)
                {
                    error = "empty envelope";
                    return false;
                }

                if (envelope.Version != CurrentVersion)
                {
                    error = "unsupported protocol version " + envelope.Version +
                        "; local protocol version is " + CurrentVersion;
                    return false;
                }

                if (envelope.Payload == null)
                {
                    envelope.Payload = JValue.CreateNull();
                }

                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryReadPayload<TPayload>(MultiplayerEnvelope envelope, out TPayload payload, out string error)
            where TPayload : class
        {
            payload = null;
            error = string.Empty;

            try
            {
                payload = envelope.Payload.ToObject<TPayload>(Serializer);
                if (payload == null)
                {
                    error = "empty payload";
                    return false;
                }

                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
