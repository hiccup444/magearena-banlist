using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Steamworks;

namespace PlayerBanMod
{
    [BepInPlugin("com.playerban.mod", "Player Ban Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    public class PlayerBanMod : BaseUnityPlugin
    {
        private static ManualLogSource ModLogger;
        private Harmony harmony;
        private static PlayerBanMod instance;
        public static PlayerBanMod Instance => instance;
        private IBanDataManager banDataManager;

        // Configuration
        private ConfigEntry<string> bannedPlayersConfig;
        private ConfigEntry<bool> autobanModdedRanksConfig;
        private ConfigEntry<bool> autobanOffensiveNamesConfig;
        private ConfigEntry<string> offensiveNamesConfig;
        public Dictionary<string, string> bannedPlayers = new Dictionary<string, string>(); // steamId -> playerName
        private Dictionary<string, DateTime> banTimestamps = new Dictionary<string, DateTime>(); // steamId -> ban timestamp
        private Dictionary<string, string> banReasons = new Dictionary<string, string>(); // steamId -> ban reason

        // UI managed fully by BanUIManager
        private bool isHost = false;
        private bool isInLobby = false;
        private ILobbyMonitor lobbyMonitor;

        // Player tracking
        private IPlayerManager playerManager;
        
        // Debug/testing
        private bool debugMode = true; // Set to false for production

        // Ban kick tracking
        private IKickSystem kickSystem;
        private const float RECENTLY_KICKED_DURATION = Constants.RecentlyKickedDurationSeconds; // 10 seconds to track recently kicked players

        // Batched saving
        private bool needsSave = false;
        private float lastSaveTime = 0f;
        private const float SAVE_INTERVAL = Constants.SaveIntervalSeconds; // Save every 5 seconds if needed

        // Virtualized UI for large ban lists
        private const int MAX_VISIBLE_BANNED_ITEMS = Constants.MaxVisibleBannedItems; // Only show 50 at a time
        private int bannedPlayersPageIndex = 0;
        private string bannedPlayersSearchQuery = "";

        // For parsing the config file
        private const string FIELD_DELIMITER = Constants.FieldDelimiter; // Diamond symbol - very unlikely in Steam names
        private const string ENTRY_DELIMITER = Constants.EntryDelimiter; // Reference mark - very unlikely in Steam names

        

        // Helper method to check if a player is the host
        public bool IsPlayerHost(string steamId) => playerManager.IsPlayerHost(steamId);
        public bool IsCurrentHost() => isHost;
        public bool IsInCurrentLobby() => isInLobby;
        public bool IsBanned(string steamId) => bannedPlayers.ContainsKey(steamId);
        public void KickBannedNow(string steamId, string name) => KickPlayerWithRetry(steamId, name, true);
        public static void LogInfoStatic(string message) => ModLogger?.LogInfo(message);
        public static void LogErrorStatic(string message) => ModLogger?.LogError(message);

        private void Awake()
        {
            ModLogger = BepInEx.Logging.Logger.CreateLogSource("PlayerBanMod");
            harmony = new Harmony("com.playerban.mod");
            instance = this;

            // Load banned players from config
            bannedPlayersConfig = Config.Bind("BannedPlayers", "BannedSteamIds", "", "Comma-separated list of banned Steam IDs");
            autobanModdedRanksConfig = Config.Bind("Settings", "AutobanModdedRanks", false, "Automatically ban players with modded ranks");
            autobanOffensiveNamesConfig = Config.Bind("Settings", "AutobanOffensiveNames", false, "Automatically ban players with offensive names");
            offensiveNamesConfig = Config.Bind("Settings", "OffensiveNames", "discord.gg,cheat", "Comma-separated list of offensive names to ban");
            
            // Initialize offensive names with default values if empty
            if (string.IsNullOrEmpty(offensiveNamesConfig.Value))
            {
                offensiveNamesConfig.Value = "discord.gg,cheat";
            }
            
            // Initialize data manager and async load for very large lists
            banDataManager = new BanDataManager(
                bannedPlayers,
                banTimestamps,
                banReasons,
                bannedPlayersConfig,
                ModLogger,
                steamId => IsPlayerHost(steamId),
                () => isHost,
                () => isInLobby,
                (id, name, isBanned) => KickPlayerWithRetry(id, name, isBanned),
                () => { needsSave = true; },
                () => { if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers(); },
                () => { if (BanUIManager.IsActive) BanUIManager.RefreshBannedPlayers(); },
                id => { kickSystem?.ClearKickTracking(id); },
                id => { playerManager?.RemoveRecentlyKicked(id); }
            );
            // Initialize player manager
            playerManager = new PlayerManager(ModLogger);
            lobbyMonitor = new LobbyMonitor(ModLogger);
            kickSystem = new KickSystem(ModLogger, this, () => IsGameActive(), () => isHost, () => isInLobby, playerManager);
            autoBanSystem = new AutoBanSystem(ModLogger, () => IsInLobbyScreen(), autobanModdedRanksConfig, autobanOffensiveNamesConfig, offensiveNamesConfig, banDataManager, playerManager);
            // Initialize UI manager dependencies
            BanUIManager.Initialize(
                ModLogger,
                autobanModdedRanksConfig,
                autobanOffensiveNamesConfig,
                () => playerManager.GetConnectedPlayers(),
                () => bannedPlayers,
                () => banTimestamps,
                () => banReasons,
                (steamId, playerName) => KickPlayer(steamId, playerName),
                (steamId, playerName) => ToggleBanPlayer(steamId, playerName),
                (steamId, playerName) => UnbanPlayer(steamId, playerName)
            );
            StartCoroutine(banDataManager.LoadBansAsync());

            // Apply Harmony patches
            harmony.PatchAll();

            ModLogger.LogInfo("Player Ban Mod loaded!");
            
            if (debugMode)
            {
                ModLogger.LogInfo("Debug mode enabled - F3: Add fake players, F4: Clear fake players");
            }
            
            // Start coroutine to initialize after game loads
            StartCoroutine(InitializeMod());
        }

        private System.Collections.IEnumerator InitializeMod()
        {
            // Wait for game to fully load
            yield return new WaitForSeconds(2f);

            // Create UI
            StartCoroutine(BanUIManager.CreateUI());

            // Start monitoring for lobby and host status
            StartCoroutine(MonitorLobbyStatus());
        }

        private System.Collections.IEnumerator MonitorLobbyStatus()
        {
            yield return lobbyMonitor.MonitorLoop(
                onEnterLobby: () => { isInLobby = true; OnEnterLobby(); },
                onLeaveLobby: () => { isInLobby = false; OnLeaveLobby(); },
                onHostChanged: (hostNow) => { isHost = hostNow; UpdateUIForHostStatus(); },
                onTickInLobby: () => { playerManager.UpdatePlayerList(); CheckForBannedPlayersInLobby(); }
            );
        }

        private IAutoBanSystem autoBanSystem;

        private void CheckForBannedPlayersInLobby()
        {
            if (!isHost || !isInLobby) return;

            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    foreach (var kvp in mainMenuManager.kickplayershold.nametosteamid)
                    {
                        string playerName = kvp.Key;
                        string steamId = kvp.Value;

                        // Don't kick ourselves
                        if (steamId == playerManager.LocalSteamId) continue;

                        if (IsPlayerHost(steamId)) continue;

                        // Delegate banned/auto-ban evaluation to LobbyMonitor wrapper
                        lobbyMonitor.CheckBannedOrAutoBanPlayer(
                            steamId,
                            playerName,
                            id => bannedPlayers.ContainsKey(id),
                            (id, name) => KickPlayerWithRetry(id, name, true),
                            (id, name) => autoBanSystem.ProcessPlayer(id, name, (bid, bname) => BanPlayer(bid, bname, "Automatic"))
                        );
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error checking for banned players: {e.Message}");
            }
        }

        // moved to AutoBanSystem
        private bool HasModdedRank(string steamId, string playerName) { return false; }
        private bool HasOffensiveName(string playerName) { return false; }

        private bool IsInLobbyScreen() { return lobbyMonitor != null && lobbyMonitor.IsInLobbyScreen(); }

        private bool IsGameActive() { return lobbyMonitor != null && lobbyMonitor.IsGameActive(); }

        private void BanPlayer(string steamId, string playerName, string reason) { banDataManager.BanPlayer(steamId, playerName, reason); }

        private void Update()
        {
            // Check for F2 key press to open/close the UI
            if (Input.GetKeyDown(KeyCode.F2))
            {
                if (isHost && isInLobby)
                {
                    {
                        bool newState = !BanUIManager.IsActive;
                        BanUIManager.SetActive(newState);
                        
                        // Handle cursor state...
                        if (IsGameActive())
                        {
                            if (newState)
                            {
                                Cursor.lockState = CursorLockMode.Confined;
                                Cursor.visible = true;
                            }
                            else
                            {
                                Cursor.lockState = CursorLockMode.Locked;
                                Cursor.visible = false;
                            }
                        }
                        
                        if (newState)
                        {
                            // Refresh the UI when opening
                            BanUIManager.RefreshActivePlayers();
                            BanUIManager.RefreshBannedPlayers();
                        }
                        
                        string gameState = IsGameActive() ? "in-game" : "in-lobby";
                        ModLogger.LogInfo($"Player Management UI {(newState ? "opened" : "closed")} via F2 key [{gameState}]");
                    }
                }
                else if (BanUIManager.IsActive)
                {
                    // Close UI if it's open but conditions no longer met
                    BanUIManager.SetActive(false);
                    
                    // If we're closing during game, restore cursor lock
                    if (IsGameActive())
                    {
                        Cursor.lockState = CursorLockMode.Locked;
                        Cursor.visible = false;
                    }
                    
                    ModLogger.LogInfo("Player Management UI closed - no longer host or in lobby");
                }
            }
            
            // Debug keys for testing (only in lobby screen, not during game)
            if (debugMode && isHost && isInLobby && !IsGameActive())
            {
                // F3 to add fake players
                if (Input.GetKeyDown(KeyCode.F3))
                {
                    playerManager.AddFakePlayersForTesting();
                    if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers();
                }
                
                // F4 to clear fake players
                if (Input.GetKeyDown(KeyCode.F4))
                {
                    playerManager.ClearFakePlayersForTesting();
                    if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers();
                }

                // F5 to generate test bans for pagination testing
                if (Input.GetKeyDown(KeyCode.F5))
                {
                    GenerateTestBansForPagination();
                }
            }

            // Batched saving - only save periodically
            if (needsSave && Time.time - lastSaveTime > SAVE_INTERVAL)
            {
                banDataManager.SaveBans();
                needsSave = false;
                lastSaveTime = Time.time;
            }
        }

        // moved to LobbyMonitor
        private bool CheckIfInLobby() { return lobbyMonitor != null && lobbyMonitor.IsInLobby; }
        private bool CheckIfHost() { return lobbyMonitor != null && lobbyMonitor.IsHost; }

        private void OnEnterLobby()
        {
            ModLogger.LogInfo("Entered lobby");
            UpdateUIForHostStatus();
            playerManager.UpdatePlayerList();
        }

        private void OnLeaveLobby()
        {
            ModLogger.LogInfo("Left lobby - clearing player data and UI");
            
            // Clear player-related data
            playerManager.ClearOnLeaveLobby();
            
            // Hide UI if visible
            if (BanUIManager.IsActive) BanUIManager.SetActive(false);
        }

        private void UpdateUIForHostStatus() { }

        private void UpdatePlayerList() { }

        private void AddFakePlayersForTesting() { }

        private void ClearFakePlayersForTesting() { }

        private void KickPlayerWithRetry(string steamId, string playerName, bool isBannedPlayer = false)
        {
            kickSystem.KickPlayerWithRetry(steamId, playerName, isBannedPlayer);
        }

        private System.Collections.IEnumerator ScheduleKickRetry(string steamId, string playerName, bool isBannedPlayer = false) { yield break; }

        private void KickPlayer(string steamId, string playerName)
        {
            // Use the enhanced kick method with retry logic (not a banned player)
            KickPlayerWithRetry(steamId, playerName, false);
        }

        private void ToggleBanPlayer(string steamId, string playerName) { banDataManager.ToggleBanPlayer(steamId, playerName); }

        private void UnbanPlayer(string steamId, string playerName) { banDataManager.UnbanPlayer(steamId); }

                 // Check if a joining player is banned
         private void CheckBannedPlayer(string steamId, string playerName)
         {
             if (IsPlayerHost(steamId))
             {
                 ModLogger.LogInfo($"Host player {playerName} (Steam ID: {steamId}) attempted to join - host is immune to bans and kicks");
                 return;
             }

             lobbyMonitor.CheckBannedOrAutoBanPlayer(
                 steamId,
                 playerName,
                 id => bannedPlayers.ContainsKey(id),
                 (id, name) => KickPlayerWithRetry(id, name, true),
                 (id, name) => autoBanSystem.ProcessPlayer(id, name, (bid, bname) => BanPlayer(bid, bname, "Automatic"))
             );
         }

        private void OnDestroy()
        {
            // Final save to ensure data isn't lost
            try
            {
                if (needsSave)
                {
                    banDataManager.SaveBans();
                    needsSave = false;
                }
            }
            catch {}
            if (harmony != null)
            {
                harmony.UnpatchSelf();
            }
        }

        // 4. ASYNC LOADING for very large lists
        private System.Collections.IEnumerator LoadBannedPlayersAsync() { return banDataManager.LoadBansAsync(); }

        private void ProcessBanEntry(string entry) { }

        // DEBUG METHOD

        private void GenerateTestBansForPagination()
        {
            try
            {
                ModLogger.LogInfo("Generating 60 test bans for pagination testing...");
                
                DateTime baseTime = DateTime.Now.AddDays(-10); // Start 10 days ago
                
                for (int i = 0; i < 60; i++)
                {
                    // Generate fake Steam ID that won't conflict with real ones
                    string fakeSteamId = "9999999" + i.ToString("D8"); // Creates IDs like 999999900000001, etc.
                    
                    // Generate test player name
                    string playerName = $"TestPlayer_{i:D3}";
                    
                    // Vary the ban times so sorting works properly
                    DateTime banTime = baseTime.AddMinutes(i * 30); // 30 minutes apart each
                    
                    // Vary ban reasons
                    string[] reasons = { "Testing", "Pagination", "Manual", "Automatic", "Griefing" };
                    string banReason = reasons[i % reasons.Length];
                    
                    // Only add if not already banned (avoid duplicates)
                    if (!bannedPlayers.ContainsKey(fakeSteamId))
                    {
                        bannedPlayers[fakeSteamId] = playerName;
                        banTimestamps[fakeSteamId] = banTime;
                        banReasons[fakeSteamId] = banReason;
                    }
                }
                
                // Mark for saving
                needsSave = true;
                
                ModLogger.LogInfo($"Generated test bans. Total banned players: {bannedPlayers.Count}");
                
                // Refresh UI if it's open
                if (BanUIManager.IsActive)
                {
                    BanUIManager.RefreshBannedPlayers();
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error generating test bans: {e.Message}");
            }
        }

        // Harmony patches moved to HarmonyPatches.cs
    }
}
