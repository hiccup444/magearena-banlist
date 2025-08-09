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
        void AddFakePlayersForTesting();
        void ClearFakePlayersForTesting();
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
        private readonly Dictionary<string, string> fakePlayers = new Dictionary<string, string>(); // name -> steamId (for testing)
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
            fakePlayers.Clear();
            recentlyKickedPlayers.Clear();
        }

        public void UpdatePlayerList()
        {
            try
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    // Preserve current fake players
                    var currentFakePlayers = new Dictionary<string, string>(fakePlayers);

                    // Reset and repopulate with real players
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

                    // Restore fake players (unless recently kicked)
                    foreach (var kvp in currentFakePlayers)
                    {
                        string playerName = kvp.Key;
                        string steamId = kvp.Value;

                        if (!recentlyKickedPlayers.ContainsKey(steamId))
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

        public void AddFakePlayersForTesting()
        {
            try
            {
                string[] fakeNames = {
                    "TestPlayer1", "DebugUser", "FakePlayer123", "MockGamer",
                    "TestDummy", "BotPlayer", "SampleUser", "DemoPlayer",
                    "TestUser42", "DebugBot", "FakeGamer", "MockUser"
                };

                int playersToAdd = UnityEngine.Random.Range(3, 8);
                for (int i = 0; i < playersToAdd; i++)
                {
                    string playerName = fakeNames[UnityEngine.Random.Range(0, fakeNames.Length)] + "_" + UnityEngine.Random.Range(100, 999);
                    string fakeSteamId = "7656119" + UnityEngine.Random.Range(10000000, 99999999).ToString();

                    while (connectedPlayers.ContainsKey(playerName) || connectedPlayers.ContainsValue(fakeSteamId) ||
                           fakePlayers.ContainsKey(playerName) || fakePlayers.ContainsValue(fakeSteamId))
                    {
                        playerName = fakeNames[UnityEngine.Random.Range(0, fakeNames.Length)] + "_" + UnityEngine.Random.Range(100, 999);
                        fakeSteamId = "7656119" + UnityEngine.Random.Range(10000000, 99999999).ToString();
                    }

                    fakePlayers[playerName] = fakeSteamId;
                    connectedPlayers[playerName] = fakeSteamId;
                }

                logger.LogInfo($"Added {playersToAdd} fake players for testing. Total players: {connectedPlayers.Count} (including {fakePlayers.Count} fake)");
            }
            catch (Exception e)
            {
                logger.LogError($"Error adding fake players: {e.Message}");
            }
        }

        public void ClearFakePlayersForTesting()
        {
            try
            {
                int fakeCount = fakePlayers.Count;
                foreach (var kvp in fakePlayers)
                {
                    connectedPlayers.Remove(kvp.Key);
                    recentlyKickedPlayers.Remove(kvp.Value);
                }
                fakePlayers.Clear();

                logger.LogInfo($"Cleared {fakeCount} fake players for testing. Remaining players: {connectedPlayers.Count}");
            }
            catch (Exception e)
            {
                logger.LogError($"Error clearing fake players: {e.Message}");
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


