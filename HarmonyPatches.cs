using HarmonyLib;
using System.Collections;
using UnityEngine;
using FishNet.Object;
using FishNet.Connection;
using FishNet.Transporting;
using FishNet.Serializing;

namespace PlayerBanMod
{
    [HarmonyPatch(typeof(KickPlayersHolder), "AddToDict")]
    internal static class PlayerJoinPatch
    {
        private static void Postfix(KickPlayersHolder __instance, string name, string steamid)
        {
            try
            {
                // Removed SteamID->ClientId mapping; no longer needed

                var mod = PlayerBanMod.Instance;
                if (mod != null && mod.IsCurrentHost())
                {
                    if (mod.IsPlayerHost(steamid))
                    {
                        PlayerBanMod.LogInfoStatic($"Host player {name} (Steam ID: {steamid}) joined - host is immune to bans and kicks");
                        return;
                    }

                    if (mod.IsBanned(steamid))
                    {
                        PlayerBanMod.LogInfoStatic($"Banned player {name} (Steam ID: {steamid}) joined - kicking them immediately");
                        mod.KickBannedNow(steamid, name);
                    }
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in player join patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(PlayerRespawnManager), "summonDeathMessage")]
    internal static class KillFeedPatch
    {
        private static void Postfix(PlayerRespawnManager __instance, string name, string causeofdeath, string killer)
        {
            try
            {
                // Log the kill to our kill logger
                // Parameters: name = victim, causeofdeath = cause, killer = killer
                KillLogger.Instance.LogKill(killer, name, causeofdeath);
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in kill feed patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(PageController), "CastSpellServer")]
    internal static class PageControllerSpellPatch
    {
        private static void Postfix(PageController __instance, GameObject ownerobj, Vector3 fwdVector, int level, Vector3 spawnpos)
        {
            try
            {
                if (ownerobj != null)
                {
                    var playerMovement = ownerobj.GetComponent<PlayerMovement>();
                    if (playerMovement != null)
                    {
                        string casterName = playerMovement.playername ?? "Unknown";
                        string spellName = GetSpellNameFromPrefab(__instance.spellprefab);
                        
                        KillLogger.Instance.LogSpell(casterName, spellName, level);
                    }
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in PageController spell patch: {e.Message}");
            }
        }

        private static string GetSpellNameFromPrefab(GameObject spellPrefab)
        {
            if (spellPrefab == null) return "Unknown Spell";
            
            // Try to get spell name from various components
            if (spellPrefab.GetComponent<MagicMissleController>() != null) return "Magic Missile";
            if (spellPrefab.GetComponent<FrostBoltController>() != null) return "Frost Bolt";
            if (spellPrefab.GetComponent<FireballController>() != null) return "Fireball";
            if (spellPrefab.GetComponent<DarkBlastController>() != null) return "Dark Blast";
            if (spellPrefab.GetComponent<WispController>() != null) return "Wisp";
            if (spellPrefab.GetComponent<BlinkSpellController>() != null) return "Blink";
            if (spellPrefab.GetComponent<LightningBoltSpellController>() != null) return "Lightning Bolt";
            if (spellPrefab.GetComponent<HolyLightSpell>() != null) return "Holy Light";
            
            // Fallback to prefab name
            return spellPrefab.name.Replace("Controller", "").Replace("_REFERENCE", "");
        }
    }

    [HarmonyPatch(typeof(MageBookController), "ShootMagicMissleServer")]
    internal static class MageBookMagicMissilePatch
    {
        private static void Postfix(MageBookController __instance, GameObject ownerobj, Vector3 fwdVector, GameObject target, int level)
        {
            try
            {
                if (ownerobj != null)
                {
                    var playerMovement = ownerobj.GetComponent<PlayerMovement>();
                    if (playerMovement != null)
                    {
                        string casterName = playerMovement.playername ?? "Unknown";
                        KillLogger.Instance.LogSpell(casterName, "Magic Missile", level);
                    }
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in MageBook magic missile patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MageBookController), "ShootFireballServer")]
    internal static class MageBookFireballPatch
    {
        private static void Postfix(MageBookController __instance, GameObject ownerobj, Vector3 fwdVector, int level, Vector3 spawnpos)
        {
            try
            {
                if (ownerobj != null)
                {
                    var playerMovement = ownerobj.GetComponent<PlayerMovement>();
                    if (playerMovement != null)
                    {
                        string casterName = playerMovement.playername ?? "Unknown";
                        KillLogger.Instance.LogSpell(casterName, "Fireball", level);
                    }
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in MageBook fireball patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MageBookController), "ShootFrostboltServer")]
    internal static class MageBookFrostBoltPatch
    {
        private static void Postfix(MageBookController __instance, GameObject ownerobj, Vector3 fwdVector, int level)
        {
            try
            {
                if (ownerobj != null)
                {
                    var playerMovement = ownerobj.GetComponent<PlayerMovement>();
                    if (playerMovement != null)
                    {
                        string casterName = playerMovement.playername ?? "Unknown";
                        KillLogger.Instance.LogSpell(casterName, "Frost Bolt", level);
                    }
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in MageBook frost bolt patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MageBookController), "WormServer")]
    internal static class MageBookWormPatch
    {
        private static void Postfix(MageBookController __instance, int level, Vector3 pos)
        {
            try
            {
                // For MageBook spells, we need to find the owner from the GameObject
                var playerMovement = __instance.GetComponentInParent<PlayerMovement>();
                if (playerMovement != null)
                {
                    string casterName = playerMovement.playername ?? "Unknown";
                    KillLogger.Instance.LogSpell(casterName, "Worm", level);
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in MageBook worm patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MageBookController), "HoleServer")]
    internal static class MageBookHolePatch
    {
        private static void Postfix(MageBookController __instance, int level, Vector3 pos)
        {
            try
            {
                // For MageBook spells, we need to find the owner from the GameObject
                var playerMovement = __instance.GetComponentInParent<PlayerMovement>();
                if (playerMovement != null)
                {
                    string casterName = playerMovement.playername ?? "Unknown";
                    KillLogger.Instance.LogSpell(casterName, "Hole", level);
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in MageBook hole patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(PlayerMovement), "RespawnPlayer")]
    internal static class PlayerRespawnPatch
    {
        private static void Postfix(PlayerMovement __instance)
        {
            try
            {
                if (__instance.playername != null)
                {
                    string playerName = __instance.playername;
                    KillLogger.Instance.LogRespawnWarning(playerName);
                }
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error in PlayerRespawn patch: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NetInteractionManager), "RpcReader___Server_SpawnBoulderServer_208080042")]
    internal static class NetInteractionManagerSpawnBoulderPatch
    {
        private static void Postfix(NetInteractionManager __instance, PooledReader PooledReader0, Channel channel, NetworkConnection conn)
        {
            try
            {
                // Get the spawn point and prefab from the reader
                Vector3 spawnPoint = PooledReader0.ReadVector3();
                GameObject prefab = PooledReader0.ReadGameObject();
                
                // Debug logging for prefab information
                PlayerBanMod.LogInfoStatic($"Prefab spawn detected - Prefab object: {(prefab != null ? "Valid" : "NULL")}");
                if (prefab != null)
                {
                    PlayerBanMod.LogInfoStatic($"Prefab name: '{prefab.name}', Prefab type: {prefab.GetType().Name}");
                }
                
                // Get the player who initiated this spawn from the connection
                string playerName = "Unknown";
                
                if (conn != null && conn.FirstObject != null)
                {
                    // Try to get the player name from the connection's first object
                    var playerMovement = conn.FirstObject.GetComponent<PlayerMovement>();
                    if (playerMovement != null)
                    {
                        playerName = playerMovement.playername ?? "Unknown";
                    }
                    else
                    {
                        // Fallback: look for any PlayerMovement component in the connection's objects
                        var networkObjects = conn.Objects;
                        foreach (var netObj in networkObjects)
                        {
                            if (netObj != null)
                            {
                                var pm = netObj.GetComponent<PlayerMovement>();
                                if (pm != null)
                                {
                                    playerName = pm.playername ?? "Unknown";
                                    break;
                                }
                            }
                        }
                    }
                }
                
                // Get the prefab name for logging with better fallback logic
                string prefabName = "Unknown";
                if (prefab != null)
                {
                    if (!string.IsNullOrEmpty(prefab.name))
                    {
                        prefabName = prefab.name;
                    }
                    else
                    {
                        // Try to get name from other sources
                        var networkObject = prefab.GetComponent<NetworkBehaviour>();
                        if (networkObject != null)
                        {
                            prefabName = networkObject.GetType().Name.Replace("Controller", "").Replace("_REFERENCE", "");
                        }
                        else
                        {
                            // Last resort: use the GameObject type name
                            prefabName = prefab.GetType().Name.Replace("Controller", "").Replace("_REFERENCE", "");
                        }
                    }
                }
                
                PlayerBanMod.LogInfoStatic($"Final prefab name resolved: '{prefabName}'");
                
                // Log the prefab spawn
                KillLogger.Instance.LogPrefabSpawn(playerName, prefabName, spawnPoint);
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error logging prefab spawn: {e.Message}\nStack trace: {e.StackTrace}");
            }
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), "SetObjectInHandServer")]
    internal static class PlayerInventoryEquipItemPatch
    {
        private static void Postfix(PlayerInventory __instance, GameObject obj)
        {
            try
            {
                // Only log if we're the server and the object is valid
                if (!__instance.IsServerInitialized || obj == null)
                    return;

                // Get the player name
                string playerName = "Unknown";
                var playerMovement = __instance.GetComponent<PlayerMovement>();
                if (playerMovement != null)
                {
                    playerName = playerMovement.playername ?? "Unknown";
                }

                // Get the item name/ID
                string itemName = "Unknown";
                var itemInteraction = obj.GetComponent<IItemInteraction>();
                if (itemInteraction != null)
                {
                    int itemId = itemInteraction.GetItemID();
                    itemName = $"Item ID {itemId}";
                    
                    // Try to get a more descriptive name from the object
                    if (!string.IsNullOrEmpty(obj.name))
                    {
                        itemName = obj.name.Replace("_REFERENCE", "").Replace("(Clone)", "").Trim();
                    }
                }

                // Log the item equip
                KillLogger.Instance.LogItemEquip(playerName, itemName);
                
                PlayerBanMod.LogInfoStatic($"Item equip logged: {playerName} equipped {itemName}");
            }
            catch (System.Exception e)
            {
                PlayerBanMod.LogErrorStatic($"Error logging item equip: {e.Message}\nStack trace: {e.StackTrace}");
            }
        }
    }
}