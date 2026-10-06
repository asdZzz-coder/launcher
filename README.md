# APP 啟動器

從 GitHub Releases 下載、更新、開啟和刪除自己寫的 Windows 小程式。

- **卡片介面**：每個 APP 一張卡片，顯示圖示（還沒下載也會從 repo 抓圖示）、說明和狀態；上方可切換「全部／已安裝／可更新」。
- **淺色／深色**：右上角可選淺色、深色或跟著 Windows 的設定（預設），選擇會記住。
- **開啟／關閉**：APP 開著時「開啟」按鈕會變成「關閉」，卡片下方顯示「已開啟 v…」；從 APP 自己的視窗關掉也會自動變回「開啟」。按「關閉」時程式可以先問要不要儲存，5 秒內沒關掉才會問你要不要強制關閉。
- **自訂排列**：拖曳卡片調整前後位置（也可從「⋯」選單選「往前移／往後移」）；右上角可切換方格或條列。排列方式會記住，下次開啟一樣。
- **批次下載／刪除**：滑鼠移到卡片左上角勾選（或按「全選」），上方會出現「下載／更新」與「刪除」。
- **更新標示**：GitHub 有新版時，圖示右上角出現橘色箭頭，並顯示「可更新 vX.Y.Z」；頂端也會提示可更新的數量，可一鍵「全部更新」。
- **舊版照常可用**：更新會另外下載一個版本資料夾，舊版不會被刪掉；從版本選單選舊版就能開啟。不需要的版本可從「⋯」選單刪除。
- 各 APP 的使用者資料存在 `%AppData%`，新舊版本共用同一份資料，刪除版本也不會刪到資料。

## 安裝

執行 `AppLauncher-Setup.msi`。只裝給目前使用者，不需要系統管理員權限：

- 程式裝在 `%LocalAppData%\Programs\AppLauncher`，並建立桌面與開始功能表捷徑。
- 從「設定 → 應用程式」可以解除安裝。解除安裝不會刪除已下載的 APP（`%LocalAppData%\AppLauncher`），要的話請自己刪除這個資料夾。
- 安裝新版的 msi 會直接取代舊版。

## 使用

從桌面捷徑開啟「APP 啟動器」。啟動時以及每 30 分鐘會自動檢查更新，也可以按右上角的「檢查更新」。

下載的檔案放在 `%LocalAppData%\AppLauncher\apps\<id>\<版本>`，可按右上角的資料夾圖示開啟。

## 新增或修改 APP

APP 清單在 [AppLauncher/apps.json](AppLauncher/apps.json)。**改完推上 GitHub（main 分支）就生效**：
每個啟動器檢查更新時都會下載這個檔案，新加入的 APP 會自動出現並標示「新」，不用重新安裝啟動器。

```json
{
  "id": "carlog",                          // 資料夾名稱，只能用英數字與 -_
  "name": "車輛紀錄",                        // 顯示名稱
  "description": "記錄車子保養和加油，自動算出油耗。",
  "repo": "asdZzz-coder/car",              // GitHub 擁有者/repo（必須是公開 repo）
  "asset": "^CarLog-ClickOnce.*\\.zip$",   // Release 附件檔名的規則運算式
  "exe": "CarLog.exe",                     // 要啟動的 exe，會在解壓後的資料夾裡找
  "icon": "car/Assets/app.ico"             // repo 裡的圖示（.ico/.png），還沒下載前顯示用，可省略
}
```

- repo 必須是**公開**的。私有 repo 未登入時 GitHub 會回應「找不到」，卡片上會顯示「repo 可能是私有的」。

- 附件可以是 `.zip`（會解壓，ClickOnce 安裝包的 `.deploy` 檔會自動改回原檔名）或單一 `.exe`。
- 只想在某一台電腦用不同的清單：把整份清單另存到那台電腦的 `%LocalAppData%\AppLauncher\apps.json`，啟動器就只用它，不再讀 GitHub 上的清單。

## 發佈啟動器新版本

推送 `v` 開頭的標籤，GitHub Actions 會自動測試、產生安裝檔並發佈到 Releases：

```bash
git tag v1.4.1
git push origin v1.4.1
```

已安裝的啟動器下次檢查更新時，上方會出現「APP 啟動器有新版本」，按「更新啟動器」就會下載並安裝，裝好後自動重新開啟。

## 開發

需要 .NET 10 SDK。

```bash
dotnet build
dotnet test
```

產生安裝檔（會先發佈 AppLauncher，再包成 `Installer\bin\x64\Release\AppLauncher-Setup.msi`，內含 .NET 執行環境）：

```bash
dotnet build Installer -c Release
```

要改版本號：`dotnet build Installer -c Release -p:AppVersion=1.0.1`。

只要程式資料夾、不要安裝檔：

```bash
dotnet publish AppLauncher -c Release -o publish
```

不發佈成單一 exe 是因為開著「智慧型應用程式控制」的電腦會擋下沒有簽章的單一檔案 exe，資料夾形式則可以正常執行。

## 注意

- 沒登入的 GitHub API 每小時只能查 60 次。啟動器用 ETag 快取，內容沒變時不算次數，正常使用不會碰到上限；離線時會顯示上次查到的版本。
- 有些 APP（例如車輛紀錄、居服紀錄表）限制同時只能開一個視窗，這時新舊版本不能同時開著。
- APP 開著時無法刪除該版本，請先關閉程式。
