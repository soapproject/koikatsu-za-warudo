# KK_ZaWarudo — Boundary Spec (v0.1)

## One-line definition
Trigger a "time stop" inside Koikatsu H-scenes: freeze every other character's animation, physics, voice, and gauge tick while the protagonist can still move; on resume inject the pleasure gauge and play special SFX.

## Inspiration
- JoJo's "ZA WARUDO" / "Toki yo tomare" ("Time, halt!")
- Doujin works in the 学園で時間よ止まれ ("Time stops at school") genre — H-scenes built around stopped time

---

## Core decisions

- **Target game**: Koikatsu (KK) / Koikatsu Party. **Not** KKS (Koikatsu Sunshine).
- **Active scope**: Only inside H-scenes (`HSceneProc` or `VRHScene` exists). No effect in Studio / Maker / Main game.
- **Protagonist definition**: `HSceneProc.male`. Even in female-protagonist positions this is treated as "the player camera owner" and is **never frozen**.
- **Time axis**: Do NOT touch `Time.timeScale` (would break camera/UI). Only mutate per-Animator speed, prefix-skip selected KK subsystems, and use `AudioListener.pause` for global mute.
- **Audio**: BGM is muted along with everything else during freeze (via `AudioListener.pause = true`). Plugin SFX uses `AudioSource.ignoreListenerPause = true` to bypass the global pause. The original spec idea ("keep BGM playing") was dropped after F2 playtest feedback because per-source silencing missed too many sources.
- **Player freedom while frozen**: Position changes, swapping the active female partner (3P/4P), and touching/grabbing for Boop or HandCtrl interaction are allowed at the engine level. (Note: free H HSprite UI clicks are currently blocked — see `docs/NOTES.md` F10 / F11.)
- **Gauge injection target**: `HFlag.gaugeFemale` (the pleasure amount, not a sensitivity multiplier). The gauge does NOT tick during freeze (see step 6 below).
- **Never trigger finish ourselves**: After Instant mode pegs the gauge to 100, whether the game enters orgasm is left to the game / other plugins.
- **VR support**: KKAPI's `GameCustomFunctionController` automatically dispatches H-scene events for both `HSceneProc` and `VRHScene`. We additionally Harmony-patch `VRHScene.ChangeAnimator` (via `Type.GetType` reflection so non-VR builds don't crash on plugin load).
- **HScene lifecycle**: We do NOT patch `HSceneProc.MapSameObjectDisable` / `OnDestroy` ourselves. Instead we use KKAPI's `OnStartH(MonoBehaviour, HFlag, bool)` / `OnEndH` callbacks (see NOTES.md "What KKAPI already provides").

---

## In Scope

### 1. Trigger / Release
- Hotkey toggle (default `T`, configurable via ConfigurationManager).
- Per-toggle debounce cooldown (default 0.3 s) to prevent rapid-press SFX chopping.
- A single H-scene may be frozen and unfrozen any number of times.

### 2. Frozen subjects
Frozen subjects = every entry in `lstFemale` plus non-protagonist males (`male1`, present in darkness mode). The protagonist (`HSceneProc.male`) is never touched. The set is recomputed on `HSceneProc.ChangeAnimator` so partner/position swaps stay frozen.

### 3. Freeze (steps run in order)

| Step | Target | Action |
|---|---|---|
| 1 | Subject `Animator`s | `animBody.speed = 0` and `animTongueEx.speed = 0` (the only two Animators ChaInfo exposes — verified via ilspy) |
| 2 | `HFlag.speedCalc` + gauge locks | `speedCalc` cached and set to 0 (game overwrites this every frame, mostly informational). `lockGugeFemale` and `lockGugeMale` cached and set to true as a belt-and-braces partner for the step-6 prefix. |
| 3 | Subject `DynamicBone` / `DynamicBone_Ver02` | **No-op** (intentional) so hair/cloth keep simulating and gradually drape under gravity instead of locking mid-swing. |
| 4 | Every `ParticleSystem` under the H-scene root | `EmissionModule.enabled = false` cached + restored — existing particles keep simulating (gravity, lifetime), only new spawns are suppressed. |
| 4b | Each subject's current mouth voice slot | `Manager.Voice.Instance.Stop(flags.transVoiceMouth[i])` to kill in-flight voice. |
| 4c | Every `AudioSource` under each subject | `Pause()` cached + restored. Defensive — KK doesn't actually attach voice here, but cheap insurance. |
| 4d | Global audio | `AudioListener.pause = true`. Mutes the entire game (BGM, ambient, voice, body SE). Plugin SFX bypasses via `ignoreListenerPause = true`. |
| 4e | Auto-blink | `ChangeEyesBlinkFlag(false)` per subject, cached + restored. Stops `fbsCtrl.BlinkCtrl` from auto-blinking. |
| 4e2 | Neck-look + head pin | `chaCtrl.neckLookCtrl.neckLookScript.skipCalc = true` per subject (in-game's own "stop calculating" flag, cached + restored). Plus snapshot the head bone localRotation so `Plugin.LateUpdate` can re-pin it every frame, defeating any residual writer that would otherwise let the head drift. |
| 4f | In-progress face | Snapshot `eyesPtn` / `mouthPtn` / `eyebrowPtn` / `tearsLv` / `eyesOpenMax` per subject. Re-applied on `ReapplyIfFrozen` so an in-progress ahegao isn't reverted. |
| 5 | Custom audio | Trigger the Freeze SFX coroutine (Enter → During loop). |
| 6 | Pleasure gauge tick | Harmony **prefix** on `HFlag.FemaleGaugeUp` and `HFlag.MaleGaugeUp` returns false while frozen, so the simulation backend's per-frame gauge increments are blocked. Resume's `InjectGauge` writes `gaugeFemale` directly and bypasses the prefix. |

In addition to the per-step state, the following **Harmony prefixes** are active for the entire freeze duration:

| Class.Method | Why |
|---|---|
| `HVoiceCtrl.VoiceProc` / `BreathProc` | Block new voice / breath being queued. |
| `HMotionEyeNeckFemale.Proc` / `HMotionEyeNeckMale.Proc` | Block per-frame writes to eye/neck/face/eyebrow/tears patterns. |
| `HSeCtrl.Proc` | Block slap / body-contact SE. |
| `EyeLookController.LateUpdate` | Block iris/pupil tracking the camera (separate component from `HMotionEyeNeck.Proc` and from neck rotation). |
| `HFlag.FemaleGaugeUp` / `MaleGaugeUp` | Block gauge increments from the still-running simulation backend. |
| `HSceneProc.ChangeAnimator` (postfix) | Re-pin freeze on partner / position switch. |
| `VRHScene.ChangeAnimator` (postfix, conditional) | Same, on VR builds. Patched via reflection at plugin load — absent on non-VR. |

`freezeStartTime = Time.realtimeSinceStartup` is recorded on entry. Every mutated Component is cached in a HashSet/Dictionary so Resume can restore it cleanly. HashSet dedupe means `ReapplyIfFrozen` after `ChangeAnimator` won't grow the cache.

### 4. Switching partner / position while frozen
A postfix on `HSceneProc.ChangeAnimator` re-runs steps 1, 3 (no-op), 4, 4b, 4c, 4e, and 4f via `ReapplyIfFrozen()` on the (possibly new) active set. The HashSet caches handle dedupe. Step 4d (`AudioListener.pause`) and the per-frame Harmony prefixes don't need re-application.

### 5. Resume
In order:
1. Restore every cached state in this order: animator speed, bones (no-op), particle emission, AudioSources, `AudioListener.pause`, blink flag, `speedCalc`. Drop the face snapshot.
2. Inject `HFlag.gaugeFemale` per **ResumeMode**:
   - **`Instant`**: set straight to 100.
   - **`Accumulated`**: `delta = (Time.realtimeSinceStartup - freezeStartTime) * AccumulationRate`, added to the current gauge (capped at 100).
3. Trigger the Resume SFX coroutine (Exit → Female Resume).
4. Defer the **game voice** unmute until the resume SFX finishes — `_voiceMuteUntil = now + ResumeSfxLength`. The voice prefix patches honor this so the game's moan doesn't kick in the instant `_frozen` flips false and overlap our SFX. (Face / eye / neck / SE all use the plain `Frozen()` check and resume immediately so the character visually comes back to life.)
5. Optionally (`Climax Face On Resume`): inject a climax face onto every female via `ChangeEyebrowPtn` / `ChangeEyesPtn` / `ChangeMouthPtn` / `tearsLv = max`, and pin it for the resume SFX duration so `HMotionEyeNeck.Proc` doesn't overwrite it on the next frame.
6. Clear caches.

### 6. Configuration

| Section | Key | Type | Default | Notes |
|---|---|---|---|---|
| General | Toggle Key | KeyboardShortcut | `T` | Hotkey |
| General | Toggle Cooldown | float (0–5) | `0.3` | Minimum seconds between toggles. Prevents SFX chopping and gauge spam from rapid presses. |
| General | Resume Mode | enum {Instant, Accumulated} | `Accumulated` | How gauge is injected on resume |
| General | Accumulation Rate | float (0–100) | `10.0` | Gauge points per frozen second (Accumulated only) |
| Climax Face | Enable | bool | `false` | Force a climax face on each female on resume, held for the duration of the resume SFX |
| Climax Face | Eyes Pattern | int (0–20) | `4` | `ChangeEyesPtn` index |
| Climax Face | Mouth Pattern | int (0–20) | `5` | `ChangeMouthPtn` index |
| Climax Face | Eyebrow Pattern | int (0–20) | `4` | `ChangeEyebrowPtn` index |
| Climax Face | Tears Level | int (0–3) | `3` | `ChaControl.tearsLv` (3 = max) |
| Audio | SFX Folder | string | `<PluginPath>/bgm/zawarudo/` | SFX directory (mirrors SlapMod's `bgm/` convention) |
| Audio | 1. Enter SFX | string | `zawarudo_sfx_enter.wav` | Played first on freeze |
| Audio | 2. Female During SFX (loop) | string | `zawarudo_female_during.wav` | Plays after Enter finishes; loops until Resume |
| Audio | 3. Exit SFX | string | `zawarudo_sfx_exit.wav` | First clip of the resume sequence (interrupts the During loop) |
| Audio | 4. Female Resume SFX | string | `zawarudo_female_resume.wav` | Plays after Exit finishes |
| Audio | SFX Volume | float (0–1) | `1.0` | Relative volume; multiplied by the game master volume |
| Audio | Play During Loop | bool | `true` | Master switch for the during loop. Disable for true silence between Enter and Exit. |
| Audio | During Loop Only While Active | bool | `true` | Only play the during loop while the player is actively touching the female (`HandCtrl.IsItemTouch` / `IsAction`). When false, the loop plays continuously while frozen. |

**Filename convention**: `zawarudo_<role>_<phase>.wav` where `role ∈ { sfx, female }` and `phase ∈ { enter, during, exit, resume }`. The `zawarudo_` prefix prevents collisions with other plugins sharing the `bgm/` directory. The plugin **does not ship copyrighted audio** — users drop their own wavs.

### 7. SFX playback model
A single `AudioSource` attached to the plugin GameObject — physically cannot overlap.

- **Freeze sequence**: `Enter` (one-shot, awaited) → `During` (loop, gated by `HandCtrl.IsItemTouch / IsAction` if `During Loop Only While Active = true`).
- **Resume sequence**: cancel any in-flight Enter / During → `Exit` (one-shot, awaited) → `Female Resume` (one-shot).

Audio loading mirrors [SlapMod](../references/SlapMod/SlapMod.decompiled.cs#L288):
```csharp
// Loaded asynchronously from a coroutine kicked off in Plugin.Awake.
// Missing files are silently skipped (warning to log).
WWW www = new WWW(new Uri(path).AbsoluteUri);
yield return www; // let the main thread pump WWW to completion
AudioClip clip = WWWAudioExtensions.GetAudioClip(www, false, false, AudioType.WAV);
audioSource.volume = Manager.Config.SoundData.Master.Volume * SfxVolume.Value * 0.01f;
audioSource.PlayOneShot(clip);
```

### 8. Logging
All log lines carry the `ZAWA>` prefix so the plugin's output can be grepped out of `BepInEx/LogOutput.log` despite noise from other plugins. Use `Plugin.LogI/LogW/LogE` — never `Logger.LogInfo` directly.

A 1 Hz `[gauge]` dump (toggleable via `Plugin.GaugeDumpEnabled`) prints `f=… m=… speedCalc=… frozen=…` for verifying the gauge does (or doesn't) tick.

---

## Out of scope (v0.1)

- ❌ Full-screen color invert / grayscale visual effect (deferred to v0.2)
- ❌ Time stop in Studio / Maker / Main game
- ❌ Koikatsu Sunshine (KKS) support
- ❌ Networking / save data
- ❌ Forced finish triggering or any character-state interaction beyond pure freeze + gauge injection
- ❌ Game-side voice triggering for the resume scream (no mouth movement / no subtitles / single line) — see NOTES F3
- ❌ Free H HSprite UI interaction during freeze (animator-frozen state machine doesn't tick) — see NOTES F10 / F11

---

## Acceptance criteria (v0.1 done = all of the below hold)

- [ ] Enter HScene → press hotkey → female is fully still (animation, head, blink, expression, voice, body SE); male can still move
- [ ] Hair / cloth / fluid particles already in motion continue to settle naturally instead of locking
- [ ] Press hotkey again → female resumes motion and gauge is injected per the configured ResumeMode
- [ ] Switching position / partner while frozen → newly-active female stays frozen
- [ ] During loop only plays while the player is actively touching (when `During Loop Only While Active = true`)
- [ ] Game voice does not overlap our resume scream — game voice unmutes only after the resume SFX finishes
- [ ] 10 rapid freeze/unfreeze cycles → no memory leaks, no NREs
- [ ] Exit HScene and re-enter → still works
- [ ] Every config entry visible in ConfigurationManager
- [ ] `grep ZAWA> LogOutput.log` cleanly traces the entire freeze/resume lifecycle, and the `[gauge]` dump shows `gaugeFemale` constant during freeze with one discrete jump on Resume

---

## Primary references

- [references/KK_HSceneOptions/](../references/KK_HSceneOptions/) — `Hooks.cs`: the `HVoiceCtrl.VoiceProc/BreathProc` prefix-mute pattern is lifted from [Hooks.cs:120](../references/KK_HSceneOptions/KK_HSceneOptions/Hooks.cs#L120). `Hooks_VR.cs` confirmed `VRHScene` field names match `HSceneProc`.
- [references/KK_Plugins/](../references/KK_Plugins/) — csproj / build conventions; source of the IllusionMods NuGet feed URL. Boop's source ([Boop.Core/Boop.cs](../references/KK_Plugins/src/Boop.Core/Boop.cs)) explained why Boop didn't work in v0.1 (we were disabling the DynamicBones it needed live).
- [references/IllusionModdingAPI/](../references/IllusionModdingAPI/) — `GameCustomFunctionController` provides the `OnStartH/OnEndH` callbacks (automatically VR-aware) and `GameAPI.InsideHScene`.
- [references/SlapMod/SlapMod.decompiled.cs](../references/SlapMod/SlapMod.decompiled.cs) — WAV loading + AudioSource playback pattern.
- [KK Plugins Compendium](https://github.com/Frostation/KK-Plugins-Compendium/blob/master/Plugins%20Compendium.md) — community index for "has anyone else solved this?" research.

## KK internal classes patched directly (discovered via ilspy)

| Class.Method | Patch type | Purpose |
|---|---|---|
| `HSceneProc.ChangeAnimator` | postfix | Re-pin freeze on partner / position switch (`ReapplyIfFrozen`) |
| `VRHScene.ChangeAnimator` | postfix (reflection, optional) | Same, on VR builds. Skipped if `VRHScene` type is missing. |
| `HVoiceCtrl.VoiceProc` | prefix | Block new voice queueing while frozen / during voice-mute window |
| `HVoiceCtrl.BreathProc` | prefix | Block new breath queueing while frozen / during voice-mute window |
| `HMotionEyeNeckFemale.Proc` | prefix | Freeze per-frame female eye/neck/face/eyebrow/tears (also held during resume SFX when `Climax Face On Resume`) |
| `HMotionEyeNeckMale.Proc` | prefix | Same for male slots |
| `HSeCtrl.Proc` | prefix | Block per-frame slap / body-contact SE |
| `EyeLookController.LateUpdate` | prefix | Block iris/pupil camera tracking |
| `HFlag.FemaleGaugeUp` | prefix | Block per-frame gauge tick from the still-running simulation backend |
| `HFlag.MaleGaugeUp` | prefix | Same for the male gauge |
