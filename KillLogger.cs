using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using BepInEx;
using BepInEx.Logging;

namespace PlayerBanMod
{
    public class KillLogger
    {
        private static KillLogger instance;
        public static KillLogger Instance 
        { 
            get 
            { 
                if (instance == null)
                    instance = new KillLogger();
                return instance;
            }
        }

        private List<KillLogEntry> currentGameKills = new List<KillLogEntry>();
        private List<SpellLogEntry> currentGameSpells = new List<SpellLogEntry>();
        private DateTime gameStartTime;
        private bool isLogging = false;
        private readonly string logDirectory;
        private readonly string logFilePath;
        private Dictionary<string, DateTime> playerDeathTimes = new Dictionary<string, DateTime>();
        public bool IsCurrentlyLogging => isLogging;
        public int CurrentKillCount => currentGameKills.Count;
        public int CurrentSpellCount => currentGameSpells.Count;
        
        // Spell cast rate monitoring
        private Dictionary<string, List<DateTime>> playerSpellCastTimes = new Dictionary<string, List<DateTime>>();
        private Dictionary<string, DateTime> playerSpellWarningCooldowns = new Dictionary<string, DateTime>();

        private List<PrefabSpawnLogEntry> currentGamePrefabSpawns = new List<PrefabSpawnLogEntry>();

        private List<ItemEquipLogEntry> currentGameItemEquips = new List<ItemEquipLogEntry>();

        private KillLogger()
        {
            // Use BepInEx plugins directory for reliable access
            try
            {
                logDirectory = Path.Combine(Paths.PluginPath, "BanList", "Logs");
                if (!Directory.Exists(logDirectory)) 
                {
                    Directory.CreateDirectory(logDirectory);
                    PlayerBanMod.LogInfoStatic($"Created log directory: {logDirectory}");
                }
                logFilePath = Path.Combine(logDirectory, "log.jsonl");
                
                // Create empty file if it doesn't exist
                if (!File.Exists(logFilePath))
                {
                    using (File.Create(logFilePath)) { }
                    PlayerBanMod.LogInfoStatic($"Created log file: {logFilePath}");
                }
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error creating log directory: {ex.Message}");
                // Fallback to current directory
                logDirectory = "BanList/Logs";
                logFilePath = Path.Combine(logDirectory, "log.jsonl");
            }
        }

        public void StartGameLogging()
        {
            if (!isLogging)
            {
                gameStartTime = DateTime.Now;
                currentGameKills.Clear();
                currentGameSpells.Clear();
                playerDeathTimes.Clear();
                playerSpellCastTimes.Clear();
                playerSpellWarningCooldowns.Clear();
                currentGamePrefabSpawns.Clear();
                currentGameItemEquips.Clear();
                isLogging = true;
                PlayerBanMod.LogInfoStatic("Kill logging started for new game");
                
                // Log the game start immediately
                LogGameStart();
                
                // Clear any previous log messages from the UI when starting a new game
                BanUIManager.ClearLogMessages();
            }
        }

        public void StopGameLogging()
        {
            if (isLogging)
            {
                isLogging = false;
                LogGameEnd();
                PlayerBanMod.LogInfoStatic($"Kill logging stopped and saved: {currentGameKills.Count} kills, {currentGameSpells.Count} spells");
            }
        }

        // method to log game start
        private void LogGameStart()
        {
            try
            {
                var gameStartLog = new GameStartLog
                {
                    GameStartTime = gameStartTime.ToString("yyyy-MM-dd HH:mm:ss")
                };
                
                string jsonLine = JsonUtility.ToJson(gameStartLog);
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine);
                PlayerBanMod.LogInfoStatic("Game start logged");
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error logging game start: {ex.Message}");
            }
        }

        // method to log game end
        private void LogGameEnd()
        {
            try
            {
                var gameEndLog = new GameEndLog
                {
                    GameEndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TotalKills = currentGameKills.Count,
                    TotalSpells = currentGameSpells.Count
                };
                
                string jsonLine = JsonUtility.ToJson(gameEndLog);
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine);
                PlayerBanMod.LogInfoStatic("Game end logged");
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error logging game end: {ex.Message}");
            }
        }

        public void LogKill(string killerName, string victimName, string causeOfDeath)
        {
            if (!isLogging) 
            {
                PlayerBanMod.LogInfoStatic("LogKill called but logging is not active");
                return;
            }

            // Sanitize usernames to prevent JSON parsing issues
            var sanitizedKillerName = SanitizeUsername(killerName);
            var sanitizedVictimName = SanitizeUsername(victimName);

            var entry = new KillLogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                KillerName = sanitizedKillerName,
                VictimName = sanitizedVictimName,
                CauseOfDeath = causeOfDeath,
                GameTime = (DateTime.Now - gameStartTime).ToString(@"mm\:ss")
            };

            currentGameKills.Add(entry);
            
            // Log kill immediately to file
            try
            {
                string jsonLine = JsonUtility.ToJson(entry);
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error logging kill to file: {ex.Message}");
            }
            
            // Track death time for respawn warning
            playerDeathTimes[sanitizedVictimName] = DateTime.Now;
            
            // Create properly formatted message for UI
            string killMessage = sanitizedKillerName == "Environment" 
                ? $"[{DateTime.Now:HH:mm:ss}] {sanitizedVictimName} died from {causeOfDeath}"
                : $"[{DateTime.Now:HH:mm:ss}] {sanitizedKillerName} killed {sanitizedVictimName} with {causeOfDeath}";
            
            // add debug logging to see if this is being called
            PlayerBanMod.LogInfoStatic($"Adding kill message to UI: {killMessage}");
            BanUIManager.AddLogMessage(killMessage);
        }

        public void TestLogging()
        {
            PlayerBanMod.LogInfoStatic("TestLogging called");
            BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] === TEST MESSAGE FROM KILLLOGGER ===");
            BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Test kill: Player1 killed Player2 with Sword");
            BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Test spell: Wizard casted Fireball at level 3");
        }

        public void LogSpell(string casterName, string spellName, int spellLevel)
        {
            if (!isLogging) 
            {
                PlayerBanMod.LogInfoStatic("LogSpell called but logging is not active");
                return;
            }

            // Sanitize username to prevent JSON parsing issues
            var sanitizedCasterName = SanitizeUsername(casterName);

            var entry = new SpellLogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CasterName = sanitizedCasterName,
                SpellName = spellName,
                SpellLevel = spellLevel,
                GameTime = (DateTime.Now - gameStartTime).ToString(@"mm\:ss")
            };

            currentGameSpells.Add(entry);
            
            // Log spell immediately to file
            try
            {
                string jsonLine = JsonUtility.ToJson(entry);
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error logging spell to file: {ex.Message}");
            }
            
            // Track spell cast times for rate monitoring
            if (!playerSpellCastTimes.ContainsKey(sanitizedCasterName))
            {
                playerSpellCastTimes[sanitizedCasterName] = new List<DateTime>();
            }
            
            var now = DateTime.Now;
            playerSpellCastTimes[sanitizedCasterName].Add(now);
            
            // Check for rapid spell casting (5+ spells in 3 seconds)
            if (!IsPlayerOnSpellWarningCooldown(sanitizedCasterName))
            {
                var recentCasts = playerSpellCastTimes[sanitizedCasterName]
                    .Where(time => (now - time).TotalSeconds <= 3.0)
                    .ToList();
                
                if (recentCasts.Count >= 5)
                {
                    // Log warning and set cooldown
                    string warningMessage = $"[{DateTime.Now:HH:mm:ss}] WARNING: {sanitizedCasterName} casted {recentCasts.Count} spells in under 3 seconds!";
                    BanUIManager.AddLogMessage(warningMessage);
                    playerSpellWarningCooldowns[sanitizedCasterName] = now.AddSeconds(10.0);
                    
                    // Clean up old cast times (older than 3 seconds)
                    playerSpellCastTimes[sanitizedCasterName] = recentCasts;
                }
            }
            
            // Create properly formatted message for UI
            string spellMessage = spellLevel > 0 
                ? $"[{DateTime.Now:HH:mm:ss}] {sanitizedCasterName} casted {spellName} at level {spellLevel}"
                : $"[{DateTime.Now:HH:mm:ss}] {sanitizedCasterName} casted {spellName}";
            
            // Add debug logging
            PlayerBanMod.LogInfoStatic($"Adding spell message to UI: {spellMessage}");
            BanUIManager.AddLogMessage(spellMessage);
        }

        public void LogRespawnWarning(string playerName)
        {
            if (!isLogging) return;

            if (playerDeathTimes.TryGetValue(playerName, out DateTime deathTime))
            {
                TimeSpan timeSinceDeath = DateTime.Now - deathTime;
                if (timeSinceDeath.TotalSeconds < 2.0)
                {
                    string warningMessage = $"[{DateTime.Now:HH:mm:ss}] WARNING: {playerName} respawned too fast! ({timeSinceDeath.TotalSeconds:F1}s)";
                    BanUIManager.AddLogMessage(warningMessage);
                }
            }
        }

        private bool IsPlayerOnSpellWarningCooldown(string playerName)
        {
            if (playerSpellWarningCooldowns.TryGetValue(playerName, out DateTime cooldownEnd))
            {
                if (DateTime.Now < cooldownEnd)
                {
                    return true; // Still on cooldown
                }
                else
                {
                    // Cooldown expired, remove it
                    playerSpellWarningCooldowns.Remove(playerName);
                }
            }
            return false; // Not on cooldown
        }

        /// <summary>
        /// Logs a prefab spawn event to the file and updates UI
        /// </summary>
        /// <param name="playerName">Name of the player who spawned the prefab</param>
        /// <param name="prefabName">Name of the spawned prefab</param>
        /// <param name="spawnPosition">Position where the prefab was spawned</param>
        public void LogPrefabSpawn(string playerName, string prefabName, Vector3 spawnPosition)
        {
            try
            {
                // Sanitize username to prevent JSON parsing issues
                var sanitizedPlayerName = SanitizeUsername(playerName);
                var sanitizedPrefabName = prefabName ?? "Unknown";
                
                var entry = new PrefabSpawnLogEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    PlayerName = sanitizedPlayerName,
                    PrefabName = sanitizedPrefabName,
                    SpawnPosition = "", // No longer storing position
                    GameTime = (DateTime.Now - gameStartTime).ToString(@"mm\:ss")
                };
                
                // Add to current game spells list for consistency with existing structure
                currentGamePrefabSpawns.Add(entry);
                
                // Write to log file
                string jsonLine = JsonUtility.ToJson(entry) + Environment.NewLine;
                File.AppendAllText(logFilePath, jsonLine);
                
                // Create properly formatted message for UI - simplified without position
                string spawnMessage = $"[{DateTime.Now:HH:mm:ss}] {sanitizedPlayerName} spawned prefab {sanitizedPrefabName}";
                
                // Add to UI
                BanUIManager.AddLogMessage(spawnMessage);
                
                // Log to BepInEx console
                PlayerBanMod.LogInfoStatic($"Prefab spawn logged: {sanitizedPlayerName} spawned {sanitizedPrefabName}");
            }
            catch (Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error logging prefab spawn: {e.Message}\nStack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Logs an item equip event to the file and updates UI
        /// </summary>
        /// <param name="playerName">Name of the player who equipped the item</param>
        /// <param name="itemName">Name of the equipped item</param>
        public void LogItemEquip(string playerName, string itemName)
        {
            if (!isLogging) 
            {
                PlayerBanMod.LogInfoStatic("LogItemEquip called but logging is not active");
                return;
            }

            // Sanitize username to prevent JSON parsing issues
            var sanitizedPlayerName = SanitizeUsername(playerName);
            var sanitizedItemName = itemName ?? "Unknown";

            var entry = new ItemEquipLogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                PlayerName = sanitizedPlayerName,
                ItemName = sanitizedItemName,
                GameTime = (DateTime.Now - gameStartTime).ToString(@"mm\:ss")
            };

            // Add to current game spells list for consistency with existing structure
            currentGameItemEquips.Add(entry);
            
            // Log item equip immediately to file
            try
            {
                string jsonLine = JsonUtility.ToJson(entry);
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error logging item equip to file: {ex.Message}");
            }
            
            // Create properly formatted message for UI
            string itemEquipMessage = $"[{DateTime.Now:HH:mm:ss}] {sanitizedPlayerName} equipped {sanitizedItemName}";
            
            // Add to UI
            BanUIManager.AddLogMessage(itemEquipMessage);
        }

        /// <summary>
        /// Sanitizes player usernames to prevent JSON parsing issues and ensure log integrity
        /// </summary>
        /// <param name="username">The raw username from the game</param>
        /// <returns>A sanitized username safe for logging</returns>
        private string SanitizeUsername(string username)
        {
            if (string.IsNullOrEmpty(username))
                return "Unknown";
            
            // Remove or replace characters that could break JSON parsing
            var sanitized = username
                .Replace("\"", "'")           // Replace quotes with single quotes
                .Replace("\\", "/")            // Replace backslashes with forward slashes
                .Replace("\n", " ")            // Replace newlines with spaces
                .Replace("\r", " ")            // Replace carriage returns with spaces
                .Replace("\t", " ")            // Replace tabs with spaces
                .Replace("\0", "");            // Remove null characters
            
            // Remove any other control characters
            sanitized = new string(sanitized.Where(c => !char.IsControl(c)).ToArray());
            
            // Trim whitespace and limit length
            sanitized = sanitized.Trim();
            if (sanitized.Length > 50) // Reasonable max length for usernames
                sanitized = sanitized.Substring(0, 50);
            
            // If after sanitization the username is empty or just whitespace, use "Unknown"
            if (string.IsNullOrWhiteSpace(sanitized))
                return "Unknown";
            
            return sanitized;
        }



        // Remove the old SaveCurrentGameLogs method since we now log in real-time
        public void SaveCurrentGameLogs()
        {
            // This method is now mostly empty since we log in real-time
            // Just add a summary log for debugging
            PlayerBanMod.LogInfoStatic($"Game session summary: {currentGameKills.Count} kills, {currentGameSpells.Count} spells");
        }

        public void LoadPreviousLogs()
        {
            try
            {
                PlayerBanMod.LogInfoStatic($"LoadPreviousLogs called, checking file: {logFilePath}");
                
                if (!File.Exists(logFilePath)) 
                {
                    PlayerBanMod.LogInfoStatic($"Log file does not exist: {logFilePath}");
                    BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] No previous log file found at: {logFilePath}");
                    return;
                }

                var lines = File.ReadAllLines(logFilePath);
                PlayerBanMod.LogInfoStatic($"Log file exists with {lines.Length} lines");
                
                if (lines.Length == 0) 
                {
                    PlayerBanMod.LogInfoStatic("Log file is empty");
                    BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Log file is empty");
                    return;
                }

                // Parse individual log entries
                currentGameKills.Clear();
                currentGameSpells.Clear();
                playerDeathTimes.Clear();
                playerSpellCastTimes.Clear();
                playerSpellWarningCooldowns.Clear();
                currentGamePrefabSpawns.Clear();
                currentGameItemEquips.Clear();

                // Variables to store game session info
                string gameStartTime = "";
                string gameEndTime = "";

                // Find the most recent game session by parsing timestamps
                int lastGameSessionIndex = -1;
                bool isIncompleteSession = false;
                DateTime latestGameStart = DateTime.MinValue;
                
                // First pass: find the latest GameStartTime by parsing actual timestamps
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    
                    if (line.Contains("\"GameStartTime\"") && !line.Contains("\"GameEndTime\""))
                    {
                        try
                        {
                            var startLog = JsonUtility.FromJson<GameStartLog>(line);
                            if (startLog != null && DateTime.TryParse(startLog.GameStartTime, out DateTime parsedTime))
                            {
                                if (parsedTime > latestGameStart)
                                {
                                    latestGameStart = parsedTime;
                                    lastGameSessionIndex = i;
                                    isIncompleteSession = true;
                                    PlayerBanMod.LogInfoStatic($"Found newer game start at line {i}: {startLog.GameStartTime}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            PlayerBanMod.LogErrorStatic($"Error parsing GameStartTime at line {i}: {ex.Message}");
                        }
                    }
                }
                
                // If we found a GameStartTime, check if there's a corresponding GameEndTime
                if (lastGameSessionIndex != -1)
                {
                    // Look for a GameEndTime that follows this GameStartTime
                    for (int i = lastGameSessionIndex + 1; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        
                        if (line.Contains("\"GameEndTime\""))
                        {
                            try
                            {
                                var endLog = JsonUtility.FromJson<GameEndLog>(line);
                                if (endLog != null && DateTime.TryParse(endLog.GameEndTime, out DateTime endTime))
                                {
                                    // Verify this end time is after our start time
                                    if (endTime > latestGameStart)
                                    {
                                        isIncompleteSession = false;
                                        PlayerBanMod.LogInfoStatic($"Found corresponding GameEndTime at line {i}: {endLog.GameEndTime}");
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                PlayerBanMod.LogErrorStatic($"Error parsing GameEndTime at line {i}: {ex.Message}");
                            }
                        }
                    }
                }
                
                // If we found a game session, parse it
                if (lastGameSessionIndex != -1)
                {
                    PlayerBanMod.LogInfoStatic($"Parsing game session at line {lastGameSessionIndex} (Incomplete: {isIncompleteSession})");
                    
                    // First pass: collect all the data and find start/end times
                    var logEntries = new List<(string timestamp, string message)>();
                    
                    try
                    {
                        var line = lines[lastGameSessionIndex].Trim();
                        
                        if (isIncompleteSession)
                        {
                            // This is a GameStartTime only entry (incomplete session)
                            var startLog = JsonUtility.FromJson<GameStartLog>(line);
                            if (startLog != null)
                            {
                                PlayerBanMod.LogInfoStatic($"Found game start: {startLog.GameStartTime}");
                                gameStartTime = startLog.GameStartTime;
                                
                                // For incomplete sessions, add a default end time (current time)
                                gameEndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                PlayerBanMod.LogInfoStatic($"Added default game end time: {gameEndTime} (session was incomplete)");
                                
                                // Since this is just a start log, we need to look for individual log entries that follow
                                // Search forward from this line to find kills, spells, and prefab spawns
                                for (int i = lastGameSessionIndex + 1; i < lines.Length; i++)
                                {
                                    var nextLine = lines[i].Trim();
                                    if (string.IsNullOrEmpty(nextLine)) continue;
                                    
                                    // Stop if we hit another game start/end entry
                                    if (nextLine.Contains("\"GameStartTime\"") || nextLine.Contains("\"GameEndTime\""))
                                        break;
                                    
                                    try
                                    {
                                        // Try to parse as different entry types
                                        if (nextLine.Contains("\"KillerName\""))
                                        {
                                            var kill = JsonUtility.FromJson<KillLogEntry>(nextLine);
                                            if (kill != null)
                                            {
                                                currentGameKills.Add(kill);
                                                
                                                // Store the message with original timestamp for later display
                                                string killMsg = kill.KillerName == "Environment" 
                                                    ? $"{kill.VictimName} died from {kill.CauseOfDeath}"
                                                    : $"{kill.KillerName} killed {kill.VictimName} with {kill.CauseOfDeath}";
                                                logEntries.Add((kill.Timestamp, killMsg));
                                            }
                                        }
                                        else if (nextLine.Contains("\"CasterName\""))
                                        {
                                            var spell = JsonUtility.FromJson<SpellLogEntry>(nextLine);
                                            if (spell != null)
                                            {
                                                currentGameSpells.Add(spell);
                                                
                                                // Store the message with original timestamp for later display
                                                string spellMsg = spell.SpellLevel > 0 
                                                    ? $"{spell.CasterName} casted {spell.SpellName} at level {spell.SpellLevel}"
                                                    : $"{spell.CasterName} casted {spell.SpellName}";
                                                logEntries.Add((spell.Timestamp, spellMsg));
                                            }
                                        }
                                        else if (nextLine.Contains("\"PrefabName\""))
                                        {
                                            var spawn = JsonUtility.FromJson<PrefabSpawnLogEntry>(nextLine);
                                            if (spawn != null)
                                            {
                                                currentGamePrefabSpawns.Add(spawn);
                                                string spawnMsg = $"{spawn.PlayerName} spawned prefab {spawn.PrefabName}";
                                                logEntries.Add((spawn.Timestamp, spawnMsg));
                                            }
                                        }
                                        else if (nextLine.Contains("\"ItemName\""))
                                        {
                                            var itemEquip = JsonUtility.FromJson<ItemEquipLogEntry>(nextLine);
                                            if (itemEquip != null)
                                            {
                                                currentGameItemEquips.Add(itemEquip);
                                                string itemEquipMsg = $"{itemEquip.PlayerName} equipped {itemEquip.ItemName}";
                                                logEntries.Add((itemEquip.Timestamp, itemEquipMsg));
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        PlayerBanMod.LogErrorStatic($"Error parsing log line {i}: {ex.Message}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            // This is a complete game session (GameStartTime followed by GameEndTime)
                            var startLog = JsonUtility.FromJson<GameStartLog>(line);
                            if (startLog != null)
                            {
                                PlayerBanMod.LogInfoStatic($"Found game start: {startLog.GameStartTime}");
                                gameStartTime = startLog.GameStartTime;
                                
                                // Find the corresponding GameEndTime
                                for (int i = lastGameSessionIndex + 1; i < lines.Length; i++)
                                {
                                    var nextLine = lines[i].Trim();
                                    if (string.IsNullOrEmpty(nextLine)) continue;
                                    
                                    if (nextLine.Contains("\"GameEndTime\""))
                                    {
                                        try
                                        {
                                            var endLog = JsonUtility.FromJson<GameEndLog>(nextLine);
                                            if (endLog != null)
                                            {
                                                PlayerBanMod.LogInfoStatic($"Found game end: {endLog.GameEndTime}");
                                                gameEndTime = endLog.GameEndTime;
                                                break;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            PlayerBanMod.LogErrorStatic($"Error parsing GameEndTime at line {i}: {ex.Message}");
                                        }
                                    }
                                }
                                
                                // Since this is a complete session, we need to look for individual log entries between start and end
                                // Search forward from this line to find kills, spells, and prefab spawns
                                for (int i = lastGameSessionIndex + 1; i < lines.Length; i++)
                                {
                                    var nextLine = lines[i].Trim();
                                    if (string.IsNullOrEmpty(nextLine)) continue;
                                    
                                    // Stop if we hit another game start/end entry
                                    if (nextLine.Contains("\"GameStartTime\"") || nextLine.Contains("\"GameEndTime\""))
                                        break;
                                    
                                    try
                                    {
                                        // Try to parse as different entry types
                                        if (nextLine.Contains("\"KillerName\""))
                                        {
                                            var kill = JsonUtility.FromJson<KillLogEntry>(nextLine);
                                            if (kill != null)
                                            {
                                                currentGameKills.Add(kill);
                                                
                                                // Store the message with original timestamp for later display
                                                string killMsg = kill.KillerName == "Environment" 
                                                    ? $"{kill.VictimName} died from {kill.CauseOfDeath}"
                                                    : $"{kill.KillerName} killed {kill.VictimName} with {kill.CauseOfDeath}";
                                                logEntries.Add((kill.Timestamp, killMsg));
                                            }
                                        }
                                        else if (nextLine.Contains("\"CasterName\""))
                                        {
                                            var spell = JsonUtility.FromJson<SpellLogEntry>(nextLine);
                                            if (spell != null)
                                            {
                                                currentGameSpells.Add(spell);
                                                
                                                // Store the message with original timestamp for later display
                                                string spellMsg = spell.SpellLevel > 0 
                                                    ? $"{spell.CasterName} casted {spell.SpellName} at level {spell.SpellLevel}"
                                                    : $"{spell.CasterName} casted {spell.SpellName}";
                                                logEntries.Add((spell.Timestamp, spellMsg));
                                            }
                                        }
                                        else if (nextLine.Contains("\"PrefabName\""))
                                        {
                                            var spawn = JsonUtility.FromJson<PrefabSpawnLogEntry>(nextLine);
                                            if (spawn != null)
                                            {
                                                currentGamePrefabSpawns.Add(spawn);
                                                string spawnMsg = $"{spawn.PlayerName} spawned prefab {spawn.PrefabName}";
                                                logEntries.Add((spawn.Timestamp, spawnMsg));
                                            }
                                        }
                                        else if (nextLine.Contains("\"ItemName\""))
                                        {
                                            var itemEquip = JsonUtility.FromJson<ItemEquipLogEntry>(nextLine);
                                            if (itemEquip != null)
                                            {
                                                currentGameItemEquips.Add(itemEquip);
                                                string itemEquipMsg = $"{itemEquip.PlayerName} equipped {itemEquip.ItemName}";
                                                logEntries.Add((itemEquip.Timestamp, itemEquipMsg));
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        PlayerBanMod.LogErrorStatic($"Error parsing log line {i}: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        PlayerBanMod.LogErrorStatic($"Error parsing game session line {lastGameSessionIndex}: {ex.Message}");
                    }
                    
                    // Now display everything in the correct order
                    if (!string.IsNullOrEmpty(gameStartTime) && !string.IsNullOrEmpty(gameEndTime))
                    {
                        // First, show the game time range
                        string gameTimeMessage = isIncompleteSession 
                            ? $"[{gameStartTime}] Game: {gameStartTime} to {gameEndTime} (incomplete session)"
                            : $"[{gameStartTime}] Game: {gameStartTime} to {gameEndTime}";
                        
                        BanUIManager.AddLogMessage(gameTimeMessage);
                        
                        // Then show all the individual log entries in chronological order
                        foreach (var entry in logEntries.OrderBy(e => e.timestamp))
                        {
                            BanUIManager.AddLogMessage($"[{entry.timestamp}] {entry.message}");
                        }
                    }
                    
                    PlayerBanMod.LogInfoStatic($"Loaded previous game session: {currentGameKills.Count} kills, {currentGameSpells.Count} spells, {currentGamePrefabSpawns.Count} prefab spawns, {currentGameItemEquips.Count} item equips");
                }
                else
                {
                    PlayerBanMod.LogInfoStatic("No game session found in log file");
                    BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] No previous game session found");
                }
                
                // Log the first few lines to help debug parsing issues
                if (lines.Length > 0)
                {
                    PlayerBanMod.LogInfoStatic($"First line of log file: {lines[0]}");
                    if (lines.Length > 1)
                    {
                        PlayerBanMod.LogInfoStatic($"Second line of log file: {lines[1]}");
                    }
                }
            }
            catch (Exception ex)
            {
                PlayerBanMod.LogErrorStatic($"Error loading previous logs: {ex.Message}");
                BanUIManager.AddLogMessage($"[{DateTime.Now:HH:mm:ss}] Error loading previous logs: {ex.Message}");
            }
        }

        public List<KillLogEntry> GetCurrentGameKills() => new List<KillLogEntry>(currentGameKills);
        public List<SpellLogEntry> GetCurrentGameSpells() => new List<SpellLogEntry>(currentGameSpells);
        public List<PrefabSpawnLogEntry> GetCurrentGamePrefabSpawns() => new List<PrefabSpawnLogEntry>(currentGamePrefabSpawns);
        public List<ItemEquipLogEntry> GetCurrentGameItemEquips() => new List<ItemEquipLogEntry>(currentGameItemEquips);

        public List<KillLogEntry> GetPreviousGameKills() => currentGameKills;
        public List<SpellLogEntry> GetPreviousGameSpells() => currentGameSpells;
        public List<PrefabSpawnLogEntry> GetPreviousGamePrefabSpawns() => currentGamePrefabSpawns;
        public List<ItemEquipLogEntry> GetPreviousGameItemEquips() => currentGameItemEquips;

        public void ClearCurrentGameLogs()
        {
            currentGameKills.Clear();
            currentGameSpells.Clear();
            playerDeathTimes.Clear();
            playerSpellCastTimes.Clear();
            playerSpellWarningCooldowns.Clear();
            currentGamePrefabSpawns.Clear();
            currentGameItemEquips.Clear();
        }

        public void ClearPreviousGameLogs()
        {
            currentGameKills.Clear();
            currentGameSpells.Clear();
            currentGamePrefabSpawns.Clear();
            currentGameItemEquips.Clear();
        }

        public string GetGameSummary()
        {
            if (currentGameKills.Count == 0 && currentGameSpells.Count == 0)
                return "No activity recorded";

            var summary = new StringBuilder();
            summary.AppendLine($"Game Duration: {(DateTime.Now - gameStartTime).ToString(@"mm\:ss")}");
            summary.AppendLine($"Total Kills: {currentGameKills.Count}");
            summary.AppendLine($"Total Spells: {currentGameSpells.Count}");
            
            if (currentGameKills.Count > 0)
            {
                summary.AppendLine("\nKill Summary:");
                var killCounts = currentGameKills.GroupBy(k => k.KillerName)
                    .OrderByDescending(g => g.Count())
                    .Take(5);
                foreach (var kill in killCounts)
                {
                    summary.AppendLine($"  {kill.Key}: {kill.Count()} kills");
                }
            }

            if (currentGameSpells.Count > 0)
            {
                summary.AppendLine("\nSpell Summary:");
                var spellCounts = currentGameSpells.GroupBy(s => s.SpellName)
                    .OrderByDescending(g => g.Count())
                    .Take(5);
                foreach (var spell in spellCounts)
                {
                    summary.AppendLine($"  {spell.Key}: {spell.Count()} casts");
                }
            }

            return summary.ToString();
        }
    }

    // New classes for cleaner log format
    [Serializable]
    public class GameStartLog
    {
        public string GameStartTime;
    }

    [Serializable]
    public class GameEndLog
    {
        public string GameEndTime;
        public int TotalKills;
        public int TotalSpells;
    }

    [Serializable]
    public class KillLogEntry
    {
        public string Timestamp;
        public string KillerName;
        public string VictimName;
        public string CauseOfDeath;
        public string GameTime;
    }

    [Serializable]
    public class SpellLogEntry
    {
        public string Timestamp;
        public string CasterName;
        public string SpellName;
        public int SpellLevel;
        public string GameTime;
    }

    [System.Serializable]
    public class PrefabSpawnLogEntry
    {
        public string Timestamp;
        public string PlayerName;
        public string PrefabName;
        public string SpawnPosition;
        public string GameTime;
    }

    [System.Serializable]
    public class ItemEquipLogEntry
    {
        public string Timestamp;
        public string PlayerName;
        public string ItemName;
        public string GameTime;
    }

    // Keep this for backward compatibility but it's no longer used for saving
    [Serializable]
    public class GameLog
    {
        public string GameStartTime;
        public string GameEndTime;
        public KillLogEntry[] Kills;
        public SpellLogEntry[] Spells;
    }
}