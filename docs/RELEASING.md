# xa sk5 發布與手動更新

發布庫：https://github.com/zzaiccf/xa-sk5 。介面只在使用者按「設定 → 檢查更新 → 檢查最新版本」時連線，按「升級」後才下載及安裝。沒有排程檢查或自動升級。

## 發布

Windows x64、PowerShell 7、.NET 10 SDK、Visual Studio C++ 工具與 Windows SDK。版本唯一來源為 `Directory.Build.props`，另更新 `release/notes-版本.txt`。全新產物目錄可避免沿用舊檔案。發布前需將官方 WinDivert 2.2.2 x64 的 WinDivert.dll／WinDivert64.sys 放入 third_party/WinDivert-2.2.2-A/x64；建置會重新核對驅動簽章，該二進位快取不納入原始碼。

```powershell
pwsh -File scripts/Build-Package.ps1 -PayloadDirectory artifacts/release-payload
pwsh -File scripts/Test-UpdateRelease.ps1 -OutputDirectory artifacts/update-validation-new
pwsh -File scripts/Publish-GitHubRelease.ps1              # 先建立草稿及上傳
pwsh -File scripts/Publish-GitHubRelease.ps1 -Publish     # 完成檢查後公開
```

`artifacts/release/版本/` 包含自帶 .NET 的 x64 可攜 ZIP、單檔安裝 EXE、原始碼 ZIP、`latest.json`、`SHA256SUMS.txt`。腳本驗證主要組件版本、每個檔案 SHA-256，以及最終安裝 EXE 的內嵌套件；來源 ZIP 與上傳檔案均使用允許清單。不包含使用者資料、代理設定、測試輸出或本機操作腳本。公開版本的檔案不可覆寫；修正須提升版本。

預設版本來源為 `https://github.com/zzaiccf/xa-sk5/releases/latest/download/latest.json`。JSON 的 portable.url 為同目錄版本 ZIP 檔名，包含 byte 大小與 SHA-256。自有站也可放同一組檔案，再於介面明確設定 HTTPS JSON 網址。重新導向限來源主機；GitHub 另外允許其發布資產主機。HTTP、含帳密網址及任意其他主機均拒絕。

目前 xa sk5 程式沒有發布者程式碼簽章；SHA-256 搭配可信任的 HTTPS 來源驗證下載完整性，不能取代發布者簽章。套件內 WinDivert 驅動另核對有效官方簽章。首次從 0.1.14 或更早版升級須使用 0.1.15 安裝包／完整 ZIP；舊版沒有檢查更新介面。

## 更新及回復

下載與準備可取消，尚未停止服務或替換程式。完整性／版本／路徑驗證全部通過後，介面將工作交給原有的獨立更新程式，確認就緒才退出。更新程式在原使用者權限重新開啟介面；只有更新標準已安裝服務的工作程序才要求 UAC。取消 UAC 時重新開啟原介面，服務未變更。

可攜版更新目前程式目錄，可選一併更新 `Program Files/WindowSocks` 的背景服務；已安裝版必須一起更新服務。自訂安裝服務路徑會拒絕，請人工維護。資料仍在原 `LocalAppData/WindowSocks` 與 `ProgramData/WindowSocks`，沿用原 DPAPI 範圍與 owner.sid；更新不匯入、不清空、不重新分配既有配置。設定備份受原 ACL 保護。

服務停止／程序退出後才檢查檔案占用、備份及替換，沒有強制結束任何遊戲、模擬器、遠控或其他 GUI。更新已安裝服務會中斷既有代理連線，使用者可能需要在遊戲中重新連線。新服務啟動後以具權限的控制管道驗證版本。

每個程式目錄的 `.xa-sk5-backup-隨機值/` 保存舊程式、完整性及 durable journal；失敗自動回復舊檔並移除新增檔案。已安裝服務另在 `ProgramData/WindowSocks/update-stage-隨機值/` 備份加密設定，更新紀錄不輸出帳密。備份會保留供人工確認，不會在未確認時刪除。下載、工作與結果位於 `LocalAppData/WindowSocks/Updates/隨機值/`。

程序被中止／斷電後，重新開啟可用的 xa sk5，展開「檢查更新 → 更新來源與離線更新 → 回復上次更新」。若舊版尚無此介面，解壓完整新版到另一資料夾，使用保留工作目錄中的 `helper/WindowSocks.Updater.exe --update 工作JSON`，將該 JSON 的 recover 改成 true、parentPid 改成 0；target 與 restartExe 保持原位置。已安裝服務需 UAC，會先停服務再回復。備份損毀、存取受限或磁碟寫入失敗時不宣稱回復成功；保留備份並修復權限／磁碟後重試。沒有強制卸載共用 WinDivert 驅動。

離線更新：將 `latest.json` 和指定的 ZIP 放在同一資料夾，按「從檔案檢查」，其餘完整性檢查與更新流程相同。
