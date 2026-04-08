using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Owns the freeze/resume logic per docs/SPEC.md.
    /// Strategy:
    ///   1. Animator.speed = 0 on every frozen subject's animBody + animTongueEx.
    ///      (ChaInfo only exposes those two Animators — verified against
    ///      Koikatu_Data/Managed/Assembly-CSharp.dll. There is no animFace.)
    ///      Male protagonist (HSceneProc.male) is left untouched.
    ///   2. HFlag.speedCalc = 0 to halt the gauge tick.
    ///   3. Cache + disable DynamicBone / DynamicBone_Ver02 components on subjects.
    ///   4. Pause every ParticleSystem under the HScene root.
    ///   4b. Stop in-flight voice via Manager.Voice.Instance.Stop(transVoiceMouth).
    ///   4c. Pause every AudioSource on every frozen subject (covers 3P/4P
    ///       and any voice/SE that 4b missed).
    ///   5. Play Enter SFX. On resume play Resume SFX and inject gauge.
    /// All mutated state is cached for clean restore.
    /// </summary>
    internal class TimeStopController
    {
        public static TimeStopController Instance { get; private set; }

        // Could be HSceneProc OR VRHScene (KKAPI dispatches both into the same callback).
        // We only need .gameObject / GetComponentsInChildren on it, so MonoBehaviour suffices.
        private MonoBehaviour _proc;
        private List<ChaControl> _females;
        private HandCtrl _hand0;
        private HandCtrl _hand1;
        private HVoiceCtrl _voice;
        public HVoiceCtrl Voice => _voice;
        private List<HActionBase> _lstProc;

        /// <summary>
        /// True when the player is actively interacting with the female (touching,
        /// grabbing, kissing). Used by AudioManager to gate the during-loop so it
        /// only plays while the player is actually doing something. Tolerates
        /// missing hand controllers (returns false).
        /// </summary>
        public bool IsPlayerActing
        {
            get
            {
                try
                {
                    if (_hand0 != null && (_hand0.IsItemTouch() || _hand0.IsAction())) return true;
                    if (_hand1 != null && (_hand1.IsItemTouch() || _hand1.IsAction())) return true;
                }
                catch { }
                return false;
            }
        }
        // _male = protagonist, intentionally untouched per spec.
        // _extraMales currently sourced from HSceneProc.male1 only (darkness mode).
        // Vanilla HSceneProc has exactly two ChaControl male slots (male, male1) —
        // verified against Koikatu_Data/Managed/Assembly-CSharp.dll.
        private List<ChaControl> _extraMales;
        private HFlag _flags;

        /// <summary>Everyone who SHOULD be frozen: females + non-protagonist males.</summary>
        private IEnumerable<ChaControl> FrozenSubjects()
        {
            if (_females != null) foreach (var f in _females) if (f != null) yield return f;
            if (_extraMales != null) foreach (var m in _extraMales) if (m != null) yield return m;
        }

        /// <summary>First non-null female, or null. Used by Plugin's state-machine dump.</summary>
        public ChaControl GetFirstFemale()
        {
            if (_females == null) return null;
            foreach (var f in _females) if (f != null) return f;
            return null;
        }

        /// <summary>
        /// F18 / lead A: escape hatch when an orgasm sequence is stuck during freeze.
        /// Implements the KK_HSceneOptions ManualOrgasm pattern (AnimationToggle.cs:218):
        ///   1. Force voice slot states to `breath` so IsCheckVoicePlay() returns true
        ///   2. Reflectively invoke LoopProc(true) twice on the active HActionBase
        ///      to advance the state machine through the orgasm sequence
        ///
        /// Should only be called when frozen AND `flags.finish != none` — using it
        /// otherwise just runs LoopProc twice for no reason.
        ///
        /// Returns the number of LoopProc invocations (0 if nothing to do).
        /// </summary>
        public int UnblockStuckOrgasm()
        {
            if (_flags == null || _voice == null) return 0;
            if (_flags.finish == HFlag.FinishKind.none)
            {
                Plugin.LogI("[unblock] flags.finish=none — nothing stuck, skipping.");
                return 0;
            }
            try
            {
                // Spoof voice slots to breath state — IsCheckVoicePlay() will then return true
                if (_voice.nowVoices != null)
                {
                    for (int i = 0; i < _voice.nowVoices.Length; i++)
                        if (_voice.nowVoices[i] != null)
                            _voice.nowVoices[i].state = HVoiceCtrl.VoiceKind.breath;
                }

                // Find the active HActionBase that matches the current mode (HSonyu/HHoushi/HAibu/etc)
                var proc = ResolveActiveProc();
                if (proc == null)
                {
                    Plugin.LogW("[unblock] no active HActionBase resolved for mode=" + _flags.mode);
                    return 0;
                }

                // Invoke LoopProc(bool) reflectively. Two passes lets the state machine
                // settle the click intent and then transition. Pattern lifted from
                // KK_HSceneOptions.AnimationToggle.ManualOrgasm.
                var loopProcMethod = AccessTools.Method(proc.GetType(), "LoopProc", new System.Type[] { typeof(bool) });
                if (loopProcMethod == null)
                {
                    Plugin.LogW("[unblock] LoopProc(bool) not found on " + proc.GetType().Name);
                    return 0;
                }

                int invocations = 0;
                for (int i = 0; i < 2; i++)
                {
                    try { loopProcMethod.Invoke(proc, new object[] { true }); invocations++; }
                    catch (System.Exception e) { Plugin.LogW($"[unblock] LoopProc invoke #{i} threw: {e.Message}"); }
                }
                Plugin.LogI($"[unblock] {proc.GetType().Name}.LoopProc invoked {invocations}x, finish was {_flags.finish}");
                return invocations;
            }
            catch (System.Exception e)
            {
                Plugin.LogW($"[unblock] failed: {e.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Resolve which HActionBase subtype is active for the current mode.
        /// Pattern lifted from KK_HSceneOptions.AnimationToggle.FindProc.
        /// Returns null if mode is unrecognized or lstProc is empty.
        /// </summary>
        private HActionBase ResolveActiveProc()
        {
            if (_lstProc == null || _flags == null) return null;
            // Use System.Linq.OfType<T>() to scan lstProc for the matching subtype.
            // Cannot use a switch on _flags.mode + generics here because mode→type
            // mapping is one-to-one but the type list is not exhaustive in all builds.
            switch (_flags.mode)
            {
                case HFlag.EMode.sonyu:        return _lstProc.OfType<HSonyu>().FirstOrDefault();
                case HFlag.EMode.houshi:       return _lstProc.OfType<HHoushi>().FirstOrDefault();
                case HFlag.EMode.aibu:         return _lstProc.OfType<HAibu>().FirstOrDefault();
                case HFlag.EMode.lesbian:      return _lstProc.OfType<HLesbian>().FirstOrDefault();
                case HFlag.EMode.masturbation: return _lstProc.OfType<HMasturbation>().FirstOrDefault();
                // 3P / darkness modes use class names that may not exist on every KK build
                // — fall through to first-element grab to avoid build-time references.
                default:
                    return _lstProc.Count > 0 ? _lstProc[0] : null;
            }
        }

        private bool _frozen;
        public bool IsFrozen => _frozen;
        public HFlag Flags => _flags;
        private float _freezeStartTime;
        private float _lastToggleTime; // for hotkey debounce

        // Game voice/breath/SE stays muted until this timestamp (Time.realtimeSinceStartup).
        // Set to "now + ResumeSfx length" on Resume() so the game's moan doesn't kick
        // in while our Exit + FemaleResume SFX is still playing.
        // Voice prefix patches in Hooks.cs check IsFrozen || IsVoiceSuppressed.
        private float _voiceMuteUntil;
        public bool IsVoiceSuppressed => Time.realtimeSinceStartup < _voiceMuteUntil;

        // When ClimaxFaceOnResume is enabled, we inject a climax face on Resume()
        // and pin it (block HMotionEyeNeck.Proc from overwriting) until this
        // timestamp. Otherwise face resumes immediately as the user wants.
        private float _faceHoldUntil;
        public bool IsFaceHeld => Time.realtimeSinceStartup < _faceHoldUntil;

        // Caches — HashSet (not List) so ReapplyIfFrozen can be called repeatedly
        // (every partner switch / position change) without growing the cache unbounded
        // when the same Components are encountered again.
        private readonly Dictionary<Animator, float> _animSpeeds = new Dictionary<Animator, float>();
        private readonly HashSet<Behaviour> _disabledBones = new HashSet<Behaviour>();
        // F9: instead of HashSet<ParticleSystem> + Pause(true), we now toggle the
        // EmissionModule.enabled per system so existing particles keep simulating
        // (gravity still pulls fluid blobs to the ground) but no new particles spawn.
        private readonly Dictionary<ParticleSystem, bool> _suppressedEmission = new Dictionary<ParticleSystem, bool>();
        private readonly HashSet<AudioSource> _pausedAudio = new HashSet<AudioSource>();
        private readonly Dictionary<ChaControl, bool> _savedBlinkFlags = new Dictionary<ChaControl, bool>();
        // Issue 1+5: snapshot per-character neck-look state and head bone rotation,
        // restore on resume. Using neckLookScript.skipCalc (the in-game flag) plus
        // a Transform localRotation snapshot prevents the head snapping to a default
        // pose at the moment of freeze, and stops "head moves on its own" mid-freeze.
        private readonly Dictionary<NeckLookCalcVer2, bool> _savedSkipCalc = new Dictionary<NeckLookCalcVer2, bool>();
        private readonly Dictionary<Transform, Quaternion> _pinnedHeadRots = new Dictionary<Transform, Quaternion>();
        // F8: capture in-progress face per character on freeze and re-apply every
        // ReapplyIfFrozen call so an in-progress ahegao isn't reverted to default.
        private struct FaceState { public int eyes; public int mouth; public int eyebrow; public byte tears; public float eyesOpen; }
        private readonly Dictionary<ChaControl, FaceState> _savedFaces = new Dictionary<ChaControl, FaceState>();
        private float _savedSpeedCalc;
        private bool _savedAudioListenerPause;
        // Belt-and-braces gauge lock (paired with HFlag.FemaleGaugeUp/MaleGaugeUp prefix).
        // FemaleGaugeUp early-returns if lockGugeFemale is true (and the caller isn't
        // forcing). InjectGauge writes gaugeFemale directly so it bypasses this lock.
        private bool _savedLockFemale;
        private bool _savedLockMale;

        public static void Bind(MonoBehaviour proc, List<ChaControl> females, ChaControl male, List<ChaControl> extraMales, HFlag flags, HandCtrl hand0 = null, HandCtrl hand1 = null, HVoiceCtrl voice = null, List<HActionBase> lstProc = null)
        {
            // Defensive: if a previous HScene didn't tear down (BepInEx hot reload,
            // KKAPI re-init, double-fire of MapSameObjectDisable), drop the old
            // Instance cleanly so cached state from the previous scene is restored
            // before we lose the references.
            if (Instance != null)
            {
                Plugin.LogW("Bind called while previous Instance exists — forcing Unbind first.");
                Unbind();
            }
            // male = protagonist, untouched per spec.
            _ = male;
            Instance = new TimeStopController
            {
                _proc = proc,
                _females = females,
                _extraMales = extraMales,
                _flags = flags,
                _hand0 = hand0,
                _hand1 = hand1,
                _voice = voice,
                _lstProc = lstProc,
            };
        }

        public static void Unbind()
        {
            Instance?.Resume();
            try { AudioManager.Instance.StopAll(); } catch { }
            Instance = null;
        }

        public void Toggle()
        {
            if (_proc == null)
            {
                Plugin.LogI("Toggle ignored: not in HScene.");
                return;
            }
            // Debounce rapid presses to avoid SFX chopping, gauge spam, and
            // pointless cache churn (every Freeze() iterates GetComponentsInChildren).
            var now = Time.realtimeSinceStartup;
            var sinceLast = now - _lastToggleTime;
            if (sinceLast < Plugin.ToggleCooldown.Value)
            {
                Plugin.LogI($"Toggle debounced: {sinceLast:F2}s since last (cooldown={Plugin.ToggleCooldown.Value:F2}s)");
                return;
            }
            _lastToggleTime = now;

            if (_frozen) Resume();
            else Freeze();
        }

        public void Freeze()
        {
            if (_frozen || _proc == null)
            {
                Plugin.LogW($"Freeze ignored: frozen={_frozen} proc={(_proc != null)}");
                return;
            }
            _frozen = true;
            _freezeStartTime = Time.realtimeSinceStartup;
            Plugin.LogI($"=== FREEZE start === females={_females?.Count ?? -1}");

            // 1. Animator.speed = 0 on females
            FreezeFemaleAnimators();
            Plugin.LogI($"  step1 animators cached={_animSpeeds.Count}");

            // 2. HFlag.speedCalc + gauge locks (belt-and-braces with the GaugeUp prefixes)
            if (_flags != null)
            {
                _savedSpeedCalc = _flags.speedCalc;
                _flags.speedCalc = 0f;
                _savedLockFemale = _flags.lockGugeFemale;
                _savedLockMale = _flags.lockGugeMale;
                _flags.lockGugeFemale = true;
                _flags.lockGugeMale = true;
                Plugin.LogI($"  step2 speedCalc {_savedSpeedCalc} -> 0  gaugeFemale={_flags.gaugeFemale:F1} gaugeMale={_flags.gaugeMale:F1} locks=set");
            }
            else Plugin.LogW("  step2 flags=null, skipped");

            // 3. DynamicBones on females
            FreezeFemaleBones();
            Plugin.LogI($"  step3 bones disabled={_disabledBones.Count}");

            // 4. ParticleSystems under HScene root — emission only, existing particles keep falling
            FreezeParticles();
            Plugin.LogI($"  step4 particle emission suppressed={_suppressedEmission.Count}");

            // 4b. Stop in-flight female voice (KK_HSceneOptions ForceStopVoice pattern)
            StopFemaleVoices();

            // 4c. Pause every AudioSource on every female ChaControl (covers 3P/4P
            //     where transVoiceMouth only includes the active partner).
            FreezeFemaleAudio();

            // 4d. F2 nuclear: AudioListener.pause = true silences EVERYTHING globally
            //     (BGM, ambient SE, body sounds, fluid sounds, anything we missed).
            //     Our plugin AudioSource has ignoreListenerPause=true so SFX still
            //     plays. Side effect: BGM is also muted during freeze (intentional
            //     trade-off for "ZA WARUDO: silence" feel).
            _savedAudioListenerPause = AudioListener.pause;
            AudioListener.pause = true;
            Plugin.LogI($"  step4d AudioListener.pause: {_savedAudioListenerPause} -> true (global mute)");

            // 4e. F1: stop auto-blink on each subject.
            DisableBlink();
            Plugin.LogI($"  step4e blink disabled on {_savedBlinkFlags.Count} subject(s)");

            // 4e1. F11: cleanly release any active hand grab BEFORE freezing the
            // animator. Without this, an in-progress click-grab gets stuck because
            // ClickAction (HandCtrl.cs:147570) waits for the female's touch
            // animation loop to reach normalizedTime >= 1f to transition out of
            // the click state — and animBody.speed=0 makes that impossible.
            // ForceFinish() is KK's own emergency-stop API: resets action=none,
            // clears useItems[], stops contact SE.
            ForceFinishHands();

            // 4e2. Issue 1+5: per-character neck-look freeze (skipCalc) + head bone snapshot.
            FreezeNeckLook();
            Plugin.LogI($"  step4e2 neck-look frozen on {_savedSkipCalc.Count} subject(s) headPins={_pinnedHeadRots.Count}");

            // 4f. F8: snapshot in-progress face so an ahegao isn't lost.
            SnapshotAndPinFace();
            Plugin.LogI($"  step4f face snapshot for {_savedFaces.Count} subject(s)");

            // 5. SFX — Enter then During (loop)
            try { AudioManager.Instance.PlayFreezeSequence(); }
            catch (System.Exception e) { Plugin.LogW($"Freeze SFX sequence failed: {e}"); }

            Plugin.LogI("=== ZA WARUDO! === (freeze complete)");
        }

        public void Resume()
        {
            if (!_frozen) return;
            var elapsed = Time.realtimeSinceStartup - _freezeStartTime;
            Plugin.LogI($"=== RESUME start === elapsed={elapsed:F2}s");

            int restoredAnims = 0;
            foreach (var kv in _animSpeeds)
            {
                if (kv.Key != null) { kv.Key.speed = kv.Value; restoredAnims++; }
            }
            _animSpeeds.Clear();
            Plugin.LogI($"  restored animators={restoredAnims}");

            int restoredBones = 0;
            foreach (var b in _disabledBones)
            {
                if (b != null) { b.enabled = true; restoredBones++; }
            }
            _disabledBones.Clear();
            Plugin.LogI($"  re-enabled bones={restoredBones}");

            int restoredEmission = 0;
            foreach (var kv in _suppressedEmission)
            {
                if (kv.Key != null)
                {
                    try
                    {
                        var em = kv.Key.emission;
                        em.enabled = kv.Value;
                        restoredEmission++;
                    }
                    catch { }
                }
            }
            _suppressedEmission.Clear();
            Plugin.LogI($"  particle emission restored={restoredEmission}");

            int restoredAudio = 0;
            foreach (var a in _pausedAudio)
            {
                if (a != null) { a.UnPause(); restoredAudio++; }
            }
            _pausedAudio.Clear();
            Plugin.LogI($"  unpaused audio sources={restoredAudio}");

            // Restore global AudioListener.pause (F2 nuclear)
            AudioListener.pause = _savedAudioListenerPause;
            Plugin.LogI($"  AudioListener.pause restored to {_savedAudioListenerPause}");

            // Restore blink flags (F1)
            int restoredBlink = 0;
            foreach (var kv in _savedBlinkFlags)
            {
                if (kv.Key != null)
                {
                    try { kv.Key.ChangeEyesBlinkFlag(kv.Value); restoredBlink++; } catch { }
                }
            }
            _savedBlinkFlags.Clear();
            Plugin.LogI($"  blink restored on {restoredBlink} subject(s)");

            // Restore neck-look skipCalc (Issue 1+5)
            RestoreNeckLook();

            // Don't restore _savedFaces — let the game take over the face on resume.
            // (If ClimaxFaceOnResume is enabled, ApplyClimaxFace below overrides anyway.)
            _savedFaces.Clear();

            if (_flags != null)
            {
                _flags.speedCalc = _savedSpeedCalc;
                _flags.lockGugeFemale = _savedLockFemale;
                _flags.lockGugeMale = _savedLockMale;
                Plugin.LogI($"  restored speedCalc={_savedSpeedCalc} locks=({_savedLockFemale},{_savedLockMale})");
            }

            InjectGauge();

            // SFX — interrupts During loop, then Exit, then FemaleResume
            float sfxLen = 0f;
            try
            {
                sfxLen = AudioManager.Instance.ResumeSequenceLength;
                AudioManager.Instance.PlayResumeSequence();
            }
            catch (System.Exception e) { Plugin.LogW($"Resume SFX sequence failed: {e}"); }

            // Keep game voice muted until our resume SFX finishes — otherwise the
            // game's moan kicks in the instant _frozen flips false and overlaps
            // our Exit + FemaleResume clips.
            _voiceMuteUntil = Time.realtimeSinceStartup + sfxLen;
            Plugin.LogI($"  voice mute extended until +{sfxLen:F2}s (resume SFX duration)");

            // Optional: force a climax face on each female and pin it for the
            // duration of resume SFX (otherwise HMotionEyeNeck.Proc would overwrite
            // it on the next frame from the current animation state).
            if (Plugin.ClimaxFaceOnResume.Value)
            {
                _faceHoldUntil = Time.realtimeSinceStartup + sfxLen;
                ApplyClimaxFace();
                Plugin.LogI($"  climax face injected, hold until +{sfxLen:F2}s");
            }

            _frozen = false;
            Plugin.LogI("=== Toki wo ugokidasu === (resume complete)");
        }

        /// <summary>
        /// Called from HSceneProc.ChangeAnimator postfix when frozen — re-pin
        /// EVERY freeze step on the (possibly new) active set.
        ///
        /// F21 transition window: if `Plugin.AnimatorTransitionWindow.Value` is true,
        /// don't re-pin animators immediately. Instead let the female animator run
        /// at normal speed for a short window so the new state can actually start
        /// playing (otherwise our re-pin freezes her at the OLD state's last frame —
        /// observed in playtest as "male moves to alternate position, female stays
        /// glued to old pose"). All other locks (head pin, voice mute, blink, face
        /// snapshot, gauge lock, AudioListener.pause) stay engaged through the window.
        /// </summary>
        public void ReapplyIfFrozen()
        {
            if (!_frozen || _proc == null) return;

            // Non-animator state — always re-pin immediately
            FreezeFemaleBones();
            FreezeParticles();
            StopFemaleVoices();
            FreezeFemaleAudio();
            DisableBlink();
            FreezeNeckLook();
            SnapshotAndPinFace();
            ForceFinishHands(); // F11: clear any grab the partner switch may have started
            if (_flags != null) _flags.speedCalc = 0f;

            // Animator: either pin immediately, or run a transition window first
            if (Plugin.AnimatorTransitionWindow.Value && Plugin.Instance != null)
            {
                Plugin.LogI("[transition] starting animator transition window");
                Plugin.Instance.StartCoroutine(AnimatorTransitionThenPin());
            }
            else
            {
                FreezeFemaleAnimators();
            }
        }

        /// <summary>
        /// F21: lets the female's animBody.speed run normally for a brief window so
        /// the new animation state (queued by HSceneProc.ChangeAnimator → SetPlay) can
        /// actually begin playing. Wait condition copied from KK_HSceneOptions.RunAfterTransition:
        /// poll until the animator's current state name matches `flags.nowAnimStateName`
        /// AND its normalizedTime has advanced past 0.05. Bound by a hard timeout so a
        /// stuck transition doesn't hang the freeze forever.
        /// </summary>
        private System.Collections.IEnumerator AnimatorTransitionThenPin()
        {
            float deadline = Time.realtimeSinceStartup + Plugin.AnimatorTransitionTimeoutSec.Value;
            string targetState = _flags != null ? _flags.nowAnimStateName : null;

            // Briefly let females run at speed=1 (overwrite our cached zero — the
            // restore on resume reads from _animSpeeds which still holds the original)
            if (_females != null)
            {
                foreach (var f in _females)
                {
                    if (f != null && f.animBody != null) f.animBody.speed = 1f;
                }
            }

            int spinFrames = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                spinFrames++;
                bool settled = true;
                if (_females != null && targetState != null)
                {
                    foreach (var f in _females)
                    {
                        if (f == null || f.animBody == null) continue;
                        var info = f.animBody.GetCurrentAnimatorStateInfo(0);
                        if (!info.IsName(targetState) || info.normalizedTime < 0.05f)
                        {
                            settled = false;
                            break;
                        }
                    }
                }
                if (settled) break;
                yield return null;
            }

            // Re-pin
            FreezeFemaleAnimators();
            Plugin.LogI($"[transition] animator transition window complete after {spinFrames} frame(s); re-pinned");
        }

        // ---------- helpers ----------

        private void FreezeFemaleAnimators()
        {
            // ChaInfo exposes exactly two Animators: animBody and animTongueEx.
            // animFace / animOption do NOT exist (ilspy-verified against:
            //   - Koikatu_Data/Managed/Assembly-CSharp.dll
            //   - illusionlibs.koikatu.assembly-csharp/2019.4.27.4 NuGet stub
            //   - cross-checked with KK_HSceneOptions / KK_Plugins / IllusionModdingAPI sources)
            //
            // animBody is the canonical HScene animator that every reference plugin
            // (KK_HSceneOptions etc.) freezes. Setting its speed=0 stops body anim,
            // its layered face anim, AnimationEvents (lip-sync, blinks).
            //
            // animTongueEx exists on ChaInfo but the game never calls .speed/.Play/etc
            // on it — only assigns it once from objTongueEx.GetComponent<Animator>().
            // Freezing it is therefore a no-op in practice, but kept here as a cheap
            // belt-and-braces against a future game patch that might start driving it.
            foreach (var c in FrozenSubjects())
            {
                CacheAndZero(c.animBody);
                CacheAndZero(c.animTongueEx);
            }
        }

        private void CacheAndZero(Animator a)
        {
            if (a == null) return;
            if (!_animSpeeds.ContainsKey(a))
                _animSpeeds[a] = a.speed;
            a.speed = 0f;
        }

        // F7: previously this disabled every DynamicBone / DynamicBone_Ver02 on
        // each subject. Playtest feedback: "hair and skirt physics turn off when
        // time freezes" — user wants hair/cloth to keep draping naturally during
        // freeze (gravity still works on bones), only the body anchor is locked
        // (animBody.speed = 0). Now a no-op so the physics solvers keep running
        // and hair settles into a static drape over time.
        // _disabledBones cache stays defined (and will simply be empty) so the
        // restore loop in Resume() is harmless.
        private void FreezeFemaleBones()
        {
            // intentional no-op — see comment above.
        }

        private void FreezeFemaleAudio()
        {
            int paused = 0;
            foreach (var c in FrozenSubjects())
            {
                foreach (var src in c.GetComponentsInChildren<AudioSource>(true))
                {
                    if (src == null || !src.isPlaying) continue;
                    if (_pausedAudio.Add(src)) { src.Pause(); paused++; }
                }
            }
            Plugin.LogI($"  step4c subject AudioSources paused={paused} (cumulative cache={_pausedAudio.Count})");
        }

        /// <summary>
        /// Issue 1 + 5 (head): stop the neck-look calculation per character via the
        /// in-game `neckLookScript.skipCalc` flag (KK uses this internally — see
        /// decompiled line 61642). Also snapshot the current head bone localRotation
        /// so a parallel coroutine can re-pin it each LateUpdate, in case some
        /// other system writes to it after we set skipCalc.
        /// </summary>
        private void FreezeNeckLook()
        {
            foreach (var c in FrozenSubjects())
            {
                try
                {
                    if (c.neckLookCtrl != null && c.neckLookCtrl.neckLookScript != null)
                    {
                        var calc = c.neckLookCtrl.neckLookScript;
                        if (!_savedSkipCalc.ContainsKey(calc))
                            _savedSkipCalc[calc] = calc.skipCalc;
                        calc.skipCalc = true;
                    }
                    var head = c.objHeadBone != null ? c.objHeadBone.transform : null;
                    if (head != null && !_pinnedHeadRots.ContainsKey(head))
                        _pinnedHeadRots[head] = head.localRotation;
                }
                catch (System.Exception e) { Plugin.LogW($"  FreezeNeckLook failed on {c.name}: {e.Message}"); }
            }
        }

        /// <summary>Restore the cached skipCalc flags and clear the head pin set.</summary>
        private void RestoreNeckLook()
        {
            int restored = 0;
            foreach (var kv in _savedSkipCalc)
            {
                if (kv.Key != null) { kv.Key.skipCalc = kv.Value; restored++; }
            }
            _savedSkipCalc.Clear();
            _pinnedHeadRots.Clear();
            Plugin.LogI($"  neck-look skipCalc restored on {restored} subject(s)");
        }

        /// <summary>
        /// Plugin.LateUpdate calls this every frame while frozen — re-applies the
        /// snapshotted head localRotation in case any LateUpdate writer overwrote it.
        /// Cheap (Dictionary iteration) and idempotent.
        /// </summary>
        public void PinHeadRotationsLate()
        {
            if (!_frozen) return;
            foreach (var kv in _pinnedHeadRots)
            {
                if (kv.Key != null) kv.Key.localRotation = kv.Value;
            }
        }

        /// <summary>
        /// F1: stop blinking. ChangeEyesBlinkFlag(false) tells fbsCtrl.BlinkCtrl
        /// to fix the blink state, so eyes don't auto-close periodically. Cache
        /// the previous flag and restore it on resume.
        /// </summary>
        private void DisableBlink()
        {
            foreach (var c in FrozenSubjects())
            {
                if (_savedBlinkFlags.ContainsKey(c)) continue;
                try
                {
                    _savedBlinkFlags[c] = c.GetEyesBlinkFlag();
                    c.ChangeEyesBlinkFlag(false);
                }
                catch (System.Exception e)
                {
                    Plugin.LogW($"  DisableBlink failed on {c.name}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// F8: snapshot the current face per subject so an in-progress ahegao
        /// isn't lost when our HMotionEyeNeck.Proc skip kicks in. Re-applied on
        /// every Reapply call so partner switches preserve it too.
        /// </summary>
        private void SnapshotAndPinFace()
        {
            foreach (var c in FrozenSubjects())
            {
                if (_savedFaces.ContainsKey(c))
                {
                    // Re-apply (covers ReapplyIfFrozen and any frame where the
                    // game wrote a default face before our prefix kicked in).
                    var s = _savedFaces[c];
                    try
                    {
                        c.ChangeEyebrowPtn(s.eyebrow);
                        c.ChangeEyesPtn(s.eyes);
                        c.ChangeMouthPtn(s.mouth);
                        c.tearsLv = s.tears;
                        c.ChangeEyesOpenMax(s.eyesOpen);
                    }
                    catch { }
                    continue;
                }
                try
                {
                    _savedFaces[c] = new FaceState
                    {
                        eyes = c.fileStatus.eyesPtn,
                        mouth = c.fileStatus.mouthPtn,
                        eyebrow = c.fileStatus.eyebrowPtn,
                        tears = c.tearsLv,
                        eyesOpen = c.fileStatus.eyesOpenMax,
                    };
                }
                catch (System.Exception e)
                {
                    Plugin.LogW($"  SnapshotFace failed on {c.name}: {e.Message}");
                }
            }
        }

        private void ApplyClimaxFace()
        {
            // Only females — male protagonist face left alone.
            if (_females == null) return;
            int eye = Plugin.ClimaxEyesPtn.Value;
            int mouth = Plugin.ClimaxMouthPtn.Value;
            int eyebrow = Plugin.ClimaxEyebrowPtn.Value;
            byte tears = (byte)Mathf.Clamp(Plugin.ClimaxTearsLv.Value, 0, 3);
            int applied = 0;
            foreach (var f in _females)
            {
                if (f == null) continue;
                try
                {
                    f.ChangeEyebrowPtn(eyebrow);
                    f.ChangeEyesPtn(eye);
                    f.ChangeMouthPtn(mouth);
                    f.tearsLv = tears;
                    applied++;
                }
                catch (System.Exception e)
                {
                    Plugin.LogW($"  ApplyClimaxFace failed on {f.name}: {e.Message}");
                }
            }
            Plugin.LogI($"  climax face applied to {applied} female(s) eye={eye} mouth={mouth} brow={eyebrow} tears={tears}");
        }

        /// <summary>
        /// F11: cleanly release any active hand grab via the game's own ForceFinish.
        /// Called from Freeze() and ReapplyIfFrozen() to make sure no grab is left
        /// dangling in a state our animator freeze would lock up.
        /// </summary>
        private void ForceFinishHands()
        {
            int finished = 0;
            foreach (var h in new[] { _hand0, _hand1 })
            {
                if (h == null) continue;
                try
                {
                    if (h.action != HandCtrl.HandAction.none)
                    {
                        h.ForceFinish();
                        finished++;
                    }
                }
                catch (System.Exception e) { Plugin.LogW($"  ForceFinishHands failed: {e.Message}"); }
            }
            Plugin.LogI($"  step4e1 hands force-finished={finished}");
        }

        private void StopFemaleVoices()
        {
            if (_flags == null) return;
            int stopped = 0;
            try
            {
                var voiceInst = Manager.Voice.Instance;
                if (voiceInst == null) { Plugin.LogW("  Voice singleton null"); return; }
                if (_flags.transVoiceMouth == null) { Plugin.LogW("  transVoiceMouth null"); return; }
                for (int i = 0; i < _flags.transVoiceMouth.Length; i++)
                {
                    var t = _flags.transVoiceMouth[i];
                    if (t == null) continue;
                    voiceInst.Stop(t);
                    stopped++;
                }
            }
            catch (System.Exception e)
            {
                Plugin.LogW($"  StopFemaleVoices failed: {e.Message}");
            }
            Plugin.LogI($"  step4b female voices stopped={stopped}");
        }

        private void FreezeParticles()
        {
            if (_proc == null) return;
            foreach (var ps in _proc.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                if (_suppressedEmission.ContainsKey(ps)) continue;
                try
                {
                    var em = ps.emission;
                    _suppressedEmission[ps] = em.enabled;
                    em.enabled = false; // stop new spawns; existing particles still simulate
                }
                catch { }
            }
        }

        private void InjectGauge()
        {
            if (_flags == null) return;
            switch (Plugin.Mode.Value)
            {
                case ResumeMode.Instant:
                    _flags.gaugeFemale = 100f;
                    Plugin.LogI("Gauge → 100 (Instant).");
                    break;
                case ResumeMode.Accumulated:
                    var elapsed = Time.realtimeSinceStartup - _freezeStartTime;
                    var delta = elapsed * Plugin.AccumulationRate.Value;
                    var before = _flags.gaugeFemale;
                    var cap = Plugin.AccumulationCap.Value;
                    // Cap only applies as a ceiling for the INJECTED value — never
                    // pulls the gauge DOWN if it's already above the cap.
                    var target = Mathf.Min(100f, before + delta);
                    if (before < cap) target = Mathf.Min(target, cap);
                    _flags.gaugeFemale = target;
                    Plugin.LogI($"Gauge {before:F1} → {_flags.gaugeFemale:F1} (+{delta:F1} over {elapsed:F1}s, cap={cap:F0}).");
                    break;
            }
        }
    }
}
