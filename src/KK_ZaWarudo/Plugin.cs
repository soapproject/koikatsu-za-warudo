using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI;
using KKAPI.MainGame;
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
    [BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
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
        internal static ConfigEntry<string> EnterSfxFile;       // 1. 時停開始
        internal static ConfigEntry<string> DuringSfxFile;      // 2. 過程中女角 (loop)
        internal static ConfigEntry<string> ExitSfxFile;        // 3. 時停結束
        internal static ConfigEntry<string> FemaleResumeSfxFile;// 4. 結束時女角
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

            // Naming convention: zawarudo_<role>_<phase>.wav
            //   role  = sfx | female
            //   phase = enter | during | exit | resume
            // Prefix avoids collision with other plugins sharing the bgm/ folder.
            EnterSfxFile = Config.Bind("Audio", "1. Enter SFX",
                "zawarudo_sfx_enter.wav",
                "SFX played first when freeze starts. Missing file = silent.");

            DuringSfxFile = Config.Bind("Audio", "2. Female During SFX (loop)",
                "zawarudo_female_during.wav",
                "Female voice loop while time is frozen. Starts after Enter finishes. Missing file = silent.");

            ExitSfxFile = Config.Bind("Audio", "3. Exit SFX",
                "zawarudo_sfx_exit.wav",
                "SFX played when freeze ends (interrupts the During loop). Missing file = silent.");

            FemaleResumeSfxFile = Config.Bind("Audio", "4. Female Resume SFX",
                "zawarudo_female_resume.wav",
                "Female voice played after Exit finishes. Missing file = silent.");

            SfxVolume = Config.Bind("Audio", "SFX Volume",
                1f,
                new ConfigDescription("Relative volume; multiplied by game master volume.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // KKAPI manages the HScene start/end lifecycle for us (incl. VR).
            // See ZaWarudoController.OnStartH/OnEndH.
            GameAPI.RegisterExtraBehaviour<ZaWarudoController>(null);

            // We still own the ChangeAnimator hook (KKAPI doesn't surface it).
            _harmony = new Harmony(GUID);
            Hooks.Apply(_harmony);

            AudioManager.Instance.StartLoad();
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
            if (!ToggleKey.Value.IsDown()) return;

            // KKAPI authoritative answer for "are we in an H scene right now"
            if (!GameAPI.InsideHScene)
            {
                LogI("Hotkey pressed but not in HScene (GameAPI.InsideHScene=false).");
                return;
            }

            if (TimeStopController.Instance == null)
            {
                // Inside HScene per KKAPI, but our controller hasn't bound yet.
                // Should be vanishingly rare — KKAPI fires OnStartH after a 1-frame yield.
                LogW("InsideHScene but TimeStopController not bound (race?).");
                return;
            }

            LogI($"Hotkey pressed (frozen={TimeStopController.Instance.IsFrozen}).");
            TimeStopController.Instance.Toggle();
        }
    }
}
