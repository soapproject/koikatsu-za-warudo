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
        internal static ConfigEntry<KeyboardShortcut> UnblockOrgasmKey;
        internal static ConfigEntry<float> ToggleCooldown;
        internal static ConfigEntry<ResumeMode> Mode;
        internal static ConfigEntry<float> AccumulationRate;
        internal static ConfigEntry<float> AccumulationCap;
        internal static ConfigEntry<bool> ClimaxFaceOnResume;
        internal static ConfigEntry<int> ClimaxEyesPtn;
        internal static ConfigEntry<int> ClimaxMouthPtn;
        internal static ConfigEntry<int> ClimaxEyebrowPtn;
        internal static ConfigEntry<int> ClimaxTearsLv;
        internal static ConfigEntry<bool> AnimatorTransitionWindow;
        internal static ConfigEntry<float> AnimatorTransitionTimeoutSec;

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

            UnblockOrgasmKey = Config.Bind("General", "Unblock Orgasm Key",
                new KeyboardShortcut(KeyCode.U),
                "F18 escape hatch: when an orgasm sequence is stuck during freeze (animator state can't advance), press this to spoof voice slots and force the state machine forward via reflection-invoked HActionBase.LoopProc(true). Only does anything when frozen AND flags.finish != none.");

            Mode = Config.Bind("General", "Resume Mode",
                ResumeMode.Accumulated,
                "Instant = jam female gauge to 100 on resume. Accumulated = add (duration * rate).");

            AccumulationRate = Config.Bind("General", "Accumulation Rate",
                2f,
                new ConfigDescription("Gauge points per second of frozen time (Accumulated mode only). Default 2 means a 10s freeze adds 20 gauge — gentle. Crank to 10+ if you want freezes to ramp up gauge fast.",
                    new AcceptableValueRange<float>(0f, 100f)));

            AccumulationCap = Config.Bind("General", "Accumulation Cap",
                65f,
                new ConfigDescription("Hard ceiling for the gauge value Resume can inject up to (Accumulated mode only). Defaults to 65 — just below the 70 orgasm threshold — so you can't accidentally trigger an orgasm by holding a long freeze. Set to 100 to disable the cap.",
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

            AnimatorTransitionWindow = Config.Bind("Advanced", "Animator Transition Window",
                true,
                "F21 fix: when ChangeAnimator fires while frozen (player switches position / partner), let the female animator briefly run at normal speed so the new state can actually start playing, then re-pin. Without this, the female stays glued to her last pose at the OLD position. All other locks (head pin, voice mute, blink) stay engaged through the window.");

            AnimatorTransitionTimeoutSec = Config.Bind("Advanced", "Animator Transition Timeout",
                1.0f,
                new ConfigDescription("Maximum seconds to wait for the new animator state to settle before forcibly re-pinning. Prevents a missed state transition from hanging the freeze forever.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

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
        // Lead E: per-frame state-machine snapshot for triaging F10/F16/F21 etc.
        // Set true to dump animState/click/voiceWait/voice slots/finish at 1 Hz.
        // Off by default — only enable while actively debugging state machine issues.
        internal const bool StateMachineDumpEnabled = true;
        private float _lastGaugeDump;
        private float _lastStateDump;
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

        private void DumpStateMachineIfNeeded()
        {
            if (!StateMachineDumpEnabled) return;
            var now = Time.realtimeSinceStartup;
            if (now - _lastStateDump < 1f) return;
            _lastStateDump = now;
            var inst = TimeStopController.Instance;
            if (inst == null) return;
            try
            {
                var flags = inst.Flags;
                if (flags == null) return;
                var f0 = inst.GetFirstFemale();
                string animState = "?";
                float animT = -1f;
                if (f0 != null && f0.animBody != null)
                {
                    var info = f0.animBody.GetCurrentAnimatorStateInfo(0);
                    animT = info.normalizedTime;
                    animState = flags.nowAnimStateName ?? "?";
                }
                var voice = inst.Voice;
                int v0 = voice != null && voice.nowVoices != null && voice.nowVoices.Length > 0
                    ? (int)voice.nowVoices[0].state : -1;
                int v1 = voice != null && voice.nowVoices != null && voice.nowVoices.Length > 1
                    ? (int)voice.nowVoices[1].state : -1;
                LogI($"[sm] anim={animState} t={animT:F2} click={flags.click} voiceWait={flags.voiceWait} v0={v0} v1={v1} finish={flags.finish} frozen={inst.IsFrozen}");
            }
            catch (System.Exception e)
            {
                // Don't spam errors if a field is missing on some build — disable silently for the session
                LogW($"[sm] dump failed once: {e.Message}");
            }
        }

        // Re-pin neck-chain bone rotations every LateUpdate while frozen.
        // NeckLookControllerVer2 is disabled (see TimeStopController.FreezeNeckLook),
        // so there's no LateUpdate writer competing with us — only the animator,
        // which runs in Animate stage BEFORE LateUpdate. Our write here is the
        // last word for the frame.
        //
        // Without this, the head appears to "reset to a default position" on freeze
        // because the animator's WLoop neck pose (without NeckLook's camera-track
        // overlay) becomes visible. The pin reapplies the freeze-moment rotation
        // (which IS the camera-tracked one from NeckLook's last LateUpdate before
        // we disabled it).
        private void LateUpdate()
        {
            TimeStopController.Instance?.PinNeckBonesLate();
        }

        private void Update()
        {
            DumpGaugeIfNeeded();
            DumpStateMachineIfNeeded();

            // F18 escape hatch — only does anything if we're frozen mid-orgasm
            if (UnblockOrgasmKey.Value.IsDown())
            {
                LogI("Unblock-orgasm hotkey pressed.");
                TimeStopController.Instance?.UnblockStuckOrgasm();
            }

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
