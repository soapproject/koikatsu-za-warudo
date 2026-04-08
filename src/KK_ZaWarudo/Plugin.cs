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
        internal static ConfigEntry<float> ToggleCooldown;
        internal static ConfigEntry<ResumeMode> Mode;
        internal static ConfigEntry<float> AccumulationRate;
        internal static ConfigEntry<bool> ClimaxFaceOnResume;
        internal static ConfigEntry<int> ClimaxEyesPtn;
        internal static ConfigEntry<int> ClimaxMouthPtn;
        internal static ConfigEntry<int> ClimaxEyebrowPtn;
        internal static ConfigEntry<int> ClimaxTearsLv;

        // Audio
        internal static ConfigEntry<string> SfxFolder;
        internal static ConfigEntry<string> EnterSfxFile;       // 1. 時停開始
        internal static ConfigEntry<string> DuringSfxFile;      // 2. 過程中女角 (loop)
        internal static ConfigEntry<string> ExitSfxFile;        // 3. 時停結束
        internal static ConfigEntry<string> FemaleResumeSfxFile;// 4. 結束時女角
        internal static ConfigEntry<float> SfxVolume;
        internal static ConfigEntry<bool> PlayDuringLoop;
        internal static ConfigEntry<bool> DuringOnlyWhileActive;

        internal static Plugin Instance;
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ToggleKey = Config.Bind("General", "Toggle Key",
                new KeyboardShortcut(KeyCode.T),
                "Press during HScene to freeze/unfreeze.");

            ToggleCooldown = Config.Bind("General", "Toggle Cooldown",
                0.3f,
                new ConfigDescription("Minimum seconds between freeze/unfreeze toggles. Prevents SFX chopping and gauge spam from rapid presses.",
                    new AcceptableValueRange<float>(0f, 5f)));

            Mode = Config.Bind("General", "Resume Mode",
                ResumeMode.Accumulated,
                "Instant = jam female gauge to 100 on resume. Accumulated = add (duration * rate).");

            AccumulationRate = Config.Bind("General", "Accumulation Rate",
                10f,
                new ConfigDescription("Gauge points per second of frozen time (Accumulated mode only).",
                    new AcceptableValueRange<float>(0f, 100f)));

            ClimaxFaceOnResume = Config.Bind("Climax Face", "Enable",
                false,
                "Force the female to make a climax face on resume. Held until the resume SFX finishes, then released to the game.");

            ClimaxEyesPtn = Config.Bind("Climax Face", "Eyes Pattern",
                4,
                new ConfigDescription("ChaControl.ChangeEyesPtn index. KK pattern numbers vary; iterate to taste.",
                    new AcceptableValueRange<int>(0, 20)));

            ClimaxMouthPtn = Config.Bind("Climax Face", "Mouth Pattern",
                5,
                new ConfigDescription("ChaControl.ChangeMouthPtn index.",
                    new AcceptableValueRange<int>(0, 20)));

            ClimaxEyebrowPtn = Config.Bind("Climax Face", "Eyebrow Pattern",
                4,
                new ConfigDescription("ChaControl.ChangeEyebrowPtn index.",
                    new AcceptableValueRange<int>(0, 20)));

            ClimaxTearsLv = Config.Bind("Climax Face", "Tears Level",
                3,
                new ConfigDescription("ChaControl.tearsLv (0=none, 3=max).",
                    new AcceptableValueRange<int>(0, 3)));

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

            PlayDuringLoop = Config.Bind("Audio", "Play During Loop",
                true,
                "Play the female 'during' voice loop while frozen. Disable for true silence after Enter SFX (the loop is what playtesters sometimes mistake for leaked game audio).");

            DuringOnlyWhileActive = Config.Bind("Audio", "During Loop Only While Active",
                true,
                "Only play the during loop while the player is actively touching the female (HandCtrl.IsItemTouch / IsAction). Otherwise the loop plays continuously while frozen. Set false for unconditional loop.");

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

        // F10 sub: dump female gauge value once per second to verify Accumulation Rate
        // doesn't leak into non-frozen time. Set Plugin.GaugeDumpEnabled true to enable.
        // Should show monotonic game-driven progression while NOT frozen, with a
        // single discrete jump at each Resume(). Any other shape = bug.
        internal const bool GaugeDumpEnabled = true;
        private float _lastGaugeDump;
        private void DumpGaugeIfNeeded()
        {
            if (!GaugeDumpEnabled) return;
            var now = Time.realtimeSinceStartup;
            if (now - _lastGaugeDump < 1f) return;
            _lastGaugeDump = now;
            var inst = TimeStopController.Instance;
            if (inst == null) return;
            try
            {
                var flags = inst.Flags;
                if (flags == null) return;
                LogI($"[gauge] f={flags.gaugeFemale:F1} m={flags.gaugeMale:F1} speedCalc={flags.speedCalc:F2} frozen={inst.IsFrozen}");
            }
            catch { }
        }

        private void Update()
        {
            DumpGaugeIfNeeded();
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
