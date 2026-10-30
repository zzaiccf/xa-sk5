# xa sk5 0.1.34 本機驗證紀錄

日期：2026-10-31（台北）。使用者回報另一台電腦使用最新發布版、兩個遊戲窗口，偶爾約三秒無回應反白。發生問題電腦的診斷尚未取得，本機資料不能證明該電腦的根因或改善。

以 0.1.33 的 `c8f05202fad21cbcba661b455e09b575026a27c1` 建立獨立工作樹及本機分支 `codex/multi-instance-latency-0.1.34`。本輪沒有安裝產品、啟動／停止實機服務、操作遊戲、修改 Sandboxie 或防火牆、檢測代理線路，也沒有發布、push、tag、草稿。

## 問題與修正

| 項目 | 證據及處理 |
|---|---|
| TCP 雙向互相等待 | 舊 tcpPump 在單執行緒使用阻塞 send_all；慢接收端使反向讀取停住。以實際 TCP 回送 socket 呼叫共同產品 pump，兩個方向皆重現反向訊息超過 1 秒仍未收到 |
| 非阻塞雙向轉送 | 兩端設定 FIONBIO，各方向最多 64 KiB 緩衝；保留 partial write，只有該方向緩衝空時再讀，兩方向每輪皆可前進。無新增每連線執行緒，總使用者緩衝最多 128 KiB／連線 |
| 生命週期 | 單方向有待送資料且約 5 秒無傳送進度才逾時；空閒空佇列不因此逾時。EOF 待該方向資料送完才 shutdown，保留反向回應；select 返回後再檢查 active/alive/路由歸屬 |
| 診斷 | 匯出診斷及每秒輪替 JSONL 新增 TCP 佇列數／累積及最大停留微秒、would-block、stall timeout；舊 native DLL 缺少新 API 時回傳 null。計數跨所有 TCP 連線，非交易式快照，不是遊戲 ping／丟包證明 |
| 原有保護 | TCP_NODELAY 原本已啟用，未拿來當作新修正。沒有更動 WFP、Sandboxie 身分、固定節點、UDP、逾時直連回退或 CPU/GPU 設定。保留 0.1.33 移除功能與既有 N1–N4 保護 |

依據：Microsoft [send](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-send) 說明阻塞傳送會等待緩衝空間，非阻塞傳送在空間不足時回報 WSAEWOULDBLOCK；[select](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-select) 說明 write-ready 不代表任意大小的阻塞 send 都不會等待。因此只加入可寫 select、不改為非阻塞，不能解決這個問題。

## 命令與結果

工作目錄：`C:\SOCKS5 Client\artifacts\worktrees\sandbox-0.1.34`。測試只使用合成設定、暫存目錄及回送 TCP，不連正式代理／遊戲。完整命令輸出與 TRX 位於本機 artifacts/（不提交）。

- **HARNESS FIX**：首次 `relay-red.trx` 用固定 8 MiB 發送並於連線後縮小接收緩衝，Windows 仍容納了整批資料，失敗在測試的「仍塞住」前置斷言，不能當產品重現。改為建立連線前設定接收窗，再以固定 64 KiB 陣列持續施壓，不累積無界記憶體。
- **EXPECTED FAIL**：使用 0.1.33 原生 DLL，`dotnet test tests/WindowSocks.Tests -c Release --filter FullyQualifiedName~NativeRelayBackpressure --logger 'trx;LogFileName=relay-red-bounded-window.trx' --results-directory artifacts/test-results`；2/2 失敗，分別在上行與下行塞住時，反向 1 byte 訊息等待 1 秒逾時。證據 `artifacts-relay-red-bounded-window.log`、對應 TRX。測試 socket 為真實本機 TCP，呼叫共同產品 pump。
- **PASS**：修正後 `scripts/Build-Native.ps1`，接著 `dotnet test tests/WindowSocks.Tests -c Release --filter FullyQualifiedName~RelayDiagnosticsTests --logger 'trx;LogFileName=relay-final.trx' --results-directory artifacts/test-results`：**10/10，0 skip**。反向訊息在下行壅塞 6.69 ms、上行壅塞 0.97 ms；只代表該次回送測試，不能換算遊戲速度。另驗證兩條與四條同時各方向約 512 KiB 的二進位資料一致、EOF、上游 RST、認證遮蔽、診斷序列化；持續壅塞 5.02 秒回報正確傳送逾時與計數。
- **PASS**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PacketPathPerformance.ps1 -OutputDirectory artifacts/packet-path-0.1.34`：**157646 assertions**，沒有開 capture handle。保留封包路徑／規則／PID／生命週期回歸，另驗證輪替日誌仍包含 detail 及新增 relay 欄位。證據 `artifacts-packet-path.log`、`artifacts/packet-path-0.1.34/result.txt`。
- **PASS**：複製已核對官方 WinDivert 快取至本工作樹；DLL SHA-256 `C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2`；SYS `8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2`，Authenticode Valid。只提供建置與 parser 測試，不啟用驅動。
- **PENDING**：完整 dotnet test、建置安裝包／可攜 ZIP、版本與 SHA-256 核對在產品提交後執行，完成後補寫結果。

## 交付範圍

版本 0.1.34，本機測試版。僅提交產品原始碼、測試、正式說明、版本及 release notes。排除 reviews/、YongOrigin/、artifacts/、experiments/、第三方二進位快取、舊未確認腳本及個人設定。主要 workspace 及其他 worktree 保持原狀。

## 實機確認（NOT RUN）

在發生問題的電腦完整更新介面與服務後，依 `docs/GAME-LATENCY-DIAGNOSTICS.md` 保存測試前後診斷、記下卡頓時刻與兩窗是否同時受影響。沒有該電腦遊戲／資源／網路紀錄，不能宣稱三秒反白已根治。真實 Sandboxie 移除、乾淨電腦安裝、0.1.31 C1/C2/C4/C6 仍 NOT RUN；C3/C5 NOT RUN，DNS／未知程序路徑／FIN/RST 等既有驗收限制維持。
