# KK_ZaWarudo — 邊界文件 (Spec v0.1)

## 一句話定義
在 Koikatsu H 場景中觸發「時間停止」:凍結對方角色的動畫與物理,玩家(主角)仍可動作,結束時依設定灌入快感累積並播放特別音效。

## 靈感來源
- JoJo「ザ・ワールド」/「時よ止まれ」
- 同人作品『学園で時間よ止まれ』類型的時停 H 演出

---

## 核心決策
- **目標遊戲**: Koikatsu (KK) / Koikatsu Party。**不**支援 KKS。
- **生效範圍**: 僅 HScene (`HSceneProc` 存在時),不涉及 Studio / Maker / Main game。
- **主角定義**: `HSceneProc.male`,即便是女主角體位也視為「玩家視角控制者」,**永遠不被凍結**。
- **時間軸**: 不動 `Time.timeScale` (避免破壞相機/UI),只動 per-Animator speed 與相關物理。
- **BGM**: 不停,維持沉浸氛圍;凍結只作用於角色動畫、物理、快感系統與人物語音。
- **時停期間玩家自由度**: 允許切換體位、操作 UI、**切換對象女角** (3P/4P 中換人)。
- **快感注入目標**: `HFlag.gaugeFemale` (快感量,非敏感度 multiplier)。
- **不主動 trigger finish**: Instant 模式拉滿 gauge 後是否進入高潮交給遊戲/其他 plugin 自然處理。

---

## 範圍 (In Scope)

### 1. 觸發 / 解除
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
| 自訂音效 | 播放「進入時停」SFX 一次 |

進入瞬間記錄 `freezeStartTime = Time.realtimeSinceStartup`,所有被改動的狀態都要快取以供 Resume 還原。

### 3. 切換對象女角 (時停期間)
`HSceneProc.ChangeAnimator` postfix 在 frozen 狀態下重新對當前非主角套用 Animator.speed = 0、disable bones、pause particles,確保切換後的新女角不會動。

### 4. 解除時停 (Resume)
依序執行:
1. 還原所有快取狀態 (animator speed、bones、particles、speedCalc)
2. 依 **ResumeMode** 注入 `HFlag.gaugeFemale`:
   - **`Instant`**: 直接設為 100
   - **`Accumulated`**: `delta = (Time.realtimeSinceStartup - freezeStartTime) * AccumulationRate`,加到目前 gauge 上 (上限 100)
3. 播放「解除時停」SFX
4. 清空快取

### 5. 設定項 (Config)
| Section | Key | Type | 預設 | 說明 |
|---|---|---|---|---|
| General | Toggle Key | KeyboardShortcut | `T` | 觸發熱鍵 |
| General | Resume Mode | enum {Instant, Accumulated} | `Accumulated` | 解除時的快感注入方式 |
| General | Accumulation Rate | float | `10.0` | Accumulated 模式下每秒累積的快感點數 |
| Audio | SFX Folder | string | `<PluginPath>/bgm/zawarudo/` | 音效資料夾 (對齊 SlapMod 慣例的 `bgm/` 子目錄) |
| Audio | 1. Enter SFX | string | `zawarudo_sfx_enter.wav` | 時停**開始**音效 (Freeze 序列第 1 段) |
| Audio | 2. Female During SFX (loop) | string | `zawarudo_female_during.wav` | 時停**過程中**女角聲音,Enter 結束後接著播,**循環直到 Resume** |
| Audio | 3. Exit SFX | string | `zawarudo_sfx_exit.wav` | 時停**結束**音效 (Resume 序列第 1 段,中斷 During loop) |
| Audio | 4. Female Resume SFX | string | `zawarudo_female_resume.wav` | 時停結束時的**女角聲音**,Exit 結束後接著播 |

**檔名規則**: `zawarudo_<role>_<phase>.wav`
- `role` ∈ { `sfx`, `female` }
- `phase` ∈ { `enter`, `during`, `exit`, `resume` }
- `zawarudo_` prefix 避免與其他 plugin 的 bgm/ 檔案撞名
| Audio | SFX Volume | float (0–1) | `1.0` | 自訂音效相對音量 (還會再乘上遊戲主音量) |

**播放序列 (單一 AudioSource,絕不重疊)**:
- **Freeze**: `Enter (one-shot, 等播完)` → `During (loop 直到被取消)`
- **Resume**: `中斷 During` → `Exit (one-shot, 等播完)` → `Female Resume (one-shot)`

### 6. 音效載入範式
參考 [references/SlapMod/SlapMod.decompiled.cs:288](../references/SlapMod/SlapMod.decompiled.cs#L288):
```csharp
// 1. 啟動時 Awake() 載入,缺檔靜默跳過 (僅警告 log)
WWW www = new WWW(Utility.ConvertToWWWFormat(path));
AudioClip clip = WWWAudioExtensions.GetAudioClipCompressed(www, false, AudioType.WAV);
while (clip.loadState != AudioDataLoadState.Loaded) { }

// 2. AudioSource 掛在 plugin GameObject 上
audioSource = gameObject.AddComponent<AudioSource>();

// 3. 播放時音量 = 遊戲主音量 * 自家 config * 0.01
audioSource.volume = Config.SoundData.Master.Volume * SfxVolume.Value * 0.01f;
audioSource.PlayOneShot(clip);
```
plugin **不附帶版權音檔**,使用者自備丟到 `bgm/zawarudo/`。

### 7. Logging
所有 log 帶 prefix `ZAWA>`,方便從 BepInEx LogOutput.log 過濾出本 plugin 的訊息 (避開其他 plugin 干擾)。

---

## 不在範圍內 (Out of Scope, v0.1)
- ❌ 全螢幕色階反轉 / 灰階濾鏡 (留待 v0.2 視覺特效)
- ❌ Studio / Maker / Main game (學園探索) 的時停
- ❌ Koikatsu Sunshine (KKS) 支援
- ❌ VR 模式 (不保證,但不主動破壞)
- ❌ 網路同步 / 存檔
- ❌ 主動 trigger finish / 切角色狀態以外的互動 (純凍結 + gauge 注入)

---

## 驗收條件 (v0.1 Done = 以下都成立)
- [ ] 進 HScene → 按熱鍵 → 女角完全靜止 (動畫、頭髮、衣服、汗液),男角仍可動
- [ ] 再按一次熱鍵 → 女角恢復動作,且快感依設定模式注入
- [ ] 時停期間切換體位 / 換對象女角後,新女角依然靜止
- [ ] 進入/解除各播放一次自訂 SFX (若檔案存在)
- [ ] 切換 / 重複觸發 10 次無記憶體洩漏、無 NRE
- [ ] 退出 HScene 後重進 HScene,功能正常
- [ ] ConfigurationManager 中可看到所有設定項
- [ ] LogOutput.log 用 `grep ZAWA>` 可清晰追蹤 freeze/resume 整個生命週期

---

## 主要參考實作
- [references/KK_HSceneOptions/](../references/KK_HSceneOptions/) — `AnimationToggle.cs`、`Hooks.cs`:HSceneProc patch + animator speed 操作的最佳前例;`MapSameObjectDisable` 為 init hook 點
- [references/KK_Plugins/](../references/KK_Plugins/) — csproj/build 慣例
- [references/IllusionModdingAPI/](../references/IllusionModdingAPI/) — KKAPI helper 與 ChaControl 擴充
- [references/SlapMod/SlapMod.decompiled.cs](../references/SlapMod/SlapMod.decompiled.cs) — wav 載入與 AudioSource 播放範式
