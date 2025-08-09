using HarmonyLib;

namespace PlayerBanMod
{
    [HarmonyPatch(typeof(KickPlayersHolder), "AddToDict")]
    internal static class PlayerJoinPatch
    {
        private static void Postfix(KickPlayersHolder __instance, string name, string steamid)
        {
            try
            {
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
}


