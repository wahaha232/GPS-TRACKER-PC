# GPS Tracker 桌面版（PC 版）開發規格

> 給任何 AI 開發工具（Kilo Code、Claude Code…）讀的規格。開工前先讀本檔和 `DEV_PROGRESS.md`，
> 從 `DEV_PROGRESS.md` 第一個「未開始／進行中」的階段接著做。照本檔決定做，不要改方案。

## 一、這個 App 是什麼

Windows 桌面程式，把兩個既有 Android App 的功能合在一起：

- **紀錄**：GPS Tracker 的網頁儀表板（看紀錄、分析、挑戰、3D 回放、紀錄管理）
- **模擬**：GPS 定位器的路線模擬（PC 沒有 GPS，紀錄由模擬產生）

PC 是**獨立裝置**：模擬產生的紀錄以裝置名稱 `PC` 上傳，**不與手機（POCO X6 Pro 5G、SONY XZ1）的紀錄合併**。
網站的挑戰本來就依裝置分開計算（`gpstracker-web/gt-core.js` 的 `filterByDevice`，沒有「全部裝置」選項）。

## 二、技術

- C# / .NET 9（本機已裝 SDK 9.0.317）、WPF
- WebView2（本機已裝 Runtime）：用 `SetVirtualHostNameToFolderMapping` 把網頁資料夾對應成本機網址，
  直接載入現有網頁，網頁的相對路徑不用改
- 網頁只維護一份：**不要複製一份網頁來改**。桌面版要藏的東西，用 WebView2 的
  `AddScriptToExecuteOnDocumentCreatedAsync` 注入 `window.GT_FEATURES = { photos: false, steps: false, share: false }`，
  網頁讀這個開關來隱藏；沒有這個開關時（手機、GitHub Pages）網頁維持原樣
- 單元測試：xUnit（或 MSTest），`dotnet test` 要能跑過

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
5. **自動更新**：（⚠️ PC 版不在 `ANDROID-APP-DEV` repo 裡，GitHub Actions 要放在哪個 GitHub repo 使用者還沒決定——做到第 4 階段時先問使用者，App 端的檢查／下載／替換邏輯可以先做）GitHub Actions 在 `windows-latest` 建置，發佈到 Release tag `gpstracker-desktop-latest`（不要動 APK 用的 `gpstracker-latest`）；
   App 查最新版 → 比版本 → 下載（網址必須以 `https://github.com/wahaha232/ANDROID-APP-DEV/releases/download/` 開頭）
   → 驗 SHA-256 → 舊 EXE 改名 `.old`、放新檔、重啟，下次啟動刪 `.old`
6. **模擬**（從 GPS 定位器移植成 C#，連同單元測試）：
   - `RouteSimulator.kt`：速度預設（走路 5／跑步 10／騎車 18／開車 40 km/h）、停留點、速度自然起伏
   - `GpxStreamImporter.kt`、`LocationDocumentCodec.kt`：GPX 依時間戳記播放、模擬 GPS 中斷
   - `RandomRouteGenerator.kt`、`GeoMath.kt`
   - 畫面：地圖點選加路線點、地點搜尋（Nominatim，每秒最多 1 次查詢，要帶 User-Agent）、收藏地點、隨機路線、暫停／繼續／1·2·4·8 倍速、進度列
   - 兩種產生方式：即時播放、直接產生
   - **不要搬**：`MockLocationService`、企業版、授權、團隊範本、雲端同步、無障礙設定
7. **模擬結果轉成紀錄**：格式與 `gt-gpx.js` 的 `build()` 輸出相同，`deviceName: "PC"`，另加 `source: "simulated"`，上傳到 `data/<trackId>.json` 並更新 `manifest.json`（寫入衝突 409／422 要重讀合併重試）
8. **網站預設裝置**：`gt-core.js` 的 `chooseDevice` 沒指定裝置時取最新一筆紀錄的裝置，會被 PC 模擬紀錄搶走。**要不要改成預設排除 `PC`，使用者還沒決定——做到第 7 階段時先問使用者，不要自己改**

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
