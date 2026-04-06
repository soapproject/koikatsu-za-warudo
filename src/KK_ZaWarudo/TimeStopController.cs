using System.Collections.Generic;
using UnityEngine;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Owns the freeze/resume logic. Strategy (see references/KK_HSceneOptions
    /// for prior art on speed manipulation):
    ///   1. Animator.speed = 0 on every ChaControl's animBody/animFace.
    ///   2. HFlag.speedCalc = 0 to halt the gauge tick.
    ///   3. Cache + disable DynamicBone / DynamicBone_Ver02 components.
    ///   4. Pause every ParticleSystem under the HScene root.
    ///   5. AudioListener.pause = true.
    /// State is cached so Resume() restores exactly what we touched.
    /// </summary>
    internal class TimeStopController
    {
        public static TimeStopController Instance { get; private set; }

        private HSceneProc _proc;
        private List<ChaControl> _females;
        private ChaControl _male;
        private HFlag _flags;

        private bool _frozen;

        // Cached pre-freeze state for clean restore.
        private readonly Dictionary<Animator, float> _animSpeeds = new Dictionary<Animator, float>();
        private readonly List<Behaviour> _disabledBones = new List<Behaviour>();
        private readonly List<ParticleSystem> _pausedParticles = new List<ParticleSystem>();
        private float _savedSpeedCalc;
        private bool _savedAudioPause;

        public static void Bind(HSceneProc proc, List<ChaControl> females, ChaControl male, HFlag flags)
        {
            Instance = new TimeStopController
            {
                _proc = proc,
                _females = females,
                _male = male,
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
            if (_proc == null) return;
            if (_frozen) Resume();
            else Freeze();
        }

        public void Freeze()
        {
            if (_frozen || _proc == null) return;
            // TODO: implement per strategy in class doc.
            _frozen = true;
            Plugin.Logger.LogInfo("ZA WARUDO! (stub)");
        }

        public void Resume()
        {
            if (!_frozen) return;
            // TODO: restore from caches.
            _animSpeeds.Clear();
            _disabledBones.Clear();
            _pausedParticles.Clear();
            _frozen = false;
            Plugin.Logger.LogInfo("Toki wo ugokidasu. (stub)");
        }
    }
}
