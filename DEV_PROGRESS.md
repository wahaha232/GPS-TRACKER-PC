# GPS Tracker 桌面版（PC 版）開發進度

- 開工日：2026-10-27（二），只在工作天開發，例假日與國定假日不做
- 每日用量上限：週額度的 2%（＝每日總額度的 10%）
- 預估：全案約耗週額度 40%（30–50%），約 20 個工作天，預計 2026-11-23 前後完成
- 範圍與設計決定：見本資料夾的 `SCOPE.md`（任何開發工具都照它做）
- 開發工具：10/27 前可先用 VS Code 的 Kilo Code（DeepSeek）開發；10/27 起由 Claude 排程接手。兩邊不要同時改，收工都要更新本檔
- 程式位置：PC 版所有檔案一律放在本資料夾（`D:\Claude\GPS TRACKER PC\`）

## 階段

| # | 內容 | 預估週額度 | 狀態 |
|---|---|---|---|
| 1 | WPF (.NET 9) + WebView2 外殼、載入本機 gpstracker-web、功能開關（藏拍照／步數／分享） | 4% | 完成 |
| 2 | 從 GitHub 同步 data/ 到本機、GitHub Token 以 DPAPI 加密保存 | 3% | 完成 |
| 3 | GPX 批次匯出、批次匯入（多選、拖放、進度） | 4% | 完成 |
| 4 | App 自動更新＋GitHub Actions 建置發佈（Release tag gpstracker-desktop-latest） | 4% | 完成 |
| 5 | 模擬引擎從 gps-locator-tw 移植成 C#（RouteSimulator、GpxStreamImporter、RandomRouteGenerator、GeoMath）＋單元測試 | 6% | 完成 |
| 6 | 路線編輯與播放畫面（地圖點選、地點搜尋、收藏、隨機路線、暫停／倍速／進度列） | 9% | 完成 |
| 7 | 模擬結果轉成紀錄、以裝置「PC」上傳、標 source: "simulated"、處理網站預設裝置 | 4% | 完成 |
| 8 | 整合測試、打包、修錯 | 6% | 完成 |

第 1 階段完成後，依實際耗用重新估算剩餘時程。

## 每日紀錄

- 2026-10-22｜Kilo｜依指示先做第 5 階段：把 GeoMath、SimulationRoute／RouteSimulator、GpxStreamImporter、LocationDocumentCodec、RandomRouteGenerator、SampleRouteFactory、RouteJsonCodec（原在 RouteRepository.kt，改用 System.Text.Json）移植到 `GpsTrackerPc.Simulation`，57 個 xUnit 測試全部通過｜測試 57/57 通過｜下一步：第 1 階段 WPF + WebView2 外殼
- 2026-10-22｜Kilo｜完成第 1 階段：新增 `GpsTrackerPc.App`（WPF + WebView2），建置時從 `gpstracker-web` 複製網頁到輸出資料夾，注入 `GT_FEATURES` 關閉拍照／步數／分享；網頁原檔加入對應開關、隱藏步數環遊與分享按鈕、忽略桌面版網址參數；以 WebView2 實際載入驗證各頁開關生效（首頁／競賽中心／活動故事／紀錄管理／探索挑戰）｜C# 測試 57/57 通過；網頁測試 89/90，唯一失敗是同步遠端新資料後的既有資料檢查（`7bc9d72a…` 的 manifest 距離 2.553 km 與軌跡點路徑 3.000 km 不一致，與本次功能開關無關）｜下一步：第 2 階段 GitHub data/ 同步與 DPAPI Token
- 2026-10-22｜Kilo｜完成第 2 階段：新增 `GpsTrackerPc.Core`（GitHub data/ 增量同步、ETag 條件式請求、原子寫檔、失敗保留舊檔；TokenStore + DPAPI CurrentUser）與 `GpsTrackerPc.Core.Tests`；WebView2 增加 `gpstracker-data.local` 虛擬主機並注入 `GPST.resolveConfig` 改寫，網頁 `data/` 直接讀 `%LOCALAPPDATA%\GpsTrackerPc\data`，未修改 gpstracker-web；主視窗加入同步狀態／立即同步，設定視窗可儲存／刪除 Token 且不顯示內容；實際啟動驗證首次同步 29 個檔案、第二次同步全部跳過、Token 檔案無明文｜測試 73/73 通過（Core 16、Simulation 57），`dotnet build` 0 警告 0 錯誤｜下一步：第 3 階段 GPX 批次匯出／匯入
- 2026-10-22｜Kilo｜完成第 3 階段：新增 `GpsTrackerPc.Core.Gpx`（GpxTrackBuilder 移植 gt-gpx.js、GpxExporter 採 records.js downloadGpx 格式、GitHubRecordUploader、GpxImportBatch）；App 新增 GPX 匯出視窗（勾選多筆、選資料夾、進度）與匯入視窗（多選／拖放、摘要、移動方式、公開倉庫警告、Token 檢查、依序上傳）；所有 GitHub 寫入測試都用假 HttpMessageHandler，沒有實際上傳｜測試 96/96 通過（Core 39、Simulation 57），`dotnet build` 0 警告 0 錯誤｜下一步：第 4 階段自動更新（開工前先確認 GitHub Actions／Release 要放在哪個 repo）
- 2026-10-22｜Kilo｜完成第 6 階段 A：新增 SimulatorWindow（MapLibre 衛星底圖、C#↔地圖 postMessage、點選／拖曳／刪除／排序／停留、速度、循環／自然起伏／GPX 時間、GPS 中斷、Nominatim 搜尋＋每秒一次限制、收藏、隨機路線、GPX／KML 匯入匯出、RouteJsonCodec 多路線存檔）與 SavedRoutesWindow；實際開過模擬視窗、地圖載入、新增 2 點、儲存路線、收藏、已存路線視窗、30 點隨機路線與新增中斷｜測試 122/122 通過（Core 65、Simulation 57），`dotnet build` 0 警告 0 錯誤｜下一步：第 6 階段 B 播放畫面（暫停／倍速／進度列）
- 2026-10-22｜Kilo｜完成第 6 階段 B：新增可注入時間的 `PlaybackController`（即時播放、暫停／繼續／停止、1/2/4/8 倍速、每秒取樣、中斷不取樣、直接產生、循環圈數）與 `PlaybackSample`／`PlaybackResult`；SimulatorWindow 加入播放控制、進度／時間／速度／距離、GPS 中斷提示；地圖加入播放標記、走過軌跡與中斷樣式；結果只存在記憶體，沒有上傳或轉成網站格式。實際開過播放視窗：開始／暫停／繼續／停止、中斷提示、直接產生 15 個取樣點與地圖軌跡｜測試 130/130 通過（Core 73、Simulation 57），`dotnet build` 0 警告 0 錯誤｜下一步：第 7 階段模擬結果轉成紀錄（先確認網站預設裝置要不要排除 PC）
- 2026-10-22｜Kilo｜完成第 7 階段：新增 `SimulatedRecordBuilder`（取樣→GpxTrackBuilder；排除中斷點、deviceName PC、source simulated、可選開始時間）與 `SimulatedUploadWindow`（摘要／時間／公開倉庫警告，沿用 GitHubRecordUploader）；網站 `chooseDevice` 改成沒指定時排除 PC、只有 PC 才退回 PC，新增 `tests/device.test.js` 並重算雜湊。實際開過上傳對話框，確認摘要（日期、距離、時間、點數、PC、simulated）與開始時間；沒有按下確認上傳｜PC 測試 135/135 通過（Core 78、Simulation 57），`dotnet build` 0 警告 0 錯誤；網站測試 94/95 通過，唯一失敗是既有 `實際軌跡檔：分段與移動時間合理` 資料問題｜下一步：第 8 階段整合打包
- 2026-10-22｜Kilo｜完成第 4 階段：新增 `UpdateVersion`／`UpdateService`／`UpdateInstaller`（版本比較、GitHub Release 查詢、下載網址白名單、SHA-256、EXE 替換與失敗還原、啟動清理 .old）；App 啟動檢查一次、設定畫面「檢查更新」、下載後詢問才重啟；新增 `.github/workflows/desktop-release.yml`（windows-latest、測試、單一 EXE、SHA-256、Release tag `gpstracker-desktop-latest`）與 csproj Version 0.1.0；本機 `dotnet publish` 已產出單一 EXE（含網頁／模擬資源），但沒有啟動 App｜測試 152/152 通過（Core 95、Simulation 57），`dotnet build` 0 警告 0 錯誤｜下一步：第 8 階段整合打包（workflow 尚未在 GitHub Actions 實際跑過）
- 2026-10-22｜Kilo｜完成第 8 階段：新增端到端整合測試（路線→播放→PC 紀錄→上傳、GPX 匯入上傳、GPX 匯出再匯入、資料同步保留舊檔、更新檢查下載替換與失敗還原）；WebView2 Runtime 缺少時顯示下載連結；排除打包中的 `web/tests`；新增 README；self-contained win-x64 資料夾發佈到 `publish\`（143.64 MB，531 個檔案），並壓成 `GpsTrackerPc-win-x64.zip`（62.81 MB）；未執行 EXE｜PC 測試 157/157 通過（Core 100、Simulation 57），`dotnet build` 0 警告 0 錯誤；網站測試 94/95 通過，唯一失敗仍是既有資料問題｜下一步：交付測試（GitHub Actions 與實機 WebView2／更新流程仍需使用者驗證）
- 2026-10-22｜Kilo｜多台電腦並存：新增裝置名稱設定（`DeviceNameStore`／`DeviceNamePolicy`／`ManifestDeviceReader`），設定畫面可改名稱（預設 PC，擋手機同名、警告模擬同名），模擬與 GPX 上傳都使用設定名稱；網站 `chooseDevice` 改以 `source === "simulated"` 判斷模擬裝置，補 PC／NB 測試並重算雜湊；重新產出 `publish\`（143.65 MB）與 ZIP（62.81 MB），沒有執行 EXE｜PC 測試 168/168 通過（Core 111、Simulation 57），`dotnet build` 0 警告 0 錯誤；網站測試 96/97 通過（1 個既有資料失敗）｜下一步：交付測試
- 2026-10-22｜Kilo｜省資源：播放計時器改 500 ms、同一次更新合併取樣、地圖軌跡每秒最多 setData 一次、顯示軌跡上限 1200 點（抽稀只影響畫面，PlaybackController 取樣不變）、視窗失焦／縮小暫停地圖更新並恢復補畫、縮小設定 WebView2 MemoryUsageTargetLevel Low、直接產生在 Task.Run 完成後一次畫上、MapLibre 關閉 antialias／淡入並限制 tile cache／pixelRatio、BuildResult 改用唯讀檢視不再複製整份清單；publish 143.66 MB、ZIP 62.82 MB，沒有執行 EXE，也沒有實際量測 CPU／RAM｜PC 測試 176/176 通過（Core 119、Simulation 57），`dotnet build` 0 警告 0 錯誤；網站測試 96/97 通過（1 個既有資料失敗）｜下一步：交付測試

