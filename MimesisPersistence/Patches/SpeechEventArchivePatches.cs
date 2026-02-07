using System;
using System.Collections.Generic;
using HarmonyLib;
using Mimic.Voice.SpeechSystem;
using ReluProtocol;

namespace MimesisPersistence.Patches
{
    /// <summary>
    /// Runs for ALL SpeechEventArchive instances (not just local).
    /// Uses SpeechEventPoolManager for 3-state event distribution:
    ///   PENDING  -> waiting for matching archive
    ///   INJECTED -> matched and added to correct archive
    ///   FALLBACK -> manually forced to local archive via debug tool
    /// </summary>
    [HarmonyPatch(typeof(SpeechEventArchive), "OnStartClient")]
    public static class SpeechEventArchivePatches
    {
        // Track which slot we've loaded the pool for (to avoid reloading)
        private static int _poolLoadedForSlot = -999;

        [HarmonyPostfix]
        public static void Postfix(SpeechEventArchive __instance)
        {
            try
            {
                // Only the host manages event injection
                if (!MimesisSaveManager.IsHost()) return;

                int slotId = MimesisSaveManager.GetCurrentSaveSlotId();
                if (!MMSaveGameData.CheckSaveSlotID(slotId, true)) return;

                // Load the pool once per slot (idempotent)
                if (slotId != _poolLoadedForSlot)
                {
                    _poolLoadedForSlot = slotId;

                    // Always reset when switching slots to clear stale data
                    // (disconnected cache, pool, mappings from previous slot)
                    SpeechEventPoolManager.Reset();

                    if (MimesisSaveManager.HasMimesisData(slotId))
                    {
                        SpeechEventPoolManager.LoadForSlot(slotId);
                    }
                }

                // Store reference to local archive for fallback
                if (__instance.IsLocal)
                {
                    SpeechEventPoolManager.SetLocalArchive(__instance);
                }

                string playerId = __instance.PlayerId;
                long playerUID = __instance.PlayerUID;
                bool isLocal = __instance.IsLocal;

                var eventsList = __instance.events;
                if (eventsList == null) return;

                float currentTime = SpeechEventPoolManager.GetCurrentSessionTime();

                // Build set of existing IDs to avoid duplicates (shared across both sources)
                var seenIds = new HashSet<long>();
                for (int i = 0; i < eventsList.Count; i++)
                    seenIds.Add(eventsList[i].Id);

                int totalAdded = 0;

                // === Source 1: Pool from disk (events loaded from previous sessions) ===
                if (SpeechEventPoolManager.HasPending())
                {
                    List<SpeechEvent> claimed = SpeechEventPoolManager.ClaimEventsForArchive(playerId, playerUID, isLocal, __instance);
                    if (claimed != null && claimed.Count > 0)
                    {
                        foreach (SpeechEvent ev in claimed)
                        {
                            if (ev == null || seenIds.Contains(ev.Id)) continue;
                            SpeechEventPoolManager.FixEventTiming(ev, currentTime);
                            eventsList.Add(ev);
                            seenIds.Add(ev.Id);
                            totalAdded++;
                        }
                    }
                }

                // === Source 2: Disconnected cache (player left and reconnected mid-session) ===
                if (SpeechEventPoolManager.DisconnectedCacheCount > 0)
                {
                    List<SpeechEvent> reclaimed = SpeechEventPoolManager.ClaimDisconnectedEventsForArchive(playerId, playerUID, isLocal);
                    if (reclaimed != null && reclaimed.Count > 0)
                    {
                        foreach (SpeechEvent ev in reclaimed)
                        {
                            if (ev == null || seenIds.Contains(ev.Id)) continue;
                            SpeechEventPoolManager.FixEventTiming(ev, currentTime);
                            eventsList.Add(ev);
                            seenIds.Add(ev.Id);
                            totalAdded++;
                        }

                        MelonLoader.MelonLogger.Msg(
                            $"[MimesisPersistence] Restored {reclaimed.Count} events from disconnected cache " +
                            $"into archive PlayerId={playerId}");
                    }
                }

                if (totalAdded > 0)
                {
                    var counts = SpeechEventPoolManager.GetCounts();
                    MelonLoader.MelonLogger.Msg(
                        $"[MimesisPersistence] Injected {totalAdded} total events into archive " +
                        $"PlayerId={playerId} (time={currentTime:F1}) " +
                        $"[pool: {counts.pending}P/{counts.injected}I/{counts.fallback}F, " +
                        $"disconnected cache: {SpeechEventPoolManager.DisconnectedCacheCount}]");
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[MimesisPersistence] SpeechEventArchive inject: {ex.Message}");
            }
        }

        /// <summary>
        /// Inject a list of events into a specific archive's SyncList.
        /// Used by both the Postfix and the debug tool fallback.
        /// </summary>
        public static int InjectEventsIntoArchive(SpeechEventArchive archive, List<SpeechEvent> events)
        {
            if (archive == null || events == null || events.Count == 0) return 0;

            var eventsList = archive.events;
            if (eventsList == null) return 0;

            float currentTime = SpeechEventPoolManager.GetCurrentSessionTime();

            var seenIds = new HashSet<long>();
            for (int i = 0; i < eventsList.Count; i++)
                seenIds.Add(eventsList[i].Id);

            int added = 0;
            foreach (SpeechEvent ev in events)
            {
                if (ev == null || seenIds.Contains(ev.Id)) continue;

                SpeechEventPoolManager.FixEventTiming(ev, currentTime);

                eventsList.Add(ev);
                seenIds.Add(ev.Id);
                added++;
            }

            return added;
        }

        public static void ResetInjectedSlot()
        {
            _poolLoadedForSlot = -999;
            SpeechEventPoolManager.Reset();
        }
    }
}
