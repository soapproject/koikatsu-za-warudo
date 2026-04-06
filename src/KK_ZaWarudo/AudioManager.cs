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

        // Cache keys so EnsureLoaded can detect config changes.
        private string _cacheFolder;
        private string _cacheEnter;
        private string _cacheDuring;
        private string _cacheExit;
        private string _cacheFemale;

        private void EnsureSource()
        {
            if (_source != null) return;
            if (Plugin.Instance == null) return;
            _source = Plugin.Instance.gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;
            Plugin.LogI("AudioSource attached to plugin GameObject.");
        }

        public void EnsureLoaded()
        {
            EnsureSource();
            var folder = Plugin.SfxFolder.Value;
            var e = Plugin.EnterSfxFile.Value;
            var d = Plugin.DuringSfxFile.Value;
            var x = Plugin.ExitSfxFile.Value;
            var fr = Plugin.FemaleResumeSfxFile.Value;

            if (folder != _cacheFolder || e != _cacheEnter)        { _enter = TryLoad(Path.Combine(folder, e));  _cacheEnter = e; }
            if (folder != _cacheFolder || d != _cacheDuring)       { _during = TryLoad(Path.Combine(folder, d)); _cacheDuring = d; }
            if (folder != _cacheFolder || x != _cacheExit)         { _exit = TryLoad(Path.Combine(folder, x));   _cacheExit = x; }
            if (folder != _cacheFolder || fr != _cacheFemale)      { _femaleResume = TryLoad(Path.Combine(folder, fr)); _cacheFemale = fr; }
            _cacheFolder = folder;

            Plugin.LogI($"SFX loaded: enter={_enter != null} during={_during != null} exit={_exit != null} femaleResume={_femaleResume != null}");
        }

        private static AudioClip TryLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Plugin.LogW($"SFX missing, skipping: {path}");
                return null;
            }
            Plugin.LogI($"Loading SFX: {path}");
            try
            {
                var uri = new Uri(path).AbsoluteUri;
                var www = new WWW(uri);
                try
                {
                    var clip = WWWAudioExtensions.GetAudioClipCompressed(www, false, AudioType.WAV);
                    int guard = 0;
                    while (clip != null && clip.loadState != AudioDataLoadState.Loaded && guard++ < 10000) { }
                    return clip;
                }
                finally { www.Dispose(); }
            }
            catch (Exception e)
            {
                Plugin.LogW($"Failed loading {path}: {e.Message}");
                return null;
            }
        }

        // ---------- public sequence API ----------

        public void PlayFreezeSequence()
        {
            EnsureLoaded();
            if (_source == null || Plugin.Instance == null) return;
            CancelRunning("PlayFreezeSequence");
            _running = Plugin.Instance.StartCoroutine(FreezeRoutine());
        }

        public void PlayResumeSequence()
        {
            EnsureLoaded();
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

            if (_during != null)
            {
                Plugin.LogI($"FreezeRoutine: starting During loop ({_during.name})");
                _source.clip = _during;
                _source.loop = true;
                _source.volume = CurrentVolume();
                _source.Play();
                // Coroutine sits here until cancelled by ResumeSequence/StopAll.
                while (true) yield return null;
            }
            else
            {
                Plugin.LogI("FreezeRoutine: no During clip, idle.");
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
