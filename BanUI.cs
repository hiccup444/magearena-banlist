using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace PlayerBanMod
{
    public static class BanUIManager
    {
        private static ManualLogSource logger;
        private static ConfigEntry<bool> autobanModdedRanksConfig;
        private static ConfigEntry<bool> autobanOffensiveNamesConfig;
        private static ConfigEntry<bool> autobanFormattedNamesConfig;

        private static Func<Dictionary<string, string>> getConnectedPlayers;
        private static Func<Dictionary<string, string>> getBannedPlayers;
        private static Func<Dictionary<string, DateTime>> getBanTimestamps;
        private static Func<Dictionary<string, string>> getBanReasons;
        private static Action<string, string> kickPlayer;
        private static Action<string, string> toggleBanPlayer;
        private static Action<string, string, string> banPlayerWithReason;
        private static Action<string, string> unbanPlayer;

        private static GameObject banUI;
        private static GameObject banUIPanel;
        private static Transform playerListContent;
        private static Transform bannedPlayersContent;
        private static GameObject activePlayersTab;
        private static GameObject bannedPlayersTab;
        private static Toggle autobanModdedRanksToggle;
        private static Toggle autobanOffensiveNamesToggle;
        private static readonly Regex richTextTagPattern = new Regex("<\\s*/?\\s*(color|b|i|size|material)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Logging UI components
        private static GameObject loggingButton;
        private static GameObject loggingPanel;
        private static Transform logContent;
        private static bool isLoggingVisible = false;
        private static List<string> logMessages = new List<string>();
        private const int MAX_LOG_MESSAGES = 1000;

        private static bool isFirstTimeOpeningBannedTab = true;
        private static int bannedPlayersPageIndex = 0;
        private const int MAX_VISIBLE_BANNED_ITEMS = 50;
        private static string bannedSearchQuery = string.Empty;
        private static InputField bannedSearchInput;
        private static bool shouldRestoreFocus = false;
        
        // UI auto-rebuild system
        private static bool isWaitingForUIRebuild = false;
        private static float lastUIRebuildWarningTime = 0f;
        private const float UI_REBUILD_DELAY = 5f; // 5 seconds delay before auto-rebuild

        public static bool IsActive => banUI != null && banUI.activeSelf;
        
        public static bool IsAutoRebuildActive => isWaitingForUIRebuild;

        public static void Initialize(
            ManualLogSource log,
            ConfigEntry<bool> autoModded,
            ConfigEntry<bool> autoOffensive,
            ConfigEntry<bool> autoFormatted,
            Func<Dictionary<string, string>> getConn,
            Func<Dictionary<string, string>> getBanned,
            Func<Dictionary<string, DateTime>> getTimestamps,
            Func<Dictionary<string, string>> getReasons,
            Action<string, string> kick,
            Action<string, string> toggleBan,
            Action<string, string> unban,
            Action<string, string, string> banWithReason)
        {
            logger = log;
            autobanModdedRanksConfig = autoModded;
            autobanOffensiveNamesConfig = autoOffensive;
            autobanFormattedNamesConfig = autoFormatted;
            getConnectedPlayers = getConn;
            getBannedPlayers = getBanned;
            getBanTimestamps = getTimestamps;
            getBanReasons = getReasons;
            kickPlayer = kick;
            toggleBanPlayer = toggleBan;
            unbanPlayer = unban;
            banPlayerWithReason = banWithReason;
        }

        public static bool IsUICreated()
        {
            return banUI != null;
        }
        
        public static bool IsUIHealthy()
        {
            return banUI != null && 
                   playerListContent != null && 
                   bannedPlayersContent != null && 
                   activePlayersTab != null && 
                   bannedPlayersTab != null;
        }
        
        public static void TriggerAutoRebuild()
        {
            if (!isWaitingForUIRebuild)
            {
                HandleUIRecreationWarning();
            }
        }
        
        public static void CancelAutoRebuild()
        {
            isWaitingForUIRebuild = false;
            logger?.LogInfo("UI auto-rebuild cancelled");
        }
        
        private static void HandleUIRecreationWarning()
        {
            if (isWaitingForUIRebuild)
            {
                return; // Already waiting for rebuild
            }
            
            // Check if we've had too many warnings recently
            if (Time.time - lastUIRebuildWarningTime < 2f) // Prevent spam warnings
            {
                return;
            }
            
            lastUIRebuildWarningTime = Time.time;
            isWaitingForUIRebuild = true;
            
            logger?.LogWarning("UI recreation warning detected - will attempt auto-rebuild in 5 seconds if UI is still not present");
            
            // Start coroutine to wait and check if rebuild is needed
            if (UnityEngine.Object.FindFirstObjectByType<MonoBehaviour>() != null)
            {
                UnityEngine.Object.FindFirstObjectByType<MonoBehaviour>().StartCoroutine(CheckAndRebuildUI());
            }
            else
            {
                isWaitingForUIRebuild = false;
            }
        }
        
        private static System.Collections.IEnumerator CheckAndRebuildUI()
        {
            logger?.LogInfo($"Waiting {UI_REBUILD_DELAY} seconds before checking UI health...");
            yield return new WaitForSeconds(UI_REBUILD_DELAY);
            
            // Check if UI is still broken after 5 seconds
            if (!IsUICreated() || playerListContent == null || bannedPlayersContent == null || loggingButton == null)
            {
                logger?.LogWarning("UI still not properly initialized after 5 seconds - triggering auto-rebuild");
                
                // Destroy any existing UI components
                DestroyUI();
                
                // Wait a frame for cleanup
                yield return null;
                
                // Recreate the UI
                yield return CreateUI();
                
                logger?.LogInfo("UI auto-rebuild completed");
            }
            else
            {
                logger?.LogInfo("UI appears to be working now - no rebuild needed");
            }
            
            isWaitingForUIRebuild = false;
        }

        public static void DestroyUI()
        {
            try
            {
                if (banUI != null)
                {
                    logger?.LogInfo("Destroying Ban UI...");
                    
                    // Find and destroy the entire canvas
                    var canvas = banUI.transform.root.gameObject;
                    if (canvas != null && canvas.name == "BanModCanvas")
                    {
                        UnityEngine.Object.Destroy(canvas);
                    }
                    else
                    {
                        // Fallback - destroy just the UI if we can't find the canvas
                        UnityEngine.Object.Destroy(banUI);
                    }
                    
                    // Reset all UI references
                    banUI = null;
                    banUIPanel = null;
                    playerListContent = null;
                    bannedPlayersContent = null;
                    activePlayersTab = null;
                    bannedPlayersTab = null;
                    autobanModdedRanksToggle = null;
                    autobanOffensiveNamesToggle = null;
                    bannedSearchInput = null;
                    
                    // Reset logging UI references
                    loggingButton = null;
                    loggingPanel = null;
                    isLoggingVisible = false;
                    
                    // Reset state
                    isFirstTimeOpeningBannedTab = true;
                    bannedPlayersPageIndex = 0;
                    bannedSearchQuery = string.Empty;
                    
                    logger?.LogInfo("Ban UI destroyed successfully");
                }
            }
            catch (Exception e)
            {
                logger?.LogError($"Error destroying Ban UI: {e.Message}");
            }
        }

        public static IEnumerator CreateUI()
        {
            // Check if UI already exists to prevent duplicates
            if (banUI != null)
            {
                logger?.LogInfo("Ban UI already exists, skipping creation");
                yield break;
            }

            // Clean up any existing UI elements to prevent duplicates
            CleanupExistingUI();

            // Wait a bit more for the game to fully initialize
            yield return new WaitForSeconds(1f);

            logger?.LogInfo("Creating Ban UI...");

            // Canvas
            GameObject canvasObj = new GameObject("BanModCanvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasObj.AddComponent<GraphicRaycaster>();
            RecentPlayersManager.CreateRecentPlayersButton(canvas);
            
            // Root
            banUI = new GameObject("BanModUI");
            banUI.transform.SetParent(canvasObj.transform, false);
            banUI.SetActive(false);
            var mainRect = banUI.AddComponent<RectTransform>();
            mainRect.anchorMin = Vector2.zero;
            mainRect.anchorMax = Vector2.one;
            mainRect.offsetMin = Vector2.zero;
            mainRect.offsetMax = Vector2.zero;

            // Panel
            banUIPanel = new GameObject("BanUIPanel");
            banUIPanel.transform.SetParent(banUI.transform, false);
            var panelImage = banUIPanel.AddComponent<Image>();
            panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);
            var panelRect = banUIPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            // Title
            var titleObj = new GameObject("Title");
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleObj.transform.SetParent(banUIPanel.transform, false);
            var titleText = titleObj.AddComponent<Text>();
            titleText.text = "Player Management";
            titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            titleText.fontSize = 24;
            titleText.color = Color.white;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleRect.anchorMin = new Vector2(0.1f, 0.85f);
            titleRect.anchorMax = new Vector2(0.9f, 0.95f);
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;

            // Close button
            var closeButtonObj = new GameObject("CloseButton");
            closeButtonObj.transform.SetParent(banUIPanel.transform, false);
            var closeButton = closeButtonObj.AddComponent<Button>();
            var closeButtonImage = closeButtonObj.AddComponent<Image>();
            closeButtonImage.color = new Color(0.8f, 0.2f, 0.2f);
            var closeButtonRect = closeButtonObj.GetComponent<RectTransform>();
            closeButtonRect.anchorMin = new Vector2(0.9f, 0.9f);
            closeButtonRect.anchorMax = new Vector2(0.97f, 0.97f);
            closeButtonRect.offsetMin = Vector2.zero;
            closeButtonRect.offsetMax = Vector2.zero;

            var closeTextObj = new GameObject("CloseText");
            var closeTextRect = closeTextObj.AddComponent<RectTransform>();
            closeTextObj.transform.SetParent(closeButtonObj.transform, false);
            var closeText = closeTextObj.AddComponent<Text>();
            closeText.text = "X";
            closeText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            closeText.fontSize = 14;
            closeText.color = Color.white;
            closeText.alignment = TextAnchor.MiddleCenter;
            closeTextRect.anchorMin = Vector2.zero;
            closeTextRect.anchorMax = Vector2.one;
            closeTextRect.offsetMin = Vector2.zero;
            closeTextRect.offsetMax = Vector2.zero;
            closeButton.onClick.AddListener(() => banUI.SetActive(false));

            // Tabs
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

            // Toggles
            Color toggleOffColor = new Color(0.2f, 0.2f, 0.2f, 0.8f);
            Color toggleOnColor = new Color(0.2f, 0.6f, 0.2f, 0.9f);
            var autoModToggleObj = new GameObject("AutobanModdedRanksToggle");
            autoModToggleObj.transform.SetParent(banUIPanel.transform, false);
            autobanModdedRanksToggle = autoModToggleObj.AddComponent<Toggle>();
            var autoModToggleImage = autoModToggleObj.AddComponent<Image>();
            autoModToggleImage.color = toggleOffColor;
            var autoModToggleRect = autoModToggleObj.GetComponent<RectTransform>();
            autoModToggleRect.anchorMin = new Vector2(0.05f, 0.92f);
            autoModToggleRect.anchorMax = new Vector2(0.08f, 0.97f);
            autoModToggleRect.offsetMin = Vector2.zero;
            autoModToggleRect.offsetMax = Vector2.zero;

            var autoModLabelObj = new GameObject("AutobanModdedRanksLabel");
            autoModLabelObj.transform.SetParent(banUIPanel.transform, false);
            var autoModLabel = autoModLabelObj.AddComponent<Text>();
            autoModLabel.text = "Autoban Modded Ranks";
            autoModLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            autoModLabel.fontSize = 24;
            autoModLabel.color = Color.white;
            autoModLabel.alignment = TextAnchor.MiddleLeft;
            var autoModLabelRect = autoModLabelObj.GetComponent<RectTransform>();
            autoModLabelRect.anchorMin = new Vector2(0.085f, 0.92f);
            autoModLabelRect.anchorMax = new Vector2(0.30f, 0.97f);
            autoModLabelRect.offsetMin = new Vector2(5, 0);
            autoModLabelRect.offsetMax = new Vector2(-5, 0);

            var autoOffToggleObj = new GameObject("AutobanOffensiveNamesToggle");
            autoOffToggleObj.transform.SetParent(banUIPanel.transform, false);
            autobanOffensiveNamesToggle = autoOffToggleObj.AddComponent<Toggle>();
            var autoOffToggleImage = autoOffToggleObj.AddComponent<Image>();
            autoOffToggleImage.color = toggleOffColor;
            var autoOffToggleRect = autoOffToggleObj.GetComponent<RectTransform>();
            autoOffToggleRect.anchorMin = new Vector2(0.05f, 0.86f);
            autoOffToggleRect.anchorMax = new Vector2(0.08f, 0.91f);
            autoOffToggleRect.offsetMin = Vector2.zero;
            autoOffToggleRect.offsetMax = Vector2.zero;

            var autoOffLabelObj = new GameObject("AutobanOffensiveNamesLabel");
            autoOffLabelObj.transform.SetParent(banUIPanel.transform, false);
            var autoOffLabel = autoOffLabelObj.AddComponent<Text>();
            autoOffLabel.text = "Autoban Offensive Names";
            autoOffLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            autoOffLabel.fontSize = 24;
            autoOffLabel.color = Color.white;
            autoOffLabel.alignment = TextAnchor.MiddleLeft;
            var autoOffLabelRect = autoOffLabelObj.GetComponent<RectTransform>();
            autoOffLabelRect.anchorMin = new Vector2(0.085f, 0.86f);
            autoOffLabelRect.anchorMax = new Vector2(0.30f, 0.91f);
            autoOffLabelRect.offsetMin = new Vector2(5, 0);
            autoOffLabelRect.offsetMax = new Vector2(-5, 0);

            // Init toggles
            autobanModdedRanksToggle.isOn = autobanModdedRanksConfig.Value;
            autobanOffensiveNamesToggle.isOn = autobanOffensiveNamesConfig.Value;
            autoModToggleImage.color = autobanModdedRanksToggle.isOn ? toggleOnColor : toggleOffColor;
            autoOffToggleImage.color = autobanOffensiveNamesToggle.isOn ? toggleOnColor : toggleOffColor;
            autobanModdedRanksToggle.onValueChanged.AddListener(value =>
            {
                autobanModdedRanksConfig.Value = value;
                autoModToggleImage.color = value ? toggleOnColor : toggleOffColor;
                logger?.LogInfo($"Autoban modded ranks: {value}");
            });
            autobanOffensiveNamesToggle.onValueChanged.AddListener(value =>
            {
                autobanOffensiveNamesConfig.Value = value;
                autoOffToggleImage.color = value ? toggleOnColor : toggleOffColor;
                logger?.LogInfo($"Autoban offensive names: {value}");
            });

            var autoFormattedToggleObj = new GameObject("AutobanFormattedNamesToggle");
            autoFormattedToggleObj.transform.SetParent(banUIPanel.transform, false);
            var autoFormattedToggle = autoFormattedToggleObj.AddComponent<Toggle>();
            var autoFormattedToggleImage = autoFormattedToggleObj.AddComponent<Image>();
            autoFormattedToggleImage.color = toggleOffColor;
            var autoFormattedToggleRect = autoFormattedToggleObj.GetComponent<RectTransform>();

            autoFormattedToggleRect.anchorMin = new Vector2(0.245f, 0.92f);
            autoFormattedToggleRect.anchorMax = new Vector2(0.275f, 0.97f);
            autoFormattedToggleRect.offsetMin = Vector2.zero;
            autoFormattedToggleRect.offsetMax = Vector2.zero;

            var autoFormattedLabelObj = new GameObject("AutobanFormattedNamesLabel");
            autoFormattedLabelObj.transform.SetParent(banUIPanel.transform, false);
            var autoFormattedLabel = autoFormattedLabelObj.AddComponent<Text>();
            autoFormattedLabel.text = "Autoban Modded Names";
            autoFormattedLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            autoFormattedLabel.fontSize = 24;
            autoFormattedLabel.color = Color.white;
            autoFormattedLabel.alignment = TextAnchor.MiddleLeft;
            var autoFormattedLabelRect = autoFormattedLabelObj.GetComponent<RectTransform>();
            autoFormattedLabelRect.anchorMin = new Vector2(0.28f, 0.92f);
            autoFormattedLabelRect.anchorMax = new Vector2(0.52f, 0.97f);
            autoFormattedLabelRect.offsetMin = new Vector2(5, 0);
            autoFormattedLabelRect.offsetMax = new Vector2(-5, 0);

            // Init
            autoFormattedToggle.isOn = autobanFormattedNamesConfig.Value;
            autoFormattedToggleImage.color = autoFormattedToggle.isOn ? toggleOnColor : toggleOffColor;
            autoFormattedToggle.onValueChanged.AddListener(value =>
            {
                autobanFormattedNamesConfig.Value = value;
                autoFormattedToggleImage.color = value ? toggleOnColor : toggleOffColor;
                logger?.LogInfo($"Autoban formatted names: {value}");
                if (value)
                {
                    BanPlayersWithFormattedNames();
                }
            });

            // Active tab content
            activePlayersTab = new GameObject("ActivePlayersTab");
            var activeTabRect = activePlayersTab.AddComponent<RectTransform>();
            activePlayersTab.transform.SetParent(banUIPanel.transform, false);
            activeTabRect.anchorMin = new Vector2(0.05f, 0.1f);
            activeTabRect.anchorMax = new Vector2(0.95f, 0.7f);
            activeTabRect.offsetMin = Vector2.zero;
            activeTabRect.offsetMax = Vector2.zero;
            activePlayersTab.SetActive(true);
            var activeContentObj = new GameObject("ActiveContent");
            var activeContentRect = activeContentObj.AddComponent<RectTransform>();
            activeContentObj.transform.SetParent(activePlayersTab.transform, false);
            playerListContent = activeContentObj.transform;
            activeContentRect.anchorMin = Vector2.zero;
            activeContentRect.anchorMax = Vector2.one;
            activeContentRect.offsetMin = Vector2.zero;
            activeContentRect.offsetMax = Vector2.zero;
            var activeScrollRect = activePlayersTab.AddComponent<ScrollRect>();
            activeScrollRect.content = activeContentRect;
            activeScrollRect.horizontal = false;
            activeScrollRect.vertical = true;
            activeScrollRect.scrollSensitivity = 10f;
            activePlayersTab.AddComponent<Mask>();
            var activeMaskImage = activePlayersTab.AddComponent<Image>();
            activeMaskImage.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);

            // Banned tab content
            bannedPlayersTab = new GameObject("BannedPlayersTab");
            var bannedTabRect = bannedPlayersTab.AddComponent<RectTransform>();
            bannedPlayersTab.transform.SetParent(banUIPanel.transform, false);
            bannedPlayersTab.SetActive(false);
            bannedTabRect.anchorMin = new Vector2(0.05f, 0.1f);
            bannedTabRect.anchorMax = new Vector2(0.95f, 0.7f);
            bannedTabRect.offsetMin = Vector2.zero;
            bannedTabRect.offsetMax = Vector2.zero;
            var bannedContentObj = new GameObject("BannedContent");
            var bannedContentRect = bannedContentObj.AddComponent<RectTransform>();
            bannedContentObj.transform.SetParent(bannedPlayersTab.transform, false);
            bannedPlayersContent = bannedContentObj.transform;
            bannedContentRect.anchorMin = Vector2.zero;
            bannedContentRect.anchorMax = Vector2.one;
            bannedContentRect.offsetMin = Vector2.zero;
            bannedContentRect.offsetMax = Vector2.zero;
            var bannedScrollRect = bannedPlayersTab.AddComponent<ScrollRect>();
            bannedScrollRect.content = bannedContentRect;
            bannedScrollRect.horizontal = false;
            bannedScrollRect.vertical = true;
            bannedScrollRect.scrollSensitivity = 10f;
            bannedPlayersTab.AddComponent<Mask>();
            var bannedMaskImage = bannedPlayersTab.AddComponent<Image>();
            bannedMaskImage.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);

            // Tab events
            activeTabButton.onClick.AddListener(() =>
            {
                activePlayersTab.SetActive(true);
                bannedPlayersTab.SetActive(false);
                activeTabButtonImage.color = new Color(0.4f, 0.4f, 0.4f, 0.9f);
                bannedTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);
                RefreshActivePlayers();
                var scroll = activePlayersTab != null ? activePlayersTab.GetComponent<ScrollRect>() : null;
                if (scroll != null) scroll.verticalNormalizedPosition = 1f;
                RecentPlayersManager.OnTabChanged();
                OnTabChanged();
            });
            bannedTabButton.onClick.AddListener(() =>
            {
                activePlayersTab.SetActive(false);
                bannedPlayersTab.SetActive(true);
                activeTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);
                bannedTabButtonImage.color = new Color(0.4f, 0.4f, 0.4f, 0.9f);
                isFirstTimeOpeningBannedTab = true;
                RefreshBannedPlayers();
                var bannedScroll = bannedPlayersTab != null ? bannedPlayersTab.GetComponent<ScrollRect>() : null;
                if (bannedScroll != null) bannedScroll.verticalNormalizedPosition = 1f;
                RecentPlayersManager.OnTabChanged();
                OnTabChanged();
            });

            // Initial tab state
            activeTabButtonImage.color = new Color(0.4f, 0.4f, 0.4f, 0.9f);
            bannedTabButtonImage.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);

            logger?.LogInfo("Ban UI created");
            // Ensure active players scroll starts at top
            var initialScroll = activePlayersTab?.GetComponent<ScrollRect>();
            if (initialScroll != null) initialScroll.verticalNormalizedPosition = 1f;
            
            // Create Logging button now that BanModUI exists
            CreateLoggingButton(canvas);
            

        }

        public static void SetActive(bool visible)
        {
            if (banUI == null) return;
            banUI.SetActive(visible);
            RecentPlayersManager.SetButtonVisible(visible);
            SetLoggingButtonVisible(visible);
        }

        // Logging button and panel methods
        private static void CreateLoggingButton(Canvas canvas)
        {
            // Check if button already exists
            if (loggingButton != null)
            {
                logger?.LogInfo("Logging button already exists, skipping creation");
                return;
            }

            // Create Logging Button as child of BanModUI
            loggingButton = new GameObject("LoggingButton");
            
            // Find BanModUI as a child of BanModCanvas (same approach as RecentPlayersManager)
            GameObject banModUI = null;
            var banModCanvas = GameObject.Find("BanModCanvas");
            if (banModCanvas != null)
            {
                Transform banModUITransform = banModCanvas.transform.Find("BanModUI");
                if (banModUITransform != null)
                {
                    banModUI = banModUITransform.gameObject;
                }
            }
            
            if (banModUI != null)
            {
                loggingButton.transform.SetParent(banModUI.transform, false);
                logger?.LogInfo("Logging button created as child of BanModUI");
            }
            else
            {
                // Fallback to canvas if BanModUI not found yet
                loggingButton.transform.SetParent(canvas.transform, false);
                logger?.LogWarning("BanModUI not found, logging button created as child of canvas (fallback)");
            }

            var buttonComponent = loggingButton.AddComponent<Button>();
            var buttonImage = loggingButton.AddComponent<Image>();
            buttonImage.color = new Color(0.8f, 0.4f, 0.2f, 0.9f); // Orange color

            var buttonRect = loggingButton.GetComponent<RectTransform>();
            // Position in bottom right corner
            buttonRect.anchorMin = new Vector2(1, 0);
            buttonRect.anchorMax = new Vector2(1, 0);
            buttonRect.pivot = new Vector2(1, 0);
            buttonRect.anchoredPosition = new Vector2(-24, 24); // offset from bottom-right
            buttonRect.sizeDelta = new Vector2(200, 52);

            // Set high sorting order to render on top
            loggingButton.transform.SetAsLastSibling();

            // Button Text
            var textObj = new GameObject("ButtonText");
            textObj.transform.SetParent(loggingButton.transform, false);
            var buttonText = textObj.AddComponent<Text>();
            buttonText.text = "Logging";
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
            buttonComponent.onClick.AddListener(ToggleLoggingPanel);

            // Start hidden - only show when Ban UI is active
            loggingButton.SetActive(false);

            logger?.LogInfo("Logging button created (initially hidden)");
        }

        public static void SetLoggingButtonVisible(bool visible)
        {
            if (loggingButton == null)
            {
                if (visible)
                {
                    // Create the button if it doesn't exist and we want to show it
                    var canvas = GameObject.Find("BanModCanvas")?.GetComponent<Canvas>();
                    if (canvas != null)
                    {
                        CreateLoggingButton(canvas);
                    }
                }
                return;
            }

            loggingButton.SetActive(visible);

            // If hiding the button or UI, also hide the panel
            if (!visible && isLoggingVisible)
            {
                HideLoggingPanel();
            }
        }

        private static void ToggleLoggingPanel()
        {
            if (isLoggingVisible)
            {
                HideLoggingPanel();
            }
            else
            {
                ShowLoggingPanel();
            }
        }

        private static System.Collections.IEnumerator DelayedUIUpdate()
        {
            yield return null; // Wait one frame
            logger?.LogInfo("Running delayed UI update");
            UpdateLogDisplay();
        }

        public static void TestLoggingSystem()
        {
            logger?.LogInfo("TestLoggingSystem called");
            
            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] === UI LOGGING TEST ===");
            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Test message 1: Basic message");
            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Test message 2: Player1 killed Player2 with Sword");
            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Test message 3: Wizard casted Fireball at level 5");
            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Test message 4: WARNING: Fast respawn detected!");
            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] === END TEST ===");
            
            logger?.LogInfo($"Added test messages, total count: {logMessages.Count}");
            
            // Force refresh if panel is visible
            if (isLoggingVisible)
            {
                ForceRefreshLogDisplay();
            }
        }

        private static void ShowLoggingPanel()
        {
            if (loggingPanel == null)
            {
                logger?.LogInfo("ShowLoggingPanel called - panel exists: False, messages count: " + logMessages.Count);
                CreateLoggingPanel();
            }
            else if (!loggingPanel.activeSelf)
            {
                // Panel exists but is inactive, just reactivate it
                logger?.LogInfo("ShowLoggingPanel called - reactivating existing panel");
                loggingPanel.SetActive(true);
            }

            if (loggingPanel != null)
            {
                isLoggingVisible = true;
                
                // Load previous logs from log.jsonl file if this is the first time opening the panel
                // AND we're not currently in an active game session
                if (logMessages.Count == 0)
                {
                    try
                    {
                        // Check if we're in an active game session
                        var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                        bool isInActiveGame = mainMenuManager != null && mainMenuManager.GameHasStarted;
                        
                        if (!isInActiveGame)
                        {
                            logger?.LogInfo("Loading previous logs from log.jsonl...");
                            KillLogger.Instance.LoadPreviousLogs();
                            logger?.LogInfo($"Loaded previous logs, total messages: {logMessages.Count}");
                        }
                        else
                        {
                            logger?.LogInfo("Game is active - skipping previous log loading");
                        }
                        
                        // If no logs were loaded, add the startup message
                        if (logMessages.Count == 0)
                        {
                            AddLogMessage($"[{DateTime.Now:HH:mm:ss}] === Logging panel created ===");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError($"Error loading previous logs: {ex.Message}");
                        // Add startup message as fallback
                        AddLogMessage($"[{DateTime.Now:HH:mm:ss}] === Logging panel created ===");
                    }
                }
                else
                {
                    logger?.LogInfo($"Logging panel opened with {logMessages.Count} previously loaded messages");
                }
                
                // Update the display immediately - no need for delay with the new structure
                UpdateLogDisplay();
                
                // Scroll to bottom when opening the UI (but not when adding new messages)
                var scrollRect = logContent.GetComponentInParent<ScrollRect>();
                if (scrollRect != null)
                {
                    scrollRect.verticalNormalizedPosition = 0f; // 0 = bottom, 1 = top
                    logger?.LogInfo("Successfully scrolled to bottom on UI open");
                }
                
                logger?.LogInfo($"Logging panel shown with {logMessages.Count} messages");
            }
        }

        private static void AddDebugTestMessage()
        {
            string testMessage = $"[{DateTime.Now:HH:mm:ss}] DEBUG: Logging panel opened - this is a test message";
            
            // Add directly to the list (bypassing the normal AddLogMessage to avoid recursion)
            logMessages.Add(testMessage);
            logger?.LogInfo($"Added debug test message: {testMessage}");
            
            // Keep only the last MAX_LOG_MESSAGES
            if (logMessages.Count > MAX_LOG_MESSAGES)
            {
                logMessages.RemoveAt(0);
            }
        }

        public static bool IsLoggingSystemHealthy()
        {
            bool healthy = loggingButton != null && 
                        (loggingPanel == null || (loggingPanel != null && logContent != null));
            
            logger?.LogInfo($"Logging system health check: {healthy} (button: {loggingButton != null}, panel: {loggingPanel != null}, content: {logContent != null})");
            
            return healthy;
        }

        public static void ForceRefreshLogDisplay()
        {
            logger?.LogInfo($"ForceRefreshLogDisplay called - isLoggingVisible: {isLoggingVisible}, logContent exists: {logContent != null}");
            
            if (isLoggingVisible && logContent != null)
            {
                UpdateLogDisplay();
                logger?.LogInfo($"Forced refresh of log display with {logMessages.Count} messages");
            }
            else
            {
                logger?.LogWarning($"Cannot force refresh - panel visible: {isLoggingVisible}, content exists: {logContent != null}");
            }
        }

        private static void HideLoggingPanel()
        {
            if (loggingPanel != null)
            {
                loggingPanel.SetActive(false);
            }
            isLoggingVisible = false;
            logger?.LogInfo("Logging panel hidden");
        }

        public static void AddLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                logger?.LogWarning("Attempted to add null or empty log message");
                return;
            }

            try
            {
                // Log before adding
                logger?.LogInfo($"Attempting to add message: {message}");

                // Add to our list
                logMessages.Add(message);
                logger?.LogInfo($"Added message to list (total: {logMessages.Count}): {message}");

                // Keep only the last MAX_LOG_MESSAGES
                if (logMessages.Count > MAX_LOG_MESSAGES)
                {
                    logMessages.RemoveAt(0);
                }

                // Update UI display if the panel is currently visible
                if (isLoggingVisible)
                {
                    logger?.LogInfo("Updating log display");
                    UpdateLogDisplay();
                }

                // Also log to BepInEx console
                logger?.LogInfo($"UI Log: {message}");
            }
            catch (Exception e)
            {
                logger?.LogError($"Error adding log message: {e.Message}\nStack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Clears all log messages from the UI and updates the display
        /// </summary>
        public static void ClearLogMessages()
        {
            try
            {
                logger?.LogInfo("Clearing all log messages");
                logMessages.Clear();
                
                // Update UI display if the panel is currently visible
                if (isLoggingVisible)
                {
                    UpdateLogDisplay();
                }
            }
            catch (Exception e)
            {
                logger?.LogError($"Error clearing log messages: {e.Message}\nStack trace: {e.StackTrace}");
            }
        }

        public static void LoadPreviousGameLogs()
        {
            try
            {
                var killLogger = KillLogger.Instance;
                if (killLogger == null)
                {
                    AddLogMessage("Kill logger not available");
                    return;
                }

                var previousKills = killLogger.GetPreviousGameKills();
                if (previousKills.Count > 0)
                {
                    AddLogMessage($"Loading {previousKills.Count} kills from previous game...");
                    
                    foreach (var kill in previousKills)
                    {
                        string logMessage = kill.KillerName == "Environment" 
                            ? $"{kill.VictimName} died from {kill.CauseOfDeath}"
                            : $"{kill.KillerName} killed {kill.VictimName} with {kill.CauseOfDeath}";
                        
                        AddLogMessage($"[PREV] {logMessage}");
                    }
                    
                    AddLogMessage($"Finished loading previous game logs");
                }
                else
                {
                    AddLogMessage("No previous game logs available");
                }
            }
            catch (Exception e)
            {
                logger?.LogError($"Error loading previous game logs: {e.Message}");
                AddLogMessage($"Error loading previous game logs: {e.Message}");
            }
        }





        private static void UpdateLogDisplay()
        {
            if (logContent == null) 
            {
                logger?.LogError("UpdateLogDisplay called but logContent is null");
                return;
            }

            try
            {
                logger?.LogInfo($"UpdateLogDisplay starting with {logMessages.Count} messages");

                // Clear existing children - using the same approach as RecentPlayersManager
                foreach (Transform child in logContent)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }

                if (logMessages.Count == 0)
                {
                    // Create a "no messages" placeholder - using the same structure as RecentPlayersManager
                    var noMsgObj = new GameObject("NoMessages");
                    noMsgObj.transform.SetParent(logContent, false);
                    var noMsgText = noMsgObj.AddComponent<Text>();
                    noMsgText.text = "No log messages yet...";
                    noMsgText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    noMsgText.fontSize = 14;
                    noMsgText.color = Color.gray;
                    noMsgText.alignment = TextAnchor.MiddleCenter;

                    var noMsgRect = noMsgObj.GetComponent<RectTransform>();
                    if (noMsgRect != null)
                    {
                        // Use the exact same positioning as RecentPlayersManager
                        noMsgRect.anchorMin = new Vector2(0, 1);
                        noMsgRect.anchorMax = new Vector2(1, 1);
                        noMsgRect.pivot = new Vector2(0.5f, 1);
                        noMsgRect.offsetMin = new Vector2(10, -35);
                        noMsgRect.offsetMax = new Vector2(-10, -5);
                    }
                    
                    var contentRect = logContent.GetComponent<RectTransform>();
                    if (contentRect != null)
                    {
                        contentRect.sizeDelta = new Vector2(0, 40);
                    }
                    return;
                }

                // Create log message entries - using the exact same structure as RecentPlayersManager.CreatePlayerEntry
                for (int i = 0; i < logMessages.Count; i++)
                {
                    var message = logMessages[i];
                    
                    // Create entry container - same as RecentPlayersManager
                    var entryObj = new GameObject($"LogMessage_{i}");
                    entryObj.transform.SetParent(logContent, false);
                    
                    var entryImage = entryObj.AddComponent<Image>();
                    entryImage.color = new Color(0.2f, 0.2f, 0.2f, 0.7f);
                    
                    var entryRect = entryObj.GetComponent<RectTransform>();
                    if (entryRect != null)
                    {
                        // Use the exact same positioning as RecentPlayersManager
                        entryRect.anchorMin = new Vector2(0, 1);
                        entryRect.anchorMax = new Vector2(1, 1);
                        entryRect.pivot = new Vector2(0.5f, 1);
                        entryRect.offsetMin = new Vector2(5, -30 - (i * 35));
                        entryRect.offsetMax = new Vector2(-5, -5 - (i * 35));
                    }

                    // Create message text - same structure as RecentPlayersManager
                    var messageTextObj = new GameObject("MessageText");
                    messageTextObj.transform.SetParent(entryObj.transform, false);
                    var messageText = messageTextObj.AddComponent<Text>();
                    messageText.text = message;
                    messageText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    messageText.fontSize = 12;
                    
                    // Set text color based on message content - warnings in yellow
                    if (message.Contains("WARNING:"))
                    {
                        messageText.color = Color.yellow;
                    }
                    else
                    {
                        messageText.color = Color.white;
                    }
                    
                    messageText.alignment = TextAnchor.MiddleLeft;
                    
                    var messageTextRect = messageTextObj.GetComponent<RectTransform>();
                    if (messageTextRect != null)
                    {
                        // Use the same text positioning as RecentPlayersManager
                        messageTextRect.anchorMin = new Vector2(0, 0);
                        messageTextRect.anchorMax = new Vector2(1, 1);
                        messageTextRect.offsetMin = new Vector2(8, 2);
                        messageTextRect.offsetMax = new Vector2(-5, -2);
                    }
                }

                // Update content size - using the same approach as RecentPlayersManager
                var contentRect2 = logContent.GetComponent<RectTransform>();
                if (contentRect2 != null)
                {
                    contentRect2.sizeDelta = new Vector2(0, logMessages.Count * 35 + 5);
                    logger?.LogInfo($"Set content size to height: {logMessages.Count * 35 + 5}");
                }

                    // Don't scroll automatically when updating display - only scroll when opening UI

                logger?.LogInfo($"UpdateLogDisplay completed - displayed {logMessages.Count} messages");
            }
            catch (Exception e)
            {
                logger?.LogError($"Error updating log display: {e.Message}\nStack trace: {e.StackTrace}");
            }
        }

        private static void CreateLoggingPanel()
        {
            logger?.LogInfo("CreateLoggingPanel() called");

            try
            {
                // Find the canvas
                var canvas = GameObject.Find("BanModCanvas")?.GetComponent<Canvas>();
                if (canvas == null)
                {
                    logger?.LogError("No canvas found for Logging panel");
                    return;
                }

                logger?.LogInfo($"Creating panel on canvas: {canvas.name}");

                // Create panel container
                loggingPanel = new GameObject("LoggingPanel");
                if (loggingPanel == null)
                {
                    logger?.LogError("Failed to create LoggingPanel GameObject");
                    return;
                }

                // Set parent first
                loggingPanel.transform.SetParent(canvas.transform, false);

                // Add Image component
                var panelImage = loggingPanel.AddComponent<Image>();
                if (panelImage == null)
                {
                    logger?.LogError("Failed to add Image component to LoggingPanel");
                    UnityEngine.Object.Destroy(loggingPanel);
                    loggingPanel = null;
                    return;
                }
                panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);

                // Get RectTransform (should exist since we added Image)
                var panelRect = loggingPanel.GetComponent<RectTransform>();
                if (panelRect == null)
                {
                    logger?.LogError("Failed to get RectTransform from LoggingPanel");
                    UnityEngine.Object.Destroy(loggingPanel);
                    loggingPanel = null;
                    return;
                }

                // Position in center-right area
                panelRect.anchorMin = new Vector2(0.5f, 0.2f);
                panelRect.anchorMax = new Vector2(0.9f, 0.8f);
                panelRect.offsetMin = Vector2.zero;
                panelRect.offsetMax = Vector2.zero;

                // Add border - wrap in try-catch as this is non-critical
                try
                {
                    var outline = loggingPanel.AddComponent<Outline>();
                    if (outline != null)
                    {
                        outline.effectColor = Color.white;
                        outline.effectDistance = new Vector2(2, 2);
                    }
                }
                catch (System.Exception e)
                {
                    logger?.LogWarning($"Failed to add Outline component: {e.Message}");
                }

                // Set very high sorting order to ensure it renders on top of everything
                loggingPanel.transform.SetAsLastSibling();
                
                // Add Canvas component for sorting - wrap in try-catch as this might fail
                try
                {
                    var panelCanvas = loggingPanel.AddComponent<Canvas>();
                    if (panelCanvas != null)
                    {
                        panelCanvas.overrideSorting = true;
                        panelCanvas.sortingOrder = 2000; // Higher than the main UI canvas
                    }

                    // Add GraphicRaycaster so buttons work
                    var graphicRaycaster = loggingPanel.AddComponent<GraphicRaycaster>();
                    if (graphicRaycaster == null)
                    {
                        logger?.LogWarning("Failed to add GraphicRaycaster to LoggingPanel");
                    }
                }
                catch (System.Exception e)
                {
                    logger?.LogWarning($"Failed to add Canvas/GraphicRaycaster components: {e.Message}");
                }



                // Create scroll area for log messages - using the EXACT same structure as RecentPlayersManager
                try
                {
                    var scrollObj = new GameObject("ScrollArea");
                    if (scrollObj != null)
                    {
                        scrollObj.transform.SetParent(loggingPanel.transform, false);
                        var scrollRect = scrollObj.AddComponent<ScrollRect>();
                        var scrollImage = scrollObj.AddComponent<Image>();
                        
                        if (scrollImage != null)
                        {
                            scrollImage.color = new Color(0.05f, 0.05f, 0.05f, 0.8f);
                        }

                        scrollObj.AddComponent<Mask>();

                        var scrollRectTransform = scrollObj.GetComponent<RectTransform>();
                        if (scrollRectTransform != null)
                        {
                            scrollRectTransform.anchorMin = new Vector2(0, 0);
                            scrollRectTransform.anchorMax = new Vector2(1, 1);
                            scrollRectTransform.offsetMin = new Vector2(5, 5);
                            scrollRectTransform.offsetMax = new Vector2(-5, -5);
                        }

                        // Create Content container - using the EXACT same structure as RecentPlayersManager
                        var logContentObj = new GameObject("Content");
                        if (logContentObj != null)
                        {
                            logContentObj.transform.SetParent(scrollObj.transform, false);
                            
                            // Add RectTransform component explicitly like RecentPlayersManager does
                            var logContentRectTransform = logContentObj.AddComponent<RectTransform>();
                            if (logContentRectTransform != null)
                            {
                                // Use the EXACT same anchoring as RecentPlayersManager
                                logContentRectTransform.anchorMin = new Vector2(0, 1);
                                logContentRectTransform.anchorMax = new Vector2(1, 1);
                                logContentRectTransform.pivot = new Vector2(0.5f, 1);
                            }

                            // Store reference to log content for adding messages
                            logContent = logContentObj.transform;

                            if (logContent != null)
                            {
                                logger?.LogInfo($"LogContent created: {logContent.name}");
                                logger?.LogInfo($"LogContent parent: {logContent.parent?.name ?? "null"}");
                            }
                            else
                            {
                                logger?.LogError("LogContent is null after creation!");
                            }

                            // Setup ScrollRect - using the EXACT same approach as RecentPlayersManager
                            if (scrollRect != null && logContentRectTransform != null)
                            {
                                scrollRect.content = logContentRectTransform;
                                scrollRect.horizontal = false;
                                scrollRect.vertical = true;
                                scrollRect.scrollSensitivity = 10f;

                                logger?.LogInfo("ScrollRect and content setup completed");
                            }
                        }
                    }
                }
                catch (System.Exception e)
                {
                    logger?.LogError($"Failed to create content area: {e.Message}");
                }



                // Add click-away functionality - wrap in try-catch as this is non-critical
                try
                {
                    AddLoggingClickAwayHandler(canvas);
                }
                catch (System.Exception e)
                {
                    logger?.LogWarning($"Failed to add click-away handler: {e.Message}");
                }

                logger?.LogInfo("CreateLoggingPanel() completed successfully");
            }
            catch (System.Exception e)
            {
                logger?.LogError($"Critical error in CreateLoggingPanel(): {e.Message}\nStack trace: {e.StackTrace}");
                
                // Cleanup on failure
                if (loggingPanel != null)
                {
                    UnityEngine.Object.Destroy(loggingPanel);
                    loggingPanel = null;
                }
            }
        }

        private static void AddLoggingClickAwayHandler(Canvas canvas)
        {
            // Create invisible overlay to detect clicks outside the panel
            var overlayObj = new GameObject("LoggingClickAwayOverlay");
            overlayObj.transform.SetParent(canvas.transform, false);
            overlayObj.transform.SetSiblingIndex(Mathf.Max(0, loggingPanel.transform.GetSiblingIndex() - 1));

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
                HideLoggingPanel();
                UnityEngine.Object.Destroy(overlayObj);
            });
        }

        public static void OnTabChanged()
        {
            if (isLoggingVisible)
            {
                HideLoggingPanel();
                logger?.LogInfo("Logging panel hidden due to tab change");
            }
        }

        private static bool IsFormattedName(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return false;
            return richTextTagPattern.IsMatch(playerName);
        }

        private static void BanPlayersWithFormattedNames()
        {
            try
            {
                var connected = getConnectedPlayers();
                if (connected == null || connected.Count == 0) return;

                int bannedCount = 0;
                foreach (var kvp in connected)
                {
                    string name = kvp.Key;
                    string steamId = kvp.Value;
                    if (IsFormattedName(name))
                    {
                        if (banPlayerWithReason != null)
                        {
                            banPlayerWithReason(steamId, name, "Modded Name");
                        }
                        else
                        {
                            toggleBanPlayer(steamId, name);
                        }
                        bannedCount++;
                    }
                }
                logger?.LogInfo($"Banned {bannedCount} player(s) with modded names.");
                RefreshActivePlayers();
                RefreshBannedPlayers();
            }
            catch (System.Exception e)
            {
                logger?.LogError($"Error banning modded names: {e.Message}");
            }
        }

        public static void RefreshActivePlayers()
        {
            try
            {
                if (playerListContent == null)
                {
                    logger?.LogWarning("RefreshActivePlayers called but playerListContent is null - UI may need recreation");
                    HandleUIRecreationWarning();
                    return;
                }

                foreach (Transform child in playerListContent)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }

                var connected = getConnectedPlayers();
                if (connected.Count == 0)
                {
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
                    var contentRect = playerListContent.GetComponent<RectTransform>();
                    contentRect.sizeDelta = new Vector2(0, 50);
                    return;
                }

                int index = 0;
                var banned = getBannedPlayers();
                foreach (var kvp in connected)
                {
                    string playerName = kvp.Key;
                    string steamId = kvp.Value;
                    bool isBanned = banned.ContainsKey(steamId);
                    if (isBanned) continue;

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

                    var kickButtonObj = new GameObject("KickButton");
                    kickButtonObj.transform.SetParent(playerEntryObj.transform, false);
                    var kickButton = kickButtonObj.AddComponent<Button>();
                    kickButtonObj.AddComponent<Image>().color = new Color(0.8f, 0.6f, 0.2f);
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
                    kickButton.onClick.AddListener(() => kickPlayer(steamId, playerName));

                    var banButtonObj = new GameObject("BanButton");
                    banButtonObj.transform.SetParent(playerEntryObj.transform, false);
                    var banButton = banButtonObj.AddComponent<Button>();
                    banButtonObj.AddComponent<Image>().color = new Color(0.8f, 0.2f, 0.2f);
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
                    banButton.onClick.AddListener(() => toggleBanPlayer(steamId, playerName));

                    // Highlight entries with modded names
                    if (IsFormattedName(playerName))
                    {
                        playerEntryImage.color = new Color(0.5f, 0.2f, 0.6f, 0.9f);
                    }

                    index++;
                }

                var contentRect2 = playerListContent.GetComponent<RectTransform>();
                var scrollRect = activePlayersTab.GetComponent<ScrollRect>();
                bool needsScrolling = index > 11;
                if (needsScrolling)
                {
                    contentRect2.sizeDelta = new Vector2(0, index * 60 + 10);
                }
                else
                {
                    float totalHeight = index * 60 + 10;
                    contentRect2.sizeDelta = new Vector2(0, totalHeight);
                }
                // Always normalize to top after repopulating
                if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
            }
            catch (Exception e)
            {
                logger?.LogError($"Error refreshing player list UI: {e.Message}");
            }
        }

        public static void RefreshBannedPlayers()
        {
            try
            {
                if (bannedPlayersContent == null)
                {
                    logger?.LogWarning("RefreshBannedPlayers called but bannedPlayersContent is null - UI may need recreation");
                    HandleUIRecreationWarning();
                    return;
                }

                // Clear existing entries but keep the search/nav controls
                foreach (Transform child in bannedPlayersContent)
                {
                    // Only destroy player entries, not the header/nav/search
                    if (child.name.StartsWith("BannedEntry_"))
                    {
                        UnityEngine.Object.Destroy(child.gameObject);
                    }
                }

                var banned = getBannedPlayers();
                var timestamps = getBanTimestamps();
                var reasons = getBanReasons();
                
                // Check if controls need to be built (first time or after tab switch)
                bool needsToBuildControls = bannedPlayersContent.Find("PaginationHeader") == null;
                
                if (needsToBuildControls)
                {
                    // Build header, navigation, and search controls once
                    BuildBannedPlayerControls();
                }
                
                // Always repopulate the list
                RepopulateBannedList(banned, timestamps, reasons);
                
                // Always put to top after refresh
                var scroll = bannedPlayersTab.GetComponent<ScrollRect>();
                if (scroll != null) scroll.verticalNormalizedPosition = 1f;
                isFirstTimeOpeningBannedTab = false;
            }
            catch (Exception e)
            {
                logger?.LogError($"Error refreshing banned players UI: {e.Message}");
            }
        }

        private static void BuildBannedPlayerControls()
        {
            // Clear everything first
            foreach (Transform child in bannedPlayersContent)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }
            
            // Build header
            CreatePaginationHeaderImproved(1, 1, 0); // Will be updated in RepopulateBannedList
            
            // Build navigation with search input
            CreateNavigationControlsWithSearch();
        }

        private static void CreateNavigationControlsWithSearch()
        {
            var navigationObj = new GameObject("NavigationButtons");
            navigationObj.transform.SetParent(bannedPlayersContent, false);
            var navigationRect = navigationObj.AddComponent<RectTransform>();
            navigationRect.anchorMin = new Vector2(0, 1);
            navigationRect.anchorMax = new Vector2(1, 1);
            navigationRect.pivot = new Vector2(0.5f, 1);
            navigationRect.offsetMin = new Vector2(5, -80);
            navigationRect.offsetMax = new Vector2(-5, -40);

            // Create search input (this will persist across list updates)
            var searchObj = new GameObject("BannedSearchInput");
            searchObj.transform.SetParent(navigationObj.transform, false);
            var searchImage = searchObj.AddComponent<Image>();
            searchImage.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            var searchRect = searchObj.GetComponent<RectTransform>();
            searchRect.anchorMin = new Vector2(0.37f, 0);
            searchRect.anchorMax = new Vector2(0.63f, 1);
            searchRect.offsetMin = new Vector2(5, 5);
            searchRect.offsetMax = new Vector2(-5, -5);

            bannedSearchInput = searchObj.AddComponent<InputField>();
            bannedSearchInput.text = bannedSearchQuery;
            bannedSearchInput.lineType = InputField.LineType.SingleLine;
            bannedSearchInput.characterLimit = 64;

            var placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(searchObj.transform, false);
            var placeholderText = placeholderObj.AddComponent<Text>();
            placeholderText.text = "Search name or SteamID";
            placeholderText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            placeholderText.fontSize = 12;
            placeholderText.color = new Color(1, 1, 1, 0.35f);
            placeholderText.alignment = TextAnchor.MiddleLeft;
            var placeholderRect = placeholderObj.GetComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = new Vector2(8, 0);
            placeholderRect.offsetMax = new Vector2(-8, 0);

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(searchObj.transform, false);
            var inputText = textObj.AddComponent<Text>();
            inputText.text = bannedSearchQuery;
            inputText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            inputText.fontSize = 14;
            inputText.color = Color.white;
            inputText.alignment = TextAnchor.MiddleLeft;
            var inputTextRect = textObj.GetComponent<RectTransform>();
            inputTextRect.anchorMin = Vector2.zero;
            inputTextRect.anchorMax = Vector2.one;
            inputTextRect.offsetMin = new Vector2(8, 0);
            inputTextRect.offsetMax = new Vector2(-8, 0);

            bannedSearchInput.placeholder = placeholderText;
            bannedSearchInput.textComponent = inputText;

            bannedSearchInput.onValueChanged.RemoveAllListeners();
            bannedSearchInput.onValueChanged.AddListener((value) =>
            {
                bannedSearchQuery = value ?? string.Empty;
                bannedPlayersPageIndex = 0;
                
                // Only repopulate the list, keep controls intact
                var banned = getBannedPlayers();
                var timestamps = getBanTimestamps();
                var reasons = getBanReasons();
                RepopulateBannedList(banned, timestamps, reasons);
                
                var scroll = bannedPlayersTab?.GetComponent<ScrollRect>();
                if (scroll != null)
                {
                    scroll.verticalNormalizedPosition = 1f;
                }
            });
        }

        private static void RepopulateBannedList(
            Dictionary<string, string> banned, 
            Dictionary<string, DateTime> timestamps, 
            Dictionary<string, string> reasons)
        {
            // Remove only player entries, keep controls
            var toDestroy = new List<Transform>();
            foreach (Transform child in bannedPlayersContent)
            {
                if (child.name.StartsWith("BannedEntry_") || child.name == "NoBannedPlayers")
                {
                    toDestroy.Add(child);
                }
            }
            foreach (var child in toDestroy)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            var bannedList = banned.ToList();
            
            // Apply search filter
            if (!string.IsNullOrEmpty(bannedSearchQuery))
            {
                string q = bannedSearchQuery.Trim().ToLowerInvariant();
                bannedList = bannedList.Where(kvp =>
                    (!string.IsNullOrEmpty(kvp.Value) && kvp.Value.ToLowerInvariant().Contains(q)) ||
                    (!string.IsNullOrEmpty(kvp.Key) && kvp.Key.ToLowerInvariant().Contains(q))
                ).ToList();
            }

            // Sort by timestamp
            bannedList = bannedList.OrderBy(kvp =>
            {
                string steamId = kvp.Key;
                return timestamps.ContainsKey(steamId) ? timestamps[steamId] : DateTime.MaxValue;
            }).Reverse().ToList();

            // Calculate pagination
            int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)bannedList.Count / MAX_VISIBLE_BANNED_ITEMS));
            bannedPlayersPageIndex = Mathf.Clamp(bannedPlayersPageIndex, 0, Math.Max(0, totalPages - 1));
            int startIndex = bannedPlayersPageIndex * MAX_VISIBLE_BANNED_ITEMS;
            int endIndex = Mathf.Min(startIndex + MAX_VISIBLE_BANNED_ITEMS, bannedList.Count);

            // Update header text
            UpdatePaginationHeader(bannedPlayersPageIndex + 1, totalPages, bannedList.Count);
            
            // Update navigation buttons
            UpdateNavigationButtons(bannedPlayersPageIndex, totalPages);

            if (bannedList.Count == 0)
            {
                CreateNoBannedPlayersMessage();
                UpdateBannedContentHeight(3);
            }
            else
            {
                // Create only the visible player entries
                for (int i = startIndex; i < endIndex; i++)
                {
                    var kvp = bannedList[i];
                    CreateBannedPlayerEntry(kvp.Key, kvp.Value, i - startIndex, timestamps, reasons);
                }
                UpdateBannedContentHeight((endIndex - startIndex) + 3);
            }
        }

        private static void UpdateNavigationButtons(int pageIndex, int totalPages)
        {
            var navButtons = bannedPlayersContent.Find("NavigationButtons");
            if (navButtons != null)
            {
                // Remove old navigation buttons but keep search
                var toDestroy = new List<Transform>();
                foreach (Transform child in navButtons)
                {
                    if (child.name == "NavigationButton")
                    {
                        toDestroy.Add(child);
                    }
                }
                foreach (var child in toDestroy)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }

                // Add new navigation buttons
                if (pageIndex > 0)
                {
                    CreateNavigationButton(navButtons.gameObject, "< Previous", new Vector2(0, 0), new Vector2(0.35f, 1), () =>
                    {
                        bannedPlayersPageIndex--;
                        var banned = getBannedPlayers();
                        var timestamps = getBanTimestamps();
                        var reasons = getBanReasons();
                        RepopulateBannedList(banned, timestamps, reasons);
                        var scroll = bannedPlayersTab?.GetComponent<ScrollRect>();
                        if (scroll != null) scroll.verticalNormalizedPosition = 1f;
                    });
                }

                if (pageIndex < totalPages - 1)
                {
                    CreateNavigationButton(navButtons.gameObject, "Next >", new Vector2(0.65f, 0), new Vector2(1, 1), () =>
                    {
                        bannedPlayersPageIndex++;
                        var banned = getBannedPlayers();
                        var timestamps = getBanTimestamps();
                        var reasons = getBanReasons();
                        RepopulateBannedList(banned, timestamps, reasons);
                        var scroll = bannedPlayersTab?.GetComponent<ScrollRect>();
                        if (scroll != null) scroll.verticalNormalizedPosition = 1f;
                    });
                }
            }
        }

        private static void UpdatePaginationHeader(int currentPage, int totalPages, int totalCount)
        {
            var header = bannedPlayersContent.Find("PaginationHeader");
            if (header != null)
            {
                var headerText = header.GetComponent<Text>();
                if (headerText != null)
                {
                    headerText.text = $"Banned Players: {totalCount} total (Page {currentPage} of {totalPages})";
                }
            }
        }

        private static void CreateNoBannedPlayersMessage()
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
            
            noBannedRect.offsetMin = new Vector2(10, -150);
            noBannedRect.offsetMax = new Vector2(-10, -120);
            
            var contentRect = bannedPlayersContent.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(0, 160);
        }

        private static void CreatePaginationHeaderImproved(int currentPage, int totalPages, int totalCount)
        {
            var headerObj = new GameObject("PaginationHeader");
            headerObj.transform.SetParent(bannedPlayersContent, false);
            var headerText = headerObj.AddComponent<Text>();
            headerText.text = $"Banned Players: {totalCount} total (Page {currentPage} of {totalPages})";
            headerText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            headerText.fontSize = 18;
            headerText.color = Color.yellow;
            headerText.alignment = TextAnchor.MiddleCenter;
            headerText.fontStyle = FontStyle.Bold;
            var headerRect = headerObj.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.offsetMin = new Vector2(5, -35);
            headerRect.offsetMax = new Vector2(-5, -5);
        }

        private static void CreateNavigationButton(GameObject parent, string text, Vector2 anchorMin, Vector2 anchorMax, Action onClick)
        {
            var buttonObj = new GameObject("NavigationButton");
            buttonObj.transform.SetParent(parent.transform, false);
            var button = buttonObj.AddComponent<Button>();
            var buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.2f, 0.4f, 0.8f, 0.9f);
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

        private static void CreateBannedPlayerEntry(
            string steamId,
            string playerName,
            int visibleIndex,
            Dictionary<string, DateTime> banTimestamps,
            Dictionary<string, string> banReasons)
        {
            DateTime banTime = banTimestamps.ContainsKey(steamId) ? banTimestamps[steamId] : DateTime.Now;
            string banTimeString = banTime.ToString("MM/dd/yyyy HH:mm");
            var bannedEntryObj = new GameObject($"BannedEntry_{visibleIndex}");
            bannedEntryObj.transform.SetParent(bannedPlayersContent, false);
            bannedEntryObj.AddComponent<Image>().color = new Color(0.8f, 0.3f, 0.3f, 0.8f);
            var bannedEntryRect = bannedEntryObj.GetComponent<RectTransform>();
            bannedEntryRect.anchorMin = new Vector2(0, 1);
            bannedEntryRect.anchorMax = new Vector2(1, 1);
            bannedEntryRect.pivot = new Vector2(0.5f, 1);

            bannedEntryRect.offsetMin = new Vector2(5, -135 - (visibleIndex * 60));
            bannedEntryRect.offsetMax = new Vector2(-5, -90 - (visibleIndex * 60));

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
            unbanButtonObj.AddComponent<Image>().color = new Color(0.2f, 0.6f, 0.2f);
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
            unbanButton.onClick.AddListener(() => unbanPlayer(steamId, playerName));
        }

        private static void UpdateBannedContentHeight(int items)
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
                if (bannedScrollRect != null) bannedScrollRect.verticalNormalizedPosition = 1f;
            }
        }

        public static void CleanupExistingUI()
        {
            try
            {
                // Find and destroy any existing BanModCanvas objects
                var existingCanvases = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None)
                    .Where(obj => obj.name == "BanModCanvas")
                    .ToArray();

                if (existingCanvases.Length > 1)
                {
                    logger?.LogWarning($"Found {existingCanvases.Length} BanModCanvas objects, cleaning up duplicates");
                    
                    // Keep the first one, destroy the rest
                    for (int i = 1; i < existingCanvases.Length; i++)
                    {
                        if (existingCanvases[i] != null)
                        {
                            GameObject.DestroyImmediate(existingCanvases[i]);
                            logger?.LogInfo($"Destroyed duplicate BanModCanvas {i}");
                        }
                    }
                }

                // Reset our references
                banUI = null;
                banUIPanel = null;
                playerListContent = null;
                bannedPlayersContent = null;
                activePlayersTab = null;
                bannedPlayersTab = null;
                loggingButton = null;
                loggingPanel = null;
                logContent = null;
                
                // Reset RecentPlayersManager references
                RecentPlayersManager.CleanupExistingUI();
                
                logger?.LogInfo("Existing UI references cleared");
            }
            catch (Exception ex)
            {
                logger?.LogError($"Error cleaning up existing UI: {ex.Message}");
            }
        }

        public static IEnumerator ForceRecreateUI()
        {
            logger?.LogInfo("Force recreating Ban UI...");
            
            // Clean up existing UI completely
            CleanupExistingUI();
            
            // Wait a moment for cleanup to complete
            yield return new WaitForSeconds(0.1f);
            
            // Create new UI
            yield return CreateUI();
        }
    }
}