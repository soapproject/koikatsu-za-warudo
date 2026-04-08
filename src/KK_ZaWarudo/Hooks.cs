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

        /// <summary>
        /// Mute the female voice queue while frozen. KK voice does NOT play through
        /// AudioSources under the ChaControl hierarchy — it goes through pooled
        /// AudioSources owned by Manager.Voice, scheduled by HVoiceCtrl coroutines
        /// that run independently of animator state. So freezing animBody.speed
        /// alone leaves the queue ticking.
        ///
        /// Following the KK_HSceneOptions ForceStopVoice/MuteAll pattern (Hooks.cs:120):
        /// short-circuit VoiceProc and BreathProc with prefix patches that return
        /// false when frozen, so no new voice / breath gets queued. Once unfrozen
        /// the patches just pass through.
        /// </summary>
        // Voice / breath stay muted slightly longer than the freeze: until our
        // Resume SFX (Exit + FemaleResume) finishes playing. Otherwise the game's
        // moan would kick in the instant _frozen flips false and overlap our SFX.
        // Face / eye / neck / SE in contrast use Frozen() and resume immediately
        // when the freeze ends (so the character visually comes back to life).
        private static bool VoiceMuted()
        {
            var inst = TimeStopController.Instance;
            return inst != null && (inst.IsFrozen || inst.IsVoiceSuppressed);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HVoiceCtrl), "VoiceProc")]
        public static bool VoiceProcPre(ref bool __result)
        {
            if (VoiceMuted())
            {
                __result = false;
                return false; // skip original
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HVoiceCtrl), "BreathProc")]
        public static bool BreathProcPre()
        {
            if (VoiceMuted())
                return false; // skip original
            return true;
        }

        // Helper: shared frozen check (used by face/eye/neck/SE — these resume immediately).
        private static bool Frozen() => TimeStopController.Instance != null && TimeStopController.Instance.IsFrozen;

        /// <summary>
        /// Freeze female face/expression/eye/neck. HMotionEyeNeckFemale.Proc is called
        /// every frame from HSceneProc and reads animator state to drive eyes/mouth/
        /// eyebrow/tears/neck-look-target. Without this prefix the female blinks,
        /// changes expression, and turns her head to track the camera even while
        /// animBody.speed = 0.
        ///
        /// Also stays held when ClimaxFaceOnResume is enabled and we're inside the
        /// post-resume hold window — so the climax face we just injected via
        /// ChangeEyesPtn etc. doesn't get overwritten on the next frame.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HMotionEyeNeckFemale), "Proc")]
        public static bool EyeNeckFemaleProcPre(ref bool __result)
        {
            var inst = TimeStopController.Instance;
            if (inst != null && (inst.IsFrozen || inst.IsFaceHeld))
            {
                __result = true;
                return false;
            }
            return true;
        }

        // (Previous NeckLookControllerVer2.LateUpdate prefix patch removed —
        // we now flip the per-instance neckLookScript.skipCalc flag inside
        // TimeStopController.FreezeNeckLook(), which is the in-game way.
        // See call sites at line 61642 of decompiled assembly.)

        /// <summary>
        /// Eye tracking driver. EyeLookController.LateUpdate aims the iris/pupil
        /// at the look target every frame, independent of NeckLookControllerVer2.
        /// Without this prefix the female's eyes track the camera while her head
        /// stays frozen (F12 / Issue 2 in v0.1 playtest round 2).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EyeLookController), "LateUpdate")]
        public static bool EyeLookLateUpdatePre()
        {
            if (Frozen()) return false;
            return true;
        }

        /// <summary>
        /// Same for the male slot when it's a non-protagonist (male1 in darkness).
        /// We don't have a clean way here to tell which HMotionEyeNeckMale instance
        /// belongs to the protagonist vs male1 — so we conservatively freeze ALL
        /// male eye/neck Proc during freeze. The protagonist still controls his own
        /// camera/posture via player input; this only stops the auto eye-neck
        /// tracking, which is fine for "ZA WARUDO" feel.
        ///
        /// If we later observe the player feels too "frozen", revisit and route
        /// only male1 through the skip.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HMotionEyeNeckMale), "Proc")]
        public static bool EyeNeckMaleProcPre(ref bool __result)
        {
            if (Frozen()) { __result = true; return false; }
            return true;
        }

        /// <summary>
        /// Freeze HSceneProc-driven sound effects: HSeCtrl.Proc plays slap / body
        /// contact SE every frame based on animator state. Without this we'd hear
        /// random slap sounds during the time stop loop.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HSeCtrl), "Proc")]
        public static bool SeCtrlProcPre(ref bool __result)
        {
            if (Frozen()) { __result = true; return false; }
            return true;
        }

        /// <summary>
        /// F11 / F22 — Trick A: keep grab WORKING during freeze by teleporting
        /// the touch action layer's normalizedTime past 1 so ClickAction's gate
        /// (HandCtrl.cs:147570 `if (info.normalizedTime >= 1f)`) trips and the
        /// click→drag transition completes naturally. Drag mode then handles
        /// mouse-up via ForceFinish like normal.
        ///
        /// Verified offline:
        ///   - `setAllLayerWeight` iterates `for (int i=1; i<layerCount; i++)`
        ///     → layer 0 is master, layers 1+ are overlays.
        ///   - HandCtrl uses separate `useItems[i].layerAction.body` and
        ///     `layerIdle.body` indices, switching weight between them — both
        ///     are non-zero. So teleporting the action layer doesn't move the
        ///     master body pose.
        ///
        /// Postfix runs AFTER ClickAction has read the gate this frame. We
        /// teleport so the NEXT frame's ClickAction sees normalizedTime >= 1.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HandCtrl), "ClickAction")]
        public static void ClickActionPost(HandCtrl __instance)
        {
            if (!Frozen()) return;
            try
            {
                var trav = Traverse.Create(__instance);
                int actionUseItem = trav.Field("actionUseItem").GetValue<int>();
                if (actionUseItem < 0) return;
                var useItems = trav.Field("useItems").GetValue<System.Array>();
                if (useItems == null || actionUseItem >= useItems.Length) return;
                var useItem = useItems.GetValue(actionUseItem);
                if (useItem == null) return;

                // Pull the LayerInfo + nLayer the same way ClickAction does
                var layer = Traverse.Create(useItem).Field("layer").GetValue();
                if (layer == null) return;
                var layerActions = Traverse.Create(layer).Field("layerActions").GetValue<System.Array>();
                if (layerActions == null || layerActions.Length < 2) return;
                var actionLayer1 = layerActions.GetValue(1);
                if (actionLayer1 == null) return;

                var female = trav.Field("female").GetValue<ChaControl>();
                var flags = trav.Field("flags").GetValue<HFlag>();
                if (female == null || female.animBody == null || flags == null) return;
                bool isFront = flags.nowAnimationInfo.paramFemale.lstFrontAndBehind[0] == 0;
                var bodyLayer = isFront
                    ? Traverse.Create(actionLayer1).Field("front").GetValue()
                    : Traverse.Create(actionLayer1).Field("back").GetValue();
                int nLayer = Traverse.Create(bodyLayer).Field("body").GetValue<int>();
                if (nLayer <= 0) return; // safety: never touch layer 0

                var info = female.animBody.GetCurrentAnimatorStateInfo(nLayer);
                if (info.normalizedTime < 1f)
                {
                    // Teleport this single layer's state to normalizedTime = 1.
                    // animBody.speed = 0 means the animator clock is paused, but
                    // Animator.Play() with explicit normalizedTime forces position.
                    female.animBody.Play(info.fullPathHash, nLayer, 1f);
                }
            }
            catch (System.Exception e) { Plugin.LogW($"[trickA] ClickActionPost: {e.Message}"); }
        }

        /// <summary>
        /// Freeze the SIMULATION (not just the visuals). HSonyu/HHoushi/HAibu.Proc
        /// runs every frame, recomputes flags.speedCalc from Time.deltaTime, and
        /// calls flags.FemaleGaugeUp / MaleGaugeUp to tick the pleasure gauge.
        /// Setting speedCalc=0 once at freeze is futile because the next frame
        /// overwrites it.
        ///
        /// Playtest evidence (gauge dump from current build):
        ///   step2 speedCalc 0.45 -> 0  gaugeFemale=35.0  ← we set 0
        ///   [gauge] f=36.3  speedCalc=0.49  frozen=True  ← 1s later, both grew
        ///   ... continues to climb to 99.0 over 17 seconds while frozen ...
        ///
        /// Fix: prefix-skip both gauge updaters when frozen. Simulation still ticks
        /// (state machines run, animator advances when unfrozen) but the gauge value
        /// stays put. Resume() then injects the explicit Accumulated/Instant delta
        /// directly, which is exactly the v0.1 spec promise.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HFlag), nameof(HFlag.FemaleGaugeUp))]
        public static bool FemaleGaugeUpPre()
        {
            if (Frozen()) return false;
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HFlag), nameof(HFlag.MaleGaugeUp))]
        public static bool MaleGaugeUpPre()
        {
            if (Frozen()) return false;
            return true;
        }
    }
}
