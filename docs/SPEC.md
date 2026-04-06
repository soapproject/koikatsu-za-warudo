# KK_ZaWarudo — 邊界文件 (Spec v0.1)

## 一句話定義
在 Koikatsu H 場景中觸發「時間停止」:凍結對方角色的動畫與物理,玩家(主角)仍可動作,結束時依設定灌入快感累積並播放特別音效。

## 靈感來源
- JoJo「ザ・ワールド」/「時よ止まれ」
- 同人作品『学園で時間よ止まれ』類型的時停 H 演出

---

## 範圍 (In Scope)

### 1. 觸發 / 解除
- 僅在 **HScene** 中可用 (`HSceneProc` 存在時)
- 熱鍵 toggle (預設 `T`,可由 ConfigurationManager 改)
- 同一場 HScene 內可重複進入/離開時停

### 2. 進入時停 (Freeze)
| 對象 | 行為 |
|---|---|
| 女角 (`lstFemale`) 動畫 | `Animator.speed = 0` (animBody / animFace) |
| 女角 DynamicBone / DynamicBone_Ver02 | 快取 enabled,disable |
| 男角 = 主角 (`male`) | **不凍結**,動畫與物理保持原狀 |
| `HFlag.speedCalc` | 設為 0 (停止快感 tick) |
| 場景 ParticleSystem | 全部 `Pause()` (汗、體液) |
| 語音 | `HVoiceCtrl` 停止當前語音;進入時停期間禁止新語音 |
| 環境音 | 不動 (BGM 維持,只壓人聲) |
| 自訂音效 | 播放「進入時停」SFX (例: ZA WARUDO 喊聲) 一次 |

進入瞬間記錄 `freezeStartTime = Time.realtimeSinceStartup`,所有被改動的狀態都要快取以供 Resume 還原。

### 3. 解除時停 (Resume)
依序執行:
1. 還原所有快取狀態 (animator speed、bones、particles、voice 解禁)
2. 依 **ResumeMode** 設定決定 **快感量 (gauge)** 注入:
   - **`Instant`**: `HFlag.gaugeFemale = 100` (max)。拉滿後是否觸發高潮交給遊戲/其他 plugin 自行決定,本 plugin 不主動呼叫 finish
   - **`Accumulated`**: `delta = (Time.realtimeSinceStartup - freezeStartTime) * AccumulationRate`,加到 `HFlag.gaugeFemale` 上 (上限 100)
3. 播放「解除時停」特別 SFX (例: 解除時的破碎聲 / 高潮音)
4. 清空快取

### 4. 設定項 (Config)
| Section | Key | Type | 預設 | 說明 |
|---|---|---|---|---|
| General | Toggle Key | KeyboardShortcut | `T` | 觸發熱鍵 |
| General | Resume Mode | enum {Instant, Accumulated} | `Accumulated` | 解除時的快感注入方式 |
| General | Accumulation Rate | float | `10.0` | Accumulated 模式下每秒累積的快感點數 |
| Audio | SFX Folder | string | `<PluginPath>/bgm/zawarudo/` | 音效資料夾 (對齊 SlapMod 慣例的 `bgm/` 子目錄) |
| Audio | Enter SFX Filename | string | `enter.wav` | 進入時停音效檔名 |
| Audio | Resume SFX Filename | string | `resume.wav` | 解除時音效檔名 |
| Audio | SFX Volume | float (0–1) | `1.0` | 自訂音效相對音量 (還會再乘上遊戲主音量) |

**載入方式 (從 `references/SlapMod/SlapMod.decompiled.cs:288` 取得的範式)**:
```csharp
// 1. 啟動時 Awake() 載入,缺檔靜默跳過
WWW www = new WWW(Utility.ConvertToWWWFormat(path));
AudioClip clip = WWWAudioExtensions.GetAudioClipCompressed(www, false, AudioType.WAV); // AudioType 20
while (clip.loadState != AudioDataLoadState.Loaded) { }

// 2. AudioSource 掛在 plugin GameObject 上
audioSource = gameObject.AddComponent<AudioSource>();
audioSource.clip = enterClip;

// 3. 播放時音量 = 遊戲主音量 * 自家 config * 0.01
audioSource.volume = Config.SoundData.Master.Volume * SfxVolume.Value * 0.01f;
audioSource.Play();
```
plugin **不附帶版權音檔**,使用者自備丟到 `bgm/zawarudo/`。

---

## 不在範圍內 (Out of Scope, v0.1)
- ❌ 全螢幕色階反轉 / 灰階濾鏡 (留待 v0.2 視覺特效)
- ❌ 時停期間玩家可主動切換體位 / 觸碰角色 (純凍結,不互動)
- ❌ Studio / Maker / Main game (學園探索) 的時停 — **僅 HScene**
- ❌ Koikatsu Sunshine (KKS) 支援 — **僅 KK / Koikatsu Party**
- ❌ VR 模式 (v0.1 不保證,但不主動破壞)
- ❌ 多人時停 (3P/4P) 的特殊處理 — 一律凍結所有非主角女角
- ❌ 網路同步 / 存檔

---

## 邊界決策與待釐清

### 已決
- **主角 = `HSceneProc.male`**,即使是女主角體位也視為「玩家視角控制者」不凍結
- 不動 `Time.timeScale` (避免破壞相機/UI)
- BGM 不停 (時停只作用於角色與快感系統,維持沉浸氛圍)

### 已決 (2026-04-07)
- **Q1**: 累積與注入的目標是 **快感量 (`HFlag.gaugeFemale`)**,不是敏感度 multiplier
- **Q2**: Instant 拉滿後 **不主動 trigger finish**,交給遊戲或其他 plugin 自然處理
- **Q3**: 時停期間 **允許** 玩家切換體位 / 操作 UI / **切換對象女角** (3P/4P 中換人),維持遊玩自由度。實作影響:`lstFemale` 在切換後 reference 可能變動,需在 `HSceneProc.ChangeAnimator` postfix 重新對「當前非主角」套用 Animator.speed=0
- **Q4**: 音效採 **SlapMod.dll 範式** (已 decompile,見 [references/SlapMod/SlapMod.decompiled.cs:288](../references/SlapMod/SlapMod.decompiled.cs#L288)):
  - 路徑 `Paths.PluginPath + "/bgm/zawarudo/<file>.wav"`
  - `WWW` + `WWWAudioExtensions.GetAudioClipCompressed(www, false, AudioType.WAV)` 載入
  - `AddComponent<AudioSource>()` + `Play()`,音量 = `Config.SoundData.Master.Volume × SfxVolume × 0.01f`

---

## 驗收條件 (v0.1 Done = 以下都成立)
- [ ] 進 HScene → 按熱鍵 → 女角完全靜止 (動畫、頭髮、衣服、汗液),男角仍可動
- [ ] 再按一次熱鍵 → 女角恢復動作,且快感依設定模式注入
- [ ] 進入/解除各播放一次自訂 SFX (若檔案存在)
- [ ] 切換 / 重複觸發 10 次無記憶體洩漏、無 NRE
- [ ] 退出 HScene 後重進 HScene,功能正常
- [ ] ConfigurationManager 中可看到所有設定項

---

## 主要參考實作
- [references/KK_HSceneOptions/](../references/KK_HSceneOptions/) — `AnimationToggle.cs`、`Hooks.cs`:HSceneProc patch + animator speed 操作的最佳前例
- [references/KK_Plugins/](../references/KK_Plugins/) — csproj/build 慣例
- [references/IllusionModdingAPI/](../references/IllusionModdingAPI/) — KKAPI helper 與 ChaControl 擴充
