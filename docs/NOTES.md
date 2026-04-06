# KK_ZaWarudo — 注意事項 / 已知風險 / 已修 bug

開發過程踩過的坑、稽核發現的風險,以及未修的 follow-up。新進來改 code 的人 (或未來的我) 進這份文件先掃一遍可省半小時。

---

## 已修的 bug

### B1 — `MissingMethodException: System.Array.Empty`  (commit `~`)
**症狀**: BepInEx 載入 plugin 後 Awake 直接拋例外,LogOutput.log 完全沒有 `ZAWA>` 字樣,只有 BepInEx 自己的 `Loading [KK_ZaWarudo 0.1.0]` 那行。

**原因**: csproj 原本 target `net46`,C# 編譯器會把空陣列字面量降級成 `Array.Empty<T>()` 呼叫。但 KK 跑在 Unity 5.6 / Mono .NET 3.5 runtime,**沒有** `Array.Empty`。

**修法**: csproj 改 `<TargetFramework>net35</TargetFramework>`,跟 KK_HSceneOptions 等所有 KK 慣例對齊。

**教訓**: 任何 KK plugin 都該 target net35。即便編譯成功也不代表執行不會炸 — runtime 的 BCL 表面比 net46 小得多。

---

### B2 — SFX 載入回傳空殼 AudioClip (length=0, name="") (commit `~`)
**症狀**: log 顯示 `enter=True during=True ...` 但 `[Enter] playing (0.00s, vol=1.00)` 馬上 done,什麼都聽不到。

**原因**: 原本 `TryLoad` 是同步函式,在 main thread 上 spin-wait `while (clip.loadState != Loaded)`。`WWW` 的下載 pump **靠 main thread 跑**,spin 把 main thread 卡死 → loadState 永遠到不了 `Loaded` → spin 撞 guard 上限後返回未載入完成的 clip,length/name 都還沒填。

**修法**: 改用 coroutine,`yield return new WWW(uri)`,讓 main thread 跑完 pump 再讀 clip。`AudioManager.StartLoad()` 在 `Plugin.Awake` 被叫一次就好,clip 在玩家進 HScene 前早就載完。

**教訓**: Unity main thread 上的 `WWW` 永遠用 `yield return www` 等待,**不要**用 spin loop。即使檔案是 local file:// 也一樣。

---

### B3 — ChangeAnimator 重複觸發導致 cache 線性成長 (稽核發現,commit `~`)
**症狀**: 切體位 / 換對象 N 次後,resume 時 log 顯示 `re-enabled bones=` 數字爆漲。功能仍正確 (重複 enable 同一個 bone 是 idempotent),但 GC pressure 與 list traversal 隨時間退化。

**原因**: `FreezeFemaleBones` / `FreezeFemaleAudio` / `FreezeParticles` 用 `List.Add` 沒去重。`ReapplyIfFrozen` (在 `HSceneProc.ChangeAnimator` postfix 呼叫) 每次切體位/換人都會把當下所有 Component 再塞一次。

**修法**: cache 改 `HashSet<T>`,`if (set.Add(x)) DoAction(x)` 模式,自然去重。`_animSpeeds` 已是 Dictionary 用 `ContainsKey` 防重沒問題。

**教訓**: 任何會被反覆呼叫的 cache 方法,容器選 `HashSet` / `Dictionary`,絕對不要用 `List`。

---

### B4 — Bind 沒有防重入,舊 Instance 會被無聲覆蓋 (稽核發現,commit `~`)
**症狀**: 理論上不會觸發 — 需要 `MapSameObjectDisable` 在沒先 `OnDestroy` 的情況下 fire 兩次 (BepInEx hot reload、KKAPI re-init、或其他 plugin 強制重 init HScene)。一旦觸發,前一場的 cache 全部丟失,被 freeze 的 animator/bone **永遠停在 frozen 狀態**,直到下一次 Resume — 但下一次 Resume 看到的是新 Instance,iter 的是空 cache,什麼也救不回來。

**修法**: `Bind` 進入時若 `Instance != null`,先呼叫 `Unbind()` (它會 trigger `Resume()` 還原舊狀態) 再蓋上新 Instance,並 log warning。

**教訓**: singleton 的 `Bind`/`Init` 方法永遠要 idempotent — 重入時先清舊狀態。

---

## 未修的已知風險 / Follow-up

### R1 — AudioManager 持有 AudioClip 的 strong ref 永不釋放
- **嚴重度**: 微
- **現況**: 4 個小 wav 檔,plugin 整個 process 生命週期就一份,實質非 leak
- **觸發條件**: 未來若加「執行中 reload SFX」(例如 ConfigManager 改檔名後熱重載) 而沒 `Object.Destroy(oldClip)`,每次 reload 會多堆一份 AudioClip 在 memory
- **預防**: 加 reload 路徑時記得 `if (oldClip != null) UnityEngine.Object.Destroy(oldClip);`

### R2 — 男主角偵測寫死 = `HSceneProc.male`
- **嚴重度**: 中
- **觸發條件**: 玩家視角實際綁的不是 `male` 而是某個女角時 (例如 darkness 模式女主視點 / 某些 mod 改視角),會凍住玩家自己
- **預防**: 要支援時加 config `Untouched Character Index`,或從 KKAPI 抓 `currentActiveCharacter`

### R3 — `male1` 欄位假設只在 darkness 存在
- **嚴重度**: 低
- **現況**: `Traverse.Field("male1").GetValue<ChaControl>()` 找不到欄位回傳 null,vanilla KK 不會 crash
- **觸發條件**: KPlug / 其他 mod 用不同欄位名加額外男角 (`male2` `maleNpc` 等)
- **預防**: 使用者跑 darkness 多人場景時若 log 看到 `extraMales=0`,需要查當前 KK 版本的真實欄位名再 patch hook

### R4 — `animFace` 用 reflection 拿
- **嚴重度**: 低
- **原因**: 不確定不同 KK 分支 (vanilla / Party / darkness / KKS — 雖然不支援) `ChaControl` 是否都有 `animFace` public 欄位
- **現況**: `c.GetType().GetField("animFace")?.GetValue(c) as Animator` 找不到就回 null,不 crash
- **預防**: 確認 vanilla KK 有後可改成直接 `c.animFace`

### R5 — `Manager.Voice.Instance.Stop(transVoiceMouth[i])` 只覆蓋當前主動 mouth slot
- **嚴重度**: 低 (已用 step 4c `FreezeFemaleAudio` 補強)
- **原因**: `transVoiceMouth` 是固定長度 2 的陣列,只代表「現在綁定到嘴部的兩個 voice slot」,3P/4P 中非主動女角的語音不在裡面
- **現況**: step 4c 走 `GetComponentsInChildren<AudioSource>()` 全 pause,實際上已經涵蓋
- **後續**: 若觀察到還有漏網的 NPC 語音,擴大到場景級 `_proc.GetComponentsInChildren<AudioSource>()` 並排除 male 階層

### R6 — 切體位過程的 race window
- **嚴重度**: 低
- **觸發條件**: 玩家在凍結中按切體位 → `ChangeAnimator` postfix 跑 `ReapplyIfFrozen` 重新對新 animator set speed=0。但若 KK 在 postfix 之後還有 1–2 frame 才完成 animator 切換 (尚未實測),新女角會「短暫動一下」
- **預防**: 若實機看到此症狀,改 coroutine 延後一兩 frame 再 ReapplyIfFrozen,或在 controller 內掛 LateUpdate watchdog

### R7 — Hotkey 衝突 (T 與其他 plugin push-to-talk)
- **嚴重度**: 微
- **現況**: 預設 `T`,使用者可在 ConfigurationManager 改
- **預防**: README 提醒,或預設改成 `Ctrl+T` 之類降衝突機率

---

## 開發環境鐵則 (Don't break these)

1. **必須 target net35** — 任何 PR 想升 net46/net472 直接 reject。原因見 B1。
2. **不准在 main thread 上 spin-wait Unity API** — 任何 `while (notReady) { }` 都要改成 coroutine `yield`。原因見 B2。
3. **任何反覆呼叫的 cache 方法用 HashSet/Dictionary** — 不准 List.Add 然後祈禱去重。原因見 B3。
4. **singleton Bind 必須 idempotent** — 進入時先 Unbind 舊 Instance。原因見 B4。
5. **男主角 (`HSceneProc.male`) 永遠不被加進凍結對象集合** — 寫死在 `FrozenSubjects()` 的設計裡,別誤改。
6. **所有 log 帶 `ZAWA>` prefix** — 透過 `Plugin.LogI/LogW/LogE` 而不是 `Logger.LogInfo` 直接呼叫。方便 grep 跟其他 plugin 的 log 區分。
7. **UnpatchSelf on OnDestroy** — Plugin.OnDestroy 必須 unpatch Harmony,否則 reload 時舊 patch 會疊上去。
