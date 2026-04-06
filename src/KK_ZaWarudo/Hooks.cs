using System;
using HarmonyLib;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Harmony patches still owned by us.
    ///
    /// HScene start/end is now handled by KKAPI's GameCustomFunctionController
    /// (see ZaWarudoController.cs) — we no longer patch MapSameObjectDisable / OnDestroy.
    ///
    /// What remains: ChangeAnimator postfix, which KKAPI does NOT surface as a callback.
    /// We need it to re-pin the freeze on the new active set when the player switches
    /// position / partner mid-freeze. Patched on both HSceneProc and (when present)
    /// VRHScene, mirroring KKAPI's own VR pattern in GameAPI.Hooks.SetupHooks.
    /// </summary>
    internal static class Hooks
    {
        public static void Apply(Harmony harmony)
        {
            // Vanilla HSceneProc.ChangeAnimator — declared via attribute below.
            harmony.PatchAll(typeof(Hooks));

            // VR variant: VRHScene type only exists in VR builds. Use AccessTools so
            // a non-VR install doesn't crash plugin load.
            try
            {
                var vrType = Type.GetType("VRHScene, Assembly-CSharp");
                if (vrType != null)
                {
                    var vrChange = AccessTools.Method(vrType, "ChangeAnimator");
                    if (vrChange != null)
                    {
                        var post = new HarmonyMethod(AccessTools.Method(typeof(Hooks), nameof(ChangeAnimatorPost)));
                        harmony.Patch(vrChange, postfix: post);
                        Plugin.LogI("Patched VRHScene.ChangeAnimator (VR build detected).");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.LogW($"VR ChangeAnimator patch skipped: {e.Message}");
            }
        }

        /// <summary>
        /// Re-apply freeze on partner-switch / position-change so the newly active
        /// female does not animate while time is stopped. HashSet caches inside
        /// TimeStopController dedupe, so repeated calls are safe.
        /// Used for both HSceneProc.ChangeAnimator (attribute below) and
        /// VRHScene.ChangeAnimator (manual patch in Apply).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HSceneProc), "ChangeAnimator")]
        public static void ChangeAnimatorPost()
        {
            if (TimeStopController.Instance != null && TimeStopController.Instance.IsFrozen)
            {
                Plugin.LogI("Hook ChangeAnimator (frozen): re-pinning new animators.");
                TimeStopController.Instance.ReapplyIfFrozen();
            }
        }
    }
}
