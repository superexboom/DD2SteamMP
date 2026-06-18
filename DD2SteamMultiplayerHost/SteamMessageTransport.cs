using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using UnityEngine;

namespace DD2SteamMultiplayerHost
{
    internal sealed class SteamMessageTransport : IDisposable
    {
        private const int Channel = 0;
        private const int MaxMessagesPerPoll = 32;

        private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
        private readonly IntPtr[] _receiveBuffer = new IntPtr[MaxMessagesPerPoll];
        private readonly HashSet<ulong> _knownPeers = new HashSet<ulong>();

        public SteamMessageTransport()
        {
            _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            Log("Steam Networking message transport initialized.");
        }

        public event Action<SteamTextMessage> MessageReceived;

        public bool SendText(CSteamID target, string text)
        {
            if (!target.IsValid())
            {
                Log("Send ignored; invalid Steam target.");
                return false;
            }

            if (target == SteamUser.GetSteamID())
            {
                return true;
            }

            byte[] payload = Encoding.UTF8.GetBytes(text ?? string.Empty);
            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(target.m_SteamID);

            GCHandle handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                IntPtr data = handle.AddrOfPinnedObject();
                EResult result = SteamNetworkingMessages.SendMessageToUser(
                    ref identity,
                    data,
                    (uint)payload.Length,
                    Constants.k_nSteamNetworkingSend_Reliable,
                    Channel);

                if (result != EResult.k_EResultOK)
                {
                    Log("SendMessageToUser failed for " + target.m_SteamID + ": " + result + ".");
                    return false;
                }

                _knownPeers.Add(target.m_SteamID);
                return true;
            }
            finally
            {
                handle.Free();
            }
        }

        public void PollIncoming()
        {
            int received = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _receiveBuffer, _receiveBuffer.Length);
            for (int i = 0; i < received; i++)
            {
                IntPtr messagePtr = _receiveBuffer[i];
                if (messagePtr == IntPtr.Zero)
                {
                    continue;
                }

                SteamNetworkingMessage_t message = (SteamNetworkingMessage_t)Marshal.PtrToStructure(messagePtr, typeof(SteamNetworkingMessage_t));
                try
                {
                    int payloadLength = checked((int)message.m_cbSize);
                    byte[] payload = new byte[payloadLength];
                    Marshal.Copy(message.m_pData, payload, 0, payload.Length);

                    ulong senderId64 = message.m_identityPeer.GetSteamID64();
                    CSteamID senderId = new CSteamID(senderId64);
                    _knownPeers.Add(senderId64);

                    string text = Encoding.UTF8.GetString(payload);
                    string senderName = SteamFriends.GetFriendPersonaName(senderId);
                    if (string.IsNullOrEmpty(senderName))
                    {
                        senderName = "[unknown]";
                    }

                    Action<SteamTextMessage> handler = MessageReceived;
                    if (handler != null)
                    {
                        handler(new SteamTextMessage(senderId, senderName, text));
                    }
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(messagePtr);
                    _receiveBuffer[i] = IntPtr.Zero;
                }
            }
        }

        public void CloseSession(CSteamID peer)
        {
            if (!peer.IsValid() || peer == SteamUser.GetSteamID())
            {
                return;
            }

            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(peer.m_SteamID);
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            _knownPeers.Remove(peer.m_SteamID);
        }

        public void Dispose()
        {
            foreach (ulong peerId in _knownPeers.ToArray())
            {
                CloseSession(new CSteamID(peerId));
            }

            _sessionRequest.Dispose();
            _sessionFailed.Dispose();
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t callback)
        {
            SteamNetworkingIdentity identity = callback.m_identityRemote;
            ulong steamId = identity.GetSteamID64();
            bool accepted = SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            _knownPeers.Add(steamId);
            Log("Steam Networking session request from " + steamId + ": accepted=" + accepted + ".");
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t callback)
        {
            ulong steamId = callback.m_info.m_identityRemote.GetSteamID64();
            Log("Steam Networking session failed for " + steamId + ": " + callback.m_info.m_eEndReason + " " + callback.m_info.m_szEndDebug + ".");
        }

        private static void Log(string message)
        {
            Debug.Log("[DD2SteamMP] " + message);
            HostLog.Write(message);
        }
    }
}
