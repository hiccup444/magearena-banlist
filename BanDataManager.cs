using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace PlayerBanMod
{
    public interface IBanDataManager
    {
        bool IsBanned(string steamId);
        void BanPlayer(string steamId, string playerName, string reason);
        void ToggleBanPlayer(string steamId, string playerName);
        void UnbanPlayer(string steamId);
        Dictionary<string, string> GetBannedPlayers();
        void SaveBans();
        IEnumerator LoadBansAsync();
        void LoadBans();
    }

    public sealed class BanDataManager : IBanDataManager
    {
        private readonly Dictionary<string, string> bannedPlayers;
        private readonly Dictionary<string, DateTime> banTimestamps;
        private readonly Dictionary<string, string> banReasons;
        private readonly ConfigEntry<string> bannedPlayersConfig;
        private readonly ManualLogSource logger;

        // Side-effect collaborators
        private readonly Func<string, bool> isPlayerHost;
        private readonly Func<bool> isHost;
        private readonly Func<bool> isInLobby;
        private readonly Action<string, string, bool> kickWithRetry;
        private readonly Action markNeedsSave;
        private readonly Action refreshActivePlayers;
        private readonly Action refreshBannedPlayers;
        private readonly Action<string> clearKickTracking;
        private readonly Action<string> removeRecentlyKicked;

        // Safe delimiters to avoid conflicts with typical player names
        private const string FIELD_DELIMITER = Constants.FieldDelimiter; // field separator
        private const string ENTRY_DELIMITER = Constants.EntryDelimiter; // entry separator

        public BanDataManager(
            Dictionary<string, string> bannedPlayers,
            Dictionary<string, DateTime> banTimestamps,
            Dictionary<string, string> banReasons,
            ConfigEntry<string> bannedPlayersConfig,
            ManualLogSource logger,
            // optional side-effect collaborators
            Func<string, bool> isPlayerHost = null,
            Func<bool> isHost = null,
            Func<bool> isInLobby = null,
            Action<string, string, bool> kickWithRetry = null,
            Action markNeedsSave = null,
            Action refreshActivePlayers = null,
            Action refreshBannedPlayers = null,
            Action<string> clearKickTracking = null,
            Action<string> removeRecentlyKicked = null)
        {
            this.bannedPlayers = bannedPlayers ?? throw new ArgumentNullException(nameof(bannedPlayers));
            this.banTimestamps = banTimestamps ?? throw new ArgumentNullException(nameof(banTimestamps));
            this.banReasons = banReasons ?? throw new ArgumentNullException(nameof(banReasons));
            this.bannedPlayersConfig = bannedPlayersConfig ?? throw new ArgumentNullException(nameof(bannedPlayersConfig));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            this.isPlayerHost = isPlayerHost;
            this.isHost = isHost;
            this.isInLobby = isInLobby;
            this.kickWithRetry = kickWithRetry;
            this.markNeedsSave = markNeedsSave;
            this.refreshActivePlayers = refreshActivePlayers;
            this.refreshBannedPlayers = refreshBannedPlayers;
            this.clearKickTracking = clearKickTracking;
            this.removeRecentlyKicked = removeRecentlyKicked;
        }

        public bool IsBanned(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return false;
            return bannedPlayers.ContainsKey(steamId);
        }

        public void BanPlayer(string steamId, string playerName, string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(steamId)) return;
                if (isPlayerHost != null && isPlayerHost(steamId))
                {
                    logger.LogWarning($"Cannot ban host player {playerName} (Steam ID: {steamId}) - host is immune to bans");
                    return;
                }

                if (!bannedPlayers.ContainsKey(steamId))
                {
                    bannedPlayers[steamId] = playerName ?? string.Empty;
                    banTimestamps[steamId] = DateTime.Now;
                    banReasons[steamId] = string.IsNullOrEmpty(reason) ? "Manual" : reason;

                    logger.LogInfo($"Banned player: {playerName} (Steam ID: {steamId}) at {DateTime.Now} - Reason: {banReasons[steamId]}");

                    if (isHost != null && isInLobby != null && isHost() && isInLobby() && kickWithRetry != null)
                    {
                        logger.LogInfo($"Immediately kicking newly banned player: {playerName}");
                        kickWithRetry(steamId, playerName, true);
                    }

                    markNeedsSave?.Invoke();
                    refreshActivePlayers?.Invoke();
                    refreshBannedPlayers?.Invoke();
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error banning player: {e.Message}");
            }
        }

        public void UnbanPlayer(string steamId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(steamId)) return;
                string name = bannedPlayers.ContainsKey(steamId) ? bannedPlayers[steamId] : steamId;
                bannedPlayers.Remove(steamId);
                banTimestamps.Remove(steamId);
                banReasons.Remove(steamId);

                logger.LogInfo($"Unbanned player: {name} (Steam ID: {steamId})");

                clearKickTracking?.Invoke(steamId);
                removeRecentlyKicked?.Invoke(steamId);

                markNeedsSave?.Invoke();
                refreshActivePlayers?.Invoke();
                refreshBannedPlayers?.Invoke();
            }
            catch (Exception e)
            {
                logger.LogError($"Error unbanning player: {e.Message}");
            }
        }

        public void ToggleBanPlayer(string steamId, string playerName)
        {
            try
            {
                if (isPlayerHost != null && isPlayerHost(steamId))
                {
                    logger.LogWarning($"Cannot ban host player {playerName} (Steam ID: {steamId}) - host is immune to bans");
                    return;
                }

                if (bannedPlayers.ContainsKey(steamId))
                {
                    UnbanPlayer(steamId);
                }
                else
                {
                    BanPlayer(steamId, playerName, "Manual");
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error toggling ban for player: {e.Message}");
            }
        }

        public Dictionary<string, string> GetBannedPlayers() => bannedPlayers;

        public void SaveBans()
        {
            try
            {
                var stringBuilder = new StringBuilder(bannedPlayers.Count * 100);
                bool first = true;
                foreach (var kvp in bannedPlayers)
                {
                    if (!first) stringBuilder.Append(ENTRY_DELIMITER);
                    first = false;

                    string steamId = kvp.Key;
                    string playerName = kvp.Value ?? string.Empty;
                    string timestamp = banTimestamps.ContainsKey(steamId)
                        ? banTimestamps[steamId].ToString("yyyy-MM-dd HH:mm:ss")
                        : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string reason = banReasons.ContainsKey(steamId) ? banReasons[steamId] : "Manual";

                    stringBuilder
                        .Append(steamId).Append(FIELD_DELIMITER)
                        .Append(playerName).Append(FIELD_DELIMITER)
                        .Append(timestamp).Append(FIELD_DELIMITER)
                        .Append(reason);
                }

                bannedPlayersConfig.Value = stringBuilder.ToString();
                logger.LogInfo($"Saved {bannedPlayers.Count} banned players with safe delimiters");
            }
            catch (Exception e)
            {
                logger.LogError($"Error saving banned players: {e.Message}");
            }
        }

        public void LoadBans()
        {
            bannedPlayers.Clear();
            banTimestamps.Clear();
            banReasons.Clear();

            string bannedData = bannedPlayersConfig.Value;
            if (string.IsNullOrEmpty(bannedData))
            {
                logger.LogInfo("Loaded 0 banned players");
                return;
            }

            bool appearsOldFormat = bannedData.Contains("|") && !bannedData.Contains(ENTRY_DELIMITER);
            if (appearsOldFormat)
            {
                logger.LogInfo("Loading banned players from old format and converting...");
                LoadOldFormat(bannedData);
            }
            else
            {
                LoadNewFormat(bannedData);
            }

            logger.LogInfo($"Loaded {bannedPlayers.Count} banned players");
        }

        public IEnumerator LoadBansAsync()
        {
            bannedPlayers.Clear();
            banTimestamps.Clear();
            banReasons.Clear();

            string bannedData = bannedPlayersConfig.Value;
            if (string.IsNullOrEmpty(bannedData))
            {
                logger.LogInfo("Loaded 0 banned players asynchronously");
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

                    if (processedCount % 100 == 0)
                    {
                        yield return null;
                    }
                }
            }

            logger.LogInfo($"Loaded {bannedPlayers.Count} banned players asynchronously");
        }

        private void ProcessBanEntry(string entry)
        {
            if (entry.Contains(FIELD_DELIMITER))
            {
                string[] parts = entry.Split(new[] { FIELD_DELIMITER }, StringSplitOptions.None);
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
                // Old format parsing path
                string[] parts = entry.Split(new[] { ':' }, 4);
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

        private void LoadOldFormat(string bannedData)
        {
            string[] entries = bannedData.Split('|');
            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    string[] parts = entry.Split(new[] { ':' }, 4);
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
        }

        private void LoadNewFormat(string bannedData)
        {
            string[] entries = bannedData.Split(new[] { ENTRY_DELIMITER }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    string[] parts = entry.Split(new[] { FIELD_DELIMITER }, StringSplitOptions.None);
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
            }
        }
    }
}


