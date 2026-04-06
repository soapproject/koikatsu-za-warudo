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

### B5 — `animFace` 反射查找永遠回傳 null (collaborator AI 稽核發現,自行交叉驗證後微調結論)
**症狀**: collaborator 報告「臉/舌頭動畫不被凍結」。

**第一輪查證 (collaborator AI 主張)**: ChaInfo 上沒有 `animFace`,只有 `animBody` 和 `animTongueEx`。

**第二輪交叉驗證**: 不能無腦相信單一來源。我又查了:
1. ilspy 對 `Koikatu_Data/Managed/Assembly-CSharp.dll`:確認 ChaInfo 只有
   ```csharp
   public Animator animBody { get; protected set; }
   public Animator animTongueEx { get; protected set; }
   ```
2. ilspy 對 IllusionLibs NuGet stub (`illusionlibs.koikatu.assembly-csharp/2019.4.27.4`):一致
3. KK_HSceneOptions 整個 codebase grep:只看到一處 `animBody.GetCurrentAnimatorStateInfo`,完全沒碰其他 anim*
4. KK_Plugins、IllusionModdingAPI:0 hits,沒人碰任何 anim* 欄位
5. **whole-assembly grep `animTongueEx`**: 只有 3 處 reference
   - 屬性宣告
   - `objTongueEx.GetComponent<Animator>()` 賦值一次
   - cleanup 設 null

**修正後的結論**:
- ✅ collaborator 對的部分:`animFace` 不存在,反射查找是 no-op,我之前的 code 等於什麼都沒做
- ⚠️ collaborator 誤導的部分:他建議用 `animTongueEx` 替代,但實際上**遊戲本身從來沒對它呼叫 `.speed`/`.Play`/`.SetTrigger`**。那只是個被 cache 但沒被驅動的 Animator handle。設它 speed=0 在現行遊戲版本是 **no-op**,跟原本反射查找的結果是一樣的
- 真正凍結臉/表情/lip-sync 的關鍵是 **`animBody.speed = 0`** — animBody 用 layer 系統把臉部 controller 疊在身上,layer 上的 AnimationEvent (眨眼、口型、表情) 會跟著主 animator 的 speed 一起停。KK_HSceneOptions 多年只動 animBody 沒人抱怨,印證這點。

**修法**:
- 直接呼叫 `c.animBody` (取代反射)
- 順便也呼叫 `c.animTongueEx` 作為「belt-and-braces」備援 (將來遊戲若 patch 開始驅動 tongue,自動 cover),但不要假設它有效
- 註解寫清楚 animTongueEx 是 no-op 預備位

**教訓**:
1. **不要憑記憶寫欄位名**,永遠 ilspy
2. **也不要無腦相信 collaborator 的建議** — 對方的論證可能正確指出問題,但提出的修法可能基於不完整的證據
3. 任何「為了安全多 cover 一個欄位」的決定要寫清楚是 evidence-based 還是 belt-and-braces,以免下次稽核又被當成 dead code 砍掉

---

### B6 — `ReapplyIfFrozen` 漏掉 voice / audio steps (collaborator AI 稽核發現)
**症狀**: 凍結中切體位或換對象後,新女角的 moan / mouth voice / 其他 AudioSource 會穿透凍結一路播到 resume。

**原因**: `Freeze()` 走完 step 1 到 4c (animator + bone + particle + StopFemaleVoices + FreezeFemaleAudio),但 `ReapplyIfFrozen` 只重跑 step 1–4 (animator + bone + particle),漏了 4b 和 4c。

**修法**: `ReapplyIfFrozen` 補上 `StopFemaleVoices()` 跟 `FreezeFemaleAudio()` 呼叫。HashSet cache (B3 修過) 會自動 dedupe,重複呼叫不會堆積。

**教訓**: freeze 步驟新增/刪減時,**同步更新 ReapplyIfFrozen**。兩個方法的步驟順序與覆蓋集合必須鏡像。考慮把兩者抽成一個 `ApplyFreezeSteps()` helper 共用,避免再分岔 (TODO)。

---

### B7 — `_extraMales` 註解假造「KPlug additions」(collaborator AI 稽核發現)
**症狀**: 註解寫 `_extraMales = male1 (darkness) + KPlug additions`,實際上 init 只抓 `male1` 一個欄位。spec/註解和實作分岔。

**驗證**: ilspy 檢查 vanilla `HSceneProc`,只有兩個 ChaControl 男角欄位:`male` 跟 `male1`。沒有 `male2/male3/maleNpc`。

**修法**: 改寫註解誠實描述目前能力 (`_extraMales currently sourced from HSceneProc.male1 only`)。要支援 KPlug 等 mod 額外男角時,得另外查那些 mod 用什麼欄位/容器、可能需要 hook 不同 class,**不是** 在 vanilla HSceneProc 上掃 male* 欄位就能解決。

**教訓**: 不要在註解寫「未來擴充」式的虛構能力。註解寫的是 code 現在做什麼,不是 code 想做什麼。

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

### R3 — KPlug / 其他 mod 加的額外男角不會被凍結
- **嚴重度**: 中
- **現況**: 只抓 vanilla `HSceneProc.male1`,KPlug 若把額外男角放在別的 class/容器/runtime 注入,本 plugin 不會看到
- **觸發條件**: 跑 KPlug 多男角場景,多出來的男配角持續正常動作
- **預防**: 實機觀察到漏網時,要查 KPlug 原始碼確認額外男角的存放位置,可能要 hook 不同 class 而非 HSceneProc

### R4 — ~~`animFace` 用 reflection 拿~~ (已修,見 B5)
~~此風險已不存在~~ — `animFace` 根本不存在於 ChaInfo,改直接抓 `animBody` + `animTongueEx`。

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
8. **不准憑記憶寫 KK API 欄位名** — 查 ilspy。`ChaInfo` 上的 Animator 只有 `animBody` 和 `animTongueEx`,沒有 `animFace`/`animOption`。`HSceneProc` 上的男角只有 `male`/`male1`。原因見 B5、B7。
9. **`Freeze()` 步驟調整時必須同步更新 `ReapplyIfFrozen()`** — 兩者覆蓋的 step 集合必須鏡像,否則切體位/換對象後新 subjects 會漏網。原因見 B6。長期看應抽 helper 共用。
10. **註解寫程式現在做什麼,不寫想做什麼** — 別在註解裡編造尚未實作的能力 (例: 「supports KPlug additions」),會誤導稽核者也誤導未來的自己。原因見 B7。
11. **Collaborator AI / 外部建議要交叉驗證** — 對方指出的「問題」可能是真的,但他提出的「修法」可能基於部分證據。永遠用 ilspy + reference plugin grep + 整個 assembly 的使用情境再驗一次。原因見 B5 第二輪查證。

---

## 開發工具 / Workflow

### 反編譯 KK runtime dll
KK 安裝目錄底下所有 dll 都可以直接 decompile 來研究 — 不只 `Koikatu_Data/Managed/Assembly-CSharp.dll`,連 `BepInEx/plugins/*.dll` (其他人的 plugin) 也可以。這是查 KK API、學別人實作模式、找未知欄位的**最直接證據來源**,優先級高於 NuGet stub 跟 reference repo。

**安裝 ilspycmd** (一次):
```bash
dotnet tool install -g ilspycmd --version 8.2.0.7535
```
注意:`latest` 版本目前 NuGet 套件 broken,要 pin `8.2.0.7535`。原因見 [b4 install attempt history]。

**Decompile 一個 type**:
```bash
/c/Users/weiss/.dotnet/tools/ilspycmd \
  "/c/Program Files (x86)/Steam/steamapps/common/Koikatsu/Koikatu_Data/Managed/Assembly-CSharp.dll" \
  -t HSceneProc > /tmp/hsceneproc.cs
```

**Decompile 整個 dll** (慢,但可以 grep 任何欄位的全局使用):
```bash
/c/Users/weiss/.dotnet/tools/ilspycmd \
  "/c/Program Files (x86)/Steam/steamapps/common/Koikatsu/Koikatu_Data/Managed/Assembly-CSharp.dll" \
  > /tmp/full_asm.cs
grep -n "animTongueEx\|gaugeFemale\|whatever" /tmp/full_asm.cs
```

**重要 dll 路徑**:
- `Koikatu_Data/Managed/Assembly-CSharp.dll` — 主遊戲邏輯 (HSceneProc, ChaControl, HFlag, Manager.* 等)
- `Koikatu_Data/Managed/Assembly-CSharp-firstpass.dll` — UnityEngine extensions, DynamicBone 等
- `BepInEx/core/BepInEx.dll` — BepInEx API
- `BepInEx/plugins/*.dll` — 其他人的 plugin (學模式、找 hook 點)

別人的 plugin 已經 commit 過的可以放到 `references/`,沒有原始碼的就 decompile 後放到 `references/<name>/<name>.decompiled.cs` (見 [SlapMod 的 case](../references/SlapMod/SlapMod.decompiled.cs))。

### 查證流程 (任何懷疑 KK API 行為時)
1. **ilspy 真實 game dll** — 第一手證據,類別實際長相
2. **ilspy 想看的 type 用 `-t TypeName`** — 看單一類別的成員、欄位、方法簽名
3. **whole-assembly grep** — 看某個欄位/方法在遊戲內被誰呼叫、被怎麼用,辨別「存在但沒被使用」vs「真的有作用」
4. **reference plugin grep** (`references/`) — 看別人怎麼用同樣的 API,有沒有踩過坑的註解
5. 上面 1–4 都通才相信
