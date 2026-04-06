using System;
using System.IO;
using UnityEngine;

namespace KK_ZaWarudo
{
    /// <summary>
    /// SlapMod-style WAV loader.
    /// See references/SlapMod/SlapMod.decompiled.cs:288 for the original pattern.
    ///   - WWW + WWWAudioExtensions.GetAudioClipCompressed(www, false, AudioType.WAV)
    ///   - AudioSource added to plugin GameObject
    ///   - Volume = SfxVolume * Config.SoundData.Master.Volume * 0.01f
    /// Missing files are silently skipped (warning to log).
    /// </summary>
    internal class AudioManager
    {
        private static AudioManager _instance;
        public static AudioManager Instance => _instance ?? (_instance = new AudioManager());

        private AudioSource _source;
        private AudioClip _enterClip;
        private AudioClip _resumeClip;

        private string _loadedFolder;
        private string _loadedEnter;
        private string _loadedResume;

        private void EnsureSource()
        {
            if (_source != null) return;
            if (Plugin.Instance == null) return;
            _source = Plugin.Instance.gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f; // 2D
        }

        /// <summary>Reload clips if config paths changed since last load.</summary>
        public void EnsureLoaded()
        {
            EnsureSource();
            var folder = Plugin.SfxFolder.Value;
            var enterName = Plugin.EnterSfxFile.Value;
            var resumeName = Plugin.ResumeSfxFile.Value;

            if (folder != _loadedFolder || enterName != _loadedEnter)
            {
                _enterClip = TryLoad(Path.Combine(folder, enterName));
                _loadedEnter = enterName;
            }
            if (folder != _loadedFolder || resumeName != _loadedResume)
            {
                _resumeClip = TryLoad(Path.Combine(folder, resumeName));
                _loadedResume = resumeName;
            }
            _loadedFolder = folder;
        }

        private static AudioClip TryLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Plugin.Log.LogWarning($"[ZaWarudo] SFX not found, skipping: {path}");
                return null;
            }
            try
            {
                // SlapMod uses WWW + WWWAudioExtensions.GetAudioClipCompressed.
                // file:// URI required by WWW.
                var uri = new Uri(path).AbsoluteUri;
                var www = new WWW(uri);
                try
                {
                    var clip = WWWAudioExtensions.GetAudioClipCompressed(www, false, AudioType.WAV);
                    // Spin-wait until loaded — same as SlapMod (clips are tiny).
                    int guard = 0;
                    while (clip != null && clip.loadState != AudioDataLoadState.Loaded && guard++ < 10000)
                    {
                        // tight loop; clips are local file so this resolves immediately.
                    }
                    return clip;
                }
                finally
                {
                    www.Dispose();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ZaWarudo] Failed loading {path}: {e.Message}");
                return null;
            }
        }

        public void PlayEnter() => Play(_enterClip);
        public void PlayResume() => Play(_resumeClip);

        private void Play(AudioClip clip)
        {
            if (clip == null || _source == null) return;
            float master = 1f;
            try { master = Manager.Config.SoundData.Master.Volume * 0.01f; }
            catch { /* fall back to 1.0 if anything is missing */ }
            _source.volume = Mathf.Clamp01(master * Plugin.SfxVolume.Value);
            _source.PlayOneShot(clip);
        }
    }
}
