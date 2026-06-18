using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace DD2SteamMultiplayerHost
{
    internal sealed class SteamLobbyClient : IDisposable
    {
        public const string LocalLobbyVersion = "dd2-steam-mp-host-115";
        private const string LobbyDataVersionKey = "dd2_mp_version";
        private const string LobbyDataHostKey = "host_steam_id";
        private const int MaxRichPresenceSlots = 4;

        private readonly CallResult<LobbyCreated_t> _lobbyCreated;
        private readonly CallResult<LobbyEnter_t> _lobbyEntered;
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly Callback<LobbyChatUpdate_t> _lobbyChatUpdated;

        private CSteamID _currentLobby = CSteamID.Nil;
        private string _currentLobbyVersion = string.Empty;
        private bool _richPresencePvpMode;
        private string _lastRichPresenceDigest = string.Empty;

        public SteamLobbyClient()
        {
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _lobbyChatUpdated = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);

            Log("Steam lobby client initialized. OverlayEnabled=" + SafeIsOverlayEnabled());
        }

        public bool IsInLobby
        {
            get { return _currentLobby.IsValid(); }
        }

        public CSteamID CurrentLobby
        {
            get { return _currentLobby; }
        }

        public CSteamID Owner
        {
            get { return IsInLobby ? SteamMatchmaking.GetLobbyOwner(_currentLobby) : CSteamID.Nil; }
        }

        public bool IsHost
        {
            get { return IsInLobby && Owner == SteamUser.GetSteamID(); }
        }

        public string CurrentLobbyVersion
        {
            get { return _currentLobbyVersion; }
        }

        public bool IsLobbyVersionCompatible
        {
            get
            {
                return !IsInLobby ||
                    string.IsNullOrEmpty(_currentLobbyVersion) ||
                    string.Equals(_currentLobbyVersion, LocalLobbyVersion, StringComparison.Ordinal);
            }
        }

        public event Action LobbyReady;

        public event Action<CSteamID, EChatMemberStateChange> MemberStateChanged;

        public void CreateLobby(int maxMembers)
        {
            if (IsInLobby)
            {
                Log("Create lobby ignored; already in lobby " + _currentLobby.m_SteamID + ".");
                DumpLobby();
                return;
            }

            int clampedMaxMembers = Math.Max(2, Math.Min(4, maxMembers));
            Log("Creating friends-only Steam lobby for " + clampedMaxMembers + " players.");

            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, clampedMaxMembers);
            _lobbyCreated.Set(call);
        }

        public void JoinLobby(CSteamID lobbyId)
        {
            if (!lobbyId.IsValid())
            {
                Log("Join lobby ignored; invalid lobby id.");
                return;
            }

            if (IsInLobby && _currentLobby == lobbyId)
            {
                Log("Join lobby ignored; already in lobby " + _currentLobby.m_SteamID + ".");
                return;
            }

            if (IsInLobby)
            {
                LeaveLobby(null);
            }

            Log("Joining Steam lobby " + lobbyId.m_SteamID + ".");
            SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobbyId);
            _lobbyEntered.Set(call);
        }

        public void JoinLobby(ulong lobbyId)
        {
            JoinLobby(new CSteamID(lobbyId));
        }

        public void OpenInviteDialog()
        {
            if (!IsInLobby)
            {
                Log("Invite overlay ignored; create or join a lobby first.");
                return;
            }

            Log("Opening Steam invite overlay for lobby " + _currentLobby.m_SteamID + ".");
            SteamFriends.ActivateGameOverlayInviteDialog(_currentLobby);
        }

        public void LeaveLobby(SteamMessageTransport transport)
        {
            if (!IsInLobby)
            {
                Log("Leave lobby ignored; no active lobby.");
                return;
            }

            if (transport != null)
            {
                IReadOnlyList<CSteamID> members = GetMembers();
                for (int i = 0; i < members.Count; i++)
                {
                    transport.CloseSession(members[i]);
                }
            }

            Log("Leaving Steam lobby " + _currentLobby.m_SteamID + ".");
            SteamMatchmaking.LeaveLobby(_currentLobby);
            _currentLobby = CSteamID.Nil;
            _currentLobbyVersion = string.Empty;
            _richPresencePvpMode = false;
            ClearMultiplayerRichPresence();
        }

        public bool BroadcastText(SteamMessageTransport transport, string text)
        {
            if (!IsInLobby)
            {
                Log("Say ignored; no active lobby.");
                return false;
            }

            if (transport == null)
            {
                Log("Say ignored; message transport is not initialized.");
                return false;
            }

            IReadOnlyList<CSteamID> members = GetMembers();
            int sent = 0;
            for (int i = 0; i < members.Count; i++)
            {
                CSteamID member = members[i];
                if (member == SteamUser.GetSteamID())
                {
                    continue;
                }

                if (transport.SendText(member, text))
                {
                    sent++;
                }
            }

            Log("[me] " + text + " (sentTo=" + sent + ")");
            return sent > 0 || members.Count == 1;
        }

        public bool SendRaw(CSteamID target, SteamMessageTransport transport, string text)
        {
            if (!IsInLobby)
            {
                Log("Send ignored; no active lobby.");
                return false;
            }

            if (transport == null)
            {
                Log("Send ignored; message transport is not initialized.");
                return false;
            }

            return transport.SendText(target, text);
        }

        public int BroadcastRaw(SteamMessageTransport transport, string text, bool includeSelf)
        {
            if (!IsInLobby)
            {
                Log("Broadcast ignored; no active lobby.");
                return 0;
            }

            if (transport == null)
            {
                Log("Broadcast ignored; message transport is not initialized.");
                return 0;
            }

            IReadOnlyList<CSteamID> members = GetMembers();
            int sent = 0;
            for (int i = 0; i < members.Count; i++)
            {
                CSteamID member = members[i];
                if (!includeSelf && member == SteamUser.GetSteamID())
                {
                    continue;
                }

                if (transport.SendText(member, text))
                {
                    sent++;
                }
            }

            return sent;
        }

        public void DumpLobby()
        {
            if (!IsInLobby)
            {
                Log("Lobby state: no active lobby.");
                return;
            }

            CSteamID owner = SteamMatchmaking.GetLobbyOwner(_currentLobby);
            Log("Lobby state: lobby=" + _currentLobby.m_SteamID +
                ", owner=" + GetPersonaName(owner) + "/" + owner.m_SteamID +
                ", isHost=" + (owner == SteamUser.GetSteamID()) +
                ", lobbyVersion=" + (string.IsNullOrEmpty(_currentLobbyVersion) ? "[unknown]" : _currentLobbyVersion) +
                ", localVersion=" + LocalLobbyVersion +
                ", compatible=" + IsLobbyVersionCompatible + ".");

            IReadOnlyList<CSteamID> members = GetMembers();
            for (int i = 0; i < members.Count; i++)
            {
                CSteamID member = members[i];
                string suffix = member == SteamUser.GetSteamID() ? " (me)" : string.Empty;
                Log("Lobby member[" + i + "]: " + GetPersonaName(member) + "/" + member.m_SteamID + suffix);
            }
        }

        public void RefreshMultiplayerRichPresence(bool pvpMode)
        {
            _richPresencePvpMode = pvpMode;
            if (!IsInLobby)
            {
                ClearMultiplayerRichPresence();
                return;
            }

            int memberCount = Math.Max(1, GetMembers().Count);
            string lobbyId = _currentLobby.m_SteamID.ToString();
            string status = pvpMode
                ? "PVP-1/1"
                : "\u5408\u4f5c-" + Math.Min(memberCount, MaxRichPresenceSlots) + "/" + MaxRichPresenceSlots;
            string groupSize = memberCount.ToString();
            string connect = "+connect_lobby " + lobbyId;
            string digest = lobbyId + "|" + memberCount + "|" + pvpMode + "|" + status;

            if (string.Equals(_lastRichPresenceDigest, digest, StringComparison.Ordinal))
            {
                return;
            }

            _lastRichPresenceDigest = digest;
            SafeSetRichPresence("status", status);
            SafeSetRichPresence("connect", connect);
            SafeSetRichPresence("steam_player_group", lobbyId);
            SafeSetRichPresence("steam_player_group_size", groupSize);
            Log("Steam rich presence updated: status=" + status +
                ", group=" + lobbyId +
                ", groupSize=" + groupSize +
                ", connect=" + connect + ".");
        }

        public void Dispose()
        {
            LeaveLobby(null);

            _lobbyCreated.Dispose();
            _lobbyEntered.Dispose();
            _joinRequested.Dispose();
            _lobbyChatUpdated.Dispose();
        }

        private void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure)
        {
            if (ioFailure || callback.m_eResult != EResult.k_EResultOK)
            {
                Log("Lobby creation failed: ioFailure=" + ioFailure + ", result=" + callback.m_eResult + ".");
                return;
            }

            _currentLobby = new CSteamID(callback.m_ulSteamIDLobby);
            _currentLobbyVersion = LocalLobbyVersion;
            SteamMatchmaking.SetLobbyData(_currentLobby, LobbyDataVersionKey, LocalLobbyVersion);
            SteamMatchmaking.SetLobbyData(_currentLobby, LobbyDataHostKey, SteamUser.GetSteamID().m_SteamID.ToString());
            SteamMatchmaking.SetLobbyJoinable(_currentLobby, true);

            Log("Lobby created: " + _currentLobby.m_SteamID + ".");
            DumpLobby();
            RefreshMultiplayerRichPresence(false);
            Action handler = LobbyReady;
            if (handler != null)
            {
                handler();
            }
        }

        private void OnLobbyEntered(LobbyEnter_t callback, bool ioFailure)
        {
            if (ioFailure || callback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Log("Lobby join failed: ioFailure=" + ioFailure + ", response=" + callback.m_EChatRoomEnterResponse + ".");
                return;
            }

            _currentLobby = new CSteamID(callback.m_ulSteamIDLobby);
            string version = SteamMatchmaking.GetLobbyData(_currentLobby, LobbyDataVersionKey);
            _currentLobbyVersion = version ?? string.Empty;
            if (!string.IsNullOrEmpty(version) && version != LocalLobbyVersion)
            {
                Log("Joined lobby " + _currentLobby.m_SteamID + " with remote version " + version + "; local version is " + LocalLobbyVersion + ".");
            }
            else
            {
                Log("Joined lobby: " + _currentLobby.m_SteamID +
                    " version=" + (string.IsNullOrEmpty(version) ? "[unknown]" : version) + ".");
            }

            DumpLobby();
            RefreshMultiplayerRichPresence(false);
            Action handler = LobbyReady;
            if (handler != null)
            {
                handler();
            }
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            Log("Steam invite join requested for lobby " + callback.m_steamIDLobby.m_SteamID + ".");
            JoinLobby(callback.m_steamIDLobby);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            if (!IsInLobby || callback.m_ulSteamIDLobby != _currentLobby.m_SteamID)
            {
                return;
            }

            CSteamID changedUser = new CSteamID(callback.m_ulSteamIDUserChanged);
            EChatMemberStateChange state = (EChatMemberStateChange)callback.m_rgfChatMemberStateChange;
            Log("Lobby member update: " + GetPersonaName(changedUser) + "/" + changedUser.m_SteamID + " => " + state + ".");
            DumpLobby();
            RefreshMultiplayerRichPresence(_richPresencePvpMode);
            Action<CSteamID, EChatMemberStateChange> handler = MemberStateChanged;
            if (handler != null)
            {
                handler(changedUser, state);
            }
        }

        public IReadOnlyList<CSteamID> GetMembers()
        {
            if (!IsInLobby)
            {
                return Array.Empty<CSteamID>();
            }

            int count = SteamMatchmaking.GetNumLobbyMembers(_currentLobby);
            List<CSteamID> members = new List<CSteamID>(count);
            for (int i = 0; i < count; i++)
            {
                members.Add(SteamMatchmaking.GetLobbyMemberByIndex(_currentLobby, i));
            }

            return members;
        }

        public bool TryFindMember(string query, out CSteamID member)
        {
            member = CSteamID.Nil;
            if (!IsInLobby || string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string trimmed = query.Trim();
            if (string.Equals(trimmed, "me", StringComparison.OrdinalIgnoreCase))
            {
                member = SteamUser.GetSteamID();
                return true;
            }

            if (ulong.TryParse(trimmed, out ulong steamId))
            {
                CSteamID candidate = new CSteamID(steamId);
                foreach (CSteamID lobbyMember in GetMembers())
                {
                    if (lobbyMember == candidate)
                    {
                        member = lobbyMember;
                        return true;
                    }
                }

                return false;
            }

            foreach (CSteamID lobbyMember in GetMembers())
            {
                string personaName = GetPersonaName(lobbyMember);
                if (personaName.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    member = lobbyMember;
                    return true;
                }
            }

            return false;
        }

        private static bool SafeIsOverlayEnabled()
        {
            try
            {
                return SteamUtils.IsOverlayEnabled();
            }
            catch
            {
                return false;
            }
        }

        private void ClearMultiplayerRichPresence()
        {
            if (string.IsNullOrEmpty(_lastRichPresenceDigest))
            {
                return;
            }

            _lastRichPresenceDigest = string.Empty;
            SafeSetRichPresence("status", string.Empty);
            SafeSetRichPresence("connect", string.Empty);
            SafeSetRichPresence("steam_player_group", string.Empty);
            SafeSetRichPresence("steam_player_group_size", string.Empty);
            Log("Steam rich presence cleared for DD2SteamMP lobby.");
        }

        private static void SafeSetRichPresence(string key, string value)
        {
            try
            {
                if (!SteamFriends.SetRichPresence(key, value ?? string.Empty))
                {
                    Log("Steam rich presence rejected key=" + key + ".");
                }
            }
            catch (Exception ex)
            {
                Log("Steam rich presence failed key=" + key + ": " + ex.Message);
            }
        }

        public string GetPersonaName(CSteamID steamId)
        {
            string personaName = SteamFriends.GetFriendPersonaName(steamId);
            return string.IsNullOrEmpty(personaName) ? "[unknown]" : personaName;
        }

        private static void Log(string message)
        {
            Debug.Log("[DD2SteamMP] " + message);
            HostLog.Write(message);
        }
    }
}
