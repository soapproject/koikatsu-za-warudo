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
        private float _freezeStartTime;

        // Caches
        private readonly Dictionary<Animator, float> _animSpeeds = new Dictionary<Animator, float>();
        private readonly List<Behaviour> _disabledBones = new List<Behaviour>();
        private readonly List<ParticleSystem> _pausedParticles = new List<ParticleSystem>();
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
            Instance = null;
        }

        public void Toggle()
        {
            if (_proc == null)
            {
                Plugin.Log.LogInfo("Toggle ignored: not in HScene.");
                return;
            }
            if (_frozen) Resume();
            else Freeze();
        }

        public void Freeze()
        {
            if (_frozen || _proc == null) return;
            _frozen = true;
            _freezeStartTime = Time.realtimeSinceStartup;

            // 1. Animator.speed = 0 on females
            FreezeFemaleAnimators();

            // 2. HFlag.speedCalc
            if (_flags != null)
            {
                _savedSpeedCalc = _flags.speedCalc;
                _flags.speedCalc = 0f;
            }

            // 3. DynamicBones on females
            FreezeFemaleBones();

            // 4. ParticleSystems under HScene root
            FreezeParticles();

            // 5. SFX
            try
            {
                AudioManager.Instance.EnsureLoaded();
                AudioManager.Instance.PlayEnter();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Enter SFX play failed: {e}");
            }

            Plugin.Log.LogInfo("ZA WARUDO!");
        }

        public void Resume()
        {
            if (!_frozen) return;

            // Restore animators
            foreach (var kv in _animSpeeds)
            {
                if (kv.Key != null) kv.Key.speed = kv.Value;
            }
            _animSpeeds.Clear();

            // Restore bones
            foreach (var b in _disabledBones)
            {
                if (b != null) b.enabled = true;
            }
            _disabledBones.Clear();

            // Resume particles
            foreach (var p in _pausedParticles)
            {
                if (p != null) p.Play(true);
            }
            _pausedParticles.Clear();

            // Restore speedCalc
            if (_flags != null)
                _flags.speedCalc = _savedSpeedCalc;

            // Inject gauge per ResumeMode
            InjectGauge();

            // SFX
            try { AudioManager.Instance.PlayResume(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Resume SFX play failed: {e}"); }

            _frozen = false;
            Plugin.Log.LogInfo("Toki wo ugokidasu.");
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
                    Plugin.Log.LogInfo("Gauge → 100 (Instant).");
                    break;
                case ResumeMode.Accumulated:
                    var elapsed = Time.realtimeSinceStartup - _freezeStartTime;
                    var delta = elapsed * Plugin.AccumulationRate.Value;
                    var before = _flags.gaugeFemale;
                    _flags.gaugeFemale = Mathf.Min(100f, before + delta);
                    Plugin.Log.LogInfo($"Gauge {before:F1} → {_flags.gaugeFemale:F1} (+{delta:F1} over {elapsed:F1}s).");
                    break;
            }
        }
    }
}
