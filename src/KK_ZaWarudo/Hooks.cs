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

            // F6: patch FaceListCtrl.OpenCtrl(ChaControl) manually to avoid adding
            // a Sirenix reference to the csproj (FaceListCtrl inherits from
            // Sirenix.OdinInspector.SerializedMonoBehaviour).
            try
            {
                var faceType = AccessTools.TypeByName("FaceListCtrl");
                if (faceType != null)
                {
                    var method = AccessTools.Method(faceType, "OpenCtrl", new[] { typeof(ChaControl) });
                    if (method != null)
                    {
                        var prefix = new HarmonyMethod(AccessTools.Method(typeof(Hooks), nameof(FaceListCtrlOpenCtrlPre)));
                        harmony.Patch(method, prefix: prefix);
                        Plugin.LogI("Patched FaceListCtrl.OpenCtrl(ChaControl) — face writes blocked during freeze.");
                    }
                    else Plugin.LogW("[F6] FaceListCtrl.OpenCtrl(ChaControl) method not found — face snapshot overwrite will continue.");
                }
                else Plugin.LogW("[F6] FaceListCtrl type not found — face snapshot overwrite will continue.");
            }
            catch (Exception e)
            {
                Plugin.LogW($"FaceListCtrl.OpenCtrl patch skipped: {e.Message}");
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
        /// F24 final piece — `FaceBlendShape.LateUpdate` is the LAST blendshape
        /// writer in the chain. It runs AFTER HMotionEyeNeck.Proc, FaceListCtrl.OpenCtrl,
        /// and HVoiceCtrl.OpenCtrl (which we already prefix-skip). It calls:
        ///   - BlinkCtrl.CalcBlink()
        ///   - EyesCtrl.CalcBlend(num)    → interpolates eye blendshape weights
        ///   - EyebrowCtrl.CalcBlend(num) → interpolates eyebrow weights
        ///   - MouthCtrl.CalcBlend(voiceValue) → interpolates mouth based on voice state
        ///
        /// The `voiceValue` (set via `SetVoiceVaule(float)`) reflects current audio
        /// intensity. During freeze, voice is muted but `voiceValue` may decay → mouth
        /// closes → ahegao lost. Without this prefix, face expressions still change
        /// during freeze based on `voiceValue` / speed thresholds.
        ///
        /// Prefix-skipping this method holds ALL face blendshapes at their freeze-moment
        /// weights. Combined with our existing blink-disable (ChangeEyesBlinkFlag) and
        /// face snapshot (step 4f), the face is now truly frozen.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FaceBlendShape), "LateUpdate")]
        public static bool FaceBlendShapeLateUpdatePre()
        {
            if (Frozen()) return false;
            return true;
        }

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
        /// F6 (real fix, round 4) — `HSceneProc.Update` calls `face.SafeProc(f => f.OpenCtrl(female))`
        /// every frame, which writes `ChangeEyesOpenMax` / `mouthCtrl.OpenMin` /
        /// `ChangeNipRate`. That bypasses our `HMotionEyeNeck.Proc` prefix and
        /// overwrites the face snapshot from step 4f.
        ///
        /// Attached via manual AccessTools patch in Apply() because FaceListCtrl
        /// inherits from Sirenix's `SerializedMonoBehaviour`, and we don't want
        /// to add a Sirenix reference to the csproj just to get the Type literal.
        /// </summary>
        public static bool FaceListCtrlOpenCtrlPre(ref bool __result)
        {
            if (Frozen()) { __result = true; return false; }
            return true;
        }

        /// <summary>
        /// Second face writer — `HVoiceCtrl.OpenCtrl(ChaControl, int)` is called
        /// from `HVoiceCtrl.Proc` (the top-level per-frame tick at line 140735)
        /// for each female, writing `ChangeEyesOpenMax` and `mouthCtrl.OpenMin`.
        /// Runs unconditionally after VoiceProc/ShortBreathProc/BreathProc — our
        /// existing prefixes on those don't block this loop. Separate prefix
        /// required for F6 completeness.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HVoiceCtrl), nameof(HVoiceCtrl.OpenCtrl), new System.Type[] { typeof(ChaControl), typeof(int) })]
        public static bool HVoiceCtrlOpenCtrlPre(ref bool __result)
        {
            if (Frozen()) { __result = true; return false; }
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // F11 / F22 — Trick B: Harmony Transpiler that rewrites HandCtrl.ClickAction's
        // `if (info.normalizedTime >= 1f)` gate to also pass when frozen.
        //
        // Why this and not Trick A (Animator.Play teleport):
        //   Trick A failed in playtest (commit 567ad43 log evidence). `Animator.Play()`
        //   with an explicit normalizedTime requires the animator to actually tick to
        //   apply the new position — and `animBody.speed = 0` halts that tick. The
        //   normalizedTime stayed at 0.000 forever after every Play() call. Verified
        //   via the [trickA] dump:
        //     [trickA] Play teleport: hand=204344 nLayer=4 normalizedTime 0.000 -> 0.000
        //
        // Trick B replaces the threshold value. The IL pattern for `>= 1f` ends in
        // `ldc.r4 1.0; <branch>`, where the branch jumps OUT of the if-body when the
        // value is less than 1. We replace `ldc.r4 1.0` with a call to TrickBGate()
        // which returns -1 when frozen — making `normalizedTime >= -1` always true,
        // so the click→drag transition fires every frame during freeze. Mouse-up
        // then runs through KK's own DragAction → ForceFinish release path.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Threshold helper used by the ClickAction transpiler. Returns -1 when
        /// frozen (so the gate always passes), 1 otherwise (KK's normal behavior).
        /// </summary>
        public static float TrickBGate()
        {
            return Frozen() ? -1f : 1f;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(HandCtrl), "ClickAction")]
        public static System.Collections.Generic.IEnumerable<CodeInstruction> ClickActionTranspiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(typeof(Hooks), nameof(TrickBGate));
            var matcher = new CodeMatcher(instructions);

            // Match: call get_normalizedTime ; ldc.r4 1.0
            matcher.MatchForward(false,
                new CodeMatch(i => i.opcode == System.Reflection.Emit.OpCodes.Call
                                   && i.operand is System.Reflection.MethodInfo mi
                                   && mi.Name == "get_normalizedTime"),
                new CodeMatch(System.Reflection.Emit.OpCodes.Ldc_R4, 1f));

            if (matcher.IsInvalid)
            {
                Plugin.LogE("[trickB] ClickAction transpiler FAILED — could not find `get_normalizedTime; ldc.r4 1.0` pattern. Patch inactive; F11/F22 will be broken.");
                return matcher.InstructionEnumeration();
            }

            // matcher is at the get_normalizedTime instruction; advance to ldc.r4
            matcher.Advance(1);
            // Replace ldc.r4 1.0 with `call TrickBGate` (returns float, same stack effect)
            matcher.SetAndAdvance(System.Reflection.Emit.OpCodes.Call, helper);

            Plugin.LogI("[trickB] ClickAction normalizedTime gate transpiled successfully — grab during freeze enabled.");
            return matcher.InstructionEnumeration();
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
