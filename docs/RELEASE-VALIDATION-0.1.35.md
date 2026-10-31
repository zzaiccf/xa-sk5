# xa sk5 0.1.35 — B 段修正與驗證

日期：2026-10-31（台北）。基底 `d833482856d11456f2e5dfe1709b6655e0b12e93`；分支 `codex/sandbox-fixes-0.1.35`。沿用先前 A 段工作樹，切換基底沒有衝突。版本為 0.1.35，包含 0.1.33 移除與 0.1.34 TCP 修正。發生問題的另一台電腦是 GitHub 正式版 0.1.32，該機日誌尚未帶回。

本輪只實作與測試，沒有操作已安裝服務、Sandboxie 設定、遊戲、代理線路或防火牆。測試僅用暫存檔、替身外部工具／IPC、合成程序及既有回送 socket 測試；原生檢查未開 capture handle。沒有發布、push、tag、草稿或安裝包建置。C 段仍須等待使用者說「繼續」。

## 問題、修正與證據

| 問題 | 根因及本輪修正 | 紅→綠證據 |
|---|---|---|
| 1 缺少 ini | silent 安裝未開 SandMan，設定可能尚未首次寫入；原 Configure 在官方工具執行前就中止。改為缺檔備份註記、官方寫入後重新查路徑並核驗。Inspect 同時檢查 SbieSvc／設定；待初始化可以準備，不重新安裝。 | `config-red.trx`：MissingIniIsCreatedByOfficialCommandBoundaryAndVerified 因「找不到設定，無法備份」失敗。修正後同一產品 Configure 由替身 CLI 建立暫存 ini、重新解析成另一位置、核對內容，再保存不重寫未變更鍵。 |
| 2 分享衝突 | 舊 async reader 使用 Share.Read，會阻擋 writer。共用 reader 改 ReadWrite\|Delete，短暫開啟並在關檔後取得第二份穩定樣本；Prepare／Remove 共用閘，保留 transaction 與回復。只有分享衝突／暫時不一致有限重試，100/200/400 ms；不改通知。 | `config-red.trx`：SharedReaderDoesNotDenyConcurrentOfficialWriterOrRename 用真實暫存檔開 reader 後開 writer 失敗；綠燈可同時寫入及 rename。補驗有限重試、權限／路徑直接失敗、準備／移除互斥、resident 舊值、append 中斷後整鍵重試不重複、其他 section 保留。 |
| 3 右鍵啟動範圍 | 原 Single 選取、右鍵重設單列、同遊戲全組擴大範圍。改 Extended、多選刷新、右鍵範圍內保留／範圍外單選、單／多／混選選單；編輯／移除限一列；依當時表格順序啟動。工具列改名、全部啟動行為保留。 | `sandbox-ui-red-recorded/failure.txt`：要求 Extended 失敗。最終 `--sandbox-ui` 驗證實際 WPF 控制項及處理器、單／多／混選、順序、重複點擊及略過結果。 |
| 4 啟動後誤判失敗 | helper 的監看和啟動共用 catch，成功後的暫時例外會回報 false；服務未鎖定成功代次。桌面取消交接也會無條件 pause／terminate。分離監看重試，服務以已核驗 PID＋birth 拒收成功後 false；交接後服務持續核驗，不由進度視窗取消。 | `launch-wired-red.trx`：監看 false 回報與服務接受晚到失敗各 1 FAIL。`sandbox-handoff-red-runtime/failure.txt`：真實 Backend 在 helper 替身交接後取消，錯誤 pause／terminate。綠燈另驗先 A 再 A/B、連點、交接前後取消；真正啟動失敗仍 pause，完整原因進服務日誌與匯出。 |
| 5 啟動器語意 | 產品只 Process.Start 設定的 LauncherPath；YongOrigin 是副本 Launcher.exe，仍需使用者開始遊戲／登入。舊 desktop 還以 Detail 前綴判成功。新增 nullable LauncherStarted 與 typed queue result，改用結構化狀態。 | `launch-wired-red.trx`：CompletedLaunchDescribesLauncherAndManualLogin 文案失敗。綠燈驗證不依文案、Started／AlreadyRunning 分開；舊服務缺欄位時以已核驗遊戲成員及出口資訊判定，PID 重用／helper 本身不能算成功。 |

紅燈的 Configure／bootstrap 外部 IO 先抽成與舊行為相同的可注入邊界，再執行失敗測試；沒有把正式服務、遊戲或 Sandboxie 工具當測試替身。`launch-wired-red.trx` 在 Tools 已使用同一 lifecycle 邊界後再次確認 3 個失敗。新增保護／相容性測試不全是「舊版應失敗」：線路封鎖、舊 wire contract、回復及不改其他 section 是回歸斷言，不能把本來通過的行為宣稱為修復。

收尾另外補兩個缺檔移除反例（Disable／Remove）：磁碟沒有 section、resident 仍有盒時原本直接返回，`missing-resident-red.trx` 為 2 FAIL；修正後先查 resident，兩者不一致即保留資料並停止，不自動刪除。另以 `nested-retry-red.trx` 檢查巢狀邊界不能把一次已耗盡的分享衝突重試放大成 16 次；耗盡後改為不可再重試的明確 IOException。

### 相容性、診斷及原始碼補充

- `SandboxLaunchReport` 原三欄保留，ErrorDetail／ErrorFile 是可缺省屬性。真實新 Controller 收舊三欄訊息的測試通過；新 helper 的成功／失敗訊息用凍結的舊服務 record 與相同 JSON 選項反序列化通過。這是協定測試，不代表已執行兩版服務的實機互換。
- 顯示原因最多 300 字；詳細例外與 stack 上限 16,000 字，遮蔽認證樣式及服務已知代理認證，寫入服務日誌與 `DiagnosticBundle.SandboxLaunchErrors`。保留 0.1.34 RelayPerformance 診斷欄位。成功後只拒絕同一已核驗 helper 的倒退回報，不放寬身分、出口、線路或 WFP 判斷。
- 準備紀錄 `ConfigBackups/commands-*.jsonl` 含每次 SbieIni／Start 的開始、結束、操作、鍵名、退出碼、stderr 摘要；不記錄參數值。所有設定寫入仍經官方 SbieIni；單一 section API 只保留為 [後續提案](SANDBOX-FIXES-0.1.35-A.md)。程式內串行閘不能約束 SandMan 或其他程序。
- 路徑以驅動 resident IniLocation 為準，支援 H／W／DOS 及 `\??\` 格式的登錄自訂路徑；無法轉換的原生 `\Device\` 路徑會明確停止，不猜另一個 ini。路由服務仍透過隔離 broker，不新增主服務載入 SbieDll；介面以獨立 Tools 查詢。`SbieApi_QueryConfAsIs` 是巨集，不是 export；實際呼叫 `SbieApi_QueryConf`＋CONF_GET_NO_EXPAND（0x20000000）。[官方宣告](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/dll/sbieapi.h)、[旗標](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/drv/api_flags.h)。
- SbieIni 1.18.8 對部分寫入錯誤仍 exit 0，且不輸出 NTSTATUS；因此「退出碼 0、stderr 空白」不能區分被吞掉的權限錯誤與暫時不一致。程式以讀回不一致有限重試後清楚提示檢查權限／路徑，不能宣稱已查明實際原因。能直接辨識的 UnauthorizedAccess／無效路徑不重試。[官方 update.c](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/apps/ini/update.c)。

## 修改檔案

- 版本：`Directory.Build.props`；正式說明：本文件、`SANDBOX-FIXES-0.1.35-A.md`、`SANDBOX-GUIDE.md`、`release/notes-0.1.35.txt`。
- 設定：Core 的 `SandboxieInstaller.cs`、`SandboxServiceState.cs`、`SandboxieRuntime.cs`、`SandboxConfiguration.cs`、`SandboxIniFile.cs`、`SandboxCommandTimeline.cs`、`SandboxPreparation.cs`、`SandboxRemoval.cs`。
- 啟動／診斷：Core 的 `SandboxBootstrapSession.cs`、`SandboxLaunchErrors.cs`、`SandboxLauncherState.cs`、`SandboxLaunchQueue.cs`、`SandboxInstances.cs`、`NetworkDiagnostics.cs`；Tools 的 `Program.cs`、`SandboxBootstrap.cs`；Service 的 `Controller.cs`、`SandboxController.cs`。
- 介面：Desktop 的 `MainWindow.xaml`、`MainWindow.xaml.cs`、`MainWindow.Sandbox.cs`、`SandboxBackend.cs`、`SandboxLaunchWindow.xaml.cs`。
- 測試：`SandboxConfigurationFixTests.cs`、`SandboxLaunchFixTests.cs`、`SecondReviewSandboxTests.cs`；WPF 的 `Program.cs`、`SandboxUi.cs`、`SandboxLaunchHandoff.cs`。WPF harness 額外保存 checks.json／failure.txt，避免 GUI 執行模式無 console 證據。

## 命令與結果

以下路徑相對於本工作樹；原始輸出在忽略的 artifacts，未提交。**B 段完成，全部最終檢查通過；C 未執行。**

```powershell
& scripts/Build-Native.ps1
dotnet test tests/WindowSocks.Tests -c Release --filter FullyQualifiedName~SandboxConfigurationFixTests --logger 'trx;LogFileName=config-red.trx' --results-directory artifacts/test-results
dotnet test tests/WindowSocks.Tests -c Release --filter 'FullyQualifiedName~SandboxLaunchFixTests|FullyQualifiedName~SandboxFix_NewService' --logger 'trx;LogFileName=launch-wired-red.trx' --results-directory artifacts/test-results
dotnet test tests/WindowSocks.Tests -c Release --logger 'trx;LogFileName=core-tests-0.1.35-complete.trx' --results-directory artifacts/test-results
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PacketPathPerformance.ps1 -OutputDirectory artifacts/packet-path-0.1.35
$env:XA_TEST_OUTPUT = Join-Path (Get-Location) 'artifacts/sandbox-ui-0.1.35-complete'
dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release -- --sandbox-ui
$env:XA_TEST_OUTPUT = Join-Path (Get-Location) 'artifacts/sandbox-storage-0.1.35-complete'
dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release --no-build -- --sandbox-storage
dotnet build src/WindowSocks.Tools -c Release
git diff --check
```

| 檢查 | 結果 | 證據 |
|---|---|---|
| 完整 dotnet test | PASS，500／500，0 FAIL、0 skip，4 分 15 秒 | `artifacts-core-0.1.35-complete.log`、`artifacts/test-results/core-tests-0.1.35-complete.trx` |
| 原生 PacketPathChecks | PASS，157,646 assertions，未開 capture handle | `artifacts-native-final.log`、`artifacts/packet-path-0.1.35/result.txt` |
| WPF --sandbox-ui | PASS，59 checks | `artifacts-ui-0.1.35-complete.log`、`artifacts/sandbox-ui-0.1.35-complete/checks.json` |
| WPF --sandbox-storage | PASS，15 checks | `artifacts-storage-0.1.35-complete.log`、`artifacts/sandbox-storage-0.1.35-complete/checks.json` |
| Tools 編譯 | PASS，0 warning／0 error | `artifacts-tools-build-complete.log` |
| 既有交付保留 | PASS，0.1.32／0.1.33／0.1.34 的 EXE、ZIP、latest.json 共 9 檔均與各自 SHA256SUMS 相符 | `artifacts/preserved-release-hashes.json` |

測試過程的 harness 修正：首次 handoff 測試因 Diagnostics 建構參數缺一項未編譯，不能算紅燈；補正後 `sandbox-handoff-red-runtime` 才是有效重現。WPF PreviewMouseRightButtonDown 是由 WPF 滑鼠管線重送的 direct event，直接在 row RaiseEvent 不會冒泡到 Grid；改為給實際 handler 已實現的 row 事件參數。這輪沒有做實體滑鼠／鍵盤手動驗收。

## 排除與未驗證項目

本機 commit 只允許上列產品、測試及去識別化文件；排除 artifacts/、reviews/、YongOrigin/、experiments/、第三方二進位快取、原始診斷、帳密、實際代理 IP、個人路徑與盒名。既有其他工作樹保留。本輪不建置 Setup／ZIP；0.1.35 的安裝包大小、SHA-256 與 verify-release 留待 C。

提交白名單 34 檔；`git diff --cached --no-renames --name-only` 與白名單差異 0、禁止路徑 0、已知私密樣式 0，`git diff --cached --check` 通過。LF／CRLF 提示是既有 Git 換行政策。原生產品來源相對基底差異為 0，沒有覆蓋 0.1.34 TCP 修正。完整白名單、紅→綠索引、來源雜湊及提交 SHA 保存在本機 `artifacts/b-stage-0.1.35/`，不提交原始測試輸出。

**NOT RUN**：可還原 VMware 快照上的乾淨機安裝→準備→啟動；真實遊戲多開；0.1.33 移除實機；0.1.31 C1／C2／C4／C6；C5 外側 FIN／RST 擷取。SBIE1402 的實際持有者、另一台 A 首次 InvalidOperationException 觸發原因及雙窗卡頓改善，均尚未由實機證明。收到該機日誌後再分析，必要追加修正在 C 前處理。

請帶回：錯誤後的匯出診斷、service-session.log（及 previous）、該盒 sandbox-bootstrap-error.txt／sandbox-monitor-error.txt、準備 commands-*.jsonl、安裝 setup.log 及錯誤時間。實際完整路徑查詢與去識別化方式見 [A 文件](SANDBOX-FIXES-0.1.35-A.md)；未知遠端使用者目錄／實例 ID 時不虛構完整路徑，也不索取帳密。
