using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_ZaWarudo
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("Koikatu")]
    [BepInProcess("Koikatsu Party")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "weiss.kk.zawarudo";
        public const string PluginName = "KK_ZaWarudo";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Logger;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;

            ToggleKey = Config.Bind(
                "General",
                "Toggle Key",
                new KeyboardShortcut(KeyCode.T),
                "Press to freeze/unfreeze time during H-scene.");

            _harmony = Harmony.CreateAndPatchAll(typeof(Hooks), GUID);
            Logger.LogInfo($"{PluginName} {Version} loaded.");
        }

        private void OnDestroy()
        {
            TimeStopController.Instance?.Resume();
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (ToggleKey.Value.IsDown())
                TimeStopController.Instance?.Toggle();
        }
    }
}
