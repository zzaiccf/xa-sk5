# xa sk5 0.1.33 本機驗證紀錄

日期：2026-10-31（台北）。使用者要求沙盒多開加入移除，避免留下未察覺的資料占用。以 0.1.32 的 `b6d3f3836adfb163604ee7ad8ac8221ea6ec8610` 建立獨立工作樹及本機分支 `codex/sandbox-removal-0.1.33`；沒有安裝產品、變更實機服務／Sandboxie／防火牆或操作遊戲與代理，沒有發布、推送、tag 或草稿。

## 實作

| 項目 | 行為與保護 |
|---|---|
| 入口 | 沙盒頁「移除實例／儲存空間」、規則右鍵；顯示檔案總量、路徑、登記狀態與無法驗證原因 |
| 保留／清除 | 預設只移除設定；永久清除需另勾選確認，換選目標會重設確認，不刪原始來源與其他實例 |
| 身分及生命週期 | 服務持久保存 RemovalPending，停用該實例的代理綁定；阻止 arm/save/舊 remove/deleteRule 繞過；其他實例保護維持。檔案與盒設定完成後才 finish 並釋放規則 |
| 殘留與重試 | RemovalRecords 位於待刪目錄之外；掃描已保存、舊 prepared.json、準備日誌及新 instance.json。移除前忘記該筆草稿，避免 resume 復活已刪身分。取消與部分失敗不報完成 |
| 檔案邊界 | 只接受目前使用者 StorageRoot/ID/Game、Box；拒絕來源位於儲存根目錄、連結／接合點、不匹配歸屬。刪除時由磁碟根到資料目錄釘住目錄 handle，不允許 share-delete，逐層非遞迴刪除，不透過提升權限處理使用者樹 |
| Sandboxie | 核對確切 FileRootPath、無執行中成員；以官方 SbieIni set Enabled n 並核對磁碟及 resident 設定，再清檔。只刪選取的 section，讀回驗證，不用可能掃描其他 pending-delete 目錄的 Start delete phase 2 |
| 執行緒 | 掃描及刪除在背景工作，介面可取消；準備與清理共用互斥閘，服務另持久保護已保存實例。外部手動重設 Sandboxie／修改目錄不屬受控流程，遇到偵測到的變更會停止 |

官方依據：[SbieIni 命令列](https://sandboxie-plus.github.io/sandboxie-docs/Content/SbieIniCommandLine/)、[Start 命令列](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine/)、[hive 卸載與占用](https://sandboxie-plus.github.io/sandboxie-docs/Content/SBIE2208/)。不宣稱取消可復原已刪檔案，不宣稱測試替身代表實機整合通過。

## 命令與結果

以下命令在本工作樹執行，日誌、TRX 與截圖位於本機 artifacts/（不提交）。

- **EXPECTED FAIL**：新增 `CleanupReservationIsDurableAndPreventsRearmingOrOrdinaryRemoval` 後，舊程式回覆「不支援的服務命令」。`dotnet test tests/WindowSocks.Tests -c Release --filter FullyQualifiedName~CleanupReservationIsDurable --logger 'trx;LogFileName=cleanup-red.trx' --results-directory artifacts/test-results`；證據 cleanup-red.trx、artifacts-cleanup-red.log。
- **PASS**：修正後同測試 1/1；cleanup-service.trx。
- **PASS**：`dotnet test tests/WindowSocks.Tests -c Release --filter 'FullyQualifiedName~SandboxRemovalTests|FullyQualifiedName~Cleanup' --logger 'trx;LogFileName=removal-focused.trx' --results-directory artifacts/test-results`，13/13。涵蓋真實合成目錄刪除、保留、重試、鎖檔、取消、設定失敗、舊副本、忘記草稿、junction 拒絕、精確 section、執行中與 resident 未確認拒絕、服務重啟後保持待清理。
- **HARNESS FIX**：首次 `--sandbox-storage` 的既有截圖 helper 只尋找 SaveRuleButton/PrepareButton，新增視窗按鈕名稱不同，NullReferenceException；加入 RemoveStorageButton 後再跑，不是略過檢查。
- **PASS**：`$env:XA_TEST_OUTPUT='artifacts/storage-ui-final'; dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release -- --sandbox-storage`。掃描延遲時 dispatcher 14 ms，取消不修改、保留不清檔、永久確認、換選確認重設、重複點擊、取消、失敗顯示與重試；一般／800×650 最小尺寸截圖檢查按鈕可見。服務與 Sandboxie 使用替身。
- **PASS**：補強後 `removal-final.trx` 為 16/16，另涵蓋刪除途中取消、壞日誌不隱藏有效實例、檔案仍在時 finish 被拒；已包含目錄祖先 handle 保護。
- **PASS**：最終 `--sandbox-storage` 再跑，dispatcher 25 ms，artifacts/storage-ui-release 截圖；選取其他實例時清掉上一項結果文字，避免誤讀。
- **PASS**：`dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks -c Release -- --sandbox-ui`，三開／十列、安裝、選線、取消、批量與右鍵啟動回歸，另驗證右鍵移除開啟正確實例、關窗不刪除。開窗 1033 ms，dispatcher 31 ms。
- **PASS**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PacketPathPerformance.ps1 -OutputDirectory artifacts/packet-path-0.1.33`，157644 assertions，未開 capture handle。
- **PASS**：`scripts/Build-Package.ps1 -PayloadDirectory artifacts/release-payload-0.1.33 -OutputDirectory artifacts/release/0.1.33`，exit 0。流程先重建 native，再跑完整 dotnet test：**472/472，0 FAIL、0 skip，3 分 16 秒**（core-tests.trx）。其後建置所有產品與安裝包；354 檔版本及內容 hash 核對通過，ZIP 355 entries，禁止項目 0，最終 Setup `--verify-payload` exit 0，未安裝。
- **PASS**：交付程式複製到 `C:\SOCKS5 Client\artifacts\local-sandbox-0.1.33`，執行該目錄 `WindowSocks.Tools.exe verify-release <目錄> 0.1.33`：354 檔通過。Core ProductVersion 為 `0.1.33+3391fbfb262be0a95aaf807369a107d831788693`，符合產品提交。
- **PASS**：交付套件複製到 `C:\SOCKS5 Client\artifacts\release\0.1.33`，逐一核對來源與交付目錄 SHA-256 相同。`artifacts/delivery-hashes.json` 保存結果。首次讀 SHA256SUMS 時 Setup 驗證仍進行、檔案尚未產生；等待建置 exit 0 後才執行正式核對，沒有略過任何驗證。

## 本機交付

產品來源提交：`3391fbfb262be0a95aaf807369a107d831788693`。建置後提交僅更新本驗證文件，不改產品或已核對的套件。提交白名單 24 檔，禁止路徑 0、私密樣式 0，`git diff --cached --check` 通過；沒有 push/tag/草稿/發布。

| 交付檔 | Bytes | SHA-256 |
|---|---:|---|
| xa-sk5-Setup-0.1.33.exe | 123416576 | AE5961B4740607522067E7B20673C2595C9B9C74EB2DBB7E834937BA1B81B53E |
| xa-sk5-0.1.33-win-x64.zip | 73159123 | AA37B27571003E5F66AFDF3859F9F93F5BFC0F798FD3C29F4CA93D1657C1A922 |

靜態核對固定版 Sandboxie 的 [Process_Enumerate](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/core/drv/process_api.c) 與 [SbieIni query](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.8/Sandboxie/apps/ini/query.c)：程序列舉依既有程序的盒名篩選，不要求盒設定仍存在；因此移除 section 後仍能再次核對成員數。query 未加 /boxes 可列出停用盒，Enabled 值為逐行輸出。此核對支持完成順序設計，不能代替下列實機驗收。

## 實機驗收清單（NOT RUN）

僅於可丟棄的測試實例進行，操作前需使用者同意；不要用現有 A/B 作刪除測試。

1. 完整更新測試機介面及服務至 0.1.33，建立一個小型合成遊戲副本與另一個對照實例，保存來源及對照檔案 hash。預期兩者可正常準備，記錄盒名及 Game/Box 路徑。
2. 選取實例仍執行時嘗試移除，預期阻止且沒有任何資料刪除；正常關閉後停止它。其他實例仍保持原綁定及 WFP 保護。
3. 先測「移除設定、保留資料」，預期規則、指定 Sandboxie section 已移除，檔案大小不變，儲存空間頁仍顯示可清理殘留。確認固定節點可重新使用。
4. 對此殘留做永久清除，預期僅該 ID 資料目錄與 RemovalRecords 該筆不存在，來源與對照 hash 不變，其他盒設定不變。保存前後 WFP 數與服務快照。
5. 再用另一個可丟棄副本測取消、檔案鎖定、重新開介面與重啟測試服務；預期未完成仍可掃描並重試，不能啟動／保存待清理實例。釋放鎖定後重試成功；不能把 Registry hive 占用視為刪除完成。
6. 原始資料只能靠事前備份回復，永久刪除不可回復；取消只停止後續工作。設定可從測試前備份恢復，復原前核對資料是否完整。不要刪除或覆寫其他盒或正式服務設定。

0.1.31 C1/C2/C4/C6、乾淨電腦安裝及真實三帳號遊玩維持 NOT RUN；C3/C5 NOT RUN，既有 DNS、未知程式路徑、FIN/RST 等限制維持。

## 交付／提交範圍

僅產品原始碼、測試、版本與正式文件。本機產物、reviews/、YongOrigin/、experiments/、未確認舊腳本、第三方二進位快取、帳號與代理認證不納入。原 workspace 的 0.1.31 C 驗收紀錄保持原狀。
