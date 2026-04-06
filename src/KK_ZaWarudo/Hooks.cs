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
            try
            {
                var trav = Traverse.Create(__instance);
                var females = trav.Field("lstFemale").GetValue<List<ChaControl>>();
                var male = trav.Field("male").GetValue<ChaControl>();
                // male1 only exists in darkness builds; absent in vanilla KK. Guard via Traverse (no throw).
                var male1 = trav.Field("male1").GetValue<ChaControl>();
                var flags = __instance.flags;

                var extras = new List<ChaControl>();
                if (male1 != null) extras.Add(male1);

                Plugin.LogI($"Hook MapSameObjectDisable: females={females?.Count ?? -1} male={(male != null ? male.name : "null")} extraMales={extras.Count} flags={(flags != null ? "ok" : "null")}");
                TimeStopController.Bind(__instance, females, male, extras, flags);
            }
            catch (System.Exception e)
            {
                Plugin.LogE($"HSceneInitPost failed: {e}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "OnDestroy")]
        private static void HSceneEndPost()
        {
            Plugin.LogI("Hook HSceneProc.OnDestroy: unbinding.");
            TimeStopController.Unbind();
        }

        /// <summary>
        /// Re-apply freeze on partner-switch / position-change so the newly active
        /// female does not animate while time is stopped.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "ChangeAnimator")]
        private static void ChangeAnimatorPost()
        {
            if (TimeStopController.Instance != null && TimeStopController.Instance.IsFrozen)
            {
                Plugin.LogI("Hook ChangeAnimator (frozen): re-pinning new animators.");
                TimeStopController.Instance.ReapplyIfFrozen();
            }
        }
    }
}
