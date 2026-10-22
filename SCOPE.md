# GPS Tracker 桌面版（PC 版）開發規格

> 給任何 AI 開發工具（Kilo Code、Claude Code…）讀的規格。開工前先讀本檔和 `DEV_PROGRESS.md`，
> 從 `DEV_PROGRESS.md` 第一個「未開始／進行中」的階段接著做。照本檔決定做，不要改方案。

## 一、這個 App 是什麼

Windows 桌面程式，把兩個既有 Android App 的功能合在一起：

- **紀錄**：GPS Tracker 的網頁儀表板（看紀錄、分析、挑戰、3D 回放、紀錄管理）
- **模擬**：GPS 定位器的路線模擬（PC 沒有 GPS，紀錄由模擬產生）

PC 是**獨立裝置**：模擬產生的紀錄以裝置名稱 `PC` 上傳，**不與手機（POCO X6 Pro 5G、SONY XZ1）的紀錄合併**。

**多台電腦並存（2026-10-22 使用者需求）**：同一個程式會裝在桌機（PC）和筆電（NB）等多台電腦，各自是一台獨立裝置。
- 裝置名稱改成**可在設定畫面設定**（預設 `PC`），存在本機設定；上傳時用設定的名稱，不要寫死 `PC`
- 名稱不能空白；如果跟網站上「非模擬」的裝置（手機）同名要擋下；跟其他模擬裝置同名要警告（會合併成同一台）
- 網站判斷「模擬裝置」一律看紀錄的 `source: "simulated"`，**不要比對名稱是不是 `PC`**
- 兩台電腦同時上傳時，靠 manifest 寫入衝突（409／422）重讀合併重試，不能互相覆蓋
網站的挑戰本來就依裝置分開計算（`gpstracker-web/gt-core.js` 的 `filterByDevice`，沒有「全部裝置」選項）。

## 二、技術

- C# / .NET 9（本機已裝 SDK 9.0.317）、WPF
- WebView2（本機已裝 Runtime）：用 `SetVirtualHostNameToFolderMapping` 把網頁資料夾對應成本機網址，
  直接載入現有網頁，網頁的相對路徑不用改
- 網頁只維護一份：**不要複製一份網頁來改**。桌面版要藏的東西，用 WebView2 的
  `AddScriptToExecuteOnDocumentCreatedAsync` 注入 `window.GT_FEATURES = { photos: false, steps: false, share: false }`，
  網頁讀這個開關來隱藏；沒有這個開關時（手機、GitHub Pages）網頁維持原樣
- 單元測試：xUnit（或 MSTest），`dotnet test` 要能跑過
- **打包（2026-10-22 使用者需求：要拿到其他電腦測試）**：發佈版一律 **self-contained、win-x64**（`--self-contained true`），
  別台電腦不用先裝 .NET；網頁、simulator.html、MapLibre 都要包在輸出裡，整個資料夾（或 ZIP）複製過去就能執行。
  不要依賴本機路徑（例如 `D:\Claude\...`）；WebView2 Runtime 由 Windows 提供，沒有時要顯示清楚的提示與下載連結，不要直接當掉

## 三、相關位置

| 項目 | 路徑 |
|---|---|
| **桌面版所有檔案（本資料夾）** | `D:\Claude\GPS TRACKER PC\`（路徑有空格，指令裡要加引號） |
| 網頁儀表板（現有） | `D:\Claude\ANDROID-APP-DEV\gpstracker-web\` |
| GPS 定位器模擬引擎（純 Kotlin，要移植） | `D:\Claude\gps-locator-tw\src\app\src\main\java\com\gpsupdater\app\routes\` |
| GPS 定位器單元測試（移植時對照） | `D:\Claude\gps-locator-tw\src\app\src\test\java\com\gpsupdater\app\routes\` |
| Android GPS Tracker 的自動更新（參考做法） | `D:\Claude\ANDROID-APP-DEV\GPSTracker\app\src\main\kotlin\com\startinsnow\gpstracker\update\AppUpdater.kt` |

**檔案放置規則**
- PC 版的**所有檔案**（程式、測試、建置腳本、文件、打包輸出）一律放在 `D:\Claude\GPS TRACKER PC\`，不要放到其他地方
- 唯一例外：網頁只有一份，在 `gpstracker-web` 加功能開關（隱藏拍照／步數／分享）這類修改，要改在 `D:\Claude\ANDROID-APP-DEV\gpstracker-web\` 原檔；
  建置時再把 `gpstracker-web` 複製到 PC 版的輸出資料夾，**不要**在本資料夾維護一份網頁副本
- `gps-locator-tw`、`ANDROID-APP-DEV` 的其他檔案只讀不改

## 四、功能範圍

### 移除（桌面版隱藏）
- 拍照全部：攝影挑戰分頁、活動頁的「拍照點」、3D 回放／工作區的照片標記、紀錄管理的照片縮圖
- 步數全部：`steps-journey.html` 頁與導覽連結（`gt-nav.js`）、競賽中心的步數統計分頁、首頁／全挑戰進度／回顧的步數數字
- 分享圖片（`gt-share.js`）、分享連結
- 網址參數 `?repo=`、`?branch=`、`?data=`（改由桌面版設定畫面指定）

### 保留
- 首頁、歷史紀錄、活動分析、3D 旅程回放、紀錄管理
- 五大挑戰、全挑戰進度、回顧、競賽中心所有其他遊戲化挑戰

### 新增
1. **資料同步**：從 GitHub 把 `gpstracker-web/data/` 下載到 `%LOCALAPPDATA%`，沒網路也能看紀錄（底圖仍需網路）
2. **GitHub Token**：用 Windows DPAPI 加密保存，不要存明文
3. **GPX 匯出**：單筆＋批次（選資料夾一次存完），可選存檔位置
4. **GPX 匯入上傳**：多選、拖放、顯示進度、依序上傳。網頁已有單筆匯入（`records.js` 的 `importGpx`、`gt-gpx.js`），沿用它的格式與防重複規則
5. **自動更新**：（2026-10-22 使用者決定：**新開一個公開 GitHub repo `wahaha232/GPS-TRACKER-PC`**，放本資料夾的程式；repo 由使用者建立並 push，開發工具不要自己建 repo）GitHub Actions 放在本資料夾的 `.github/workflows/`，在 `windows-latest` 建置，發佈到 Release tag `gpstracker-desktop-latest`；
   建置時要從 `wahaha232/ANDROID-APP-DEV` 取出 `gpstracker-web` 一起打包（公開 repo，不需要 Token）。
   App 查最新版（`https://api.github.com/repos/wahaha232/GPS-TRACKER-PC/releases/tags/gpstracker-desktop-latest`，公開 repo 不用 Token）→ 比版本 → 下載（網址必須以 `https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/` 開頭）
   → 驗 SHA-256 → 舊 EXE 改名 `.old`、放新檔、重啟，下次啟動刪 `.old`
6. **模擬**（從 GPS 定位器移植成 C#，連同單元測試）：
   - `RouteSimulator.kt`：速度預設（走路 5／跑步 10／騎車 18／開車 40 km/h）、停留點、速度自然起伏
   - `GpxStreamImporter.kt`、`LocationDocumentCodec.kt`：GPX 依時間戳記播放、模擬 GPS 中斷
   - `RandomRouteGenerator.kt`、`GeoMath.kt`
   - 畫面：地圖點選加路線點、地點搜尋（Nominatim，每秒最多 1 次查詢，要帶 User-Agent）、收藏地點、隨機路線、暫停／繼續／1·2·4·8 倍速、進度列
   - 兩種產生方式：即時播放、直接產生
   - **不要搬**：`MockLocationService`、企業版、授權、團隊範本、雲端同步、無障礙設定
   - **省資源（2026-10-22 使用者需求：模擬時要降低 CPU／RAM）**：
     - 畫面更新頻率要低：播放計時器不要 100 ms 一次（目前 `SimulatorWindow` 是 100 ms），地圖上的位置每秒更新 2–4 次就夠；模擬計算照常用實際經過時間推進，結果不能因此改變
     - 軌跡線不要每個取樣點就整條重畫（目前 `simulator.js` 的 `appendPlaybackSample` 每點都 `setData` 整條）：同一次畫面更新的新取樣點合併成一則訊息送出，軌跡線最多每秒重畫一次；很長的軌跡，畫面上的舊段落可以抽稀顯示（存下來的取樣資料不能抽稀）
     - 視窗縮小或被遮住時暫停地圖更新（模擬照常進行），恢復時一次補畫；WebView2 縮小時把 `MemoryUsageTargetLevel` 設成 `Low`
     - 「直接產生」放到背景執行緒算，不要卡住畫面，也不要一點一點送到地圖
     - MapLibre 降低負擔：限制圖磚快取數量、關閉不需要的效果（例如 antialias、淡入動畫）、高解析度螢幕不要用超過 2 倍的像素比
     - 取樣資料不要重複複製好幾份（例如每次 `BuildResult` 都複製整個清單）
   - **模擬視窗的底圖切換（2026-10-22 使用者需求：不要一直載入衛星空拍照）**：
     - 底圖選項：①**簡易地圖（預設）**：OpenFreeMap 向量圖（`https://tiles.openfreemap.org/styles/positron`，免費、免金鑰，向量圖磚比衛星照片小很多）；
       ②衛星：Esri World Imagery（原本的）；③台灣電子地圖：國土測繪中心 NLSC 通用版電子地圖 WMTS（網址要實測確認）；④無底圖：只畫路線和軌跡，最省資源
     - 使用者的選擇要記住，下次開啟沿用；每種底圖右下角都要顯示圖資來源
     - **跟隨模式**：播放時地圖不要每次更新都移動鏡頭；預設只在位置快跑出畫面邊緣時才平移一次，可以關掉跟隨；倍速播放時不要自動放大到很細的層級
     - 只改模擬視窗；網站儀表板（gpstracker-web）的底圖不動
   - **大頭針與軌跡（2026-10-22 使用者需求）**：
     - 播放時用**大頭針圖示**標出目前位置，沿著模擬路線移動，走過的地方即時畫出軌跡線；GPS 中斷時大頭針照樣移動但軌跡斷開（跟真的失去訊號一樣）
     - 移動要看起來連續：省資源後畫面每秒只更新 2–4 次，大頭針在兩次更新之間用短動畫平滑移動（例如 CSS transition），不要一格一格跳
     - 軌跡分兩層畫：已確定的軌跡最多每秒重畫一次；從最後一個取樣點到大頭針的「尾巴」是一小段線，跟大頭針一起更新，所以軌跡永遠接在大頭針後面，不會落後
     - 跟 Android 版 GPS 定位器（`MainActivity.kt` 的 `followSimulation`、`appendSimulationTrail`）一致：使用者自己拖曳地圖就暫停跟隨，按「跟隨」按鈕再回到大頭針；播放結束後軌跡保留，下一次開始播放才清掉；位置沒動（暫停、停留點）時不重複加點。
       但**不要**照抄 Android 版「超過 5000 點就丟掉最舊的點」的做法，PC 版改用上面的「舊段落抽稀顯示」，整條軌跡都要看得到
     - **切換底圖時，大頭針、已畫的軌跡、路線、路線點都要保留**，播放中切換也不能中斷或清掉軌跡（MapLibre 換 style 會清掉自訂圖層，換完要立刻把資料加回去；大頭針用 Marker，不受換 style 影響）
7. **模擬結果轉成紀錄**：格式與 `gt-gpx.js` 的 `build()` 輸出相同，`deviceName: "PC"`，另加 `source: "simulated"`，上傳到 `data/<trackId>.json` 並更新 `manifest.json`（寫入衝突 409／422 要重讀合併重試）
8. **網站預設裝置**：`gt-core.js` 的 `chooseDevice` 沒指定裝置時取最新一筆紀錄的裝置，會被 PC 模擬紀錄搶走。**2026-10-22 使用者決定：預設排除模擬裝置**——沒指定裝置（網址沒有 `?device=`、localStorage 也沒有）時，只從「有非模擬紀錄」的裝置挑最新一筆（判斷依據是 `source: "simulated"`，不是名稱 `PC`，因為還會有 NB 等其他電腦）；全部都是模擬裝置時才退回最新一筆。使用者手動切換到 PC 照常可以看。要改 `gpstracker-web/gt-core.js` 並補網頁測試；網站只能本機 commit，不要 push

## 五、規則

- 回覆與註解用繁體中文；程式風格跟周圍程式碼一致
- 能測的邏輯要寫單元測試並實際跑過，測試沒過要照實記錄
- 改了 `gpstracker-web` 的 JS／CSS 後：
  - 重算快取雜湊：`python -I "D:\Claude\ANDROID-APP-DEV\GPSTracker\gpstracker-redesign\bust_hashes.py" "D:\Claude\ANDROID-APP-DEV\gpstracker-web"`（新增的 `<script>` 先帶 `?v=0`）
  - 跑網頁測試：`node --test` 後面**逐一列出** `gpstracker-web/tests/*.test.js` 檔案（Node 24 不能直接傳資料夾）
- 版本控制：`D:\Claude\GPS TRACKER PC\` 是獨立的本機 git repo，PC 版的變更 commit 在這裡；
  改到 `gpstracker-web` 的部分另外 commit 在 `D:\Claude\ANDROID-APP-DEV`。
  - **網站不能 push**：`ANDROID-APP-DEV` 只能本機 commit，**絕對不要 `git push`**（有 GitHub Pages，push 就會更新網站；要 push 先問使用者）
  - **桌面版程式可以 push**：`GPS TRACKER PC` 的 commit 可以 push（目前沒有 GitHub 遠端，沒有就不用 push）
- 不要把 GitHub Token 或任何金鑰寫進程式碼、設定檔或 commit
- 每次收工：更新 `DEV_PROGRESS.md` 的階段狀態，並在「每日紀錄」加一行（日期、工具、做了什麼、下一步）
