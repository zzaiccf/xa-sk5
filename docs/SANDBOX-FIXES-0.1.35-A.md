# 沙盒修正 0.1.35：A 段查證與根因分析

日期：2026-10-31，台北時間。本文件保存 A 段查證；使用者已核對 A 並核准 B。最新實作／測試見 [B 段驗證](RELEASE-VALIDATION-0.1.35.md)。C 尚未核准。

使用者確認發生問題的另一台電腦使用 **GitHub 正式版 0.1.32**；尚未收到該機診斷檔、錯誤堆疊或檔案存取追蹤。

原 A 段使用 `codex/sandbox-fixes-0.1.34`、基底 `c8f05202fad21cbcba661b455e09b575026a27c1`。B 改依使用者指示從 `d833482856d11456f2e5dfe1709b6655e0b12e93` 建立 `codex/sandbox-fixes-0.1.35`，沒有基底衝突，保留 0.1.33 移除功能及 0.1.34 TCP 修正。沿用原獨立工作樹位置，並將分析改名為 0.1.35。

A 段只讀產品／測試／官方原始碼並建立本分析與研究紀錄，當時來源為 0.1.33。B 目標改為 **0.1.35**，現有 0.1.32／0.1.33／0.1.34 交付全部保留。各段狀態以 B 驗證文件為準。

## 查證依據與可信範圍

- Sandboxie tag `v1.18.8` 已透過 GitHub ref API 核對至 commit `bde0890108a563f5d72eb8e57085be2b37177218`；檔案 blob SHA 與該 ref 的 tree 相符。以下官方連結固定到此 commit。
- GitHub 程式碼搜尋只用來定位檔名，定位後重新讀取固定 tag 的檔案。網站顯示行號可能壓縮空白行，報告引用 GitHub 原始檔行號。
- 0.1.32 與基底 0.1.33 的 Installer、Runtime、Tools bootstrap 在相關程式路徑相同；0.1.33 新增移除保護，並未修正本次初始化／監看問題。
- **程式碼確證**不等於**已在另一台重現**。A 沒有新增／執行測試，紅→綠、完整回歸與安裝包均留待 B／C。

## 問題 1：自動安裝後找不到 ini

**結論：產品把「尚未首次寫設定」當成安裝失敗，並過早顯示可準備環境；這條失敗路徑已由程式碼確認。**

| 層次 | 確認內容 |
|---|---|
| 官方安裝程式 | 只有 portable 模式複製 Sandboxie.ini；一般安裝會安裝並啟動 SbieSvc。silent 且 OPEN_AGENT=0 不會開 SandMan。[Installer](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Installer/Sandboxie-Plus.iss#L74) |
| 驅動 | ini 不存在時繼續載入 Templates.ini 預設值，沒有在這個讀取流程建立 ini。[Conf_Read](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/drv/conf.c#L294) |
| SbieSvc | 初始化設定服務只嘗試鎖定已有設定；真正設定修改時才載入、保存。CIniFile.LoadIni 遇 ERROR_FILE_NOT_FOUND 接受空模型，SaveIni 使用 OPEN_ALWAYS，可建立不存在的檔案；缺少父目錄、權限錯誤不能當成「沒有設定」。[LoadIni](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/common/ini.cpp#L48)、[SaveIni](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/common/ini.cpp#L457) |
| SandMan | 連線後會寫入使用者的 agent 設定，並在沒有預設盒時 CreateBox；這些設定操作可觸發服務建立 ini。因此手動安裝後自動開 SandMan，與本程式的 silent 流程結果可能不同。[SandMan](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/SandboxiePlus/SandMan/SandMan.cpp#L2868) |
| 本產品 | SandboxieInstaller.Inspect 只查固定位置檔案及版本；Configure 先要求 ini 存在才能備份，尚未呼叫官方寫入就拋錯。ValidatePersistedSettings／SandboxBoxRemoval 也只猜兩個位置。 |

**SbieIni set 能否建立：能，但有條件。** 普通 set 經 SbieDll_UpdateConf → SbieSvc → CIniFile.LoadIni／SaveIni；有效目錄與服務可用時，不要求 ini 預先存在。不是 SbieIni 自己直接手寫檔案，也不必為此打開 SandMan。實際乾淨機仍未驗證。

**設定路徑不能只查兩處。** 驅動先讀 `HKLM\SYSTEM\CurrentControlSet\Services\SbieDrv\IniPath`；自訂開啟失敗後嘗試安裝目錄，該處是 NAME_NOT_FOUND 才轉至 Windows 目錄。驅動 resident `IniLocation` 回傳實際自訂路徑或 H／W；服務 GetIniPath 依此選擇寫入位置。登錄設定不等於此次實際載入位置。[驅動查找](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/drv/conf.c#L252)、[服務 GetIniPath](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/svc/sbieiniserver.cpp#L1396)。

B 計畫：區分已安裝／服務未啟動／設定待初始化／可用；缺檔時記錄「無既有設定可備份」，不阻擋官方初始化；寫入後重新解析有效路徑，短暫讀取並核對實體與 resident 設定。不可用 File.Exists=false 掩蓋存取拒絕。不得因新增環境檢查而讓自動安裝流程陷入「已安裝但不能安裝／不能初始化」的死路。

## 問題 2：SBIE1402 (none) [C0000043]

**確認錯誤意義，尚未確認該機的衝突持有者。**

- `C0000043` 是 STATUS_SHARING_VIOLATION。[Microsoft 定義](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)
- Conf_Read 的三個 ini 開檔均為 FILE_GENERIC_READ + FILE_SHARE_READ；開檔回傳非「不存在」錯誤時，先把行號設為 `(none)` 再記錄 MSG_CONF_READ。此時尚未解析內容，因此不能解讀成某行語法錯誤。[conf.c](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/drv/conf.c#L252)
- SbieSvc 的 SaveIni 用 FILE_GENERIC_WRITE、分享 Read／Write／Delete、OPEN_ALWAYS 原地寫入；共享衝突時最多重試 10 次、每次 100 ms，結束時關 handle。RefreshConf 隨後再要求驅動 reload。服務的單次設定請求有 critical section，但不代表跨程序整批設定是交易。[SaveIni](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/common/ini.cpp#L457)、[RefreshConf](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/svc/sbieiniserver.cpp#L1279)
- 本程式 Configure 的備份、逐鍵讀回、最終讀回，以及 SandboxRemoval.ReadInstalled 使用 File.ReadAllLinesAsync；.NET 10 內部採 FileShare.Read，可能阻擋寫入。ValidatePersistedSettings 已使用 ReadWrite | Delete，不能重複宣稱這處尚未修。[.NET 原始碼](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/File.cs#L1078)
- **單純 Read + Share.Read 的本程式 reader，不會直接阻擋同樣只讀的驅動。** 它會和 writer 衝突；驅動的 Share.Read 則不能與仍開著的寫入 handle 共存。不能把這次 SBIE1402 直接歸罪給某一個 ReadAllLinesAsync。SandMan／外部編輯器、其他 reload 與設定寫入的實際重疊，須由時間線追蹤證明。[Windows 分享規則](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)

**「整批只 reload 一次」需調整設計，不能只數本程式的 reload callback：**

1. 現有 SandboxSettingsTransaction 已是每批最後一次明確呼叫 Start /reload；但每次 SbieIni set／append 都會在 SbieDll_UpdateConf 設定 `refresh=TRUE`，服務每次保存並 reload。YongOrigin 多條 MAC 設定會放大次數。[callsvc.c](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/core/dll/callsvc.c#L873)
2. SbieIni 1.18.8 沒有可用的批次 defer-reload CLI；`/drv` 只改 resident 設定，不能替代持久保存。普通更新對非密碼錯誤仍可能 return 0；不能只看退出碼。[update.c](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/apps/ini/update.c#L115)
3. 官方 API 支援 `refresh=false` 後 commit，但服務共用 cache，其他 writer 可提前 flush。較適合本需求的提案是使用官方已實作的**單一 section 更新**：保存並合併本盒原始 section，經官方設定服務一次提交，服務負責寫檔與一次 reload；不自行寫整份 ini，不改其他 section。[官方 section 更新](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/Sandboxie/common/ini.cpp#L189)、[官方 UI 用例](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/SandboxiePlus/QSbieAPI/Sandboxie/SbieIni.cpp#L348)
4. **B 已決定保留 CLI：** 使用者核准維持 SbieIni.exe，不要求整批只有一次實際 reload。本輪不實作單一 section 更新 API，改以共用短暫 reader、程式內串行閘、磁碟／resident 核驗及有限次重試處理。不能宣稱可阻止 SandMan 或其他程序同時寫入。

**後續 section API 提案，未實作：** 需固定官方版本及 API/訊息 ABI，先核對函式簽章、呼叫慣例、UTF-16 長度單位、結構大小／對齊及變長資料；在獨立 helper 隔離呼叫，不載入路由服務。風險包括其他 writer 在讀取與提交之間改同一 section、共用服務 cache 的 flush 時點、部分失敗後覆蓋未知鍵。驗證應在可還原測試機對照官方 UI，涵蓋未知鍵、多值、Unicode、取消、權限、SandMan 同時寫入、driver reload 失敗及回復，只改本盒、不合併覆寫其他 section。須先提出具體協定與實測方案再核准。

B 其餘計畫：共用短暫 reader、ReadWrite | Delete、關檔後才呼叫工具；設定寫入串行、穩定讀回與有限退避；只重試分享衝突／暫時不一致，權限及非法路徑清楚回報；不關閉通知。回復只處理本實例被修改的設定，新 ini 失敗時不能刪除整份檔案。沿用服務隔離 broker，不能把 SbieDll 重新載入正式路由服務來解析路徑。

## 問題 3：右鍵選取與啟動範圍

**根因已確認。** MainWindow.xaml 的 RuleGrid 是 Single；右鍵處理無條件重設 SelectedItem；ContextStartSandboxGroup 依 SourceLauncherPath 擴大為同遊戲全部實例，與選取不一致。刷新 ItemsSource 也只還原單一 key，僅把 SelectionMode 改成 Extended 仍會掉多選。

SandboxLaunchQueue 的 launch 回傳 Task，無法知道「本次新開」或「已執行略過」，所以兩種情況共用訊息。StartRuleGroupClick 及工具列按名稱排序；選取批量應依 DataGrid.Items 顯示順序建立固定 ID 快照，而非依點選順序或名稱另排。

B 範圍：Ctrl／Shift 多選、已選範圍內右鍵保留／範圍外改單選、刷新按 keys 還原；單／多／混選選單；移除舊的同遊戲全部項；編輯、刪除、雙擊等入口均限制一列；typed outcome 區分啟動器新開與已在執行；工具列改名但保留原有全選行為。

## 問題 4：單開 A 後再批量出現 InvalidOperationException

**確認兩個缺陷，尚未查明該機首次拋出的具體原因。**

- Tools/Program.cs 捕捉 bootstrap 全生命週期的例外，只回傳類型；PipeTransport 把服務 Reply.Error 包成 InvalidOperationException，原始訊息被丟掉。
- SandboxBootstrap.Run 在 report Started=true 後繼續每秒 refreshSandboxes；監看沒有獨立的重試邊界。之後任何例外均落到外層 catch，再送 Started=false。ReportSandboxLaunch 只驗證 helper 身分，沒有「同 helper 成功不可倒退」保護，因而 PauseSandbox，把已在玩的實例變成持久停用。
- 另有取消競態：Desktop.StartSandbox 取得 ticket 之後的 catch 一律 pause／terminate。若遊戲啟動器已開啟且服務已接受成功、桌面尚未讀到結果時關閉啟動視窗，取消也可能進此清理路徑。現有 queue 測試只證明等待下一項時取消，不涵蓋此交接時刻。
- 正常 A 狀態為「代理執行中」且有成員時，StartSandbox 在 arm 前直接略過；因此「列表含 A、B」本身不能證明重開了 A。要區分先前監看已失敗、當時狀態不穩、取消交接，還是新的 arm/register/network 錯誤。
- 服務 refreshSandboxes 路徑可在停止中拒絕；啟動註冊／出口核驗也有多種 InvalidOperationException。列出可能分支不能當成已找出這次實際觸發者。

B 計畫：啟動與成功後監看分離；暫時監看失敗退避重試，不能送 Started=false 或 pause；服務以 bootstrap PID＋birth／啟動代次保存成功狀態，拒絕同代次倒退。使用者可見訊息限制 300 字；原始原因及堆疊另以受限長度、去除認證的詳細欄位傳給服務保存，不能先截短到 300 字才期待服務還原全文。診斷加入結構化沙盒啟動錯誤；仍保留服務日誌供重啟後查證。

取消／連點測試須調用實際生產流程、以可注入的 IPC／程序啟動／時鐘邊界替換外部作用，不能只測 queue 假成功。若必須增加測試接點，先維持舊行為，用實際斷言失敗保存紅燈，再改行為；不以缺少型別的編譯失敗充當重現。

## 問題 5：「啟動此實例」啟動什麼

**確認只開啟已設定的遊戲啟動器。** SandboxCompatibility.PreferLauncher 會在辨識 YongOrigin 時選同目錄 Launcher.exe；SandboxInstances.Create 把它映射到專用 Game 副本；bootstrap 完成出口核驗後 Process.Start(permission.LauncherPath)。沒有自動按「開始遊戲」或登入的程式碼。

B 將統一右鍵提示、啟動視窗與成功結果：「已開啟啟動器，請在啟動器按開始遊戲並登入」。不能只改服務文字：Desktop/SandboxBackend.cs 現以 Detail.StartsWith("啟動器已建立") 判斷成功，須同步改用結構化成功狀態，否則新文案會造成 75 秒逾時及錯誤清理。已執行的顯示「已在執行，略過」。不加入自動點擊／登入；直接開本體需另提方案。

## A 時點的 B 段測試計畫（最新結果見 B 驗證文件）

| 問題 | 預定修改檔案／位置 | 要先重現的失敗與後續驗證 |
|---|---|---|
| 1 | Core/SandboxieInstaller、SandboxieRuntime；共用設定路徑／讀取邊界；Desktop/SandboxBackend、SandboxWindow | 缺 ini 不應阻斷官方建立；服務停用不能報 Ready；自訂路徑及 driver H/W 回退；路徑失效／拒絕不可默默寫錯處；初始化後再次解析 |
| 2 | Core/SandboxSettingsTransaction、SandboxieRuntime、SandboxRemoval；設定 helper（方案待確認） | 真實暫存檔 reader 不得阻擋 writer；短暫鎖後恢復；有限重試耗盡；穩定磁碟／resident 核對；正常提交次數、保留其他 section／原有鍵、回復與取消；不以單一 callback 次數當官方 reload 次數 |
| 3 | Desktop/MainWindow.xaml、MainWindow.xaml.cs、MainWindow.Sandbox、Core/SandboxLaunchQueue | --sandbox-ui 覆蓋 Ctrl／Shift、多選刷新、右鍵範圍、單／多／混選、單列編輯移除、顯示順序與已執行略過 |
| 4 | Tools/SandboxBootstrap、Program；Service/SandboxController、Controller；Core/SandboxInstances、NetworkDiagnostics；Desktop/SandboxBackend | A 已成功且監看中，再要求 A/B；監看暫時拒絕／斷線後恢復；成功後 false 被拒且不 pause；錯誤訊息完整傳遞／300 字顯示／診斷；關窗發生於成功前／後；連點只啟動一次；舊 helper 不能污染新代次 |
| 5 | Desktop/SandboxLaunchWindow、SandboxBackend；Service/SandboxController；SANDBOX-GUIDE、操作說明 | 只開 LauncherPath、不自動操作遊戲；成功判斷不依文案；新開／略過顯示不同且語意一致 |

B 完成後跑完整 dotnet test、原生 PacketPathChecks、--sandbox-ui、--sandbox-storage，逐項列數字與證據；再停下等「繼續」，不得自行進 C。

## 請從另一台帶回的資料

1. 錯誤發生後的「設定 → 匯出診斷」JSON，以及大約時間、A/B 當時是否已登入、是否關閉啟動視窗。另附技術資訊中的介面／服務版本。不要提供遊戲密碼、代理密碼或 settings.dpapi。
2. `%PROGRAMDATA%\WindowSocks\service-session.log`，若輪替則一併保存 `.previous`。現有匯出只帶最近 100 條一般訊息，較早的實際原因可能只在日誌中。
3. 該盒內的 `sandbox-bootstrap-error.txt`，保留檔案原時間。本版會覆寫同一檔案，宜在重試前保存。
4. 自動安裝問題另帶 `%LOCALAPPDATA%\WindowSocks\Downloads\Sandboxie-*\setup.log`、Sandboxie 版本、SbieSvc 狀態、通知 SBIE1402 的時間與完整文字，以及有效 IniLocation／登錄自訂 IniPath（若有）。不用搬整份遊戲或所有盒資料。

**錯誤檔位置：** 預設 SeparateUserFolders 下為

```text
%USERPROFILE%\WindowSocksGames\SandboxGames\<32字實例ID>\Box\user\current\AppData\Local\WindowSocks\sandbox-bootstrap-error.txt
```

其中 `<32字實例ID>` 不是短的盒名。自訂使用者目錄／SeparateUserFolders／磁碟設定時也可能在 Box\drive 的映射下。[官方映射](https://github.com/sandboxie-plus/Sandboxie/blob/bde0890108a563f5d72eb8e57085be2b37177218/SandboxiePlus/QSbieAPI/SbieAPI.cpp#L2064)

尚未取得另一台實例 ID 與使用者目錄，不能虛構它的完整路徑。以下只讀命令可由使用者在**另一台** PowerShell 列出實際存在檔案的完整 FullName；不啟動服務、不 reload、不讀帳密內容。本輪未執行此命令：

```powershell
$sandboxData = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'WindowSocksGames\SandboxGames'
Get-ChildItem -LiteralPath $sandboxData -Directory | Where-Object Name -Match '^[0-9a-fA-F]{32}$' | ForEach-Object {
    $boxData = Join-Path $_.FullName 'Box'
    if (Test-Path -LiteralPath $boxData -PathType Container) {
        Get-ChildItem -LiteralPath $boxData -File -Filter 'sandbox-bootstrap-error.txt' -Recurse -Force -ErrorAction Continue
    }
} | Select-Object FullName, LastWriteTime, Length | Format-List
```

若沒有結果，不能推論沒有錯誤：可能是寫檔失敗、沙盒映射不同、檔案已清除或該次未進 catch；請保留服務日誌與診斷。

SBIE1402 若需再次重現，先在可回復 VMware 快照獲准後用 [Microsoft Process Monitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon) 過濾實際 ini 路徑，保存 CreateFile／WriteFile／CloseFile、Result、Desired Access、ShareMode、PID、時間及相關 stack。需保留失敗前後的成功開檔，單獨一筆 SHARING VIOLATION 不能找出鎖持有者。PML 可能含使用者資料，帶回診斷處理，不提交原始檔。

## C 段交付與尚未驗證項目

現有 0.1.34 交付目錄已含上一輪 **TCP 修正測試包**，來源產品提交 `529dfc16`。本次目標升為 0.1.35，不覆蓋 0.1.32／0.1.33／0.1.34；C 獲准後才在全新 0.1.35 目錄建置 Setup／ZIP／SHA256SUMS，並以 verify-release 核對。

本輪不發布、不推送、不 tag、不建草稿。只可提交產品、測試與去識別化文件；本分析使用環境變數與占位符，不含實際代理 IP、使用者帳戶路徑、盒名或帳密。研究原始摘錄、命令輸出與其他私人資料只留在忽略的 artifacts 內。

以下仍為 **NOT RUN**：乾淨機自動安裝→準備→啟動、真實遊戲多開、0.1.33 移除實機、0.1.31 C1／C2／C4／C6。本輪任何實機動作（含可回復 VMware 測試機）須另外取得同意。B 完成後等待「繼續」，不自動進 C。
