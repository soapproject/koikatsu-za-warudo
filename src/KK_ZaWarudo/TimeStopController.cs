using System.Collections.Generic;
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

        private bool _frozen;
        public bool IsFrozen => _frozen;
        private float _freezeStartTime;

        // Caches — HashSet (not List) so ReapplyIfFrozen can be called repeatedly
        // (every partner switch / position change) without growing the cache unbounded
        // when the same Components are encountered again.
        private readonly Dictionary<Animator, float> _animSpeeds = new Dictionary<Animator, float>();
        private readonly HashSet<Behaviour> _disabledBones = new HashSet<Behaviour>();
        private readonly HashSet<ParticleSystem> _pausedParticles = new HashSet<ParticleSystem>();
        private readonly HashSet<AudioSource> _pausedAudio = new HashSet<AudioSource>();
        private float _savedSpeedCalc;

        public static void Bind(MonoBehaviour proc, List<ChaControl> females, ChaControl male, List<ChaControl> extraMales, HFlag flags)
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

            // 2. HFlag.speedCalc
            if (_flags != null)
            {
                _savedSpeedCalc = _flags.speedCalc;
                _flags.speedCalc = 0f;
                Plugin.LogI($"  step2 speedCalc {_savedSpeedCalc} -> 0  gaugeFemale={_flags.gaugeFemale:F1}");
            }
            else Plugin.LogW("  step2 flags=null, skipped");

            // 3. DynamicBones on females
            FreezeFemaleBones();
            Plugin.LogI($"  step3 bones disabled={_disabledBones.Count}");

            // 4. ParticleSystems under HScene root
            FreezeParticles();
            Plugin.LogI($"  step4 particles paused={_pausedParticles.Count}");

            // 4b. Stop in-flight female voice (KK_HSceneOptions ForceStopVoice pattern)
            StopFemaleVoices();

            // 4c. Pause every AudioSource on every female ChaControl (covers 3P/4P
            //     where transVoiceMouth only includes the active partner).
            FreezeFemaleAudio();

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

            int restoredParticles = 0;
            foreach (var p in _pausedParticles)
            {
                if (p != null) { p.Play(true); restoredParticles++; }
            }
            _pausedParticles.Clear();
            Plugin.LogI($"  resumed particles={restoredParticles}");

            int restoredAudio = 0;
            foreach (var a in _pausedAudio)
            {
                if (a != null) { a.UnPause(); restoredAudio++; }
            }
            _pausedAudio.Clear();
            Plugin.LogI($"  unpaused audio sources={restoredAudio}");

            if (_flags != null)
            {
                _flags.speedCalc = _savedSpeedCalc;
                Plugin.LogI($"  restored speedCalc={_savedSpeedCalc}");
            }

            InjectGauge();

            // SFX — interrupts During loop, then Exit, then FemaleResume
            try { AudioManager.Instance.PlayResumeSequence(); }
            catch (System.Exception e) { Plugin.LogW($"Resume SFX sequence failed: {e}"); }

            _frozen = false;
            Plugin.LogI("=== Toki wo ugokidasu === (resume complete)");
        }

        /// <summary>
        /// Called from HSceneProc.ChangeAnimator postfix when frozen — re-pin
        /// EVERY freeze step on the (possibly new) active set: animators, bones,
        /// particles, the in-flight voice slots, and any AudioSources that just
        /// started playing on the new partner. The HashSet caches dedupe so this
        /// is safe to call repeatedly.
        /// </summary>
        public void ReapplyIfFrozen()
        {
            if (!_frozen || _proc == null) return;
            FreezeFemaleAnimators();
            FreezeFemaleBones();
            FreezeParticles();
            StopFemaleVoices();    // re-stop voice slots — new partner may have new mouth bindings
            FreezeFemaleAudio();   // catches any AudioSource that started playing during the switch
            if (_flags != null) _flags.speedCalc = 0f;
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

        private void FreezeFemaleBones()
        {
            foreach (var c in FrozenSubjects())
            {
                foreach (var b in c.GetComponentsInChildren<DynamicBone>(true))
                {
                    if (b == null || !b.enabled) continue;
                    if (_disabledBones.Add(b)) b.enabled = false;
                }
                foreach (var b in c.GetComponentsInChildren<DynamicBone_Ver02>(true))
                {
                    if (b == null || !b.enabled) continue;
                    if (_disabledBones.Add(b)) b.enabled = false;
                }
            }
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
                if (ps == null || !ps.isPlaying) continue;
                if (_pausedParticles.Add(ps)) ps.Pause(true);
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
                    _flags.gaugeFemale = Mathf.Min(100f, before + delta);
                    Plugin.LogI($"Gauge {before:F1} → {_flags.gaugeFemale:F1} (+{delta:F1} over {elapsed:F1}s).");
                    break;
            }
        }
    }
}
