using System;
using HarmonyLib;
using Mimic.Voice.SpeechSystem;

namespace MimesisPersistence.Patches
{
    /// <summary>
    /// Patches VoiceManager.GetRandomOtherSpeechEventArchive to fall back
    /// to the local archive when no other archives have events.
    /// This ensures hallucination voices work even when playing solo
    /// with FALLBACK events in the local archive.
    /// </summary>
    [HarmonyPatch(typeof(VoiceManager), "GetRandomOtherSpeechEventArchive")]
    public static class VoiceManagerHallucinationPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref SpeechEventArchive __result)
        {
            try
            {
                // Only intervene if the original method found nothing
                if (__result != null) return;

                // Get the local archive (stored by the injection patch)
                SpeechEventArchive local = SpeechEventPoolManager.GetLocalArchive();
                if (local == null) return;

                // Only use it if it has events in the random pool
                if (local.RandomPoolSize > 0)
                {
                    __result = local;
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[MimesisPersistence] Hallucination fallback: {ex.Message}");
            }
        }
    }
}
