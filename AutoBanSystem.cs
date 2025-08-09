using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using System.Text.RegularExpressions;

namespace PlayerBanMod
{
    public interface IAutoBanSystem
    {
        void ProcessPlayer(string steamId, string playerName, Action<string, string> onAutoBan);
    }

    public class AutoBanSystem : IAutoBanSystem
    {
        private readonly ManualLogSource logger;
        private readonly Func<bool> isInLobbyScreen;
        private readonly Func<bool> isGameActive;
        private readonly ConfigEntry<bool> autobanModdedRanks;
        private readonly ConfigEntry<bool> autobanOffensiveNames;
        private readonly ConfigEntry<bool> autobanFormattedNames;
        private readonly ConfigEntry<string> offensiveNames;
        private readonly IBanDataManager banDataManager;
        private readonly IPlayerManager playerManager;
        private static readonly Regex richTextTagPattern = new Regex("<\\s*/?\\s*(color|b|i|size|material)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public AutoBanSystem(
            ManualLogSource logger,
            Func<bool> isInLobbyScreen,
            Func<bool> isGameActive,
            ConfigEntry<bool> autobanModdedRanks,
            ConfigEntry<bool> autobanOffensiveNames,
            ConfigEntry<bool> autobanFormattedNames,
            ConfigEntry<string> offensiveNames,
            IBanDataManager banDataManager,
            IPlayerManager playerManager)
        {
            this.logger = logger;
            this.isInLobbyScreen = isInLobbyScreen;
            this.isGameActive = isGameActive;
            this.autobanModdedRanks = autobanModdedRanks;
            this.autobanOffensiveNames = autobanOffensiveNames;
            this.autobanFormattedNames = autobanFormattedNames;
            this.offensiveNames = offensiveNames;
            this.banDataManager = banDataManager;
            this.playerManager = playerManager;
        }

        public void ProcessPlayer(string steamId, string playerName, Action<string, string> onAutoBan)
        {
            if (string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(playerName)) return;

            // skip host
            if (PlayerBanMod.Instance.IsPlayerHost(steamId)) return;
            if (banDataManager.IsBanned(steamId)) return; // already banned

            try
            {
                // Only skip when game is actively running; otherwise allow checks in all menu/lobby states
                bool inGame = isGameActive();
                bool inLobbyScreen = isInLobbyScreen();
                if (inGame) return;

                // Modded rank/title: only in lobby screen
                if (autobanModdedRanks.Value && HasModdedRankInternal(steamId, playerName))
                {
                    logger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has modded rank - auto-banning");
                    banDataManager.BanPlayer(steamId, playerName, "Modded Rank");
                    return;
                }

                // Offensive name: lobby screen only
                if (autobanOffensiveNames.Value && HasOffensiveNameInternal(playerName))
                {
                    logger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has offensive name - auto-banning");
                    banDataManager.BanPlayer(steamId, playerName, "Offensive Name");
                    return;
                }

                // Formatted name (rich text tags): lobby screen only
                if (autobanFormattedNames.Value && HasFormattedName(playerName))
                {
                    logger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has modded name - auto-banning");
                    // Ban directly with reason
                    banDataManager.BanPlayer(steamId, playerName, "Modded Name");
                    return;
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error processing auto-ban for {playerName}: {e.Message}");
            }
        }

        private bool HasModdedRankInternal(string steamId, string playerName)
        {
            try
            {
                // Allow rank checks in any non-game state; do not block by lobby screen check

                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null)
                {
                    logger.LogInfo("[AutoBan] MainMenuManager not found while checking modded rank");
                    return false;
                }
                if (mainMenuManager.kickplayershold == null)
                {
                    logger.LogInfo("[AutoBan] kickplayershold is null while checking modded rank");
                    return false;
                }

                if (!mainMenuManager.kickplayershold.nametosteamid.ContainsKey(playerName) ||
                    mainMenuManager.kickplayershold.nametosteamid[playerName] != steamId)
                {
                    if (mainMenuManager.kickplayershold.nametosteamid.ContainsKey(playerName))
                    {
                        string mapped = mainMenuManager.kickplayershold.nametosteamid[playerName];
                        logger.LogInfo($"[AutoBan] Name present but SteamID mismatch: lobby='{mapped}', target='{steamId}' for playerName='{playerName}'");
                    }
                    else
                    {
                        logger.LogInfo($"[AutoBan] Player name '{playerName}' not found in nametosteamid");
                    }
                    var trackedPlayers = playerManager.GetConnectedPlayers();
                    if (trackedPlayers.ContainsKey(playerName))
                    {
                        trackedPlayers.Remove(playerName);
                        logger.LogInfo($"Removed {playerName} from tracking - no longer in lobby");
                    }
                    return false;
                }

                if (mainMenuManager.rankandleveltext == null) return false;

                string playerRankText = "";
                bool foundPlayer = false;
                int maxSlots = Mathf.Min(mainMenuManager.rankandleveltext.Length, 8);
                logger.LogDebug($"[AutoBan] Scanning rank UI for '{playerName}' across {maxSlots} slots");
                for (int i = 0; i < maxSlots; i++)
                {
                    if (i * 2 < mainMenuManager.texts.Length)
                    {
                        string uiName = mainMenuManager.texts[i * 2].text;
                        string normUi = (uiName ?? string.Empty).Trim().ToLower().Replace(" ", "");
                        string normPlayer = (playerName ?? string.Empty).Trim().ToLower().Replace(" ", "");

                        // Match if equal or one is a prefix of the other (handles truncation or appended tokens)
                        bool nameMatches = normUi == normPlayer || normUi.StartsWith(normPlayer) || normPlayer.StartsWith(normUi);
                        logger.LogDebug($"[AutoBan] Slot {i}: uiName='{uiName}', normUi='{normUi}', normPlayer='{normPlayer}', match={nameMatches}");
                        if (nameMatches)
                        {
                            playerRankText = mainMenuManager.rankandleveltext[i].text;
                            foundPlayer = true;
                            logger.LogDebug($"[AutoBan] Matched at slot {i}, rankText='{playerRankText}'");
                            break;
                        }
                    }
                }

                if (!foundPlayer)
                {
                    // Silenced periodic info log to avoid spam in busy lobbies
                    return false;
                }

                playerRankText = playerRankText.Replace("_", " ").Trim();

                bool hasValidRank = playerRankText.Contains("Lackey") ||
                                     playerRankText.Contains("Sputterer") ||
                                     playerRankText.Contains("Novice") ||
                                     playerRankText.Contains("Apprentice") ||
                                     playerRankText.Contains("Savant") ||
                                     playerRankText.Contains("Master") ||
                                     playerRankText.Contains("Grand Master") ||
                                     playerRankText.Contains("Archmagus") ||
                                     playerRankText.Contains("Magus Prime") ||
                                     playerRankText.Contains("Supreme Archmagus");
                logger.LogDebug($"[AutoBan] Cleaned rankText='{playerRankText}', hasValidRank={hasValidRank}");

                if (!hasValidRank && !string.IsNullOrEmpty(playerRankText))
                {
                    logger.LogInfo($"Player {playerName} has invalid rank text: {playerRankText}");
                    return true;
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error checking rank for player {playerName}: {e.Message}");
                return false;
            }

            return false;
        }

        private bool HasOffensiveNameInternal(string playerName)
        {
            try
            {
                if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(offensiveNames.Value)) return false;

                string[] offensiveTokens = offensiveNames.Value.Split(',');
                string playerNameLower = playerName.ToLower();

                foreach (string offensiveName in offensiveTokens)
                {
                    string trimmed = offensiveName.Trim().ToLower();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    string playerNoSpaces = playerNameLower.Replace(" ", "");
                    string offensiveNoSpaces = trimmed.Replace(" ", "");

                    if (playerNoSpaces.Contains(offensiveNoSpaces))
                    {
                        logger.LogInfo($"Player {playerName} has offensive name containing: {trimmed}");
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error checking offensive name for player {playerName}: {e.Message}");
            }

            return false;
        }

        private static bool HasFormattedName(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return false;
            return richTextTagPattern.IsMatch(playerName);
        }
    }
}