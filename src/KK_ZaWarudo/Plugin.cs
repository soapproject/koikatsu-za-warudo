using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_ZaWarudo
{
    public enum ResumeMode
    {
        Instant,
        Accumulated,
    }

    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("Koikatu")]
    [BepInProcess("Koikatsu Party")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "weiss.kk.zawarudo";
        public const string PluginName = "KK_ZaWarudo";
        public const string Version = "0.1.0";

        // Distinctive prefix so the log can be grep'd out of BepInEx/LogOutput.log
        // among many other plugins. Search for "ZAWA>" to find every line.
        internal const string LogTag = "ZAWA>";
        internal static ManualLogSource Log;

        internal static void LogI(string msg) => Log?.LogInfo($"{LogTag} {msg}");
        internal static void LogD(string msg) => Log?.LogInfo($"{LogTag} [dbg] {msg}"); // promoted to Info so default log level shows it
        internal static void LogW(string msg) => Log?.LogWarning($"{LogTag} {msg}");
        internal static void LogE(string msg) => Log?.LogError($"{LogTag} {msg}");

        // General
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<ResumeMode> Mode;
        internal static ConfigEntry<float> AccumulationRate;

        // Audio
        internal static ConfigEntry<string> SfxFolder;
        internal static ConfigEntry<string> EnterSfxFile;
        internal static ConfigEntry<string> ResumeSfxFile;
        internal static ConfigEntry<float> SfxVolume;

        internal static Plugin Instance;
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ToggleKey = Config.Bind("General", "Toggle Key",
                new KeyboardShortcut(KeyCode.T),
                "Press during HScene to freeze/unfreeze.");

            Mode = Config.Bind("General", "Resume Mode",
                ResumeMode.Accumulated,
                "Instant = jam female gauge to 100 on resume. Accumulated = add (duration * rate).");

            AccumulationRate = Config.Bind("General", "Accumulation Rate",
                10f,
                new ConfigDescription("Gauge points per second of frozen time (Accumulated mode only).",
                    new AcceptableValueRange<float>(0f, 100f)));

            SfxFolder = Config.Bind("Audio", "SFX Folder",
                Path.Combine(Paths.PluginPath, "bgm/zawarudo"),
                "Folder containing wav files. Defaults to BepInEx/plugins/bgm/zawarudo (SlapMod-style).");

            EnterSfxFile = Config.Bind("Audio", "Enter SFX Filename",
                "enter.wav",
                "Filename inside SFX Folder, played on freeze. Missing file = silent.");

            ResumeSfxFile = Config.Bind("Audio", "Resume SFX Filename",
                "resume.wav",
                "Filename inside SFX Folder, played on resume. Missing file = silent.");

            SfxVolume = Config.Bind("Audio", "SFX Volume",
                1f,
                new ConfigDescription("Relative volume; multiplied by game master volume.",
                    new AcceptableValueRange<float>(0f, 1f)));

            _harmony = Harmony.CreateAndPatchAll(typeof(Hooks), GUID);
            LogI($"{PluginName} {Version} loaded. Toggle={ToggleKey.Value} Mode={Mode.Value} Rate={AccumulationRate.Value} SfxFolder={SfxFolder.Value}");
        }

        private void OnDestroy()
        {
            LogI("OnDestroy: forcing resume + unpatching.");
            TimeStopController.Instance?.Resume();
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (ToggleKey.Value.IsDown())
            {
                LogI($"Hotkey pressed (frozen={TimeStopController.Instance?.IsFrozen}).");
                if (TimeStopController.Instance == null)
                    LogW("No TimeStopController bound — not in HScene yet.");
                else
                    TimeStopController.Instance.Toggle();
            }
        }
    }
}
