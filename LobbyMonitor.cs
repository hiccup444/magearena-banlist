using System;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;

namespace PlayerBanMod
{
    public interface ILobbyMonitor
    {
        bool IsInLobby { get; }
        bool IsHost { get; }
        bool IsGameActive();
        bool IsInLobbyScreen();
        System.Collections.IEnumerator MonitorLoop(Action onEnterLobby, Action onLeaveLobby, Action<bool> onHostChanged, Action onTickInLobby);
        void CheckBannedOrAutoBanPlayer(string steamId, string playerName, Func<string, bool> isBanned, Action<string, string> kickBanned, Action<string, string> processAutoBan);
    }

    public class LobbyMonitor : ILobbyMonitor
    {
        private readonly ManualLogSource logger;

        public bool IsInLobby { get; private set; }
        public bool IsHost { get; private set; }

        public LobbyMonitor(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public System.Collections.IEnumerator MonitorLoop(Action onEnterLobby, Action onLeaveLobby, Action<bool> onHostChanged, Action onTickInLobby)
        {
            while (true)
            {
                yield return new WaitForSeconds(1f);

                bool wasInLobby = IsInLobby;
                IsInLobby = CheckIfInLobbyInternal();

                if (IsInLobby != wasInLobby)
                {
                    if (IsInLobby) onEnterLobby?.Invoke(); else onLeaveLobby?.Invoke();
                }

                bool wasHost = IsHost;
                IsHost = CheckIfHostInternal();
                if (IsHost != wasHost)
                {
                    onHostChanged?.Invoke(IsHost);
                }

                if (IsInLobby)
                {
                    onTickInLobby?.Invoke();
                }
            }
        }

        public bool IsGameActive()
        {
            try
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null) return false;

                // When the match is in progress, most input should be locked and UI hidden
                return mainMenuManager.GameHasStarted;
            }
            catch
            {
                return false;
            }
        }

        public bool IsInLobbyScreen()
        {
            try
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager == null) return false;
                return IsInLobby && !mainMenuManager.GameHasStarted;
            }
            catch
            {
                return false;
            }
        }

        private bool CheckIfInLobbyInternal()
        {
            try
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                return mainMenuManager != null && BootstrapManager.CurrentLobbyID != 0;
            }
            catch
            {
                return false;
            }
        }

        private bool CheckIfHostInternal()
        {
            try
            {
                var mainMenuManager = UnityEngine.Object.FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null)
                {
                    CSteamID lobbyId = new CSteamID(BootstrapManager.CurrentLobbyID);
                    CSteamID ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId);
                    CSteamID localId = SteamUser.GetSteamID();
                    return ownerId == localId;
                }
            }
            catch
            {
            }
            return false;
        }

        public void CheckBannedOrAutoBanPlayer(
            string steamId,
            string playerName,
            Func<string, bool> isBanned,
            Action<string, string> kickBanned,
            Action<string, string> processAutoBan)
        {
            try
            {
                if (isBanned(steamId))
                {
                    logger.LogInfo($"Banned player {playerName} (Steam ID: {steamId}) detected - kicking");
                    kickBanned(steamId, playerName);
                }
                else
                {
                    processAutoBan(steamId, playerName);
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error checking banned/auto-ban for {playerName}: {e.Message}");
            }
        }
    }
}


