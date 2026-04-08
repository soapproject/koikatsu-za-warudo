using System.Collections.Generic;
using HarmonyLib;
using KKAPI.MainGame;
using UnityEngine;

namespace KK_ZaWarudo
{
    /// <summary>
    /// KKAPI-managed lifecycle controller. Replaces our manual MapSameObjectDisable
    /// + OnDestroy Harmony hooks. KKAPI fires OnStartH/OnEndH for both HSceneProc
    /// and VRHScene, so this gives us VR support for free.
    ///
    /// We still Traverse for lstFemale / male / male1 because KKAPI doesn't
    /// surface those private fields.
    /// </summary>
    internal class ZaWarudoController : GameCustomFunctionController
    {
        protected override void OnStartH(MonoBehaviour proc, HFlag hFlag, bool vr)
        {
            try
            {
                var trav = Traverse.Create(proc);
                var females = trav.Field("lstFemale").GetValue<List<ChaControl>>();
                var male = trav.Field("male").GetValue<ChaControl>();
                // male1 only exists in darkness builds — Traverse returns null on miss
                var male1 = trav.Field("male1").GetValue<ChaControl>();

                var extras = new List<ChaControl>();
                if (male1 != null) extras.Add(male1);

                // Hand controllers — used to detect player action intensity for the
                // during-loop gating. HSceneProc and VRHScene both expose `hand`/`hand1`
                // as public HandCtrl fields. Traverse returns null on miss, IsPlayerActing
                // tolerates that.
                var hand0 = trav.Field("hand").GetValue<HandCtrl>();
                var hand1 = trav.Field("hand1").GetValue<HandCtrl>();

                // HVoiceCtrl is `voice` on HSceneProc (and VRHScene). Used by the
                // state-machine debug dump to read nowVoices[i].state, and by the
                // F18 unblock helper to spoof voice slots.
                var voiceCtrl = trav.Field("voice").GetValue<HVoiceCtrl>();

                // lstProc holds the active HActionBase instances (HSonyu/HHoushi/HAibu/etc).
                // Used by F18 unblock helper to find the active proc and invoke its
                // LoopProc reflectively (KK_HSceneOptions.AnimationToggle.FindProc pattern).
                var lstProc = trav.Field("lstProc").GetValue<List<HActionBase>>();

                Plugin.LogI($"OnStartH (vr={vr}, freeH={hFlag?.isFreeH}): females={females?.Count ?? -1} male={(male != null ? male.name : "null")} extraMales={extras.Count} hand0={(hand0 != null)} hand1={(hand1 != null)} voice={(voiceCtrl != null)} lstProc={lstProc?.Count ?? -1}");
                TimeStopController.Bind(proc, females, male, extras, hFlag, hand0, hand1, voiceCtrl, lstProc);
            }
            catch (System.Exception e)
            {
                Plugin.LogE($"OnStartH failed: {e}");
            }
        }

        protected override void OnEndH(MonoBehaviour proc, HFlag hFlag, bool vr)
        {
            Plugin.LogI($"OnEndH (vr={vr}): unbinding.");
            TimeStopController.Unbind();
        }
    }
}
