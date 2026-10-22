# 本專案規則（GPS Tracker PC 版）

- **網站不能 push**：`D:\Claude\ANDROID-APP-DEV`（含 `gpstracker-web`）一律只能在本機 `git commit`，**絕對不要 `git push`**。
  - 它開了 GitHub Pages，push 上去網站就會直接更新。
  - 網站要 push 時，先停下來問使用者，由使用者自己決定。
- **桌面版程式可以 push**：`D:\Claude\GPS TRACKER PC` 的 commit 可以 push（目前還沒設定 GitHub 遠端，沒有遠端時不用 push）。
- **不要跳出測試視窗**：測試和驗證時不要啟動 App、WPF 視窗、WebView2 或瀏覽器視窗，也不要開任何會在桌面上跳出來的畫面。
  - 只用 `dotnet build`、`dotnet test`、`node --test` 這類不開視窗的方式驗證。
  - 畫面沒辦法用自動測試驗證時，照實寫「畫面未實際開啟驗證」，不要自己開視窗。
- 開工前先讀 `SCOPE.md` 和 `DEV_PROGRESS.md`，照規格做。
- 回覆和程式註解一律用繁體中文。
