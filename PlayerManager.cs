using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;

namespace PlayerBanMod
{
    public interface IPlayerManager
    {
        Dictionary<string, string> GetConnectedPlayers();
        void UpdatePlayerList();
        
        bool IsPlayerHost(string steamId);
        string LocalSteamId { get; }
        string LocalPlayerName { get; }
        bool WasRecentlyKicked(string steamId);
        void MarkRecentlyKicked(string steamId);
        void RemoveRecentlyKicked(string steamId);
        void ClearOnLeaveLobby();
    }

    public class PlayerManager : IPlayerManager
    {
        private readonly ManualLogSource logger;

        private readonly Dictionary<string, string> connectedPlayers = new Dictionary<string, string>(); // name -> steamId
        private readonly Dictionary<string, float> recentlyKickedPlayers = new Dictionary<string, float>(); // steamId -> time

        private const float RECENTLY_KICKED_DURATION = Constants.RecentlyKickedDurationSeconds; // seconds

        public string LocalSteamId { get; private set; }
        public string LocalPlayerName { get; private set; }

        public PlayerManager(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public Dictionary<string, string> GetConnectedPlayers()
        {
            return connectedPlayers;
        }

        public bool WasRecentlyKicked(string steamId)
        {
            return recentlyKickedPlayers.ContainsKey(steamId);
        }

        public void MarkRecentlyKicked(string steamId)
        {
            recentlyKickedPlayers[steamId] = Time.time;
        }

        public void RemoveRecentlyKicked(string steamId)
        {
            recentlyKickedPlayers.Remove(steamId);
        }

        public void ClearOnLeaveLobby()
        {
            connectedPlayers.Clear();
            recentlyKickedPlayers.Clear();
        }

        public void UpdatePlayerList()
        {
            try
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {

                    connectedPlayers.Clear();

                    // Update local player info
                    LocalSteamId = SteamUser.GetSteamID().ToString();
                    LocalPlayerName = SteamFriends.GetPersonaName();

                    // Remove expired recently kicked entries
                    float now = Time.time;
                    var expired = new List<string>();
                    foreach (var kvp in recentlyKickedPlayers)
                    {
                        if (now - kvp.Value > RECENTLY_KICKED_DURATION)
                        {
                            expired.Add(kvp.Key);
                        }
                    }
                    foreach (var id in expired)
                    {
                        recentlyKickedPlayers.Remove(id);
                    }

                    // Add connected players from lobby UI
                    foreach (var kvp in mainMenuManager.kickplayershold.nametosteamid)
                    {
                        string playerName = kvp.Key;
                        string steamId = kvp.Value;

                        if (steamId != LocalSteamId && !recentlyKickedPlayers.ContainsKey(steamId))
                        {
                            connectedPlayers[playerName] = steamId;
                        }
                    }

                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error updating player list: {e.Message}");
            }
        }

        public bool IsPlayerHost(string steamId)
        {
            try
            {
                if (string.IsNullOrEmpty(steamId)) return false;

                if (steamId == LocalSteamId) return true;

                CSteamID lobbyId = new CSteamID(BootstrapManager.CurrentLobbyID);
                CSteamID ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId);

                if (ulong.TryParse(steamId, out ulong playerSteamUlong))
                {
                    CSteamID playerId = new CSteamID(playerSteamUlong);
                    return ownerId == playerId;
                }
                else
                {
                    logger.LogWarning($"Could not parse Steam ID: {steamId}");
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error checking if player is host: {e.Message}");
            }

            return false;
        }
    }
}