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

        // Configuration
        private ConfigEntry<string> bannedPlayersConfig;
        private ConfigEntry<bool> autobanModdedRanksConfig;
        private ConfigEntry<bool> autobanOffensiveNamesConfig;
        private ConfigEntry<string> offensiveNamesConfig;
        public Dictionary<string, string> bannedPlayers = new Dictionary<string, string>(); // steamId -> playerName
        private Dictionary<string, DateTime> banTimestamps = new Dictionary<string, DateTime>(); // steamId -> ban timestamp
        private Dictionary<string, string> banReasons = new Dictionary<string, string>(); // steamId -> ban reason

        // UI Components
        private GameObject banUI;
        private GameObject banUIPanel;
        private Transform playerListContent;
        private Transform bannedPlayersContent;
        private Button closeButton;
        private GameObject activePlayersTab;
        private GameObject bannedPlayersTab;
        private Toggle autobanModdedRanksToggle;
        private Toggle autobanOffensiveNamesToggle;
        private bool isFirstTimeOpeningBannedTab = true;
        private bool isHost = false;
        private bool isInLobby = false;

        // Player tracking
        private Dictionary<string, string> connectedPlayers = new Dictionary<string, string>(); // name -> steamId
        private Dictionary<string, string> fakePlayers = new Dictionary<string, string>(); // name -> steamId (for testing)
        private Dictionary<string, float> recentlyKickedPlayers = new Dictionary<string, float>(); // steamId -> kick time (for tracking recently kicked players)
        private string localPlayerName;
        private string localSteamId;
        
        // Debug/testing
        private bool debugMode = true; // Set to false for production

        // Ban kick tracking
        private Dictionary<string, int> kickAttempts = new Dictionary<string, int>(); // steamId -> attempt count
        private Dictionary<string, float> lastKickTime = new Dictionary<string, float>(); // steamId -> last kick time
        private const int MAX_KICK_ATTEMPTS = 3;
        private const float KICK_RETRY_DELAY = 3f; // 3 seconds between retries
        private const float RECENTLY_KICKED_DURATION = 10f; // 10 seconds to track recently kicked players

        // Batched saving
        private bool needsSave = false;
        private float lastSaveTime = 0f;
        private const float SAVE_INTERVAL = 5f; // Save every 5 seconds if needed

        // Virtualized UI for large ban lists
        private const int MAX_VISIBLE_BANNED_ITEMS = 50; // Only show 50 at a time
        private int bannedPlayersPageIndex = 0;
        private string bannedPlayersSearchQuery = "";

        // For parsing the config file
        private const string FIELD_DELIMITER = "◊"; // Diamond symbol - very unlikely in Steam names
        private const string ENTRY_DELIMITER = "※"; // Reference mark - very unlikely in Steam names

        // WizardRank enum for rank validation
        private enum WizardRank
        {
            Lackey,
            Sputterer,
            Novice,
            Apprentice,
            Savant,
            Master,
            Grand_Master,
            Archmagus,
            Magus_Prime,
            Supreme_Archmagus
        }

        // Helper method to check if a player is the host
        public bool IsPlayerHost(string steamId)
        {
            try
            {
                if (string.IsNullOrEmpty(steamId)) return false;
                
                // Check if this is the local player's Steam ID
                if (steamId == localSteamId) return true;
                
                // Check if we're the lobby owner
                if (isHost)
                {
                    CSteamID lobbyId = new CSteamID(BootstrapManager.CurrentLobbyID);
                    CSteamID ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId);
                    
                    // Try to parse the Steam ID safely
                    if (ulong.TryParse(steamId, out ulong playerSteamId))
                    {
                        CSteamID playerId = new CSteamID(playerSteamId);
                        return ownerId == playerId;
                    }
                    else
                    {
                        ModLogger.LogWarning($"Could not parse Steam ID: {steamId}");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error checking if player is host: {e.Message}");
            }
            return false;
        }

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
            
            // Async loading for very large lists
            StartCoroutine(LoadBannedPlayersAsync());

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
            CreateBanUI();

            // Start monitoring for lobby and host status
            StartCoroutine(MonitorLobbyStatus());
        }

        private void LoadBannedPlayers()
        {
            bannedPlayers.Clear();
            banTimestamps.Clear();
            banReasons.Clear();
            string bannedData = bannedPlayersConfig.Value;
            
            if (!string.IsNullOrEmpty(bannedData))
            {
                // Handle both old format (colon-separated) and new format (safe delimiters)
                bool isOldFormat = bannedData.Contains("|") && !bannedData.Contains(ENTRY_DELIMITER);
                
                if (isOldFormat)
                {
                    ModLogger.LogInfo("Loading banned players from old format and converting...");
                    LoadOldFormat(bannedData);
                    // Save in new format immediately
                    needsSave = true;
                }
                else
                {
                    LoadNewFormat(bannedData);
                }
            }
            
            ModLogger.LogInfo($"Loaded {bannedPlayers.Count} banned players");
            
            // Debug: Log the first few entries to check parsing
            int debugCount = 0;
            foreach (var kvp in bannedPlayers)
            {
                if (debugCount < 3) // Only log first 3 for debugging
                {
                    string steamId = kvp.Key;
                    string playerName = kvp.Value;
                    string timestamp = banTimestamps.ContainsKey(steamId) ? banTimestamps[steamId].ToString() : "None";
                    string reason = banReasons.ContainsKey(steamId) ? banReasons[steamId] : "None";
                    ModLogger.LogInfo($"Debug - Loaded ban: {playerName} ({steamId}) at {timestamp}, reason: {reason}");
                    debugCount++;
                }
            }
        }

        // method for loading old format (backwards compatibility)
        private void LoadOldFormat(string bannedData)
        {
            string[] entries = bannedData.Split('|');
            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    // Use the improved colon parsing logic for backwards compatibility
                    string[] parts = entry.Split(new char[] { ':' }, 4);
                    if (parts.Length >= 2)
                    {
                        string steamId = parts[0].Trim();
                        string playerName = parts[1].Trim();
                        bannedPlayers[steamId] = playerName;
                        
                        if (parts.Length >= 3)
                        {
                            string timestampPart = parts[2].Trim();
                            
                            if (parts.Length >= 4)
                            {
                                // 4 parts: steamId:playerName:timestamp:reason
                                if (DateTime.TryParse(timestampPart, out DateTime timestamp))
                                {
                                    banTimestamps[steamId] = timestamp;
                                }
                                else
                                {
                                    banTimestamps[steamId] = DateTime.Now;
                                }
                                banReasons[steamId] = parts[3].Trim();
                            }
                            else
                            {
                                // 3 parts: could be steamId:playerName:timestamp or steamId:playerName:reason
                                if (DateTime.TryParse(timestampPart, out DateTime timestamp))
                                {
                                    banTimestamps[steamId] = timestamp;
                                    banReasons[steamId] = "Manual";
                                }
                                else
                                {
                                    banTimestamps[steamId] = DateTime.Now;
                                    banReasons[steamId] = timestampPart;
                                }
                            }
                        }
                        else
                        {
                            // 2 parts: steamId:playerName
                            banTimestamps[steamId] = DateTime.Now;
                            banReasons[steamId] = "Manual";
                        }
                    }
                }
            }
        }

        // method for loading new format with safe delimiters
        private void LoadNewFormat(string bannedData)
        {
            string[] entries = bannedData.Split(new string[] { ENTRY_DELIMITER }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    string[] parts = entry.Split(new string[] { FIELD_DELIMITER }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        string steamId = parts[0].Trim();
                        string playerName = parts[1].Trim();
                        bannedPlayers[steamId] = playerName;
                        
                        // Load timestamp if available
                        if (parts.Length >= 3)
                        {
                            if (DateTime.TryParse(parts[2].Trim(), out DateTime timestamp))
                            {
                                banTimestamps[steamId] = timestamp;
                            }
                            else
                            {
                                banTimestamps[steamId] = DateTime.Now;
                            }
                        }
                        else
                        {
                            banTimestamps[steamId] = DateTime.Now;
                        }

                        // Load ban reason if available
                        if (parts.Length >= 4)
                        {
                            banReasons[steamId] = parts[3].Trim();
                        }
                        else
                        {
                            banReasons[steamId] = "Manual";
                        }
                    }
                }
            }
        }

        private void SaveBannedPlayersOptimized()
        {
            try
            {
                var sb = new StringBuilder(bannedPlayers.Count * 100);
                bool first = true;
                foreach (var kvp in bannedPlayers)
                {
                    if (!first) sb.Append(ENTRY_DELIMITER);
                    first = false;

                    string steamId = kvp.Key;
                    string playerName = kvp.Value;
                    string timestamp = banTimestamps.ContainsKey(steamId)
                        ? banTimestamps[steamId].ToString("yyyy-MM-dd HH:mm:ss")
                        : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string reason = banReasons.ContainsKey(steamId) ? banReasons[steamId] : "Manual";

                    // Use safe delimiters instead of colons
                    sb.Append(steamId).Append(FIELD_DELIMITER).Append(playerName).Append(FIELD_DELIMITER).Append(timestamp).Append(FIELD_DELIMITER).Append(reason);
                }

                bannedPlayersConfig.Value = sb.ToString();
                ModLogger.LogInfo($"Saved {bannedPlayers.Count} banned players with safe delimiters");
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error saving banned players: {e.Message}");
            }
        }

        private System.Collections.IEnumerator MonitorLobbyStatus()
        {
            while (true)
            {
                yield return new WaitForSeconds(1f);

                // Check if we're in a lobby
                bool wasInLobby = isInLobby;
                isInLobby = CheckIfInLobby();

                if (isInLobby != wasInLobby)
                {
                    if (isInLobby)
                    {
                        OnEnterLobby();
                    }
                    else
                    {
                        OnLeaveLobby();
                    }
                }

                // Check if we're host
                bool wasHost = isHost;
                isHost = CheckIfHost();

                if (isHost != wasHost)
                {
                    UpdateUIForHostStatus();
                }

                // Update player list if in lobby
                if (isInLobby)
                {
                    UpdatePlayerList();
                }

                // Check for banned players that need to be kicked
                CheckForBannedPlayersInLobby();
            }
        }

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
                        if (steamId == localSteamId) continue;

                        // Don't ban or kick the host
                        if (IsPlayerHost(steamId))
                        {
                            continue;
                        }

                        // Check if this player is banned
                        if (bannedPlayers.ContainsKey(steamId))
                        {
                            ModLogger.LogInfo($"Banned player {playerName} (Steam ID: {steamId}) detected - kicking");
                            KickPlayerWithRetry(steamId, playerName, true);
                        }
                        // Only check for modded ranks when in lobby screen (not during game)
                        else if (autobanModdedRanksConfig.Value && IsInLobbyScreen() && HasModdedRank(steamId, playerName))
                        {
                            ModLogger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has modded rank - auto-banning");
                            BanPlayer(steamId, playerName, "Automatic");
                        }
                        // Check for offensive names (this can be done anytime)
                        else if (autobanOffensiveNamesConfig.Value && HasOffensiveName(playerName))
                        {
                            ModLogger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has offensive name - auto-banning");
                            BanPlayer(steamId, playerName, "Automatic");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error checking for banned players: {e.Message}");
            }
        }

        private bool HasModdedRank(string steamId, string playerName)
        {
            try
            {
                // Only check ranks when in lobby screen, not during game
                if (!IsInLobbyScreen())
                {
                    return false;
                }

                // Double-check that this player is still actually connected
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null || mainMenuManager.kickplayershold == null)
                {
                    return false;
                }

                // Verify player is still in the actual lobby
                if (!mainMenuManager.kickplayershold.nametosteamid.ContainsKey(playerName) ||
                    mainMenuManager.kickplayershold.nametosteamid[playerName] != steamId)
                {
                    // Player is no longer in lobby, remove from our tracking
                    if (connectedPlayers.ContainsKey(playerName))
                    {
                        connectedPlayers.Remove(playerName);
                        ModLogger.LogInfo($"Removed {playerName} from tracking - no longer in lobby");
                    }
                    return false;
                }

                if (mainMenuManager.rankandleveltext == null)
                {
                    return false;
                }

                // Find the player's rank text in the lobby UI
                string playerRankText = "";
                bool foundPlayer = false;
                
                // Search through the rank texts to find this player
                for (int i = 0; i < mainMenuManager.rankandleveltext.Length && i < 8; i++)
                {
                    // Check if the corresponding name text matches our player
                    if (i * 2 < mainMenuManager.texts.Length && 
                        mainMenuManager.texts[i * 2].text == playerName)
                    {
                        playerRankText = mainMenuManager.rankandleveltext[i].text;
                        foundPlayer = true;
                        break;
                    }
                }

                if (!foundPlayer)
                {
                    // Don't spam logs - only log occasionally and only if player is supposed to be connected
                    if (connectedPlayers.ContainsKey(playerName) && Time.time % 10f < 1f)
                    {
                        ModLogger.LogInfo($"Could not find rank text for player {playerName} in lobby UI (player may have left)");
                    }
                    return false;
                }

                // Clean up rank text
                playerRankText = playerRankText.Replace("_", " ").Trim();

                // Check if the rank text contains any valid rank name
                bool hasValidRank = false;
                foreach (WizardRank rank in Enum.GetValues(typeof(WizardRank)))
                {
                    if (playerRankText.Contains(rank.ToString().Replace("_", " ")))
                    {
                        hasValidRank = true;
                        break;
                    }
                }

                if (!hasValidRank && !string.IsNullOrEmpty(playerRankText))
                {
                    ModLogger.LogInfo($"Player {playerName} has invalid rank text: {playerRankText}");
                    return true;
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error checking rank for player {playerName}: {e.Message}");
                return false; // Don't autoban on errors
            }
            
            return false;
        }

        private bool HasOffensiveName(string playerName)
        {
            try
            {
                if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(offensiveNamesConfig.Value))
                    return false;

                // Get the list of offensive names from config
                string[] offensiveNames = offensiveNamesConfig.Value.Split(',');
                
                // Convert player name to lowercase for case-insensitive comparison
                string playerNameLower = playerName.ToLower();
                
                foreach (string offensiveName in offensiveNames)
                {
                    string trimmedOffensiveName = offensiveName.Trim().ToLower();
                    if (string.IsNullOrEmpty(trimmedOffensiveName))
                        continue;

                    // Check if the offensive name is contained in the player name (without spaces)
                    // Remove spaces from player name for comparison
                    string playerNameNoSpaces = playerNameLower.Replace(" ", "");
                    string offensiveNameNoSpaces = trimmedOffensiveName.Replace(" ", "");
                    
                    if (playerNameNoSpaces.Contains(offensiveNameNoSpaces))
                    {
                        ModLogger.LogInfo($"Player {playerName} has offensive name containing: {trimmedOffensiveName}");
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error checking offensive name for player {playerName}: {e.Message}");
            }
            
            return false;
        }

        private bool IsInLobbyScreen()
        {
            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null) return false;
                
                // Check if we're in lobby and game hasn't started
                return isInLobby && !mainMenuManager.GameHasStarted;
            }
            catch
            {
                return false;
            }
        }

        private bool IsGameActive()
        {
            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                return mainMenuManager != null && mainMenuManager.GameHasStarted;
            }
            catch
            {
                return false;
            }
        }

        private void BanPlayer(string steamId, string playerName, string reason)
        {
            try
            {
                // Don't ban the host
                if (IsPlayerHost(steamId))
                {
                    ModLogger.LogWarning($"Cannot ban host player {playerName} (Steam ID: {steamId}) - host is immune to bans");
                    return;
                }

                if (!bannedPlayers.ContainsKey(steamId))
                {
                    bannedPlayers[steamId] = playerName;
                    banTimestamps[steamId] = DateTime.Now;
                    banReasons[steamId] = reason;
                    
                    ModLogger.LogInfo($"Banned player: {playerName} (Steam ID: {steamId}) at {DateTime.Now} - Reason: {reason}");
                    
                    // Immediately kick the player if they're currently in the lobby
                    if (isHost && isInLobby)
                    {
                        ModLogger.LogInfo($"Immediately kicking newly banned player: {playerName}");
                        KickPlayerWithRetry(steamId, playerName, true);
                    }
                    
                    // Mark for batched saving instead of immediate save
                    needsSave = true;

                    // Only refresh UI if it's currently visible
                    if (banUI != null && banUI.activeSelf)
                    {
                        RefreshPlayerListUI();
                        RefreshBannedPlayersUI();
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error banning player: {e.Message}");
            }
        }

        private void Update()
        {
            // Check for F2 key press to open/close the UI
            if (Input.GetKeyDown(KeyCode.F2))
            {
                if (isHost && isInLobby)
                {
                    if (banUI != null)
                    {
                        bool newState = !banUI.activeSelf;
                        banUI.SetActive(newState);
                        
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
                            // Reset the flag when opening UI so banned tab scrolls to top when first viewed
                            isFirstTimeOpeningBannedTab = true;
                            
                            // Refresh the UI when opening
                            RefreshPlayerListUI();
                            RefreshBannedPlayersUI();
                        }
                        
                        string gameState = IsGameActive() ? "in-game" : "in-lobby";
                        ModLogger.LogInfo($"Player Management UI {(newState ? "opened" : "closed")} via F2 key [{gameState}]");
                    }
                }
                else if (banUI != null && banUI.activeSelf)
                {
                    // Close UI if it's open but conditions no longer met
                    banUI.SetActive(false);
                    
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
                    AddFakePlayersForTesting();
                }
                
                // F4 to clear fake players
                if (Input.GetKeyDown(KeyCode.F4))
                {
                    ClearFakePlayersForTesting();
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
                SaveBannedPlayersOptimized();
                needsSave = false;
                lastSaveTime = Time.time;
            }
        }

        private bool CheckIfInLobby()
        {
            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                return mainMenuManager != null && BootstrapManager.CurrentLobbyID != 0;
            }
            catch
            {
                return false;
            }
        }

        private bool CheckIfHost()
        {
            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null)
                {
                    // Check if we're the lobby owner
                    CSteamID lobbyId = new CSteamID(BootstrapManager.CurrentLobbyID);
                    CSteamID ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId);
                    CSteamID localId = SteamUser.GetSteamID();
                    return ownerId == localId;
                }
            }
            catch
            {
                // Ignore errors
            }
            return false;
        }

        private void OnEnterLobby()
        {
            ModLogger.LogInfo("Entered lobby");
            UpdateUIForHostStatus();
            UpdatePlayerList();
        }

        private void OnLeaveLobby()
        {
            ModLogger.LogInfo("Left lobby - clearing player data and UI");
            
            // Clear player data
            connectedPlayers.Clear();
            fakePlayers.Clear(); // Clear fake players when leaving lobby
            recentlyKickedPlayers.Clear(); // Clear recently kicked players when leaving lobby
            kickAttempts.Clear();
            lastKickTime.Clear();
            
            // Destroy and recreate UI
            if (banUI != null)
            {
                ModLogger.LogInfo("Destroying old UI and recreating...");
                
                // Close UI if it's open and restore cursor if needed
                if (banUI.activeSelf && IsGameActive())
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                
                // Destroy the entire UI hierarchy (this will destroy the canvas and all children)
                var canvas = banUI.transform.root.gameObject;
                if (canvas != null && canvas.name == "BanModCanvas")
                {
                    Destroy(canvas);
                }
                else
                {
                    // Fallback - destroy just the UI if we can't find the canvas
                    Destroy(banUI);
                }
                
                // Reset UI references
                banUI = null;
                banUIPanel = null;
                playerListContent = null;
                bannedPlayersContent = null;
                closeButton = null;
                activePlayersTab = null;
                bannedPlayersTab = null;
                autobanModdedRanksToggle = null;
                autobanOffensiveNamesToggle = null;
                
                ModLogger.LogInfo("Old UI destroyed, recreating new UI...");
                
                // Recreate the UI after a short delay to ensure cleanup is complete
                StartCoroutine(RecreateUIAfterDelay());
            }
        }

private System.Collections.IEnumerator RecreateUIAfterDelay()
{
    // Wait a frame to ensure destruction is complete
    yield return new WaitForEndOfFrame();
    
    // Recreate the UI
    CreateBanUI();
    
    ModLogger.LogInfo("UI recreated successfully");
}

        private void UpdateUIForHostStatus()
        {
            // UI is now controlled only by F2 key, no button needed
        }

        private void UpdatePlayerList()
        {
            try
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    // Store current fake players
                    var currentFakePlayers = new Dictionary<string, string>(fakePlayers);
                    
                    // Clear and repopulate with real players
                    connectedPlayers.Clear();
                    
                    // Get local player info
                    localSteamId = SteamUser.GetSteamID().ToString();
                    localPlayerName = SteamFriends.GetPersonaName();

                    // Clean up old entries from recentlyKickedPlayers
                    var currentTime = Time.time;
                    var playersToRemove = new List<string>();
                    foreach (var kvp in recentlyKickedPlayers)
                    {
                        if (currentTime - kvp.Value > RECENTLY_KICKED_DURATION)
                        {
                            playersToRemove.Add(kvp.Key);
                        }
                    }
                    foreach (var steamId in playersToRemove)
                    {
                        recentlyKickedPlayers.Remove(steamId);
                    }

                    // Get all connected players
                    foreach (var kvp in mainMenuManager.kickplayershold.nametosteamid)
                    {
                        string playerName = kvp.Key;
                        string steamId = kvp.Value;

                        // Don't include self in the list
                        if (steamId != localSteamId)
                        {
                            // Don't add recently kicked players back to the list
                            if (!recentlyKickedPlayers.ContainsKey(steamId))
                            {
                                connectedPlayers[playerName] = steamId;
                            }
                        }
                    }
                    
                    // Restore fake players (but not if they were recently kicked)
                    foreach (var kvp in currentFakePlayers)
                    {
                        string playerName = kvp.Key;
                        string steamId = kvp.Value;
                        
                        // Don't restore fake players that were recently kicked
                        if (!recentlyKickedPlayers.ContainsKey(steamId))
                        {
                            connectedPlayers[playerName] = steamId;
                        }
                    }

                    // Update UI if it exists
                    if (banUI != null && banUI.activeSelf)
                    {
                        RefreshPlayerListUI();
                        RefreshBannedPlayersUI();
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error updating player list: {e.Message}");
            }
        }

        private void AddFakePlayersForTesting()
        {
            try
            {
                // Generate some fake players with realistic names and fake Steam IDs
                string[] fakeNames = { 
                    "TestPlayer1", "DebugUser", "FakePlayer123", "MockGamer", 
                    "TestDummy", "BotPlayer", "SampleUser", "DemoPlayer",
                    "TestUser42", "DebugBot", "FakeGamer", "MockUser"
                };
                
                int playersToAdd = UnityEngine.Random.Range(3, 8); // Add 3-7 fake players
                
                for (int i = 0; i < playersToAdd; i++)
                {
                    string playerName = fakeNames[UnityEngine.Random.Range(0, fakeNames.Length)] + "_" + UnityEngine.Random.Range(100, 999);
                    string fakeSteamId = "7656119" + UnityEngine.Random.Range(10000000, 99999999).ToString(); // Fake Steam ID format
                    
                    // Make sure we don't duplicate names or IDs (check both real and fake players)
                    while (connectedPlayers.ContainsKey(playerName) || connectedPlayers.ContainsValue(fakeSteamId) || 
                           fakePlayers.ContainsKey(playerName) || fakePlayers.ContainsValue(fakeSteamId))
                    {
                        playerName = fakeNames[UnityEngine.Random.Range(0, fakeNames.Length)] + "_" + UnityEngine.Random.Range(100, 999);
                        fakeSteamId = "7656119" + UnityEngine.Random.Range(10000000, 99999999).ToString();
                    }
                    
                    fakePlayers[playerName] = fakeSteamId;
                    connectedPlayers[playerName] = fakeSteamId; // Also add to main list for UI
                }
                
                ModLogger.LogInfo($"Added {playersToAdd} fake players for testing. Total players: {connectedPlayers.Count} (including {fakePlayers.Count} fake)");
                
                // Refresh UI if it's open
                if (banUI != null && banUI.activeSelf)
                {
                    RefreshPlayerListUI();
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error adding fake players: {e.Message}");
            }
        }

        private void ClearFakePlayersForTesting()
        {
            try
            {
                int fakePlayerCount = fakePlayers.Count;
                
                // Remove fake players from both dictionaries and recently kicked players
                foreach (var kvp in fakePlayers)
                {
                    connectedPlayers.Remove(kvp.Key);
                    // Also remove from recently kicked players if they were fake players
                    recentlyKickedPlayers.Remove(kvp.Value);
                }
                fakePlayers.Clear();
                
                ModLogger.LogInfo($"Cleared {fakePlayerCount} fake players for testing. Remaining players: {connectedPlayers.Count}");
                
                // Refresh UI if it's open
                if (banUI != null && banUI.activeSelf)
                {
                    RefreshPlayerListUI();
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error clearing fake players: {e.Message}");
            }
        }

        private void CreateBanUI()
        {
            try
            {
                ModLogger.LogInfo("Starting UI creation...");
                
                // Delay UI creation slightly to ensure game is fully loaded
                StartCoroutine(CreateBanUIDelayed());
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error in CreateBanUI: {e.Message}\nStackTrace: {e.StackTrace}");
            }
        }

        private System.Collections.IEnumerator CreateBanUIDelayed()
        {
            // Wait a bit more for the game to fully initialize
            yield return new WaitForSeconds(1f);
            
            ModLogger.LogInfo("Starting delayed UI creation...");
            
            // Try a much simpler approach first
            yield return StartCoroutine(CreateSimpleUI());
        }

        private System.Collections.IEnumerator CreateSimpleUI()
        {
            try
            {
                ModLogger.LogInfo("Creating our own Canvas like ModSyncUI reference...");
                
                // Create our own Canvas like the reference code
                GameObject canvasObj = new GameObject("BanModCanvas");
                Canvas canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 1000; // High priority to be on top
                
                // Add CanvasScaler for proper scaling
                CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                
                // Add GraphicRaycaster for UI interactions
                canvasObj.AddComponent<GraphicRaycaster>();

                ModLogger.LogInfo("Canvas created successfully");

                // Create main UI container
                banUI = new GameObject("BanModUI");
                banUI.transform.SetParent(canvasObj.transform, false);
                banUI.SetActive(false);

                // Add RectTransform to the main container
                var mainRect = banUI.AddComponent<RectTransform>();
                mainRect.anchorMin = Vector2.zero;
                mainRect.anchorMax = Vector2.one;
                mainRect.offsetMin = Vector2.zero;
                mainRect.offsetMax = Vector2.zero;

                ModLogger.LogInfo("Main UI container created successfully");

                // Create panel background
                banUIPanel = new GameObject("BanUIPanel");
                banUIPanel.transform.SetParent(banUI.transform, false);
                
                var panelImage = banUIPanel.AddComponent<Image>();
                panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);
                
                var panelRect = banUIPanel.GetComponent<RectTransform>();
                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = Vector2.zero;
                panelRect.offsetMax = Vector2.zero;

                ModLogger.LogInfo("Panel background created successfully");

                // Create title using regular Text component like reference
                var titleObj = new GameObject("Title");
                var titleRect = titleObj.AddComponent<RectTransform>();
                titleObj.transform.SetParent(banUIPanel.transform, false);
                
                Text titleText = titleObj.AddComponent<Text>();
                titleText.text = "Player Management";
                titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                titleText.fontSize = 24;
                titleText.color = Color.white;
                titleText.alignment = TextAnchor.MiddleCenter;
                titleRect.anchorMin = new Vector2(0.1f, 0.85f);
                titleRect.anchorMax = new Vector2(0.9f, 0.95f);
                titleRect.offsetMin = Vector2.zero;
                titleRect.offsetMax = Vector2.zero;

                ModLogger.LogInfo("Title created successfully");

                // Create close button with text
                var closeButtonObj = new GameObject("CloseButton");
                closeButtonObj.transform.SetParent(banUIPanel.transform, false);
                closeButton = closeButtonObj.AddComponent<Button>();
                var closeButtonImage = closeButtonObj.AddComponent<Image>();
                closeButtonImage.color = new Color(0.8f, 0.2f, 0.2f);
                
                var closeButtonRect = closeButtonObj.GetComponent<RectTransform>();
                // Make the close button smaller and raise it up to avoid touching the banned players tab
                closeButtonRect.anchorMin = new Vector2(0.9f, 0.9f);
                closeButtonRect.anchorMax = new Vector2(0.97f, 0.97f);
                closeButtonRect.offsetMin = Vector2.zero;
                closeButtonRect.offsetMax = Vector2.zero;

                // Add text to close button
                var closeTextObj = new GameObject("CloseText");
                var closeTextRect = closeTextObj.AddComponent<RectTransform>();
                closeTextObj.transform.SetParent(closeButtonObj.transform, false);
                Text closeText = closeTextObj.AddComponent<Text>();
                closeText.text = "X";
                closeText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                closeText.fontSize = 14; // Smaller font size to match the smaller button
                closeText.color = Color.white;
                closeText.alignment = TextAnchor.MiddleCenter;
                closeTextRect.anchorMin = Vector2.zero;
                closeTextRect.anchorMax = Vector2.one;
                closeTextRect.offsetMin = Vector2.zero;
                closeTextRect.offsetMax = Vector2.zero;
                
                closeButton.onClick.AddListener(() => banUI.SetActive(false));

                ModLogger.LogInfo("Close button created successfully");

                // Create tab buttons
                ModLogger.LogInfo("Creating tab buttons...");
                
                // Active Players Tab Button
                var activeTabButtonObj = new GameObject("ActiveTabButton");
                activeTabButtonObj.transform.SetParent(banUIPanel.transform, false);
                var activeTabButton = activeTabButtonObj.AddComponent<Button>();
                var activeTabButtonImage = activeTabButtonObj.AddComponent<Image>();
                activeTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);
                
                var activeTabButtonRect = activeTabButtonObj.GetComponent<RectTransform>();
                activeTabButtonRect.anchorMin = new Vector2(0.05f, 0.75f);
                activeTabButtonRect.anchorMax = new Vector2(0.45f, 0.85f);
                activeTabButtonRect.offsetMin = Vector2.zero;
                activeTabButtonRect.offsetMax = Vector2.zero;

                // Active tab button text
                var activeTabTextObj = new GameObject("ActiveTabText");
                activeTabTextObj.transform.SetParent(activeTabButtonObj.transform, false);
                var activeTabText = activeTabTextObj.AddComponent<Text>();
                activeTabText.text = "Active Players";
                activeTabText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                activeTabText.fontSize = 14;
                activeTabText.color = Color.white;
                activeTabText.alignment = TextAnchor.MiddleCenter;
                
                var activeTabTextRect = activeTabTextObj.GetComponent<RectTransform>();
                activeTabTextRect.anchorMin = Vector2.zero;
                activeTabTextRect.anchorMax = Vector2.one;
                activeTabTextRect.offsetMin = Vector2.zero;
                activeTabTextRect.offsetMax = Vector2.zero;

                // Banned Players Tab Button
                var bannedTabButtonObj = new GameObject("BannedTabButton");
                bannedTabButtonObj.transform.SetParent(banUIPanel.transform, false);
                var bannedTabButton = bannedTabButtonObj.AddComponent<Button>();
                var bannedTabButtonImage = bannedTabButtonObj.AddComponent<Image>();
                bannedTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);
                
                var bannedTabButtonRect = bannedTabButtonObj.GetComponent<RectTransform>();
                bannedTabButtonRect.anchorMin = new Vector2(0.55f, 0.75f);
                bannedTabButtonRect.anchorMax = new Vector2(0.95f, 0.85f);
                bannedTabButtonRect.offsetMin = Vector2.zero;
                bannedTabButtonRect.offsetMax = Vector2.zero;

                // Banned tab button text
                var bannedTabTextObj = new GameObject("BannedTabText");
                bannedTabTextObj.transform.SetParent(bannedTabButtonObj.transform, false);
                var bannedTabText = bannedTabTextObj.AddComponent<Text>();
                bannedTabText.text = "Banned Players";
                bannedTabText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                bannedTabText.fontSize = 14;
                bannedTabText.color = Color.white;
                bannedTabText.alignment = TextAnchor.MiddleCenter;
                
                var bannedTabTextRect = bannedTabTextObj.GetComponent<RectTransform>();
                bannedTabTextRect.anchorMin = Vector2.zero;
                bannedTabTextRect.anchorMax = Vector2.one;
                bannedTabTextRect.offsetMin = Vector2.zero;
                bannedTabTextRect.offsetMax = Vector2.zero;

                ModLogger.LogInfo("Tab buttons created successfully");

                // Create checkboxes above the active players tab
                ModLogger.LogInfo("Creating checkboxes...");
                
                // Colors for toggle states
                Color toggleOffColor = new Color(0.2f, 0.2f, 0.2f, 0.8f);
                Color toggleOnColor = new Color(0.2f, 0.6f, 0.2f, 0.9f);
                
                // Autoban Modded Ranks Checkbox (square toggle)
                var autobanModdedRanksToggleObj = new GameObject("AutobanModdedRanksToggle");
                autobanModdedRanksToggleObj.transform.SetParent(banUIPanel.transform, false);
                autobanModdedRanksToggle = autobanModdedRanksToggleObj.AddComponent<Toggle>();
                var autobanModdedRanksToggleImage = autobanModdedRanksToggleObj.AddComponent<Image>();
                autobanModdedRanksToggleImage.color = toggleOffColor;
                
                var autobanModdedRanksToggleRect = autobanModdedRanksToggleObj.GetComponent<RectTransform>();
                // Make the toggle less wide (square-like)
                autobanModdedRanksToggleRect.anchorMin = new Vector2(0.05f, 0.92f);
                autobanModdedRanksToggleRect.anchorMax = new Vector2(0.08f, 0.97f);
                autobanModdedRanksToggleRect.offsetMin = Vector2.zero;
                autobanModdedRanksToggleRect.offsetMax = Vector2.zero;

                // Label to the right of the toggle
                var autobanModdedRanksLabelObj = new GameObject("AutobanModdedRanksLabel");
                autobanModdedRanksLabelObj.transform.SetParent(banUIPanel.transform, false);
                var autobanModdedRanksLabel = autobanModdedRanksLabelObj.AddComponent<Text>();
                autobanModdedRanksLabel.text = "Autoban modded ranks";
                autobanModdedRanksLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                autobanModdedRanksLabel.fontSize = 24;
                autobanModdedRanksLabel.color = Color.white;
                autobanModdedRanksLabel.alignment = TextAnchor.MiddleLeft;
                
                var autobanModdedRanksLabelRect = autobanModdedRanksLabelObj.GetComponent<RectTransform>();
                autobanModdedRanksLabelRect.anchorMin = new Vector2(0.085f, 0.92f);
                autobanModdedRanksLabelRect.anchorMax = new Vector2(0.30f, 0.97f);
                autobanModdedRanksLabelRect.offsetMin = new Vector2(5, 0);
                autobanModdedRanksLabelRect.offsetMax = new Vector2(-5, 0);

                // Autoban Offensive Names Checkbox (square toggle)
                var autobanOffensiveNamesToggleObj = new GameObject("AutobanOffensiveNamesToggle");
                autobanOffensiveNamesToggleObj.transform.SetParent(banUIPanel.transform, false);
                autobanOffensiveNamesToggle = autobanOffensiveNamesToggleObj.AddComponent<Toggle>();
                var autobanOffensiveNamesToggleImage = autobanOffensiveNamesToggleObj.AddComponent<Image>();
                autobanOffensiveNamesToggleImage.color = toggleOffColor;
                
                var autobanOffensiveNamesToggleRect = autobanOffensiveNamesToggleObj.GetComponent<RectTransform>();
                // Make the toggle less wide (square-like)
                autobanOffensiveNamesToggleRect.anchorMin = new Vector2(0.05f, 0.86f);
                autobanOffensiveNamesToggleRect.anchorMax = new Vector2(0.08f, 0.91f);
                autobanOffensiveNamesToggleRect.offsetMin = Vector2.zero;
                autobanOffensiveNamesToggleRect.offsetMax = Vector2.zero;

                // Label to the right of the toggle
                var autobanOffensiveNamesLabelObj = new GameObject("AutobanOffensiveNamesLabel");
                autobanOffensiveNamesLabelObj.transform.SetParent(banUIPanel.transform, false);
                var autobanOffensiveNamesLabel = autobanOffensiveNamesLabelObj.AddComponent<Text>();
                autobanOffensiveNamesLabel.text = "Autoban offensive names";
                autobanOffensiveNamesLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                autobanOffensiveNamesLabel.fontSize = 24;
                autobanOffensiveNamesLabel.color = Color.white;
                autobanOffensiveNamesLabel.alignment = TextAnchor.MiddleLeft;
                
                var autobanOffensiveNamesLabelRect = autobanOffensiveNamesLabelObj.GetComponent<RectTransform>();
                autobanOffensiveNamesLabelRect.anchorMin = new Vector2(0.085f, 0.86f);
                autobanOffensiveNamesLabelRect.anchorMax = new Vector2(0.30f, 0.91f);
                autobanOffensiveNamesLabelRect.offsetMin = new Vector2(5, 0);
                autobanOffensiveNamesLabelRect.offsetMax = new Vector2(-5, 0);

                // Set initial toggle states from config
                autobanModdedRanksToggle.isOn = autobanModdedRanksConfig.Value;
                autobanOffensiveNamesToggle.isOn = autobanOffensiveNamesConfig.Value;
                // Apply initial colors
                autobanModdedRanksToggleImage.color = autobanModdedRanksToggle.isOn ? toggleOnColor : toggleOffColor;
                autobanOffensiveNamesToggleImage.color = autobanOffensiveNamesToggle.isOn ? toggleOnColor : toggleOffColor;

                // Add toggle listeners
                autobanModdedRanksToggle.onValueChanged.AddListener((bool value) => {
                    autobanModdedRanksConfig.Value = value;
                    autobanModdedRanksToggleImage.color = value ? toggleOnColor : toggleOffColor;
                    ModLogger.LogInfo($"Autoban modded ranks: {value}");
                });

                autobanOffensiveNamesToggle.onValueChanged.AddListener((bool value) => {
                    autobanOffensiveNamesConfig.Value = value;
                    autobanOffensiveNamesToggleImage.color = value ? toggleOnColor : toggleOffColor;
                    ModLogger.LogInfo($"Autoban offensive names: {value}");
                });

                ModLogger.LogInfo("Checkboxes created successfully");

                // Create content areas
                ModLogger.LogInfo("Creating active players tab...");
                activePlayersTab = new GameObject("ActivePlayersTab");
                if (activePlayersTab == null)
                {
                    ModLogger.LogError("Failed to create activePlayersTab GameObject");
                    yield break;
                }
                
                ModLogger.LogInfo("Adding RectTransform to activePlayersTab...");
                var activeTabRect = activePlayersTab.AddComponent<RectTransform>();
                if (activeTabRect == null)
                {
                    ModLogger.LogError("Failed to add RectTransform to activePlayersTab");
                    yield break;
                }
                
                ModLogger.LogInfo("Setting activePlayersTab parent...");
                activePlayersTab.transform.SetParent(banUIPanel.transform, false);
                activeTabRect.anchorMin = new Vector2(0.05f, 0.1f);
                activeTabRect.anchorMax = new Vector2(0.95f, 0.7f);
                activeTabRect.offsetMin = Vector2.zero;
                activeTabRect.offsetMax = Vector2.zero;
                activePlayersTab.SetActive(true); // Start with active players tab visible

                ModLogger.LogInfo("Creating active content object...");
                var activeContentObj = new GameObject("ActiveContent");
                if (activeContentObj == null)
                {
                    ModLogger.LogError("Failed to create activeContentObj GameObject");
                    yield break;
                }
                
                ModLogger.LogInfo("Adding RectTransform to activeContentObj...");
                var activeContentRect = activeContentObj.AddComponent<RectTransform>();
                if (activeContentRect == null)
                {
                    ModLogger.LogError("Failed to add RectTransform to activeContentObj");
                    yield break;
                }
                
                ModLogger.LogInfo("Setting active content parent...");
                activeContentObj.transform.SetParent(activePlayersTab.transform, false);
                playerListContent = activeContentObj.transform;
                activeContentRect.anchorMin = Vector2.zero;
                activeContentRect.anchorMax = Vector2.one;
                activeContentRect.offsetMin = Vector2.zero;
                activeContentRect.offsetMax = Vector2.zero;

                // Add ScrollRect to active content
                var activeScrollRect = activePlayersTab.AddComponent<ScrollRect>();
                activeScrollRect.content = activeContentRect;
                activeScrollRect.horizontal = false;
                activeScrollRect.vertical = true;
                activeScrollRect.scrollSensitivity = 10f;
                
                // Add mask to active players tab
                var activeMask = activePlayersTab.AddComponent<Mask>();
                var activeMaskImage = activePlayersTab.AddComponent<Image>();
                activeMaskImage.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);

                ModLogger.LogInfo("Active players tab created successfully");

                // Create banned players tab
                ModLogger.LogInfo("Creating banned players tab...");
                bannedPlayersTab = new GameObject("BannedPlayersTab");
                if (bannedPlayersTab == null)
                {
                    ModLogger.LogError("Failed to create bannedPlayersTab GameObject");
                    yield break;
                }
                
                ModLogger.LogInfo("Adding RectTransform to bannedPlayersTab...");
                var bannedTabRect = bannedPlayersTab.AddComponent<RectTransform>();
                if (bannedTabRect == null)
                {
                    ModLogger.LogError("Failed to add RectTransform to bannedPlayersTab");
                    yield break;
                }
                
                ModLogger.LogInfo("Setting banned players tab parent...");
                bannedPlayersTab.transform.SetParent(banUIPanel.transform, false);
                bannedPlayersTab.SetActive(false);
                bannedTabRect.anchorMin = new Vector2(0.05f, 0.1f);
                bannedTabRect.anchorMax = new Vector2(0.95f, 0.7f);
                bannedTabRect.offsetMin = Vector2.zero;
                bannedTabRect.offsetMax = Vector2.zero;

                ModLogger.LogInfo("Creating banned content object...");
                var bannedContentObj = new GameObject("BannedContent");
                if (bannedContentObj == null)
                {
                    ModLogger.LogError("Failed to create bannedContentObj GameObject");
                    yield break;
                }
                
                ModLogger.LogInfo("Adding RectTransform to bannedContentObj...");
                var bannedContentRect = bannedContentObj.AddComponent<RectTransform>();
                if (bannedContentRect == null)
                {
                    ModLogger.LogError("Failed to add RectTransform to bannedContentObj");
                    yield break;
                }
                
                ModLogger.LogInfo("Setting banned content parent...");
                bannedContentObj.transform.SetParent(bannedPlayersTab.transform, false);
                bannedPlayersContent = bannedContentObj.transform;
                bannedContentRect.anchorMin = Vector2.zero;
                bannedContentRect.anchorMax = Vector2.one;
                bannedContentRect.offsetMin = Vector2.zero;
                bannedContentRect.offsetMax = Vector2.zero;

                // Add ScrollRect to banned content
                var bannedScrollRect = bannedPlayersTab.AddComponent<ScrollRect>();
                bannedScrollRect.content = bannedContentRect;
                bannedScrollRect.horizontal = false;
                bannedScrollRect.vertical = true;
                bannedScrollRect.scrollSensitivity = 10f;
                
                // Add mask to banned players tab
                var bannedMask = bannedPlayersTab.AddComponent<Mask>();
                var bannedMaskImage = bannedPlayersTab.AddComponent<Image>();
                bannedMaskImage.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);

                ModLogger.LogInfo("Banned players tab created successfully");

                ModLogger.LogInfo("Content areas created successfully");

                // Add tab button click handlers
                activeTabButton.onClick.AddListener(() => {
                    activePlayersTab.SetActive(true);
                    bannedPlayersTab.SetActive(false);
                    activeTabButtonImage.color = new Color(0.4f, 0.4f, 0.4f, 0.9f); // Highlight active tab
                    bannedTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f); // Dim inactive tab
                    RefreshPlayerListUI();
                });
                
                bannedTabButton.onClick.AddListener(() => {
                activePlayersTab.SetActive(false);
                bannedPlayersTab.SetActive(true);
                activeTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f); // Dim inactive tab
                bannedTabButtonImage.color = new Color(0.4f, 0.4f, 0.4f, 0.9f); // Highlight active tab
                
                // Reset scroll flag when switching to banned tab
                isFirstTimeOpeningBannedTab = true;
                RefreshBannedPlayersUI();
            });

                // Set initial tab state
                activeTabButtonImage.color = new Color(0.4f, 0.4f, 0.4f, 0.9f); // Highlight active tab
                bannedTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f); // Dim inactive tab

                ModLogger.LogInfo("UI created successfully using ModSyncUI pattern");
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error in CreateSimpleUI: {e.Message}\nStackTrace: {e.StackTrace}");
            }
            
            yield return null; // Coroutine must yield
        }




        private void RefreshPlayerListUI()
        {
            try
            {
                // Clear existing player entries
                foreach (Transform child in playerListContent)
                {
                    Destroy(child.gameObject);
                }

                if (connectedPlayers.Count == 0)
                {
                    // Create "no players" message
                    var noPlayersObj = new GameObject("NoPlayers");
                    noPlayersObj.transform.SetParent(playerListContent, false);
                    var noPlayersText = noPlayersObj.AddComponent<Text>();
                    noPlayersText.text = "No other players in lobby";
                    noPlayersText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    noPlayersText.fontSize = 16;
                    noPlayersText.color = Color.gray;
                    noPlayersText.alignment = TextAnchor.MiddleCenter;
                    
                    var noPlayersRect = noPlayersObj.GetComponent<RectTransform>();
                    noPlayersRect.anchorMin = new Vector2(0, 1);
                    noPlayersRect.anchorMax = new Vector2(1, 1);
                    noPlayersRect.pivot = new Vector2(0.5f, 1);
                    noPlayersRect.offsetMin = new Vector2(10, -40);
                    noPlayersRect.offsetMax = new Vector2(-10, -10);
                    
                    // Update content height
                    var playerListContentRect1 = playerListContent.GetComponent<RectTransform>();
                    playerListContentRect1.sizeDelta = new Vector2(0, 50);
                    return;
                }

                int index = 0;
                foreach (var kvp in connectedPlayers)
                {
                    string playerName = kvp.Key;
                    string steamId = kvp.Value;
                    bool isBanned = bannedPlayers.ContainsKey(steamId);

                    // Skip banned players - they should only appear in the banned players tab
                    if (isBanned) continue;

                    // Create player entry container
                    var playerEntryObj = new GameObject($"PlayerEntry_{index}");
                    playerEntryObj.transform.SetParent(playerListContent, false);
                    var playerEntryImage = playerEntryObj.AddComponent<Image>();
                    playerEntryImage.color = new Color(0.3f, 0.3f, 0.3f, 0.8f);
                    
                    var playerEntryRect = playerEntryObj.GetComponent<RectTransform>();
                    playerEntryRect.anchorMin = new Vector2(0, 1);
                    playerEntryRect.anchorMax = new Vector2(1, 1);
                    playerEntryRect.pivot = new Vector2(0.5f, 1);
                    playerEntryRect.offsetMin = new Vector2(5, -50 - (index * 60));
                    playerEntryRect.offsetMax = new Vector2(-5, -5 - (index * 60));

                    // Create player name text
                    var nameObj = new GameObject("PlayerName");
                    nameObj.transform.SetParent(playerEntryObj.transform, false);
                    var nameText = nameObj.AddComponent<Text>();
                    nameText.text = playerName;
                    nameText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    nameText.fontSize = 14;
                    nameText.color = Color.white;
                    nameText.alignment = TextAnchor.MiddleLeft;
                    
                    var nameRect = nameObj.GetComponent<RectTransform>();
                    nameRect.anchorMin = new Vector2(0, 0);
                    nameRect.anchorMax = new Vector2(0.4f, 1);
                    nameRect.offsetMin = new Vector2(10, 5);
                    nameRect.offsetMax = new Vector2(-5, -5);

                    // Create Steam ID text
                    var steamIdObj = new GameObject("SteamID");
                    steamIdObj.transform.SetParent(playerEntryObj.transform, false);
                    var steamIdText = steamIdObj.AddComponent<Text>();
                    steamIdText.text = steamId;
                    steamIdText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    steamIdText.fontSize = 14;
                    steamIdText.color = Color.gray;
                    steamIdText.alignment = TextAnchor.MiddleLeft;
                    
                    var steamIdRect = steamIdObj.GetComponent<RectTransform>();
                    steamIdRect.anchorMin = new Vector2(0.4f, 0);
                    steamIdRect.anchorMax = new Vector2(0.7f, 1);
                    steamIdRect.offsetMin = new Vector2(5, 5);
                    steamIdRect.offsetMax = new Vector2(-5, -5);

                    // Create kick button
                    var kickButtonObj = new GameObject("KickButton");
                    kickButtonObj.transform.SetParent(playerEntryObj.transform, false);
                    var kickButton = kickButtonObj.AddComponent<Button>();
                    var kickButtonImage = kickButtonObj.AddComponent<Image>();
                    kickButtonImage.color = new Color(0.8f, 0.6f, 0.2f);
                    
                    var kickButtonTextObj = new GameObject("KickButtonText");
                    kickButtonTextObj.transform.SetParent(kickButtonObj.transform, false);
                    var kickButtonText = kickButtonTextObj.AddComponent<Text>();
                    kickButtonText.text = "Kick";
                    kickButtonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    kickButtonText.fontSize = 12;
                    kickButtonText.color = Color.white;
                    kickButtonText.alignment = TextAnchor.MiddleCenter;
                    
                    var kickButtonRect = kickButtonObj.GetComponent<RectTransform>();
                    kickButtonRect.anchorMin = new Vector2(0.75f, 0.2f);
                    kickButtonRect.anchorMax = new Vector2(0.85f, 0.8f);
                    kickButtonRect.offsetMin = Vector2.zero;
                    kickButtonRect.offsetMax = Vector2.zero;
                    
                    kickButton.onClick.AddListener(() => KickPlayer(steamId, playerName));

                    // Create ban button
                    var banButtonObj = new GameObject("BanButton");
                    banButtonObj.transform.SetParent(playerEntryObj.transform, false);
                    var banButton = banButtonObj.AddComponent<Button>();
                    var banButtonImage = banButtonObj.AddComponent<Image>();
                    banButtonImage.color = new Color(0.8f, 0.2f, 0.2f);
                    
                    var banButtonTextObj = new GameObject("BanButtonText");
                    banButtonTextObj.transform.SetParent(banButtonObj.transform, false);
                    var banButtonText = banButtonTextObj.AddComponent<Text>();
                    banButtonText.text = "Ban";
                    banButtonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    banButtonText.fontSize = 12;
                    banButtonText.color = Color.white;
                    banButtonText.alignment = TextAnchor.MiddleCenter;
                    
                    var banButtonRect = banButtonObj.GetComponent<RectTransform>();
                    banButtonRect.anchorMin = new Vector2(0.9f, 0.2f);
                    banButtonRect.anchorMax = new Vector2(0.98f, 0.8f);
                    banButtonRect.offsetMin = Vector2.zero;
                    banButtonRect.offsetMax = Vector2.zero;
                    
                    banButton.onClick.AddListener(() => ToggleBanPlayer(steamId, playerName));

                    index++;
                }

                // Update content height and scroll behavior
                var playerListContentRect2 = playerListContent.GetComponent<RectTransform>();
                var activeScrollRect = activePlayersTab.GetComponent<ScrollRect>();
                
                // Calculate if we need scrolling (more than 11 players)
                bool needsScrolling = index > 11;
                
                if (needsScrolling)
                {
                    // Normal scrolling behavior - set content height to accommodate all players
                    playerListContentRect2.sizeDelta = new Vector2(0, index * 60 + 10);
                    
                    // Don't change scroll position when scrolling is needed
                }
                else
                {
                    // Fit all players on screen - calculate exact height needed
                    float totalHeight = index * 60 + 10;
                    playerListContentRect2.sizeDelta = new Vector2(0, totalHeight);
                    
                    // Reset scroll to top when all players fit on screen
                    if (activeScrollRect != null)
                    {
                        activeScrollRect.verticalNormalizedPosition = 1f;
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error refreshing player list UI: {e.Message}");
            }
        }

        private void RefreshBannedPlayersUI()
        {
            RefreshBannedPlayersUIOptimized();
            
            // Only auto-scroll to top when UI is first opened, not on every refresh
            if (isFirstTimeOpeningBannedTab && bannedPlayers.Count > 11)
            {
                var bannedScrollRect = bannedPlayersTab.GetComponent<ScrollRect>();
                if (bannedScrollRect != null)
                {
                    // Set scroll to top
                    bannedScrollRect.verticalNormalizedPosition = 1f;
                }
                isFirstTimeOpeningBannedTab = false; // Don't auto-scroll again
            }
        }

        private void RefreshBannedPlayersUIOptimized()
        {
            try
            {
                // Clear existing entries
                foreach (Transform child in bannedPlayersContent)
                {
                    Destroy(child.gameObject);
                }

                var bannedList = bannedPlayers.ToList();
                if (bannedList.Count == 0)
                {
                    CreateNoBannedPlayersMessage();
                    return;
                }

                // Sort by ban timestamp - most recent first (FIXED: Use OrderBy instead of OrderByDescending)
                bannedList = bannedList.OrderBy(kvp => 
                {
                    string steamId = kvp.Key;
                    if (banTimestamps.ContainsKey(steamId))
                    {
                        return banTimestamps[steamId];
                    }
                    return DateTime.MaxValue; // Put entries without timestamps at the end
                }).Reverse().ToList(); // Reverse to get newest first

                // Calculate pagination
                int totalPages = Mathf.CeilToInt((float)bannedList.Count / MAX_VISIBLE_BANNED_ITEMS);
                bannedPlayersPageIndex = Mathf.Clamp(bannedPlayersPageIndex, 0, Math.Max(0, totalPages - 1));
                int startIndex = bannedPlayersPageIndex * MAX_VISIBLE_BANNED_ITEMS;
                int endIndex = Mathf.Min(startIndex + MAX_VISIBLE_BANNED_ITEMS, bannedList.Count);

                // Create pagination header with larger text
                CreatePaginationHeaderImproved(bannedPlayersPageIndex + 1, totalPages, bannedList.Count);

                // Create navigation buttons
                CreateNavigationButtons(bannedPlayersPageIndex, totalPages);

                // Create only visible entries
                for (int i = startIndex; i < endIndex; i++)
                {
                    var kvp = bannedList[i];
                    string steamId = kvp.Key;
                    string playerName = kvp.Value;
                    CreateBannedPlayerEntry(steamId, playerName, i - startIndex);
                }

                // Update content height for visible items (+3 for header, navigation, and spacing)
                UpdateBannedContentHeight((endIndex - startIndex) + 3);
                
                // Only auto-scroll to top when UI is first opened (not on every refresh)
                // We'll handle this in the main UI refresh method instead
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error refreshing banned players UI: {e.Message}");
            }
        }

        private void CreateNoBannedPlayersMessage()
        {
            var noBannedObj = new GameObject("NoBannedPlayers");
            noBannedObj.transform.SetParent(bannedPlayersContent, false);
            var noBannedText = noBannedObj.AddComponent<Text>();
            noBannedText.text = "No banned players";
            noBannedText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            noBannedText.fontSize = 16;
            noBannedText.color = Color.gray;
            noBannedText.alignment = TextAnchor.MiddleCenter;

            var noBannedRect = noBannedObj.GetComponent<RectTransform>();
            noBannedRect.anchorMin = new Vector2(0, 1);
            noBannedRect.anchorMax = new Vector2(1, 1);
            noBannedRect.pivot = new Vector2(0.5f, 1);
            noBannedRect.offsetMin = new Vector2(10, -40);
            noBannedRect.offsetMax = new Vector2(-10, -10);

            var bannedPlayersContentRect = bannedPlayersContent.GetComponent<RectTransform>();
            bannedPlayersContentRect.sizeDelta = new Vector2(0, 50);
        }

        private void CreatePaginationHeaderImproved(int currentPage, int totalPages, int totalCount)
        {
            var headerObj = new GameObject("PaginationHeader");
            headerObj.transform.SetParent(bannedPlayersContent, false);
            var headerText = headerObj.AddComponent<Text>();
            headerText.text = $"Banned Players: {totalCount} total (Page {currentPage} of {totalPages})";
            headerText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            headerText.fontSize = 18; // Increased from 14 to 18
            headerText.color = Color.yellow;
            headerText.alignment = TextAnchor.MiddleCenter;
            headerText.fontStyle = FontStyle.Bold; // Make it bold too

            var headerRect = headerObj.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.offsetMin = new Vector2(5, -35); // Made slightly taller
            headerRect.offsetMax = new Vector2(-5, -5);
        }

        private void CreateNavigationButtons(int pageIndex, int totalPages)
        {
            var navigationObj = new GameObject("NavigationButtons");
            navigationObj.transform.SetParent(bannedPlayersContent, false);

            var navigationRect = navigationObj.GetComponent<RectTransform>();
            if (navigationRect == null) navigationRect = navigationObj.AddComponent<RectTransform>();
            navigationRect.anchorMin = new Vector2(0, 1);
            navigationRect.anchorMax = new Vector2(1, 1);
            navigationRect.pivot = new Vector2(0.5f, 1);
            navigationRect.offsetMin = new Vector2(5, -80); // Positioned below header
            navigationRect.offsetMax = new Vector2(-5, -45);

            // Show Previous button if we're not on the first page (pageIndex > 0)
            if (pageIndex > 0)
            {
                CreateNavigationButton(navigationObj, "< Previous", new Vector2(0, 0), new Vector2(0.35f, 1), () => {
                    bannedPlayersPageIndex--;
                    RefreshBannedPlayersUIOptimized();
                });
            }

            // Show Next button if we're not on the last page (pageIndex < totalPages - 1)
            if (pageIndex < totalPages - 1)
            {
                CreateNavigationButton(navigationObj, "Next >", new Vector2(0.65f, 0), new Vector2(1, 1), () => {
                    bannedPlayersPageIndex++;
                    RefreshBannedPlayersUIOptimized();
                });
            }
        }

        private void CreatePageIndicator(GameObject parent, string text)
        {
            var indicatorObj = new GameObject("PageIndicator");
            indicatorObj.transform.SetParent(parent.transform, false);
            var indicatorText = indicatorObj.AddComponent<Text>();
            indicatorText.text = text;
            indicatorText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            indicatorText.fontSize = 14;
            indicatorText.color = Color.white;
            indicatorText.alignment = TextAnchor.MiddleCenter;
            indicatorText.fontStyle = FontStyle.Bold;

            var indicatorRect = indicatorObj.GetComponent<RectTransform>();
            indicatorRect.anchorMin = new Vector2(0.35f, 0);
            indicatorRect.anchorMax = new Vector2(0.65f, 1);
            indicatorRect.offsetMin = Vector2.zero;
            indicatorRect.offsetMax = Vector2.zero;
        }

        private void CreateNavigationButton(GameObject parent, string text, Vector2 anchorMin, Vector2 anchorMax, System.Action onClick)
        {
            var buttonObj = new GameObject("NavigationButton");
            buttonObj.transform.SetParent(parent.transform, false);
            var button = buttonObj.AddComponent<Button>();
            var buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.2f, 0.4f, 0.8f, 0.9f); // Blue color for navigation

            var buttonRect = buttonObj.GetComponent<RectTransform>();
            buttonRect.anchorMin = anchorMin;
            buttonRect.anchorMax = anchorMax;
            buttonRect.offsetMin = new Vector2(5, 0);
            buttonRect.offsetMax = new Vector2(-5, 0);

            var textObj = new GameObject("ButtonText");
            textObj.transform.SetParent(buttonObj.transform, false);
            var buttonText = textObj.AddComponent<Text>();
            buttonText.text = text;
            buttonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            buttonText.fontSize = 14;
            buttonText.color = Color.white;
            buttonText.alignment = TextAnchor.MiddleCenter;
            buttonText.fontStyle = FontStyle.Bold;

            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            button.onClick.AddListener(() => onClick());
        }


        private void CreatePageButton(GameObject parent, string text, Vector2 anchorMin, Vector2 anchorMax, System.Action onClick)
        {
            var buttonObj = new GameObject("PageButton");
            buttonObj.transform.SetParent(parent.transform, false);
            var button = buttonObj.AddComponent<Button>();
            var buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);

            var buttonRect = buttonObj.GetComponent<RectTransform>();
            buttonRect.anchorMin = anchorMin;
            buttonRect.anchorMax = anchorMax;
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            var textObj = new GameObject("ButtonText");
            textObj.transform.SetParent(buttonObj.transform, false);
            var buttonText = textObj.AddComponent<Text>();
            buttonText.text = text;
            buttonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            buttonText.fontSize = 12;
            buttonText.color = Color.white;
            buttonText.alignment = TextAnchor.MiddleCenter;

            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            button.onClick.AddListener(() => onClick());
        }

        private void CreateBannedPlayerEntry(string steamId, string playerName, int visibleIndex)
        {
            DateTime banTime = banTimestamps.ContainsKey(steamId) ? banTimestamps[steamId] : DateTime.Now;
            string banTimeString = banTime.ToString("MM/dd/yyyy HH:mm");

            var bannedEntryObj = new GameObject($"BannedEntry_{visibleIndex}");
            bannedEntryObj.transform.SetParent(bannedPlayersContent, false);
            var bannedEntryImage = bannedEntryObj.AddComponent<Image>();
            bannedEntryImage.color = new Color(0.8f, 0.3f, 0.3f, 0.8f);

            var bannedEntryRect = bannedEntryObj.GetComponent<RectTransform>();
            bannedEntryRect.anchorMin = new Vector2(0, 1);
            bannedEntryRect.anchorMax = new Vector2(1, 1);
            bannedEntryRect.pivot = new Vector2(0.5f, 1);
            // Adjusted spacing to account for larger header and navigation buttons
            bannedEntryRect.offsetMin = new Vector2(5, -135 - (visibleIndex * 60)); // Start further down
            bannedEntryRect.offsetMax = new Vector2(-5, -90 - (visibleIndex * 60));

            // ... rest of the CreateBannedPlayerEntry method stays the same ...
            var nameObj = new GameObject("PlayerName");
            nameObj.transform.SetParent(bannedEntryObj.transform, false);
            var nameText = nameObj.AddComponent<Text>();
            nameText.text = playerName;
            nameText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameText.fontSize = 14;
            nameText.color = Color.white;
            nameText.alignment = TextAnchor.MiddleLeft;
            var nameRect = nameObj.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0);
            nameRect.anchorMax = new Vector2(0.25f, 1);
            nameRect.offsetMin = new Vector2(10, 5);
            nameRect.offsetMax = new Vector2(-5, -5);

            var steamIdObj = new GameObject("SteamID");
            steamIdObj.transform.SetParent(bannedEntryObj.transform, false);
            var steamIdText = steamIdObj.AddComponent<Text>();
            steamIdText.text = steamId;
            steamIdText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            steamIdText.fontSize = 14;
            steamIdText.color = Color.gray;
            steamIdText.alignment = TextAnchor.MiddleLeft;
            var steamIdRect = steamIdObj.GetComponent<RectTransform>();
            steamIdRect.anchorMin = new Vector2(0.25f, 0);
            steamIdRect.anchorMax = new Vector2(0.45f, 1);
            steamIdRect.offsetMin = new Vector2(5, 5);
            steamIdRect.offsetMax = new Vector2(-5, -5);

            var timestampObj = new GameObject("Timestamp");
            timestampObj.transform.SetParent(bannedEntryObj.transform, false);
            var timestampText = timestampObj.AddComponent<Text>();
            timestampText.text = banTimeString;
            timestampText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            timestampText.fontSize = 14;
            timestampText.color = Color.yellow;
            timestampText.alignment = TextAnchor.MiddleLeft;
            var timestampRect = timestampObj.GetComponent<RectTransform>();
            timestampRect.anchorMin = new Vector2(0.45f, 0);
            timestampRect.anchorMax = new Vector2(0.65f, 1);
            timestampRect.offsetMin = new Vector2(5, 5);
            timestampRect.offsetMax = new Vector2(-5, -5);

            string banReason = banReasons.ContainsKey(steamId) ? banReasons[steamId] : "Manual";
            var banReasonObj = new GameObject("BanReason");
            banReasonObj.transform.SetParent(bannedEntryObj.transform, false);
            var banReasonText = banReasonObj.AddComponent<Text>();
            banReasonText.text = banReason;
            banReasonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            banReasonText.fontSize = 14;
            banReasonText.color = Color.cyan;
            banReasonText.alignment = TextAnchor.MiddleLeft;
            var banReasonRect = banReasonObj.GetComponent<RectTransform>();
            banReasonRect.anchorMin = new Vector2(0.65f, 0);
            banReasonRect.anchorMax = new Vector2(0.75f, 1);
            banReasonRect.offsetMin = new Vector2(5, 5);
            banReasonRect.offsetMax = new Vector2(-5, -5);

            var unbanButtonObj = new GameObject("UnbanButton");
            unbanButtonObj.transform.SetParent(bannedEntryObj.transform, false);
            var unbanButton = unbanButtonObj.AddComponent<Button>();
            var unbanButtonImage = unbanButtonObj.AddComponent<Image>();
            unbanButtonImage.color = new Color(0.2f, 0.6f, 0.2f);
            var unbanButtonTextObj = new GameObject("UnbanButtonText");
            unbanButtonTextObj.transform.SetParent(unbanButtonObj.transform, false);
            var unbanButtonText = unbanButtonTextObj.AddComponent<Text>();
            unbanButtonText.text = "Unban";
            unbanButtonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            unbanButtonText.fontSize = 12;
            unbanButtonText.color = Color.white;
            unbanButtonText.alignment = TextAnchor.MiddleCenter;
            var unbanButtonRect = unbanButtonObj.GetComponent<RectTransform>();
            unbanButtonRect.anchorMin = new Vector2(0.75f, 0.2f);
            unbanButtonRect.anchorMax = new Vector2(0.98f, 0.8f);
            unbanButtonRect.offsetMin = Vector2.zero;
            unbanButtonRect.offsetMax = Vector2.zero;
            var unbanButtonTextRect = unbanButtonTextObj.GetComponent<RectTransform>();
            unbanButtonTextRect.anchorMin = Vector2.zero;
            unbanButtonTextRect.anchorMax = Vector2.one;
            unbanButtonTextRect.offsetMin = Vector2.zero;
            unbanButtonTextRect.offsetMax = Vector2.zero;
            unbanButton.onClick.AddListener(() => UnbanPlayer(steamId, playerName));
        }

        private void UpdateBannedContentHeight(int items)
        {
            var bannedPlayersContentRect2 = bannedPlayersContent.GetComponent<RectTransform>();
            var bannedScrollRect = bannedPlayersTab.GetComponent<ScrollRect>();

            int index = items;
            bool needsScrolling = index > 11;
            if (needsScrolling)
            {
                bannedPlayersContentRect2.sizeDelta = new Vector2(0, index * 60 + 10);
            }
            else
            {
                float totalHeight = index * 60 + 10;
                bannedPlayersContentRect2.sizeDelta = new Vector2(0, totalHeight);
                if (bannedScrollRect != null)
                {
                    bannedScrollRect.verticalNormalizedPosition = 1f;
                }
            }
        }

        private void KickPlayerWithRetry(string steamId, string playerName, bool isBannedPlayer = false)
        {
            try
            {
                // Don't kick the host
                if (IsPlayerHost(steamId))
                {
                    ModLogger.LogWarning($"Cannot kick host player {playerName} (Steam ID: {steamId}) - host is immune to kicks");
                    return;
                }

                // Check if we've exceeded max attempts
                if (kickAttempts.ContainsKey(steamId) && kickAttempts[steamId] >= MAX_KICK_ATTEMPTS)
                {
                    ModLogger.LogWarning($"Max kick attempts ({MAX_KICK_ATTEMPTS}) reached for {playerName} (Steam ID: {steamId}) - stopping retry");
                    return;
                }

                // Check if enough time has passed since last attempt
                if (lastKickTime.ContainsKey(steamId))
                {
                    float timeSinceLastKick = Time.time - lastKickTime[steamId];
                    if (timeSinceLastKick < KICK_RETRY_DELAY)
                    {
                        return; // Not enough time has passed
                    }
                }

                // Increment attempt counter
                if (!kickAttempts.ContainsKey(steamId))
                {
                    kickAttempts[steamId] = 0;
                }
                kickAttempts[steamId]++;

                // Update last kick time
                lastKickTime[steamId] = Time.time;

                string playerType = isBannedPlayer ? "banned player" : "player";
                string gameState = IsGameActive() ? "in-game" : "in-lobby";
                ModLogger.LogInfo($"Kicking {playerType}: {playerName} (Steam ID: {steamId}) - Attempt {kickAttempts[steamId]}/{MAX_KICK_ATTEMPTS} [{gameState}]");
                
                // Use different kick methods based on game state
                if (IsGameActive())
                {
                    // For in-game kicks, use BootstrapNetworkManager directly
                    BootstrapNetworkManager.KickPlayer(steamId);
                }
                else
                {
                    // For lobby kicks, use MainMenuManager
                    var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                    if (mainMenuManager != null)
                    {
                        mainMenuManager.KickPlayer(steamId);
                    }
                    else
                    {
                        ModLogger.LogError($"MainMenuManager not found - cannot kick {playerName} in lobby");
                        return;
                    }
                }
                
                // For non-banned players, immediately remove from connectedPlayers and refresh UI
                if (!isBannedPlayer)
                {
                    // Find and remove the player from connectedPlayers by Steam ID
                    var playerToRemove = connectedPlayers.FirstOrDefault(kvp => kvp.Value == steamId);
                    if (!string.IsNullOrEmpty(playerToRemove.Key))
                    {
                        connectedPlayers.Remove(playerToRemove.Key);
                        ModLogger.LogInfo($"Immediately removed {playerName} from active players list after kick");
                        
                        // Add to recently kicked players to prevent re-adding
                        recentlyKickedPlayers[steamId] = Time.time;
                        
                        // Refresh the UI to reflect the change
                        if (banUI != null && banUI.activeSelf)
                        {
                            RefreshPlayerListUI();
                        }
                    }
                }
                
                // If this is a retry attempt, schedule another check
                if (kickAttempts[steamId] < MAX_KICK_ATTEMPTS)
                {
                    StartCoroutine(ScheduleKickRetry(steamId, playerName, isBannedPlayer));
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error kicking player {playerName}: {e.Message}");
                
                // Schedule retry even on error
                if (!kickAttempts.ContainsKey(steamId) || kickAttempts[steamId] < MAX_KICK_ATTEMPTS)
                {
                    StartCoroutine(ScheduleKickRetry(steamId, playerName, isBannedPlayer));
                }
            }
        }

        private System.Collections.IEnumerator ScheduleKickRetry(string steamId, string playerName, bool isBannedPlayer = false)
        {
            yield return new WaitForSeconds(KICK_RETRY_DELAY);
            
            // Check if player is still in lobby
            if (isHost && isInLobby)
            {
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    // Check if player is still connected
                    bool playerStillConnected = mainMenuManager.kickplayershold.nametosteamid.ContainsValue(steamId);
                    if (playerStillConnected)
                    {
                        string playerType = isBannedPlayer ? "banned player" : "player";
                        ModLogger.LogInfo($"{playerType} {playerName} still connected after kick attempt - retrying...");
                        KickPlayerWithRetry(steamId, playerName, isBannedPlayer);
                    }
                    else
                    {
                        string playerType = isBannedPlayer ? "banned player" : "player";
                        ModLogger.LogInfo($"{playerType} {playerName} successfully kicked");
                        
                        // Remove player from connectedPlayers dictionary if they were successfully kicked and not already removed
                        if (!isBannedPlayer)
                        {
                            // Check if player is still in connectedPlayers (might have been removed already)
                            var playerToRemove = connectedPlayers.FirstOrDefault(kvp => kvp.Value == steamId);
                            if (!string.IsNullOrEmpty(playerToRemove.Key))
                            {
                                connectedPlayers.Remove(playerToRemove.Key);
                                ModLogger.LogInfo($"Removed {playerName} from active players list after successful kick (retry)");
                                
                                // Add to recently kicked players to prevent re-adding
                                recentlyKickedPlayers[steamId] = Time.time;
                                ModLogger.LogInfo($"Added {playerName} to recently kicked players list (retry)");
                                
                                // Refresh the UI to reflect the change
                                if (banUI != null && banUI.activeSelf)
                                {
                                    RefreshPlayerListUI();
                                }
                            }
                            else
                            {
                                ModLogger.LogInfo($"{playerName} was already removed from active players list");
                                
                                // Still add to recently kicked players to prevent re-adding
                                if (!recentlyKickedPlayers.ContainsKey(steamId))
                                {
                                    recentlyKickedPlayers[steamId] = Time.time;
                                    ModLogger.LogInfo($"Added {playerName} to recently kicked players list (already removed)");
                                }
                            }
                        }
                        
                        // Clear tracking for this player
                        kickAttempts.Remove(steamId);
                        lastKickTime.Remove(steamId);
                    }
                }
            }
        }

        private void KickPlayer(string steamId, string playerName)
        {
            // Use the enhanced kick method with retry logic (not a banned player)
            KickPlayerWithRetry(steamId, playerName, false);
        }

        private void ToggleBanPlayer(string steamId, string playerName)
        {
            try
            {
                // Don't ban the host
                if (IsPlayerHost(steamId))
                {
                    ModLogger.LogWarning($"Cannot ban host player {playerName} (Steam ID: {steamId}) - host is immune to bans");
                    return;
                }

                if (bannedPlayers.ContainsKey(steamId))
                {
                    // Unban player
                    bannedPlayers.Remove(steamId);
                    banTimestamps.Remove(steamId); // Remove timestamp on unban
                    banReasons.Remove(steamId); // Remove ban reason on unban
                    ModLogger.LogInfo($"Unbanned player: {playerName} (Steam ID: {steamId})");
                    
                    // Clear kick tracking for this player
                    kickAttempts.Remove(steamId);
                    lastKickTime.Remove(steamId);
                    recentlyKickedPlayers.Remove(steamId); // Allow unbanned players to rejoin
                }
                else
                {
                    // Ban player with manual reason
                    BanPlayer(steamId, playerName, "Manual");
                }
                
                // Mark for batched saving instead of immediate save
                needsSave = true;

                // Only refresh UI if it's currently visible
                if (banUI != null && banUI.activeSelf)
                {
                    RefreshPlayerListUI();
                    RefreshBannedPlayersUI();
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error toggling ban for player: {e.Message}");
            }
        }

        private void UnbanPlayer(string steamId, string playerName)
        {
            try
            {
                if (bannedPlayers.ContainsKey(steamId))
                {
                    bannedPlayers.Remove(steamId);
                    banTimestamps.Remove(steamId); // Remove timestamp on unban
                    banReasons.Remove(steamId); // Remove ban reason on unban
                    ModLogger.LogInfo($"Unbanned player: {playerName} (Steam ID: {steamId})");
                    
                    // Clear kick tracking for this player
                    kickAttempts.Remove(steamId);
                    lastKickTime.Remove(steamId);
                    recentlyKickedPlayers.Remove(steamId); // Allow unbanned players to rejoin
                    
                    // Mark for batched saving instead of immediate save
                    needsSave = true;

                    // Only refresh UI if it's currently visible
                    if (banUI != null && banUI.activeSelf)
                    {
                        RefreshPlayerListUI();
                        RefreshBannedPlayersUI();
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error unbanning player: {e.Message}");
            }
        }

                 // Check if a joining player is banned
         private void CheckBannedPlayer(string steamId, string playerName)
         {
             // Don't kick the host
             if (IsPlayerHost(steamId))
             {
                 ModLogger.LogInfo($"Host player {playerName} (Steam ID: {steamId}) attempted to join - host is immune to bans and kicks");
                 return;
             }

             if (bannedPlayers.ContainsKey(steamId))
             {
                 ModLogger.LogInfo($"Banned player {playerName} (Steam ID: {steamId}) attempted to join - kicking them");
                 KickPlayerWithRetry(steamId, playerName, true);
             }
         }

        private void OnDestroy()
        {
            // Final save to ensure data isn't lost
            try
            {
                if (needsSave)
                {
                    SaveBannedPlayersOptimized();
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
        private System.Collections.IEnumerator LoadBannedPlayersAsync()
        {
            bannedPlayers.Clear();
            banTimestamps.Clear();
            banReasons.Clear();

            string bannedData = bannedPlayersConfig.Value;
            if (string.IsNullOrEmpty(bannedData))
            {
                ModLogger.LogInfo("Loaded 0 banned players asynchronously");
                yield break;
            }

            string[] entries = bannedData.Split('|');
            int processedCount = 0;

            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    ProcessBanEntry(entry);
                    processedCount++;

                    // Yield every 100 entries to prevent frame drops
                    if (processedCount % 100 == 0)
                    {
                        yield return null;
                    }
                }
            }

            ModLogger.LogInfo($"Loaded {bannedPlayers.Count} banned players asynchronously");
        }

        private void ProcessBanEntry(string entry)
        {
            // Check if this is new format (contains safe delimiters) or old format (contains colons)
            if (entry.Contains(FIELD_DELIMITER))
            {
                // New format with safe delimiters
                string[] parts = entry.Split(new string[] { FIELD_DELIMITER }, StringSplitOptions.None);
                if (parts.Length >= 2)
                {
                    string steamId = parts[0].Trim();
                    string playerName = parts[1].Trim();
                    bannedPlayers[steamId] = playerName;

                    if (parts.Length >= 3)
                    {
                        if (DateTime.TryParse(parts[2].Trim(), out DateTime timestamp))
                        {
                            banTimestamps[steamId] = timestamp;
                        }
                        else
                        {
                            banTimestamps[steamId] = DateTime.Now;
                        }
                    }
                    else
                    {
                        banTimestamps[steamId] = DateTime.Now;
                    }

                    if (parts.Length >= 4)
                    {
                        banReasons[steamId] = parts[3].Trim();
                    }
                    else
                    {
                        banReasons[steamId] = "Manual";
                    }
                }
            }
            else
            {
                // Old format with colons - use backwards compatible parsing
                string[] parts = entry.Split(new char[] { ':' }, 4);
                if (parts.Length >= 2)
                {
                    string steamId = parts[0].Trim();
                    string playerName = parts[1].Trim();
                    bannedPlayers[steamId] = playerName;

                    if (parts.Length >= 3)
                    {
                        string timestampPart = parts[2].Trim();
                        
                        if (parts.Length >= 4)
                        {
                            if (DateTime.TryParse(timestampPart, out DateTime timestamp))
                            {
                                banTimestamps[steamId] = timestamp;
                            }
                            else
                            {
                                banTimestamps[steamId] = DateTime.Now;
                            }
                            banReasons[steamId] = parts[3].Trim();
                        }
                        else
                        {
                            if (DateTime.TryParse(timestampPart, out DateTime timestamp))
                            {
                                banTimestamps[steamId] = timestamp;
                                banReasons[steamId] = "Manual";
                            }
                            else
                            {
                                banTimestamps[steamId] = DateTime.Now;
                                banReasons[steamId] = timestampPart;
                            }
                        }
                    }
                    else
                    {
                        banTimestamps[steamId] = DateTime.Now;
                        banReasons[steamId] = "Manual";
                    }
                }
            }
        }

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
                if (banUI != null && banUI.activeSelf)
                {
                    RefreshBannedPlayersUI();
                }
            }
            catch (Exception e)
            {
                ModLogger.LogError($"Error generating test bans: {e.Message}");
            }
        }

        // monitor player joins through the kickplayershold system
        [HarmonyPatch(typeof(KickPlayersHolder), "AddToDict")]
        private static class PlayerJoinPatch
        {
            private static void Postfix(KickPlayersHolder __instance, string name, string steamid)
            {
                try
                {
                    if (instance != null && instance.isHost)
                    {
                        // Don't kick the host
                        if (instance.IsPlayerHost(steamid))
                        {
                            PlayerBanMod.ModLogger.LogInfo($"Host player {name} (Steam ID: {steamid}) joined - host is immune to bans and kicks");
                            return;
                        }
                        
                        // Check if this player is banned
                        if (instance.bannedPlayers.ContainsKey(steamid))
                        {
                            PlayerBanMod.ModLogger.LogInfo($"Banned player {name} (Steam ID: {steamid}) joined - kicking them immediately");
                            instance.KickPlayerWithRetry(steamid, name, true);
                        }
                    }
                }
                catch (Exception e)
                {
                    if (instance != null)
                    {
                        PlayerBanMod.ModLogger.LogError($"Error in player join patch: {e.Message}");
                    }
                }
            }
        }
    }
}
