using System.Collections.Generic;
using HarmonyLib;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Harmony patches that capture HSceneProc state so TimeStopController has
    /// the references it needs (female/male ChaControls, HFlag, etc).
    /// </summary>
    internal static class Hooks
    {
        [HarmonyPostfix, HarmonyPatch(typeof(HSceneProc), nameof(HSceneProc.SetStartVoice))]
        private static void OnHSceneStart(HSceneProc __instance)
        {
            // SetStartVoice runs once after HSceneProc has populated lstFemale/male.
            var females = (List<ChaControl>)AccessTools
                .Field(typeof(HSceneProc), "lstFemale").GetValue(__instance);
            var male = (ChaControl)AccessTools
                .Field(typeof(HSceneProc), "male").GetValue(__instance);
            var flags = (HFlag)AccessTools
                .Field(typeof(HSceneProc), "flags").GetValue(__instance);

            TimeStopController.Bind(__instance, females, male, flags);
            Plugin.Logger.LogDebug("HScene bound to TimeStopController.");
        }

        [HarmonyPostfix, HarmonyPatch(typeof(HSceneProc), "OnDestroy")]
        private static void OnHSceneEnd()
        {
            TimeStopController.Unbind();
            Plugin.Logger.LogDebug("HScene unbound from TimeStopController.");
        }
    }
}
