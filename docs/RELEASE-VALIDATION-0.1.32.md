# xa sk5 0.1.32 驗證紀錄

日期：2026-10-31（台北）。依使用者最新要求加入三個以上沙盒與新電腦自動安裝 Sandboxie，完成後發布。工作自已審查的 `389cc68` 建立獨立工作樹；保留原 0.1.31 套件與 C 階段未提交紀錄。未安裝產品、重啟實機服務、調整既有 Sandboxie 設定或操作代理／遊戲。

## 變更與失敗重現

| 項目 | 舊程式失敗重現 | 修正與覆蓋 |
|---|---|---|
| 三個以上實例 | 3 個已保存沙盒的 WPF 測試找不到第 3 條可設定列，exit 1 | 可增減未保存列，支援 1–10 個；固定身分、路徑與線路、反向載入、擴充與取消接續、名額限制、重複線路拒絕 |
| 任意實例啟動 | 原操作限 A／B，已有佇列本身可接收 N 個 | 選取啟動 C、同遊戲全部啟動、右鍵單個啟動、間隔與取消；A–J 名稱及不同邊框色 |
| 新電腦安裝入口 | WPF 檢查缺少安裝按鈕，exit 1 | 一鍵安裝及準備時自動接續；背景下載、固定 hash／大小、簽章核對、UAC、安裝後檢查與可診斷錯誤 |
| 安裝失敗／取消 | 新增隔離測試 | 壞 hash、壞簽章、錯誤導向、超量下載、UAC 取消、重開機碼、安裝失敗／檔案仍缺、既有安裝與競爭、下載取消及安裝中關窗不強殺 |

## 已執行

命令在獨立工作樹根目錄執行；產物位於其 `artifacts/`，不提交。完整命令及後續建置結果保留在本機產物目錄。

- **PASS**：`dotnet test tests/WindowSocks.Tests -c Release --filter 'FullyQualifiedName~SandboxieInstallerTests|FullyQualifiedName~SandboxMultiInstanceTests' --logger 'trx;LogFileName=feature.trx' --results-directory artifacts/test-results`，17／17。
- **PASS**：`dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release -- --sandbox-ui`。三列擴至十列、禁止第十一列、保留已保存列、選線刷新保留、取消、三個批量啟動、選取 C、右鍵啟動、新電腦安裝按鈕，以及「新增三列→自動安裝→準備→三列狀態」皆通過。真實 importer／resume planner，安裝、複製與服務 IPC 使用隔離替身。開窗 1051 ms、忙碌時 dispatcher 37 ms，並保存一般／最小尺寸截圖。
- **PASS**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PacketPathPerformance.ps1 -OutputDirectory artifacts/packet-path-final`，157644 assertions；未開 capture handle。
- **SETUP ERROR，保留證據**：首次直接跑完整 dotnet test，新工作樹未先建置 `WindowSocksNative.dll`，350 PASS／106 FAIL。日誌與 TRX 保留，不能算產品回歸通過；後續先跑 `scripts/Build-Native.ps1` 再跑完整測試。
- **PASS**：官方 Sandboxie x64 安裝檔僅下載及驗證，沒有執行。24930680 bytes；SHA-256 `F27078FF74A07906FD51476079E1A7A8F5E20C8BB75A9D2AE86169D47179AE76`，符合官方 GitHub release digest。Authenticode `Valid`，簽署者 NOVASYNC LABS PTE. LTD.。已核對固定版本安裝原始碼與 Inno Setup 參數；`/SILENT /NORESTART /RESTARTEXITCODE=3010 /ALLUSERS /PORTABLE=0 /OPEN_AGENT=0 /TASKS=`，不使用強制關閉或自動重開機。
- **PASS**：下載官方 0.1.30 portable 作隔離更新基準，SHA-256 `F771066D6DCF32AA6FC296BDE6647482DC1A7ADED97894418E8C95ED415D833B`。未啟動舊產品或其服務。

## 發布前檢查

完整 dotnet test、最終 package build／內嵌 payload 驗證、0.1.30→0.1.32 synthetic portable 更新、遠端附件 hash 核對：完成後在此追加結果。失敗時不得公開套件。

## 實機限制與延後項目

| 驗收 | 結果 |
|---|---|
| 0.1.31 N1–N4、A／B | 使用者已複核接受；沿用原紀錄，沒有重跑實機 |
| C0、0.1.31／0.1.30 套件 hash | 前輪已核對，不修改既有證據 |
| C1、C2、C4、C6 | NOT RUN；本次沒有安裝／登入或 30 分鐘實機觀察 |
| C3、C5 FIN／RST 擷取 | NOT RUN；仍是已知限制 |
| 乾淨電腦的官方 Sandboxie 實際安裝、三帳號同時遊玩 | NOT RUN；自動化使用替身，不能當作實機通過 |

手動驗收：在未裝 Sandboxie 的 Windows x64 測試電腦安裝本版，點「一鍵安裝」並允許 UAC，確認環境顯示 1.18.8；另測取消 UAC。提供三條不同出口的測試線路，新增 A／B／C，準備後以 10 秒批量啟動，核對三個遊戲本體、出口與實例名稱，再由使用者登入。故障時停止未完成操作、保留下載與 setup.log；本版不自動卸載共用 Sandboxie 或修改其他沙盒。

WFP 仍只保護已知 Game／Box EXE，未知系統路徑、共享 Windows DNS、既有 FIN／RST、BFE 啟動前與普通 PID 綁定不在完整崩潰保護保證內。實例數量支援不代表遊戲允許相同數目的同時登入。

## 提交範圍

只提交產品原始碼、測試、版本與正式操作／驗證文件。排除主工作樹既有未提交 C 文件、reviews/、YongOrigin/、artifacts/、experiments/、舊歸屬不明腳本與第三方私有快取，不包含帳號或代理認證。私密樣式檢查中的 RDxaomao 為既有遠端控制程式白名單名稱，不是代理帳號。
