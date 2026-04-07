# KK_ZaWarudo — Boundary Spec (v0.1)

## One-line definition
Trigger a "time stop" inside Koikatsu H-scenes: freeze every other character's animation and physics while the player (protagonist) can still move, then on resume inject pleasure gauge and play special SFX.

## Inspiration
- JoJo's "ZA WARUDO" / "Toki yo tomare" ("Time, halt!")
- Doujin works in the 学園で時間よ止まれ ("Time stops at school") genre — H-scenes built around stopped time

---

## Core decisions
- **Target game**: Koikatsu (KK) / Koikatsu Party. **Not** KKS (Koikatsu Sunshine).
- **Active scope**: Only inside H-scenes (`HSceneProc` or `VRHScene` exists). No effect in Studio / Maker / Main game.
- **Protagonist definition**: `HSceneProc.male`. Even in female-protagonist positions this is treated as "the player camera owner" and is **never frozen**.
- **Time axis**: Do NOT touch `Time.timeScale` (would break camera/UI). Only mutate per-Animator speed, prefix-skip KK subsystems, and pause physics.
- **BGM**: Muted along with everything else during freeze (via `AudioListener.pause = true`). Per-AudioSource silencing missed too many sources (F2 feedback), so the global mute is the simpler correct answer. Plugin SFX uses `AudioSource.ignoreListenerPause = true` to bypass the global pause.
- **Player freedom while frozen**: Position changes, UI interaction, and **swapping the active female partner** (3P/4P) are all allowed.
- **Gauge injection target**: `HFlag.gaugeFemale` (the pleasure amount, not a sensitivity multiplier).
- **Never trigger finish ourselves**: After Instant mode pegs the gauge to 100, whether the game enters orgasm is left to the game / other plugins.
- **VR support**: KKAPI's `GameCustomFunctionController` automatically dispatches H-scene events for both `HSceneProc` and `VRHScene`. We additionally Harmony-patch `VRHScene.ChangeAnimator` (via `Type.GetType` reflection so non-VR builds don't crash on plugin load).
- **HScene lifecycle**: We do NOT patch `HSceneProc.MapSameObjectDisable` / `OnDestroy` ourselves. Instead we use KKAPI's `OnStartH(MonoBehaviour, HFlag, bool)` / `OnEndH` callbacks (see NOTES.md "What KKAPI already provides").

---

## In Scope

### 1. Trigger / Release
- Hotkey toggle (default `T`, configurable via ConfigurationManager).
- A single H-scene may be frozen and unfrozen any number of times.

### 2. Freeze
Frozen subjects = every entry in `lstFemale` plus non-protagonist males (`male1`, present in darkness mode). The protagonist (`male`) is never touched.

| Step | Target | Action |
|---|---|---|
| 1 | Subject `Animator`s | `animBody.speed = 0` and `animTongueEx.speed = 0` (the only two Animators ChaInfo actually exposes — verified via ilspy) |
| 2 | `HFlag.speedCalc` | Cached, then set to 0 (halts the gauge tick) |
| 3 | Subject `DynamicBone` / `DynamicBone_Ver02` | HashSet-cache enabled state and disable (hair, clothes, breast physics) |
| 4 | Every `ParticleSystem` under the H-scene root | HashSet-cache and `Pause(true)` (sweat, fluids) |
| 4b | The current mouth voice slot of each subject | `Manager.Voice.Instance.Stop(flags.transVoiceMouth[i])` to kill in-flight voice |
| 4c | Every `AudioSource` in each subject's hierarchy | HashSet-cache and `Pause()` (defensive — KK doesn't actually attach voice here, see R5) |
| — | `HVoiceCtrl.VoiceProc` / `BreathProc` | Harmony **prefix** short-circuits these (returns false while frozen) so no new voice/breath gets queued |
| — | `HMotionEyeNeckFemale.Proc` / `HMotionEyeNeckMale.Proc` | Harmony **prefix** short-circuits — freezes eye-tracking the camera, head turning, and expression / mouth-shape / eyebrow / tears patterns |
| — | `HSeCtrl.Proc` | Harmony **prefix** short-circuits — blocks slap / body-contact SE |
| 5 | Custom audio | Trigger the Freeze SFX sequence (Enter → During loop) |

We record `freezeStartTime = Time.realtimeSinceStartup` on entry. Every mutated Component is cached in a HashSet/Dictionary so Resume can restore it cleanly. HashSet dedupe means `ReapplyIfFrozen` after `ChangeAnimator` won't grow the cache.

### 3. Switching partner / position while frozen
A postfix on `HSceneProc.ChangeAnimator` re-runs steps 1, 3, 4, 4b, and 4c (`ReapplyIfFrozen()`) on the (possibly new) active set so newly-bound subjects and the new mouth voice slot are also frozen. The HashSet caches handle dedupe automatically. Both `HSceneProc.ChangeAnimator` and `VRHScene.ChangeAnimator` are patched.

### 4. Resume
In order:
1. Restore every cached state (animator speed, bones, particles, AudioSources, `speedCalc`).
2. Inject `HFlag.gaugeFemale` per **ResumeMode**:
   - **`Instant`**: set straight to 100.
   - **`Accumulated`**: `delta = (Time.realtimeSinceStartup - freezeStartTime) * AccumulationRate`, added to the current gauge (capped at 100).
3. Trigger the Resume SFX sequence (Exit → Female Resume).
4. Defer game voice unmute until the resume SFX finishes (`_voiceMuteUntil = now + ResumeSfxLength`) — otherwise the game's moan would kick in the instant `_frozen` flips false and overlap our SFX.
5. Optionally (`Climax Face On Resume`): inject a climax face onto every female via `ChangeEyebrowPtn` / `ChangeEyesPtn` / `ChangeMouthPtn` / `tearsLv = max`, and pin it for the resume SFX duration so `HMotionEyeNeck.Proc` doesn't overwrite it on the next frame.
6. Clear caches.

### 5. Configuration
| Section | Key | Type | Default | Notes |
|---|---|---|---|---|
| General | Toggle Key | KeyboardShortcut | `T` | Hotkey |
| General | Toggle Cooldown | float (0–5) | `0.3` | Minimum seconds between toggles. Prevents SFX chopping and gauge spam from rapid presses. |
| General | Resume Mode | enum {Instant, Accumulated} | `Accumulated` | How gauge is injected on resume |
| General | Accumulation Rate | float | `10.0` | Gauge points per frozen second (Accumulated only) |
| Climax Face | Enable | bool | `false` | Force a climax face on the female on resume, held for the duration of the resume SFX |
| Climax Face | Eyes Pattern | int (0–20) | `4` | `ChangeEyesPtn` index |
| Climax Face | Mouth Pattern | int (0–20) | `5` | `ChangeMouthPtn` index |
| Climax Face | Eyebrow Pattern | int (0–20) | `4` | `ChangeEyebrowPtn` index |
| Climax Face | Tears Level | int (0–3) | `3` | `ChaControl.tearsLv` (3 = max) |
| Audio | SFX Folder | string | `<PluginPath>/bgm/zawarudo/` | SFX directory (mirrors SlapMod's `bgm/` convention) |
| Audio | 1. Enter SFX | string | `zawarudo_sfx_enter.wav` | **Start-of-freeze** SFX (first clip in the freeze sequence) |
| Audio | 2. Female During SFX (loop) | string | `zawarudo_female_during.wav` | Female voice **during** freeze. Plays after Enter finishes, **loops until Resume** |
| Audio | 3. Exit SFX | string | `zawarudo_sfx_exit.wav` | **End-of-freeze** SFX (first clip in the resume sequence — interrupts the During loop) |
| Audio | 4. Female Resume SFX | string | `zawarudo_female_resume.wav` | Female voice on resume, plays after Exit finishes |
| Audio | SFX Volume | float (0–1) | `1.0` | Relative volume; multiplied by the game master volume |

**Filename convention**: `zawarudo_<role>_<phase>.wav`
- `role` ∈ { `sfx`, `female` }
- `phase` ∈ { `enter`, `during`, `exit`, `resume` }
- The `zawarudo_` prefix prevents collisions with other plugins sharing the `bgm/` directory.

**Playback sequence (single AudioSource — physically cannot overlap)**:
- **Freeze**: `Enter (one-shot, awaited)` → `During (loops until cancelled)`
- **Resume**: `cancel During` → `Exit (one-shot, awaited)` → `Female Resume (one-shot)`

### 6. Audio loading pattern
Mirrors [references/SlapMod/SlapMod.decompiled.cs:288](../references/SlapMod/SlapMod.decompiled.cs#L288):
```csharp
// 1. Loaded asynchronously from a coroutine kicked off in Plugin.Awake.
//    Missing files are silently skipped (warning to log).
WWW www = new WWW(Utility.ConvertToWWWFormat(path));
yield return www; // let the main thread pump WWW to completion
AudioClip clip = WWWAudioExtensions.GetAudioClip(www, false, false, AudioType.WAV);

// 2. AudioSource attached to the plugin GameObject
audioSource = gameObject.AddComponent<AudioSource>();

// 3. At play time: volume = game master * our config * 0.01
audioSource.volume = Manager.Config.SoundData.Master.Volume * SfxVolume.Value * 0.01f;
audioSource.PlayOneShot(clip);
```
The plugin **does not ship copyrighted audio**. Users drop their own wavs into `bgm/zawarudo/`.

### 7. Logging
All log lines carry the `ZAWA>` prefix so the plugin's output can be grepped out of `BepInEx/LogOutput.log` despite the noise from other plugins.

---

## Out of scope (v0.1)
- ❌ Full-screen color invert / grayscale visual effect (deferred to v0.2)
- ❌ Time stop in Studio / Maker / Main game (school exploration)
- ❌ Koikatsu Sunshine (KKS) support
- ❌ VR mode is best-effort: not guaranteed but should not actively break
- ❌ Networking / save data
- ❌ Forced finish triggering or any character-state interaction beyond pure freeze + gauge injection

---

## Acceptance criteria (v0.1 done = all of the below hold)
- [ ] Enter HScene → press hotkey → female is fully still (animation, hair, clothing, sweat); male can still move
- [ ] Press hotkey again → female resumes motion and gauge is injected per the configured ResumeMode
- [ ] Switching position / partner while frozen → newly-active female stays frozen
- [ ] Custom SFX plays once on freeze and once on resume (when files exist)
- [ ] 10 rapid freeze/unfreeze cycles → no memory leaks, no NREs
- [ ] Exit HScene and re-enter → still works
- [ ] Every config entry visible in ConfigurationManager
- [ ] `grep ZAWA> LogOutput.log` cleanly traces the entire freeze/resume lifecycle

---

## Primary references
- [references/KK_HSceneOptions/](../references/KK_HSceneOptions/) — `Hooks.cs`: the `HVoiceCtrl.VoiceProc/BreathProc` prefix-mute pattern is lifted directly from [Hooks.cs:120](../references/KK_HSceneOptions/KK_HSceneOptions/Hooks.cs#L120). `Hooks_VR.cs` confirmed `VRHScene` field names match `HSceneProc`.
- [references/KK_Plugins/](../references/KK_Plugins/) — csproj / build conventions; source of the IllusionMods NuGet feed URL.
- [references/IllusionModdingAPI/](../references/IllusionModdingAPI/) — `GameCustomFunctionController` provides the `OnStartH/OnEndH` callbacks (automatically VR-aware).
- [references/SlapMod/SlapMod.decompiled.cs](../references/SlapMod/SlapMod.decompiled.cs) — WAV loading + AudioSource playback pattern.

## KK internal classes patched directly (discovered via ilspy)
| Class | Purpose | Patch type |
|---|---|---|
| `HSceneProc.ChangeAnimator` | Position / animation switch | postfix → `ReapplyIfFrozen` |
| `VRHScene.ChangeAnimator` | VR variant of the above | postfix (manually patched via reflection) |
| `HVoiceCtrl.VoiceProc` | Female voice queue | prefix → return false (when frozen) |
| `HVoiceCtrl.BreathProc` | Female breath/sigh queue | prefix → return false (when frozen) |
| `HMotionEyeNeckFemale.Proc` | Per-frame female eye/neck/expression driver | prefix → return false (when frozen, or when face-held during resume SFX) |
| `HMotionEyeNeckMale.Proc` | Male equivalent | prefix → return false (when frozen) |
| `HSeCtrl.Proc` | Per-frame slap / body-contact SE | prefix → return false (when frozen) |
