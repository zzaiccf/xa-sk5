# 上游版本與修改記錄

日期：2026-10-17；WindowSocks 0.1.0。

## ProxyBridge

- 上游：https://github.com/InterceptSuite/ProxyBridge
- 鎖定提交：`d697164b836f0d3f897128774d3cb72e54770e74`
- 授權：MIT；原始 LICENSE 保留於 `third_party/ProxyBridge/LICENSE`。
- 連結 `Windows/src/pb_util.c`、`pb_socks5.c`，保留所需標頭。其餘來源供對照，未連結上游的 UI、名稱規則、單埠路由與直連回退路徑。
- `pb_util.c`：`send_all` 對零進度／錯誤返回失敗，避免無限迴圈。
- `pb_socks5.c`：兩處已限長至 255 位元組的帳密傳送長度顯式轉成 `int`，消除 x64 編譯警告。
- `pb_socks5.c`：協議寫入改用 send_all；指定帳密時拒絕降級成無帳密模式，避免把未驗證帳密標記為成功；驗證 CONNECT 保留欄位。
- 上游日誌 callback 不啟用；應用程式記錄中文結果，不記錄帳密與完整匯入行。
- `native/WindowSocksNative.cpp` 是新增的 C++ 路由、生命週期與 ABI 層，並非宣稱上游已提供以下功能。

## 新增的路由／服務

1. 不可變規則集合，PID＋FILETIME 身分；一次替換程序規則及啟動保護集合。
2. 完整 IPv4 TCP／UDP 五元組、UDP socket 建立時間、TCP 轉送 alias、每流獨立 SOCKS UDP association。
3. 不支援或缺少設定即封鎖；受管 IPv6 封鎖；受管 DNS 的原目的地記錄後經代理轉到 1.1.1.1:53。
4. 切換／暫停後關閉所有失去有效綁定的流；舊連線不能改為直連。
5. 服務所有者 SID 的啟動保護、已知未綁定實例的明確放行、子程序身分繼承。
6. 後台重辨識、重試同代理、出口重複拒絕、DPAPI、Named Pipe ACL。

尚待驗收的範圍見 VALIDATION.md。新增引擎不代表通過上游的相容性測試。

## WinDivert

版本 v2.2.2；`native/include/windivert.h` 為官方未修改標頭。原授權全文存於 `native/include/WinDivert-LICENSE.txt`。選用 LGPLv3，使用 DLL 動態載入；不把其二進位靜態連結到自有 DLL。本次未散布驅動或 WinDivert DLL。官方來源：https://github.com/basil00/WinDivert/tree/v2.2.2 。
