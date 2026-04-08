using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace KK_ZaWarudo
{
    /// <summary>
    /// Serial WAV player. One AudioSource → no overlap by construction.
    /// Two sequences:
    ///   FREEZE:  Enter (one-shot) → During (loops until ResumeSequence cancels it)
    ///   RESUME:  Exit (one-shot)  → FemaleResume (one-shot)
    /// Resume cancels any in-flight Enter or During by stopping the source and
    /// starting a new coroutine, so it cannot overlap with the freeze sequence.
    ///
    /// Loader follows SlapMod pattern (references/SlapMod/SlapMod.decompiled.cs:288):
    /// WWW + WWWAudioExtensions.GetAudioClipCompressed.
    /// </summary>
    internal class AudioManager
    {
        private static AudioManager _instance;
        public static AudioManager Instance => _instance ?? (_instance = new AudioManager());

        private AudioSource _source;
        private Coroutine _running;

        private AudioClip _enter;
        private AudioClip _during;
        private AudioClip _exit;
        private AudioClip _femaleResume;

        /// <summary>
        /// Sum of Exit + FemaleResume clip lengths in seconds.
        /// Used by TimeStopController to keep game voice muted until our resume
        /// sequence finishes (so the game's moan doesn't kick in while our SFX
        /// is still playing). Returns 0 if either clip is missing — caller falls
        /// through to immediate unmute.
        /// </summary>
        public float ResumeSequenceLength
        {
            get
            {
                float total = 0f;
                if (_exit != null) total += _exit.length;
                if (_femaleResume != null) total += _femaleResume.length;
                return total;
            }
        }

        private bool _loaded;
        private bool _loading;

        private void EnsureSource()
        {
            if (_source != null) return;
            if (Plugin.Instance == null) return;
            _source = Plugin.Instance.gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;
            // F2: TimeStopController flips AudioListener.pause = true on freeze to
            // mute the entire game (including stubborn voice/SE sources we never
            // located). Our own SFX must opt out of that global pause.
            _source.ignoreListenerPause = true;
            Plugin.LogI("AudioSource attached to plugin GameObject (ignoreListenerPause=true).");
        }

        /// <summary>Kick off async load. Idempotent. Call from Plugin.Awake.</summary>
        public void StartLoad()
        {
            if (_loaded || _loading) return;
            EnsureSource();
            if (Plugin.Instance == null) return;
            _loading = true;
            Plugin.Instance.StartCoroutine(LoadAllAsync());
        }

        private IEnumerator LoadAllAsync()
        {
            var folder = NormalizeFolder(Plugin.SfxFolder.Value);
            Plugin.LogI($"AudioManager.LoadAllAsync from: {folder}");

            yield return LoadOne(Path.Combine(folder, Plugin.EnterSfxFile.Value),       c => _enter = c,        "enter");
            yield return LoadOne(Path.Combine(folder, Plugin.DuringSfxFile.Value),      c => _during = c,       "during");
            yield return LoadOne(Path.Combine(folder, Plugin.ExitSfxFile.Value),        c => _exit = c,         "exit");
            yield return LoadOne(Path.Combine(folder, Plugin.FemaleResumeSfxFile.Value),c => _femaleResume = c, "femaleResume");

            _loading = false;
            _loaded = true;
            Plugin.LogI($"SFX load done: enter={Describe(_enter)} during={Describe(_during)} exit={Describe(_exit)} femaleResume={Describe(_femaleResume)}");
        }

        private static string Describe(AudioClip c) => c == null ? "null" : $"{c.name}({c.length:F2}s)";

        private static string NormalizeFolder(string folder)
        {
            // Config may have mixed slashes from Path.Combine + raw default. Normalize for Uri.
            return folder.Replace('/', Path.DirectorySeparatorChar);
        }

        private static IEnumerator LoadOne(string path, Action<AudioClip> assign, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Plugin.LogW($"SFX missing [{label}]: {path}");
                yield break;
            }
            Plugin.LogI($"Loading SFX [{label}]: {path}");
            string uri;
            try { uri = new Uri(path).AbsoluteUri; }
            catch (Exception e) { Plugin.LogW($"  uri build failed: {e.Message}"); yield break; }

            var www = new WWW(uri);
            // Yielding WWW lets the main thread pump it to completion.
            yield return www;

            if (!string.IsNullOrEmpty(www.error))
            {
                Plugin.LogW($"  WWW error [{label}]: {www.error}");
                www.Dispose();
                yield break;
            }

            AudioClip clip = null;
            try
            {
                clip = WWWAudioExtensions.GetAudioClip(www, false, false, AudioType.WAV);
            }
            catch (Exception e)
            {
                Plugin.LogW($"  GetAudioClip failed [{label}]: {e.Message}");
            }
            finally { www.Dispose(); }

            if (clip != null)
            {
                clip.name = label;
                assign(clip);
                Plugin.LogI($"  loaded [{label}]: {clip.length:F2}s, samples={clip.samples}");
            }
        }

        // ---------- public sequence API ----------

        public void PlayFreezeSequence()
        {
            if (_source == null || Plugin.Instance == null) return;
            if (!_loaded) Plugin.LogW("PlayFreezeSequence: clips not loaded yet (race?)");
            CancelRunning("PlayFreezeSequence");
            _running = Plugin.Instance.StartCoroutine(FreezeRoutine());
        }

        public void PlayResumeSequence()
        {
            if (_source == null || Plugin.Instance == null) return;
            CancelRunning("PlayResumeSequence");
            _running = Plugin.Instance.StartCoroutine(ResumeRoutine());
        }

        public void StopAll()
        {
            CancelRunning("StopAll");
        }

        // ---------- internals ----------

        private void CancelRunning(string reason)
        {
            if (_running != null && Plugin.Instance != null)
            {
                Plugin.LogI($"  cancel running SFX coroutine ({reason}).");
                Plugin.Instance.StopCoroutine(_running);
                _running = null;
            }
            if (_source != null)
            {
                _source.loop = false;
                if (_source.isPlaying) _source.Stop();
                _source.clip = null;
            }
        }

        private static bool ShouldPlayDuring()
        {
            if (!Plugin.PlayDuringLoop.Value) return false;
            if (!Plugin.DuringOnlyWhileActive.Value) return true; // unconditional loop
            return TimeStopController.Instance != null && TimeStopController.Instance.IsPlayerActing;
        }

        private float CurrentVolume()
        {
            float master = 1f;
            try { master = Manager.Config.SoundData.Master.Volume * 0.01f; }
            catch (Exception e) { Plugin.LogW($"  master volume read failed, using 1.0: {e.Message}"); }
            return Mathf.Clamp01(master * Plugin.SfxVolume.Value);
        }

        private IEnumerator FreezeRoutine()
        {
            Plugin.LogI("FreezeRoutine: begin");
            yield return PlayOneShotAndWait(_enter, "Enter");

            // During loop tick: each frame decide whether the loop should be playing
            // based on (a) config (Play During Loop) and (b) optionally whether the
            // player is actively touching (During Loop Only While Active).
            // The source is single, so toggling Play/Stop here cleanly without overlap.
            Plugin.LogI($"FreezeRoutine: entering During tick (during={_during != null} enabled={Plugin.PlayDuringLoop.Value} gated={Plugin.DuringOnlyWhileActive.Value})");
            bool playing = false;
            while (true) // until coroutine is cancelled by ResumeSequence/StopAll
            {
                bool shouldPlay = ShouldPlayDuring();
                if (shouldPlay && !playing && _during != null)
                {
                    _source.clip = _during;
                    _source.loop = true;
                    _source.volume = CurrentVolume();
                    _source.Play();
                    playing = true;
                    Plugin.LogI("  during loop START (player active)");
                }
                else if (!shouldPlay && playing)
                {
                    _source.Stop();
                    playing = false;
                    Plugin.LogI("  during loop STOP (player idle)");
                }
                yield return null;
            }
        }

        private IEnumerator ResumeRoutine()
        {
            Plugin.LogI("ResumeRoutine: begin");
            yield return PlayOneShotAndWait(_exit, "Exit");
            yield return PlayOneShotAndWait(_femaleResume, "FemaleResume");
            Plugin.LogI("ResumeRoutine: complete");
            _running = null;
        }

        private IEnumerator PlayOneShotAndWait(AudioClip clip, string label)
        {
            if (clip == null)
            {
                Plugin.LogI($"  [{label}] skipped (no clip)");
                yield break;
            }
            _source.loop = false;
            _source.clip = clip;
            _source.volume = CurrentVolume();
            _source.Play();
            Plugin.LogI($"  [{label}] playing ({clip.length:F2}s, vol={_source.volume:F2})");
            // Wait until clip finishes (or external cancel triggers Stop).
            while (_source != null && _source.isPlaying && _source.clip == clip)
                yield return null;
            Plugin.LogI($"  [{label}] done");
        }
    }
}
