---
name: kk-plugin-dev
description: Verified Koikatsu / KKAPI / BepInEx knowledge for this plugin. Load this before changing code or proposing fixes for HScene-related features. Rule of thumb — if it's not in here or in NOTES.md, you have not verified it.
---

# KK plugin development knowledge base

This skill is the durable, verified-only knowledge base for working on `KK_ZaWarudo`. Anything claimed here has been confirmed via at least one of:

1. ilspy on `Koikatu_Data/Managed/Assembly-CSharp.dll` (the **first-hand** source)
2. ilspy on a real reference plugin DLL
3. Source-grep on a reference plugin clone in `references/`
4. Direct playtest evidence (a log line or behavior the user reproduced)

**If you're about to make a claim that doesn't trace to one of these, stop and verify first.** See the "verification flow" section below.

---

## Session startup procedure (READ THIS FIRST every time)

Before writing any code or proposing any fix, **in this order**:

1. **Read [`docs/SPEC.md`](../../../docs/SPEC.md)** end-to-end. It is the authoritative current design — freeze step list, configuration, KK classes patched, in-scope/out-of-scope. If your change conflicts with SPEC, either change SPEC first or you're going off-spec.

2. **Read [`docs/NOTES.md`](../../../docs/NOTES.md) "Status overview"** at the top to learn:
   - Which feedback IDs (F-series) are already fixed, deferred, or in-progress
   - Which historical bugs (B-series) have been fixed and *why*
   - Which risks (R-series) are known but unfixed
   - The "Architecture revision" section for the current plan on the hardest open issues
   - **Don't propose a fix that's already shipped, deferred, or known-broken** — the F-series table tells you which is which.

3. **Read this entire skill (SKILL.md)** for the verified KK API surface, hard rules, anti-patterns, reference plugin map, and the architecture-level constraint that drives most open issues.

4. **For any code change you're considering, do the API research FIRST** (don't iterate fixes blindly):
   - **ilspy** the relevant KK class(es). See "Verification flow" below for the commands.
   - **Grep `references/`** for any plugin that has already touched the same KK API. Copy their pattern (they've already debugged it).
   - **Check the [Reference plugin map](#reference-plugin-map-what-to-grep-for-what)** in this skill for "if you want to do X, look at Y" entries.
   - Only after you have at least one ilspy hit AND at least one reference-plugin precedent (or a clear comment that none exists) should you start writing the patch.

5. **When you ship a change, you MUST update the docs in the same commit**:
   - **SPEC.md** if you changed the freeze/resume step list, added/removed/renamed a config, added/removed a Harmony patch target, or changed an architectural decision in the "Core decisions" section.
   - **NOTES.md** if you fixed an F-series feedback item (update the Status overview table emoji, write the "how" in the detail section), added a new bug, learned a new risk (add to R-series), discovered a new anti-pattern (add to dev rules), or changed the architecture plan.
   - **SKILL.md** (this file) if you verified a new piece of KK API or KKAPI helper that's worth remembering, found a new useful reference plugin pattern, or added a new hard rule. **Skill content must be evidence-backed** — never write speculation here.
   - The commit is incomplete if it changes code without updating these.

6. **Run the [Pre-flight checklist](#pre-flight-checklist-before-committing-a-fix)** at the bottom of this skill before every commit.

If you skip any of these steps you're in the failure mode that produced bugs B5 (`animFace` reflection lookup that did nothing because nobody verified the field existed) and B7 (`_extraMales` comment fabricating "KPlug additions" that didn't exist). Both were avoidable with a 60-second ilspy check.

---

## Hard rules (DO NOT BREAK)

These map 1:1 to the bug postmortems in [`docs/NOTES.md`](../../../docs/NOTES.md).

1. **Target framework MUST be `net35`.** KK runs Mono / .NET 3.5. `net46` builds compile but the C# compiler emits calls to `Array.Empty<T>()` which doesn't exist in 3.5 → `MissingMethodException` at `Awake`. Cause of B1.
2. **No spin-waiting on Unity APIs from the main thread.** `WWW`, asset loaders, etc. need the main thread pump to make progress. Use `yield return` in a coroutine. Cause of B2.
3. **Caches that get re-touched must be `HashSet<T>` or `Dictionary<K,V>`.** `List.Add` plus dedupe-by-hope grows linearly. Cause of B3. Especially relevant to `ReapplyIfFrozen`.
4. **Singleton `Bind`/`Init` must be idempotent.** If `Instance != null`, `Unbind()` first. Cause of B4.
5. **The protagonist (`HSceneProc.male`) is NEVER added to the frozen-subjects set.** Hardcoded in `FrozenSubjects()`.
6. **Every log line carries `ZAWA>` prefix.** Use `Plugin.LogI/LogW/LogE`, never `Logger.LogInfo` directly. Makes our output greppable amid other plugins' noise.
7. **`UnpatchSelf` on `OnDestroy`.** Otherwise reload stacks old patches.
8. **Don't invent KK API field/method names from memory.** ilspy first. Cause of B5/B7.
9. **When `Freeze()` step list changes, update `ReapplyIfFrozen()` in the same commit.** Cause of B6. Long-term: extract a shared helper.
10. **Comments describe what code does NOW**, not aspirations. Cause of B7.
11. **Cross-verify external suggestions** (collaborator AI, forum advice, etc.). The problem report may be right but the proposed fix often rests on incomplete evidence. Run all 5 verification flow steps.
12. **No absolute paths in committed files.** `<KK_DIR>` / `$KK_DIR` placeholder, or `.env.local` (gitignored).
13. **`.claude/settings*.json` is gitignored. `.claude/skills/` is NOT** — share knowledge, hide personal config.
14. **Research the API before refactoring.** Don't iterate fixes blindly — survey reference plugins and ilspy first. (Why this skill exists.)

---

## Verified KK API surface (with ilspy line refs)

### `ChaInfo` / `ChaControl` Animators

ChaInfo exposes **exactly two** `Animator` properties:
```csharp
public Animator animBody     { get; protected set; }   // ChaInfo line ~246
public Animator animTongueEx { get; protected set; }   // line ~248
```

There is **no** `animFace`, `animOption`, or any other `anim*` field on ChaInfo or ChaControl.

- `animBody` is the canonical HScene animator. It uses Unity's layer system with the face controller layered on top of the body — `AnimationEvent`s on those layers (blinks, mouth shapes, expression changes) stop together with the master animator. Setting `animBody.speed = 0` is the canonical "freeze body + face/lip-sync events" lever.
- `animTongueEx` exists but the entire game assembly contains exactly 3 references to it: one declaration, one `objTongueEx.GetComponent<Animator>()` assignment, one cleanup-to-null. The game itself **never** calls `.speed`, `.Play`, `.SetTrigger`, etc. on it. Setting its speed=0 is a no-op in current KK. Keep it as belt-and-braces but label it as such — don't pretend it does anything.

### `HSceneProc` (and `VRHScene`) fields used

```csharp
private List<ChaControl> lstFemale;      // grab via Traverse
private ChaControl       male;           // protagonist — NEVER freeze
private ChaControl       male1;          // darkness mode only; null otherwise
private ChaControl       male2;          // does NOT exist on vanilla HSceneProc
public  HFlag            flags;
public  HandCtrl         hand;
public  HandCtrl         hand1;
public  HVoiceCtrl       voice;
public  HSprite          sprite;
public  List<HActionBase> lstProc;       // contains the active HSonyu/HHoushi/HAibu/etc instance(s)
```

`VRHScene` mirrors the same field names — verified against `KK_HSceneOptions/Hooks_VR.cs`. Use `Type.GetType("VRHScene, Assembly-CSharp")` for optional VR patching (KKAPI does this in `GameAPI.Hooks.SetupHooks`).

`lstFemale` / `male` / `male1` / `lstProc` are **private** — use `Traverse.Create(__instance).Field("lstFemale").GetValue<List<ChaControl>>()`.

### `HFlag` fields used

```csharp
public float                 speedCalc;          // line ~156088. Recomputed every frame from Time.deltaTime
                                                  // by HFlag.WaitSpeedProc. Setting to 0 manually is overwritten next frame.
public float                 gaugeFemale;
public float                 gaugeMale;
public bool                  lockGugeFemale;     // line ~156036. When true, FemaleGaugeUp early-returns unless _force=true.
public bool                  lockGugeMale;       // line ~156042. Same for MaleGaugeUp.
public bool                  voiceWait;          // gates state machine transitions; cleared only when
                                                  // (animator state name == Idle/Stop_Idle) AND IsCheckVoicePlay()
public ClickKind             click;              // pending click intent; processed when voiceWait clears
public Transform[]           transVoiceMouth;    // length-2; mouth transforms for voice playback
public AnimationInfo         nowAnimationInfo;
public string                nowAnimStateName;
public EMode                 mode;               // sonyu / houshi / aibu / lesbian / masturbation / sonyu3P / houshi3P / sonyu3PMMF / houshi3PMMF
public FinishKind            finish;             // none / inside / outside / orgW / sameW / orgS / sameS

public void FemaleGaugeUp(float _addPoint, bool _force, bool _isIdle = true);  // line 156544
public void MaleGaugeUp(float _addPoint);                                       // line 156566
public bool WaitSpeedProc(bool _isLock, AnimationCurve _curve);                 // ~156382 — recomputes speedCalc per frame
```

### Per-frame KK subsystems (each is a separate driver, must be patched individually)

These are separate `MonoBehaviour`s or methods that each independently push state every frame, even when `animBody.speed = 0`. Stopping one does NOT stop the others.

| Class.Method | What it drives | Patch type used in this plugin |
|---|---|---|
| `HVoiceCtrl.VoiceProc` | Female voice queue | prefix → return false |
| `HVoiceCtrl.BreathProc` | Female breath/sigh queue | prefix → return false |
| `HMotionEyeNeckFemale.Proc(...)` | Per-frame eye/neck/face/eyebrow/tears patterns from animator state | prefix → return false |
| `HMotionEyeNeckMale.Proc(...)` | Same for males | prefix → return false |
| `HSeCtrl.Proc(...)` | Slap / body-contact SE | prefix → return false |
| `EyeLookController.LateUpdate` | Iris/pupil aim at camera | prefix → return false |
| `NeckLookControllerVer2.LateUpdate` | Neck bone rotation | **don't prefix** — use per-instance `chaCtrl.neckLookCtrl.neckLookScript.skipCalc = true` (the in-game flag) instead. Type-level prefix breaks the head pose; per-instance flag is the documented way. |
| `HFlag.FemaleGaugeUp` / `MaleGaugeUp` | Pleasure gauge tick | prefix → return false. Combine with `flags.lockGugeFemale = true` belt-and-braces. |
| `HSceneProc.Update` (calls `face.OpenCtrl(female)`) | Eye/mouth open + nip rate | prefix-skip `FaceListCtrl.OpenCtrl` instead (line ~165419 calls into it) |
| `HSceneProc.ChangeAnimator` | Position / animation switch | postfix → re-pin freeze on the new active set |
| `VRHScene.ChangeAnimator` | VR variant | same, manually patched via reflection if VRHScene type exists |

### Voice / audio APIs

```csharp
// Game-side voice trigger that drives mouth lip-sync + subtitles + speaker UI.
// KKS Subtitles plugin hooks Manager.Voice.Play_Standby for caption injection — confirmed canonical path.
public Transform Manager.Voice.Play(int no, string assetBundleName, string assetName,
                                    float pitch=1f, float delayTime=0f, float fadeTime=0f,
                                    bool isAsync=true, Transform voiceTrans=null,
                                    Type type=Type.PCM, int settingNo=-1,
                                    bool isPlayEndDelete=true, bool isBundleUnload=true,
                                    bool is2D=false);
// Manager.Voice line ~239

// Stop the voice currently bound to a mouth slot.
Singleton<Manager.Voice>.Instance.Stop(flags.transVoiceMouth[i]);
// Used by KK_HSceneOptions ForceStopVoice — see Hooks.cs:602

// Master volume for SFX scaling.
Manager.Config.SoundData.Master.Volume   // 0–100 int
```

### `HAction` state machine ladder

`HSceneProc.lstProc` contains one (or more in 3P/dark) of these. Each has `LoopProc(bool)` and `SetPlay(string, bool)`:

```
HActionBase
├── HSonyu          (intercourse)
├── HHoushi         (service)
├── HAibu           (foreplay/touch)
├── HLesbian
├── HMasturbation
├── HPeeping
├── H3PHoushi       (3P darkness)
├── H3PSonyu
├── H3PDarkHoushi
└── H3PDarkSonyu
```

`SetPlay(string animName, bool ?)` programmatically transitions the animator to a target state. Common state names:
- `"Loop"` / `"SLoop"` / `"WLoop"` / `"MLoop"` — main piston/loop states
- `"OLoop"` — precum / orgasm-buildup
- `"Idle"` / `"Stop_Idle"` — the states where `voiceWait` can clear
- `"A_Loop"` / `"A_OLoop"` etc — anal variants

`LoopProc(bool)` is the per-frame state machine tick. Calling it manually advances the state machine **without** waiting for the normal Update tick — used by `KK_HSceneOptions.AnimationToggle.ManualOrgasm` to force orgasm progression.

`flags.click = HFlag.ClickKind.X` — set the pending click intent directly. Processed on next state machine tick.

`HActionBase.IsCheckVoicePlay(int)` — voice slot in `breath` state OR voice clip finished. The condition `voiceWait` is gated on.

### Face / expression API

```csharp
// All on ChaControl. ChangeXxxPtn writes through fbsCtrl which holds the FBS controllers.
public void ChangeEyesPtn(int ptn, bool blend = true);       // line ~66700
public void ChangeMouthPtn(int ptn, bool blend = true);      // line ~66900
public void ChangeMouthOpenMax(float maxValue);              // line ~66916
public void ChangeMouthFixed(bool fix);                      // line ~66935
public void ChangeEyebrowPtn(int ptn, bool blend = true);
public void ChangeEyebrowOpenMax(float maxValue);
public void ChangeEyesOpenMax(float maxValue);
public void ChangeEyesBlinkFlag(bool blink);                 // line ~66881 — set false to fix-stop blinking
public byte tearsLv;                                          // 0..3 (3 = max)
```

Pattern numbers (`eyesPtn` / `mouthPtn` / `eyebrowPtn`) **vary per character card** — there's no canonical "orgasm" index. Make these configurable, don't hardcode magic numbers.

### Head / neck / IK

```csharp
public NeckLookControllerVer2 neckLookCtrl  { get; protected set; }  // line ~71689
public GameObject              objHead       { get; protected set; }
public GameObject              objHeadBone   { get; protected set; }  // the actual head bone Transform parent
```

To stop the neck look calculation **per character** (not type-level — that breaks pose):
```csharp
chaCtrl.neckLookCtrl.neckLookScript.skipCalc = true;   // set false to resume
```
This is what the game itself uses internally — see decompiled assembly line ~61642 (`SetEyesPtn(21)` mannequin path) and line ~30020.

If a per-frame writer (animator residue, IK, constraint) keeps overwriting the head rotation despite `skipCalc=true`, snapshot `objHeadBone.transform.localRotation` and re-apply it from `Plugin.LateUpdate` every frame.

### `HandCtrl` activity signals

```csharp
public bool IsItemTouch();      // ~146382 — true if any hand item slot is currently grabbing
public bool IsAction();         // ~146440 — true if action == HandAction.action
public bool IsKissAction();
```

Used for "is the player actively interacting" gating. Caveat: once a touch starts, `IsItemTouch()` stays true until the player releases — and during freeze the release path may be blocked (see F11 in NOTES).

### Other useful subsystems

```csharp
// Particle physics — F9 fix uses this instead of Pause(true) so existing
// particles keep falling under gravity:
var em = particleSystem.emission;
em.enabled = false;   // stops new spawns; existing particles still simulate

// Global mute (F2 nuclear) — covers BGM, ambient, body SE, anything we missed.
// Plugin SFX must opt out:
AudioListener.pause = true;
audioSource.ignoreListenerPause = true;
```

---

## Verified KKAPI helpers

KKAPI ([`references/IllusionModdingAPI/`](../../../references/IllusionModdingAPI/)) is the standard library most KK plugins build on. Build target: pin to **1.40** (the lowest version we test against; build target = lowest tested = backward-compatible).

### Lifecycle

```csharp
[BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
public class Plugin : BaseUnityPlugin { ... }

// Register a controller that gets HScene start/end callbacks (VR-aware automatically):
GameAPI.RegisterExtraBehaviour<MyController>(null);

internal class MyController : GameCustomFunctionController
{
    protected override void OnStartH(MonoBehaviour proc, HFlag hFlag, bool vr) { ... }
    protected override void OnEndH(MonoBehaviour proc, HFlag hFlag, bool vr)   { ... }
}

// proc is HSceneProc OR VRHScene — KKAPI dispatches both into the same callback.
// Use Traverse to get private fields off it.

// Static "are we in HScene right now":
if (KKAPI.MainGame.GameAPI.InsideHScene) { ... }
```

KKAPI **does NOT** provide:
- Direct access to private HScene fields (`lstFemale`, `male`, `male1`, `flags.transVoiceMouth`) — use `Traverse`.
- A callback for `HSceneProc.ChangeAnimator` — patch it ourselves.
- WAV loading — copy SlapMod's WWW pattern.
- Animator/DynamicBone freezing — pure Unity.
- Voice channel control — use `Manager.Voice.Instance.Stop(...)`.

---

## Reference plugin map (what to grep for what)

| If you want to know how to... | Look at... |
|---|---|
| Mute female voice mid-scene | [`KK_HSceneOptions/Hooks.cs:120`](../../../references/KK_HSceneOptions/KK_HSceneOptions/Hooks.cs#L120) — `VoiceProc` prefix-mute pattern |
| Force a specific animation state | [`KK_HSceneOptions/AnimationToggle.cs:176`](../../../references/KK_HSceneOptions/KK_HSceneOptions/AnimationToggle.cs#L176) — `ManualOLoop` calls `proc.SetPlay("OLoop", true)` |
| Manually advance the state machine | [`KK_HSceneOptions/AnimationToggle.cs:208`](../../../references/KK_HSceneOptions/KK_HSceneOptions/AnimationToggle.cs#L208) — `ManualOrgasm` invokes `loopProcDelegate?.Invoke(true)` directly |
| Wait for an animation transition to complete | [`KK_HSceneOptions/HSceneOptions.cs:446`](../../../references/KK_HSceneOptions/KK_HSceneOptions/HSceneOptions.cs#L446) — `RunAfterTransition` coroutine using `WaitUntil` on animator state name. **Caveat**: if `animBody.speed = 0`, the state never changes — must un-freeze the animator first. |
| Map `HFlag.mode` to a concrete `HActionBase` subtype | [`KK_HSceneOptions/AnimationToggle.cs:92`](../../../references/KK_HSceneOptions/KK_HSceneOptions/AnimationToggle.cs#L92) — `FindProc` switch table |
| Stop in-flight voice | [`KK_HSceneOptions/Hooks.cs:602`](../../../references/KK_HSceneOptions/KK_HSceneOptions/Hooks.cs#L602) — `Singleton<Voice>.Instance.Stop(transVoiceMouth[i])` |
| Hook a `DynamicBone` to apply external forces | [`KK_Plugins/src/Boop.Core/Boop.cs`](../../../references/KK_Plugins/src/Boop.Core/Boop.cs) — patches `DynamicBone.SetupParticles` to register, runs own `Update()` with mouse position. **Boop only works if the bones are not disabled.** |
| Load a wav at plugin startup | [`references/SlapMod/SlapMod.decompiled.cs:288`](../../../references/SlapMod/SlapMod.decompiled.cs#L288) — `WWW` + `WWWAudioExtensions.GetAudioClipCompressed`. **Must `yield return www`** in a coroutine, never spin-wait. |
| Hook subtitle injection | [`KK_Plugins/src/Subtitles.KKS/KKS.Subtitles.Hooks.cs:11`](../../../references/KK_Plugins/src/Subtitles.KKS/KKS.Subtitles.Hooks.cs#L11) — `Manager.Voice.Play_Standby` postfix |
| Find more plugins to grep | [KK Plugins Compendium](https://github.com/Frostation/KK-Plugins-Compendium/blob/master/Plugins%20Compendium.md) — community index |

---

## Verification flow (run before committing any KK API claim)

When you want to use a KK class/method/field, **all five** of these should pass before you write code that depends on it:

1. **ilspy the actual game DLL with `-t TypeName`** — confirm the type, the member, the signature. First-hand evidence beats everything.
   ```sh
   $ILSPYCMD "$KK_DIR/Koikatu_Data/Managed/Assembly-CSharp.dll" -t HSceneProc > /tmp/hsceneproc.cs
   ```
2. **Whole-assembly grep on the symbol** — distinguishes "exists but unused" (e.g. `animTongueEx`) from "actually drives behavior".
   ```sh
   $ILSPYCMD "$KK_DIR/Koikatu_Data/Managed/Assembly-CSharp.dll" > /tmp/full_asm.cs
   grep -n "animTongueEx\|gaugeFemale" /tmp/full_asm.cs
   ```
3. **Reference plugin grep** — check `references/` for any plugin that already used this symbol; copy their pattern, learn from their pitfalls in comments.
4. **Cross-check** — if a "fix" was suggested by collaborator AI / forum / external source, run steps 1–3 to validate the proposed approach independently. Don't just trust the suggestion. (See B5 — collaborator was right about the problem but wrong about the fix.)
5. **Playtest evidence** — when behavior is the question (not just existence), look at log lines / observable effects in the running game. The 1 Hz `[gauge]` dump in `Plugin.Update` (`Plugin.GaugeDumpEnabled = true`) is the existing example.

If you're about to commit a fix and a step is missing, do that step first.

---

## Architecture-level constraint that drives many open issues

KK's `HSonyu`/`HHoushi`/`HAibu` state machines (`HSceneProc.LoopProc` callees) gate every action transition behind two simultaneous conditions:

1. The animator state name reaches `"Idle"` or `"Stop_Idle"` — read via `lstFemale[0].animBody.GetCurrentAnimatorStateInfo(0).IsName("Idle")`.
2. `IsCheckVoicePlay(0)` returns true — voice slot in `breath` state or finished playing.

When we freeze with `animBody.speed = 0`, condition (1) never holds (state stays mid-loop). When we prefix-skip `HVoiceCtrl.VoiceProc/BreathProc`, condition (2) never holds either. So `flags.voiceWait` stays true, and any `flags.click` value the player tries to set just queues forever.

**This is the root cause of**: F4 (UI queued during resume audio), F10 main (free H UI dead during freeze), F11 (grab can't release), F16 (control only works in some animation states), F17 (groping animations frozen with female), F18 (male orgasm sequence can't complete), F21 (alternate position female stays at old pose).

**Two paths forward** (both rely on the verified KK APIs above; pick one or combine):

### Path A — Tap-unfreeze hotkey
Hotkey that briefly drops the freeze (sets `animBody.speed` back to 1, removes the `VoiceProc` prefix, clears `lockGugeFemale`) for 1–2 frames, lets `flags.click` get processed naturally, then refreezes. Rest of the freeze (head pin, blink, face snapshot, AudioListener.pause, particle emission off) stays engaged the whole time, only the animator + voice slot tick briefly.

Variant: instead of timed unfreeze, manually invoke `loopProcDelegate.Invoke(true)` once or twice — the same trick `KK_HSceneOptions.AnimationToggle.ManualOrgasm` uses to force state machine progression without waiting for normal Update.

### Path B — Transition window on `ChangeAnimator`
Specifically for F21 (and incidentally helps F11). When `HSceneProc.ChangeAnimator` postfix fires while frozen, instead of immediately calling `ReapplyIfFrozen()`:
1. Spawn a coroutine
2. Restore `animBody.speed` for the females only (or all subjects)
3. `WaitUntil(() => female.animBody.GetCurrentAnimatorStateInfo(0).IsName(flags.nowAnimStateName))` — wait until the new state actually begins playing (the `RunAfterTransition` pattern from HSceneOptions)
4. Re-pin `animBody.speed = 0`
5. The other locks (head pin, voice mute, gauge lock) stay engaged through the whole window — only the animator briefly advances

These can coexist. Path B is the narrower fix and lower risk; Path A is a UX feature on top of the existing freeze.

### What we should NOT do

- **Don't replace `animBody.speed = 0` with bone-cache + reapply each LateUpdate** unless the simpler paths above genuinely fail. It's expensive, fragile, and reinvents the animator. Reach for it only as a last resort.
- **Don't try to make the state machine tick with the animator frozen.** It's not designed to. KK's own internal forced-orgasm uses `loopProcDelegate.Invoke()` with the animator running.

---

## Pre-flight checklist before committing a fix

- [ ] **Is this fixing a real reported symptom (an F-series feedback item) or am I scope-creeping?** If there's no F-ID this addresses, stop and ask.
- [ ] Did I run all 5 verification flow steps for every new KK API I touched?
- [ ] Does the fix preserve every hard rule (1–14)?
- [ ] If I added a step to `Freeze()`, did I also update `ReapplyIfFrozen()`?
- [ ] If I cached state, is restore wired into `Resume()` AND `Unbind()` (HScene end)?
- [ ] If I added a config, does it have a sensible default that doesn't change existing behavior unless opted-in?
- [ ] Did I update [`docs/SPEC.md`](../../../docs/SPEC.md) freeze-step table or config table?
- [ ] Did I update [`docs/NOTES.md`](../../../docs/NOTES.md) Status overview if a feedback ID changed state?
- [ ] Did I update this SKILL if I verified a new piece of KK API or a useful pattern?
- [ ] Did I commit the build, copy the dll into the runtime plugins folder, and verify mtime?
- [ ] Did I `grep "ZAWA>"` the test log to confirm the fix actually fired?
