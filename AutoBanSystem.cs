using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

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
        private readonly ConfigEntry<bool> autobanModdedRanks;
        private readonly ConfigEntry<bool> autobanOffensiveNames;
        private readonly ConfigEntry<string> offensiveNames;
        private readonly IBanDataManager banDataManager;
        private readonly IPlayerManager playerManager;

        public AutoBanSystem(
            ManualLogSource logger,
            Func<bool> isInLobbyScreen,
            ConfigEntry<bool> autobanModdedRanks,
            ConfigEntry<bool> autobanOffensiveNames,
            ConfigEntry<string> offensiveNames,
            IBanDataManager banDataManager,
            IPlayerManager playerManager)
        {
            this.logger = logger;
            this.isInLobbyScreen = isInLobbyScreen;
            this.autobanModdedRanks = autobanModdedRanks;
            this.autobanOffensiveNames = autobanOffensiveNames;
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
                // Only run autoban checks while on the lobby screen (not during game)
                if (!isInLobbyScreen()) return;

                // Modded rank: only in lobby screen
                if (autobanModdedRanks.Value && HasModdedRankInternal(steamId, playerName))
                {
                    logger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has modded rank - auto-banning");
                    onAutoBan?.Invoke(steamId, playerName);
                    return;
                }

                // Offensive name: lobby screen only (players cannot join mid-game)
                if (autobanOffensiveNames.Value && HasOffensiveNameInternal(playerName))
                {
                    logger.LogInfo($"Player {playerName} (Steam ID: {steamId}) has offensive name - auto-banning");
                    onAutoBan?.Invoke(steamId, playerName);
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
                if (!isInLobbyScreen()) return false;

                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null || mainMenuManager.kickplayershold == null) return false;

                if (!mainMenuManager.kickplayershold.nametosteamid.ContainsKey(playerName) ||
                    mainMenuManager.kickplayershold.nametosteamid[playerName] != steamId)
                {
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
                for (int i = 0; i < mainMenuManager.rankandleveltext.Length && i < 8; i++)
                {
                    if (i * 2 < mainMenuManager.texts.Length && mainMenuManager.texts[i * 2].text == playerName)
                    {
                        playerRankText = mainMenuManager.rankandleveltext[i].text;
                        foundPlayer = true;
                        break;
                    }
                }

                if (!foundPlayer)
                {
                    if (playerManager.GetConnectedPlayers().ContainsKey(playerName) && Time.time % 10f < 1f)
                    {
                        logger.LogInfo($"Could not find rank text for player {playerName} in lobby UI (player may have left)");
                    }
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
    }
}


