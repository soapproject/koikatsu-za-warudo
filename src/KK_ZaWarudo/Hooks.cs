using System.Collections.Generic;
using HarmonyLib;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Harmony patches.
    /// MapSameObjectDisable is the canonical late-init hook used by KK_HSceneOptions
    /// (originally from KK_EyeShaking) — by then HSceneProc has populated lstFemale/male/flags.
    /// </summary>
    internal static class Hooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "MapSameObjectDisable")]
        private static void HSceneInitPost(HSceneProc __instance)
        {
            var trav = Traverse.Create(__instance);
            var females = trav.Field("lstFemale").GetValue<List<ChaControl>>();
            var male = trav.Field("male").GetValue<ChaControl>();
            var flags = __instance.flags;

            TimeStopController.Bind(__instance, females, male, flags);
            Plugin.Log.LogDebug("HScene bound to TimeStopController.");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "OnDestroy")]
        private static void HSceneEndPost()
        {
            TimeStopController.Unbind();
            Plugin.Log.LogDebug("HScene unbound.");
        }

        /// <summary>
        /// Re-apply freeze on partner-switch / position-change so the newly active
        /// female does not animate while time is stopped.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "ChangeAnimator")]
        private static void ChangeAnimatorPost()
        {
            TimeStopController.Instance?.ReapplyIfFrozen();
        }
    }
}
