using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

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

        // Safe delimiters for legacy config format
        private const string FIELD_DELIMITER = Constants.FieldDelimiter; // field separator
        private const string ENTRY_DELIMITER = Constants.EntryDelimiter; // entry separator

        // File storage
        private readonly string bansFilePath;
        [Serializable]
        private class BanRecord
        {
            public string Name;
            public string SteamID;
            public string Date;
            public string Reason;
        }

        public BanDataManager(
            Dictionary<string, string> bannedPlayers,
            Dictionary<string, DateTime> banTimestamps,
            Dictionary<string, string> banReasons,
            ConfigEntry<string> bannedPlayersConfig,
            ManualLogSource logger,

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

            // Compute bans file path under BepInEx/plugins/BanList/Bans/banned_players.jsonl
            try
            {
                string baseDir = Path.Combine(Paths.PluginPath, "BanList", "Bans");
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
                bansFilePath = Path.Combine(baseDir, "banned_players.jsonl");
            }
            catch
            {
                bansFilePath = "banned_players.jsonl";
            }
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
                    string safeName = SanitizePlayerNameForStorage(playerName);
                    bannedPlayers[steamId] = safeName;
                    banTimestamps[steamId] = DateTime.Now;
                    banReasons[steamId] = string.IsNullOrEmpty(reason) ? "Manual" : reason;

                    logger.LogInfo($"Banned player: {playerName} (stored as: {safeName}) (Steam ID: {steamId}) at {DateTime.Now} - Reason: {banReasons[steamId]}");

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
                using (var fs = new FileStream(bansFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    foreach (var kvp in bannedPlayers)
                    {
                        string steamId = kvp.Key;
                        string playerName = kvp.Value ?? string.Empty;
                        string timestamp = banTimestamps.ContainsKey(steamId)
                            ? banTimestamps[steamId].ToString("yyyy-MM-dd HH:mm:ss")
                            : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string reason = banReasons.ContainsKey(steamId) ? banReasons[steamId] : "Manual";

                        var record = new BanRecord
                        {
                            Name = playerName,
                            SteamID = steamId,
                            Date = timestamp,
                            Reason = reason
                        };
                        string json = UnityEngine.JsonUtility.ToJson(record);
                        sw.WriteLine(json);
                    }
                }
                logger.LogInfo($"Saved {bannedPlayers.Count} banned players to JSONL: {bansFilePath}");
            }
            catch (Exception e)
            {
                logger.LogError($"Error saving banned players: {e.Message}");
            }
        }

        private string SanitizePlayerNameForStorage(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            // Replace if it contains any delimiter to avoid breaking config parsing
            if (name.Contains(FIELD_DELIMITER) || name.Contains(ENTRY_DELIMITER))
            {
                return "Player";
            }
            return name;
        }

        public void LoadBans()
        {
            bannedPlayers.Clear();
            banTimestamps.Clear();
            banReasons.Clear();

            // Migrate from legacy config if present
            MigrateLegacyConfigIfNeeded();

            // Load from file
            LoadFromFile();
            logger.LogInfo($"Loaded {bannedPlayers.Count} banned players from file");
        }

        public IEnumerator LoadBansAsync()
        {
            // Async: migrate then load file in one go (file sizes are small)
            MigrateLegacyConfigIfNeeded();
            LoadFromFile();
            logger.LogInfo($"Loaded {bannedPlayers.Count} banned players from file asynchronously");
            yield break;
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

        private void LoadFromFile()
        {
            try
            {
                // Ensure directory exists
                string dir = Path.GetDirectoryName(bansFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    logger.LogInfo($"Created bans directory: {dir}");
                }

                // Create empty file if missing
                if (!File.Exists(bansFilePath))
                {
                    using (File.Create(bansFilePath)) { }
                    logger.LogInfo($"Created bans file: {bansFilePath}");
                    return;
                }
                var lines = File.ReadAllLines(bansFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = UnityEngine.JsonUtility.FromJson<BanRecord>(line);
                        if (rec == null || string.IsNullOrEmpty(rec.SteamID)) continue;
                        bannedPlayers[rec.SteamID] = rec.Name ?? string.Empty;
                        if (!string.IsNullOrEmpty(rec.Date))
                        {
                            if (DateTime.TryParse(rec.Date, out var dt)) banTimestamps[rec.SteamID] = dt; else banTimestamps[rec.SteamID] = DateTime.Now;
                        }
                        else
                        {
                            banTimestamps[rec.SteamID] = DateTime.Now;
                        }
                        banReasons[rec.SteamID] = string.IsNullOrEmpty(rec.Reason) ? "Manual" : rec.Reason;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"Failed to parse ban record line: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error loading bans file: {e.Message}");
            }
        }

        private void MigrateLegacyConfigIfNeeded()
        {
            try
            {
                string bannedData = bannedPlayersConfig.Value;
                if (string.IsNullOrEmpty(bannedData)) return;

                // Parse legacy config into dictionaries
                bannedPlayers.Clear();
                banTimestamps.Clear();
                banReasons.Clear();

                bool appearsOldFormat = bannedData.Contains("|") && !bannedData.Contains(ENTRY_DELIMITER);
                if (appearsOldFormat)
                {
                    logger.LogInfo("[Migration] Loading banned players from old format...");
                    LoadOldFormat(bannedData);
                }
                else
                {
                    logger.LogInfo("[Migration] Loading banned players from legacy config...");
                    LoadNewFormat(bannedData);
                }

                // Ensure directory exists before writing
                string dir = Path.GetDirectoryName(bansFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    logger.LogInfo($"Created bans directory for migration: {dir}");
                }

                // Write to file
                SaveBans();

                // Clear config to remove outdated field
                bannedPlayersConfig.Value = string.Empty;
                logger.LogInfo("[Migration] Migration complete. Cleared legacy config storage.");
            }
            catch (Exception e)
            {
                logger.LogError($"[Migration] Error migrating legacy config: {e.Message}");
            }
        }
    }
}