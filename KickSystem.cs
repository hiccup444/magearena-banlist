using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PlayerBanMod
{
    public interface IKickSystem
    {
        void KickPlayerWithRetry(string steamId, string playerName, bool isBannedPlayer = false);
        void ClearKickTracking(string steamId);
    }

    public class KickSystem : IKickSystem
    {
        private readonly ManualLogSource logger;
        private readonly Func<bool> isGameActive;
        private readonly Func<bool> isHost;
        private readonly Func<bool> isInLobby;
        private readonly IPlayerManager playerManager;

        private readonly Dictionary<string, int> kickAttempts = new Dictionary<string, int>();
        private readonly Dictionary<string, float> lastKickTime = new Dictionary<string, float>();

        private const int MAX_KICK_ATTEMPTS = Constants.MaxKickAttempts;
        private const float KICK_RETRY_DELAY = Constants.KickRetryDelaySeconds;

        private readonly MonoBehaviour coroutineHost;

        public KickSystem(ManualLogSource logger, MonoBehaviour coroutineHost, Func<bool> isGameActive, Func<bool> isHost, Func<bool> isInLobby, IPlayerManager playerManager)
        {
            this.logger = logger;
            this.coroutineHost = coroutineHost;
            this.isGameActive = isGameActive;
            this.isHost = isHost;
            this.isInLobby = isInLobby;
            this.playerManager = playerManager;
        }

        public void KickPlayerWithRetry(string steamId, string playerName, bool isBannedPlayer = false)
        {
            try
            {
                if (PlayerBanMod.Instance.IsPlayerHost(steamId))
                {
                    logger.LogWarning($"Cannot kick host player {playerName} (Steam ID: {steamId}) - host is immune to kicks");
                    return;
                }

                if (kickAttempts.ContainsKey(steamId) && kickAttempts[steamId] >= MAX_KICK_ATTEMPTS)
                {
                    logger.LogWarning($"Max kick attempts ({MAX_KICK_ATTEMPTS}) reached for {playerName} (Steam ID: {steamId}) - stopping retry");
                    return;
                }

                if (lastKickTime.ContainsKey(steamId))
                {
                    float timeSinceLastKick = Time.time - lastKickTime[steamId];
                    if (timeSinceLastKick < KICK_RETRY_DELAY)
                    {
                        return;
                    }
                }

                if (!kickAttempts.ContainsKey(steamId)) kickAttempts[steamId] = 0;
                kickAttempts[steamId]++;
                lastKickTime[steamId] = Time.time;

                string playerType = isBannedPlayer ? "banned player" : "player";
                string gameState = isGameActive() ? "in-game" : "in-lobby";
                logger.LogInfo($"Kicking {playerType}: {playerName} (Steam ID: {steamId}) - Attempt {kickAttempts[steamId]}/{MAX_KICK_ATTEMPTS} [{gameState}]");

                if (isGameActive())
                {
                    BootstrapNetworkManager.KickPlayer(steamId);
                }
                else
                {
                    var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                    if (mainMenuManager != null)
                    {
                        mainMenuManager.KickPlayer(steamId);
                    }
                    else
                    {
                        logger.LogError($"MainMenuManager not found - cannot kick {playerName} in lobby");
                        return;
                    }
                }

                if (!isBannedPlayer)
                {
                    var trackedPlayers = playerManager.GetConnectedPlayers();
                    var playerToRemove = System.Linq.Enumerable.FirstOrDefault(trackedPlayers, kvp => kvp.Value == steamId);
                    if (!string.IsNullOrEmpty(playerToRemove.Key))
                    {
                        trackedPlayers.Remove(playerToRemove.Key);
                        logger.LogInfo($"Immediately removed {playerName} from active players list after kick");
                        playerManager.MarkRecentlyKicked(steamId);
                        if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers();
                    }
                }

                if (kickAttempts[steamId] < MAX_KICK_ATTEMPTS)
                {
                    coroutineHost.StartCoroutine(ScheduleKickRetry(steamId, playerName, isBannedPlayer));
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error kicking player {playerName}: {e.Message}");
                if (!kickAttempts.ContainsKey(steamId) || kickAttempts[steamId] < MAX_KICK_ATTEMPTS)
                {
                    coroutineHost.StartCoroutine(ScheduleKickRetry(steamId, playerName, isBannedPlayer));
                }
            }
        }

        public void ClearKickTracking(string steamId)
        {
            kickAttempts.Remove(steamId);
            lastKickTime.Remove(steamId);
        }

        private System.Collections.IEnumerator ScheduleKickRetry(string steamId, string playerName, bool isBannedPlayer)
        {
            yield return new WaitForSeconds(KICK_RETRY_DELAY);

            if (isHost() && isInLobby())
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    bool playerStillConnected = mainMenuManager.kickplayershold.nametosteamid.ContainsValue(steamId);
                    if (playerStillConnected)
                    {
                        string playerType = isBannedPlayer ? "banned player" : "player";
                        logger.LogInfo($"{playerType} {playerName} still connected after kick attempt - retrying...");
                        KickPlayerWithRetry(steamId, playerName, isBannedPlayer);
                    }
                    else
                    {
                        string playerType = isBannedPlayer ? "banned player" : "player";
                        logger.LogInfo($"{playerType} {playerName} successfully kicked");

                        if (!isBannedPlayer)
                        {
                            var trackedPlayers = playerManager.GetConnectedPlayers();
                            var playerToRemove = System.Linq.Enumerable.FirstOrDefault(trackedPlayers, kvp => kvp.Value == steamId);
                            if (!string.IsNullOrEmpty(playerToRemove.Key))
                            {
                                trackedPlayers.Remove(playerToRemove.Key);
                                logger.LogInfo($"Removed {playerName} from active players list after successful kick (retry)");
                                playerManager.MarkRecentlyKicked(steamId);
                                if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers();
                            }
                            else
                            {
                                logger.LogInfo($"{playerName} was already removed from active players list");
                                if (!playerManager.WasRecentlyKicked(steamId))
                                {
                                    playerManager.MarkRecentlyKicked(steamId);
                                    logger.LogInfo($"Marked {playerName} as recently kicked (already removed)");
                                }
                            }
                        }

                        ClearKickTracking(steamId);
                    }
                }
            }
        }
    }
}


