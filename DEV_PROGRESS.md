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
| 2 | 從 GitHub 同步 data/ 到本機、GitHub Token 以 DPAPI 加密保存 | 3% | 未開始 |
| 3 | GPX 批次匯出、批次匯入（多選、拖放、進度） | 4% | 未開始 |
| 4 | App 自動更新＋GitHub Actions 建置發佈（Release tag gpstracker-desktop-latest） | 4% | 未開始 |
| 5 | 模擬引擎從 gps-locator-tw 移植成 C#（RouteSimulator、GpxStreamImporter、RandomRouteGenerator、GeoMath）＋單元測試 | 6% | 完成 |
| 6 | 路線編輯與播放畫面（地圖點選、地點搜尋、收藏、隨機路線、暫停／倍速／進度列） | 9% | 未開始 |
| 7 | 模擬結果轉成紀錄、以裝置「PC」上傳、標 source: "simulated"、處理網站預設裝置 | 4% | 未開始 |
| 8 | 整合測試、打包、修錯 | 6% | 未開始 |

第 1 階段完成後，依實際耗用重新估算剩餘時程。

## 每日紀錄

- 2026-10-22｜Kilo｜依指示先做第 5 階段：把 GeoMath、SimulationRoute／RouteSimulator、GpxStreamImporter、LocationDocumentCodec、RandomRouteGenerator、SampleRouteFactory、RouteJsonCodec（原在 RouteRepository.kt，改用 System.Text.Json）移植到 `GpsTrackerPc.Simulation`，57 個 xUnit 測試全部通過｜測試 57/57 通過｜下一步：第 1 階段 WPF + WebView2 外殼
- 2026-10-22｜Kilo｜完成第 1 階段：新增 `GpsTrackerPc.App`（WPF + WebView2），建置時從 `gpstracker-web` 複製網頁到輸出資料夾，注入 `GT_FEATURES` 關閉拍照／步數／分享；網頁原檔加入對應開關、隱藏步數環遊與分享按鈕、忽略桌面版網址參數；以 WebView2 實際載入驗證各頁開關生效（首頁／競賽中心／活動故事／紀錄管理／探索挑戰）｜C# 測試 57/57 通過；網頁測試 89/90，唯一失敗是同步遠端新資料後的既有資料檢查（`7bc9d72a…` 的 manifest 距離 2.553 km 與軌跡點路徑 3.000 km 不一致，與本次功能開關無關）｜下一步：第 2 階段 GitHub data/ 同步與 DPAPI Token

