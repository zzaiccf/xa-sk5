# 架構與生命週期

## 0.1.31 複審修正與驗收邊界

本輪未發布、未安裝；實作與隔離測試見 [RELEASE-VALIDATION-0.1.31](RELEASE-VALIDATION-0.1.31.md)。沒有操作實機服務、遊戲、Sandboxie 設定或代理。

第二輪 N1–N4：PID query 的未知（-1）與裝置／ABI 失敗（-2）分離，封包執行緒只設驅動檢查旗標；單一 PID 500 ms 重試不改全域健康。控制工作每 5 秒查 GET_VERSION，已核驗驅動連續三次或 15 秒失敗才轉 -1，恢復轉 1；控制工作在等待 broker 前撤回 WFP，重建政策後放行，不改持久 Enabled。全新未核驗驅動不享有寬限。WFP Replace 在 BLOCK 交易後建立全部映像的新 dynamic lease，再關閉舊 lease；建置失敗保留舊放行，關閉失敗保留 handle 供重試。映像掃描（Arm、Save、Remove、規則刪除及初始化）在 Gate 外；回鎖核對世代，初始化完成前暫停。owned 列舉使用 provider＋四個 ALE layer 範本及全 action mask，另驗 sublayer；不掃全機 filter。分階段實機工具僅完成編譯，見 [驗收清單](LIVE-ACCEPTANCE-0.1.31.md)。

- 全局與沙盒只在同一節點互斥，不做模式互斥。全局啟用前拒絕已知重複出口，再以候選線路檢測結果核對；執行中重複只封鎖其他綁定，保留既有全局路由。
- 沙盒原生規則 Reserved=13：父系接續（bit 0）、不建立 Job（bit 2）、沙盒身分失效時封鎖（bit 3）。PID＋建立時間釘選及父系接續保留。`@box:` 只對有效票據／存活成員生效；Game、Box、來源路徑保護保留。
- 原生身分查詢先核對 Sandboxie Plus 1.18.8／driver 5.73.8／ABI 0x57230。未知 ABI 拒絕 Arm，查詢失敗不封鎖無關主機程序。已知身分與祖先鏈、受保護映像維持封鎖；撤回已知映像的 WFP 動態放行。目錄外、服務代建且尚未核驗的未知程序仍是無法保證的邊界。
- box 清單在政策更新時建立不可變快照；4096 筆 PID 身分快取逐筆淘汰。PID 4／未受管 PID 的 birth=0 不因沙盒存在而封鎖。受管 PID 身分無法確認時維持封鎖。
- 250 ms 沙盒觀察僅在有效路由狀態有變化時套用政策。Arm 在 Gate 外讀 INI 專屬區段（1 秒等待上限），不再執行三個 SbieIni 查詢。停止在 Gate 外操作 broker，返回後核對世代；過程中拒絕新啟動。snapshot 回傳已序列化、原子發布的快照，不等 Gate；寫入完成或監測週期更新資料。
- 暫時 broker 失敗保留仍存活的已核驗成員，持續失敗封鎖。correlation id、部分回覆拒絕及 500 ms～30 秒共同退避保留。
- 正常停止先逐一結束沙盒，失敗保留封鎖。明確的強制停止先釘選核對並結束已知成員，列出未關閉與未知範圍，第二次確認才 SCM stop；沒有靜默後備。Desktop、Setup、Updater、Maintenance 共用 Core 結束流程。
- 擷取健康不重啟；故障立即開啟不同 priority 的新 handle，再 shutdown／join 舊讀取執行緒、交換 send handle 並關舊 handle。WinDivert 數值較高的 priority 先攔截；使用 100／101 輪替。失敗保留舊 handle；持續故障且有存活沙盒時才排程結束沙盒。
- 已關閉 TCP tuple／alias 的 240 秒隔離使用獨立輕量表。完整 Flow 在清理週期移除；活流以原子計數限制 2048，不再用 32768 總筆數阻擋新流。沒有活流時不取得 flowMutex；隔離查詢使用獨立共享鎖。prune 的系統表讀取、UDP比對與連線結束在長鎖外執行。
- `PersistentFirewall.h` 使用實際 Fwpm API：專屬持久 provider／sublayer，四個 ALE CONNECT／RECV_ACCEPT V4/V6 layer 的 AppId 精確路徑 BLOCK（權重 1、排除 loopback）；健康期間以 dynamic session 添加相同條件 PERMIT（權重 2）。一般 soft permit 不覆蓋其他防火牆的拒絕。服務崩潰由 BFE 回收 session；捕獲故障、重啟與退出前主動撤回。路徑變更在交易內替換 BLOCK；正常退出不刪持久封鎖。
- WFP 管理僅由正式服務的無參數建構式注入；隔離 Controller 測試不接觸 BFE。移除實例更新其路徑集合；UI 清除須先停止並移除全部沙盒；解除安裝清理專屬 provider。WFP 整合測試使用不同 GUID，預設只編譯，必須明確 opt-in 且具有管理員權限。
- 限制：精確 AppId 不涵蓋未列出的系統 EXE、共享 DNS、任意新 EXE；一般 PID 綁定不套 WFP。已授權連線的 FIN／RST 不能由 ALE 持久政策宣稱完全防止。持久物件在 BFE 啟動載入，不等於 TCP/IP 啟動至 BFE 之前的 boot-time 保護。SandboxieLogon 的每盒 SID 尚未實測，不自動改動既有設定。
- 提權更新保留受保護 helper／job、官方來源重取與獨占解壓。已安裝版離線更新／回復、可攜版更新服務及 0.1.30 第一次升級，改用官方安裝程式。UI 提供下載頁按鈕，未新增發布者簽章。

## 各線路分配數量（0.1.26 本機測試）

自動規則以 `ProxyCapacities` 保存節點 ID 對應的 0–10 個名額；沒有覆寫的節點沿用 `PerProxy`（新規則為 1），讓舊規則及後續匯入節點維持可預期的預設。零只停止新增分配。`AutoProxyPlanner` 計入所有既有綁定並保留獨占限制及總管理上限十個，降低名額不移動或中斷既有窗口，待名額釋放才重新分配。節點刪除／清空同步移除自訂名額。

規則編輯器顯示各線路已分配數與可分配數，支援單條修改及勾選多條後批量套用；無效輸入不修改任何批量項目。模擬器預設依啟動順序自動分配，原有固定配置在進階模式載入。遊戲／程式沿用相同分配器；僅修改名額時保留原有匹配範圍資料。手動窗口分配及 VMware 的配置不在此變更範圍。

Snapshot 以 `SupportsProxyCapacities` 宣告能力，介面在送出自訂名額前檢查；舊服務或離線時明確要求更新，不讓舊服務忽略新欄位。服務在儲存前驗證名額範圍及節點是否仍存在。隔離測試涵蓋不同名額／零／降低名額／舊 JSON、保存後重啟與刪除清理，WPF 測試涵蓋單條及批量輸入、舊服務拒絕與視窗布局，不改動實機服務或遊戲。

## 統一遊戲／程式規則（0.1.25 本機測試）

新增規則僅保留模擬器、遊戲／程式、VMware 三個目標類型。`ProgramRuleInput` 將同一輸入欄位解析為確切檔名或完整路徑，支援 Windows 複製路徑的外層雙引號；帶路徑分隔符或磁碟機冒號的輸入必須通過既有完整路徑驗證，不退回寬鬆的同名匹配。預設維持 Process＋IncludeChildren 的遊戲接續。

「進階：指定登入後的遊戲」明確勾選才使用原有 GameGroup 格式，要求兩個不同的完整路徑，維持只接管指定登入器與遊戲的行為。取消勾選時保留編輯框文字供切換，但儲存的 Process 不含舊 ClientPath。既有三種規則從同一編輯入口載入，GameGroup 自動展開進階欄位；背景服務、配置格式與路由政策沿用既有實作。

0.1.26 在編輯器的 PreviewDragEnter／PreviewDragOver／PreviewDrop 接收 Explorer FileDrop，先於 TextBox 預設處理；只接受單個 .exe／.bin／.lnk，游標檢查不進行磁碟或 COM 存取。`ProgramFileImport` 在背景解析捷徑目標，沿用唯讀 `WindowsShortcuts.Target`，不啟動目標、不執行參數、不保存捷徑。檢查實際檔案及完整路徑後才寫回欄位，失敗保留原輸入；窗口關閉後忽略結果。拖到進階 ClientPath 只更新遊戲欄位，其餘位置帶入登入器並切到遊戲／程式模式。檔案選擇器也走同一解析路徑。

## 全局代理時長（0.1.24）

`GlobalProxySessionTracker` 由背景服務持有，只保存在記憶體。`UpdateSessions` 依全局啟用、核心執行與服務既有健康判定同步目前節點，並透過 Snapshot 的可空 `GlobalProxyConnectedAt` 傳回起始時間。持續使用同一節點不重設；政策停用、健康封鎖、換節點、核心重啟或服務重啟後重新計時。重新檢測時仍適用原有健康寬限政策。

介面沿用一秒 DispatcherTimer 更新文字，不增加 RPC 或重新建立整個狀態卡。時長使用服務起始時間，負值歸零並支援天數；服務斷線、未生效或舊服務缺少起始時間時不繼續累加。這是全局政策生效時長，不代表遊戲登入或單一連線存續時間。

## 視窗縮小與退出（0.1.24）

X／Alt+F4／工作列「關閉視窗」取消真正關閉並改為 Minimized，保留工作列圖示、系統匣、輪詢及代理。StateChanged 在最小化時要求 Windows 顯示約三秒通知，說明背景運作與退出入口；同一縮小狀態三秒內去重，還原後的新操作重新提示。實際通知顯示時間依 Windows 通知設定。

工作列 JumpTask 與系統匣選單均使用「退出」。JumpTask 以目前完整 EXE 路徑及 `--exit` 啟動，第二個程序透過同使用者單一視窗的 `.Exit` 事件要求主視窗退出；沒有既有介面時不建立窗口或啟動服務。一般啟動仍使用 `.Activate` 還原。

退出獨立於 Closing：先禁止重複請求、取消尚未完成的啟動並等待啟動工作落定，再要求背景服務停止。成功才設定 quitting 並清理視窗／系統匣；停止失敗還原可操作的視窗供重試。只取消啟動請求，不提前取消整個視窗 lifetime，避免失敗後無法繼續操作。更新器已完成交接的 quitting 路徑保留，不再次停止服務。隔離 WPF 測試使用假服務及通知回呼，沒有改寫使用者 JumpList 或操作真實服務。

介面只以目前 Windows 使用者身分執行 ldconsole，背景服務不執行使用者指定的 EXE。服務透過 WMI 與 vbox UUID 重新辨識已知雷電程序，驗證 PID 建立時間及所有者 SID。透過 Named Pipe 傳送有長度上限的 JSON；ACL 限制安裝帳戶、SYSTEM 與管理員，拒絕網路登入 SID。

`ProxyProfile` 包含代理憑證，寫入磁碟前整份 DPAPI 加密；`InstanceBinding` 保存穩定實例鍵與期望狀態；`RuntimeStatus` 為當下核心狀態。Snapshot 不傳密碼。介面每五秒讀取一次快照；本版是輪詢介面，尚無長連線推送訂閱。

## 封包路徑

0.1.23 移除原生封包執行緒的 Toolhelp 全系統快照，以動態查詢 `NtQueryInformationProcess(ProcessBasicInformation)` 取得已開啟程序物件的父 PID，核對回傳長度、PID、建立時間及存活狀態，再沿最多 32 層父系驗證。Job Object、已知成員與父程序生命週期後備保留。不可將只有 inventory 或未設定 Reserved bit 0 的效能測試當作 IncludeChildren 的驗證；新增無快取父系壓力與無 Job 的真實子程序回歸，以及實際 WinDivert 的開關對照。

服務程序的原生 prune 執行緒每秒寫入 `diagnostics/packet-*.jsonl`，記錄累計封包／位元組、owner／分類／state lock／父系／送回核心耗時、佇列等待及未知／溢出／解析錯誤。8 MiB 分段，保留最近 16 份，失敗不阻斷轉送；沒有 IP、封包內容或帳密。分類最大值是程序啟動後的累計最大值，計數不可直接解讀為網路丟包率。公開驗證範圍及限制見 `RELEASE-VALIDATION-0.1.23.md`。

- 服務開啟 WinDivert NETWORK outbound，查詢 Windows TCP owner 完整端點／UDP owner module 表。
- 無法唯一歸屬或受管 IPv6 丟棄；已知未受管程序放行。未知 PID／分片採保守封鎖，可能影響未選取程式，待實機改善。
- TCP SYN 以來源 alias 改寫到本地 listener，listener 建立 SOCKS CONNECT；返程封包還原來源、目的及埠。只接受由 flow map 確認的對端，不提供可被任意使用的開放代理。
- UDP 每個流持有控制 TCP 與 relay UDP socket，控制連線中斷即關閉關聯；驗證 relay 及回覆目的端。UDP socket 建立時間不同即銷毀舊關聯，以區分埠重用。
- DNS TCP／UDP 53 經同代理轉至 1.1.1.1，回覆還原原始 DNS 位址。沒有本機 DNS 直連回退。
- 流量受限於 2048 flows、256 workers、每流 128 個待傳 UDP datagrams；UDP 閒置 60 秒、TCP 閒置 30 分鐘回收。

## 規則狀態

0.1.2 的 `bindBatch` 先用 AssignmentPlanner 完整檢查容量、代理存在、共用／獨占衝突及重複程序，再驗證實際 PID 身分與所有者。一次替換全批新規則，全部新目標初始為封鎖，再逐個檢測啟用；啟用失敗不會自動解除封鎖。明確共用相同 ProxyId 不視為獨立出口，不與自己重複出口衝突；不同 ProxyId 出口相同仍拒絕獨立分流。

0.1.9 一般程序可明確選擇 IncludeChildren，預設 false。原生 Reserved bit 0 在第一個子程序封包驗證父子 PID＋建立時間並記住歸屬；服務以 Core_GetDescendants 及程序快照合併同一使用者的活成員，將已觀察身分保存於 Descendants。父程序退出後保留活子程序規則，服務重啟重新驗證身分。最後成員退出後釋放名額；不以 PID 單獨繼承。重疊程序樹與其他綁定衝突時保持封鎖。獨立服務、未觀察即退出的中介程序不一定能辨識，應使用全局或直接選網路程序。

GlobalProxy 保存所選代理、啟用狀態、區網直連選項與啟用前解析的 IPv4 位址。Core_ReplacePolicyEx 一次替換指定規則、路徑保護、全局預設與區網選項；全局 state=0 停用、1 代理、2 封鎖。指定程序規則與 guard 優先，未知 PID／身分不明封鎖；服務本身永遠排除。更改或停用全局時立即關閉不再符合規則的轉送。全局涵蓋 Windows 本機 NETWORK outbound TCP／UDP，沒有宣稱涵蓋 NETWORK_FORWARD 或建立 TUN 網卡。

全局上游使用保存的數字位址，健康檢測的 STUN 主機使用 RFC 1928 ATYP=3 的 UDP 封裝交給代理解析。如此服務重啟、尚未通過檢測而封鎖系統 DNS 時仍能完成首次健康檢測。代理位址改變需重新套用設定；UDP 網域封裝不受上游支援時檢測失敗，沒有直連回退。健康寬限必須有三分鐘內的成功紀錄，首次失敗絕不放行。

離線分配寫入使用者 DPAPI 設定，Enabled 始終為 false；重開仍只呈現草稿。移交時先匯入代理，按唯一端點＋帳號對應服務 ID，再一次送出分配並清除成功移交的本地草稿；對應歧義或失效程序保留草稿並要求重新選取。篩選只用於目前候選清單，不是常駐程序名稱規則。

檢測新增獨立連線時間、TCP 查出口時間與 UDP STUN 往返。所在地查詢獨立於健康狀態，使用同一 SOCKS 連線路徑 HTTPS 查詢明確的出口 IP，限制 6 秒、16 KiB 及不重新導向。解析確認回覆 IP 相同，再以 Windows NLS 轉為繁體；失敗顯示未取得，不回退到直接連線。只快取所在地，服務不沿用磁碟上的舊健康結果；介面保留舊檢測紀錄並顯示時間。

未綁定 → 綁定且封鎖 → 檢測通過 → 啟用。暫停與檢測失敗回到封鎖；解除綁定恢復原有連線。改綁先封鎖、關閉舊流，再寫入新代理。啟用前測試 TCP／UDP 並拒絕重複出口；健康檢查通常每 60 秒，失敗後指數退避、最高 60 秒，不輪換代理。

服務重開後先重新辨識及檢測，不能使用上次的健康快取。核心故障嘗試重啟，但沒有 WFP 層級持久 kill switch；服務／驅動崩潰不保證斷線隔離。一般程序重新啟動需重新選取，系統共享 DNS 服務不在一般程序的 DNS 保證範圍內。

## 批次操作（0.1.22）

`SupportsBulkActions` 表示服務支援清除節點及關閉個別代理程序。`clearProxyPool` 在組態鎖內一次檢查所有綁定、固定模擬器配置、待套用配置、VMware 與全局節點使用；任一節點仍被引用就整批拒絕。先成功寫入空池再更新記憶體，作廢檢測世代；遲到的健康檢測不能復原已刪除節點。離線清除使用相同使用狀態檢查。

`prepareCloseProxyProcesses` 只從個別綁定及使用代理的 VMware 執行紀錄建立目標，沒有以全局流量列舉關閉名單。回傳兩分鐘內有效的一次性 token 及清單，實際執行時重新比對同一綁定中的同一程序身分；期間新增的其他根程序不加入，既有根程序的新子程序仍會涵蓋。每個程序以 PID＋建立時間及所有者 SID 驗證，先開啟並保留程序控制代碼，再透過相同控制代碼結束，避免 PID 重用造成誤關。代理介面與背景元件受保護，未知或無權限程序回報未關閉。保留節點、規則與全局設定，單次最多 1024 個程序，以保留 1 MiB RPC 回覆預算。

## 發布

### 桌面沙盒（0.1.30）

`SandboxInstanceProfile` 保存實例 ID、專屬沙盒名稱、原始登入器、副本與工作目錄、代理 ID 及相容選項。固定綁定在停止時仍保留，避免其他自動規則取用該線路。大型複製在背景執行，可取消並接續；完成的副本不會因再次保存設定而覆寫遊戲更新。Sandboxie 設定變更先保存該專屬區段，再以官方 SbieIni 命令套用及讀回。

啟動時服務先保留路由與目錄保護，再交付限時一次性票據。使用者身分的沙盒輔助程序透過受限命名管道登記；服務核對管道 PID、建立時間、沙盒名稱及安裝者 SID。由官方程序 API 辨識全部成員，包含服務代建程序；不繼承共用 SbieSvc 的線路。核驗後，沙盒內使用普通 socket 測量 TCP／UDP 出口，與該線路最近成功結果一致後才建立登入器。直接 DNS 結果另列，不能推論共享 Windows DNS 已分流。

服務透過受保護安裝目錄的長駐 `sandbox-broker` 子程序呼叫官方 SbieDll API；資料僅經繼承的標準輸入／輸出傳遞，沒有對外控制埠。這避免 Sandboxie 重新載入設定時，把載入 SbieDll 的 xa sk5 Windows 服務列為需要重啟的主機服務。0.1.31 的查詢容錯與首包查詢策略以上方 P0 章節為準，不再於單次失敗清空可信成員。受管目錄的啟動保護在程序擁有者無法直接查詢時仍生效。

`SupportsSandboxInstances` 與原生 `Core_SupportsSandboxGuards` 用於拒絕不相容的舊元件。快照另帶沙盒狀態及沙盒內實測出口；界面不把建立登入器當成客戶端登入成功。正常停止先針對已核對的沙盒名稱及使用者工作階段結束成員，再關閉核心。TCP listener 採可取消的輪詢，避免停止時等待阻塞的 accept；核心階段與錯誤碼記錄於診斷目錄的 `core-lifecycle.log`。

操作與相容性範圍見 [沙盒操作說明](SANDBOX-GUIDE.md)。個別使用者的遊戲、帳號、代理與機器診斷紀錄不包含在發布原始碼或套件。

`SupportsSandboxExperience` 表示服務支援 0.1.30 引導準備流程。`IsolateDeviceIdentity` 為 nullable：舊版 null 保留既有人工相容設定；新配置依實例 ID 固定產生識別，保存 true／false 明確套用或停用。每個受管設定先讀取持久 INI 的專屬區段，逐項寫入及核對，再 reload 並確認執行中設定；失敗或取消以獨立回復時限還原。全域繼承值不會被當成區段原值寫回。

`SandboxLaunchQueue` 對同一介面一次只允許一個啟動佇列，依 ID 去重並保留順序，上一個完成後才倒數間隔。各實例仍經原本票據、代理及出口核驗；取消只處理未完成工作。設定頁即時用已呈現的快照開窗，真正套用前另讀新狀態。程序探索、環境辨識及複製不在 WPF dispatcher 執行。

VMware 清單探索使用 single-flight 背景查詢，逾時不產生更多卡住的 worker，也不占用 Controller.Gate。未執行 VMware 時略過 WMI。沙盒定期成員探索在主要鎖外執行，套用時重新核對 PID 建立時間；新啟動登記提升世代，舊探索結果不得覆蓋剛核驗的成員。管道接受迴圈也與要求的同步前段分離。

0.1.1：Desktop 以 ShellExecute `runas` 啟動 Maintenance，UAC 保留給人操作；傳遞原登入者 SID，以支援以另一管理員帳戶核准的情況。Maintenance 與 Setup 共用程式碼。manifest 檢查及完整 staging 在停止服務前執行；驅動來自固定官方 HTTPS 發布網址，限制重新導向主機、解壓路徑、大小與 x64 架構，WinVerifyTrust 驗證 SYS 簽章。GitHub 這個舊發布沒有提供 digest，沒有將本機計算 SHA-256 冒稱為官方 hash 驗證；DLL 來源依官方 HTTPS 傳輸，manifest 僅提供套件完整性，不取代程式碼簽章。測試載入使用 `false` 篩選器，不擷取封包。安全性封鎖、UAC 取消與服務錯誤皆會回報失敗，不會變更安全性政策或停止其他軟體共用的 WinDivert 服務。

`Build.ps1` 預設依序編譯原生 DLL、執行測試、產生 WPF／服務／工具的本機可執行目錄 `artifacts/local`。`Prepare-Payload.ps1` 共用準備主程式入口、說明、授權與完整性清單；本機建置不呼叫壓縮或安裝器建置。只有要求發布時使用 `Build.ps1 -Package` 或 `Package.ps1 -PortableOnly` 產生可攜 ZIP。獨立執行完整的 `Package.ps1` 仍可產生原始碼與安裝器。安裝器寫入 Program Files、建立 LocalSystem 服務與受保護的 ProgramData 設定資料夾。解除安裝保留設定，移除服務／專屬防火牆規則。安裝升級尚未實作交易式回復；升級前應備份並關閉受管程式。
