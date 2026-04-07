# KK_ZaWarudo — Notes / Known Risks / Fixed Bugs

Pitfalls hit during development, risks found in audits, and unresolved follow-ups. Anyone (or future-me) about to touch this code should skim this first — it'll save half an hour.

---

## Fixed bugs

### B1 — `MissingMethodException: System.Array.Empty`
**Symptom**: BepInEx loaded the plugin, then `Awake` immediately threw and died. `LogOutput.log` showed zero `ZAWA>` lines — only BepInEx's own `Loading [KK_ZaWarudo 0.1.0]`.

**Cause**: csproj originally targeted `net46`. The C# compiler lowers empty-array literals into calls to `Array.Empty<T>()`. KK runs on Unity 5.6 / Mono .NET 3.5, which **does not have** `Array.Empty`.

**Fix**: csproj → `<TargetFramework>net35</TargetFramework>`, matching every other KK plugin (KK_HSceneOptions etc.).

**Lesson**: Every KK plugin must target net35. A successful build does NOT mean it'll run — the runtime BCL surface is much smaller than net46.

---

### B2 — SFX loader returned a stub AudioClip (length=0, name="")
**Symptom**: log said `enter=True during=True ...` but `[Enter] playing (0.00s, vol=1.00)` finished instantly and nothing was audible.

**Cause**: original `TryLoad` was synchronous and spin-waited on the main thread (`while (clip.loadState != Loaded)`). Unity's `WWW` download pump **runs on the main thread**, so the spin loop starves the pump → loadState never reaches `Loaded` → after the guard counter trips, the clip returned has length 0 and an empty name (header not parsed yet).

**Fix**: rewrote loading as a coroutine that does `yield return new WWW(uri)`, letting the main thread pump WWW to completion before reading the clip. `AudioManager.StartLoad()` is invoked once from `Plugin.Awake`, so by the time the player enters an HScene the clips are long since loaded.

**Lesson**: On Unity's main thread, **always await `WWW` via `yield return www`**; never spin. This is true even for local `file://` URLs.

---

### B3 — `ChangeAnimator` re-triggering grew the cache linearly
**Symptom**: After N position/partner switches, the resume log showed wildly inflated `re-enabled bones=` counts. Behaviorally still correct (re-enabling the same bone is idempotent), but with degraded GC pressure and list-traversal cost over time.

**Cause**: `FreezeFemaleBones` / `FreezeFemaleAudio` / `FreezeParticles` used `List.Add` with no dedupe. `ReapplyIfFrozen` (called from the `HSceneProc.ChangeAnimator` postfix) re-added every Component on every switch.

**Fix**: caches changed to `HashSet<T>`, with the pattern `if (set.Add(x)) DoAction(x)` for natural dedupe. `_animSpeeds` was already a Dictionary using `ContainsKey`, so it was fine.

**Lesson**: Any cache that may be touched multiple times needs `HashSet`/`Dictionary`, not `List`.

---

### B4 — `Bind` had no re-entry guard, old Instance got silently overwritten
**Symptom**: In theory unreachable — would require `MapSameObjectDisable` to fire twice without a matching `OnDestroy` (BepInEx hot reload, KKAPI re-init, or another plugin force-reinitting the HScene). If it ever did fire, the previous scene's cache would be lost; whatever animators/bones we'd frozen would **stay frozen forever**, since the next Resume would iterate an empty cache.

**Fix**: at the top of `Bind`, if `Instance != null`, call `Unbind()` first (which triggers `Resume()` and restores the old state) before installing the new Instance. Logs a warning when this fires.

**Lesson**: Singleton `Bind`/`Init` methods must be idempotent — clean up the old state before swapping in the new one.

---

### B5 — `animFace` reflection lookup always returned null (raised by collaborator AI, refined after cross-checking)
**Symptom**: collaborator reported "face/tongue animation is not being frozen".

**First-pass claim (from collaborator AI)**: ChaInfo has no `animFace`, only `animBody` and `animTongueEx`.

**Second-pass cross-check**: don't trust a single source. Verified independently:
1. ilspy on `Koikatu_Data/Managed/Assembly-CSharp.dll`: ChaInfo has only
   ```csharp
   public Animator animBody { get; protected set; }
   public Animator animTongueEx { get; protected set; }
   ```
2. ilspy on the IllusionLibs NuGet stub (`illusionlibs.koikatu.assembly-csharp/2019.4.27.4`): consistent.
3. `KK_HSceneOptions` codebase grep: only one hit, `animBody.GetCurrentAnimatorStateInfo`. Touches no other `anim*`.
4. `KK_Plugins`, `IllusionModdingAPI`: 0 hits — no one touches any `anim*` field.
5. **Whole-assembly grep for `animTongueEx`**: only 3 references — the property declaration, a single `objTongueEx.GetComponent<Animator>()` assignment, and a cleanup setting it to null.

**Refined conclusion**:
- ✅ Collaborator was right: `animFace` doesn't exist; the reflection lookup was a no-op and our previous code was doing nothing.
- ⚠️ Collaborator was misleading: their suggested fix was to use `animTongueEx` instead, but **the game itself never calls `.speed` / `.Play` / `.SetTrigger` on it**. It's a cached but never-driven `Animator` handle. Setting `speed = 0` on it is also a no-op in the current game version.
- The actual lever for face/expression/lip-sync is `animBody.speed = 0`. animBody uses Unity's layer system, with the face controller layered on top of the body — `AnimationEvent`s (blinks, mouth shapes, expression changes) on those layers stop together with the master animator. KK_HSceneOptions has only ever touched `animBody` and no one has complained, which corroborates this.

**Fix**:
- Direct `c.animBody` access (replacing the reflection lookup).
- Also call `c.animTongueEx` as a "belt-and-braces" backup (in case a future game patch starts driving it), but don't assume it has any current effect.
- Comment in code clearly labels `animTongueEx` as a no-op standby slot.

**Lessons**:
1. Don't write KK API field names from memory — always ilspy first.
2. Don't blindly trust collaborator suggestions either — they may correctly identify a problem but propose a fix based on incomplete evidence.
3. Any "let's also cover field X just to be safe" decision must be labelled as evidence-based or belt-and-braces, otherwise the next audit will treat it as dead code and remove it.

---

### B6 — `ReapplyIfFrozen` was missing the voice / audio steps (raised by collaborator AI)
**Symptom**: After switching position / partner while frozen, the new female's moan / mouth voice / other AudioSources would punch through the freeze and play until resume.

**Cause**: `Freeze()` runs steps 1 through 4c (animator + bone + particle + StopFemaleVoices + FreezeFemaleAudio), but `ReapplyIfFrozen` only re-ran steps 1–4 (animator + bone + particle), missing 4b and 4c.

**Fix**: `ReapplyIfFrozen` now calls `StopFemaleVoices()` and `FreezeFemaleAudio()` as well. The HashSet caches (B3) handle dedupe so repeated calls don't pile up.

**Lesson**: When you add or remove a step from `Freeze()`, **update `ReapplyIfFrozen` in the same commit**. Their step sets must mirror each other exactly. Long-term: extract a shared `ApplyFreezeSteps()` helper to prevent further drift (TODO).

---

### B7 — `_extraMales` comment fabricated "KPlug additions" (raised by collaborator AI)
**Symptom**: comment claimed `_extraMales = male1 (darkness) + KPlug additions`, but the init code only grabbed `male1`. Comment and implementation were divergent.

**Verification**: ilspy on vanilla `HSceneProc` shows exactly two `ChaControl`-typed male slots: `male` and `male1`. No `male2`/`male3`/`maleNpc`.

**Fix**: rewrote the comment to honestly describe current capability (`_extraMales currently sourced from HSceneProc.male1 only`). Supporting KPlug-style mods that add extra males will require finding where those mods stash them — possibly hooking a different class entirely — **not** just grepping `HSceneProc` for more `male*` fields.

**Lesson**: Don't write comments that describe future capabilities. Comments describe what the code does now, not what you wish it did.

---

## User feedback (v0.1 playtest, unresolved)

Recorded verbatim from a tester. Each item still needs investigation/repro before fix — none of this is yet diagnosed or attempted.

- **F1 — Head tracking + blinking still active during freeze.** Our `HMotionEyeNeckFemale.Proc` prefix-skip evidently isn't the only driver. Possibilities to investigate: blink driven from a separate `EyeLookController` / `EyeLookCalc` (these classes exist per ilspy, lines ~182705/183105), head turn driven from `NeckLookControllerVer2` directly, or animator events on `animBody` that we should be blocking despite `speed=0`. Need to grep callers of `chara.ChangeEyesOpenMax` etc. and identify the per-frame source.

- **F2 — Woman moans + a "crumpling/boiling noise" play during freeze with zero input.** Our `HVoiceCtrl.VoiceProc/BreathProc` prefixes return false but something else is queueing voice. Possible culprits: `Manager.Voice` has another tick path, or audio is coming from `HSeCtrl` that we thought was skipped. The "crumpling/boiling" noise sounds like fluid/SE — could be `HParticleCtrl` SE or a separate ambient loop we never silenced.

- **F3 — On resume, woman screams once but mouth doesn't move and no subtitles appear; only one voice line variant.** Two real issues here:
  1. Our resume audio plays through *our* AudioSource, decoupled from the game's lip-sync / subtitle pipeline, so mouth and text don't follow.
  2. Only one wav per slot → no variety. User suggestion: pool of clips selected at random, ideally context-aware (intercourse vs head etc).
  Possible better path: invoke the game's own voice trigger (`Singleton<Voice>.Instance.Play(...)` or whatever HVoiceCtrl uses) so mouth + subtitles align, falling back to our wav only if we want a custom one.

- **F4 — UI input is blocked / queued while the resume scream is playing.** Selecting anything during the resume window (~16s right now) just queues the action. Probably tied to KKAPI or some UI gating we don't own — but might also be us holding a coroutine that blocks input. Investigate.

- **F5 — Boop plugin does not work during freeze.** Boop hooks `HandCtrl` (touch / grab system). We're skipping `HMotionEyeNeck.Proc` etc., but Boop itself shouldn't be affected by our patches. May be that Boop relies on animator state which is `speed=0` now. **User explicitly wants Boop to work in frozen time** — needs investigation.

- **F6 — Touching the female changes her expression even during freeze.** The touch → expression path bypasses `HMotionEyeNeck.Proc` (which we skip). Likely `HandCtrl` calls `ChangeEyesPtn`/`ChangeMouthPtn` directly via something like `HExpression` reaction system. Need to find that callsite and either skip-prefix it during freeze, or accept it (touching is intentional player action — could be a feature).

- **F7 — Hair / skirt physics turn off when time freezes.** This is currently *intended*: our `FreezeFemaleBones` disables `DynamicBone` and `DynamicBone_Ver02`. User finds it ugly — they want hair/cloth to keep draping naturally during freeze (gravity-settled), not be locked mid-motion. Options to consider: don't disable DynamicBone at all (let physics keep simulating with the body frozen — bones will drape), or only disable for a moment then re-enable.

- **F8 — Freezing time reverts an in-progress ahegao expression.** The female loses her current ahegao face the moment we freeze. Our `HMotionEyeNeck.Proc` skip prevents new expression updates, but something is *replacing* the current face on the freeze frame — possibly because we restore animator speed=0 mid-state and the next layer state evaluation hits a default, or `HFlag.speedCalc=0` resets a tied state. Capture the current `eyesPtn`/`mouthPtn`/`eyebrowPtn` on freeze and re-apply on the same frame after the prefixes engage.

- **F9 — Fluid particles don't fall during freeze, until the female reaches a certain animation timestamp.** Our `ParticleSystem.Pause(true)` halts simulation entirely, so existing fluid blobs hang in mid-air. User wants gravity to keep working on existing particles (only stop emission of new ones). Switch from `Pause()` to setting the emission module enabled=false (cache + restore) so existing particles continue to simulate to ground.

- **F10 — Free H: cannot select HSprite UI buttons (speed up/down, auto, position change) while frozen.** Possibly the same root as F4 — UI is gated. **User wants to be able to control free H actions during freeze.** Might need to NOT freeze whatever the HSprite click handler depends on, or whitelist specific actions to bypass our prefixes.

  Sub-issue: user reports `Accumulation Rate` config "seems to affect non-frozen time as well". This shouldn't be happening — `InjectGauge()` is only called in `Resume()`. Could be perception (post-resume gauge bump appears continuous if you resume mid-progression), but verify: log gauge values across a full freeze→resume cycle and confirm the only change is the one explicit injection.

---

## Known unresolved risks / Follow-ups

### R1 — `AudioManager` holds strong refs to `AudioClip`s forever
- **Severity**: trivial
- **Status**: 4 small wav files for the lifetime of the process. Not actually a leak.
- **When it'd matter**: if we ever add "reload SFX at runtime" (e.g. respond to ConfigurationManager changes), each reload would leak one set of clips unless we destroy the old ones.
- **Prevention**: in any future reload path, `if (oldClip != null) UnityEngine.Object.Destroy(oldClip);`

### R2 — Protagonist detection is hardcoded to `HSceneProc.male`
- **Severity**: medium
- **Trigger**: any scenario where the actual player camera is bound to a female and not `male` (e.g. darkness female-protagonist viewpoint, or a mod that switches the camera). We'd freeze the player themselves.
- **Prevention**: add a config like `Untouched Character Index`, or query KKAPI for the active controlled character.

### R3 — KPlug / other mods with extra male slots aren't covered
- **Severity**: medium
- **Status**: only vanilla `HSceneProc.male1` is grabbed. If KPlug stores extra males in a different class, container, or runtime injection, we won't see them.
- **Trigger**: KPlug multi-male scenarios — extra male NPCs continue to act normally during freeze.
- **Prevention**: when this is observed, dig into the offending mod's source to find where it stores extra males. Likely needs hooking a different class instead of `HSceneProc`.

### R4 — ~~`animFace` reflection lookup~~ (fixed, see B5)
~~No longer a risk~~ — `animFace` doesn't exist on ChaInfo at all. We now access `animBody` and `animTongueEx` directly.

### R5 — `Manager.Voice.Instance.Stop(transVoiceMouth[i])` only covers the active mouth slot
- **Severity**: low (already mitigated by step 4c `FreezeFemaleAudio`)
- **Cause**: `transVoiceMouth` is a fixed length-2 array representing the two voice slots currently bound to mouths. In 3P/4P, non-active females' voices aren't in there.
- **Status**: step 4c walks `GetComponentsInChildren<AudioSource>()` and pauses everything, which already covers this in practice. The voice prefix patches on `HVoiceCtrl.VoiceProc/BreathProc` also block new voice queueing.
- **Follow-up**: if we still see leakage in some scenarios, broaden to a scene-level `_proc.GetComponentsInChildren<AudioSource>()` excluding the male hierarchy.

### R6 — Race window during position switch
- **Severity**: low
- **Trigger**: while frozen, the player triggers a position change → `ChangeAnimator` postfix calls `ReapplyIfFrozen` to set speed=0 on the new animator set. If KK takes 1–2 frames after the postfix to actually swap animators (not yet observed in practice), the new female could "twitch briefly" before being re-pinned.
- **Prevention**: if observed, defer `ReapplyIfFrozen` by one or two frames via a coroutine, or install a `LateUpdate` watchdog inside the controller.

### R7 — Hotkey collision (T conflicts with other plugins' push-to-talk)
- **Severity**: trivial
- **Status**: default is `T`; user can rebind in ConfigurationManager.
- **Prevention**: README mention, or change default to e.g. `Ctrl+T` to reduce collision risk.

---

## Inviolable development rules ("don't break these")

1. **Must target net35.** Reject any PR that bumps to net46/net472. See B1.
2. **No spin-waiting on Unity APIs from the main thread.** Any `while (notReady) { }` must become a coroutine `yield`. See B2.
3. **Caches that get re-touched must be `HashSet`/`Dictionary`.** Never `List.Add` and hope for dedupe. See B3.
4. **Singleton `Bind` must be idempotent.** Call `Unbind` first if there's an existing instance. See B4.
5. **The protagonist (`HSceneProc.male`) is never added to the frozen-subjects set.** Hardcoded into `FrozenSubjects()` — don't accidentally include him.
6. **Every log line carries the `ZAWA>` prefix.** Use `Plugin.LogI/LogW/LogE`, not `Logger.LogInfo` directly. Makes our output greppable amid other plugins' noise.
7. **`UnpatchSelf` on `OnDestroy`.** `Plugin.OnDestroy` must unpatch Harmony, otherwise reload stacks old patches on top of new ones.
8. **Don't write KK API field names from memory.** Use ilspy. ChaInfo's only Animators are `animBody` and `animTongueEx` — there's no `animFace`/`animOption`. `HSceneProc`'s only male slots are `male` and `male1`. See B5, B7.
9. **When `Freeze()` steps change, update `ReapplyIfFrozen()` in the same commit.** Their step sets must mirror each other or partner-switch will leak unfrozen subjects. See B6. Long-term: extract a shared helper.
10. **Comments describe what the code does now, not what you wish it did.** Don't fabricate "supports X" claims that future readers (or auditors) will discover are lies. See B7.
11. **Cross-verify collaborator AI / external suggestions.** They may correctly identify a problem, but the proposed fix may rest on incomplete evidence. Always re-check with ilspy + reference plugin grep + whole-assembly usage analysis. See the second pass on B5.

---

## Development tooling / Workflow

### Decompiling KK runtime DLLs
Every DLL under the KK install directory can be decompiled directly — not only `Koikatu_Data/Managed/Assembly-CSharp.dll`, but also `BepInEx/plugins/*.dll` (other people's plugins). This is the **most direct source of evidence** for KK API behavior, learning implementation patterns, and finding undocumented fields. It outranks the NuGet stub assemblies and the reference repos.

**Install ilspycmd** (one-off):
```bash
dotnet tool install -g ilspycmd --version 8.2.0.7535
```
Note: the `latest` NuGet package is currently broken (missing `DotnetToolSettings.xml`). Pin `8.2.0.7535`.

**Decompile a single type**:
```bash
/c/Users/weiss/.dotnet/tools/ilspycmd \
  "/c/Program Files (x86)/Steam/steamapps/common/Koikatsu/Koikatu_Data/Managed/Assembly-CSharp.dll" \
  -t HSceneProc > /tmp/hsceneproc.cs
```

**Decompile the entire DLL** (slow, but lets you grep field usage globally):
```bash
/c/Users/weiss/.dotnet/tools/ilspycmd \
  "/c/Program Files (x86)/Steam/steamapps/common/Koikatsu/Koikatu_Data/Managed/Assembly-CSharp.dll" \
  > /tmp/full_asm.cs
grep -n "animTongueEx\|gaugeFemale\|whatever" /tmp/full_asm.cs
```

**Important DLL paths**:
- `Koikatu_Data/Managed/Assembly-CSharp.dll` — main game logic (HSceneProc, ChaControl, HFlag, Manager.*, etc.)
- `Koikatu_Data/Managed/Assembly-CSharp-firstpass.dll` — UnityEngine extensions, DynamicBone, etc.
- `BepInEx/core/BepInEx.dll` — BepInEx API
- `BepInEx/plugins/*.dll` — other people's plugins (good for learning patterns and finding hook points)

Other people's plugins that have public source can go into `references/`. For closed-source ones, decompile and stash under `references/<name>/<name>.decompiled.cs` (see [the SlapMod case](../references/SlapMod/SlapMod.decompiled.cs)).

### What KKAPI already provides (don't reinvent it)
KKAPI ([references/IllusionModdingAPI/](../references/IllusionModdingAPI/)) is the standard library most KK plugins build on. Before writing a new feature, grep its source — don't patch something it already wraps for you.

| You want to | KKAPI provides | Location |
|---|---|---|
| Detect H-scene start/end | `GameCustomFunctionController.OnStartH/OnEndH` (VR-aware) | `KKAPI/MainGame/GameCustomFunctionController.cs` |
| Know whether you're inside HScene | `GameAPI.InsideHScene` (static bool) | `KKAPI/MainGame/GameApi.cs` |
| Register your own game-level controller | `GameAPI.RegisterExtraBehaviour<T>(extendedDataId)` | (same) |
| HFlag mode helpers (peeping/shower etc.) | `HFlag` extension methods | `KKAPI/MainGame/Utilities/GameExtensions.cs` |
| Maker (character editor) hooks | the entire `MakerAPI` namespace | `KKAPI/Maker/` |
| Studio hooks | `StudioAPI` | `KKAPI/Studio/` |
| ConfigurationManager attribute template | `ConfigurationManagerAttributes` template | shipped by several plugins |

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
4. **Reference plugin grep** (`references/`) — see how others use the same API; check for warning comments from prior pitfalls.
5. Only trust a claim once 1–4 all line up.
