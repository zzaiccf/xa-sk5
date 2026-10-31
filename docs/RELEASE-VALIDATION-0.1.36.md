# 0.1.36 沙盒本地網路測試紀錄

## 範圍與基底

- 分支 `codex/sandbox-local-network-0.1.36`，基底 `d70200e6ecc62fd5ea8a47a4b6367d5878249aaa`。
- 獨立工作樹；包含既有 0.1.33 移除、0.1.34 TCP 修正及 0.1.35 沙盒修正。
- 使用者已確認：本地實例略過 xa sk5 全局代理，模式切換須先停止該實例。
- 本輪只用替身服務／Sandboxie／WFP、合成遊戲檔案與封包；沒有操作正式服務、Sandboxie 設定、遊戲、線路或防火牆。不發布、不 push、不 tag、不建立草稿。
- 下列證據路徑相對於本工作樹：`artifacts/local-network-0.1.36/`。證據不納入 Git。

## 實作與保護

| 項目 | 行為與對應檔案 |
| --- | --- |
| 明確保存模式 | `SandboxInstanceProfile.UseLocalNetwork` 預設 false；本地模式 ProxyId 必須為空，代理模式仍須有效節點。`SandboxInstances`、`SandboxPreparation`、`SandboxNetworkRules`。 |
| 實例各自設定 | 沙盒頁每列本地核取方塊；新增「保存網路選擇」，以實例 ID 保存有變更的列。前端預檢與服務端再驗證均要求停止。`SandboxWindow`、`SandboxBackend`。 |
| 固定代理與直連並存 | 原生現有 action 3／rule Blocked=2 明確直連，可越過全局代理；保留 PID birth、祖先繼承、驅動健康與沙盒查詢保護（Reserved=13）。代理異常仍 Blocked=1。原生產品程式碼未修改。 |
| 服務保護 | `SandboxController`、`Controller`、`EmulatorController` 保留 WFP 防護與身分核驗；本地不檢測或保留代理節點，不把空 ID 納入背景代理檢測。 |
| 啟動與相容性 | Snapshot 能力旗標、helper 註冊能力旗標阻止舊服務／舊 helper 使用本地模式。`SandboxBootstrap`、Tools CLI 在本地模式略過出口探測；仍先註冊路由，再開啟啟動器。 |
| 狀態顯示 | `SandboxLauncherState` 結構化確認；`ProxyRules`、`WindowInventory` 顯示本地，直連流量不假報為已測得。小視窗的保存／取消按鈕固定在底部。 |

本地模式會共用本機出口，xa 全局代理以外的主機 VPN／路由仍適用。仍需服務執行；未知身分、暫停、核心失效保持封鎖。共享 Windows DNS 與系統代送流量沿用既有範圍限制，本版不宣稱已驗證所有 DNS／系統流量獨立直連。

## 紅 → 綠與檢查

先引入明確模式資料欄位、路由組裝邊界及新測試，在原有「必須有代理」行為上執行：

- `local-red.trx`：7 項中 **6 FAIL / 1 PASS**。涵蓋無節點本地設定、混合模式接續、釋放節點、直連規則、啟動確認及服務保存。`red-source.patch`、`red-tests.cs`、`red-rule-boundary.cs` 保存當時來源。
- `local-green.trx`：本地加相關既有測試 **19/19 PASS**。
- `local-service-green.trx`：本地核心／保存／服務啟動與暫停檢查 **9/9 PASS**。服務替身覆蓋不需要節點的 arm/register、舊 helper 拒絕、無出口回報啟動、暫停後拒收成功、晚到失敗拒收、執行中禁止改模式與身分錯誤撤回放行。
- 新原生測試覆蓋全局啟用時本地與代理並存、IPv4／IPv6 路由、TCP／UDP 原封包通過、PID 重用、暫停、代理故障不退回直連、驅動失敗／恢復、移除規則後未知身分封鎖。
- 新 UI 測試覆蓋模式刷新保留、切回代理、僅保存變更實例、舊服務拒絕、混合 A+B 啟動、已執行略過、執行中禁止改模式、停止 A 後改線不影響 B。

初次紅測試的多載編譯歧義不算紅證據；新方法改名 ResumeNetwork 後取得以上可執行失敗。原生新測試最初未標記合成封包 checksum-valid，修正測試 fixture 後通過；不是產品失敗。介面原先在小視窗下超出可視區，移至固定底部後通過，前後日誌均保留。

| 命令 | 結果 | 證據 |
| --- | --- | --- |
| `dotnet test tests/WindowSocks.Tests -c Release` | PASS：520/520；0 失敗／略過 | `complete.trx`、`full-tests.log` |
| `scripts/Test-PacketPathPerformance.ps1` | PASS：157,661 斷言；未開啟 capture handle | `native-final.log`、`packet-path-final/` |
| Desktop PerformanceChecks `--sandbox-ui` | PASS：78 項 | `ui-final.log`、`sandbox-ui-final/checks.json`、`sandbox-local-network.png` |
| Desktop PerformanceChecks `--sandbox-storage` | PASS：15 項 | `storage.log`、`sandbox-storage/checks.json` |

已檢視 800×630 合成沙盒頁截圖：本地核取方塊、停用代理選擇與底部保存按鈕可見。測試不連線至正式服務。

## 另一台電腦待驗收清單（尚未執行）

1. 正常登出並關閉遊戲，先匯出診斷。使用完整測試安裝包更新，核對介面與背景服務都為 0.1.36。
2. 開啟原有沙盒設定，確認原先代理沒有被自動改成本地。
3. 保持 B 原有代理，停止 A，勾選 A 本地網路並按「保存網路選擇」。確認只有 A 改變，B 保留線路；重新開啟 A，應開啟啟動器，自行開始遊戲並登入。
4. 先開 A，再多選 A+B：A 應顯示「已在執行，略過」，B 開啟啟動器。右鍵單選／多選／混選仍符合 0.1.35 清單。
5. 經另行核准開啟 xa 全局代理，以受控端點核對 A 實際直連、B 固定代理及一般程式全局路由。代理端點不測速。本地標籤本身不是出口證據。
6. A 執行中嘗試保存模式，應提示先停止且不更動。停止 A 後切回空閒代理；B 不換線。其他本地實例可共用本機網路。
7. 任何失敗先匯出診斷；SBIE1402 記下時間並帶回實例 `ConfigBackups/commands-*.jsonl`。不要傳送帳密或 settings.dpapi。

乾淨機／VMware 快照自動安裝、真實遊戲本地＋代理多開、實際全局旁路、0.1.33 移除實機及 0.1.31 C1／C2／C4／C6 **NOT RUN**。C5 外側 FIN／RST **NOT RUN**。須另外取得使用者同意，不能把替身／封包測試寫成實機驗收。

## 本機交付

產物來源 commit：`b58af693650c9176fecd456a94c16540d7da7fa6`。本機分支 `codex/sandbox-local-network-0.1.36`。交付位於主要專案目錄的 `artifacts/release/0.1.36/`；建置 payload 為本工作樹 `artifacts/local-network-0.1.36/payload/`，沒有安裝或啟動正式介面。

執行 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Package.ps1 -Version 0.1.36 -PayloadDirectory artifacts/local-network-0.1.36/payload -OutputDirectory <主要專案>/artifacts/release/0.1.36`，使用全新目錄。建置流程再次執行完整測試 **520/520 PASS**（`artifacts/test-results/core-tests.trx`）。

| 核對 | 結果與證據 |
| --- | --- |
| 本機提交 | PASS：26 個白名單檔案；禁止路徑 0、私密資料命中 0、`git diff --cached --check` 通過。固定合成測試盒識別另人工核對，不含真實盒名。`commit-whitelist.txt`、`staging-check.json`。 |
| 來源一致 | PASS：建置前來源乾淨；6 個主要 DLL 的 ProductVersion 都為 `0.1.36+b58af693650c9176fecd456a94c16540d7da7fa6`。`source-provenance.json`、`component-provenance.json`。 |
| 建置 payload | PASS：354 個清單檔及元件版本相符。`package.log`。 |
| Setup | PASS：Package.ps1 的 `--verify-payload` 退出碼 0；只有暫存解壓驗證，沒有執行安裝。`build-result.json`、`package.log`。 |
| 實際 ZIP | PASS：解壓至全新 `zip-verify/`，執行其中 Tools 的 `verify-release <zip-verify> 0.1.36`，354 檔通過；manifest 與 payload 相同。`verify-release.txt`、`verification-result.json`。 |
| Setup 輸入 | PASS：內嵌來源 `artifacts/package.zip` SHA-256 等於交付 ZIP。`verification-result.json`。 |
| SHA256SUMS | PASS：3 筆均符合實際檔案；sums 自身雜湊另記錄。`delivery-files.json`。 |
| 既有交付 | PASS：0.1.32–0.1.35 共 18 檔的大小與 SHA-256 前後一致，未覆蓋。`prior-deliveries.json`、`prior-deliveries-after.json`。 |

`Verify-Delivery.ps1` 與 `verification.log` 保存此次解壓／元件來源／舊包核對命令及結果，僅位於本機證據目錄，沒有提交。

| 檔名 | 位元組 | SHA-256 |
| --- | ---: | --- |
| xa-sk5-Setup-0.1.36.exe | 123463680 | `1EEB42812F668C8B1FE922C0DDA6A445CB4046F2F052951B9E36985C85B52834` |
| xa-sk5-0.1.36-win-x64.zip | 73189794 | `F348E5E568F3BB9061ADF4E7CE2BE3DBA3C4298961A6184F039C0ABC1E7C640C` |
| SHA256SUMS.txt | 263 | `06F9638A66464ADF6156C04D5504483EC5BA6E5F5586F6BB3F5CADA08EE99180` |
| latest.json（本機離線更新資訊） | 1306 | `06C40A2707B38CC673053657E75BE3329DE5A1E3AC3C28C1C73F26A37AC11AB1` |

完成本機測試交付後停止。本次沒有發布、push、tag、草稿或實機操作；上述 NOT RUN 項目仍待另行核准。

## 後續授權發布（2026-11-01）

使用者隨後明確指示「發布」。已於台北時間 **2026-11-01 03:44:27** 公開 [v0.1.36](https://github.com/zzaiccf/xa-sk5/releases/tag/v0.1.36)，設為 **Latest**，不是草稿或預發布。沿用上述相同 Setup／ZIP 位元組與 SHA-256；沒有重新建置或覆寫歷史交付。發布說明保留實機 NOT RUN 範圍。

來源推送至 `release/0.1.36`，未修改 main。註解標籤 `v0.1.36` 解參照為產物來源 `b58af693650c9176fecd456a94c16540d7da7fa6`，Release target 同此提交。標籤之後的差異只含正式發布文字及驗證文件，不含產品原始碼變更。

發布前檢查新增歷史中的 67 個路徑：禁止目錄、私密資料與非合成盒名命中皆為 0。只公開兩個附件：Setup EXE、可攜 ZIP；latest.json／SHA256SUMS 仍只保留本機。

本次證據位於工作樹 `artifacts/publish-0.1.36-20261101-034128/`，未提交至 Git：

| 命令／核對 | 結果 | 證據 |
| --- | --- | --- |
| 重新核對本機套件雜湊、Tools `verify-release` | PASS，354 清單檔及元件版本一致 | 原交付雜湊、既有 `verification-result.json` |
| `Test-UpdateRelease.ps1`，來源為解壓的 0.1.32 ZIP、目的為全新合成目錄、payload 為已驗證 0.1.36 | PASS，合成 settings 保留、`serviceTouched=false`、更新後 354 檔驗證通過 | `update-validation.log`、`update-validation/validation.json` |
| `git push --atomic origin HEAD:refs/heads/release/0.1.36 refs/tags/v0.1.36` | PASS，只有指定分支與標籤 | `source-push.log`、`remote-source.txt` |
| `Publish-GitHubRelease.ps1 -ReleaseDirectory <0.1.36交付目錄>` | PASS，草稿上傳兩個附件 | `draft-upload.log`、`draft-release.json` |
| 草稿附件大小、state、GitHub SHA-256 digest | PASS，兩者均 uploaded 且符合本機交付 | `draft-release.json` |
| `gh release edit v0.1.36 --repo zzaiccf/xa-sk5 --target b58af693650c9176fecd456a94c16540d7da7fa6 --draft=false --latest` | PASS，公開為 Latest | `published-release.json`、`publication-check.json` |
| GitHub Latest 與公開附件 | PASS，版本 0.1.36、非草稿／預發布、兩個正式下載網址、大小／digest 一致 | `publication-check.json` |
| 解壓的舊 0.1.32 Tools 執行 `check-update https://api.github.com/repos/zzaiccf/xa-sk5/releases/latest` | PASS，available=0.1.36、newer=true、ZIP 大小與 SHA-256 一致；只查詢，不安裝 | `old-client-update-check.json` |

草稿階段以 REST tag endpoint 查詢回傳 404；改用 `gh release view` 及 release ID 成功核對。這是草稿查詢方式差異，未改動或重新上傳附件。發布後 Latest API 與舊客戶端的公開查詢均通過。

隔離目錄更新檢查不是正式服務升級驗收。仍未操作本機正式服務、遊戲、Sandboxie 設定或代理線路；既有實機 NOT RUN 清單維持不變。
