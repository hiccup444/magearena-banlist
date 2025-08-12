using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace PlayerBanMod
{
    public class RecentPlayersManager
    {
        private static ManualLogSource logger;
        private static List<RecentPlayerData> currentGamePlayers = new List<RecentPlayerData>();
        private static GameObject recentPlayersButton;
        private static GameObject recentPlayersPanel;
        private static bool isRecentPlayersVisible = false;
        private static Canvas mainCanvas;
        private static DateTime lastGameStartTime = DateTime.MinValue;
        private static bool isWaitingForBanModUI = false; // Flag to prevent multiple coroutines
        private static bool hasCapturedCurrentGame = false; // Flag to prevent duplicate captures in same game session
        
        // References to ban/unban functionality
        private static Func<Dictionary<string, string>> getBannedPlayers;
        private static Action<string, string> toggleBanPlayer;
        
        // File storage for recent players
        private static string recentPlayersFilePath;
        private const int MAX_STORED_PLAYERS = 100;
        
        [System.Serializable]
        private class RecentPlayerRecord
        {
            public string LobbyDateTime;
            public string PlayerName;
            public string SteamID;
            public string Team;
            public string BanStatus;
        }
        
        public struct RecentPlayerData
        {
            public string playerName;
            public string steamId;
            public string team; // "sorcerer" or "warlock"
            public DateTime gameStartTime;
        }

        public static void Initialize(ManualLogSource log)
        {
            logger = log;
            
            // Initialize file path for recent players storage
            try
            {
                string baseDir = Path.Combine(BepInEx.Paths.PluginPath, "BanList", "RecentPlayers");
                if (!Directory.Exists(baseDir)) 
                {
                    Directory.CreateDirectory(baseDir);
                    logger?.LogInfo($"Created RecentPlayers directory: {baseDir}");
                }
                recentPlayersFilePath = Path.Combine(baseDir, "recent_players.jsonl");
                
                // Create empty file if it doesn't exist
                if (!File.Exists(recentPlayersFilePath))
                {
                    using (File.Create(recentPlayersFilePath)) { }
                    logger?.LogInfo($"Created recent players file: {recentPlayersFilePath}");
                }
                
                // Load the most recent lobby from file
                LoadMostRecentLobbyFromFile();
            }
            catch (Exception e)
            {
                logger?.LogError($"Error initializing recent players storage: {e.Message}");
                recentPlayersFilePath = "recent_players.jsonl"; // Fallback
            }
        }
        
        public static void SetBanFunctions(Func<Dictionary<string, string>> getBanned, Action<string, string> toggleBan)
        {
            getBannedPlayers = getBanned;
            toggleBanPlayer = toggleBan;
        }

        private static GameObject ResolveBanModUI()
        {
            // Look for BanModUI which is the main UI container
            var banModCanvas = GameObject.Find("BanModCanvas");
            if (banModCanvas != null)
            {
                // BanModUI should be a child of BanModCanvas
                Transform banModUITransform = banModCanvas.transform.Find("BanModUI");
                if (banModUITransform != null)
                {
                    return banModUITransform.gameObject;
                }
            }
            return null;
        }

        private static Canvas ResolveBanModCanvas()
        {
            var banModCanvas = GameObject.Find("BanModCanvas");
            if (banModCanvas != null)
            {
                return banModCanvas.GetComponent<Canvas>();
            }
            return null;
        }

        private static System.Collections.IEnumerator WaitForBanModUI(System.Action<GameObject> onUIFound)
        {
            GameObject banModUI = null;
            int attempts = 0;
            const int maxAttempts = 20; // Try for up to 10 seconds (20 * 0.5s)
            
            while (banModUI == null && attempts < maxAttempts)
            {
                banModUI = ResolveBanModUI();
                if (banModUI != null)
                {
                    logger?.LogInfo($"Found BanModUI after {attempts * 0.5f} seconds");
                    isWaitingForBanModUI = false; // Reset flag
                    onUIFound?.Invoke(banModUI);
                    yield break;
                }
                
                attempts++;
                yield return new WaitForSeconds(0.5f);
            }
            
            if (banModUI == null)
            {
                logger?.LogError("Could not find BanModUI after 10 seconds - Recent Players button will not be created");
            }
            
            isWaitingForBanModUI = false; // Reset flag even on failure
        }

        public static void CreateRecentPlayersButton(Canvas canvas = null)
        {
            // Check if button already exists
            if (recentPlayersButton != null)
            {
                logger?.LogInfo("Recent Players button already exists, skipping creation");
                return;
            }
            
            // Check if we're already waiting for BanModUI
            if (isWaitingForBanModUI)
            {
                logger?.LogInfo("Already waiting for BanModUI, skipping duplicate coroutine");
                return;
            }
            
            // Look for BanModUI instead of just the canvas
            GameObject banModUI = ResolveBanModUI();
            
            if (banModUI == null)
            {
                logger?.LogWarning("BanModUI not found, starting wait coroutine");
                isWaitingForBanModUI = true; // Set flag before starting coroutine
                
                // Start coroutine to wait for BanModUI
                if (UnityEngine.Object.FindFirstObjectByType<MonoBehaviour>() != null)
                {
                    UnityEngine.Object.FindFirstObjectByType<MonoBehaviour>().StartCoroutine(
                        WaitForBanModUI(foundUI => CreateRecentPlayersButton())
                    );
                }
                else
                {
                    isWaitingForBanModUI = false; // Reset flag if we can't start coroutine
                }
                return;
            }
            
            // Set mainCanvas for reference but attach button to BanModUI
            if (canvas != null)
            {
                mainCanvas = canvas;
            }
            else
            {
                var banModCanvas = GameObject.Find("BanModCanvas");
                if (banModCanvas != null)
                {
                    mainCanvas = banModCanvas.GetComponent<Canvas>();
                }
            }
            
            // Create Recent Players Button as child of BanModUI
            recentPlayersButton = new GameObject("RecentPlayersButton");
            recentPlayersButton.transform.SetParent(banModUI.transform, false);
            
            var buttonComponent = recentPlayersButton.AddComponent<Button>();
            var buttonImage = recentPlayersButton.AddComponent<Image>();
            buttonImage.color = new Color(0.2f, 0.4f, 0.8f, 0.9f);
            
            var buttonRect = recentPlayersButton.GetComponent<RectTransform>();
            // Position in bottom left corner
            buttonRect.anchorMin = new Vector2(0, 0);
            buttonRect.anchorMax = new Vector2(0, 0);
            buttonRect.pivot = new Vector2(0, 0);
            buttonRect.anchoredPosition = new Vector2(24, 24); // offset from bottom-left
            buttonRect.sizeDelta = new Vector2(200, 52); // slightly bigger
            
            // Set high sorting order to render on top
            recentPlayersButton.transform.SetAsLastSibling();
            
            // Button Text
            var textObj = new GameObject("ButtonText");
            textObj.transform.SetParent(recentPlayersButton.transform, false);
            var buttonText = textObj.AddComponent<Text>();
            buttonText.text = "Recent Players";
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
            
            // Button click event
            buttonComponent.onClick.AddListener(ToggleRecentPlayersPanel);
            
            // Start hidden - only show when Ban UI is active
            recentPlayersButton.SetActive(false);
            
            logger?.LogInfo($"Recent Players button created as child of BanModUI (initially hidden)");
        }

        public static bool IsButtonCreated()
        {
            return recentPlayersButton != null;
        }

        public static void CleanupExistingUI()
        {
            try
            {
                // Reset our references
                recentPlayersButton = null;
                recentPlayersPanel = null;
                isWaitingForBanModUI = false;
                
                logger?.LogInfo("RecentPlayersManager UI references cleared");
            }
            catch (Exception ex)
            {
                logger?.LogError($"Error cleaning up RecentPlayersManager UI: {ex.Message}");
            }
        }

        public static void EnsureRecentPlayersButton(Canvas canvas = null)
        {
            if (!IsButtonCreated())
            {
                CreateRecentPlayersButton(canvas);
            }
        }

        public static void SetButtonVisible(bool visible)
        {
            if (recentPlayersButton == null)
            {
                if (visible)
                {
                    // Create the button if it doesn't exist and we want to show it
                    CreateRecentPlayersButton();
                }
                return;
            }
            
            recentPlayersButton.SetActive(visible);
            
            // If hiding the button or UI, also hide the panel
            if (!visible && isRecentPlayersVisible)
            {
                HideRecentPlayersPanel();
            }
        }

        private static void ToggleRecentPlayersPanel()
        {
            if (isRecentPlayersVisible)
            {
                HideRecentPlayersPanel();
            }
            else
            {
                ShowRecentPlayersPanel();
            }
        }

        private static void ShowRecentPlayersPanel()
        {
            if (mainCanvas == null) 
            {
                mainCanvas = ResolveBanModCanvas();
                logger?.LogInfo($"Resolved mainCanvas: {(mainCanvas != null ? mainCanvas.name : "null")}");
            }
            
            if (mainCanvas == null)
            {
                logger?.LogWarning("[RecentPlayers] No canvas found; cannot show panel");
                return;
            }
            
            if (recentPlayersPanel != null)
            {
                recentPlayersPanel.SetActive(true);
                isRecentPlayersVisible = true;
                return;
            }

            CreateRecentPlayersPanel();
            isRecentPlayersVisible = true;
            logger?.LogInfo("Recent Players panel shown");
        }

        private static void HideRecentPlayersPanel()
        {
            if (recentPlayersPanel != null)
            {
                recentPlayersPanel.SetActive(false);
            }
            isRecentPlayersVisible = false;
            logger?.LogInfo("Recent Players panel hidden");
        }

        private static void CreateRecentPlayersPanel()
        {
            logger?.LogInfo("CreateRecentPlayersPanel() called");
            
            // Use the same canvas as the main UI
            if (mainCanvas == null)
            {
                logger?.LogInfo("mainCanvas is null, trying to resolve BanModCanvas");
                mainCanvas = ResolveBanModCanvas();
                logger?.LogInfo($"Resolved mainCanvas: {(mainCanvas != null ? mainCanvas.name : "null")}");
            }
            
            if (mainCanvas == null) 
            {
                logger?.LogError("No canvas found for Recent Players panel");
                return;
            }
            
            logger?.LogInfo($"Creating panel on canvas: {mainCanvas.name}");
            
            // Create panel container - attach to canvas, not BanModUI
            recentPlayersPanel = new GameObject("RecentPlayersPanel");
            if (recentPlayersPanel == null)
            {
                logger?.LogError("Failed to create RecentPlayersPanel GameObject");
                return;
            }
            
            logger?.LogInfo("Setting panel parent");
            recentPlayersPanel.transform.SetParent(mainCanvas.transform, false);
            
            logger?.LogInfo("Adding panel image component");
            var panelImage = recentPlayersPanel.AddComponent<Image>();
            if (panelImage == null)
            {
                logger?.LogError("Failed to add Image component to panel");
                return;
            }
            panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);
            
            logger?.LogInfo("Setting up panel rect transform");
            var panelRect = recentPlayersPanel.GetComponent<RectTransform>();
            if (panelRect == null)
            {
                logger?.LogError("Failed to get RectTransform from panel");
                return;
            }
            
            // Position above the button - moved higher to avoid overlap
            panelRect.anchorMin = new Vector2(0, 0);
            panelRect.anchorMax = new Vector2(0, 0);
            panelRect.pivot = new Vector2(0, 0);
            panelRect.anchoredPosition = new Vector2(20, 90); // Moved from 70 to 90 to avoid overlap
            
            // Calculate height based on number of current game players
            int playerCount = currentGamePlayers.Count;
            float panelHeight = Math.Max(100, playerCount * 35 + 60); // 35px per player + padding
            panelRect.sizeDelta = new Vector2(400, panelHeight); // Increased width for ban button
            
            logger?.LogInfo("Adding panel outline");
            // Add border
            var outline = recentPlayersPanel.AddComponent<Outline>();
            if (outline != null)
            {
                outline.effectColor = Color.white;
                outline.effectDistance = new Vector2(2, 2);
            }
            
            // Set very high sorting order to ensure it renders on top of everything
            recentPlayersPanel.transform.SetAsLastSibling();
            
            logger?.LogInfo("Adding panel canvas override");
            // Force the panel to render on top by setting a higher Canvas sorting order
            var panelCanvas = recentPlayersPanel.AddComponent<Canvas>();
            if (panelCanvas != null)
            {
                panelCanvas.overrideSorting = true;
                panelCanvas.sortingOrder = 2000; // Higher than the main UI canvas
            }
            
            // Add GraphicRaycaster so buttons work
            recentPlayersPanel.AddComponent<GraphicRaycaster>();
            
            logger?.LogInfo("Creating panel title");
            // Title
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(recentPlayersPanel.transform, false);
            var titleText = titleObj.AddComponent<Text>();
            titleText.text = "Recent Players";
            titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            titleText.fontSize = 16;
            titleText.color = Color.yellow;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.fontStyle = FontStyle.Bold;
            
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0.5f, 1);
            titleRect.offsetMin = new Vector2(10, -30);
            titleRect.offsetMax = new Vector2(-10, -5);
            
            logger?.LogInfo("Creating scroll area");
            // Create scroll area for players
            var scrollObj = new GameObject("ScrollArea");
            logger?.LogInfo("Created ScrollArea GameObject");
            
            scrollObj.transform.SetParent(recentPlayersPanel.transform, false);
            logger?.LogInfo("Set ScrollArea parent");
            
            var scrollRect = scrollObj.AddComponent<ScrollRect>();
            logger?.LogInfo("Added ScrollRect component");
            
            scrollObj.AddComponent<Mask>();
            logger?.LogInfo("Added Mask component");
            
            var scrollImage = scrollObj.AddComponent<Image>();
            logger?.LogInfo("Added Image component to scroll area");
            
            if (scrollImage != null)
            {
                scrollImage.color = new Color(0.05f, 0.05f, 0.05f, 0.8f);
                logger?.LogInfo("Set scroll image color");
            }
            else
            {
                logger?.LogError("scrollImage is null!");
            }
            
            var scrollRectTransform = scrollObj.GetComponent<RectTransform>();
            logger?.LogInfo("Got scroll RectTransform");
            
            if (scrollRectTransform != null)
            {
                scrollRectTransform.anchorMin = new Vector2(0, 0);
                scrollRectTransform.anchorMax = new Vector2(1, 1);
                scrollRectTransform.offsetMin = new Vector2(5, 5);
                scrollRectTransform.offsetMax = new Vector2(-5, -35);
                logger?.LogInfo("Set scroll rect transform properties");
            }
            else
            {
                logger?.LogError("scrollRectTransform is null!");
            }
            
            logger?.LogInfo("Creating content container");
            // Content container
            var contentObj = new GameObject("Content");
            logger?.LogInfo("Created Content GameObject");
            
            // Add RectTransform component explicitly
            var contentRect = contentObj.AddComponent<RectTransform>();
            logger?.LogInfo("Added RectTransform to Content");
            
            contentObj.transform.SetParent(scrollObj.transform, false);
            logger?.LogInfo("Set Content parent");
            
            if (contentRect != null)
            {
                contentRect.anchorMin = new Vector2(0, 1);
                contentRect.anchorMax = new Vector2(1, 1);
                contentRect.pivot = new Vector2(0.5f, 1);
                logger?.LogInfo("Set content rect properties");
            }
            else
            {
                logger?.LogError("contentRect is still null after AddComponent!");
            }
            
            if (scrollRect != null)
            {
                scrollRect.content = contentRect;
                scrollRect.horizontal = false;
                scrollRect.vertical = true;
                scrollRect.scrollSensitivity = 10f;
                logger?.LogInfo("Configured ScrollRect properties");
            }
            else
            {
                logger?.LogError("scrollRect is null!");
            }
            
            logger?.LogInfo("Populating players list");
            PopulateRecentPlayersList(contentObj);
            
            logger?.LogInfo("Adding click-away handler");
            // Add click-away functionality
            AddClickAwayHandler();
            
            logger?.LogInfo("CreateRecentPlayersPanel() completed successfully");
        }

        private static void PopulateRecentPlayersList(GameObject contentParent)
        {
            // Clear existing entries
            foreach (Transform child in contentParent.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }
            
            if (currentGamePlayers.Count == 0)
            {
                // Show "No recent players" message
                var noPlayersObj = new GameObject("NoRecentPlayers");
                noPlayersObj.transform.SetParent(contentParent.transform, false);
                var noPlayersText = noPlayersObj.AddComponent<Text>();
                noPlayersText.text = "No recent game played";
                noPlayersText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                noPlayersText.fontSize = 14;
                noPlayersText.color = Color.gray;
                noPlayersText.alignment = TextAnchor.MiddleCenter;
                
                var noPlayersRect = noPlayersObj.GetComponent<RectTransform>();
                noPlayersRect.anchorMin = new Vector2(0, 1);
                noPlayersRect.anchorMax = new Vector2(1, 1);
                noPlayersRect.pivot = new Vector2(0.5f, 1);
                noPlayersRect.offsetMin = new Vector2(10, -35);
                noPlayersRect.offsetMax = new Vector2(-10, -5);
                
                var contentRect = contentParent.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, 40);
                return;
            }
            
            // Show all players from current/last game
            for (int i = 0; i < currentGamePlayers.Count; i++)
            {
                var player = currentGamePlayers[i];
                CreatePlayerEntry(contentParent, player, i);
            }
            
            // Update content size
            var contentRect2 = contentParent.GetComponent<RectTransform>();
            contentRect2.sizeDelta = new Vector2(0, currentGamePlayers.Count * 35 + 5);
        }

        private static void CreatePlayerEntry(GameObject parent, RecentPlayerData player, int index)
        {
            var entryObj = new GameObject($"PlayerEntry_{index}");
            entryObj.transform.SetParent(parent.transform, false);
            
            var entryImage = entryObj.AddComponent<Image>();
            entryImage.color = new Color(0.2f, 0.2f, 0.2f, 0.7f);
            
            var entryRect = entryObj.GetComponent<RectTransform>();
            entryRect.anchorMin = new Vector2(0, 1);
            entryRect.anchorMax = new Vector2(1, 1);
            entryRect.pivot = new Vector2(0.5f, 1);
            entryRect.offsetMin = new Vector2(5, -30 - (index * 35));
            entryRect.offsetMax = new Vector2(-5, -5 - (index * 35));
            
            // Player name
            var nameObj = new GameObject("PlayerName");
            nameObj.transform.SetParent(entryObj.transform, false);
            var nameText = nameObj.AddComponent<Text>();
            nameText.text = player.playerName;
            nameText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameText.fontSize = 12;
            nameText.color = Color.white;
            nameText.alignment = TextAnchor.MiddleLeft;
            
            var nameRect = nameObj.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0);
            nameRect.anchorMax = new Vector2(0.45f, 1);
            nameRect.offsetMin = new Vector2(8, 2);
            nameRect.offsetMax = new Vector2(-5, -2);
            
            // Team
            var teamObj = new GameObject("Team");
            teamObj.transform.SetParent(entryObj.transform, false);
            var teamText = teamObj.AddComponent<Text>();
            teamText.text = $"({player.team})";
            teamText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            teamText.fontSize = 12;
            teamText.color = player.team.ToLower() == "sorcerer" ? new Color(0.8f, 0.3f, 0.8f) : new Color(0.3f, 0.8f, 0.3f);
            teamText.alignment = TextAnchor.MiddleCenter;
            
            var teamRect = teamObj.GetComponent<RectTransform>();
            teamRect.anchorMin = new Vector2(0.45f, 0);
            teamRect.anchorMax = new Vector2(0.65f, 1);
            teamRect.offsetMin = new Vector2(5, 2);
            teamRect.offsetMax = new Vector2(-5, -2);
            
            // Ban/Unban Button
            bool isPlayerBanned = false;
            if (getBannedPlayers != null)
            {
                var bannedPlayers = getBannedPlayers();
                isPlayerBanned = bannedPlayers.ContainsKey(player.steamId);
            }
            
            var banButtonObj = new GameObject("BanButton");
            banButtonObj.transform.SetParent(entryObj.transform, false);
            var banButton = banButtonObj.AddComponent<Button>();
            var banButtonImage = banButtonObj.AddComponent<Image>();
            
            // Set button color and text based on ban status
            if (isPlayerBanned)
            {
                banButtonImage.color = new Color(0.2f, 0.6f, 0.2f, 0.9f); // Green for unban
            }
            else
            {
                banButtonImage.color = new Color(0.8f, 0.2f, 0.2f, 0.9f); // Red for ban
            }
            
            var banButtonRect = banButtonObj.GetComponent<RectTransform>();
            banButtonRect.anchorMin = new Vector2(0.65f, 0.15f);
            banButtonRect.anchorMax = new Vector2(0.95f, 0.85f);
            banButtonRect.offsetMin = new Vector2(5, 0);
            banButtonRect.offsetMax = new Vector2(-5, 0);
            
            // Button text
            var banButtonTextObj = new GameObject("BanButtonText");
            banButtonTextObj.transform.SetParent(banButtonObj.transform, false);
            var banButtonText = banButtonTextObj.AddComponent<Text>();
            banButtonText.text = isPlayerBanned ? "Unban" : "Ban";
            banButtonText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            banButtonText.fontSize = 11;
            banButtonText.color = Color.white;
            banButtonText.alignment = TextAnchor.MiddleCenter;
            banButtonText.fontStyle = FontStyle.Bold;
            
            var banButtonTextRect = banButtonTextObj.GetComponent<RectTransform>();
            banButtonTextRect.anchorMin = Vector2.zero;
            banButtonTextRect.anchorMax = Vector2.one;
            banButtonTextRect.offsetMin = Vector2.zero;
            banButtonTextRect.offsetMax = Vector2.zero;
            
            // Button click event
            banButton.onClick.AddListener(() => 
            {
                if (toggleBanPlayer != null)
                {
                    toggleBanPlayer(player.steamId, player.playerName);
                    logger?.LogInfo($"{(isPlayerBanned ? "Unbanned" : "Banned")} player: {player.playerName} ({player.steamId})");
                    
                    // Refresh the panel to update button states
                    if (isRecentPlayersVisible && recentPlayersPanel != null)
                    {
                        UnityEngine.Object.Destroy(recentPlayersPanel);
                        CreateRecentPlayersPanel();
                    }
                }
                else
                {
                    logger?.LogWarning("Cannot ban/unban player - ban functions not set");
                }
            });
        }

        private static void AddClickAwayHandler()
        {
            // Create invisible overlay to detect clicks outside the panel
            var overlayObj = new GameObject("ClickAwayOverlay");
            overlayObj.transform.SetParent(mainCanvas.transform, false);
            overlayObj.transform.SetSiblingIndex(Mathf.Max(0, recentPlayersPanel.transform.GetSiblingIndex() - 1)); // directly behind panel
            
            var overlayImage = overlayObj.AddComponent<Image>();
            overlayImage.color = new Color(0, 0, 0, 0); // Transparent
            overlayImage.raycastTarget = true;
            
            var overlayRect = overlayObj.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            
            var overlayButton = overlayObj.AddComponent<Button>();
            overlayButton.onClick.AddListener(() => {
                HideRecentPlayersPanel();
                UnityEngine.Object.Destroy(overlayObj);
            });
        }

        // Call this when a game starts to capture the current lobby players
        public static void CaptureRecentPlayers(Dictionary<string, string> connectedPlayers, Func<string, string> getPlayerTeam)
        {
            if (connectedPlayers == null || connectedPlayers.Count == 0)
            {
                logger?.LogInfo("No players to capture for recent players list");
                return;
            }

            // Only capture if we haven't already captured for this game session
            if (hasCapturedCurrentGame)
            {
                return;
            }

            var gameStartTime = DateTime.Now;
            
            // Mark that we've captured for this game session
            hasCapturedCurrentGame = true;
            lastGameStartTime = gameStartTime;

            // Clear previous game's players and capture new ones
            currentGamePlayers.Clear();

            foreach (var kvp in connectedPlayers)
            {
                string playerName = kvp.Key;
                string steamId = kvp.Value;
                string team = getPlayerTeam?.Invoke(steamId) ?? "unknown";

                currentGamePlayers.Add(new RecentPlayerData
                {
                    playerName = playerName,
                    steamId = steamId,
                    team = team,
                    gameStartTime = gameStartTime
                });
            }

            logger?.LogInfo($"Captured {currentGamePlayers.Count} players from game started at {gameStartTime} (capture flag set to true)");
            
            // Save to file
            SaveRecentPlayersToFile(gameStartTime);
            
            // Refresh the panel if it's currently visible
            if (isRecentPlayersVisible && recentPlayersPanel != null)
            {
                UnityEngine.Object.Destroy(recentPlayersPanel);
                CreateRecentPlayersPanel();
            }
        }

        private static void SaveRecentPlayersToFile(DateTime lobbyStartTime)
        {
            try
            {
                if (string.IsNullOrEmpty(recentPlayersFilePath)) return;
                
                // Read existing records
                var existingRecords = LoadRecentPlayersFromFile();
                
                // Add new lobby header and players
                var newRecords = new List<RecentPlayerRecord>();
                
                // Add lobby header
                var lobbyHeader = new RecentPlayerRecord
                {
                    LobbyDateTime = $"[{lobbyStartTime:yyyy-MM-dd HH:mm:ss}]",
                    PlayerName = "",
                    SteamID = "",
                    Team = "",
                    BanStatus = ""
                };
                newRecords.Add(lobbyHeader);
                
                // Add players for this lobby
                var bannedPlayers = getBannedPlayers?.Invoke();
                foreach (var player in currentGamePlayers)
                {
                    bool isBanned = bannedPlayers?.ContainsKey(player.steamId) == true;
                    var record = new RecentPlayerRecord
                    {
                        LobbyDateTime = "",
                        PlayerName = player.playerName,
                        SteamID = player.steamId,
                        Team = player.team,
                        BanStatus = isBanned ? "banned" : "notbanned"
                    };
                    newRecords.Add(record);
                }
                
                // Combine with existing records
                var allRecords = new List<RecentPlayerRecord>();
                allRecords.AddRange(newRecords);
                allRecords.AddRange(existingRecords);
                
                // Keep only the most recent entries up to MAX_STORED_PLAYERS
                // Count actual player records (not lobby headers)
                var playerRecords = allRecords.Where(r => !string.IsNullOrEmpty(r.PlayerName)).ToList();
                if (playerRecords.Count > MAX_STORED_PLAYERS)
                {
                    // Keep the newest MAX_STORED_PLAYERS player records
                    var playersToKeep = playerRecords.Take(MAX_STORED_PLAYERS).ToList();
                    
                    // Rebuild allRecords with only the lobbies that have players we're keeping
                    var steamIdsToKeep = new HashSet<string>();
                    foreach (var p in playersToKeep)
                    {
                        steamIdsToKeep.Add(p.SteamID);
                    }
                    
                    var filteredRecords = new List<RecentPlayerRecord>();
                    foreach (var r in allRecords)
                    {
                        if (string.IsNullOrEmpty(r.PlayerName) || steamIdsToKeep.Contains(r.SteamID))
                        {
                            filteredRecords.Add(r);
                        }
                    }
                    allRecords = filteredRecords;
                    
                    logger?.LogInfo($"Trimmed recent players file to keep {playersToKeep.Count} most recent players");
                }
                
                // Write to file
                using (var fs = new FileStream(recentPlayersFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    foreach (var record in allRecords)
                    {
                        string json = JsonUtility.ToJson(record);
                        sw.WriteLine(json);
                    }
                }
                
                int playerCount = 0;
                foreach (var r in allRecords)
                {
                    if (!string.IsNullOrEmpty(r.PlayerName))
                        playerCount++;
                }
                logger?.LogInfo($"Saved recent players to file: {playerCount} players across multiple lobbies");
            }
            catch (Exception e)
            {
                logger?.LogError($"Error saving recent players to file: {e.Message}");
            }
        }
        
        private static void LoadMostRecentLobbyFromFile()
        {
            try
            {
                if (string.IsNullOrEmpty(recentPlayersFilePath) || !File.Exists(recentPlayersFilePath))
                    return;
                
                var allRecords = LoadRecentPlayersFromFile();
                if (allRecords.Count == 0)
                    return;
                
                // Find the most recent lobby (first lobby header in the file since newest entries are at the top)
                RecentPlayerRecord mostRecentLobbyHeader = null;
                var mostRecentPlayers = new List<RecentPlayerRecord>();
                
                foreach (var record in allRecords)
                {
                    if (!string.IsNullOrEmpty(record.LobbyDateTime))
                    {
                        // This is a lobby header
                        if (mostRecentLobbyHeader == null)
                        {
                            mostRecentLobbyHeader = record;
                        }
                        else
                        {
                            // We've reached the next (older) lobby, stop collecting players
                            break;
                        }
                    }
                    else if (mostRecentLobbyHeader != null && !string.IsNullOrEmpty(record.PlayerName))
                    {
                        // This is a player record from the most recent lobby
                        mostRecentPlayers.Add(record);
                    }
                }
                
                if (mostRecentLobbyHeader != null && mostRecentPlayers.Count > 0)
                {
                    // Parse the lobby date time
                    string dateTimeStr = mostRecentLobbyHeader.LobbyDateTime.Trim('[', ']');
                    DateTime lobbyTime;
                    if (!DateTime.TryParse(dateTimeStr, out lobbyTime))
                    {
                        lobbyTime = DateTime.Now;
                    }
                    
                    // Convert to currentGamePlayers format
                    currentGamePlayers.Clear();
                    foreach (var playerRecord in mostRecentPlayers)
                    {
                        currentGamePlayers.Add(new RecentPlayerData
                        {
                            playerName = playerRecord.PlayerName,
                            steamId = playerRecord.SteamID,
                            team = playerRecord.Team,
                            gameStartTime = lobbyTime
                        });
                    }
                    
                    lastGameStartTime = lobbyTime;
                    logger?.LogInfo($"Loaded {currentGamePlayers.Count} players from most recent lobby ({dateTimeStr})");
                }
            }
            catch (Exception e)
            {
                logger?.LogError($"Error loading most recent lobby from file: {e.Message}");
            }
        }
        
        private static List<RecentPlayerRecord> LoadRecentPlayersFromFile()
        {
            var records = new List<RecentPlayerRecord>();
            
            try
            {
                if (string.IsNullOrEmpty(recentPlayersFilePath) || !File.Exists(recentPlayersFilePath))
                    return records;
                
                var lines = File.ReadAllLines(recentPlayersFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    try
                    {
                        var record = JsonUtility.FromJson<RecentPlayerRecord>(line);
                        if (record != null)
                        {
                            records.Add(record);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"Failed to parse recent player record line: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                logger?.LogError($"Error loading recent players from file: {e.Message}");
            }
            
            return records;
        }

        public static void DestroyRecentPlayersUI()
        {
            try
            {
                if (recentPlayersButton != null)
                {
                    UnityEngine.Object.Destroy(recentPlayersButton);
                    recentPlayersButton = null;
                }
                
                if (recentPlayersPanel != null)
                {
                    UnityEngine.Object.Destroy(recentPlayersPanel);
                    recentPlayersPanel = null;
                }
                
                isRecentPlayersVisible = false;
                logger?.LogInfo("Recent Players UI destroyed");
            }
            catch (Exception e)
            {
                logger?.LogError($"Error destroying Recent Players UI: {e.Message}");
            }
        }

        // Call this when leaving lobby to reset capture state
        public static void OnLeaveLobby()
        {
            hasCapturedCurrentGame = false;
            logger?.LogInfo("Reset recent players capture state - ready for new game session");
        }

        // Call this when ban status changes to refresh the Recent Players panel
        public static void OnBanStatusChanged()
        {
            if (isRecentPlayersVisible && recentPlayersPanel != null)
            {
                logger?.LogInfo("Refreshing Recent Players panel due to ban status change");
                UnityEngine.Object.Destroy(recentPlayersPanel);
                CreateRecentPlayersPanel();
            }
        }
        
        // Call this when tab changes in the main UI to hide the recent players panel
        public static void OnTabChanged()
        {
            if (isRecentPlayersVisible)
            {
                HideRecentPlayersPanel();
                logger?.LogInfo("Recent Players panel hidden due to tab change");
            }
        }
        
        public static bool IsRecentPlayersVisible => isRecentPlayersVisible;
        
        // Get count of players from current/last game
        public static int GetCurrentGamePlayerCount() => currentGamePlayers.Count;
        
        // Get the last game start time
        public static DateTime GetLastGameStartTime() => lastGameStartTime;
        
        // Check if we have players from a recent game
        public static bool HasRecentGameData() => currentGamePlayers.Count > 0;
    }
}