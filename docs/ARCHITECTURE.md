# 架構與生命週期

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

0.1.1：Desktop 以 ShellExecute `runas` 啟動 Maintenance，UAC 保留給人操作；傳遞原登入者 SID，以支援以另一管理員帳戶核准的情況。Maintenance 與 Setup 共用程式碼。manifest 檢查及完整 staging 在停止服務前執行；驅動來自固定官方 HTTPS 發布網址，限制重新導向主機、解壓路徑、大小與 x64 架構，WinVerifyTrust 驗證 SYS 簽章。GitHub 這個舊發布沒有提供 digest，沒有將本機計算 SHA-256 冒稱為官方 hash 驗證；DLL 來源依官方 HTTPS 傳輸，manifest 僅提供套件完整性，不取代程式碼簽章。測試載入使用 `false` 篩選器，不擷取封包。安全性封鎖、UAC 取消與服務錯誤皆會回報失敗，不會變更安全性政策或停止其他軟體共用的 WinDivert 服務。

`Build.ps1` 預設依序編譯原生 DLL、執行測試、產生 WPF／服務／工具的本機可執行目錄 `artifacts/local`。`Prepare-Payload.ps1` 共用準備主程式入口、說明、授權與完整性清單；本機建置不呼叫壓縮或安裝器建置。只有要求發布時使用 `Build.ps1 -Package` 或 `Package.ps1 -PortableOnly` 產生可攜 ZIP。獨立執行完整的 `Package.ps1` 仍可產生原始碼與安裝器。安裝器寫入 Program Files、建立 LocalSystem 服務與受保護的 ProgramData 設定資料夾。解除安裝保留設定，移除服務／專屬防火牆規則。安裝升級尚未實作交易式回復；升級前應備份並關閉受管程式。
