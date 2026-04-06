using System.Collections.Generic;
using UnityEngine;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Owns the freeze/resume logic per docs/SPEC.md.
    /// Strategy:
    ///   1. Animator.speed = 0 on every female ChaControl's animBody/animFace/animOption.
    ///      Male (= player) is left untouched.
    ///   2. HFlag.speedCalc = 0 to halt the gauge tick.
    ///   3. Cache + disable DynamicBone / DynamicBone_Ver02 components on females.
    ///   4. Pause every ParticleSystem under the HScene root.
    ///   5. AudioListener.pause stays alone — voice handled per HVoiceCtrl below if needed.
    ///   6. Play Enter SFX. On resume play Resume SFX and inject gauge.
    /// All mutated state is cached for clean restore.
    /// </summary>
    internal class TimeStopController
    {
        public static TimeStopController Instance { get; private set; }

        private HSceneProc _proc;
        private List<ChaControl> _females;
        // _male intentionally not stored — male = player, untouched per spec.
        private HFlag _flags;

        private bool _frozen;
        public bool IsFrozen => _frozen;
        private float _freezeStartTime;

        // Caches
        private readonly Dictionary<Animator, float> _animSpeeds = new Dictionary<Animator, float>();
        private readonly List<Behaviour> _disabledBones = new List<Behaviour>();
        private readonly List<ParticleSystem> _pausedParticles = new List<ParticleSystem>();
        private readonly List<AudioSource> _pausedAudio = new List<AudioSource>();
        private float _savedSpeedCalc;

        public static void Bind(HSceneProc proc, List<ChaControl> females, ChaControl male, HFlag flags)
        {
            // male reference accepted for API symmetry but not stored — left untouched per spec.
            _ = male;
            Instance = new TimeStopController
            {
                _proc = proc,
                _females = females,
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
        /// the new animators / bones / particles since the active set may have shifted.
        /// </summary>
        public void ReapplyIfFrozen()
        {
            if (!_frozen || _proc == null) return;
            FreezeFemaleAnimators();
            FreezeFemaleBones();
            FreezeParticles();
            if (_flags != null) _flags.speedCalc = 0f;
        }

        // ---------- helpers ----------

        private void FreezeFemaleAnimators()
        {
            if (_females == null) return;
            foreach (var f in _females)
            {
                if (f == null) continue;
                CacheAndZero(f.animBody);
                // animFace / animOption may not exist on every build — guard via reflection-friendly null checks
                var face = f.GetType().GetField("animFace")?.GetValue(f) as Animator;
                CacheAndZero(face);
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
            if (_females == null) return;
            foreach (var f in _females)
            {
                if (f == null) continue;
                // DynamicBone (v1)
                foreach (var b in f.GetComponentsInChildren<DynamicBone>(true))
                {
                    if (b == null || !b.enabled) continue;
                    b.enabled = false;
                    _disabledBones.Add(b);
                }
                // DynamicBone_Ver02
                foreach (var b in f.GetComponentsInChildren<DynamicBone_Ver02>(true))
                {
                    if (b == null || !b.enabled) continue;
                    b.enabled = false;
                    _disabledBones.Add(b);
                }
            }
        }

        private void FreezeFemaleAudio()
        {
            if (_females == null) return;
            int paused = 0;
            foreach (var f in _females)
            {
                if (f == null) continue;
                foreach (var src in f.GetComponentsInChildren<AudioSource>(true))
                {
                    if (src == null || !src.isPlaying) continue;
                    src.Pause();
                    _pausedAudio.Add(src);
                    paused++;
                }
            }
            Plugin.LogI($"  step4c female AudioSources paused={paused}");
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
                ps.Pause(true);
                _pausedParticles.Add(ps);
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
