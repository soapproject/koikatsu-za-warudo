# KK_ZaWarudo — Notes / Known Risks / Fixed Bugs

Pitfalls hit during development, risks found in audits, playtest feedback, and unresolved follow-ups. Anyone (or future-me) about to touch this code should skim this first.

---

## Status overview (v0.1 playtest feedback)

| ID | Issue | Status |
|---|---|---|
| F1  | Head tracking + auto-blink active during freeze | ✅ Fixed |
| F2  | Game audio leaks during freeze | ✅ Fixed (nuclear AudioListener.pause) |
| F3  | Resume scream: no mouth movement / no subtitles / single line | ⏳ Deferred to v0.2 |
| F4  | UI input queued during ~16 s post-resume audio window | 🔍 Patch ready (next round) |
| F5  | Boop plugin doesn't work during freeze | ✅ Auto-fixed by F7 (verify) |
| F6  | Touching the female changes her expression during freeze | 🔍 Patch ready (next round) |
| F7  | Hair / skirt physics turn off when frozen | ✅ Fixed |
| F8  | In-progress ahegao reverts to default on freeze | ✅ Fixed |
| F9  | Fluid particles hang in mid-air during freeze | ✅ Fixed (emission off, not Pause) |
| F10 main | Free H HSprite UI clicks blocked during freeze | ⏸ Deferred — needs design rethink |
| F10 sub  | Gauge climbs during freeze ("Accumulation Rate leak") | ✅ Fixed (real bug — `HFlag.FemaleGaugeUp` prefix-skip) |
| F11 | Grabbing the breast during freeze can't be released | ⏸ Same root as F10 main |

Bug fixes from earlier audits (B-series) are below the F-series.

---

## User feedback (v0.1 playtest)

### F1 — Head tracking + auto-blink stayed active during freeze ✅
Two separate drivers needed handling beyond `HMotionEyeNeck.Proc`:
1. `NeckLookControllerVer2.LateUpdate` runs every frame and writes head/neck rotations directly. Now Harmony-prefix-skipped while frozen.
2. Blinking is driven by `fbsCtrl.BlinkCtrl`. Calling `ChangeEyesBlinkFlag(false)` on each subject (cached + restored on resume) stops the auto-blink.

Side effect: the `NeckLookControllerVer2` patch is type-level so the male protagonist's head also stops tracking. Verify in playtest; if it feels too dead, add per-instance gating.

### F2 — Game audio leaks during freeze (moans + "crumpling/boiling noise") ✅
Two-part fix:
1. Nuclear `AudioListener.pause = true` on freeze. Plugin AudioSource has `ignoreListenerPause = true` so SFX still plays. **Trade-off**: BGM is also muted during freeze. SPEC updated to reflect this.
2. **Re-evaluation**: the "crumpling/boiling noise" was very likely the **`zawarudo_female_during.wav` loop itself** (a clip from a hentai anime, full of organic moans + fluid SE) being mistaken for leaked game audio. New config `Audio > Play During Loop` (default true) lets the user disable it for true silence between Enter and Exit. New config `Audio > During Loop Only While Active` (default true) makes the loop only play while the player is actively touching, so it stops when they idle.

### F3 — Resume scream: no mouth, no subtitles, only one variant ⏳ v0.2
Two real issues stacked:
1. Our resume audio plays through *our* AudioSource, decoupled from the game's lip-sync / subtitle pipeline.
2. Only one wav per slot → no variety.

The game-side trigger is `Manager.Voice.Play(int no, string assetBundleName, string assetName, …)` (verified via ilspy on `Manager.Voice`). KKS Subtitles plugin hooks `Manager.Voice.Play_Standby(AudioSource, Manager.Voice.Loader)` to inject captions, confirming this is the canonical path. To use it we'd need:
1. Knowledge of the right asset bundle + clip name for the female's "orgasm scream" voice (varies per character personality / voice slot). Bundles live under `sound/data/h/voice/<personality>/...`.
2. A pool of candidate voicelines for variety.
3. Optional context-awareness (intercourse vs head, weak point hit, etc).

**Punt**: this is genuinely v0.2 work — needs research into the voice bundle layout per-character plus a config UX for picking lines. For now keep the wav approach.

### F4 — UI input queued during the post-resume audio window 🔍
Same root cause as F10 main: KK's `HSonyu`/`HHoushi` state machine waits for `flags.voiceWait` to clear, which only happens when `IsCheckVoicePlay(0)` returns true (voice slot transitions to `breath` or finishes playing). Our `HVoiceCtrl.VoiceProc` / `BreathProc` prefix patches return false → voice slot state is never updated → `IsCheckVoicePlay` never trips → `voiceWait` stays true → click intent queues forever.

**F4-only fix plan** (applies during the post-resume window where the animator IS unfrozen, so removing the voice prefix block actually unblocks the state machine):
1. Stop using `VoiceProc` / `BreathProc` prefix-skip during the post-resume mute window.
2. Instead, extend `AudioListener.pause = true` through the resume SFX duration (so game voice is actually muted globally).
3. Voice slot states then update normally → `IsCheckVoicePlay` → state machine ticks.
4. Trade-off: BGM stays muted ~16 s longer than today.

Will ship in the next round, paired with F6.

### F5 — Boop plugin didn't work during freeze ✅ (auto-fixed by F7, needs verify)
Reading Boop's source ([Boop.Core/Boop.cs](../references/KK_Plugins/src/Boop.Core/Boop.cs)): it only patches `DynamicBone.SetupParticles` (postfix) to register bones, then runs its own `Update()` that reads mouse position and calls `db.ApplyForce(f)`. No `HandCtrl` hook, no animator dependency. Boop was failing in v0.1 because we previously DISABLED every `DynamicBone` in `FreezeFemaleBones` — disabled components don't simulate, so applied forces did nothing. F7's no-op fix means DynamicBones now stay live during freeze, so Boop's `ApplyForce` should work again. **Verify in playtest.**

### F6 — Touching the female changes her expression even during freeze 🔍
Root cause found, patch ready. The path is NOT through `HMotionEyeNeck.Proc`. `HSceneProc.Update` runs every frame and calls `face.SafeProc(f => f.OpenCtrl(female))`. `FaceListCtrl.OpenCtrl` then writes:
- `female.ChangeEyesOpenMax(ans)` ← derived from `blendEye.Proc(ref ans)`
- `female.mouthCtrl.OpenMin = ans2` ← from `blendMouth.Proc(ref ans2)`
- `female.ChangeNipRate(rate)` ← derived from `flags.gaugeFemale * 0.01f`

**Fix plan**: Harmony prefix on `FaceListCtrl.OpenCtrl` returning false when frozen. The `ChangeNipRate` line is part of `HSceneProc.Update` itself — would need a transpile or accept it.

### F7 — Hair / skirt physics turn off during freeze ✅
`FreezeFemaleBones` is now a no-op. DynamicBone solvers keep running, so with the body anchor locked (`animBody.speed = 0`) hair/cloth gradually settle into a natural drape under gravity instead of being frozen mid-swing. The `_disabledBones` cache and the restore loop in Resume are kept (harmless empty iterations) so the change is local to one method. As a side effect this also fixed F5.

### F8 — Freezing reverts an in-progress ahegao expression ✅
Snapshot `eyesPtn` / `mouthPtn` / `eyebrowPtn` / `tearsLv` / `eyesOpenMax` per subject on freeze. Re-applied on every `ReapplyIfFrozen` (covers partner switches and any race where the game wrote a default face before our prefix kicked in). Snapshot is dropped on resume so the game takes over normally.

### F9 — Fluid particles don't fall during freeze ✅
Switched from `ParticleSystem.Pause(true)` to toggling `EmissionModule.enabled = false`. Existing particles keep simulating (gravity, velocity, lifetime), so fluid blobs already in flight will continue falling to the ground. Only new spawns are suppressed. Cache type changed `HashSet<ParticleSystem>` → `Dictionary<ParticleSystem, bool>` to remember the original `enabled` state per system.

### F10 main — Free H HSprite UI buttons unresponsive while frozen ⏸
Investigation found the root cause: KK's `HSonyu`/`HHoushi`/`HAibu` state machines (`HSceneProc.LoopProc` callees) gate every action transition behind `flags.voiceWait` clearing, which only happens when (a) the animator state name reaches `Idle`/`Stop_Idle` AND (b) `IsCheckVoicePlay` returns true. We block BOTH: `animBody.speed = 0` keeps the animator stuck mid-state, and our `HVoiceCtrl.VoiceProc` prefix prevents voice playback from completing. So the wait condition never resolves and any click intent (`flags.click = X`) sits in the queue forever.

**Partial unblock available**: removing the VoiceProc/BreathProc prefix patches (rely on `AudioListener.pause = true` for muting instead, same as F4's plan) would let voice slot states update naturally → `IsCheckVoicePlay` would track them → `voiceWait` could clear. But the animator block remains: state machine still can't transition out of "Loop" state because `animBody.speed=0`, so click intents still don't process. So this only fixes F4 (post-resume window where animator is unfrozen), not F10 main.

**Possible solutions**, none clean:
1. Don't freeze the animator at all — defeats the visual point of time stop.
2. Use a different mechanism to visually freeze (e.g. cache + reapply bone positions every LateUpdate). Expensive and fragile.
3. Hotkey to "tap unfreeze" for one frame, accept the click, refreeze. Hacky UX.
4. Intercept HSprite click handlers at our layer and stash them as pending actions, replayed on real Resume.

Need a design decision. **For now this is a known limitation**: in free H the player should unfreeze to interact with the UI, then refreeze.

### F10 sub — `Accumulation Rate` config "affects non-frozen time" ✅ (real bug)
Playtest gauge dump:
```
step2 speedCalc 0.45 -> 0  gaugeFemale=35.0   ← we set 0
[gauge] f=36.3  speedCalc=0.49  frozen=True   ← 1 s later, both grew
[gauge] f=99.0  speedCalc=1.00  frozen=True   ← 17 s later, gauge fully climbed during freeze
```
Root cause: `flags.speedCalc = 0` in step 2 is overwritten by `HFlag.WaitSpeedProc`, which `HSonyu/HHoushi/HAibu.Proc` calls every frame from `Time.deltaTime`. The whole simulation backend keeps running during freeze — only the visuals (animator.speed=0), voice (prefix-skip), and face/eye/SE were stopped in v0.1. The gauge tick wasn't.

Fix: Harmony prefix on `HFlag.FemaleGaugeUp` and `HFlag.MaleGaugeUp` returning false when frozen. Resume's `InjectGauge` writes `flags.gaugeFemale` directly, so it bypasses our prefix and works as before. Result: gauge stays put during freeze; user-perceived "AccumulationRate leak" was actually the natural in-freeze tick + accumulated injection stacking.

### F11 — Grabbing the breast during freeze can't be released ⏸
Same root as F10 main. Log evidence: `during loop START (player active)` fires once and never `STOP` for the entire freeze duration → `HandCtrl.IsItemTouch()` stays true forever.

Likely cause: `HandCtrl.SetIconTexture` (the cursor-area judge that decides which body region the click hits) reads `nowMES.isTouchAreas[]`, which is set from animation events on the current animator state. With `animBody.speed = 0`, no new animation events fire → `isTouchAreas` stays frozen at whatever value it had at freeze time → the click→DetachItem release path either never identifies a "different area" (needed to release the current grab), or the release transition needs an animator state change that never happens. Same fundamental issue as F10 main.

Workaround for tester: unfreeze (T), release the grab normally, refreeze.

---

## Fixed bugs (audit / development series)

### B1 — `MissingMethodException: System.Array.Empty`
**Symptom**: BepInEx loaded the plugin, then `Awake` immediately threw and died. `LogOutput.log` showed zero `ZAWA>` lines — only BepInEx's own `Loading [KK_ZaWarudo 0.1.0]`.

**Cause**: csproj originally targeted `net46`. The C# compiler lowers empty-array literals into calls to `Array.Empty<T>()`. KK runs on Unity 5.6 / Mono .NET 3.5, which **does not have** `Array.Empty`.

**Fix**: csproj → `<TargetFramework>net35</TargetFramework>`, matching every other KK plugin (KK_HSceneOptions etc.).

**Lesson**: Every KK plugin must target net35. A successful build does NOT mean it'll run — the runtime BCL surface is much smaller than net46.

### B2 — SFX loader returned a stub `AudioClip` (length=0, name="")
**Symptom**: log said `enter=True during=True ...` but `[Enter] playing (0.00s, vol=1.00)` finished instantly and nothing was audible.

**Cause**: original `TryLoad` was synchronous and spin-waited on the main thread (`while (clip.loadState != Loaded)`). Unity's `WWW` download pump **runs on the main thread**, so the spin loop starves the pump → loadState never reaches `Loaded` → after the guard counter trips, the clip returned has length 0 and an empty name (header not parsed yet).

**Fix**: rewrote loading as a coroutine that does `yield return new WWW(uri)`, letting the main thread pump WWW to completion before reading the clip. `AudioManager.StartLoad()` is invoked once from `Plugin.Awake`.

**Lesson**: On Unity's main thread, **always await `WWW` via `yield return www`**; never spin. This is true even for local `file://` URLs.

### B3 — `ChangeAnimator` re-triggering grew the cache linearly
**Symptom**: After N position/partner switches, the resume log showed wildly inflated `re-enabled bones=` counts.

**Cause**: `FreezeFemaleBones` / `FreezeFemaleAudio` / `FreezeParticles` used `List.Add` with no dedupe. `ReapplyIfFrozen` (called from the `HSceneProc.ChangeAnimator` postfix) re-added every Component on every switch.

**Fix**: caches changed to `HashSet<T>`, with the pattern `if (set.Add(x)) DoAction(x)` for natural dedupe. `_animSpeeds` was already a Dictionary using `ContainsKey`, so it was fine.

**Lesson**: Any cache that may be touched multiple times needs `HashSet`/`Dictionary`, not `List`.

### B4 — `Bind` had no re-entry guard, old Instance got silently overwritten
**Symptom**: In theory unreachable — would require `MapSameObjectDisable` to fire twice without a matching `OnDestroy` (BepInEx hot reload, KKAPI re-init, etc). If it ever did fire, the previous scene's cache would be lost; whatever animators/bones we'd frozen would **stay frozen forever**.

**Fix**: at the top of `Bind`, if `Instance != null`, call `Unbind()` first (which triggers `Resume()` and restores the old state) before installing the new Instance. Logs a warning when this fires.

**Lesson**: Singleton `Bind`/`Init` methods must be idempotent — clean up the old state before swapping in the new one.

### B5 — `animFace` reflection lookup always returned null
Surfaced by collaborator AI, refined after cross-checking. ChaInfo only has `animBody` and `animTongueEx` — there is no `animFace`. Verified independently via:
1. ilspy on the live game DLL
2. ilspy on the IllusionLibs NuGet stub
3. KK_HSceneOptions / KK_Plugins / IllusionModdingAPI codebase grep (0 hits for any anim* other than `animBody`)
4. Whole-assembly grep on `animTongueEx` — only 3 references (declaration, assignment, cleanup), the game itself never calls `.speed`/`.Play` on it

**Refined conclusion**: `animFace` doesn't exist (collaborator was right). But the suggested `animTongueEx` replacement is **also a no-op in current KK** — the game never drives it. The actual lever for face/expression/lip-sync is `animBody.speed = 0`, because animBody uses Unity's layer system with face controllers layered on top.

**Fix**: direct `c.animBody` access (replacing the reflection lookup), plus `c.animTongueEx` as a "belt-and-braces" backup in case a future game patch starts driving it. Comment in code clearly labels `animTongueEx` as a no-op standby slot.

**Lessons**:
1. Don't write KK API field names from memory — always ilspy first.
2. Don't blindly trust collaborator suggestions either — they may correctly identify a problem but propose a fix based on incomplete evidence.
3. Any "let's also cover field X just to be safe" decision must be labelled as evidence-based or belt-and-braces.

### B6 — `ReapplyIfFrozen` was missing the voice / audio steps
**Symptom**: After switching position / partner while frozen, the new female's moan / mouth voice / other AudioSources would punch through the freeze and play until resume.

**Cause**: `Freeze()` runs steps 1 through 4c, but `ReapplyIfFrozen` only re-ran steps 1–4, missing 4b and 4c.

**Fix**: `ReapplyIfFrozen` now calls `StopFemaleVoices()` and `FreezeFemaleAudio()` as well. The HashSet caches (B3) handle dedupe so repeated calls don't pile up.

**Lesson**: When you add or remove a step from `Freeze()`, **update `ReapplyIfFrozen` in the same commit**. Long-term: extract a shared `ApplyFreezeSteps()` helper to prevent further drift.

### B7 — `_extraMales` comment fabricated "KPlug additions"
**Symptom**: comment claimed `_extraMales = male1 (darkness) + KPlug additions`, but the init code only grabbed `male1`.

**Verification**: ilspy on vanilla `HSceneProc` shows exactly two `ChaControl`-typed male slots: `male` and `male1`.

**Fix**: rewrote the comment to honestly describe current capability. Supporting KPlug-style mods that add extra males will require finding where those mods stash them — possibly hooking a different class entirely.

**Lesson**: Don't write comments that describe future capabilities. Comments describe what the code does now, not what you wish it did.

---

## Known unresolved risks / Follow-ups

### R1 — `AudioManager` holds strong refs to `AudioClip`s forever
- **Severity**: trivial. 4 small wav files for the lifetime of the process.
- **When it'd matter**: if we ever add "reload SFX at runtime" (e.g. respond to ConfigurationManager changes), each reload would leak one set of clips unless we destroy the old ones.
- **Prevention**: in any future reload path, `if (oldClip != null) UnityEngine.Object.Destroy(oldClip);`

### R2 — Protagonist detection is hardcoded to `HSceneProc.male`
- **Severity**: medium.
- **Trigger**: any scenario where the actual player camera is bound to a female and not `male` (e.g. darkness female-protagonist viewpoint, or a mod that switches the camera). We'd freeze the player themselves.
- **Prevention**: add a config like `Untouched Character Index`, or query KKAPI for the active controlled character.

### R3 — KPlug / other mods with extra male slots aren't covered
- **Severity**: medium.
- **Status**: only vanilla `HSceneProc.male1` is grabbed. If KPlug stores extra males in a different class, container, or runtime injection, we won't see them.
- **Prevention**: when this is observed, dig into the offending mod's source. Likely needs hooking a different class instead of `HSceneProc`.

### R5 — `Manager.Voice.Instance.Stop(transVoiceMouth[i])` only covers the active mouth slot
- **Severity**: low (already mitigated by step 4d `AudioListener.pause`).
- **Cause**: `transVoiceMouth` is a fixed length-2 array representing the two voice slots currently bound to mouths. In 3P/4P, non-active females' voices aren't in there.
- **Status**: step 4d (global mute) makes this irrelevant in practice.

### R6 — Race window during position switch
- **Severity**: low.
- **Trigger**: while frozen, the player triggers a position change → `ChangeAnimator` postfix calls `ReapplyIfFrozen` to set speed=0 on the new animator set. If KK takes 1–2 frames after the postfix to actually swap animators (not yet observed in practice), the new female could "twitch briefly" before being re-pinned.
- **Prevention**: defer `ReapplyIfFrozen` by one or two frames via a coroutine, or install a `LateUpdate` watchdog inside the controller.

### R7 — Hotkey collision (T conflicts with other plugins' push-to-talk)
- **Severity**: trivial. Default is `T`; user can rebind in ConfigurationManager.
- **Prevention**: README mention, or change default to `Ctrl+T`.

### R8 — `NeckLookControllerVer2.LateUpdate` patch is type-level
- **Severity**: low.
- **Status**: skipping this method when frozen affects every `NeckLookControllerVer2` instance in the scene, including the male protagonist. The protagonist's head will also stop tracking during freeze.
- **Prevention**: per-instance gating using `__instance` and a lookup against the protagonist's NeckLookControllerVer2 reference. Only worth doing if playtest confirms the male freeze is jarring.

---

## Inviolable development rules ("don't break these")

1. **Must target net35.** Reject any PR that bumps to net46/net472. See B1.
2. **No spin-waiting on Unity APIs from the main thread.** Any `while (notReady) { }` must become a coroutine `yield`. See B2.
3. **Caches that get re-touched must be `HashSet`/`Dictionary`.** Never `List.Add` and hope for dedupe. See B3.
4. **Singleton `Bind` must be idempotent.** Call `Unbind` first if there's an existing instance. See B4.
5. **The protagonist (`HSceneProc.male`) is never added to the frozen-subjects set.** Hardcoded into `FrozenSubjects()` — don't accidentally include him.
6. **Every log line carries the `ZAWA>` prefix.** Use `Plugin.LogI/LogW/LogE`, not `Logger.LogInfo` directly.
7. **`UnpatchSelf` on `OnDestroy`.** `Plugin.OnDestroy` must unpatch Harmony, otherwise reload stacks old patches on top of new ones.
8. **Don't write KK API field names from memory.** Use ilspy. ChaInfo's only Animators are `animBody` and `animTongueEx` — there's no `animFace`/`animOption`. `HSceneProc`'s only male slots are `male` and `male1`. See B5, B7.
9. **When `Freeze()` steps change, update `ReapplyIfFrozen()` in the same commit.** Their step sets must mirror each other. See B6. Long-term: extract a shared helper.
10. **Comments describe what the code does now, not what you wish it did.** See B7.
11. **Cross-verify collaborator AI / external suggestions.** They may correctly identify a problem, but the proposed fix may rest on incomplete evidence. See the second pass on B5.
12. **No absolute paths in committed docs or source.** Local-machine paths (KK install location, dotnet tool path, user home) belong in `.env.local` or similar gitignored files, or in placeholder form (`$KK_DIR`, `<your KK install>`) in committed docs.
13. **No personal Claude Code workspace settings in the repo.** `.claude/` is gitignored.

---

## Development tooling / Workflow

### Local environment variables
The dev workflow assumes a couple of paths that vary per machine. Set them in your shell profile (or a gitignored `.env.local` you `source` manually); **don't hardcode them into committed files**:

```sh
# Example placeholders — set to wherever YOUR copies live
export KK_DIR="<path to Koikatsu install>"           # e.g. .../Steam/steamapps/common/Koikatsu
export ILSPYCMD="<path to ilspycmd binary>"          # usually under ~/.dotnet/tools after install
```

In the snippets below, `$KK_DIR` and `$ILSPYCMD` refer to these.

### Decompiling KK runtime DLLs
Every DLL under the KK install directory can be decompiled directly — not only `Koikatu_Data/Managed/Assembly-CSharp.dll`, but also `BepInEx/plugins/*.dll` (other people's plugins). This is the **most direct source of evidence** for KK API behavior, learning implementation patterns, and finding undocumented fields. It outranks the NuGet stub assemblies and the reference repos.

**Install ilspycmd** (one-off):
```sh
dotnet tool install -g ilspycmd --version 8.2.0.7535
```
Note: the `latest` NuGet package is currently broken (missing `DotnetToolSettings.xml`). Pin `8.2.0.7535`.

**Decompile a single type**:
```sh
$ILSPYCMD "$KK_DIR/Koikatu_Data/Managed/Assembly-CSharp.dll" -t HSceneProc > /tmp/hsceneproc.cs
```

**Decompile the entire DLL** (slow, but lets you grep field usage globally):
```sh
$ILSPYCMD "$KK_DIR/Koikatu_Data/Managed/Assembly-CSharp.dll" > /tmp/full_asm.cs
grep -n "animTongueEx\|gaugeFemale\|whatever" /tmp/full_asm.cs
```

**Important DLL paths** (relative to `$KK_DIR`):
- `Koikatu_Data/Managed/Assembly-CSharp.dll` — main game logic (HSceneProc, ChaControl, HFlag, Manager.*, etc.)
- `Koikatu_Data/Managed/Assembly-CSharp-firstpass.dll` — UnityEngine extensions, DynamicBone, etc.
- `BepInEx/core/BepInEx.dll` — BepInEx API
- `BepInEx/plugins/*.dll` — other people's plugins

Other people's plugins that have public source can go into `references/`. For closed-source ones, decompile and stash under `references/<name>/<name>.decompiled.cs` (see the SlapMod case).

**More open-source plugins** to mine for patterns:
- [KK Plugins Compendium](https://github.com/Frostation/KK-Plugins-Compendium/blob/master/Plugins%20Compendium.md) — community-maintained index. First place to look when figuring out "has anyone already solved this for KK?".

### What KKAPI already provides (don't reinvent it)
KKAPI ([references/IllusionModdingAPI/](../references/IllusionModdingAPI/)) is the standard library most KK plugins build on. Before writing a new feature, grep its source.

| You want to | KKAPI provides | Location |
|---|---|---|
| Detect H-scene start/end | `GameCustomFunctionController.OnStartH/OnEndH` (VR-aware) | `KKAPI/MainGame/GameCustomFunctionController.cs` |
| Know whether you're inside HScene | `GameAPI.InsideHScene` (static bool) | `KKAPI/MainGame/GameApi.cs` |
| Register your own game-level controller | `GameAPI.RegisterExtraBehaviour<T>(extendedDataId)` | (same) |
| HFlag mode helpers | `HFlag` extension methods | `KKAPI/MainGame/Utilities/GameExtensions.cs` |
| Maker / Studio hooks | `MakerAPI` / `StudioAPI` namespaces | `KKAPI/Maker`, `KKAPI/Studio` |
| ConfigurationManager attribute template | `ConfigurationManagerAttributes` | shipped by several plugins |

**What KKAPI does NOT provide** (we still implement these ourselves):
- Direct access to HScene private fields (`lstFemale`, `male`, `male1`, `flags.transVoiceMouth`, etc.) — use `Traverse`.
- Callback for `HSceneProc.ChangeAnimator` — none, we patch it ourselves.
- WAV loading — none; copy SlapMod's pattern.
- Animator / DynamicBone / AudioSource freezing — pure Unity, not KKAPI's job.
- Freeing voice channels — KK-side: `Manager.Voice.Instance.Stop(transform)`.

### Verification flow (any time you're unsure about KK API behavior)
1. **ilspy the actual game DLL** — first-hand evidence of class shape.
2. **`-t TypeName`** — inspect a single type's members, fields, method signatures.
3. **Whole-assembly grep** — find every caller / consumer of a field or method to distinguish "exists but unused" from "actually does something".
4. **Reference plugin grep** (`references/`) — see how others use the same API.
5. Only trust a claim once 1–4 all line up.
