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

測試包與來源 commit／SHA-256 待建置後填入。本輪不修改或覆蓋 0.1.32、0.1.33、0.1.34、0.1.35 交付；對照基準保存在 `prior-deliveries.json`。
