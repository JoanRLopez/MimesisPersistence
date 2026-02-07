using System;
using System.IO;
using HarmonyLib;
using ReluProtocol;

namespace MimesisPersistence.Patches
{
    [HarmonyPatch(typeof(PlatformMgr), nameof(PlatformMgr.Delete))]
    public static class PlatformMgrPatches
    {
        [HarmonyPostfix]
        public static void Postfix(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith("MMGameData", StringComparison.OrdinalIgnoreCase))
                    return;
                string slotStr = Path.GetFileNameWithoutExtension(fileName).Replace("MMGameData", "");
                if (int.TryParse(slotStr, out int slotId) && MMSaveGameData.CheckSaveSlotID(slotId, true))
                    MimesisSaveManager.DeleteMimesisData(slotId);
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[MimesisPersistence] PlatformMgr.Delete: {ex.Message}");
            }
        }
    }
}
