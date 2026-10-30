# 0.1.31 A／B／C 分階段實機驗收（已準備，未執行）

**2026-10-30 收尾範圍更新：** A 的 `-ScheduleSystem` 方式已核准；B 需 A 通過、實際端點及開始前「遊戲已關閉」回覆。C0／C1／C6 必做但尚待再次確認；C2／C3／C4 選做；**C5 為 NOT RUN，本次不執行**，其下方方法僅保留作限制說明與未來參考。當前只允許在 A／B 通過後建置 C 安裝包，不允許安裝或發布。P1 延後到發布之後。

本文件針對只有一台電腦、平時使用 A／B 遊戲的情境。每階段都須先由使用者確認該階段的實際指令、端點、時段與回復方法。**本輪沒有執行任何階段、沒有建立 SYSTEM 工作、沒有安裝測試包。** `-RunApproved` 是防誤執行開關，不代表使用者已同意。任一步失敗先保留證據，停止後續步驟。

以下在 `C:\SOCKS5 Client` 的 Windows PowerShell 5.1 執行；管理員需求另標示。`$Endpoint`、`$Port` 必須由使用者提供可控制、持續接受 TCP 的專用測試端點，不能使用遊戲、更新站或既有代理端點。沒有端點就不做 B／C3。A／B/C 產出的檔案只放本機，避免分享帳號、代理認證與完整流量。

## 工具與預設行為

| 工具 | 預設行為／範圍 |
|---|---|
| `scripts/Build-AcceptanceTools.ps1 -Target ReadOnly` | 只編譯 SYSTEM 唯讀工具；不執行 |
| `scripts/Build-AcceptanceTools.ps1 -Target Capture` | 只編譯專用端點的真實 WinDivert 交接工具；不執行 |
| `scripts/Invoke-AcceptanceA.ps1` | 只顯示計畫；核准後讀版本、PID birth、產品 provider／sublayer |
| `scripts/Test-SandboxFirewall.ps1` | 只編譯 B 階段真實 WFP 工具；不執行 |
| `scripts/Invoke-AcceptanceB.ps1` | 只顯示計畫；核准後保存 netsh 前後快照、執行測試 GUID fixture、核對殘留 |
| `scripts/Invoke-AcceptanceC.ps1` | 只顯示計畫；核准後一次只做 Backup、Install、Snapshot、Observe 或 Rollback |
| `scripts/Test-FirewallPolicy.ps1` | 隔離單元測試，所有 Fwpm 呼叫均為記憶體假 API；不連 BFE |

驗收資料夾使用新的日期目錄，不覆寫舊證據。備份含 DPAPI 檔案，僅適用這台電腦及原安裝使用者。C 的安裝包尚未製作，須核准後另行建置、檢查 manifest 與 SHA-256；不能拿現有舊版包冒充本輪工作樹。

## A：唯讀，可保留執行中的遊戲

| 步驟／指令 | 預期結果 | 失敗與回復 |
|---|---|---|
| A0：選定既有 SYSTEM 執行環境；或明確核准下列暫存排程工作方式 | 全程不呼叫 SCM start/stop，不改 Sandboxie、不新增 filter | 沒有核准 SYSTEM 執行方式就停在這裡；不要使用會安裝服務的 PsExec 後備方式 |
| A1：既有 SYSTEM PowerShell 執行 `powershell -NoProfile -File .\scripts\Invoke-AcceptanceA.ps1 -RunApproved -OutputDirectory .\artifacts\acceptance\A-日期` | 工具確認 token 為 SYSTEM；GET_VERSION 顯示 driver 5.73.8、ABI 0x57230 | 若其他版本或裝置無法開啟，保存結果，不升降 Sandboxie、不關遊戲 |
| A1 替代：管理員 PowerShell 在上述指令加 `-ScheduleSystem`（須單獨核准） | 建立唯一 `xa-sk5-readonly-<GUID>` 工作，以 SYSTEM 啟動唯讀 exe，45 秒內完成後移除工作 | 這個傳輸方式會新增／移除暫存排程工作及寫紀錄，並非零系統寫入；不啟停任何服務。逾時只停止該工作。若工作殘留，用 `Unregister-ScheduledTask -TaskName '<此次完整工作名稱>' -Confirm:$false` 清除 |
| A2：閱讀 `system-read.txt` 的 PID 4、lsass、MsMpEng、services birth／錯誤碼 | PID 4 可讀與否如實記錄；受保護程序讀不到時為 birth=0 並附 Win32Error | 程式名稱不代表已證實其 PPL 等級；不提升權限、不終止程序、不把失敗視為可路由身分 |
| A3：讀產品 provider／sublayer 的 GetByKey 回傳碼 | 0=存在；provider 0x80320005／sublayer 0x80320007=不存在。舊版尚未使用 WFP 時不存在是有效觀察 | 其他錯誤停止判讀，不把 access denied 當不存在。唯讀工作無需政策回復 |

完成後回報實際版本／ABI、PID 與錯誤碼、provider／sublayer 結果；先等使用者同意 B。

## B：低風險；A／B 遊戲均已正常關閉

測試 provider `eb758a61-7b8d-46d7-a4ef-918759818171`、sublayer `eb758a62-7b8d-46d7-a4ef-918759818171`；與正式 GUID 完全不同。只封鎖／放行合成 `WfpIntegration.exe`，不設定代理、不處理遊戲。

```powershell
# 僅在使用者核准 B 並確認遊戲已關閉後；管理員 PowerShell
.\scripts\Invoke-AcceptanceB.ps1 -RunApproved -GamesClosed `
  -Endpoint $Endpoint -Port $Port -OutputDirectory .\artifacts\acceptance\B-日期
```

| 步驟 | 預期結果 | 失敗與回復 |
|---|---|---|
| B0：確認遊戲關閉、端點可控；`netsh wfp show filters file=<before.xml>` | 保存 before.xml；測試 GUID 沒有既存 filter。fixture 的直連 baseline 成功 | baseline 失敗不能推論 WFP 是否有效；停止驗收 |
| B1：fixture 建立四個持久 BLOCK | 非 loopback IPv4 TCP 建連失敗；合成 loopback listener 可連 | 不符合就保留 fixture.log；fixture finally 清除測試 GUID |
| B2：健康時 dynamic session 增加四個 PERMIT | 同 sublayer 高權重放行恢復 TCP；撤回後重新封鎖 | 不宣稱驗證 UDP／IPv6／連入方向；這些需另外核准端點與測法 |
| B3：子測試程序持有 PERMIT，終止該合成程序 | BFE 回收 dynamic session，BLOCK 仍在，合成 exe 無法建立新連線 | 不涉及產品服務；rundown 逾時或仍通過都判失敗 |
| B4：finally 清理、`netsh wfp show filters file=<after.xml>` | 測試 filter 為零；保存 product-filter-diff.txt，核對沒有非預期產品政策變動 | Windows／產品背景活動可能自行改 filter，差異需人工辨讀，不能要求全系統 XML 位元相同 |

若 fixture 被外力中止或清理失敗，先核對 GUID，再於管理員視窗執行：

```powershell
& .\artifacts\wfp-integration\WfpIntegration.exe --cleanup-test-approved
netsh wfp show filters file="C:\SOCKS5 Client\artifacts\acceptance\B-日期\after-cleanup.xml"
```

這個回復入口只含測試 GUID。**不得用全域 firewall reset、停止 BFE 或刪正式 provider。** B 完成後先等使用者同意 C。

## C：安裝及高風險驗收；先關閉遊戲、準備可用舊版安裝包

先指定 `$Run` 為此次 C 的全新絕對路徑，`$Tools` 為實際已安裝 Tools，`$Ini` 為實際使用的 Sandboxie.ini，`$Boxes` 為目前兩個完整 Xa_ 名稱。不可從範例猜名稱／PID／端點。先記錄 `sc.exe qc WindowSocks`、`sc.exe qfailure WindowSocks` 及原服務狀態；記錄所有人工變更。

### C0：備份、安裝、回復準備

| 步驟／指令 | 預期結果 | 回復 |
|---|---|---|
| 正常關閉遊戲及 xa sk5；確認服務無待寫設定；`Invoke-AcceptanceC.ps1 -Step Backup -RunApproved -GamesClosed -OutputDirectory $Run -SandboxIni $Ini -BoxNames $Boxes` | 備份 ProgramData\WindowSocks、兩個專屬 INI 區段、各檔 SHA-256；原資料不修改 | 任一區段找不到或備份失敗，停止安裝；保留不完整備份並改用新目錄重做 |
| 檢查原 0.1.30／0.1.29 Setup 的來源、hash 及可用性；另建本輪 0.1.31 Setup，核對 manifest 與工作樹雜湊 | 有可回復套件及可追溯本輪套件。不得自動發布 | 沒有可靠舊包就不進行 C |
| `Invoke-AcceptanceC.ps1 -Step Install -RunApproved -GamesClosed -OutputDirectory $Run -SetupPath $Setup031 -ExpectedSetupSha256 $Sha031` | 正常 Setup 精靈完成；核對已安裝 Desktop、Service、Native 的版本與 SHA-256 | 取消 UAC／Setup 失敗即停止，不另用 taskkill 幫安裝器強制覆寫；依 C-R 回復 |
| 啟動 xa sk5，等待「正在建立 WFP 防護」完成 | 介面保持可操作；未完成前 Arm 拒絕；保存設定的 Enabled 意圖未被初始化失敗永久清掉 | 初始化失敗先保存訊息，自動重試；仍失敗依 C-R |

### C1：啟動、出口、N1／N2 觀察

在 xa sk5 分別啟動 A，再啟動 B；記下實例 ID、固定 ProxyId、PID＋birth、TCP／UDP 實測出口。兩個出口須不同且符合各自綁定；登入及進入角色須再取得此階段帳號操作授權。不要把登入器開窗算成進入遊戲。

每步用 `Invoke-AcceptanceC.ps1 -Step Snapshot -RunApproved -OutputDirectory $Run -ToolsPath $Tools` 保存去認證資料的快照。A 執行時啟動／儲存／移除**已停止的 B**，應沒有 A 的 WFP 放行空窗；觀察 A 換地圖／新 TCP 建連、固定節點不變。要驗證毫秒級空窗需外側封包或 ETW 證據，僅看 UI 成功不能算通過。失敗正常關閉兩盒，保留 WFP 與服務紀錄，依 C-R 回復。

N1 若自然發生單一 PID 查詢錯誤，A／B 應維持；驅動連續三次或 15 秒失敗才封鎖，恢復相容後最遲下一個 5 秒控制檢查重新套用政策。**不為測試而停止／卸載真實 Sandboxie 驅動。** 本輪門檻與恢復已有隔離測試；若沒有自然故障，實機故障恢復標記「未觀察」，不能填通過。既有 TCP 若已被遠端關閉可能仍需遊戲自行重連；程式不要求停止遊戲來重新 Arm。

### C2：沙盒內系統路徑 curl（缺口測試）

核准一個回傳來源 IP 的 HTTPS URL `$ExitUrl`。使用實際 Sandboxie Start.exe 路徑，分別在 A、B 執行：

```powershell
& $SandboxStart ("/box:" + $BoxA) "$env:SystemRoot\System32\curl.exe" --max-time 15 $ExitUrl
& $SandboxStart ("/box:" + $BoxB) "$env:SystemRoot\System32\curl.exe" --max-time 15 $ExitUrl
```

預期：成員被核驗後走相應代理、兩個出口與 C1 一致。捕捉從程序建立起的外側封包，分別記錄 IPv4、IPv6、DNS、首包；任何直接出口判失敗。系統 EXE 不在精確映像 WFP 清單，尤其服務故障／尚無祖先時仍有已知缺口；不能拿 C1 的成功代替此步。回復：等待 curl 的 15 秒逾時或只關閉這兩個合成測試程序，不調整全機防火牆。

### C3：真實 handle 交接（合成流量，遊戲已關閉）

正常停止產品服務後，先用 `sc.exe query WindowSocks` 確認 STOPPED。此步會使用真正的 WinDivert 裝置；須單獨核准。工具拒絕與仍在執行的正式服務重疊。

```powershell
& .\artifacts\acceptance-tools\CaptureHandoffIntegration.exe --run-approved `
  'C:\SOCKS5 Client\third_party\WinDivert-2.2.2-A\x64\WinDivert.dll' $Endpoint $Port
```

預期：baseline 成功、有實際捕獲封包；新 open 故意失敗時舊 handle 保留；100→101、101→100 均依 open→shutdown→close，兩次各 10 條合成連線成功。filter 僅含指定 IPv4 TCP 端點；不加 WFP filter、不改沙盒。這是實際核心函式＋真實驅動的隔離程序驗收，**不是已安裝服務端到端代理／UDP／IPv6 的通過證明**。

回復：工具 finally 關閉自己的 handles。若卡住，先記錄該工具 PID，只結束該合成工具，Windows 回收其 handles；不要停止／卸載共用 WinDivert 驅動。確認工具結束後，依原服務狀態使用 `sc.exe start WindowSocks`，重讀狀態；服務未恢復則走 C-R。

### C4：產品服務 taskkill 與持久 WFP（遊戲已關閉）

此步會中斷代理，必須獨立確認。先以沙盒內的**遊戲副本路徑下專用合成測試 EXE**建立到專用端點的新連線並確認經代理；不要使用遊戲或系統 curl 代替精確映像保護案例。該 EXE 必須先被重新掃描納入 WFP，且保留原副本檔案。另保存 `netsh wfp show filters file=<C4-before.xml>`。

```powershell
$svc = Get-CimInstance Win32_Service -Filter "Name='WindowSocks'"
$svc | Select-Object Name,State,ProcessId,PathName
Get-Process -Id $svc.ProcessId | Select-Object Id,StartTime,Path
# 人工核對 PID、建立時間及已安裝的 WindowSocks.Service.exe 路徑後才執行；不可沿用舊 PID。
taskkill /PID <剛核對的服務PID> /F
netsh wfp show filters file="<此次C4-after.xml絕對路徑>"
```

預期：服務程序死後 PERMIT 隨 BFE session rundown 消失，已知 EXE 的持久 BLOCK 保留，新 TCP 不得直連。外側擷取應看到沒有新的真實出口遊戲連線。若 SCM 自動重啟，標記實際重啟時間與可觀察封鎖時窗；不能把重啟後的 PERMIT 當成 rundown 失敗，也不能沒有證據就判通過。若需暫停 SCM 復原設定，先另外提出精確變更及原值回復指令取得同意，本清單不自動變更。

回復：關閉合成程序，先 `sc.exe start WindowSocks`，核對身份／代理檢測／WFP 恢復，再考慮啟動遊戲；失敗走 C-R。測試用 EXE 只在確認退出後從此次專用路徑刪除，重新掃描時保留遊戲檔案。

### C5：外側 FIN／RST 證據

在使用者控制的路由器 WAN／鏡像埠或外部觀測點執行擷取；不能只在被測主機看 WinDivert 之前的封包。先辨識遊戲目的 IP、代理目的 IP、主機 WAN 真實出口。Linux 外側觀測範例：

```sh
tcpdump -ni <已核對介面> -w xa-sk5-C5.pcap 'host <遊戲目的IP> and tcp'
```

依序記錄開始擷取、正常退出遊戲、關閉沙盒、服務退出時間；需經核准才重做崩潰情境。預期：到**遊戲目的**的封包沒有由主機真實 WAN 出口發出的 FIN／RST；到代理端點的正常外層 FIN／RST 不算遊戲 IP 洩漏。必須連同 SYN、既有 tuple、IPv6 一起判讀。任何直連尾包判失敗，不以 WFP ALE 理論掩蓋。回復：只停止外側擷取，不改網路拓撲；本機依原狀恢復服務，失敗走 C-R。

### C6：雙帳號 30 分鐘

重新核對固定出口後，兩帳號登入角色，正常遊玩至少 30 分鐘；包含換地圖、不同啟動順序、關閉重開 B，並在相同時間觀察主機未代理流量的延遲／逾時。未代理基準端點、採樣頻率須由使用者確認，不使用提供的代理作測速壓力測試。

```powershell
.\scripts\Invoke-AcceptanceC.ps1 -Step Observe -RunApproved `
  -OutputDirectory $Run -ToolsPath $Tools -Minutes 30
```

每 5 秒保存一次去除帳密／票據的狀態、流量與出口資訊。預期：A／B 都保持在線、ProxyId 不交換、沒有全機延遲惡化、WFP 動態放行與核心健康一致。若斷線先記錄時間、實例與階段，停止驗收；正常退出並視情況 C-R。單有 30 分鐘快照不算雙帳號實際在線證據，需遊戲畫面／使用者確認及連線資料相互核對。

## C-R：回復 0.1.30／0.1.29（所有遊戲先關閉）

1. 保存本輪紀錄與壞狀態，正常停止沙盒。備份要先完成且 hash 可核對。不要把壞狀態覆寫到備份。
2. **先處理 0.1.31 的持久 WFP**：在仍可用的 0.1.31 UI 移除已停止的沙盒規則，再執行「清除防火牆封鎖」；確認正式 GUID 的 filters 已清除。原設定稍後從備份回復。若 0.1.31 無法啟動，由其正常解除安裝流程清理專屬 provider；清理失敗就停下回報，不用全機 reset。0.1.30／0.1.29 不會管理新持久封鎖，未清理就降版可能讓遊戲繼續被擋。
3. 執行 `Invoke-AcceptanceC.ps1 -Step Rollback -RunApproved -GamesClosed -OutputDirectory $Run -SetupPath $ReviewedOldSetup -ExpectedSetupSha256 $OldSha`。此步只開啟正常 Setup 精靈；不靜默強制停服務。若舊 Setup 拒絕降版，依正常解除安裝再重裝流程處理，保留遊戲與備份。
4. 舊版安裝完成，確認服務停止後，比對並還原備份的 `settings.dpapi`、`owner.sid` 及原設定必要檔案到原 ProgramData 位置，保留原 ACL；不整目錄盲目覆寫執行中服務。只在 INI 專屬區段確有變動時，以 Sandboxie 編輯功能還原備份的兩個 `[Xa_...]` 區段並 reload，不覆蓋其他使用者的區段。C 本身沒有自動修改 INI。
5. 重新啟動原版本，核對版本、固定節點、A／B 原設定與原服務啟動方式。完成後才由使用者決定是否重開遊戲。回復舊版不代表具備本輪新增的 WFP 防護。

## 回報空表

每階段記錄：同意時間／實際命令／工具 SHA-256／開始與結束時間／每步 expected vs observed／紀錄檔／PASS、FAIL 或 NOT RUN／是否已回復。失敗時保留第一個不符預期的證據；不執行下一階段、不進入 P1。
