using System;
using HarmonyLib;
using MelonLoader;
using Mimic.Voice.SpeechSystem;

namespace MimesisPersistence.Patches
{
    /// <summary>
    /// Caches SpeechEvents from a player's archive before it is destroyed on disconnect.
    /// Without this, all voice events recorded during the session are lost when a player
    /// leaves because FishNet destroys the NetworkObject (SpeechEventArchive).
    /// 
    /// Uses Prefix (not Postfix) because we need the events BEFORE OnStopClient cleans up.
    /// </summary>
    [HarmonyPatch(typeof(SpeechEventArchive), nameof(SpeechEventArchive.OnStopClient))]
    public static class SpeechEventArchiveDisconnectPatches
    {
        [HarmonyPrefix]
        public static void Prefix(SpeechEventArchive __instance)
        {
            try
            {
                // Only the host needs to cache (host is the one who saves)
                if (!MimesisSaveManager.IsHost()) return;

                // Don't cache the host's own archive
                // (if host calls OnStopClient, the whole session is ending anyway)
                bool isLocal = false;
                try { isLocal = __instance.IsLocal; }
                catch { /* Player ref may be gone */ }

                if (isLocal) return;

                SpeechEventPoolManager.CacheEventsFromArchive(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MimesisPersistence] SpeechEventArchiveDisconnect cache error: {ex.Message}");
            }
        }
    }
}
