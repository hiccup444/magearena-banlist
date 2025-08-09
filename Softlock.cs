using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace PlayerBanMod
{
    public static class Softlock
    {
        private static ManualLogSource logger;
        private static MonoBehaviour coroutineHost;
        private static readonly Dictionary<string, Coroutine> steamIdToCoroutine = new Dictionary<string, Coroutine>();

        public static void Initialize(ManualLogSource logSource, MonoBehaviour host)
        {
            logger = logSource;
            coroutineHost = host;
        }

        public static void Start(string steamId, string playerName)
        {
            if (coroutineHost == null)
            {
                return;
            }

            Stop(steamId);
            try
            {
                var routine = coroutineHost.StartCoroutine(SoftlockLoop(steamId, playerName));
                steamIdToCoroutine[steamId] = routine;
                logger?.LogWarning($"[Softlock] Started softlock on {playerName} (Steam ID: {steamId})");
            }
            catch (Exception e)
            {
                logger?.LogError($"[Softlock] Failed to start for {playerName}: {e.Message}");
            }
        }

        public static void Stop(string steamId)
        {
            if (coroutineHost == null) return;
            if (steamIdToCoroutine.TryGetValue(steamId, out var routine))
            {
                try
                {
                    coroutineHost.StopCoroutine(routine);
                }
                catch {}
                steamIdToCoroutine.Remove(steamId);
                logger?.LogInfo($"[Softlock] Stopped softlock for Steam ID: {steamId}");
            }
        }

        private static IEnumerator SoftlockLoop(string steamId, string playerName)
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                try
                {
                    var target = FindPlayerMovementByName(playerName);
                    if (target != null)
                    {
                        try
                        {
                            MethodInfo frogRpcMethod = typeof(PlayerMovement).GetMethod("FrogRpc", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (frogRpcMethod != null)
                            {
                                frogRpcMethod.Invoke(target, null);
                            }
                        }
                        catch (Exception e)
                        {
                            logger?.LogDebug($"[Softlock] FrogRpc invoke error for {playerName}: {e.Message}");
                        }

                        // Freeze via CallSummonIceBox (level 1, no owner)
                        try
                        {
                            target.CallSummonIceBox(1, null);
                        }
                        catch (Exception e)
                        {
                            logger?.LogDebug($"[Softlock] CallSummonIceBox error for {playerName}: {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    logger?.LogDebug($"[Softlock] Loop error for {playerName}: {e.Message}");
                }

                yield return wait;
            }
        }

        private static PlayerMovement FindPlayerMovementByName(string playerName)
        {
            try
            {
                var allPlayers = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
                foreach (var player in allPlayers)
                {
                    if (player != null && !string.IsNullOrEmpty(player.playername))
                    {
                        if (player.playername == playerName)
                        {
                            return player;
                        }
                    }
                }
            }
            catch {}
            return null;
        }

        public static void FreezePlayer(GameObject playerObject)
        {
            if (playerObject == null) return;
            var targetPlayerMovement = playerObject.GetComponent<PlayerMovement>();
            if (targetPlayerMovement == null) return;
            targetPlayerMovement.CallSummonIceBox(1, null);
        }
    }
}


