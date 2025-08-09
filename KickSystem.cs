using System;
using System.Reflection;
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
                    logger.LogWarning($"Max kick attempts ({MAX_KICK_ATTEMPTS}) reached for {playerName} (Steam ID: {steamId})");
                    // If still in-game after all attempts, initiate softlock to neutralize the player
                    var mainMenuManagerForState = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                    bool gameHasStartedForState = false;
                    try { gameHasStartedForState = mainMenuManagerForState != null && mainMenuManagerForState.GameHasStarted; } catch {}
                    if (gameHasStartedForState && isHost())
                    {
                        Softlock.Start(steamId, playerName);
                    }
                    return;
                }

                if (lastKickTime.ContainsKey(steamId))
                {
                    float timeSinceLastKick = Time.time - lastKickTime[steamId];
                    if (timeSinceLastKick < KICK_RETRY_DELAY)
                    {
                        logger.LogInfo($"[Kick] Cooldown active for {playerName}: {KICK_RETRY_DELAY - timeSinceLastKick:F2}s remaining");
                        return;
                    }
                }

                if (!kickAttempts.ContainsKey(steamId)) kickAttempts[steamId] = 0;
                kickAttempts[steamId]++;
                lastKickTime[steamId] = Time.time;

                string playerType = isBannedPlayer ? "banned player" : "player";
                string gameState = isGameActive() ? "in-game" : "in-lobby";
                logger.LogInfo($"Kicking {playerType}: {playerName} (Steam ID: {steamId}) - Attempt {kickAttempts[steamId]}/{MAX_KICK_ATTEMPTS} [{gameState}]");

                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null)
                {
                    logger.LogError($"MainMenuManager not found - cannot kick {playerName}");
                    return;
                }

                bool gameHasStarted = false;
                try { gameHasStarted = mainMenuManager.GameHasStarted; } catch {}
                logger.LogInfo($"[Kick] GameHasStarted={gameHasStarted}");

                if (gameHasStarted)
                {
                    // In-game: try all paths in quick succession
                    try
                    {
                        mainMenuManager.KickPlayer(steamId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[Kick] MainMenuManager.KickPlayer threw: {ex.Message}");
                    }

                    try
                    {
                        if (mainMenuManager.mmmn != null)
                        {
                            mainMenuManager.mmmn.KickPlayer(steamId);
                        }
                        else
                        {
                            logger.LogWarning("[Kick] In-game: mmmn is null; skipping mmmn.KickPlayer");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[Kick] mmmn.KickPlayer threw: {ex.Message}");
                    }

                    try
                    {
                        BootstrapNetworkManager.KickPlayer(steamId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[Kick] BootstrapNetworkManager.KickPlayer threw: {ex.Message}");
                    }
                }
                else
                {
                    // Lobby: use normal MainMenuManager.KickPlayer
                    mainMenuManager.KickPlayer(steamId);
                }

                if (!isBannedPlayer)
                {
                    var trackedPlayers = playerManager.GetConnectedPlayers();
                    var playerToRemove = System.Linq.Enumerable.FirstOrDefault(trackedPlayers, kvp => kvp.Value == steamId);
                    if (!string.IsNullOrEmpty(playerToRemove.Key))
                    {
                        trackedPlayers.Remove(playerToRemove.Key);
                        playerManager.MarkRecentlyKicked(steamId);
                        if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers();
                    }
                }

                if (kickAttempts[steamId] < MAX_KICK_ATTEMPTS)
                {
                    coroutineHost.StartCoroutine(ScheduleKickRetry(steamId, playerName, isBannedPlayer));
                }
                else
                {
                    // If we've just hit max attempts, and we're in-game, start softlock
                    var mainMenuManagerForState = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                    bool gameHasStartedForState = false;
                    try { gameHasStartedForState = mainMenuManagerForState != null && mainMenuManagerForState.GameHasStarted; } catch {}
                    if (gameHasStartedForState && isHost())
                    {
                        Softlock.Start(steamId, playerName);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error kicking player {playerName}: {e.Message}\n{e.StackTrace}");
                if (!kickAttempts.ContainsKey(steamId) || kickAttempts[steamId] < MAX_KICK_ATTEMPTS)
                {
                    coroutineHost.StartCoroutine(ScheduleKickRetry(steamId, playerName, isBannedPlayer));
                }
                else
                {
                    // removed implimetation
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

            if (!isHost()) yield break;

            bool inLobbyNow = isInLobby();
            bool inGameNow = isGameActive();
            logger.LogInfo($"[Retry] host=true, inLobby={inLobbyNow}, inGame={inGameNow} for {playerName}");

            if (inLobbyNow)
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null)
                {
                    logger.LogWarning("[Retry] MainMenuManager not found during retry check");
                    yield break;
                }

                if (mainMenuManager.kickplayershold == null)
                {
                    logger.LogWarning("[Retry] kickplayershold is null during retry check");
                    yield break;
                }

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
                                playerManager.MarkRecentlyKicked(steamId);
                                if (BanUIManager.IsActive) BanUIManager.RefreshActivePlayers();
                            }
                            else
                            {
                                if (!playerManager.WasRecentlyKicked(steamId))
                                {
                                    playerManager.MarkRecentlyKicked(steamId);
                                }
                            }
                        }

                        ClearKickTracking(steamId);
                        // Stop any softlock if it was running for this player
                        Softlock.Stop(steamId);
                    }
                }
            }
            else if (inGameNow)
            {
                // In-game: just retry via main flow which will escalate on attempts
                string playerType = isBannedPlayer ? "banned player" : "player";
                logger.LogInfo($"{playerType} {playerName} retrying kick in-game...");
                KickPlayerWithRetry(steamId, playerName, isBannedPlayer);
            }
        }
    }
}


