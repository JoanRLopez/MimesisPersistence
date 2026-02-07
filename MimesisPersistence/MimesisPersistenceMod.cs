using HarmonyLib;
using MelonLoader;
#if DEBUG
using UnityEngine;
#endif

[assembly: MelonInfo(typeof(MimesisPersistence.MimesisPersistenceMod), "MimesisPersistence", "0.3.0", "JoanR")]
[assembly: MelonGame(null, null)]

namespace MimesisPersistence
{
    public class MimesisPersistenceMod : MelonMod
    {
        private static HarmonyLib.Harmony _harmony;
        private const string HarmonyId = "MimesisPersistence";

#if DEBUG
        private DebugAudioTester _debugTester;
#endif

        public override void OnInitializeMelon()
        {
            try
            {
                _harmony = new HarmonyLib.Harmony(HarmonyId);
                _harmony.PatchAll(typeof(MimesisPersistenceMod).Assembly);
                LoggerInstance.Msg("Persistence enabled (host only). Patches applied.");

#if DEBUG
                _debugTester = new DebugAudioTester(LoggerInstance);
                LoggerInstance.Msg("[DEBUG] Audio Debug Tool ready. Press F9 to toggle.");
#endif
            }
            catch (System.Exception ex)
            {
                LoggerInstance.Error($"Failed to apply patches: {ex}");
            }
        }

        public override void OnUpdate()
        {
            // Process deferred PlayerName updates (events injected before PlayerId was set)
            SpeechEventPoolManager.ProcessDeferredUpdates();

#if DEBUG
            _debugTester?.HandleInput();
#endif
        }

#if DEBUG
        public override void OnGUI()
        {
            _debugTester?.DrawGUI();
        }
#endif

        public override void OnDeinitializeMelon()
        {
            _harmony?.UnpatchSelf();

#if DEBUG
            _debugTester?.Cleanup();
#endif
        }
    }
}
