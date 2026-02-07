using System.Collections.Generic;
using HarmonyLib;
using ReluProtocol.Enum;

namespace MimesisPersistence.Patches
{
    [HarmonyPatch(typeof(MaintenanceRoom), nameof(MaintenanceRoom.SaveGameData))]
    public static class MaintenanceRoomPatches
    {
        [HarmonyPostfix]
        public static void Postfix(int saveSlotID, List<string> playerNames, bool isAutoSave, MsgErrorCode __result)
        {
            if (__result != MsgErrorCode.Success) return;
            if (!MimesisSaveManager.IsHost()) return;
            int slotId = isAutoSave ? 0 : saveSlotID;
            MimesisSaveManager.SaveMimesisData(slotId);
        }
    }
}
