# xa sk5 0.1.31 — 第二輪複審 N1–N4 修正與驗證

## 發布收尾進度（2026-10-30）

本次依使用者指定順序：本機保護提交 → A → B → 建置安裝包與回復包準備。任一步失敗即停，不進入下一步。**P1 全部延後到 0.1.31 發布之後；沒有授權發布、push 或 tag。** 當次證據目錄：`artifacts/acceptance/closeout-20261030-201603/`。

續行授權：使用者已確認 `ManagedProcessTerminator.cs` 的 R098 搬移是 R9 共用化的預期變更，要求重新暫存本報告、使用 `--no-renames` 核對後提交，再執行 A。檢查工具自身的改名／換行／格式誤判，可先用唯讀證據排除並記錄後續行；真正實機驗收不符預期或超出授權仍停止。

| 步驟 | 狀態 | 實際操作／結果 |
|---|---|---|
| 1：保護成果 | FAIL（核對方式誤判；已停止） | 盤點、敏感資料審核、`git -c core.safecrlf=false diff --check`、建立 `release/0.1.31`、依 82 路徑白名單暫存均完成。`git diff --cached --name-only` 與白名單比較失敗：Git 將 `Service/ManagedProcessTerminator.cs` → `Core/ManagedProcessTerminator.cs` 辨識為 R098 搬移，只列出新路徑。隨後僅唯讀複核：`git diff --cached --no-renames --name-only` 與白名單差異為 0、禁止路徑為 0。未執行 commit、未進入 A；等使用者確認後以展開搬移的方式重新核對。 |
| 2：A | NOT RUN | 已核准 `-ScheduleSystem` 暫存工作；等待步驟 1 完成。 |
| 3：B | NOT RUN | A 通過後才進行；尚缺實際 IP／Port，開始前須詢問並取得「遊戲已關閉」回覆。 |
| 4：C 準備／建置 | NOT RUN | B 通過後才取得 0.1.30 正式回復安裝包及全新目錄建置 0.1.31；不安裝、不上傳、不建草稿。 |
| C0／C1／C6 | NOT RUN | 必做，但執行前仍須使用者再次確認；先備妥 C-R。 |
| C2／C3／C4 | NOT RUN | 選做，由使用者決定。 |
| C5：外側 FIN／RST | NOT RUN | 使用者明確排除。本版未實測外側尾端封包；ALE 規則不能代替此驗證，仍屬已知隔離限制。 |

強制排除 `reviews/`、`YongOrigin/`、`artifacts/`、`experiments/` 與任何真實帳號／代理認證／遊戲私有資料。逐檔候選、排除決定、實際命令及結果保存在當次驗收目錄。以下為本次收尾之前的修正與測試紀錄，未執行狀態依上表及後續紀錄更新。

步驟 1 停止時：沒有新 commit hash；HEAD 仍為 `cedaa653ec67e224c912100699cb718995b7ffbf`（這是原基底，不是本輪提交）。使用者已同意排除的 42 個舊腳本、9 份文件與 `third_party/ProxiFyre-2.6.4/` 均保留本機且未暫存。已暫存的是核准的產品候選；本段停止紀錄在其後寫入，尚未重新暫存。沒有 push、tag、實機驗收、安裝包下載或建置。

本輪日期：2026-10-30。**版本維持 0.1.31，未發布、未推送、未提交、未安裝；未操作實機背景服務、遊戲、Sandboxie 設定或代理。A／B／C 實機驗收全部 NOT RUN，P1 等待使用者確認。** 以下先列本輪結果；後半部保留上一輪 R1–R9／P0-3／P0-4 的歷史證據，其測試數字與 hash 不代表本輪產物。

## N1 — 單一 PID 錯誤與驅動故障分離、自動恢復

- **是否重現：是。** 先改測試並執行舊行為。`N1-native-red.log`：`FAIL: N1 a single PID error must not change global driver health`。`N1-service-red.log`：`N1_IdentityRecoveryKeepsIntentAndResumesWithoutRestart` 與更新後的 R3 測試均得到 `Expected: True; Actual: False`，證實 Enabled 被永久保存為 false。
- **修正摘要／主要檔案：** `native/SandboxIdentity.h`、共用唯讀 `SandboxDriverDevice.h`、`WindowSocksNative.cpp`、`Core/NativeEngine.cs`、`Service/SandboxieServiceRuntime.cs`、`SandboxController.cs`。PID 查詢錯誤為未知 -1，500 ms 後重試；裝置或版本不合為 -2，封包路徑只設檢查旗標，不在身分 mutex 內呼叫 BFE、不直接更動全域健康。控制工作每 5 秒核對驅動，已核驗狀態連續三次或累計 15 秒失敗才轉 -1；首次尚未核驗的驅動保持拒絕。控制工作先撤回 WFP，再等待 broker；恢復時轉 1、重建原政策並放行。不改使用者持久 Enabled，也不要求重新 Arm。顯示實際版本／ABI，不支援新版的提示與操作文件已補。
- **新測試結果：通過。** 原生覆蓋 PID 的 500 ms 重試、不影響全域狀態、三次／15 秒門檻與恢復；服務 `N1_ControlWorkerPollsAndRestoresPermitWithoutArm` 驗證每 5 秒核驗、撤回早於 broker、無 Arm 自動重新 Allow，以及設定檔意圖保留。全部納入完整回歸。
- **尚未完成／限制：** 沒有讓實機驅動故障來驗收。未知系統映像、由服務代建且尚無可核驗祖先的程序仍有先前披露的身分缺口。自動恢復路由不能復活已被遠端關閉的 TCP，遊戲可能需要自行重連；程式不再要求關掉遊戲重新建立實例。

## N2 — WFP 更新先建立新放行再關閉舊放行

- **是否重現：是。** `N2-red.log`：`FAIL: N2 replace must retain A permit at every API boundary and immediately permit B`。這個測試包含真正的 `PersistentFirewall.h`，僅將 Fwpm API 邊界換成記憶體實作，舊程式確實先關閉 A 的 session。
- **修正摘要／主要檔案：** `native/PersistentFirewall.h`、`Service/SandboxController.cs`、`Controller.cs`。持久 BLOCK 在交易內更新；健康時建立包含全部映像的新 dynamic session，commit 後才關舊 session。建置新 lease 失敗保留既有 lease；關閉舊 session 失敗保留 handle 供重試，撤回會處理全部留存 handles。`allow()` 比對放行映像集合，不因已有 session 就略過新增路徑。Arm／Save／Remove／背景初始化共用立即 Replace＋Allow；Remove 先完成防火牆更新再變更設定。
- **新測試結果：通過。** `N2-green.log`：**43 項原生 WFP 斷言**，每個假 API 邊界觀察 A 都仍有放行；涵蓋新增、移除、BLOCK 失敗、PERMIT 失敗、close 失敗及重試。服務另驗 Arm B／Save B／Remove B 的 Replace→Allow 順序且無 Withdraw。假測試不呼叫真實 BFE。
- **尚未完成／限制：** 真實 BFE 交易、程序死亡 rundown、遊戲新連線是否零中斷仍待 B／C 驗收；不把假 API 結果當成實機通過。

## N3 — 掃描移出 Gate，啟動時背景初始化

- **是否重現：是。** `N3-red.log`：儲存／移除得到 `recursive discovery held Gate`；建構測試得到 `constructor blocked on recursive discovery`。測試接縫只替換掃描器，舊程式的呼叫位置與鎖行為保持原樣後取得失敗。
- **修正摘要／主要檔案：** `Service/Controller.cs`、`SandboxController.cs`、`RuleManagement.cs`。儲存、移除、規則頁刪除均在 Gate 外讀成員與遞迴映像；回鎖核對世代再套用，掃描後也重查目標是否已啟動。初始化背景掃描，成功前沙盒暫停且拒絕 Arm，不把暫停寫成使用者設定；失敗自動重試。取消／Dispose 等待初始化收尾，避免背景工作使用已釋放鎖。
- **新測試結果：通過。** 驗證三個入口可取得 Gate、建構在掃描受阻時返回、初始暫停但保留磁碟 Enabled、保存／移除時世代改變拒絕過期清單，過期結果不觸發防火牆更新。`N1-N3-green.log` 保留針對性結果，最後完整測試另包含追加控制工作順序案例。
- **尚未完成／限制：** 未在真實 65,000 檔遊戲目錄量測掃描時間。將長 I/O 移到背景不等於 Windows 磁碟列舉可立即中止；取消後不得套用過期結果，停止會等工作收尾。其他 P1 效能工作未展開。

## N4 — 列舉限制產品 provider

- **是否重現：是。** `N4-red.log`：`FAIL: N4 enumeration must specify the product provider`。
- **修正摘要／主要檔案：** `native/PersistentFirewall.h` 使用 `FWPM_FILTER_ENUM_TEMPLATE0` 指定產品 provider、四個 ALE layer、全 action mask 及 disabled filter；回傳後仍核對 sublayer。[Microsoft 範本文件](https://learn.microsoft.com/en-us/windows/win32/api/fwpmtypes/ns-fwpmtypes-fwpm_filter_enum_template0) 說明 provider 範圍與 actionMask 的語意。
- **新測試結果：通過。** `N4-green.log`：**23 項斷言**，驗證範本 provider、BLOCK／PERMIT 計數、排除其他 provider／sublayer，清理不刪外部規則。
- **尚未完成／限制：** 真實 BFE 列舉只準備驗收，不在本輪執行。僅管理本產品目前使用的四個 ALE layer，未宣稱涵蓋未來其他 layer 的未知政策。

## A／B／C 驗收腳本清單（全部未執行）

完整每步命令、預期結果、失敗判定與回復方式：[LIVE-ACCEPTANCE-0.1.31.md](LIVE-ACCEPTANCE-0.1.31.md)。

- **A**：`Build-AcceptanceTools.ps1 -Target ReadOnly`、`Invoke-AcceptanceA.ps1`、`ReadOnlyAcceptance.cpp`。以 SYSTEM 讀 GET_VERSION、PID 4／常見受保護程序 birth、產品 provider／sublayer；可選暫存排程工作傳輸，需另外同意，結束移除，不啟停服務。
- **B**：`Test-SandboxFirewall.ps1`、`Invoke-AcceptanceB.ps1`、`WfpIntegration.cpp`。獨立 GUID、合成 EXE、指定端點、netsh 前後快照與差異；新增明確的測試 GUID 專用 cleanup 入口。
- **C**：`Invoke-AcceptanceC.ps1`、`Build-AcceptanceTools.ps1 -Target Capture`、`CaptureHandoffIntegration.cpp`。備份 ProgramData 與專屬 INI 區段、正常 Setup 安裝／回復、去認證資訊快照與 30 分鐘觀察。清單另列系統 curl、taskkill 服務、外側 FIN／RST 擷取及逐步回復。交接工具使用實際核心函式與真實但限定端點的 WinDivert，拒絕與正在執行的服務重疊；它不代替正式服務完整代理驗收。
- **本輪僅做**：三個實機工具編譯成功（A-build.log、B-build.log、C-capture-build.log），PowerShell AST 解析通過。沒有呼叫任何 `-RunApproved`／`--read-approved`／`--run-approved`／cleanup 入口，也沒有建立排程工作。

## 本輪完整檢查與來源

- `dotnet test tests/WindowSocks.Tests -c Release`：**439／439 通過，0 失敗、0 略過**；最終結果記錄於 `artifacts/review2-0.1.31/dotnet-final.log` 與 `test-results/review2-final.trx`。
- PacketPathChecks：**157,644 項斷言通過**（Windows PowerShell 5.1，`native-full.log`），沒有開啟真實 capture handle。N2／N4 的原生假 WFP 檢查另為 43／23 項通過。
- 本輪未改 WPF 介面配置，沒有重跑上一輪視覺／工具列測試；下方的舊數字保留為歷史，不列成本輪新測結果。原生 DLL 與 A／B／C 工具建置通過；實機工具僅編譯。
- 證據目錄 `artifacts/review2-0.1.31/`；red 先於各項行為修正，編譯錯誤不作為 red 證據。Git 沒有暫存／提交／推送；reviews、YongOrigin、artifacts、experiments 未提交。
- **install-manifest：沒有製作安裝包或新 manifest。** 來源為 HEAD `cedaa653ec67e224c912100699cb718995b7ffbf` 加未提交工作樹，不宣稱已安裝或已發布套件對應本次測試。
- 本輪原生原始碼集合 SHA-256：`54f2f47cb1fa6dca811f053ba61bcad93dcda46e49ee56497270d8b25951ea58`。逐檔集合／DLL hash 記錄在 `artifacts/review2-0.1.31/build-evidence.json`；集合算法沿用下方歷史紀錄，新增共用 SandboxDriverDevice.h 包含在集合中。
- 本輪原生 DLL SHA-256：`5b4c4378d689884e6119377990352d2a8c19a756e65096e2b2261dfe587ceeed`；測試輸出目錄使用相同 DLL。沒有新 install-manifest，這個 hash 不是發布套件宣告。

---

# 上一輪 R1–R9／P0-3／P0-4 歷史紀錄

日期：2026-10-30（Asia/Taipei）。本輪依複審第 4 節順序處理 R1、R2、R3 → R5、R8、R9 → R4、R7 → R6 → P0-3／P0-4。**未發布、未推送、未提交、未安裝；未進入其餘 P1。實作完成與隔離測試通過不代表實機完整驗收。**

## 範圍與證據

沒有操作實機背景服務、遊戲、Sandboxie 設定或提供的代理。測試只使用臨時設定、假 runtime／引擎／防火牆介面、自建合成程序、loopback socket、合成封包與螢幕外 WPF 視窗；沒有開啟真正的 WinDivert handle，也沒有呼叫真實 BFE 設定 API。WFP 整合測試只編譯，沒有執行。

本輪證據在 `artifacts/review-0.1.31/`，上一輪 P0 證據仍在 `artifacts/p0-0.1.31/`。每個 R 項先保留舊行為加入測試／測試接縫，取得實際斷言失敗，再修改。下列 red 引述均是行為失敗，編譯錯誤不作為 red 證據。私有 reviews、遊戲與 artifacts 未加入 Git 暫存區。

## R1 — 全局與沙盒只在同一節點互斥

- **是否重現：是。** `R1_PreexistingGlobalAndDifferentSandboxNodeKeepGlobalEnabled`，`R1-red.log`：`Expected: 1; Actual: 2`。
- **修正摘要／主要檔案：** `Core/ProxyOwnershipLedger.cs` 移除 RequireMode；`Service/Controller.cs` 移除全局的 SandboxInstances.Count 條件；`Service/SandboxController.cs` 保留帳本的同節點占用拒絕。設定了停止中的沙盒不會封鎖整台主機。
- **新測試結果：通過。** 不同節點可並存；既有 P0OwnershipRegressionTests 持續核對同節點的雙向拒絕。
- **尚未完成：** 舊版設定在真實安裝服務上的遷移驗收未執行。

## R2 — 重複出口只影響衝突綁定

- **是否重現：是。** `R2_LaterBindingWithSameExitCannotDisableGlobalTraffic`，`R1-green-R2-red.log`：`Expected: 1; Actual: 2`。`R2_EnablingConflictingGlobalIsRejectedBeforeChangingExistingPolicy`，`R2-enable-red.log`：`Not found: "出口"`（舊程式先走驅動啟用）。
- **修正摘要／主要檔案：** `Service/Controller.cs` 啟用前檢查帳本與出口，候選檢測完成後再次核對，最後才變更全局；運行中的全局不因其他綁定出口重複而切成狀態 2。缺少驅動時先做唯讀拒絕，不發起網路檢測。
- **新測試結果：通過。** 全局保持狀態 1，衝突綁定封鎖，拒絕啟用不改原政策；完整回歸也涵蓋無驅動啟用拒絕。
- **尚未完成：** 未測真實供應商節點出口變動；出口檢測到實際連線之間仍受供應商動態出口影響。

## R3 — 身分故障範圍與快取

- **是否重現：是。** 原生 `r3Checks`，`R3-native-red.log`：`FAIL: R3 missing or incompatible identity driver must not block unrelated host traffic`。`R3_StoppedSandboxDoesNotInstallHostWideIdentityProbe` 預期 false、實際 true；`R3_UnsupportedDriverIsRejectedBeforeArming` 找不到 `unsupported Sandboxie ABI`，見 `R3-service-red.log`。
- **修正摘要／主要檔案：** `native/SandboxIdentity.h`、`native/WindowSocksNative.cpp`、`Core/ISandboxRuntime.cs`、`Core/NativeEngine.cs`、`Service/SandboxController.cs`、`Service/SandboxieServiceRuntime.cs`。只為有效票據／存活成員安裝 @box；先核對 Plus 1.18.8、driver 5.73.8、ABI 0x57230；失敗封鎖已知成員、可達祖先鏈及保護路徑，不封鎖無關主機 PID。未知狀態撤回已知映像的 WFP 放行並暫停實例。box vector 在政策替換時建立；4096 項快取逐筆淘汰。
- **新測試結果：通過。** 包含 missing/incompatible 查詢、5,000 次快取填入、已知成員失效、停止實例沒有 @box、Arm 不支援 ABI 拒絕。
- **未達成的完整保證：** 若程序位於未知系統路徑、由服務代建、尚無核驗祖先鏈，又恰逢驅動不可用，就沒有足夠身分資訊可在不封鎖主機的前提下辨識它。精確 AppId WFP 也無法覆蓋未知路徑；**不能宣稱「任何沙盒程序都不直連」已驗收**。每盒 SID 方案及實機 ABI 驗證見下方。

## R4 — 活流與隔離表效能

- **是否重現：是。** `r4Checks`，`R4-red.log`：`FAIL: R4 closed tuples must not retain full Flow objects after pruning`。補充並行關閉測試 `R4-expiry-red.log`：`FAIL: R4 a late old close must not shorten a newer tuple or alias quarantine`，修正為期限取最大值，避免遲到的舊 close 縮短隔離。
- **修正摘要／主要檔案：** `native/WindowSocksNative.cpp`、`native/VmwareGateway.h`。活流改原子計數；已關閉 TCP tuple／alias 分離成輕量到期表，清理週期釋放完整 Flow。無活流時不取 flowMutex；移除 32768 總流表配額，prune 的系統查詢／UDP 比對不長時間持有 flowMutex。
- **新測試結果：通過。** 20,000 筆隔離紀錄、30,000 次分類，修改前／後 p99 均 **1 µs**、p50 均 **0 µs**（計時解析度 1 µs）。另持有 flowMutex 200 ms 驗證無活流主機封包不等待，及 40,000 筆已關閉 tuple 不阻止新 SYN。
- **限制：** 這是模擬分類測試，並非真實網卡延遲。2048 活流、256 worker 與約 40,000 個 240 秒 alias 配額仍保留；40,000 tuple 壓測不等於同時耗盡全部 relay alias，沒有宣稱無限連線能力。

## R5 — PID 4／未受管 PID 的 birth=0

- **是否重現：是。** `r5Checks`，`R5-red.log`：`FAIL: R5 System without a managed identity must not be blocked by unrelated guards`。
- **修正摘要／主要檔案：** `native/WindowSocksNative.cpp`、`native/SandboxIdentity.h`。PID 4 與沒有受管規則的 PID 不因 birth=0 封鎖；仍有受管身分規則的 PID 無法核對 birth 時封鎖。FIN／RST tuple 保護保留。
- **新測試結果：通過。** PID 4、未受管 PID、曾納管且規則仍保留的 PID 三類均涵蓋。
- **尚未完成：** 未以 SYSTEM 查詢真正 PID 4／PPL；依本輪限制列為需同意的實機步驟。

## R6 — 沙盒接續但不建立 Job

- **是否重現：是。** `r6Checks`，`R6-red.log`：`FAIL: R6 sandbox ancestry must support the no-Job flag`。
- **修正摘要／主要檔案：** `Service/Controller.cs` 的 Sandbox Reserved=13；`native/WindowSocksNative.cpp` bit 2 禁止建立／查詢 xa sk5 Job，仍保留 PID＋birth lifetime 及父系接續。舊 Job handle 也不再作為該規則的成員依據。
- **新測試結果：通過。** 合成子程序繼承路由，根身分仍釘選，沒有 xa sk5 Job。
- **尚未完成：** 未對實際遊戲／CEF 測試。Windows 無法把已加入舊 Job 的存活程序直接移出；正式驗收須用新啟動的遊戲程序。

## R7 — Gate 與快照

- **是否重現：是。** `R7_SnapshotDoesNotWaitForConfigurationGate`：`snapshot waited for Gate`；`R7_ValidateAndTerminateDoNotHoldGate`：`operation held Gate`；`R7_UnchangedSandboxObservationDoesNotRepublishPolicy`：`Expected: 2; Actual: 3`。見 `R7-red.log`。
- **修正摘要／主要檔案：** `Service/Controller.cs`、`Service/SandboxController.cs`、`Service/SandboxieServiceRuntime.cs`、`Core/SandboxieRuntime.cs`。INI 只讀少數專屬鍵、Gate 外最多等待 1 秒；terminate 在鎖外，返回核對世代，停止期間拒絕新啟動。250 ms 相同路由狀態不重套。snapshot 原子發布已序列化快照，讀取不等 Gate。
- **新測試結果：通過。** 三項重現測試、停止／broker 回歸與 WPF 沙盒流程通過。
- **限制：** snapshot 是最近發布資料，長操作時可呈現上次狀態；broker 內部仍序列化官方 API，停止期間成員查詢可能等待，但不再占用主 Gate。INI 的 1 秒是等待期限，不是 Windows 磁碟 I/O 能被強制中止的保證。其他 P1 效能工作未展開。

## R8 — 故障立即恢復與 handle 交接

- **是否重現：是。** `R8_RecoveryWithoutLiveSandboxIsImmediateAndReplacesCaptureBeforeClosing`（有／無已保存沙盒），`R8-red.log`：`Expected: ["replace"]; Actual: ["close", "start"]`；另一案例得到「持續十秒後停止受管沙盒」。
- **修正摘要／主要檔案：** `Service/EngineLifecycle.cs`、`Service/Controller.cs`、`native/WindowSocksNative.cpp`。不因停止中的設定等待十秒；立即 ReplaceCapture。新 handle 以 100／101 不同優先權先開，舊讀取結束後才交換送出 handle 並關閉舊 handle；保留 relay／路由。失敗保留舊 handle，只有存活沙盒持續故障才排程停止。
- **新測試結果：通過。** 服務的兩案例與原生 `r8Checks` 驗證健康不開新 handle、開啟失敗不關舊 handle、成功順序 open → shutdown → close。
- **尚未完成：** 真實 WinDivert 優先權、排隊／重注入、30 分鐘連續流與外側抓包尚未執行。

## R9 — 明確強制停止

- **是否重現：是。** `R9_ExplicitRecoveryCanRecoverUnresponsiveService`，`R9-red.log`：`System.IO.IOException: 背景服務尚未停止，請重試關閉。`。
- **修正摘要／主要檔案：** `Core/ServiceExit.cs`、`Core/ForceServiceStop.cs`、`Core/ManagedProcessTerminator.cs`（由 Service 共用化）、`Shared/ForceStopDialog.cs`、Desktop MainWindow、Setup ReleaseInstaller／ComponentInstaller／Program、Updater Program。一般停止失敗保留正常路徑；明確選強制停止後先用釘選 handle 核對 PID、birth、SID 並結束已知沙盒成員，列出未關閉程序與未知範圍，再確認 SCM stop。取消不發 SCM，停止未確認不覆寫程式。
- **新測試結果：通過。** 恢復入口、同意／取消的呼叫順序、既有正常退出與工具列生命週期回歸通過；Setup／Maintenance／Updater 編譯通過。
- **限制：** 服務卡住時清單可能過期或無法取得，未列出的新子程序／全局程式無法完整列舉；UI 明確顯示此風險。SCM 自身卡住仍回報失敗，不另做靜默 taskkill。實際 UAC／SCM 確認流程待驗收。

## P0-3 — 持久 WFP 與停止生命週期

- **是否重現：是。** 前輪 `P03_StopAttemptsEveryBoxEvenWhenFirstTerminationFails` 已保存於 `artifacts/p0-0.1.31/sandbox-red.log`。本輪 `P03_CaptureRecoveryWithdrawsDynamicPermitBeforeReplacement`，`P03-WFP-red.log`：`Expected: ["withdraw", "replace", "permit"]; Actual: ["replace"]`。
- **修正摘要／主要檔案：** 新增 `native/PersistentFirewall.h`、`Core/ISandboxFirewall.cs`、`Core/NativeSandboxFirewall.cs`，整合 Service、Desktop、解除安裝。真正呼叫 Fwpm API，持久 provider／sublayer、ALE_AUTH_CONNECT_V4/V6 和 ALE_AUTH_RECV_ACCEPT_V4/V6 的精確 AppId BLOCK，排除 loopback。相同 sublayer 動態 session PERMIT 權重較高；不使用覆蓋其他防火牆的 hard permit。正常停止／故障撤回動態 session，持久封鎖保留；移除 profile 更新路徑集合，清除／解除安裝只清專屬 GUID。
- **新測試結果：** 單元測試通過，包含恢復失敗不重新開放，Dispose 不清持久封鎖；原生 DLL 與真實 BFE 整合 fixture 建置成功。**BFE 整合未執行，不宣稱通過。**
- **未完成／範圍：** 普通 PID 綁定不套 WFP；只列 Game／Box 已知 EXE 與啟動器，未知系統路徑不涵蓋。持久 filter 在 BFE 啟動時載入，沒有 boot-time filter；既有連線 FIN／RST 不能由 ALE 新連線授權保證消失。完整要求的實機崩潰驗收仍待同意。

## P0-4 — 健康判斷、交接與故障撤回

- **是否重現：是。** 前輪 `p04Checks`：`diagnostic parser error must not mark healthy capture as stopped`；本輪交接缺口由 R8 的舊 close／start 呼叫順序重現，動態放行缺口由 P0-3 本輪 red 重現。
- **修正摘要／主要檔案：** `native/WindowSocksNative.cpp`、`native/PersistentFirewall.h`、`Service/Controller.cs`、`Service/EngineLifecycle.cs`。維持非致命錯誤不重啟；健康 capture 不重新開啟，致命 receive／send／讀取執行緒錯誤立即撤回 WFP permit。重啟前撤回，先開新 handle 再關舊，成功後才放行；服務程序退出由 BFE 回收 dynamic session。
- **新測試結果：通過。** 原生 parser／send／批次／handoff 回歸、服務撤回／恢復順序測試。實際 WFP 的規則與 session 程式碼已編譯。
- **尚未完成：** 不以假引擎結果宣稱核心切換零丟包或實機崩潰不直連。WinDivert 真實交接與 WFP 實機驗收列於下方；適用範圍與 P0-3 相同。

## 技術查證與替代方案

- [Microsoft：Filter arbitration](https://learn.microsoft.com/en-us/windows/win32/fwp/filter-arbitration)：同 sublayer 先採最高權重的終止動作，支持持久 BLOCK＋高權重動態 PERMIT。其他 sublayer 的封鎖仍可生效。
- [Microsoft：Object management](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management)：dynamic session 結束時刪除其物件，persistent 物件於 BFE 啟動載入。本 provider 不綁 serviceName，避免服務非自動啟動時政策不載入。
- [WinDivert 2.2.2 官方文件](https://github.com/basil00/WinDivert/blob/v2.2.2/doc/windivert.html)：數值較高的 priority 先處理，故交接使用不同優先權；仍需外側流量驗證。
- [SandboxieLogon](https://sandboxie-plus.github.io/sandboxie-docs/Content/SandboxieLogon.html) 有每盒虛擬使用者 SID，但預設關閉；[AccessTokenIsolation](https://sandboxie-plus.github.io/sandboxie-docs/Content/AccessTokenIsolation.html) 的 SandboxieAllGroup SID 是共用群組，不能辨識每盒。尚未證明所有遊戲／broker／AppContainer token 都包含可用的單盒 SID。因此沒有擅自啟用 SandboxieLogon，也沒有寫一條假裝覆蓋全部程序的 ALE_USER_ID 規則。替代路線是於隔離測試盒驗證每盒 token／SID及登入相容性，成功後再設計 SID-based WFP；目前採精確路徑範圍並明示缺口，不改用 VM。

## 完整檢查與建置來源

- `dotnet test tests/WindowSocks.Tests`：**427／427 通過，0 失敗、0 略過**。完整輸出 `dotnet-full.log`，TRX：`test-results/review-0.1.31.trx`。最後安裝／修復入口共用確認流程另跑 16 項恢復／更新回歸通過。
- 原生 `PacketPathChecks`：**157,636 個斷言通過**，`native-full.log`；透過 **Windows PowerShell 5.1** 執行。64,000 封包／1,000 模擬批次 19.062 ms；UDP owner lookup 4.050 µs／封包。這些不含真實驅動 I/O。
- WPF 沙盒流程通過：開窗 1,077 ms、dispatcher 回應 50 ms，`wpf-sandbox.log`。規則新增頁在忙碌／探索阻塞時開窗 248 ms、VMware 頁 235 ms、取消 96 ms，`wpf-editor.log`；正常／最小尺寸截圖在 `wpf/`。工具列／關閉／退出生命週期通過，`wpf-tray.log`。未操作實機 UI 的背景服務。
- 原生 DLL、Desktop、Setup、Maintenance、Updater 建置通過；WFP fixture 在 PowerShell 5.1 **只建置，未執行**，`WFP-fixture-build.log`。`git diff --check` 通過，Git 暫存區為空。
- **install-manifest：本輪沒有產生安裝包或新的 install-manifest，沒有可宣稱的安裝包 commit。** 建置來源是基底 HEAD `cedaa653ec67e224c912100699cb718995b7ffbf` 加未提交工作樹。沒有把舊 manifest 當成本輪證據，也未重建驗證公開 0.1.30 DLL 與 tag 的一致性。
- 原生原始碼集合 SHA-256：`570759fe287769948180e38b96f39a8a1167ccb64c7224e86b6c3abef316401b`。集合為 `native/` 與 `third_party/ProxyBridge/src/` 下排序後的 .cpp／.c／.h 路徑及各檔 SHA-256，以 UTF-8 `path hash\n` 串接再雜湊；版本產生值另為 0.1.31，未包含外部 SDK／編譯器。
- 本輪 `artifacts/native/WindowSocksNative.dll` SHA-256：`8902c1e76a5b018885e945497153b67a082758b3461891c743297d150bf45f9c`。完整逐檔清單與 hash 保存在 `artifacts/review-0.1.31/build-evidence.json`，不是已發布套件。

## 等待使用者同意的實機驗收清單（未執行）

1. 使用專用測試機、測試服務、合成 EXE 及受控非 loopback TCP endpoint；不借用正在執行的 A／B、原代理或既有沙盒。以管理員執行 `scripts/Test-SandboxFirewall.ps1 -RunApproved -Endpoint <受控IPv4> -Port <TCP埠>`。預設不傳該參數只編譯。
2. 該 fixture 使用測試 provider `eb758a61-7b8d-46d7-a4ef-918759818171`／sublayer `eb758a62-7b8d-46d7-a4ef-918759818171`，與產品的 `783a5e91-5835-43ad-869e-6fcd5a310201`／`783a5e92-5835-43ad-869e-6fcd5a310201` 分離。測試實際 BLOCK、PERMIT、loopback、主動撤回、獨立 lease 程序被終止後由 BFE 撤回與持久封鎖保留，結束清理。清理失敗會回報 GUID，不能刪其他供應者規則。
3. 額外人工驗證 IPv6、UDP、連入層、BFE／整機重啟、服務不啟動期間、新連線及既有 FIN／RST；從主機外側擷取封包。這些不包含在上述 IPv4 TCP fixture 的通過宣稱中。
4. 專用測試服務注入 receive 故障與新 handle 建立失敗；確認先撤回 permit、新 handle 先於舊 handle 關閉、開新失敗保持攔截、健康不重啟。連續 30 分鐘比較無關主機流量的延遲／逾時。
5. Sandboxie 1.18.8／driver 5.73.8 核驗 ABI、服務代建程序、Box 路徑 EXE、未知系統路徑、故意失敗查詢與恢復；確認無關主機流量不封鎖，已知成員顯示失效並暫停。未知路徑結果單獨回報，不以 Game 目錄成功替代。
6. 在核准的測試環境以 SYSTEM 讀 PID 4 與 PPL 的建立時間，並測 SMB／445；不將本輪合成 birth=0 測試當成已做此步驟。
7. 測試停止失敗、卡住 Gate、舊版命令不相容的退出／安裝／更新；確認兩階段提示、PID 重用不誤殺、拒絕時不 SCM stop、同意後停止未完成不覆寫。確認清除需先移除實例、解除安裝只移除本產品 filters。
8. 在新啟動的遊戲程序驗證無額外 xa sk5 Job、反向啟動與重開保持固定節點、全局使用不同節點可並存。遊戲實測與登入另行取得同意。

0.1.30 → 0.1.31 第一次升級請使用官方安裝程式，避開舊更新路徑。更新視窗已明示已安裝版離線更新／回復、可攜版同時更新服務改由安裝程式處理，並加入下載頁。其他 P1 等待使用者確認。
