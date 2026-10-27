# GPS Tracker 桌面版（PC 版）開發規格

> 給任何 AI 開發工具（Kilo Code、Claude Code…）讀的規格。開工前先讀本檔和 `DEV_PROGRESS.md`，
> 從 `DEV_PROGRESS.md` 第一個「未開始／進行中」的階段接著做。照本檔決定做，不要改方案。

## 一、這個 App 是什麼

Windows 桌面程式，把兩個既有 Android App 的功能合在一起：

- **紀錄**：GPS Tracker 的網頁儀表板（看紀錄、分析、挑戰、3D 回放、紀錄管理）
- **模擬**：GPS 定位器的路線模擬（PC 沒有 GPS，紀錄由模擬產生）

PC 是**獨立裝置**：模擬產生的紀錄以裝置名稱 `PC` 上傳；預設使用自己的名稱，但**可以沿用網站上已存在的名稱**（含手機等非模擬紀錄），此時模擬紀錄會歸在同一個裝置名稱下，繼承既有紀錄。

**多台電腦並存（2026-10-22 使用者需求）**：同一個程式會裝在桌機（PC）和筆電（NB）等多台電腦，各自是一台獨立裝置。
- 裝置名稱改成**可在設定畫面設定**（預設 `PC`），存在本機設定；上傳時用設定的名稱，不要寫死 `PC`
- 名稱不能空白；**沿用自己目前的名稱一律允許**（即使網站上同名紀錄是非模擬來源，例如舊版上傳）。設定成網站上已存在的其他名稱時只警告「會合併成同一台裝置」，不再阻擋；版本更新不可要求使用者換名稱
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
   - **雲端更新與版本號（2026-10-22 使用者需求）**：
     - 版本顯示：主視窗標題列和設定畫面都顯示目前版本（例如「GPS Tracker PC v0.2.0」）；設定畫面另外顯示「最後檢查更新時間」
     - 雲端更新：主視窗選單加「檢查雲端更新」；有新版時顯示「目前版本 → 新版本」和更新說明（Release 的說明文字），使用者按「更新」才下載安裝；已是最新版要明確顯示「已是最新版本」；沒網路或 GitHub 連不上要顯示原因，不能當掉
     - 啟動時自動檢查一次（一天最多一次）；可設定背景下載、下載後自動安裝，或只提示；自動檢查可關掉
     - **背景下載與重啟（2026-10-24 使用者需求）**：設定可開關「背景下載更新檔」；下載完成後可選「自動安裝」或「使用者手動確認安裝」。安裝只替換 EXE，工具列要顯示「請重啟程式」訊息與「重啟程式」按鈕；重啟前先暫停模擬路線並存檔，重啟後詢問「從暫停點繼續／存檔並結束／不存檔放棄」
     - Release 同時有 EXE 與 ZIP 時，**SHA-256 必須挑同檔名的檢查碼**，不能拿第一個 `.sha256`；ZIP 檢查碼用 `.sha256.txt`，避免舊版用戶端誤抓
     - **版本號規則**：用 `主版本.次版本.修訂`（例如 0.2.0），**每次發佈都必須進版**，版本號只在 `GpsTrackerPc.App.csproj` 的 `<Version>` 一個地方設定，程式和打包都從這裡讀：
       修 bug → 修訂 +1（0.2.0 → 0.2.1）；加功能 → 次版本 +1、修訂歸 0（0.2.1 → 0.3.0）；測試完成、正式給多台電腦使用 → 1.0.0
     - 每次進版要在本資料夾的 `CHANGELOG.md` 加一段（版本、日期、改了什麼，繁體中文），Release 的更新說明就用這一段
     - GitHub Actions 發佈前要檢查：新版本號必須**大於**目前 Release 上的版本，沒進版就停止發佈並顯示錯誤；`CHANGELOG.md` 沒有這個版本的段落也停止
     - App 端比版本時只接受比目前版本**新**的版本，不能被舊版或同版覆蓋（降版保護）
6. **模擬**（從 GPS 定位器移植成 C#，連同單元測試）：
   - `RouteSimulator.kt`：速度預設（走路 5／跑步 10／騎車 18／開車 40 km/h）、停留點、速度自然起伏
   - **循環與速度（2026-10-27 使用者需求）**：
     - 沒勾循環：走到終點就停在終點，不回起點，也不畫終點連回起點的線
     - 勾循環：到終點後沿原路折返回起點（起點→終點→起點算 1 次），不能從終點直線連回起點；次數可設定（1–10000）
     - 隨機「環狀」路線不自動勾循環
     - 移動方式速度按鈕（走路／跑步／騎車／開車）的數值可由使用者自訂並存在本機，可恢復預設
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
9. **好友 & 群組（2026-10-24 使用者需求）**：主視窗新增「好友」獨立分頁，內含好友／群組清單、成員線上狀態、目前位置地圖與互相分享位置。
   - 邀請碼 `GTF1-...` 加入好友或群組；邀請碼只包含分享連線資料與金鑰，不含 GitHub Token
   - 位置以 AES-256-GCM 端對端加密後經 `ntfy.sh` 中繼（免註冊；主題與金鑰由邀請碼產生）；中繼服務看不到座標與名稱
   - 分享開關存在本機 `social.json`；開啟時每 30 秒以 Windows 定位服務更新，停止分享時送出停止通知；線上判定為 2 分鐘內
   - 好友地圖頁面放在 `src/GpsTrackerPc.App/Friends/`，使用 OpenFreeMap 向量底圖；不修改 `gpstracker-web`，因此不影響公開網站
   - 清單與地圖只顯示這台電腦加入的連線；不用 GitHub Token、不寫入公開倉庫
10. **待上傳紀錄與網路中斷（2026-10-24 使用者需求）**：
   - 模擬紀錄上傳失敗（或重啟時選擇「存檔並結束」）先存到 `%LOCALAPPDATA%\GpsTrackerPc\pending-records\`；每 5 分鐘比對網站 manifest 並自動補傳，成功或網站已有相同紀錄就刪除本機檔
   - 網路中斷時自動暫停模擬播放並停止背景補傳；恢復連線後自動繼續模擬、補傳並同步紀錄。使用者手動暫停／繼續不受影響
   - 設定可關閉自動補傳；可調每日自動補傳上限（預設 20 筆）；開啟前要有公開上傳的隱私提示
  - 主視窗「待上傳」可看清單、失敗次數與原因，並手動立即補傳（跳過上限）或刪除；本機最多保留 200 筆
11. **更新與連線強化（2026-10-24 使用者需求）**：
   - 背景下載支援暫停／續傳（`.part` + HTTP Range）、速度顯示、非計量網路限制、下載空間檢查、失敗退避重試（30 秒／2 分／10 分，最多 3 次）與系統匣通知
   - 設定可選穩定版／測試版通道；Release tag 分別為 `gpstracker-desktop-latest` 與 `gpstracker-desktop-beta`（beta 以 prerelease 發佈）
   - 安裝前檢查 EXE 數位簽章（未簽章警告、鏈結失敗阻擋）；`.old` 保留 7 天，設定提供「回復上一個版本」；啟動成功後清理已安裝或更舊的 updates 資料夾
   - 重啟續傳要保留未儲存路線草稿、選取路線點、圈數與倍速；重啟詢問可記住選擇並可重設
   - 網路中斷時主視窗顯示離線橫幅、播放面板標記中斷區段；恢復後自動繼續
   - 程式單一實例保護；更新重啟時以 `--restarted-for-update` 等待舊行程結束

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
