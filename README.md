# KK_ZaWarudo

A BepInEx plugin for **Koikatsu** / Koikatsu Party that adds a JoJo-style "ZA WARUDO" time stop to H-scenes.

Press a hotkey during an H-scene → every other character freezes (animation, voice, expression, body SE), the player still moves, custom SFX plays. Press again to resume — the female's pleasure gauge gets injected (instant or accumulated by frozen duration) and a special voice clip plays.

> ⚠️ Adult-content plugin. Targets **Koikatsu (KK)** / Koikatsu Party only. Not Koikatsu Sunshine (KKS).

## Features

- **Hotkey toggle** in any H-scene (default `T`, configurable, with rapid-press debounce)
- **Visual freeze** of all non-protagonist characters: body anim, head turn, blink, expression, in-progress ahegao
- **Audio freeze**: global mute via `AudioListener.pause`; plugin SFX bypasses
- **Physics that still feel alive**: hair / cloth keep draping under gravity; existing fluid particles keep falling
- **Pleasure gauge does not tick during freeze** (plus an "Accumulated" mode that injects gauge proportional to frozen duration on resume)
- **4-clip serial SFX sequence** with no overlap (Enter → During loop → Exit → Female Resume), all user-supplied wavs
- **During loop intelligently gated** by `HandCtrl.IsItemTouch` so it only plays while the player is actively touching
- **Optional climax face on resume** (config-gated; pins eyes/mouth/eyebrow/tears for the resume SFX duration)
- **VR-aware** via KKAPI's `GameCustomFunctionController`
- **Partner-switch safe**: `HSceneProc.ChangeAnimator` postfix re-pins the freeze when you change position or partner

## Install

1. Have BepInEx 5.4.x set up for KK and `KKAPI ≥ 1.40` installed.
2. Drop `KK_ZaWarudo.dll` into `BepInEx/plugins/`.
3. Drop your own four wav files into `BepInEx/plugins/bgm/zawarudo/`:
   - `zawarudo_sfx_enter.wav` — start-of-freeze SFX
   - `zawarudo_female_during.wav` — female voice loop while frozen
   - `zawarudo_sfx_exit.wav` — end-of-freeze SFX
   - `zawarudo_female_resume.wav` — female voice on resume
4. Launch the game; configure via [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) (`F1`).

The plugin **does not ship copyrighted audio**. Missing files are silently skipped (warning to log) — the freeze still works, just without that clip.

## Configuration

All settings live under `KK_ZaWarudo` in ConfigurationManager.

| Section | Key | Default | Notes |
|---|---|---|---|
| General | Toggle Key | `T` | Hotkey |
| General | Toggle Cooldown | `0.3` | Min seconds between toggles (debounce) |
| General | Resume Mode | `Accumulated` | `Instant` = jam gauge to 100; `Accumulated` = `frozen_seconds × Accumulation Rate` |
| General | Accumulation Rate | `10.0` | Gauge points per frozen second |
| Climax Face | Enable | `false` | Inject a climax face on resume, pinned for the resume SFX duration |
| Climax Face | Eyes / Mouth / Eyebrow Pattern | `4` / `5` / `4` | `ChangeXxxPtn` indices — KK pattern numbers vary per character, iterate to taste |
| Climax Face | Tears Level | `3` | 0–3 |
| Audio | SFX Folder | `<plugins>/bgm/zawarudo/` | |
| Audio | 1./2./3./4. SFX | `zawarudo_*.wav` | Filenames inside SFX Folder |
| Audio | SFX Volume | `1.0` | Multiplied by game master volume |
| Audio | Play During Loop | `true` | Master switch for the during-loop |
| Audio | During Loop Only While Active | `true` | Only loop while player is actively touching |

See [docs/SPEC.md](docs/SPEC.md) for the full design and [docs/NOTES.md](docs/NOTES.md) for known issues, dev rules, and the playtest feedback log.

## Known limitations

- **Free H HSprite UI is unresponsive while frozen** (speed up/down, position change, auto, etc.). KK's state machine is gated on animator advancement, which we deliberately stop. Workaround: unfreeze, click, refreeze. See `docs/NOTES.md` F10.
- **Touching/grabbing the breast can't be released without unfreezing** (same root cause). See F11.
- **Resume scream has no mouth movement / no subtitles / single voice line** — playing through our own AudioSource decouples from the game's lip-sync pipeline. Game-side voice trigger (`Manager.Voice.Play`) exists; integration is v0.2 work. See F3.
- **BGM is muted during freeze** (intentional — global silence is the simpler correct answer per playtest feedback).
- **KKS not supported** and not planned.
- **VR is best-effort**: KKAPI dispatches the lifecycle for both `HSceneProc` and `VRHScene`, and we patch `VRHScene.ChangeAnimator` if present, but VR is not regularly tested.

## Building from source

Requires .NET SDK with `dotnet` on PATH. KK install path is **not** required to build (NuGet stub assemblies are used).

```sh
dotnet restore src/KK_ZaWarudo/KK_ZaWarudo.csproj
dotnet build  src/KK_ZaWarudo/KK_ZaWarudo.csproj
```

The output `KK_ZaWarudo.dll` lands in `src/KK_ZaWarudo/bin/Debug/`. Targets `net35` (matches Unity 5.6 / Mono runtime — see `docs/NOTES.md` B1 for why this matters).

The IllusionMods NuGet feed (where KKAPI lives) is configured in [`nuget.config`](nuget.config); no extra setup needed.

## Repository layout

```
.
├── src/KK_ZaWarudo/      # plugin source
├── docs/
│   ├── SPEC.md           # design spec
│   └── NOTES.md          # bugs, risks, dev rules, playtest feedback log
├── references/           # gitignored — clones of reference plugins for offline grep
└── nuget.config
```

The `references/` directory is excluded from git. Clone the relevant repos there yourself for source-grep workflows; see `docs/NOTES.md` "Decompiling KK runtime DLLs" for the workflow.

## Acknowledgments

- [KK_HSceneOptions](https://github.com/MayouKurayami/KK_HSceneOptions) — `HVoiceCtrl.VoiceProc/BreathProc` prefix-mute pattern, VRHScene field reference, HSceneProc init hook point.
- [IllusionMods/KK_Plugins](https://github.com/IllusionMods/KK_Plugins) — csproj/build conventions, IllusionMods NuGet feed, Boop source.
- [IllusionModdingAPI](https://github.com/IllusionMods/IllusionModdingAPI) — `GameCustomFunctionController` lifecycle, `GameAPI.InsideHScene`.
- [SlapMod](#) — WAV loading + AudioSource playback pattern (decompiled into `references/SlapMod/`).
- [KK Plugins Compendium](https://github.com/Frostation/KK-Plugins-Compendium) — community index for finding similar work.

## License

TODO. Source is intended to be permissive (MIT/Apache-2.0); will pick before tagging v0.1.
