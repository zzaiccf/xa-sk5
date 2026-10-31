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

## C 段：打包前修正與測試交付

本節接續已接受的 B 段；上方「C 未執行」及「不建置」是 B 結束時的歷史狀態。本次授權只到本機測試打包，不發布、不 push、不 tag、不建草稿，不操作已安裝服務、Sandboxie、遊戲、線路或防火牆。

### C0 根因、修正與紅 → 綠

| 項目 | 根因及最終行為 | 失敗重現與綠燈證據 |
|---|---|---|
| C0-1 arm | ServiceRuntime 曾直接呼叫靜態 ValidatePersistedSettings，繞過常駐 broker，啟動路徑 helper；Controller 外層只有 1 秒。現改傳 broker `validate`，由已載入 SbieDll 的 broker 解析路徑／讀設定。第一個 broker 回覆預算 20 秒、後續 validate 10 秒，arm 外層 30 秒；仍在 Gate 外等待，失敗不發 ticket。其他請求正常 5 秒，terminate 保留 65 秒。 | `c0-red.trx` 的 C01_Arm…：假核驗延遲 1.3 秒，舊程式 Timeout，未到後續身分檢查。`c0-final-green.trx` 通過並確認後續身分拒絕仍封鎖、沒有 ticket。另有 broker 假回覆 1.3 秒、重複 validate 不關閉 broker、明確拒絕／false 不放行。未啟動實際 broker 或讀本機 Sandboxie。 |
| C0-2 安裝就緒 | 退出碼 0 後立即 Inspect，誤把 START_PENDING 當失敗。現每秒重新檢查，最多 30 秒，檢查本身也受剩餘預算約束；需要 SbieSvc 執行且 IniLocation 可查。設定待初始化仍 CanPrepare。3010 重開機、非零退出碼及使用者取消維持原行為。 | `c0-red.trx` 的 C02 theory 兩例失敗：pending→ready 原本立即拋錯；持續 pending 原本未等待。假單調時鐘綠燈：3 秒／4 次檢查後成功（含缺 ini 可準備）；30 秒／30 次未就緒才逾時。紅版測試原預期邊界第 31 次，實作改為到 30 秒即停止，不再發新檢查，綠版斷言為 30 次。 |
| C0-3 helper 上限 | 啟動成功後每秒重試沒有退出條件。現連續錯誤五分鐘安靜返回；成功監看會重設預算，取消延遲也安靜返回。正在等待的重試受剩餘預算約束，不能把期限往後拖；不發 Started=false，不 pause，服務的出口／身分／線路判斷未放寬。 | `c0-red.trx` 三例：舊迴圈跑到假測試防掛的第 601／901 次，及取消延遲向外拋錯。綠燈在五分鐘邊界退出、成功回覆後重新計時、failure 回呼 0 次。`monitor-deadline-red.trx` 另重現未完成請求拖過期限，修正後同在 `c0-final-green.trx` 通過。 |
| C0-4 每次操作一次路徑解析 | 多次磁碟讀回都啟動 path helper。現串行設定 operation 保存一次有效路徑，準備及巢狀 Disable／Remove／環境檢查共用；操作結束即丟棄，下一次重新查。只快取路徑，不快取檔案內容；每次讀仍用共享 reader 及穩定性／resident 核對。 | `c0-red.trx`：準備 34 次解析（期望 1），合成巢狀移除 7 次（期望 1）。綠燈均為 1，另驗下一個 operation 重新解析。B 的「途中改回報另一 ini 路徑」替身依本次使用者指定改為固定有效位置，缺檔仍由官方命令邊界建立並核對，未自行手寫真實 ini。 |

紅燈前只加入未使用的 TimeProvider／delay 接縫，以及不快取的 resolver overload，原行為未修；證據 `artifacts/c-stage-0.1.35/red-tracked.patch`、`red-SandboxCloseoutTests.cs`。第一輪 **8 FAIL／0 PASS**，監看未完成請求另 **1 FAIL**；針對性相鄰回歸 **61／61**，最終 C0 **12／12**。其中 broker 保護三例是額外回歸，不宣稱舊版紅燈。

假命令量測同一個缺設定單實例：解析成本固定 200 ms、每個命令 10 ms；改前 **34 次解析＋31 命令＝7,110 ms**，改後 **1 次解析＋31 命令＝510 ms**，節省 6,600 ms（92.8%）。命令數未減少、核對未省略；不計檔案穩定等待、複製及實際載入，**不是實機準備耗時**。數值在紅／綠 TRX StdOut。

修改產品檔：Core `SandboxBootstrapSession.cs`、`SandboxConfiguration.cs`、`SandboxieInstaller.cs`、`SandboxieRuntime.cs`；Service `SandboxController.cs`、`SandboxieServiceRuntime.cs`。修改／新增測試：`SandboxCloseoutTests.cs`、`SandboxConfigurationFixTests.cs`、`SandboxieInstallerTests.cs`、`SecondReviewSandboxTests.cs`、`P0SandboxBrokerTests.cs`。文件為本節、操作說明、測試清單及 release notes；原生與 TCP 產品來源未修改。

### C 完整驗證與產物

實際命令輸出集中在 `artifacts/c-stage-0.1.35/`；C 段已完成，安裝／實機驗收仍未執行。

| 驗證 | 結果 | 證據（相對本工作樹） |
|---|---|---|
| 完整 dotnet test | PASS，511／511、0 fail、0 skip，3 分 40 秒 | `artifacts/c-stage-0.1.35/core-complete.trx`、`core-complete.log` |
| PacketPathChecks | PASS，157,646 assertions，沒有開 capture handle | `artifacts/c-stage-0.1.35/packet-path/result.txt`、`native.log` |
| --sandbox-ui | PASS，59 checks，exit 0 | `artifacts/c-stage-0.1.35/sandbox-ui/checks.json`、`ui.log` |
| --sandbox-storage | PASS，15 checks，exit 0 | `artifacts/c-stage-0.1.35/sandbox-storage/checks.json`、`storage.log` |
| 既有交付基準 | PASS，0.1.32／0.1.33／0.1.34 的 9 個交付檔符合既有 SHA256SUMS，另保存 3 份 SHA256SUMS 自身雜湊 | `artifacts/c-stage-0.1.35/preserved-before.json` |

本次完整測試比 B 增加 11 例（500 → 511）；另修改缺設定準備的既有測試，驗證一次解析。白名單 15 檔，禁止路徑 0、已知私人資料樣式 0，`git diff --check` 通過；原生／第三方產品來源差異 0。證據 `commit-whitelist.txt`、`precommit-scope.json`。

```powershell
dotnet test tests/WindowSocks.Tests -c Release --logger 'trx;LogFileName=core-complete.trx' --results-directory artifacts/c-stage-0.1.35
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PacketPathPerformance.ps1 -OutputDirectory artifacts/c-stage-0.1.35/packet-path
$env:XA_TEST_OUTPUT = Join-Path (Get-Location) 'artifacts/c-stage-0.1.35/sandbox-ui'
dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release -- --sandbox-ui
$env:XA_TEST_OUTPUT = Join-Path (Get-Location) 'artifacts/c-stage-0.1.35/sandbox-storage'
dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release --no-build -- --sandbox-storage
```

另一台電腦可直接使用 [繁中白話測試清單](TEST-CHECKLIST-0.1.35.md)：0.1.32 升級、單／多／混選選單、先 A 後 A+B 的略過及啟動、SBIE1402 時間與 commands-*.jsonl，任何失敗先匯出診斷。

**NOT RUN 維持**：未裝過 Sandboxie 的乾淨電腦／VMware 快照全流程；真實遊戲多開；0.1.33 移除實機；0.1.31 C1／C2／C4／C6；C5 外側 FIN／RST。另一台的實際 A 失敗觸發原因及 SBIE1402 持有者仍待日誌，不用替身測試推稱實機已解決。

### C 打包結果與對應 commit

分支 `codex/sandbox-fixes-0.1.35`。**產物來源 commit：`6541b0de267fd78d8a2802388aec52821c6462d1`**，包含已接受的 B commit `b6d310ac087034f7b56f538b7828a9b2f0463eeb`。已核對 `c8f05202` 移除基底及 `d8334828` TCP 基底皆為祖先。打包前及打包後工作樹乾淨；六個主要 DLL 的 ProductVersion 全為 `0.1.35+6541b0de267fd78d8a2802388aec52821c6462d1`。本段交付數據及回復清單補充另作文件提交，不重建或改寫已驗證產物。

在全新 `artifacts/c-stage-0.1.35/payload` 建置，自帶 .NET；交付於**主工作區 `artifacts/release/0.1.35/`**。實際建置命令中的 OutputDirectory 是該主工作區的完整路徑，保存於不提交的 `build-input.json`：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Package.ps1 -Version 0.1.35 -PayloadDirectory artifacts/c-stage-0.1.35/payload -OutputDirectory <主工作區的完整交付路徑>
# Package.ps1 已執行 payload verify-release 與 Setup --verify-payload。
# 再將交付 ZIP 解到全新 artifacts/c-stage-0.1.35/zip-verify，執行：
& artifacts/c-stage-0.1.35/zip-verify/WindowSocks.Tools.exe verify-release artifacts/c-stage-0.1.35/zip-verify 0.1.35
```

| 檢查 | 實際結果 | 證據（位於本工作樹 artifacts/c-stage-0.1.35） |
|---|---|---|
| 官方打包流程 | PASS，退出碼 0；內含第二次完整測試 511／511、0 fail、0 skip，3 分 33 秒 | `package.log`、`build-input.json`、`build-result.json`、`pipeline-core-complete.trx` |
| 建置 payload | PASS，354 個清單檔，所有組件版本一致 | `package.log` |
| Setup 內嵌套件 | PASS，Package.ps1 的 `--verify-payload` 退出碼 0，才抵達 Release ready；僅暫存解壓核驗 | `package.log`、`build-result.json` |
| 實際交付 ZIP | PASS，354 個清單檔；manifest 與建置 payload 相同、版本一致 | `verify-release.txt`、`verification-result.json` |
| 安裝包輸入一致性 | PASS，內嵌資源來源 `artifacts/package.zip` 的 SHA-256 等於交付 ZIP | `verification-result.json` |
| 二進位來源 | PASS，六個 DLL 均含產物 commit；未修改 TCP／原生來源 | `component-provenance.json`、`source-provenance.json` |
| SHA256SUMS | PASS，3 項記錄皆符合實際檔案；另記錄 sums 自身雜湊 | `delivery-files.json` |

| 檔名 | 位元組 | SHA-256 |
|---|---:|---|
| xa-sk5-Setup-0.1.35.exe | 123456000 | `9776097C9B2F65258162987937A64A1732BEBDE91A82346F00F59D23CFF290BB` |
| xa-sk5-0.1.35-win-x64.zip | 73183144 | `5D812846C370335A0C847FA3F418EE09DB58D9D70CB9947639DC04A683D5836B` |
| SHA256SUMS.txt | 263 | `C8EFF9813CAE5B5A1DE4FA96013EF1617C3ACC989CB050FA55C237F19061BCC8` |
| latest.json（本機離線更新資訊） | 1533 | `F9197E202F796119DE65A85874001F07E92CF2DDBF2FB116B2F3A38C6B02C037` |

### 舊交付保留核對

PASS：三個舊版本的 Setup、ZIP、latest.json 及 SHA256SUMS 共 **12 檔**，建置前／後大小與 SHA-256 完全相同。`preserved-before.json`／`preserved-after.json` 保存全部記錄；下表列安裝包及 ZIP 的共同雜湊（前後一致），未覆蓋任何舊版交付。

| 版本／檔案 | 前後相同 SHA-256 |
|---|---|
| 0.1.32 Setup | `39344A81354B9964D8770A745E945E0CAE5D1754F565681658F6DF029A86DC9C` |
| 0.1.32 ZIP | `1A3B066B0E771A0D5116DDFFE83776ECBC063782ED0E1F65B0B4AC693E9275FD` |
| 0.1.33 Setup | `AE5961B4740607522067E7B20673C2595C9B9C74EB2DBB7E834937BA1B81B53E` |
| 0.1.33 ZIP | `AA37B27571003E5F66AFDF3859F9F93F5BFC0F798FD3C29F4CA93D1657C1A922` |
| 0.1.34 Setup | `B2279AE87BBA023D1D1725D2D5D2566A56E31E38EF4DB32F23639846B734742D` |
| 0.1.34 ZIP | `0B85BDF6D317B88395F5D6EE7EBC413005E9990B40CD5AC31C20AB4518C57405` |

**C 段結束**：本機測試交付完成，停止等待使用者「繼續」。沒有發布、推送、tag、草稿、安裝、遊戲登入、Sandboxie 設定變更或代理線路測試。實機 NOT RUN 清單及另一台電腦驗收步驟仍適用。
