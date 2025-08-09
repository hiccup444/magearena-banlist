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
    [BepInPlugin("com.playerban.mod", "Player Ban Mod", "1.0.4")]
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
        private ConfigEntry<bool> autobanFormattedNamesConfig;
        private ConfigEntry<string> offensiveNamesConfig;
        private ConfigEntry<KeyCode> uiToggleKeyConfig;
        public Dictionary<string, string> bannedPlayers = new Dictionary<string, string>(); // steamId -> playerName
        private Dictionary<string, DateTime> banTimestamps = new Dictionary<string, DateTime>(); // steamId -> ban timestamp
        private Dictionary<string, string> banReasons = new Dictionary<string, string>(); // steamId -> ban reason

        // UI managed fully by BanUIManager
        private bool isHost = false;
        private bool isInLobby = false;
        private ILobbyMonitor lobbyMonitor;

        // Player tracking
        private IPlayerManager playerManager;

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
        private const string FIELD_DELIMITER = Constants.FieldDelimiter; // Diamond symbol
        private const string ENTRY_DELIMITER = Constants.EntryDelimiter; // Reference mark

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
            autobanFormattedNamesConfig = Config.Bind("Settings", "AutobanFormattedNames", false, "Automatically ban players with formatted/rich-text names (e.g., <color>, <b>)");
            offensiveNamesConfig = Config.Bind("Settings", "OffensiveNames", "discord.gg,cheat", "Comma-separated list of offensive names to ban");
            uiToggleKeyConfig = Config.Bind("Settings", "UIToggleKey", KeyCode.F2, "Key to toggle the Player Management UI");
            
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
            Softlock.Initialize(ModLogger, this);
            autoBanSystem = new AutoBanSystem(ModLogger, () => IsInLobbyScreen(), () => IsGameActive(), autobanModdedRanksConfig, autobanOffensiveNamesConfig, autobanFormattedNamesConfig, offensiveNamesConfig, banDataManager, playerManager);
            RecentPlayersManager.Initialize(ModLogger);
            RecentPlayersManager.SetBanFunctions(() => bannedPlayers, (steamId, playerName) => ToggleBanPlayer(steamId, playerName));
            
            // Initialize UI manager dependencies
            BanUIManager.Initialize(
                ModLogger,
                autobanModdedRanksConfig,
                autobanOffensiveNamesConfig,
                autobanFormattedNamesConfig,
                () => playerManager.GetConnectedPlayers(),
                () => bannedPlayers,
                () => banTimestamps,
                () => banReasons,
                (steamId, playerName) => KickPlayer(steamId, playerName),
                (steamId, playerName) => ToggleBanPlayer(steamId, playerName),
                (steamId, playerName) => UnbanPlayer(steamId, playerName),
                (steamId, playerName, reason) => BanPlayer(steamId, playerName, reason)
            );
            StartCoroutine(banDataManager.LoadBansAsync());

            // Apply Harmony patches
            harmony.PatchAll();

            ModLogger.LogInfo("Player Ban Mod loaded!");
            
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

        private bool HasModdedRank(string steamId, string playerName) { return false; }
        private bool HasOffensiveName(string playerName) { return false; }

        private bool IsInLobbyScreen() { return lobbyMonitor != null && lobbyMonitor.IsInLobbyScreen(); }

        private bool IsGameActive() { return lobbyMonitor != null && lobbyMonitor.IsGameActive(); }

        private void BanPlayer(string steamId, string playerName, string reason) 
        { 
            banDataManager.BanPlayer(steamId, playerName, reason); 
            
            RecentPlayersManager.OnBanStatusChanged();
        }

        private void Update()
        {
            // Check for configured key press to open/close the UI
            if (Input.GetKeyDown(uiToggleKeyConfig.Value))
            {
                if (isHost && isInLobby)
                {
                    {
                        bool newState = !BanUIManager.IsActive;
                        BanUIManager.SetActive(newState);
                        RecentPlayersManager.SetButtonVisible(newState);
                        
                        // Handle cursor state
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
                    RecentPlayersManager.SetButtonVisible(false);
                    
                    // If we're closing during game, restore cursor lock
                    if (IsGameActive())
                    {
                        Cursor.lockState = CursorLockMode.Locked;
                        Cursor.visible = false;
                    }
                    
                    ModLogger.LogInfo("Player Management UI closed - no longer host or in lobby");
                }
            }
            
            // Batched saving - only save periodically
            if (needsSave && Time.time - lastSaveTime > SAVE_INTERVAL)
            {
                banDataManager.SaveBans();
                needsSave = false;
                lastSaveTime = Time.time;
            }

            // Capture recent players right after game starts
            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.GameHasStarted && isHost)
                {
                    if (mainMenuManager.kickplayershold != null)
                    {
                        var dict = new Dictionary<string, string>(mainMenuManager.kickplayershold.nametosteamid);
                        string GetTeamForSteam(string steamId)
                        {
                            var players = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
                            foreach (var pm in players)
                            {
                                if (!string.IsNullOrEmpty(pm.playername) && dict.TryGetValue(pm.playername, out var id) && id == steamId)
                                {
                                    try
                                    {
                                        var teamField = typeof(PlayerMovement).GetField("playerTeam", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                                        if (teamField != null)
                                        {
                                            int teamNum = (int)teamField.GetValue(pm);
                                            return teamNum == 0 ? "sorcerer" : teamNum == 2 ? "warlock" : "unknown";
                                        }
                                    }
                                    catch {}
                                    return "unknown";
                                }
                            }
                            return "unknown";
                        }

                        RecentPlayersManager.CaptureRecentPlayers(dict, GetTeamForSteam);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error capturing recent players: {ex.Message}");
            }
        }

        private bool CheckIfInLobby() { return lobbyMonitor != null && lobbyMonitor.IsInLobby; }
        private bool CheckIfHost() { return lobbyMonitor != null && lobbyMonitor.IsHost; }

        private void OnEnterLobby()
        {
            ModLogger.LogInfo("Entered lobby");
            
            // Recreate UI if it doesn't exist
            if (!BanUIManager.IsUICreated())
            {
                StartCoroutine(BanUIManager.CreateUI());
            }
            
            UpdateUIForHostStatus();
            playerManager.UpdatePlayerList();
            // Ensure Recent Players button exists (create if missing)
            try
            {
                var canvas = FindFirstObjectByType<Canvas>();
                if (canvas != null)
                {
                    RecentPlayersManager.EnsureRecentPlayersButton(canvas);
                    // Respect current UI visibility
                    RecentPlayersManager.SetButtonVisible(BanUIManager.IsActive);
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error ensuring Recent Players button: {ex.Message}");
            }
        }

        private void OnLeaveLobby()
        {
            ModLogger.LogInfo("Left lobby - clearing player data and UI");
            
            // Clear player-related data
            playerManager.ClearOnLeaveLobby();
            
            // Destroy UI completely so it can be recreated
            BanUIManager.DestroyUI();
            RecentPlayersManager.DestroyRecentPlayersUI();
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

        private void ToggleBanPlayer(string steamId, string playerName) 
        { 
            banDataManager.ToggleBanPlayer(steamId, playerName);
            
            RecentPlayersManager.OnBanStatusChanged();
        }

        private void UnbanPlayer(string steamId, string playerName) 
        { 
            banDataManager.UnbanPlayer(steamId); 
            
            RecentPlayersManager.OnBanStatusChanged();
        }

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

        

        private System.Collections.IEnumerator LoadBannedPlayersAsync() { return banDataManager.LoadBansAsync(); }

        private void ProcessBanEntry(string entry) { }

    }
}
